// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Vcs;

/// <summary>
/// The workspace has no version control. Every file on disk is <see cref="VcsFileState.Untracked"/>, nothing is changed or
/// ignored, and there is no revision.
/// </summary>
public sealed class NoVersionControl : IVersionControl
{
	/// <inheritdoc/>
	public VcsKind Kind => VcsKind.None;

	/// <inheritdoc/>
	public DirectoryInfo? RootDirectory => null;

	/// <inheritdoc/>
	public Task<string?> GetCurrentRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

	/// <inheritdoc/>
	public Task<IReadOnlyList<VcsFileStatus>> GetChangedFilesAsync(string? pathFilter = null, CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyList<VcsFileStatus>>([]);

	/// <inheritdoc/>
	public Task<IReadOnlyList<VcsFileStatus>> GetFileStatusAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		List<VcsFileStatus> result = new(paths.Count);
		foreach (string path in paths)
		{
			string fullPath = Path.GetFullPath(path);
			result.Add(new VcsFileStatus(fullPath, File.Exists(fullPath) || Directory.Exists(fullPath) ? VcsFileState.Untracked : VcsFileState.Unknown));
		}
		return Task.FromResult<IReadOnlyList<VcsFileStatus>>(result);
	}

	/// <inheritdoc/>
	public Task<bool> IsIgnoredAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(false);

	/// <inheritdoc/>
	public Task<string> DescribeAsync(CancellationToken cancellationToken = default) => Task.FromResult("None");

	/// <inheritdoc/>
	public void Dispose()
	{
	}
}
