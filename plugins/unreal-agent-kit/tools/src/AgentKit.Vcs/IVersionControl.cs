// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Vcs;

/// <summary>Which version-control system a workspace uses.</summary>
public enum VcsKind
{
	/// <summary>No version control was found, or it was turned off with <c>-vcs=none</c>.</summary>
	None,

	/// <summary>Git, driven through the <c>git</c> command line.</summary>
	Git,

	/// <summary>Perforce, driven through EpicGames.Perforce (which runs <c>p4 -G</c>).</summary>
	Perforce,
}

/// <summary>
/// Read-only access to the version control of a workspace (DESIGN.md, "Version control"). Writes (commit, submit, checkout,
/// edit) are out of scope. Paths given to and returned from these methods are absolute, in the platform's native form.
/// Dispose it when done: the Perforce implementation owns a connection.
/// </summary>
public interface IVersionControl : IDisposable
{
	/// <summary>The system in use.</summary>
	VcsKind Kind { get; }

	/// <summary>The workspace root: the directory holding <c>.git</c>, or the Perforce client root. Null for <see cref="VcsKind.None"/>.</summary>
	DirectoryInfo? RootDirectory { get; }

	/// <summary>
	/// The revision the workspace is at: the commit hash of HEAD for Git, the highest changelist the client has synced (<c>#have</c>)
	/// for Perforce. Null when there is none yet (an empty repository or client) or for <see cref="VcsKind.None"/>.
	/// </summary>
	Task<string?> GetCurrentRevisionAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Files that differ from the current revision: modified, added, deleted, renamed, conflicted or untracked files for Git, and
	/// files opened in the client for Perforce. Ignored files are not listed.
	/// </summary>
	/// <param name="pathFilter">A file or directory to limit the result to; null for the whole workspace.</param>
	/// <param name="cancellationToken">Cancels the query.</param>
	Task<IReadOnlyList<VcsFileStatus>> GetChangedFilesAsync(string? pathFilter = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// The status of each given file, one entry per path in the order given. Unchanged tracked files are
	/// <see cref="VcsFileState.Unmodified"/>; files outside the workspace, or neither tracked nor on disk, are <see cref="VcsFileState.Unknown"/>.
	/// </summary>
	Task<IReadOnlyList<VcsFileStatus>> GetFileStatusAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

	/// <summary>Whether the version-control system ignores the path (.gitignore, P4IGNORE). Tracked files are never ignored.</summary>
	Task<bool> IsIgnoredAsync(string path, CancellationToken cancellationToken = default);

	/// <summary>A one-line summary for <c>uak env</c>, such as "Git at C:\Repo, HEAD 1a2b3c4d (main)".</summary>
	Task<string> DescribeAsync(CancellationToken cancellationToken = default);
}

/// <summary>A failure to query version control: the tool is missing, the server is unreachable, or a command failed.</summary>
public sealed class VcsException : Exception
{
	/// <summary>Creates the exception with a message.</summary>
	public VcsException(string message) : base(message)
	{
	}

	/// <summary>Creates the exception with a message and the failure that caused it.</summary>
	public VcsException(string message, Exception innerException) : base(message, innerException)
	{
	}
}
