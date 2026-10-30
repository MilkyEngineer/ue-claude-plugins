// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Globalization;
using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Vcs;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>
/// `uak build`: builds a target through UBT under the editor lock, because a running editor holds the modules' binaries.
/// </summary>
public sealed class BuildCommand : IUakCommand
{
	readonly UnrealServices Services;

	/// <summary>Creates the command with the real services.</summary>
	public BuildCommand() : this(new UnrealServices())
	{
	}

	/// <summary>Creates the command with the given services (for tests).</summary>
	public BuildCommand(UnrealServices services)
	{
		Services = services;
	}

	/// <inheritdoc/>
	public string Name => "build";

	/// <inheritdoc/>
	public string Summary => "Build a target through UBT, under the editor lock.";

	/// <inheritdoc/>
	public string Usage =>
		"""
		uak build [options] [-- <UBT option>...]
		  -target=<name>            The UBT target (default: the project's editor target).
		  -config=<name>            Default Development.
		  -platform=<name>          Default host.
		  -MaxParallelActions=<n>   Default UAK_MAX_PARALLEL_ACTIONS, else UBT's own.
		  -maxerrors=<n>            Errors to print (default 10).
		  -name=<name>              Names the build's summary for `uak runs show -name=<name>` (default uak-build).
		  -resultfile=<path>        Also write the result as JSON. Deleted first, so an old one is never read
		                            as this run's; a refused or stopped run writes Outcome refused or stopped.
		  -- <UBT option>...        More UBT options, passed as given, e.g. -- -DisableAdaptiveUnity -Module=Foo.
		                            Options only: UBT reads a bare word as a target. Rejected because uak sets them or
		                            they break the lock or UBT's mutex: -Project, -Target, -TargetList, -Mode, -WaitMutex,
		                            -NoMutex, -NoHotReloadFromIDE, -ForceHotReload, -LiveCoding, -MaxParallelActions.
		The summary names the tree as the build found it once it held the lock: the revision and the changed files (or
		their count and hash), read alongside the build.
		It refuses to build while an interrupted `uak mutate` has left its patch in the tree (`uak mutate -restore`).
		-name= is a plain file name without spaces.
		Exit code: 0 built, 1 failed, 2 usage or setup error.
		""";

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return UnrealServices.GuardAsync(context, () => SummaryWriter.RecordingAsync(context, Services, "build", PeekName(arguments),
			() => UnrealServices.WithFreshResultFileAsync("build", arguments, () => RunCheckedAsync(context, arguments, cancellationToken))));
	}

	/// <summary>The -name= the build records its summary under, read before anything else is checked (null when it can't be read).</summary>
	static string? PeekName(IReadOnlyList<string> arguments)
	{
		try
		{
			return new UakArguments(arguments).GetString("name", "uak-build");
		}
		catch (UakUsageException)
		{
			return null;
		}
	}

	async Task<int> RunCheckedAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments Arguments = new(arguments);
		string? Target = Arguments.GetString("target");
		string Configuration = Arguments.GetString("config", "Development")!;
		UnrealPlatform Platform = UnrealServices.ParsePlatform(Arguments);
		int? MaxParallel = Services.MaxParallelActions(Arguments);
		int MaxErrors = Arguments.GetInt("maxerrors", 1) ?? 10;
		string Name = Arguments.GetString("name", "uak-build")!;
		RunSummaries.CheckName(Name);
		string? ResultFile = Arguments.GetString("resultfile");
		Arguments.ThrowIfUnknown();
		if (Arguments.Positional.Count > 0)
		{
			throw new UakUsageException("uak build takes no positional arguments: " + string.Join(' ', Arguments.Positional));
		}
		IReadOnlyList<string> PassThrough = UbtCommandLine.CheckPassThrough(Arguments.Rest);

		BuildRequest Request = new()
		{
			Target = Target,
			Configuration = Configuration,
			Platform = Platform,
			MaxParallelActions = MaxParallel,
			MaxErrors = MaxErrors,
			PassThrough = PassThrough,
			Name = Name,
			ResultFile = ResultFile,
		};
		return (await BuildAsync(context, Request, heldLock: null, cancellationToken).ConfigureAwait(false)).Built ? 0 : 1;
	}

	/// <summary>
	/// Builds and reports. Without <paramref name="heldLock"/> it takes the editor lock, and refuses to build a tree that a
	/// mutation run left changed; <c>uak mutate</c> passes the lock it holds. Returns whether the target built, and what
	/// UBT did.
	/// </summary>
	internal async Task<BuildOutcome> BuildAsync(UakContext context, BuildRequest request, IAsyncDisposable? heldLock, CancellationToken cancellationToken)
	{
		string? Project = context.ProjectFile?.FullName;
		string Target = request.Target ?? TargetResolver.ResolveEditorTarget(Project ?? throw new UakSetupException("no project found: give -project= or -target="));
		EngineLayout Paths = Services.Paths(context);
		ProcessInvocation Invocation = UbtCommandLine.Build(Paths, new UbtTarget(Target, request.Platform, request.Configuration, Project, request.MaxParallelActions),
			request.PassThrough);
		string LogFile = Services.NewLogFile(context, "build");
		// Named first, and written as it goes, so a long build can be followed in its log while it runs.
		context.Logger.LogInformation("Log: {Log}", LogFile);

		BuildLogParser Parser = new();
		int ExitCode;
		Stopwatch Timer;
		Task<VcsTreeState>? TreeTask;
		await using (StreamWriter Log = new(LogFile, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true })
		{
			await Log.WriteLineAsync("> " + Invocation).ConfigureAwait(false);
			IAsyncDisposable? OwnLock = heldLock is null
				? await Services.Lock.AcquireAsync(context, $"uak build {Target}", cancellationToken).ConfigureAwait(false)
				: null;
			await using (OwnLock)
			{
				IAsyncDisposable Lock = heldLock ?? OwnLock!;
				if (heldLock is null)
				{
					MutationJournal.ThrowIfUnfinished(context, building: true);
				}
				// Read alongside the build, so it never holds the lock up.
				TreeTask = request.CaptureTree ? Services.StartCaptureTree(context, cancellationToken) : null;
				context.Logger.LogInformation("Building {Target} {Platform} {Configuration}...", Target, request.Platform.Name, request.Configuration);
				UbtProgress Progress = new(Services.Clock, UbtProgress.DefaultInterval, Text => context.Logger.LogInformation("  {Progress}", Text));
				Timer = Stopwatch.StartNew();
				ExitCode = await Services.RunToolAsync(Invocation, Line =>
				{
					Log.WriteLine(Line);
					Parser.AddLine(Line);
					Progress.AddLine(Line);
				}, Lock, cancellationToken).ConfigureAwait(false);
				Timer.Stop();
			}
		}

		VcsTreeState? Tree = TreeTask is null ? null : await TreeTask.ConfigureAwait(false);
		BuildLogSummary Summary = Parser.Summary;
		foreach (CompileDiagnostic Error in Summary.Errors.Take(request.MaxErrors))
		{
			context.Logger.LogError("    {Error}", Error);
		}
		if (Summary.Errors.Count > request.MaxErrors)
		{
			context.Logger.LogError("    ... and {More} more errors", Summary.Errors.Count - request.MaxErrors);
		}
		bool Built = ExitCode == 0 && Summary.Errors.Count == 0;
		int Compiled = Summary.Actions.Count(Action => Action.Kind.Equals("Compile", StringComparison.OrdinalIgnoreCase));
		SummaryWriter Out = new(context.Logger);
		if (Tree is not null)
		{
			Out.Info("tree: " + Tree);
		}
		string Result = Summary.Result ?? $"exit code {ExitCode}";
		string Reason = Summary.ResultReason is null ? "" : $" ({Summary.ResultReason})";
		string UpToDate = Summary.UpToDate ? ", up to date" : "";
		string Line = string.Create(CultureInfo.InvariantCulture,
			$"build: {(Built ? "SUCCEEDED" : "FAILED")} ({Result}{Reason}; {Summary.Actions.Count} actions, {Compiled} compiles{UpToDate}) in {Timer.Elapsed.TotalSeconds:0.0} s. Log: {LogFile}");
		if (Built)
		{
			Out.Info(Line);
		}
		else
		{
			Out.Error(Line);
		}
		if (request.RecordSummary)
		{
			Out.Record(context, Services, "build", request.Name, Built ? 0 : 1);
		}
		UnrealServices.WriteResultFile(request.ResultFile, new
		{
			Outcome = Built ? "succeeded" : "failed",
			Target,
			UbtExitCode = ExitCode,
			Summary.Result,
			Summary.ResultReason,
			Actions = Summary.Actions.Count,
			Compiles = Compiled,
			Summary.UpToDate,
			Tree,
			Seconds = Math.Round(Timer.Elapsed.TotalSeconds, 1),
			LogFile,
			Errors = Summary.Errors.Select(Error => Error.ToString()).ToList(),
			Warnings = Summary.Warnings.Count,
		});
		return new BuildOutcome(Built, Summary.UpToDate,
			[.. Summary.Actions.Where(Action => Action.Kind.Equals("Compile", StringComparison.OrdinalIgnoreCase)).Select(Action => Action.Item)], Summary.Errors);
	}
}

