// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Vcs;

/// <summary>The state of one file relative to the workspace's current revision.</summary>
public enum VcsFileState
{
	/// <summary>Neither tracked nor on disk, or outside the workspace.</summary>
	Unknown,

	/// <summary>Tracked and unchanged (Perforce: synced and not opened).</summary>
	Unmodified,

	/// <summary>Content changed (Perforce: opened for edit or integrate).</summary>
	Modified,

	/// <summary>Newly added (Git: staged as new; Perforce: opened for add, branch or import).</summary>
	Added,

	/// <summary>Deleted (Perforce: opened for delete, move/delete, purge or archive).</summary>
	Deleted,

	/// <summary>Renamed or moved; <see cref="VcsFileStatus.OriginalPath"/> holds where it came from.</summary>
	Renamed,

	/// <summary>Copied from <see cref="VcsFileStatus.OriginalPath"/> (Git only).</summary>
	Copied,

	/// <summary>The file type changed, for example a file became a symbolic link (Git only).</summary>
	TypeChanged,

	/// <summary>Has unresolved merge conflicts.</summary>
	Conflicted,

	/// <summary>On disk but not tracked, and not ignored.</summary>
	Untracked,

	/// <summary>On disk, not tracked, and ignored.</summary>
	Ignored,
}

/// <summary>The status of one file.</summary>
/// <param name="Path">Absolute path, in the platform's native form.</param>
/// <param name="State">The file's state.</param>
/// <param name="OriginalPath">For <see cref="VcsFileState.Renamed"/> and <see cref="VcsFileState.Copied"/>, the source: an absolute path for Git, a depot path for Perforce.</param>
/// <param name="Detail">The system's own code, for display: Git's two-letter XY status, or the Perforce action (for example "move/add").</param>
public sealed record VcsFileStatus(string Path, VcsFileState State, string? OriginalPath = null, string? Detail = null);
