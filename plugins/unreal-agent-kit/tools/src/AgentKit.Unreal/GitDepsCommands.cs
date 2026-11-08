// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>What the gitdeps commands reach outside the process, so tests can replace it.</summary>
internal sealed class GitDepsServices
{
	/// <summary>Makes the HTTP handler for a download, given the resolved settings (the proxy and its credentials).</summary>
	public Func<GitDepsSettings, HttpMessageHandler> CreateHandler { get; init; } = CreateDefaultHandler;

	public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

	/// <summary>Whether a process named GitDependencies runs: it rewrites .uedependencies and the files it lists.</summary>
	public Func<bool> IsGitDependenciesRunning { get; init; } = GitDependenciesRunning;

	/// <summary>Waits between tries of a failed download.</summary>
	public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

	/// <summary>The longest a read of a pack may wait for data before it counts as stalled.</summary>
	public TimeSpan IdleTimeout { get; init; } = GitDepsSettings.DefaultIdleTimeout;

	/// <summary>The time limit for one try at a pack, in place of GitDependencies' (tests); null for GitDependencies'.</summary>
	public TimeSpan? TransferTimeout { get; init; }

	/// <summary>Deletes a file while .uedependencies is written: the old manifest, and the tmp file after a failure (tests fail it).</summary>
	public Action<string> DeleteFile { get; init; } = File.Delete;

	/// <summary>Called once every file is staged and checked, before the renames (tests).</summary>
	public Action? AfterStaging { get; init; }

	/// <summary>Where -json goes.</summary>
	public TextWriter? Output { get; init; }

	internal static HttpMessageHandler CreateDefaultHandler(GitDepsSettings settings)
	{
		HttpClientHandler Handler = new() { AutomaticDecompression = DecompressionMethods.None };
		if (settings.Proxy is not null)
		{
			// As GitDependencies does (CreateHttpClient, Program.cs:1265-1272): every URL, https included, goes through the
			// proxy, local addresses excepted. Its URI carries no user info, so no error text can show the credentials.
			Handler.Proxy = new WebProxy(settings.Proxy, true, null, settings.ProxyCredentials);
			Handler.UseProxy = true;
		}
		return Handler;
	}

	static bool GitDependenciesRunning()
	{
		Process[] Found = Process.GetProcessesByName("GitDependencies");
		foreach (Process Each in Found)
		{
			Each.Dispose();
		}
		return Found.Length > 0;
	}
}

/// <summary>Shared by <c>uak gitdeps status</c> and <c>uak gitdeps fetch</c>: the engine root, the manifests and the selection.</summary>
public abstract class GitDepsCommandBase : IUakCommand
{
	/// <summary>The most files (or packs) one list prints without -all.</summary>
	private protected const int ListLimit = 50;

	private protected GitDepsCommandBase(GitDepsServices services)
	{
		Services = services;
	}

	private protected GitDepsServices Services { get; }

	/// <inheritdoc/>
	public abstract string Name { get; }

	/// <inheritdoc/>
	public abstract string Summary { get; }

	/// <inheritdoc/>
	public abstract string Usage { get; }

	/// <inheritdoc/>
	public bool RequiresEngine => false;

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
		UnrealServices.GuardAsync(context, () => RunCheckedAsync(context, new UakArguments(arguments, DotSplitRule.Paths), cancellationToken));

	private protected abstract Task<int> RunCheckedAsync(UakContext context, UakArguments arguments, CancellationToken cancellationToken);

	/// <summary>The engine root: -root=, else the engine uak resolved (as <c>uak env</c> shows), with where it came from.</summary>
	private protected static (string Root, string From) ResolveRoot(UakContext context, string? given)
	{
		if (given is not null)
		{
			string Full = EngineLocator.NormalizeEngineRoot(given) ?? Path.GetFullPath(given);
			if (!Directory.Exists(Full))
			{
				throw new UakUsageException($"-root={given}: no such folder.");
			}
			return (Full, "-root=");
		}
		if (context.EngineRoot is null)
		{
			throw new UakSetupException("No engine root: pass -root=<engine root>, or run inside a project uak resolves an engine for (see uak env).");
		}
		string How = context.Provenance.TryGetValue(UakContextResolver.EngineKey, out string? Value) ? Value : "unknown";
		return (context.EngineRoot.FullName, "uak's engine: " + How);
	}

	/// <summary>Reads the manifests under the root and selects the files the patterns name; a setup error when there are no manifests.</summary>
	private protected static (GitDepsManifests Manifests, IReadOnlyList<GitDepsFile> Files) Select(string root, IReadOnlyList<string> patterns)
	{
		if (patterns.Count == 0)
		{
			throw new UakUsageException("give at least one path or glob, from the engine root.");
		}
		List<GitDepsPattern> Parsed = [.. patterns.Select(Pattern => GitDepsPattern.Parse(Pattern, root))];
		GitDepsManifests Manifests = GitDepsManifests.Read(root);
		if (Manifests.ManifestFiles.Count == 0)
		{
			throw new UakSetupException($"{root} has no gitdeps manifests (<folder>/Build/*.gitdeps.xml). An installed engine has none: give a source engine's root with -root=.");
		}
		return (Manifests, GitDepsPattern.Select(Manifests, Parsed, root));
	}

	/// <summary>The states in the order lists print them.</summary>
	private protected static readonly GitDepsState[] StateOrder = [GitDepsState.Present, GitDepsState.Outdated, GitDepsState.Modified, GitDepsState.Missing];

	private protected static string Describe(GitDepsEntry entry) =>
		$"{entry.State.ToString().ToLowerInvariant(),-9} {(entry.Tracked ? "tracked" : "untracked"),-10} {entry.File.Name}";

	/// <summary>A count per state, for summaries.</summary>
	private protected static string Counts(IReadOnlyCollection<GitDepsEntry> entries) =>
		string.Join(", ", StateOrder.Select(State => $"{entries.Count(Entry => Entry.State == State)} {State.ToString().ToLowerInvariant()}"));

	/// <summary>Logs lines, at most <see cref="ListLimit"/> of them unless <paramref name="all"/>, then how many more there are.</summary>
	private protected static void LogList(ILogger log, LogLevel level, IReadOnlyList<string> lines, bool all, bool allLiftsIt = true)
	{
		int Shown = all ? lines.Count : Math.Min(lines.Count, ListLimit);
		for (int Index = 0; Index < Shown; Index++)
		{
			log.Log(level, "{Line}", lines[Index]);
		}
		if (Shown < lines.Count)
		{
			log.Log(level, "  and {More} more{Hint}", lines.Count - Shown, allLiftsIt ? " (-all lists every one)" : "");
		}
	}
}

