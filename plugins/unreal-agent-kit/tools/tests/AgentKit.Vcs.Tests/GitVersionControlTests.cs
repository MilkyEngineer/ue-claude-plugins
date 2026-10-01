// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Vcs.Tests;

/// <summary>Git against real temporary repositories, made with the git command line.</summary>
[TestClass]
public sealed class GitVersionControlTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	[TestMethod]
	public async Task RevisionIsNullBeforeTheFirstCommitThenTheHeadHash()
	{
		using TempGitRepository repo = new();
		GitVersionControl git = new(repo.Directory);

		Assert.IsNull(await git.GetCurrentRevisionAsync(Token));

		repo.Write("README.md");
		string head = repo.Commit();

		Assert.AreEqual(head, await git.GetCurrentRevisionAsync(Token));
		Assert.AreEqual(40, head.Length);
	}

	[TestMethod]
	public async Task ChangedFilesCoverEveryKindOfChangeAndSkipIgnored()
	{
		using TempGitRepository repo = new();
		repo.Write(".gitignore", "Saved/\n*.tmp\n");
		repo.Write("Source/Game.cpp");
		repo.Write("Source/Old Name.cpp");
		repo.Write("Config/Gone.ini");
		repo.Commit();

		repo.Write("Source/Game.cpp", "changed\n");
		repo.Write("Source/Staged.cpp");
		repo.Git("add", "Source/Staged.cpp");
		File.Delete(repo.Combine("Config/Gone.ini"));
		repo.Git("mv", "Source/Old Name.cpp", "Source/New Name.cpp");
		repo.Write("Notes/Untracked.txt");
		repo.Write("Saved/Logs/Game.log");
		repo.Write("Scratch.tmp");

		GitVersionControl git = new(repo.Directory);
		IReadOnlyList<VcsFileStatus> changed = await git.GetChangedFilesAsync(null, Token);
		Dictionary<string, VcsFileStatus> byPath = changed.ToDictionary(status => status.Path, StringComparer.OrdinalIgnoreCase);

		Assert.HasCount(5, changed, string.Join(", ", changed));
		Assert.AreEqual(VcsFileState.Modified, byPath[repo.Combine("Source/Game.cpp")].State);
		Assert.AreEqual(VcsFileState.Added, byPath[repo.Combine("Source/Staged.cpp")].State);
		Assert.AreEqual(VcsFileState.Deleted, byPath[repo.Combine("Config/Gone.ini")].State);
		Assert.AreEqual(VcsFileState.Renamed, byPath[repo.Combine("Source/New Name.cpp")].State);
		Assert.AreEqual(repo.Combine("Source/Old Name.cpp"), byPath[repo.Combine("Source/New Name.cpp")].OriginalPath);
		Assert.AreEqual(VcsFileState.Untracked, byPath[repo.Combine("Notes/Untracked.txt")].State);
	}

	[TestMethod]
	public async Task ChangedFilesHonourThePathFilter()
	{
		using TempGitRepository repo = new();
		repo.Write("Source/A.cpp");
		repo.Write("Config/B.ini");
		repo.Commit();
		repo.Write("Source/A.cpp", "changed\n");
		repo.Write("Config/B.ini", "changed\n");

		GitVersionControl git = new(repo.Directory);

		IReadOnlyList<VcsFileStatus> source = await git.GetChangedFilesAsync(repo.Combine("Source"), Token);
		Assert.HasCount(1, source);
		Assert.AreEqual(repo.Combine("Source/A.cpp"), source[0].Path);

		using TempDirectory outside = new();
		Assert.IsEmpty(await git.GetChangedFilesAsync(outside.Path, Token));
	}

	[TestMethod]
	public async Task FileStatusGivesOneEntryPerPathInOrder()
	{
		using TempGitRepository repo = new();
		repo.Write(".gitignore", "Saved/\n");
		repo.Write("Source/Clean.cpp");
		repo.Write("Source/Edited.cpp");
		repo.Write("Source/With Space [1].cpp");
		repo.Commit();
		repo.Write("Source/Edited.cpp", "changed\n");
		repo.Write("Source/New.cpp");
		repo.Write("Saved/Logs/Game.log");
		using TempDirectory outside = new();
		string outsideFile = outside.Write("Elsewhere.txt");

		GitVersionControl git = new(repo.Directory);
		string[] paths =
		[
			repo.Combine("Source/Edited.cpp"),
			repo.Combine("Source/Clean.cpp"),
			repo.Combine("Source/New.cpp"),
			repo.Combine("Saved/Logs/Game.log"),
			repo.Combine("Source/Missing.cpp"),
			repo.Combine("Source/With Space [1].cpp"),
			outsideFile,
		];
		IReadOnlyList<VcsFileStatus> statuses = await git.GetFileStatusAsync(paths, Token);

		CollectionAssert.AreEqual(paths, statuses.Select(status => status.Path).ToArray());
		CollectionAssert.AreEqual(
			new[] { VcsFileState.Modified, VcsFileState.Unmodified, VcsFileState.Untracked, VcsFileState.Ignored, VcsFileState.Unknown, VcsFileState.Unmodified, VcsFileState.Unknown },
			statuses.Select(status => status.State).ToArray());
	}

	[TestMethod]
	public async Task FileStatusReportsConflicts()
	{
		using TempGitRepository repo = new();
		repo.Write("Merge.txt", "base\n");
		repo.Commit();
		repo.Git("checkout", "-q", "-b", "other");
		repo.Write("Merge.txt", "other\n");
		repo.Commit();
		repo.Git("checkout", "-q", "main");
		repo.Write("Merge.txt", "main\n");
		repo.Commit();
		Assert.ThrowsExactly<InvalidOperationException>(() => repo.Git("merge", "-q", "other"));

		GitVersionControl git = new(repo.Directory);
		IReadOnlyList<VcsFileStatus> statuses = await git.GetFileStatusAsync([repo.Combine("Merge.txt")], Token);

		Assert.AreEqual(VcsFileState.Conflicted, statuses[0].State);
	}

	[TestMethod]
	public async Task IgnoredOnlyForUntrackedMatchingFilesInsideTheRepository()
	{
		using TempGitRepository repo = new();
		repo.Write(".gitignore", "*.log\n");
		repo.Write("Tracked.log");
		repo.Git("add", "-f", "Tracked.log");
		repo.Commit();
		string ignored = repo.Write("Saved/Game.log");
		string normal = repo.Write("Source/Game.cpp");
		using TempDirectory outside = new();

		GitVersionControl git = new(repo.Directory);

		Assert.IsTrue(await git.IsIgnoredAsync(ignored, Token));
		Assert.IsTrue(await git.IsIgnoredAsync(repo.Combine("NotYetWritten.log"), Token), "check-ignore works on paths that do not exist yet");
		Assert.IsFalse(await git.IsIgnoredAsync(normal, Token));
		Assert.IsFalse(await git.IsIgnoredAsync(repo.Combine("Tracked.log"), Token), "tracked files are never ignored");
		Assert.IsFalse(await git.IsIgnoredAsync(outside.Write("Other.log"), Token));
	}

	[TestMethod]
	public async Task DescribeNamesRootHeadAndBranch()
	{
		using TempGitRepository repo = new();
		GitVersionControl git = new(repo.Directory);
		StringAssert.Contains(await git.DescribeAsync(Token), "no commits");

		repo.Write("README.md");
		string head = repo.Commit();
		string description = await git.DescribeAsync(Token);

		StringAssert.StartsWith(description, $"Git at {repo.Path}");
		StringAssert.Contains(description, head[..12]);
		StringAssert.Contains(description, "(main)");
	}

	/// <summary>A read-only smoke test on the repository the kit is built in, when it is built inside one.</summary>
	[TestMethod]
	public async Task ReadsTheRepositoryHoldingTheKit()
	{
		DirectoryInfo? root = VersionControlDetector.FindGitRoot(new DirectoryInfo(AppContext.BaseDirectory));
		if (root is null)
		{
			Assert.Inconclusive("The kit is not built inside a git repository.");
			return;
		}

		GitVersionControl git = new(root);
		string? revision = await git.GetCurrentRevisionAsync(Token);
		IReadOnlyList<VcsFileStatus> changed = await git.GetChangedFilesAsync(null, Token);
		string thisProject = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "AgentKit.Vcs.Tests", "AgentKit.Vcs.Tests.csproj"));
		IReadOnlyList<VcsFileStatus> statuses = await git.GetFileStatusAsync([thisProject], Token);

		TestContext.WriteLine(await git.DescribeAsync(Token));
		TestContext.WriteLine($"{changed.Count} changed; {thisProject}: {statuses[0].State}");
		Assert.IsTrue(changed.All(status => VcsPaths.IsUnder(status.Path, root.FullName)));
		Assert.AreNotEqual(VcsFileState.Unknown, statuses[0].State, thisProject);
		Assert.AreNotEqual(VcsFileState.Ignored, statuses[0].State);
		Assert.IsTrue(await git.IsIgnoredAsync(Path.Combine(AppContext.BaseDirectory, "AgentKit.Vcs.dll"), Token), "tools/.gitignore ignores bin/");
	}

	[TestMethod]
	public async Task MissingGitExecutableIsAVcsException()
	{
		using TempGitRepository repo = new();
		GitVersionControl git = new(repo.Directory, "uak-no-such-git-executable");

		await Assert.ThrowsExactlyAsync<VcsException>(() => git.GetCurrentRevisionAsync(Token));
	}

	[TestMethod]
	public async Task NotARepositoryIsAVcsException()
	{
		using TempDirectory directory = new();
		GitVersionControl git = new(directory.Directory);

		await Assert.ThrowsExactlyAsync<VcsException>(() => git.GetChangedFilesAsync(null, Token));
	}
}