/// <summary>
/// What a build did, for <c>uak mutate</c>: whether it built, whether UBT found it up to date, the items its Compile actions
/// named ("Foo.cpp", "Module.Game.2.cpp"), and its errors.
/// </summary>
internal sealed record BuildOutcome(bool Built, bool UpToDate, IReadOnlyList<string> Compiled, IReadOnlyList<CompileDiagnostic> Errors);

/// <summary>What <see cref="BuildCommand.BuildAsync"/> builds.</summary>
internal sealed record BuildRequest
{
	/// <summary>The UBT target; null for the project's editor target.</summary>
	public string? Target { get; init; }

	public string Configuration { get; init; } = "Development";

	public required UnrealPlatform Platform { get; init; }

	public int? MaxParallelActions { get; init; }

	public int MaxErrors { get; init; } = 10;

	public IReadOnlyList<string> PassThrough { get; init; } = [];

	/// <summary>The name its summary is recorded under, for <c>uak runs show</c>.</summary>
	public string Name { get; init; } = "uak-build";

	public string? ResultFile { get; init; }

	/// <summary>Whether the build records its summary; <c>uak mutate</c> records its own instead.</summary>
	public bool RecordSummary { get; init; } = true;

	/// <summary>Whether the build reads and prints the tree it built (<see cref="VcsTreeState"/>); <c>uak mutate</c> reads it once, for its tests.</summary>
	public bool CaptureTree { get; init; } = true;
}