/// <summary>
/// <c>uak gitdeps status &lt;path-or-glob&gt;...</c>: for each file the engine's gitdeps manifests list that the patterns name,
/// whether it is on disk with the manifest's hash, and whether GitDependencies' working manifest tracks it.
/// </summary>
public sealed class GitDepsStatusCommand : GitDepsCommandBase
{
	/// <summary>Creates the command.</summary>
	public GitDepsStatusCommand() : this(new GitDepsServices())
	{
	}

	internal GitDepsStatusCommand(GitDepsServices services) : base(services)
	{
	}

	/// <inheritdoc/>
	public override string Name => "gitdeps status";

	/// <inheritdoc/>
	public override string Summary => "Show whether files a source engine's gitdeps manifests list are present, outdated, modified or missing.";

	/// <inheritdoc/>
	public override string Usage =>
		"""
		uak gitdeps status <path-or-glob>... [-root=<engine root>] [-all] [-json]
		  <path-or-glob>   A file or folder from the engine root (an absolute path under it works too), or a glob:
		                   * within a folder, ** across folders, ? one character; case-insensitive. Quote a glob
		                   ('Engine/**/IOS/*.png') so Git Bash and PowerShell pass it through unexpanded. Give a
		                   name that starts with a dot as ./.name.
		  -root=<folder>   The engine root (the folder holding Engine/ and Setup.bat). Default: the engine uak
		                   resolves (uak env); the output says which.
		  -all             List every file. By default each state lists at most 50, then a count of the rest.
		  -json            Print one JSON object, with every file, instead of text.
		Each file is present (the manifest's SHA-1), outdated, modified or missing, and tracked or untracked
		(listed in GitDependencies' working manifest, .uedependencies, or not). Outdated: tracked, with the hash
		.uedependencies expected but not the manifest's, because the engine changed since GitDependencies wrote
		it; GitDependencies replaces such a file without asking, and so does uak gitdeps fetch. Modified: tracked
		with a hash other than the one .uedependencies expected, or untracked with a hash other than the
		manifest's; a local change. A tracked file whose write time is the one .uedependencies saved keeps its
		saved hash, as GitDependencies does; every other file is hashed. An unreadable .uedependencies is a
		warning, and every file then counts as untracked.
		It only reads, takes no lock, and is safe while builds run.
		Exit code: 0 reported, 2 usage or setup error (including a pattern that matches no manifest file, or a
		manifest that names a file outside the engine root).
		""";

	private protected override Task<int> RunCheckedAsync(UakContext context, UakArguments arguments, CancellationToken cancellationToken)
	{
		string? GivenRoot = arguments.GetString("root");
		bool Json = arguments.GetFlag("json");
		bool All = arguments.GetFlag("all");
		arguments.ThrowIfUnknown();

		(string Root, string From) = ResolveRoot(context, GivenRoot);
		(GitDepsManifests Manifests, IReadOnlyList<GitDepsFile> Files) = Select(Root, arguments.Positional);
		GitDepsWorking Working = GitDepsWorking.Read(Root);
		if (Working.Error is not null)
		{
			context.Logger.LogWarning("{Path} could not be read ({Error}): every file counts as untracked.", Working.Path, Working.Error);
		}
		List<GitDepsEntry> Entries = [];
		foreach (GitDepsFile File in Files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Entries.Add(GitDepsManifests.Inspect(Root, File, Working));
		}

		if (Json)
		{
			var Report = new
			{
				Root,
				RootFrom = From,
				WorkingManifest = Working.Path,
				WorkingManifestError = Working.Error,
				Files = Entries.Select(Entry => new
				{
					Entry.File.Name,
					Entry.File.Hash,
					State = Entry.State.ToString(),
					Entry.Tracked,
					Entry.File.IsExecutable,
					Size = Manifests.Blobs.TryGetValue(Entry.File.Hash, out GitDepsBlob? Blob) ? Blob.Size : (long?)null,
				}),
			};
			(Services.Output ?? Console.Out).WriteLine(UakJson.Serialize(Report));
			return Task.FromResult(UakExitCodes.Success);
		}

		context.Logger.LogInformation("Engine root: {Root} ({From})", Root, From);
		string NoWorking = File.Exists(Path.Combine(Root, GitDepsWorking.FileName + ".tmp"))
			? "none (no .uedependencies at the root, but .uedependencies.tmp is there: it holds an update to the manifest that was not moved into place, which Setup recovers)"
			: "none (no .uedependencies at the root)";
		context.Logger.LogInformation("Working manifest: {Working}", Working.Path is null ? NoWorking : Working.Error is null ? Working.Path : Working.Path + " (unreadable)");
		foreach (GitDepsState State in StateOrder)
		{
			LogList(context.Logger, LogLevel.Information, [.. Entries.Where(Entry => Entry.State == State).Select(Describe)], All);
		}
		context.Logger.LogInformation("gitdeps status: {Count} file(s): {Counts}; {Untracked} untracked", Entries.Count, Counts(Entries), Entries.Count(Entry => !Entry.Tracked));
		return Task.FromResult(UakExitCodes.Success);
	}
}

/// <summary>
/// <c>uak gitdeps fetch &lt;path-or-glob&gt;...</c>: downloads only the packs that hold the named files' blobs, checks each
/// blob's SHA-1, writes the files GitDependencies would have, and records them in its working manifest as it would.
/// </summary>
public sealed class GitDepsFetchCommand : GitDepsCommandBase
{
	/// <summary>The default for -max-mb=.</summary>
	public const int DefaultMaxMegabytes = 512;

	/// <summary>Creates the command.</summary>
	public GitDepsFetchCommand() : this(new GitDepsServices())
	{
	}

	internal GitDepsFetchCommand(GitDepsServices services) : base(services)
	{
	}

	/// <inheritdoc/>
	public override string Name => "gitdeps fetch";

	/// <inheritdoc/>
	public override string Summary => "Fetch single files a source engine's gitdeps manifests list, without running GitDependencies.";

