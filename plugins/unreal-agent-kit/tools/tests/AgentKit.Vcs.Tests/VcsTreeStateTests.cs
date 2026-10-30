// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Vcs.Tests;

[TestClass]
public sealed class VcsTreeStateTests
{
	sealed class FakeVcs(string root, string? revision, IReadOnlyList<VcsFileStatus> changed, bool fail = false) : IVersionControl
	{
		public VcsKind Kind => VcsKind.Git;
		public DirectoryInfo? RootDirectory => new(root);
		public Task<string?> GetCurrentRevisionAsync(CancellationToken cancellationToken = default) =>
			fail ? throw new VcsException("git rev-parse HEAD failed") : Task.FromResult(revision);
		public Task<IReadOnlyList<VcsFileStatus>> GetChangedFilesAsync(string? pathFilter = null, CancellationToken cancellationToken = default) => Task.FromResult(changed);
		public Task<IReadOnlyList<VcsFileStatus>> GetFileStatusAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<bool> IsIgnoredAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<string> DescribeAsync(CancellationToken cancellationToken = default) => Task.FromResult("fake");
		public void Dispose()
		{
		}
	}

	[TestMethod]
	public async Task TheStateListsFewChangesAndHashesTheirContent()
	{
		using TempDirectory Temp = new();
		string A = Path.Combine(Temp.Path, "Source", "A.cpp");
		Directory.CreateDirectory(Path.GetDirectoryName(A)!);
		File.WriteAllText(A, "one");
		VcsFileStatus[] Changed = [new(A, VcsFileState.Modified), new(Path.Combine(Temp.Path, "Gone.h"), VcsFileState.Deleted)];

		VcsTreeState First = await VcsTreeState.CaptureAsync(new FakeVcs(Temp.Path, "abc123", Changed), CancellationToken.None);
		VcsTreeState Same = await VcsTreeState.CaptureAsync(new FakeVcs(Temp.Path, "abc123", [.. Changed.Reverse()]), CancellationToken.None);
		File.WriteAllText(A, "two");
		VcsTreeState Edited = await VcsTreeState.CaptureAsync(new FakeVcs(Temp.Path, "abc123", Changed), CancellationToken.None);

		Assert.AreEqual("Git", First.Kind);
		Assert.AreEqual(2, First.ChangedCount);
		CollectionAssert.AreEqual(new[] { "Deleted Gone.h", "Modified Source/A.cpp" }, First.Changed);
		Assert.AreEqual(12, First.ChangesHash!.Length);
		Assert.AreEqual(First.ChangesHash, Same.ChangesHash, "the order git lists them in must not change the hash");
		Assert.AreNotEqual(First.ChangesHash, Edited.ChangesHash, "another edit to the same files must change the hash");
		Assert.AreEqual($"Git abc123 + 2 changed (hash {First.ChangesHash}): Deleted Gone.h, Modified Source/A.cpp", First.ToString());
	}

	[TestMethod]
	public async Task ManyChangesAreCountedAndHashedNotListed()
	{
		using TempDirectory Temp = new();
		VcsFileStatus[] Changed = [.. Enumerable.Range(0, VcsTreeState.MaxListed + 1).Select(Index => new VcsFileStatus(Path.Combine(Temp.Path, $"F{Index}.cpp"), VcsFileState.Untracked))];

		VcsTreeState State = await VcsTreeState.CaptureAsync(new FakeVcs(Temp.Path, "abc", Changed), CancellationToken.None);

		Assert.IsNull(State.Changed);
		Assert.AreEqual($"Git abc + {VcsTreeState.MaxListed + 1} changed (hash {State.ChangesHash})", State.ToString());
	}

	[TestMethod]
	public async Task AFailedQueryIsTheStatesErrorNotTheRunsFailure()
	{
		VcsTreeState State = await VcsTreeState.CaptureAsync(new FakeVcs(Path.GetTempPath(), null, [], fail: true), CancellationToken.None);

		Assert.AreEqual("unknown (git rev-parse HEAD failed)", State.ToString());
		Assert.AreEqual("none (no version control)", (await VcsTreeState.CaptureAsync(new NoVersionControl(), CancellationToken.None)).ToString());
	}
}
