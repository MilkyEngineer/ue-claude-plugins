// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Vcs;

/// <summary>
/// Git, through the <c>git</c> command line: <c>status --porcelain=v2 -z</c>, <c>rev-parse</c>, <c>ls-files</c> and
/// <c>check-ignore</c>. Every command runs with <c>-C &lt;root&gt;</c> and literal pathspecs, so file names are never globs, and
/// with a time limit (<see cref="Timeout"/>), after which git is killed and the call throws <see cref="VcsException"/>.
/// </summary>
public sealed class GitVersionControl : IVersionControl
{
	/// <summary>The environment variable giving the time limit of each git command, in seconds.</summary>
	public const string TimeoutVariable = VcsProcess.GitTimeoutVariable;

	readonly string _gitExecutable;

	/// <summary>Creates the Git implementation for a work tree.</summary>
	/// <param name="rootDirectory">The work tree's top-level directory (the one holding <c>.git</c>).</param>
	/// <param name="gitExecutable">The git program; "git" finds it on PATH.</param>
	/// <param name="timeout">The time limit of each git command; null for <see cref="GetTimeout"/> (UAK_GIT_TIMEOUT, else 30 s).</param>
	public GitVersionControl(DirectoryInfo rootDirectory, string gitExecutable = "git", TimeSpan? timeout = null)
	{
		RootDirectory = rootDirectory;
		_gitExecutable = gitExecutable;
		Timeout = timeout ?? GetTimeout();
	}

	/// <summary>How long one git command may run.</summary>
	public TimeSpan Timeout { get; }

	/// <summary>The time limit from <see cref="TimeoutVariable"/> (positive seconds), else 30 s.</summary>
	public static TimeSpan GetTimeout(Func<string, string?>? getEnvironmentVariable = null) => VcsProcess.GetGitTimeout(getEnvironmentVariable);

	/// <inheritdoc/>
	public VcsKind Kind => VcsKind.Git;

	/// <inheritdoc/>
	public DirectoryInfo RootDirectory { get; }

	DirectoryInfo? IVersionControl.RootDirectory => RootDirectory;