	/// <inheritdoc/>
	public override string Usage =>
		"""
		uak gitdeps fetch <path-or-glob>... [-root=<engine root>] [-dry-run] [-force] [-max-mb=<n>] [-all]
		                  [-proxy=<url>] [-cache=<folder> | -no-cache]
		  <path-or-glob>   As for uak gitdeps status: files the engine's gitdeps manifests list (each
		                   <folder>/Build/*.gitdeps.xml and each plugin's), from the engine root. Quote a glob
		                   ('Engine/**/IOS/*.png') so Git Bash and PowerShell pass it through unexpanded; give a
		                   name that starts with a dot as ./.name.
		  -root=<folder>   The engine root. Default: the engine uak resolves (uak env); the output says which.
		  -dry-run         Print the files, the packs and their compressed bytes, and write nothing,
		                   .uedependencies included.
		  -force           Also replace a modified file (see uak gitdeps status). Without it, a modified file
		                   refuses the whole fetch before anything is downloaded. Missing and outdated files are
		                   fetched without it.
		  -max-mb=<n>      Refuse a fetch that would download more than n MiB (default 512). It counts each pack's
		                   whole compressed size, an upper bound, as a fetch stops reading a pack after its last
		                   needed blob. A pack read from the cache counts only when the cached copy is unusable and
		                   is downloaded instead, which is checked before that download.
		  -all             List every file and pack. By default each list shows at most 50, then a count.
		  -proxy=<url>     The HTTP proxy, for every pack, https included. Default as GitDependencies: -proxy= in
		                   UE_GITDEPS_ARGS, else HTTP_PROXY, else http_proxy, else .NET's default. Give
		                   credentials in UE_GITDEPS_ARGS (-proxy-user= and -proxy-password=) or in HTTP_PROXY,
		                   never on uak's command line, which uak runs list shows. The output shows the proxy's
		                   scheme, host and port, never its credentials; an error names only where the proxy
		                   came from, never its value.
		  -cache=<folder>  Read packs from this GitDependencies download cache when they are in it. Default as
		                   GitDependencies: -cache= in UE_GITDEPS_ARGS, UE_GITDEPS, then .git/ue-gitdeps above the
		                   root (an empty -cache= in UE_GITDEPS_ARGS skips UE_GITDEPS, as GitDependencies does).
		                   Read only: uak never writes to the cache. -no-cache downloads every pack.
		Only the packs that hold the files' blobs are read, and each only up to its last needed blob; a file of
		zero bytes needs no pack. A failed read of a pack (an HTTP error other than 4xx, or a 408 or 429; a
		broken connection; no data for 100 s; or a transfer longer than GitDependencies' time limit for it) is
		tried again after 1 s, then after 2 s, or after the server's Retry-After (at most 30 s) when it is
		longer: three tries in all. A failure writing a file is not retried. Each file is written to a temporary
		file beside it and its SHA-1 checked; only once every file is checked are they renamed into place.
		A failure before the renames writes nothing. If a rename fails, the files already renamed stay (each
		verified), the output names them, the other temporary files are deleted, and the exit code is 1. Once
		the renames start they finish; a cancel then takes effect after the files are listed and recorded. A
		file already present with the manifest's hash is skipped. On Mac and Linux, a file the manifest marks
		executable gets its x bits; a failure there is reported as such, and the exit code is 1.
		.uedependencies: when it exists, each fetched file is recorded in it as GitDependencies records what it
		downloads (Hash and ExpectedHash the manifest's, Timestamp the file's write time), and so is each
		selected file already present with the manifest's hash but untracked, as GitDependencies records such a
		file; every other entry stays as it was. GitDependencies then treats the files as its own and updates
		them with the engine. For a folder its exclusions cover, it drops the entry on its next run and leaves
		the file, which an engine update can then leave out of date (shown as modified; fetch it again with
		-force). With no .uedependencies (or only the legacy .ue4dependencies), the files stay untracked, and
		the output says so; if it can't be read or written, the files stay, untracked, with a warning (which
		names any .uedependencies.tmp left behind, to delete before running Setup). A fetch refuses to start
		while a process named GitDependencies runs, as it rewrites .uedependencies.
		One fetch writes to an engine at a time: a fetch (not -dry-run) holds a system-wide lock named after
		the engine root folder (whatever path reaches it) for its whole run, and another fetch on the same
		engine is refused at once, as is one whose lock another user's or an elevated fetch holds. Holding
		it, a fetch first deletes the temporary files (<file>.uak-<id>.tmp) a killed fetch left beside the
		selected files, and names them. It takes no editor lock and is safe while builds run; it writes only
		files that are missing, outdated or (with -force) modified, so don't fetch a file a running build or
		editor uses.
		Exit code: 0 fetched (or nothing to do, or a dry run), 1 a download, hash check, write, rename or
		execute-bit change failed (nothing written, except the files renamed before a failed rename, which the
		output names), 2 usage or setup error, or a refusal (a pattern that matches nothing, a modified file
		without -force, more than -max-mb, a running GitDependencies, another fetch on the same engine).
		""";

