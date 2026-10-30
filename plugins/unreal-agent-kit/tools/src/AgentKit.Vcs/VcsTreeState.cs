// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentKit.Core;

namespace AgentKit.Vcs;

/// <summary>
/// The source tree as a build or test found it once it held the editor lock: the revision (as <c>uak vcs revision</c> gives
/// it) and the files that differ from it (as <c>uak vcs changed</c> lists them), with a short hash that tells two trees
/// apart. Recorded in a run's result and summary, so a result can be matched to the code it came from. It is read alongside
/// the run, so it is no proof of what was built or tested: an editor test builds nothing, <c>-cooked -skipbuild -skipcook</c>
/// tests a game staged earlier, and a file changed during the run is not seen.
/// </summary>
public sealed class VcsTreeState
{
	/// <summary>Up to this many changed files are listed by name; above it, only their count and hash.</summary>
	public const int MaxListed = 10;

	/// <summary>The first this many changed files (by path) are hashed by content; the rest by size and write time.</summary>
	public const int MaxContentHashed = 200;

	/// <summary>A changed file larger than this is hashed by size and write time, not by content.</summary>
	public const long MaxContentBytes = 16L * 1024 * 1024;

	/// <summary>"Git", "Perforce" or "None"; null when it could not be read.</summary>
	public string? Kind { get; init; }

	/// <summary>The revision: HEAD's commit hash (Git), or the highest changelist the client has synced (Perforce). Null when there is none.</summary>
	public string? Revision { get; init; }

	/// <summary>How many files differ from the revision.</summary>
	public int ChangedCount { get; init; }

	/// <summary>The changed files ("Modified Source/A.cpp", relative to the workspace root), when there are at most <see cref="MaxListed"/>; else null.</summary>
	public List<string>? Changed { get; init; }

	/// <summary>
	/// The first 12 hex digits of a SHA-256 over each changed file's state, path (relative, '/' separated) and content, sorted
	/// by path: in one workspace, the same changes give the same hash, and a different edit to the same files gives another.
	/// It is no fingerprint across machines: it hashes the bytes as checked out (a CRLF and an LF checkout differ), Git's
	/// index is not part of it (a staged and an unstaged edit to a tracked file hash alike; a new file hashes otherwise once
	/// it is added, since its state changes from untracked to added), and Perforce lists only opened files (a writable file
	/// that isn't opened counts as unmodified). Past <see cref="MaxContentHashed"/> files, and for a file over
	/// <see cref="MaxContentBytes"/>, a file's size and write time stand in for its content. Null when nothing changed.
	/// </summary>
	public string? ChangesHash { get; init; }

	/// <summary>Why the state could not be read (the version-control query failed); null when it was.</summary>
	public string? Error { get; init; }

	/// <summary>The summary line's text after "tree: ".</summary>
	public override string ToString()
	{
		if (Error is not null)
		{
			return $"unknown ({Error})";
		}
		if (Kind is null || Kind == nameof(VcsKind.None))
		{
			return "none (no version control)";
		}
		string revision = Revision ?? "no revision";
		if (ChangedCount == 0)
		{
			return $"{Kind} {revision}, no changes";
		}
		string count = string.Create(CultureInfo.InvariantCulture, $"{ChangedCount} changed (hash {ChangesHash})");
		return Changed is null ? $"{Kind} {revision} + {count}" : $"{Kind} {revision} + {count}: {string.Join(", ", Changed)}";
	}

	/// <summary>Reads the tree of the context's workspace. Never throws for a version-control failure: that is the state's <see cref="Error"/>.</summary>
	public static async Task<VcsTreeState> CaptureAsync(UakContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		try
		{
			VcsDetection detection = await VersionControlDetector.DetectAsync(context, null, cancellationToken).ConfigureAwait(false);
			using IVersionControl vcs = detection.VersionControl;
			return await CaptureAsync(vcs, cancellationToken).ConfigureAwait(false);
		}
		catch (VcsException exception)
		{
			return new VcsTreeState { Error = exception.Message };
		}
	}

	/// <summary>Reads the tree of a workspace. Never throws for a version-control failure: that is the state's <see cref="Error"/>.</summary>
	public static async Task<VcsTreeState> CaptureAsync(IVersionControl vcs, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(vcs);
		if (vcs.Kind == VcsKind.None)
		{
			return new VcsTreeState { Kind = nameof(VcsKind.None) };
		}
		try
		{
			string? revision = await vcs.GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false);
			IReadOnlyList<VcsFileStatus> changed = await vcs.GetChangedFilesAsync(null, cancellationToken).ConfigureAwait(false);
			string root = vcs.RootDirectory?.FullName ?? "";
			List<(string Path, VcsFileStatus Status)> files = [.. changed
				.Select(status => (Path: Relative(root, status.Path), Status: status))
				.OrderBy(file => file.Path, StringComparer.Ordinal)];
			return new VcsTreeState
			{
				Kind = vcs.Kind.ToString(),
				Revision = revision,
				ChangedCount = files.Count,
				Changed = files.Count <= MaxListed ? [.. files.Select(file => $"{file.Status.State} {file.Path}")] : null,
				ChangesHash = files.Count == 0 ? null : Hash(files),
			};
		}
		catch (VcsException exception)
		{
			return new VcsTreeState { Kind = vcs.Kind.ToString(), Error = exception.Message };
		}
	}

	static string Relative(string root, string path)
	{
		string relative = root.Length > 0 && VcsPaths.IsUnder(path, root) ? Path.GetRelativePath(root, path) : path;
		return relative.Replace('\\', '/');
	}

	/// <summary>
	/// The hash of the changes: per file, its state, path and the SHA-256 of its bytes ("-" when it is not a readable file:
	/// deleted, a directory, or locked). Past <see cref="MaxContentHashed"/> files, and for a file over
	/// <see cref="MaxContentBytes"/>, its size and write time instead, so a tree with many untracked files never takes long.
	/// </summary>
	static string Hash(List<(string Path, VcsFileStatus Status)> files)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		for (int index = 0; index < files.Count; index++)
		{
			(string path, VcsFileStatus status) = files[index];
			string content = index < MaxContentHashed ? ContentHash(status.Path) : StatHash(status.Path);
			hash.AppendData(Encoding.UTF8.GetBytes($"{status.State}\0{path}\0{content}\n"));
		}
		return Convert.ToHexString(hash.GetHashAndReset())[..12].ToLowerInvariant();
	}

	/// <summary>A file's size and last write time, standing in for its content; "-" when it is not a file.</summary>
	static string StatHash(string path)
	{
		FileInfo file = new(path);
		return file.Exists ? string.Create(CultureInfo.InvariantCulture, $"size {file.Length}, written {file.LastWriteTimeUtc.Ticks}") : "-";
	}

	static string ContentHash(string path)
	{
		try
		{
			if (new FileInfo(path) is { Exists: true, Length: > MaxContentBytes })
			{
				return StatHash(path);
			}
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return "-";
		}
	}
}