	/// <inheritdoc/>
	public async Task<string?> GetCurrentRevisionAsync(CancellationToken cancellationToken = default)
	{
		// --verify -q exits 1 without output when HEAD does not exist yet (a repository with no commits).
		ProcessCapture result = await RunGitAsync(["rev-parse", "--verify", "-q", "HEAD"], cancellationToken).ConfigureAwait(false);
		if (result.ExitCode == 1 && result.StandardOutput.Length == 0)
		{
			return null;
		}
		EnsureSuccess(result, "rev-parse HEAD");
		return result.StandardOutput.Trim();
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<VcsFileStatus>> GetChangedFilesAsync(string? pathFilter = null, CancellationToken cancellationToken = default)
	{
		List<string> arguments = ["status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignored=no"];
		if (pathFilter is not null)
		{
			string fullFilter = Path.GetFullPath(pathFilter);
			if (!VcsPaths.IsUnder(fullFilter, RootDirectory.FullName))
			{
				return [];
			}
			arguments.Add("--");
			arguments.Add(fullFilter);
		}
		ProcessCapture result = await RunGitAsync(arguments, cancellationToken).ConfigureAwait(false);
		EnsureSuccess(result, "status");
		return GitStatusParser.Parse(result.StandardOutput, RootDirectory.FullName);
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<VcsFileStatus>> GetFileStatusAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		string[] fullPaths = paths.Select(Path.GetFullPath).ToArray();
		List<string> inside = fullPaths.Where(path => VcsPaths.IsUnder(path, RootDirectory.FullName)).Distinct(VcsPaths.Comparer).ToList();

		// Changed files, from status.
		Dictionary<string, VcsFileStatus> known = new(VcsPaths.Comparer);
		foreach (List<string> batch in VcsPaths.Batch(inside))
		{
			ProcessCapture status = await RunGitAsync(["status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignored=no", "--", .. batch], cancellationToken).ConfigureAwait(false);
			EnsureSuccess(status, "status");
			foreach (VcsFileStatus entry in GitStatusParser.Parse(status.StandardOutput, RootDirectory.FullName))
			{
				known.TryAdd(entry.Path, entry);
			}
		}

		// Unchanged tracked files, from ls-files.
		List<string> remaining = inside.Where(path => !known.ContainsKey(path)).ToList();
		HashSet<string> tracked = new(VcsPaths.Comparer);
		foreach (List<string> batch in VcsPaths.Batch(remaining))
		{
			ProcessCapture files = await RunGitAsync(["ls-files", "-z", "--full-name", "--", .. batch], cancellationToken).ConfigureAwait(false);
			EnsureSuccess(files, "ls-files");
			foreach (string relative in files.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
			{
				tracked.Add(GitStatusParser.ToFullPath(RootDirectory.FullName, relative));
			}
		}

		// Whatever is left and on disk is untracked or ignored.
		List<string> onDisk = remaining.Where(path => !tracked.Contains(path) && (File.Exists(path) || Directory.Exists(path))).ToList();
		HashSet<string> ignored = await GetIgnoredAsync(onDisk, cancellationToken).ConfigureAwait(false);

		List<VcsFileStatus> result = new(fullPaths.Length);
		foreach (string path in fullPaths)
		{
			if (known.TryGetValue(path, out VcsFileStatus? status))
			{
				result.Add(status);
			}
			else if (tracked.Contains(path))
			{
				result.Add(new VcsFileStatus(path, VcsFileState.Unmodified));
			}
			else if (ignored.Contains(path))
			{
				result.Add(new VcsFileStatus(path, VcsFileState.Ignored, null, "!!"));
			}
			else if (onDisk.Contains(path, VcsPaths.Comparer))
			{
				result.Add(new VcsFileStatus(path, VcsFileState.Untracked, null, "??"));
			}
			else
			{
				result.Add(new VcsFileStatus(path, VcsFileState.Unknown));
			}
		}
		return result;
	}

	/// <inheritdoc/>
	public async Task<bool> IsIgnoredAsync(string path, CancellationToken cancellationToken = default)
	{
		string fullPath = Path.GetFullPath(path);
		if (!VcsPaths.IsUnder(fullPath, RootDirectory.FullName))
		{
			return false;
		}
		HashSet<string> ignored = await GetIgnoredAsync([fullPath], cancellationToken).ConfigureAwait(false);
		return ignored.Count > 0;
	}

	/// <inheritdoc/>
	public async Task<string> DescribeAsync(CancellationToken cancellationToken = default)
	{
		string? revision = await GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false);
		ProcessCapture branch = await RunGitAsync(["symbolic-ref", "--short", "-q", "HEAD"], cancellationToken).ConfigureAwait(false);
		string branchName = branch.ExitCode == 0 ? branch.StandardOutput.Trim() : "detached";
		string head = revision is null ? "no commits" : $"HEAD {revision[..Math.Min(12, revision.Length)]}";
		return $"Git at {RootDirectory.FullName}, {head} ({branchName})";
	}

	/// <inheritdoc/>
	public void Dispose()
	{
	}

	/// <summary>Runs <c>git check-ignore</c> on the paths and returns those it ignores. Tracked files are never reported.</summary>
	async Task<HashSet<string>> GetIgnoredAsync(IReadOnlyList<string> fullPaths, CancellationToken cancellationToken)
	{
		HashSet<string> ignored = new(VcsPaths.Comparer);
		// Paths go in relative to the root with '/' separators, and come back the same way, so git never needs to quote them.
		// (-z would avoid quoting too, but check-ignore only takes it with --stdin, and Core's process runner has no stdin.)
		IEnumerable<string> relativePaths = fullPaths.Select(path => Path.GetRelativePath(RootDirectory.FullName, path).Replace(Path.DirectorySeparatorChar, '/'));
		foreach (List<string> batch in VcsPaths.Batch(relativePaths))
		{
			// Exit 0: some are ignored (listed); 1: none are; anything else is an error.
			ProcessCapture result = await RunGitAsync(["check-ignore", "--", .. batch], cancellationToken).ConfigureAwait(false);
			if (result.ExitCode == 1)
			{
				continue;
			}
			EnsureSuccess(result, "check-ignore");
			foreach (string reported in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				ignored.Add(GitStatusParser.ToFullPath(RootDirectory.FullName, reported));
			}
		}
		return ignored;
	}

	/// <summary>Runs git in the root. Pathspecs are literal, except for check-ignore, which takes plain paths and rejects pathspec magic.</summary>
	Task<ProcessCapture> RunGitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		List<string> fullArguments = ["-C", RootDirectory.FullName, "-c", "core.quotepath=false"];
		if (arguments[0] != "check-ignore")
		{
			fullArguments.Add("--literal-pathspecs");
		}
		fullArguments.AddRange(arguments);
		return VcsProcess.RunAsync(ProcessRunner.Default, _gitExecutable, fullArguments, "git " + arguments[0], Timeout, TimeoutVariable, cancellationToken);
	}

	static void EnsureSuccess(ProcessCapture result, string command)
	{
		if (result.ExitCode != 0)
		{
			throw new VcsException($"git {command} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
		}
	}
}