	private protected override async Task<int> RunCheckedAsync(UakContext context, UakArguments arguments, CancellationToken cancellationToken)
	{
		string? GivenRoot = arguments.GetString("root");
		bool DryRun = arguments.GetFlag("dry-run");
		bool Force = arguments.GetFlag("force");
		bool All = arguments.GetFlag("all");
		int MaxMegabytes = arguments.GetInt("max-mb", 1) ?? DefaultMaxMegabytes;
		string? Proxy = arguments.GetString("proxy");
		string? Cache = arguments.GetString("cache");
		bool NoCache = arguments.GetFlag("no-cache");
		arguments.ThrowIfUnknown();
		if (Cache is not null && NoCache)
		{
			throw new UakUsageException("-cache= and -no-cache don't go together.");
		}

		(string Root, string From) = ResolveRoot(context, GivenRoot);
		ILogger Log = context.Logger;
		Log.LogInformation("Engine root: {Root} ({From})", Root, From);

		// A fetch that writes holds the engine's fetch lock for its whole run, so two fetches never stage the same files or
		// rewrite .uedependencies over each other.
		using GitDepsFetchLock? Lock = DryRun ? null : TakeLock(Root, Log);
		(GitDepsManifests Manifests, IReadOnlyList<GitDepsFile> Files) = Select(Root, arguments.Positional);
		if (Lock is not null)
		{
			DeleteLeftovers(Log, Root, Files);
		}
		GitDepsWorking Working = GitDepsWorking.Read(Root);
		if (Working.Error is not null)
		{
			Log.LogWarning("{Path} could not be read ({Error}): every file counts as untracked, and the fetched files will stay untracked.", Working.Path, Working.Error);
		}
		List<GitDepsEntry> Entries = [.. Files.Select(File => GitDepsManifests.Inspect(Root, File, Working))];

		// Refusals first, before any file is listed.
		List<GitDepsEntry> Modified = [.. Entries.Where(Entry => Entry.State == GitDepsState.Modified)];
		if (Modified.Count > 0 && !Force)
		{
			LogList(Log, LogLevel.Error, [.. Modified.Select(Entry => $"modified: {Entry.File.Name} (its hash is not the manifest's {Entry.File.Hash}{(Entry.Tracked ? ", nor the one .uedependencies expected" : "")})")], false, allLiftsIt: false);
			throw new UakRefusalException($"{Modified.Count} file(s) on disk differ from the manifest, and uak keeps local changes; nothing was downloaded. Pass -force to replace them.");
		}
		List<GitDepsEntry> Present = [.. Entries.Where(Entry => Entry.State == GitDepsState.Present)];
		List<GitDepsEntry> ToFetch = [.. Entries.Where(Entry => Entry.State != GitDepsState.Present)];
		// Present but untracked: GitDependencies would record them (Program.cs:479-500, 516-525), so a fetch does too.
		List<GitDepsEntry> Untracked = [.. Present.Where(Entry => !Entry.Tracked)];
		if (ToFetch.Count == 0)
		{
			LogList(Log, LogLevel.Information, [.. Present.Select(Entry => "already present: " + Entry.File.Name)], All);
			if (DryRun)
			{
				LogWouldRecord(Log, Root, Working, Untracked);
				Log.LogInformation("gitdeps fetch: nothing to fetch; {Count} file(s) already present; dry run, nothing written (.uedependencies included)", Present.Count);
				return UakExitCodes.Success;
			}
			GitDepsRecorder.Record(Log, Services, Root, Working, [], Untracked);
			Log.LogInformation("gitdeps fetch: nothing to fetch; {Count} file(s) already present", Present.Count);
			return UakExitCodes.Success;
		}

		// The blobs and packs to read. Files with the same content share a blob; a zero-size blob needs no pack.
		Dictionary<string, GitDepsBlob> Blobs = new(StringComparer.OrdinalIgnoreCase);
		foreach (GitDepsEntry Entry in ToFetch)
		{
			if (!Manifests.Blobs.TryGetValue(Entry.File.Hash, out GitDepsBlob? Blob))
			{
				throw new UakSetupException($"{Entry.File.Manifest} lists {Entry.File.Name} with hash {Entry.File.Hash}, but no manifest has that blob.");
			}
			if (Blob.Size == 0)
			{
				if (!Blob.Hash.Equals(GitDepsBlob.EmptyHash, StringComparison.OrdinalIgnoreCase))
				{
					throw new UakSetupException($"{Entry.File.Manifest} is not a valid gitdeps manifest: blob {Blob.Hash} of {Entry.File.Name} has size 0, and only {GitDepsBlob.EmptyHash}, the SHA-1 of no bytes, can.");
				}
			}
			else if (!Manifests.Packs.ContainsKey(Blob.PackHash))
			{
				throw new UakSetupException($"blob {Blob.Hash} of {Entry.File.Name} is in pack {Blob.PackHash}, which no manifest lists.");
			}
			Blobs[Blob.Hash] = Blob;
		}
		GitDepsSettings Settings = GitDepsSettings.Resolve(Root, Proxy, Cache, NoCache, Services.GetEnvironmentVariable);
		List<GitDepsPackPlan> Packs = [.. Blobs.Values
			.Where(Blob => Blob.Size > 0)
			.GroupBy(Blob => Blob.PackHash, StringComparer.OrdinalIgnoreCase)
			.Select(Group => new GitDepsPackPlan(Manifests.Packs[Group.Key], [.. Group.OrderBy(Blob => Blob.PackOffset).ThenBy(Blob => Blob.Size)], Settings.CachedPack(Manifests.Packs[Group.Key])))
			.OrderBy(Plan => Plan.Pack.Hash, StringComparer.OrdinalIgnoreCase)];
		long DownloadBytes = Packs.Where(Plan => Plan.Cached is null).Sum(Plan => Plan.Pack.CompressedSize);
		long MaxBytes = MaxMegabytes * 1024L * 1024L;
		if (DownloadBytes > MaxBytes)
		{
			string Message = $"the fetch of {ToFetch.Count} file(s) from {Packs.Count} pack(s) would download {DownloadBytes} bytes (each pack's whole compressed size, an upper bound), more than -max-mb={MaxMegabytes} ({MaxBytes} bytes). Narrow the patterns, or raise -max-mb=.";
			if (!DryRun)
			{
				throw new UakRefusalException(Message);
			}
			Log.LogWarning("{Message}", Message);
		}

		TimeSpan TransferTimeout = Services.TransferTimeout ?? Settings.TransferTimeout(Packs.Count == 0 ? 0 : Packs.Max(Plan => Plan.Pack.CompressedSize));
		Log.LogInformation("Cache: {Cache} ({From}){Missing}; proxy: {Proxy}; time limits: {Idle} s without data, {Transfer} s per pack",
			Settings.CacheDirectory ?? "none", Settings.CacheFrom, Settings.CacheDirectory is not null && !Directory.Exists(Settings.CacheDirectory) ? ", which does not exist" : "",
			Settings.ProxyDescription, Services.IdleTimeout.TotalSeconds, Math.Round(TransferTimeout.TotalSeconds));
		LogList(Log, LogLevel.Information, [.. Packs.Select(Plan => $"pack {Plan.Pack.Hash}: {Plan.Pack.CompressedSize} bytes compressed, {Plan.Blobs.Count} blob(s) needed, from {(Plan.Cached is null ? GitDepsRedact.Url(Plan.Pack.Url) : "the cache, " + Plan.Cached)}")], All);
		LogList(Log, LogLevel.Information, [.. Present.Select(Entry => "already present: " + Entry.File.Name)], All);
		foreach (GitDepsState State in StateOrder.Where(State => State != GitDepsState.Present))
		{
			LogList(Log, LogLevel.Information, [.. ToFetch.Where(Entry => Entry.State == State).Select(Entry => $"{(DryRun ? "would fetch" : "to fetch")} {Entry.File.Name} ({Blobs[Entry.File.Hash].Size} bytes, SHA-1 {Entry.File.Hash}){Replacing(Entry.State)}")], All);
		}
		Log.LogInformation("{Count} file(s) to fetch ({Missing} missing, {Outdated} outdated, {Modified} modified) from {Packs} pack(s): at most {Bytes} bytes to download; {Present} already present",
			ToFetch.Count, ToFetch.Count(Entry => Entry.State == GitDepsState.Missing), ToFetch.Count(Entry => Entry.State == GitDepsState.Outdated), Modified.Count, Packs.Count, DownloadBytes, Present.Count);
		if (DryRun)
		{
			LogWouldRecord(Log, Root, Working, Untracked);
			Log.LogInformation("gitdeps fetch: dry run, nothing written (.uedependencies included)");
			return UakExitCodes.Success;
		}
		if (Services.IsGitDependenciesRunning())
		{
			throw new UakRefusalException("a process named GitDependencies is running (Setup, or another engine's); it rewrites .uedependencies and the files it lists, so nothing was downloaded. Fetch once it has ended.");
		}

		GitDepsFetchRun Run = new(Log, Services, Root, Working, Settings, ToFetch, Untracked, Blobs, Packs, All, MaxMegabytes, MaxBytes, DownloadBytes, TransferTimeout);
		return await Run.RunAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Takes the engine's fetch lock, or refuses: another fetch on this engine is running.</summary>
	static GitDepsFetchLock TakeLock(string root, ILogger log)
	{
		GitDepsFetchLock Lock = GitDepsFetchLock.TryTake(root)
			?? throw new UakRefusalException($"another uak gitdeps fetch is running on this engine ({root}), or its lock can't be opened, as when another user's or an elevated fetch holds it; nothing was downloaded. Fetch once it has ended (a -dry-run needs no lock).");
		if (Lock.WasAbandoned)
		{
			log.LogInformation("An earlier uak gitdeps fetch on this engine ended without releasing its lock (it was killed); this fetch holds it now.");
		}
		return Lock;
	}

	/// <summary>What a staged file's name is: the target's name, ".uak-", 32 lower-case hex digits (a Guid's "N" form), ".tmp".</summary>
	static readonly Regex LeftoverName = new(@"^(?<Name>.+)\.uak-[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant);

	/// <summary>
	/// Deletes the staged files a killed fetch left (a finally can't run then): in the selected files' folders only, with no
	/// walk of the tree, and only names a fetch makes for a selected file (&lt;file&gt;.uak-&lt;32 hex digits&gt;.tmp). Run
	/// with the fetch lock held, so no other fetch is staging there. Each one deleted is named.
	/// </summary>
	static void DeleteLeftovers(ILogger log, string root, IReadOnlyList<GitDepsFile> files)
	{
		StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		foreach (IGrouping<string, string> Folder in files
			.Select(File => Path.Combine(root, File.Name.Replace('/', Path.DirectorySeparatorChar)))
			.GroupBy(Target => Path.GetDirectoryName(Target)!, Target => Path.GetFileName(Target), Comparer))
		{
			HashSet<string> Names = new(Folder, Comparer);
			List<string> Found;
			try
			{
				if (!Directory.Exists(Folder.Key))
				{
					continue;
				}
				Found = [.. Directory.EnumerateFiles(Folder.Key, "*.uak-*.tmp", SearchOption.TopDirectoryOnly)];
			}
			catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
			{
				log.LogWarning("could not look for temporary files an earlier fetch left in {Folder}: {Message}", Folder.Key, Error.Message);
				continue;
			}
			foreach (string Leftover in Found)
			{
				Match Name = LeftoverName.Match(Path.GetFileName(Leftover));
				if (!Name.Success || !Names.Contains(Name.Groups["Name"].Value))
				{
					continue;
				}
				try
				{
					File.Delete(Leftover);
					log.LogInformation("deleted {Path}, a temporary file an earlier fetch left (it was killed)", Leftover);
				}
				catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
				{
					log.LogWarning("could not delete {Path}, a temporary file an earlier fetch left: {Message}; delete it by hand", Leftover, Error.Message);
				}
			}
		}
	}

	/// <summary>In a dry run, says which files already present a fetch would record.</summary>
	static void LogWouldRecord(ILogger log, string root, GitDepsWorking working, List<GitDepsEntry> untracked)
	{
		if (untracked.Count > 0 && working.Error is null && File.Exists(Path.Combine(root, GitDepsWorking.FileName)))
		{
			log.LogInformation("would record {Count} file(s) already present (untracked, with the manifest's hash) in .uedependencies", untracked.Count);
		}
	}

	static string Replacing(GitDepsState state) => state switch
	{
		GitDepsState.Outdated => ", replacing an outdated file",
		GitDepsState.Modified => ", replacing a modified file",
		_ => "",
	};
}

/// <summary>A pack to read: the blobs needed from it, sorted by offset, and its cached copy, if any.</summary>
internal sealed record GitDepsPackPlan(GitDepsPack Pack, List<GitDepsBlob> Blobs, string? Cached);

/// <summary>A download that failed for good: out of tries, or not one another try would fix (a 4xx, a bad gzip stream).</summary>
internal sealed class GitDepsFetchException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>One fetch, once every refusal has passed: stages each file, renames them into place, records them, with the files already present but untracked.</summary>
internal sealed class GitDepsFetchRun(ILogger log, GitDepsServices services, string root, GitDepsWorking working, GitDepsSettings settings, List<GitDepsEntry> toFetch,
	List<GitDepsEntry> untracked, Dictionary<string, GitDepsBlob> blobs, List<GitDepsPackPlan> packs, bool all, int maxMegabytes, long maxBytes, long plannedBytes, TimeSpan transferTimeout)
{
	/// <summary>How many times a pack is read before its failure stands: the first try and two more, after 1 s and then 2 s.</summary>
	const int MaxAttempts = 3;

	/// <summary>The longest wait a server's Retry-After gets.</summary>
	static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

	const int ListLimit = 50;

	long PlannedBytes = plannedBytes;

	/// <summary>The Retry-After (in seconds) of the last try's 408 or 429, if it sent one.</summary>
	TimeSpan? RetryAfter;

	// Each blob is written to the temporary file of the first file that needs it.
	readonly Dictionary<string, GitDepsEntry> FirstForBlob = toFetch.GroupBy(Entry => Entry.File.Hash, StringComparer.OrdinalIgnoreCase).ToDictionary(Group => Group.Key, Group => Group.First(), StringComparer.OrdinalIgnoreCase);

	/// <summary>The current temporary file of each file staged so far, by name.</summary>
	readonly Dictionary<string, string> Staged = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Every temporary file made and not yet renamed or deleted.</summary>
	readonly HashSet<string> Temporaries = new(StringComparer.OrdinalIgnoreCase);

	readonly List<string> CreatedDirectories = [];
	readonly List<GitDepsEntry> Committed = [];

	public async Task<int> RunAsync(CancellationToken cancellationToken)
	{
		(GitDepsEntry Entry, Exception Error)? RenameFailure = null;
		List<(GitDepsEntry Entry, Exception Error)> ExecutableFailures = [];
		try
		{
			try
			{
				await StageAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception Error) when (Error is GitDepsFetchException or GitDepsCorruptException or GitDepsWriteException or IOException or UnauthorizedAccessException)
			{
				log.LogError("{Message}", Error.Message);
				log.LogError("gitdeps fetch: FAILED; nothing written");
				return UakExitCodes.Failure;
			}
			services.AfterStaging?.Invoke();

			// Every blob is checked: rename into place. Once started, the renames finish (they are quick), so a cancel never
			// leaves renamed files unlisted and unrecorded; it takes effect below.
			foreach (GitDepsEntry Entry in toFetch)
			{
				try
				{
					Rename(Entry);
				}
				catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
				{
					RenameFailure = (Entry, Error);
					break;
				}
				try
				{
					SetExecutable(TargetOf(Entry), Entry.File);
				}
				catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
				{
					ExecutableFailures.Add((Entry, Error));
				}
			}
		}
		finally
		{
			DeleteTemporaries();
			RemoveCreatedDirectories();
		}

		LogList([.. Committed.Select(Entry => $"fetched {Entry.File.Name} ({blobs[Entry.File.Hash].Size} bytes, SHA-1 {Entry.File.Hash} verified){Replaced(Entry.State)}")]);
		GitDepsRecorder.Record(log, services, root, working, Committed, untracked);
		foreach ((GitDepsEntry Entry, Exception Error) in ExecutableFailures)
		{
			log.LogError("setting the execute bits of {Name} failed: {Message}; the file is in place, but not executable", Entry.File.Name, Error.Message);
		}
		// A rename failure's reason is shown before a cancel that came with it ends the fetch.
		if (RenameFailure is { } Failed)
		{
			log.LogError("renaming {Name} into place failed: {Message}", Failed.Entry.File.Name, Failed.Error.Message);
		}
		if (cancellationToken.IsCancellationRequested)
		{
			log.LogWarning("gitdeps fetch: cancelled once the renames had started; the {Count} file(s) renamed into place stay, listed above", Committed.Count);
			cancellationToken.ThrowIfCancellationRequested();
		}
		if (RenameFailure is not null)
		{
			log.LogError("gitdeps fetch: FAILED; {Count} of {Total} file(s) were renamed into place before it failed, each verified, and stay: {Names}; the other temporary files were deleted",
				Committed.Count, toFetch.Count, Committed.Count == 0 ? "none" : string.Join(", ", Committed.Take(ListLimit).Select(Entry => Entry.File.Name)) + (Committed.Count > ListLimit ? $" and {Committed.Count - ListLimit} more" : ""));
			return UakExitCodes.Failure;
		}
		if (ExecutableFailures.Count > 0)
		{
			log.LogError("gitdeps fetch: FAILED; every file was renamed into place, each SHA-1 verified, but {Count} could not be made executable", ExecutableFailures.Count);
			return UakExitCodes.Failure;
		}
		log.LogInformation("gitdeps fetch: {Count} file(s) fetched from {Packs} pack(s), each SHA-1 verified", toFetch.Count, packs.Count);
		return UakExitCodes.Success;
	}

	static string Replaced(GitDepsState state) => state switch
	{
		GitDepsState.Outdated => ", replaced the outdated file",
		GitDepsState.Modified => ", replaced the modified file",
		_ => "",
	};

	void LogList(IReadOnlyList<string> lines)
	{
		int Shown = all ? lines.Count : Math.Min(lines.Count, ListLimit);
		for (int Index = 0; Index < Shown; Index++)
		{
			log.LogInformation("{Line}", lines[Index]);
		}
		if (Shown < lines.Count)
		{
			log.LogInformation("  and {More} more (-all lists every one)", lines.Count - Shown);
		}
	}

	string TargetOf(GitDepsEntry entry) => Path.Combine(root, entry.File.Name.Replace('/', Path.DirectorySeparatorChar));

	/// <summary>Writes every file to a temporary file beside it: zero-size ones directly, the rest from their packs, and copies for files that share a blob.</summary>
	async Task StageAsync(CancellationToken cancellationToken)
	{
		foreach (GitDepsBlob Blob in blobs.Values.Where(Blob => Blob.Size == 0))
		{
			string Output = BlobOutput(Blob);
			GitDepsPackReader.Writing(Output, () => new FileStream(Output, FileMode.CreateNew, FileAccess.Write, FileShare.None)).Dispose();
		}
		foreach (GitDepsPackPlan Plan in packs)
		{
			await ReadPackAsync(Plan, cancellationToken).ConfigureAwait(false);
		}
		foreach (GitDepsEntry Entry in toFetch)
		{
			if (!Staged.ContainsKey(Entry.File.Name))
			{
				string Source = Staged[FirstForBlob[Entry.File.Hash].File.Name];
				string Temporary = NewTemporary(Entry);
				GitDepsPackReader.Writing(Temporary, () =>
				{
					File.Copy(Source, Temporary);
					return true;
				});
			}
		}
	}

	string BlobOutput(GitDepsBlob blob) => NewTemporary(FirstForBlob[blob.Hash]);

	/// <summary>A new temporary file name beside the file, its folder created; a new one on each try, so a file a failed try couldn't delete is never reused.</summary>
	string NewTemporary(GitDepsEntry entry)
	{
		string Target = TargetOf(entry);
		string Folder = Path.GetDirectoryName(Target)!;
		GitDepsPackReader.Writing(Folder, () =>
		{
			CreateDirectory(Folder);
			return true;
		});
		string Temporary = $"{Target}.uak-{Guid.NewGuid():N}.tmp";
		Temporaries.Add(Temporary);
		Staged[entry.File.Name] = Temporary;
		return Temporary;
	}

	/// <summary>Reads a pack's blobs from the cache, else downloads it, trying a failed read of the source again.</summary>
	async Task ReadPackAsync(GitDepsPackPlan plan, CancellationToken cancellationToken)
	{
		GitDepsPack Pack = plan.Pack;
		string Url = GitDepsRedact.Url(Pack.Url);
		if (plan.Cached is not null)
		{
			string? Problem = await TryReadCachedAsync(plan, cancellationToken).ConfigureAwait(false);
			if (Problem is null)
			{
				log.LogInformation("pack {Hash}: read from the cache", Pack.Hash);
				return;
			}
			Discard(plan.Blobs);
			if (PlannedBytes + Pack.CompressedSize > maxBytes)
			{
				throw new UakRefusalException($"the cached pack {plan.Cached} is unusable ({Problem}), and downloading it ({Pack.CompressedSize} bytes) would bring the download to {PlannedBytes + Pack.CompressedSize} bytes, more than -max-mb={maxMegabytes} ({maxBytes} bytes); nothing written. Raise -max-mb=, or fix the cache.");
			}
			PlannedBytes += Pack.CompressedSize;
			log.LogWarning("the cached pack {Path} is unusable ({Problem}); downloading it from {Url} ({Bytes} bytes; {Planned} bytes to download in all)", plan.Cached, Problem, Url, Pack.CompressedSize, PlannedBytes);
		}

		for (int Attempt = 1; ; Attempt++)
		{
			try
			{
				RetryAfter = null;
				await DownloadAsync(plan, Url, cancellationToken).ConfigureAwait(false);
				log.LogInformation("pack {Hash}: downloaded from {Url}", Pack.Hash, Url);
				return;
			}
			catch (Exception Error) when (Attempt < MaxAttempts && IsTransient(Error, cancellationToken))
			{
				Discard(plan.Blobs);
				TimeSpan Wait = TimeSpan.FromSeconds(Attempt);
				if (RetryAfter is TimeSpan Asked && Asked > Wait)
				{
					Wait = Asked < MaxRetryAfter ? Asked : MaxRetryAfter;
				}
				log.LogWarning("download of {Url} failed ({Message}); trying again in {Seconds} s (try {Next} of {Max})", Url, Error.Message, Wait.TotalSeconds, Attempt + 1, MaxAttempts);
				await services.Delay(Wait, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception Error) when (IsTransient(Error, cancellationToken) || Error is HttpRequestException or InvalidDataException)
			{
				string Tries = IsTransient(Error, cancellationToken) ? $" {MaxAttempts} times" : "";
				throw new GitDepsFetchException($"download of {Url} failed{Tries}: {Error.Message}", Error);
			}
		}
	}

	/// <summary>Reads a pack's blobs from its cached copy; null when it worked, else why not (the caller then downloads it). A write failure is thrown.</summary>
	async Task<string?> TryReadCachedAsync(GitDepsPackPlan plan, CancellationToken cancellationToken)
	{
		FileStream Stream;
		try
		{
			Stream = new FileStream(plan.Cached!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			return Error.Message;
		}
		await using (Stream.ConfigureAwait(false))
		{
			try
			{
				GitDepsSourceStream Source = new(Stream, plan.Cached!, services.IdleTimeout, Timeout.InfiniteTimeSpan, CancellationToken.None);
				await GitDepsPackReader.ExtractAsync(Source, plan.Blobs, BlobOutput, cancellationToken).ConfigureAwait(false);
				return null;
			}
			catch (Exception Error) when (Error is GitDepsCorruptException or GitDepsSourceException or InvalidDataException)
			{
				return Error.Message;
			}
		}
	}

	/// <summary>One try at downloading a pack: the headers within the idle time, then every read within it, all before GitDependencies' time limit.</summary>
	async Task DownloadAsync(GitDepsPackPlan plan, string url, CancellationToken cancellationToken)
	{
		using CancellationTokenSource Deadline = new(transferTimeout);
		using CancellationTokenSource Linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Deadline.Token);
		using HttpClient Client = new(services.CreateHandler(settings), disposeHandler: true) { Timeout = services.IdleTimeout };
		using HttpResponseMessage Response = await Client.GetAsync(plan.Pack.Url, HttpCompletionOption.ResponseHeadersRead, Linked.Token).ConfigureAwait(false);
		if (Response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests && Response.Headers.RetryAfter?.Delta is TimeSpan Asked)
		{
			RetryAfter = Asked;
		}
		Response.EnsureSuccessStatusCode();
		Stream Raw;
		try
		{
			Raw = await Response.Content.ReadAsStreamAsync(Linked.Token).ConfigureAwait(false);
		}
		catch (IOException Error)
		{
			throw new GitDepsSourceException($"reading {url} failed: {Error.Message}", Error);
		}
		await using (Raw.ConfigureAwait(false))
		{
			GitDepsSourceStream Source = new(Raw, url, services.IdleTimeout, transferTimeout, Deadline.Token);
			await GitDepsPackReader.ExtractAsync(Source, plan.Blobs, BlobOutput, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// A failure reading the source, which another try may not repeat: a connection or read error, a stall, a time limit, a
	/// server error, a 408 (Request Timeout) or a 429 (Too Many Requests). Not another 4xx (the pack isn't there), a wrong
	/// hash, a bad gzip stream, or a failure writing a file.
	/// </summary>
	static bool IsTransient(Exception error, CancellationToken cancellationToken) => error switch
	{
		GitDepsSourceException => true,
		HttpRequestException Http => Http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
			|| Http.StatusCode is not (>= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError),
		OperationCanceledException => !cancellationToken.IsCancellationRequested,
		_ => false,
	};

	/// <summary>Deletes the temporary files a failed read of a pack wrote, so a retry starts clean. One that can't be deleted stays listed, for the last cleanup.</summary>
	void Discard(List<GitDepsBlob> packBlobs)
	{
		foreach (GitDepsBlob Blob in packBlobs)
		{
			if (Staged.Remove(FirstForBlob[Blob.Hash].File.Name, out string? Temporary) && DeleteQuietly(Temporary))
			{
				Temporaries.Remove(Temporary);
			}
		}
	}

	/// <summary>Renames a staged file into place, replacing an outdated or modified file.</summary>
	void Rename(GitDepsEntry entry)
	{
		string Target = TargetOf(entry);
		string Temporary = Staged[entry.File.Name];
		if (entry.State == GitDepsState.Missing)
		{
			// Throws if a file appeared since it was found missing: it is kept.
			File.Move(Temporary, Target, overwrite: false);
		}
		else
		{
			if (File.Exists(Target))
			{
				File.SetAttributes(Target, File.GetAttributes(Target) & ~FileAttributes.ReadOnly);
			}
			File.Move(Temporary, Target, overwrite: true);
		}
		Temporaries.Remove(Temporary);
		Committed.Add(entry);
	}

	/// <summary>Deletes every temporary file not renamed into place, on every way out; one that can't be deleted is named.</summary>
	void DeleteTemporaries()
	{
		foreach (string Temporary in Temporaries.ToList())
		{
			if (DeleteQuietly(Temporary))
			{
				Temporaries.Remove(Temporary);
			}
			else
			{
				log.LogWarning("could not delete the temporary file {Path}; delete it by hand", Temporary);
			}
		}
	}

	/// <summary>Creates a folder and the missing ones above it, recording each it created, outermost first.</summary>
	void CreateDirectory(string directory)
	{
		List<string> Missing = [];
		for (string? Current = directory; Current is not null && !Directory.Exists(Current); Current = Path.GetDirectoryName(Current))
		{
			Missing.Add(Current);
		}
		Missing.Reverse();
		foreach (string Folder in Missing)
		{
			Directory.CreateDirectory(Folder);
			CreatedDirectories.Add(Folder);
		}
	}

	/// <summary>Removes the folders this fetch created that are still empty, innermost first.</summary>
	void RemoveCreatedDirectories()
	{
		for (int Index = CreatedDirectories.Count - 1; Index >= 0; Index--)
		{
			try
			{
				if (!Directory.EnumerateFileSystemEntries(CreatedDirectories[Index]).Any())
				{
					Directory.Delete(CreatedDirectories[Index]);
				}
			}
			catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	/// <summary>Deletes a file; whether it is gone.</summary>
	static bool DeleteQuietly(string path)
	{
		try
		{
			File.Delete(path);
			return true;
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>On Mac and Linux, gives an executable file x wherever it has r, as GitDependencies does (SetExecutablePermissions). Windows has no such bit.</summary>
	internal static void SetExecutable(string path, GitDepsFile file)
	{
		if (!file.IsExecutable)
		{
			return;
		}
		if (OperatingSystem.IsWindows())
		{
			return;
		}
		UnixFileMode Mode = File.GetUnixFileMode(path);
		UnixFileMode Executable = (UnixFileMode)(((int)Mode >> 2) & (int)(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
		if ((Mode | Executable) != Mode)
		{
			File.SetUnixFileMode(path, Mode | Executable);
		}
	}
}

/// <summary>
/// Records files in .uedependencies as GitDependencies does: each fetched file as it records a download (Program.cs:662-670),
/// and each selected file already present with the manifest's hash but untracked as it adds such a file (Program.cs:479-500,
/// 516-525). Hash and ExpectedHash are the manifest's, Timestamp the file's write time; each replaces an entry of the same
/// name or is added, and every other entry stays as it was. Only when .uedependencies exists; a failure leaves the files
/// untracked, with one warning.
/// </summary>
internal static class GitDepsRecorder
{
	public static void Record(ILogger log, GitDepsServices services, string root, GitDepsWorking working, IReadOnlyList<GitDepsEntry> fetched, IReadOnlyList<GitDepsEntry> present)
	{
		if (fetched.Count == 0 && present.Count == 0)
		{
			return;
		}
		string What = fetched.Count == 0 ? $"the {present.Count} file(s) already present"
			: present.Count == 0 ? $"the {fetched.Count} fetched file(s)"
			: $"the {fetched.Count} fetched file(s) and the {present.Count} file(s) already present";
		string Main = Path.Combine(root, GitDepsWorking.FileName);
		if (working.Error is not null)
		{
			log.LogInformation("{Path} was unreadable, so it was not written: {What} stay untracked", working.Path, What);
			return;
		}
		if (!File.Exists(Main))
		{
			if (File.Exists(Main + ".tmp"))
			{
				log.LogInformation("No .uedependencies at the root, but .uedependencies.tmp is there: it holds an update to the manifest that was not moved into place, which Setup recovers; {What} stay untracked", What);
			}
			else if (working.IsLegacy)
			{
				log.LogInformation("Only the legacy .ue4dependencies is at the root, and uak writes only .uedependencies: {What} stay untracked", What);
			}
			else
			{
				log.LogInformation("No .uedependencies at the root (GitDependencies has not run here): {What} stay untracked", What);
			}
			return;
		}
		if (services.IsGitDependenciesRunning())
		{
			log.LogWarning("A process named GitDependencies is running, so .uedependencies was not written: {What} stay, untracked", What);
			return;
		}
		try
		{
			GitDepsWorkingManifest Manifest = GitDepsWorking.ReadManifest(Main);
			Dictionary<string, int> Index = new(StringComparer.Ordinal);
			for (int Position = 0; Position < Manifest.Files.Count; Position++)
			{
				if (Manifest.Files[Position].Name is string Name)
				{
					Index.TryAdd(Name, Position);
				}
			}
			void Track(GitDepsEntry entry, long timestamp)
			{
				GitDepsWorkingFile Tracked = new()
				{
					Name = entry.File.Name,
					Hash = entry.File.Hash,
					ExpectedHash = entry.File.Hash,
					Timestamp = timestamp,
				};
				if (Index.TryGetValue(entry.File.Name, out int Position))
				{
					Manifest.Files[Position] = Tracked;
				}
				else
				{
					Index[entry.File.Name] = Manifest.Files.Count;
					Manifest.Files.Add(Tracked);
				}
			}

			foreach (GitDepsEntry Entry in fetched)
			{
				Track(Entry, File.GetLastWriteTimeUtc(PathOf(root, Entry)).Ticks);
			}
			int PresentCount = 0;
			foreach (GitDepsEntry Entry in present)
			{
				// Its hash was taken at this write time; a file written since may not hold it any more.
				if (File.GetLastWriteTimeUtc(PathOf(root, Entry)).Ticks != Entry.Timestamp)
				{
					log.LogInformation("{Name} changed since it was hashed, so it is not recorded", Entry.File.Name);
					continue;
				}
				Track(Entry, Entry.Timestamp);
				PresentCount++;
			}
			if (fetched.Count == 0 && PresentCount == 0)
			{
				return;
			}
			GitDepsWorking.WriteManifest(Main, Manifest, services.DeleteFile);
			if (fetched.Count > 0)
			{
				log.LogInformation("Recorded {Count} fetched file(s) in {Path}, as GitDependencies records what it downloads", fetched.Count, Main);
			}
			if (PresentCount > 0)
			{
				log.LogInformation("Recorded {Count} file(s) already present in {Path} (untracked, with the manifest's hash), as GitDependencies records them", PresentCount, Main);
			}
		}
		catch (Exception Error) when (GitDepsWorking.IsManifestError(Error))
		{
			Exception Cause = Error is GitDepsLeftoverException ? Error.InnerException ?? Error : Error;
			string Message = Cause.InnerException?.Message is string Inner ? $"{Cause.Message} {Inner}" : Cause.Message;
			string Note = Error is GitDepsLeftoverException Leftover ? $"; {Leftover.Path} was left behind and could not be deleted ({Leftover.Reason}): delete it before running Setup"
				: !File.Exists(Main) && File.Exists(Main + ".tmp") ? $"; {Main}.tmp holds the update, which GitDependencies' next run moves into place"
				: "";
			log.LogWarning("{Path} could not be read or written ({Message}): {What} stay, untracked{Note}", Main, Message, What, Note);
		}
	}

	static string PathOf(string root, GitDepsEntry entry) => Path.Combine(root, entry.File.Name.Replace('/', Path.DirectorySeparatorChar));
}
