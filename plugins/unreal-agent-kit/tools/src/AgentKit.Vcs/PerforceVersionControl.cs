// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using EpicGames.Perforce;

namespace AgentKit.Vcs;

/// <summary>
/// Perforce, through EpicGames.Perforce's <see cref="IPerforceConnection"/> (which runs <c>p4 -G</c>; no native library):
/// <c>changes -m1 ...#have</c> for the revision, <c>fstat</c> for status, and <c>add -n</c> for ignore checks.
/// </summary>
/// <remarks>
/// "Changed" means opened in this client. Files edited without being opened (offline work) are not found; that needs
/// <c>p4 reconcile -n</c>, which reads every file and is left out for speed.
/// TODO: an opt-in offline check through <c>reconcile -n</c>, and reporting unresolved files as <see cref="VcsFileState.Conflicted"/>
/// (fstat's "unresolved" tag, which <see cref="FStatRecord"/> does not map).
/// </remarks>
public sealed partial class PerforceVersionControl : IVersionControl
{
	readonly IPerforceConnection _connection;

	/// <summary>Creates the Perforce implementation over a connection whose settings name the client.</summary>
	/// <param name="connection">The connection; this object disposes it.</param>
	/// <param name="rootDirectory">The client's root directory.</param>
	public PerforceVersionControl(IPerforceConnection connection, DirectoryInfo rootDirectory)
	{
		_connection = connection;
		RootDirectory = rootDirectory;
	}

	/// <inheritdoc/>
	public VcsKind Kind => VcsKind.Perforce;

	/// <inheritdoc/>
	public DirectoryInfo RootDirectory { get; }

	DirectoryInfo? IVersionControl.RootDirectory => RootDirectory;

	/// <summary>The client (workspace) name, from the connection's settings.</summary>
	public string? ClientName => _connection.Settings.ClientName;

	/// <summary>The whole client, as a local file spec: "&lt;root&gt;/...", with the root escaped.</summary>
	string RootSpec => DirectoryPattern(RootDirectory.FullName);

	/// <summary>
	/// Why the server was not reachable when this workspace was detected, or null. <see cref="DescribeAsync"/> then reports it
	/// instead of asking the server again (and waiting for another time-out).
	/// </summary>
	public string? UnreachableReason { get; init; }

	/// <inheritdoc/>
	public Task<string?> GetCurrentRevisionAsync(CancellationToken cancellationToken = default) => GuardAsync("changes", () => GetCurrentRevisionCoreAsync(cancellationToken));

	/// <inheritdoc/>
	public Task<IReadOnlyList<VcsFileStatus>> GetChangedFilesAsync(string? pathFilter = null, CancellationToken cancellationToken = default) => GuardAsync("fstat", () => GetChangedFilesCoreAsync(pathFilter, cancellationToken));

	/// <inheritdoc/>
	public Task<IReadOnlyList<VcsFileStatus>> GetFileStatusAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => GuardAsync("fstat", () => GetFileStatusCoreAsync(paths, cancellationToken));

	/// <inheritdoc/>
	public Task<bool> IsIgnoredAsync(string path, CancellationToken cancellationToken = default) => GuardAsync("add -n", () => IsIgnoredCoreAsync(path, cancellationToken));

	async Task<string?> GetCurrentRevisionCoreAsync(CancellationToken cancellationToken)
	{
		PerforceResponseList<ChangesRecord> responses = await _connection.TryGetChangesAsync(ChangesOptions.None, null, -1, 1, ChangeStatus.Submitted, null, RootSpec + "#have", cancellationToken).ConfigureAwait(false);
		ThrowOnFailure(responses, "changes");
		ChangesRecord? latest = responses.Where(response => response.Succeeded).Select(response => response.Data).FirstOrDefault();
		return latest?.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	async Task<IReadOnlyList<VcsFileStatus>> GetChangedFilesCoreAsync(string? pathFilter, CancellationToken cancellationToken)
	{
		string spec = RootSpec;
		if (pathFilter is not null)
		{
			string fullFilter = Path.GetFullPath(pathFilter);
			if (!VcsPaths.IsUnder(fullFilter, RootDirectory.FullName))
			{
				return [];
			}
			spec = Directory.Exists(fullFilter) ? DirectoryPattern(fullFilter) : EscapePath(fullFilter);
		}

		List<VcsFileStatus> result = [];
		await foreach (PerforceResponse<FStatRecord> response in _connection.TryFStatAsync(FStatOptions.OnlyOpenInWorkspace, spec, cancellationToken).ConfigureAwait(false))
		{
			if (response.Succeeded)
			{
				VcsFileStatus? status = ToStatus(response.Data);
				if (status is not null)
				{
					result.Add(status);
				}
			}
			else
			{
				// Warnings such as "file(s) not opened on this client" just mean nothing is open.
				ThrowOnFailure(response.Error, "fstat");
			}
		}
		return result;
	}

	async Task<IReadOnlyList<VcsFileStatus>> GetFileStatusCoreAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
	{
		string[] fullPaths = paths.Select(Path.GetFullPath).ToArray();
		List<string> inside = fullPaths.Where(path => VcsPaths.IsUnder(path, RootDirectory.FullName)).Distinct(VcsPaths.Comparer).ToList();

		Dictionary<string, VcsFileStatus> known = new(VcsPaths.Comparer);
		foreach (List<string> batch in VcsPaths.Batch(inside))
		{
			await foreach (PerforceResponse<FStatRecord> response in _connection.TryFStatAsync(FStatOptions.None, batch.Select(EscapePath).ToArray(), cancellationToken).ConfigureAwait(false))
			{
				if (response.Succeeded)
				{
					VcsFileStatus? status = ToStatus(response.Data);
					if (status is not null)
					{
						known.TryAdd(status.Path, status);
					}
				}
				else
				{
					// "no such file(s)" and "not in client view" are warnings about individual paths; those become Untracked or Unknown below.
					ThrowOnFailure(response.Error, "fstat");
				}
			}
		}

		List<VcsFileStatus> result = new(fullPaths.Length);
		foreach (string path in fullPaths)
		{
			if (known.TryGetValue(path, out VcsFileStatus? status))
			{
				result.Add(status);
			}
			else if (VcsPaths.IsUnder(path, RootDirectory.FullName) && File.Exists(path))
			{
				bool ignored = await IsIgnoredCoreAsync(path, cancellationToken).ConfigureAwait(false);
				result.Add(new VcsFileStatus(path, ignored ? VcsFileState.Ignored : VcsFileState.Untracked));
			}
			else
			{
				result.Add(new VcsFileStatus(path, VcsFileState.Unknown));
			}
		}
		return result;
	}

	async Task<bool> IsIgnoredCoreAsync(string path, CancellationToken cancellationToken)
	{
		string fullPath = Path.GetFullPath(path);
		if (!VcsPaths.IsUnder(fullPath, RootDirectory.FullName))
		{
			return false;
		}
		// "p4 add -n" previews an add without opening anything; an ignored file answers "... - ignored file can't be added."
		// add takes the name as it is on disk: with -f, p4 escapes @ # % * itself (and refuses such names without -f), so
		// this is the one command that gets the path unescaped.
		RejectWildcard(fullPath);
		AddOptions options = AddOptions.PreviewOnly | (NeedsEscaping(fullPath) ? AddOptions.IncludeWildcards : AddOptions.None);
		PerforceResponseList<AddRecord> responses = await _connection.TryAddAsync(-1, null, options, fullPath, cancellationToken).ConfigureAwait(false);
		foreach (PerforceResponse<AddRecord> response in responses)
		{
			string? message = response.Error?.Data ?? response.Info?.Data;
			if (message is not null && IsIgnoredMessage(message))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Whether a line of <c>p4 add -n</c> says the file is ignored: "&lt;path&gt; - ignored file can't be added." Only that
	/// message counts, not "ignored" anywhere in the line (a path such as "Docs/ignored-notes.txt" would match that).
	/// </summary>
	internal static bool IsIgnoredMessage(string message)
	{
		return message.Contains(" - ignored file can't be added", StringComparison.Ordinal);
	}

	/// <inheritdoc/>
	public async Task<string> DescribeAsync(CancellationToken cancellationToken = default)
	{
		string where = $"Perforce client {ClientName ?? "(unset)"} at {RootDirectory.FullName}, server {_connection.Settings.ServerAndPort}";
		if (UnreachableReason is not null)
		{
			return $"{where} (unreachable: {UnreachableReason})";
		}
		try
		{
			string? revision = await GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false);
			return revision is null ? $"{where}, nothing synced" : $"{where}, have @{revision}";
		}
		catch (VcsException exception)
		{
			return $"{where} (unreachable: {VcsPaths.OneLine(exception.Message)})";
		}
	}

	/// <summary>
	/// Runs a query, turning the library's failures into <see cref="VcsException"/>: p4 missing, or output that is not -G
	/// records (p4 prints some connection failures as plain text, which EpicGames.Perforce reports as an unexpected response).
	/// </summary>
	static async Task<T> GuardAsync<T>(string command, Func<Task<T>> query)
	{
		try
		{
			return await query().ConfigureAwait(false);
		}
		catch (PerforceException exception)
		{
			throw new VcsException($"p4 {command} failed: {VcsPaths.OneLine(exception.Message)}", exception);
		}
		catch (System.ComponentModel.Win32Exception exception)
		{
			throw new VcsException($"Could not run p4: {exception.Message}. Is it installed and on PATH?", exception);
		}
		catch (TimeoutException exception)
		{
			throw new VcsException(exception.Message, exception);
		}
	}

	/// <summary>The characters p4 reads as revision specifiers or wildcards in a file argument.</summary>
	static readonly char[] s_specialCharacters = ['@', '#', '%', '*'];

	/// <summary>Whether a path holds a character that p4 needs escaped (@ # % *).</summary>
	internal static bool NeedsEscaping(string path) => path.IndexOfAny(s_specialCharacters) >= 0;

	/// <summary>
	/// A local path as a p4 file argument that names exactly that file: % becomes %25, @ %40, # %23 and * %2A. A path holding
	/// "..." is rejected (<see cref="VcsException"/>), because p4 always reads it as a wildcard and it cannot be escaped.
	/// </summary>
	internal static string EscapePath(string path)
	{
		RejectWildcard(path);
		return path
			.Replace("%", "%25", StringComparison.Ordinal)
			.Replace("@", "%40", StringComparison.Ordinal)
			.Replace("#", "%23", StringComparison.Ordinal)
			.Replace("*", "%2A", StringComparison.Ordinal);
	}

	/// <summary>Everything under a directory, as a p4 file argument: the escaped directory, then the "..." wildcard.</summary>
	internal static string DirectoryPattern(string directory) => Path.Combine(EscapePath(directory), "...");

	/// <summary>Undoes <see cref="EscapePath"/>.</summary>
	internal static string UnescapePath(string path) => path
		.Replace("%40", "@", StringComparison.Ordinal)
		.Replace("%23", "#", StringComparison.Ordinal)
		.Replace("%2A", "*", StringComparison.OrdinalIgnoreCase)
		.Replace("%25", "%", StringComparison.Ordinal);

	static void RejectWildcard(string path)
	{
		if (path.Contains("...", StringComparison.Ordinal))
		{
			throw new VcsException($"Perforce cannot name {path}: p4 reads \"...\" in a path as a wildcard, and it cannot be escaped.");
		}
	}

	/// <summary>
	/// A record's local path as it is on disk. p4 may report a file whose name holds @ # % * in its escaped form (Foo%402x.png),
	/// so the unescaped form is used, unless only the escaped form exists on disk (a file really named with "%40").
	/// </summary>
	internal static string LocalPathFromRecord(string path)
	{
		string unescaped = UnescapePath(path);
		if (unescaped == path || (File.Exists(path) && !File.Exists(unescaped)))
		{
			return path;
		}
		return unescaped;
	}

	/// <inheritdoc/>
	public void Dispose() => _connection.Dispose();

	/// <summary>Maps an fstat record to a status. Records without a local path (not mapped into the client) are skipped.</summary>
	internal static VcsFileStatus? ToStatus(FStatRecord record)
	{
		string? localPath = record.ClientFile ?? record.Path;
		if (string.IsNullOrEmpty(localPath) || localPath.StartsWith("//", StringComparison.Ordinal))
		{
			return null;
		}
		string fullPath = Path.GetFullPath(LocalPathFromRecord(localPath));
		if (record.Action != FileAction.None)
		{
			(VcsFileState state, string detail) = Classify(record.Action);
			return new VcsFileStatus(fullPath, state, state == VcsFileState.Renamed ? record.MovedFile : null, detail);
		}
		if (record.HaveRevision > 0)
		{
			return new VcsFileStatus(fullPath, VcsFileState.Unmodified);
		}
		// Known to the depot but not synced here (or deleted at head).
		return new VcsFileStatus(fullPath, File.Exists(fullPath) ? VcsFileState.Untracked : VcsFileState.Unknown);
	}

	/// <summary>Maps an open action to a state and p4's own name for it.</summary>
	internal static (VcsFileState State, string Detail) Classify(FileAction action) => action switch
	{
		FileAction.Add => (VcsFileState.Added, "add"),
		FileAction.Branch => (VcsFileState.Added, "branch"),
		FileAction.Import => (VcsFileState.Added, "import"),
		FileAction.MoveAdd => (VcsFileState.Renamed, "move/add"),
		FileAction.Edit => (VcsFileState.Modified, "edit"),
		FileAction.Integrate => (VcsFileState.Modified, "integrate"),
		FileAction.Delete => (VcsFileState.Deleted, "delete"),
		FileAction.MoveDelete => (VcsFileState.Deleted, "move/delete"),
		FileAction.Purge => (VcsFileState.Deleted, "purge"),
		FileAction.Archive => (VcsFileState.Deleted, "archive"),
		_ => (VcsFileState.Modified, action.ToString().ToLowerInvariant()),
	};

	static void ThrowOnFailure<T>(PerforceResponseList<T> responses, string command) where T : class
	{
		foreach (PerforceError error in responses.Errors)
		{
			ThrowOnFailure(error, command);
		}
	}

	static void ThrowOnFailure(PerforceError? error, string command)
	{
		if (error is not null && error.Severity >= PerforceSeverityCode.Failed)
		{
			throw new VcsException($"p4 {command} failed: {error.Data.Trim()}");
		}
	}
}
