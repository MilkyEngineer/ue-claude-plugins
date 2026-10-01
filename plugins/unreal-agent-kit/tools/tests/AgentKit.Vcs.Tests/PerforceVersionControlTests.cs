// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using EpicGames.Perforce;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Vcs.Tests;

/// <summary>
/// Perforce over recorded server output: the fixtures go through EpicGames.Perforce's real -G parser, and these tests check
/// what we ask p4 and how the records map to statuses.
/// </summary>
[TestClass]
public sealed class PerforceVersionControlTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	[TestMethod]
	public void FixturesEncodeToMarshalRecords()
	{
		byte[] data = PerforceFixtures.Encode("connect-failed", "/ws");
		Assert.AreEqual((byte)'{', data[0]);
		Assert.AreEqual((byte)'0', data[^1]);
		Assert.HasCount(5, PerforceFixtures.ReadRecords("fstat-opened", "/ws"));
		StringAssert.Contains(PerforceFixtures.ReadRecords("connect-failed", "/ws")[0][1].Value, "\n\tTCP connect");
	}

	[TestMethod]
	public async Task RevisionIsTheHighestHaveChangelist()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("changes", "changes-have");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		Assert.AreEqual("12345", await p4.GetCurrentRevisionAsync(Token));

		PerforceCall call = connection.Calls.Single();
		CollectionAssert.Contains(call.Arguments.ToList(), "-m1");
		CollectionAssert.Contains(call.Arguments.ToList(), "-ssubmitted");
		Assert.AreEqual(Path.Combine(root.Path, "...") + "#have", call.Arguments[^1]);
	}

	[TestMethod]
	public async Task RevisionIsNullWhenNothingIsSynced()
	{
		using TempDirectory root = new();
		using PerforceVersionControl p4 = new(new FakePerforceConnection(root.Path).On("changes", "empty"), root.Directory);

		Assert.IsNull(await p4.GetCurrentRevisionAsync(Token));
	}

	[TestMethod]
	public async Task ChangedFilesAreTheOpenedFiles()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("fstat", "fstat-opened");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		IReadOnlyList<VcsFileStatus> changed = await p4.GetChangedFilesAsync(null, Token);

		CollectionAssert.AreEqual(
			new[]
			{
				new VcsFileStatus(root.Combine("Source/Game/Game.cpp"), VcsFileState.Modified, null, "edit"),
				new VcsFileStatus(root.Combine("Source/Game/New.cpp"), VcsFileState.Added, null, "add"),
				new VcsFileStatus(root.Combine("Content/Maps/Moved.umap"), VcsFileState.Renamed, "//Game/main/Content/Maps/Old.umap", "move/add"),
				new VcsFileStatus(root.Combine("Content/Maps/Old.umap"), VcsFileState.Deleted, null, "move/delete"),
				new VcsFileStatus(root.Combine("Config/Removed.ini"), VcsFileState.Deleted, null, "delete"),
			},
			changed.ToArray());
		PerforceCall call = connection.Calls.Single();
		CollectionAssert.Contains(call.Arguments.ToList(), "-Ro");
		Assert.AreEqual(Path.Combine(root.Path, "..."), call.FileArguments.Single());
	}

	[TestMethod]
	public async Task ChangedFilesWithAFilterAndNothingOpened()
	{
		using TempDirectory root = new();
		System.IO.Directory.CreateDirectory(root.Combine("Plugins"));
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("fstat", "fstat-none-opened");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		Assert.IsEmpty(await p4.GetChangedFilesAsync(root.Combine("Plugins"), Token));
		Assert.AreEqual(Path.Combine(root.Combine("Plugins"), "..."), connection.Calls.Single().FileArguments.Single());

		using TempDirectory outside = new();
		Assert.IsEmpty(await p4.GetChangedFilesAsync(outside.Path, Token));
		Assert.HasCount(1, connection.Calls, "a filter outside the client never reaches the server");
	}

	[TestMethod]
	public async Task FileStatusMapsRecordsAndLeftovers()
	{
		using TempDirectory root = new();
		string scratch = root.Write("Source/Game/Scratch.txt");
		string log = root.Write("Saved/Logs/Game.log");
		using TempDirectory outside = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path)
			.On("fstat", "fstat-status")
			.On("add", call => call.Arguments[^1].EndsWith("Game.log", StringComparison.Ordinal) ? "add-n-ignored" : "add-n-ok");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		string[] paths = [root.Combine("Source/Game/Game.h"), root.Combine("Source/Game/Game.cpp"), scratch, log, root.Combine("Source/Game/Missing.cpp"), outside.Combine("x.txt")];
		IReadOnlyList<VcsFileStatus> statuses = await p4.GetFileStatusAsync(paths, Token);

		CollectionAssert.AreEqual(paths, statuses.Select(status => status.Path).ToArray());
		CollectionAssert.AreEqual(
			new[] { VcsFileState.Unmodified, VcsFileState.Modified, VcsFileState.Untracked, VcsFileState.Ignored, VcsFileState.Unknown, VcsFileState.Unknown },
			statuses.Select(status => status.State).ToArray());
		Assert.AreEqual(5, connection.Calls.Single(call => call.Command == "fstat").FileArguments.Count(argument => argument.StartsWith(root.Path, StringComparison.Ordinal)), "only paths inside the client are asked about");
	}

	[TestMethod]
	public async Task IgnoredComesFromAddPreview()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path)
			.On("add", call => call.Arguments[^1].EndsWith(".log", StringComparison.Ordinal) ? "add-n-ignored" : "add-n-ok");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		Assert.IsTrue(await p4.IsIgnoredAsync(root.Combine("Saved/Logs/Game.log"), Token));
		Assert.IsFalse(await p4.IsIgnoredAsync(root.Combine("Source/Game/Scratch.txt"), Token));
		CollectionAssert.Contains(connection.Calls[0].Arguments.ToList(), "-n", "never opens anything");

		using TempDirectory outside = new();
		Assert.IsFalse(await p4.IsIgnoredAsync(outside.Combine("a.log"), Token));
		Assert.HasCount(2, connection.Calls);
	}

	[TestMethod]
	public async Task ServerFailureIsAVcsExceptionAndDescribeSaysUnreachable()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("changes", "connect-failed").On("fstat", "connect-failed");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		VcsException exception = await Assert.ThrowsExactlyAsync<VcsException>(() => p4.GetCurrentRevisionAsync(Token));
		StringAssert.Contains(exception.Message, "Connect to server failed");
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.GetChangedFilesAsync(null, Token));

		string description = await p4.DescribeAsync(Token);
		StringAssert.Contains(description, "Perforce client user-ws at " + root.Path);
		StringAssert.Contains(description, "unreachable");
	}

	[TestMethod]
	public async Task PlainTextFromP4IsAVcsException()
	{
		using TempDirectory root = new();
		string text = "text:Perforce client error:\n\tConnect to server failed; check $P4PORT.\n";
		using PerforceVersionControl p4 = new(new FakePerforceConnection(root.Path).On("changes", text).On("fstat", text).On("add", text), root.Directory);

		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.GetCurrentRevisionAsync(Token));
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.GetChangedFilesAsync(null, Token));
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.GetFileStatusAsync([root.Combine("a.txt")], Token));
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.IsIgnoredAsync(root.Combine("a.txt"), Token));
		string description = await p4.DescribeAsync(Token);
		StringAssert.Contains(description, "unreachable");
		Assert.DoesNotContain("\n", description);
	}

	[TestMethod]
	public async Task DescribeGivesClientServerAndHave()
	{
		using TempDirectory root = new();
		using PerforceVersionControl p4 = new(new FakePerforceConnection(root.Path).On("changes", "changes-have"), root.Directory);

		Assert.AreEqual($"Perforce client user-ws at {root.Path}, server perforce.example.com:1666, have @12345", await p4.DescribeAsync(Token));
	}

	[TestMethod]
	[DataRow(FileAction.Add, VcsFileState.Added)]
	[DataRow(FileAction.Branch, VcsFileState.Added)]
	[DataRow(FileAction.Import, VcsFileState.Added)]
	[DataRow(FileAction.MoveAdd, VcsFileState.Renamed)]
	[DataRow(FileAction.Edit, VcsFileState.Modified)]
	[DataRow(FileAction.Integrate, VcsFileState.Modified)]
	[DataRow(FileAction.Delete, VcsFileState.Deleted)]
	[DataRow(FileAction.MoveDelete, VcsFileState.Deleted)]
	[DataRow(FileAction.Purge, VcsFileState.Deleted)]
	[DataRow(FileAction.Archive, VcsFileState.Deleted)]
	public void ClassifiesOpenActions(FileAction action, VcsFileState expected)
	{
		Assert.AreEqual(expected, PerforceVersionControl.Classify(action).State);
	}

	[TestMethod]
	public void DisposingDisposesTheConnection()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new(root.Path);
		new PerforceVersionControl(connection, root.Directory).Dispose();
		Assert.IsTrue(connection.Disposed);
	}

	/// <summary>
	/// Against a real server. Skipped unless UAK_TEST_P4_WORKSPACE names a directory inside a synced client whose P4 settings
	/// (P4CONFIG or p4 set) work from there. It only reads: it never opens, edits or submits anything.
	/// </summary>
	[TestMethod]
	[TestCategory("Integration")]
	public async Task RealServerIntegration()
	{
		string? workspace = Environment.GetEnvironmentVariable("UAK_TEST_P4_WORKSPACE");
		if (string.IsNullOrEmpty(workspace))
		{
			Assert.Inconclusive("Set UAK_TEST_P4_WORKSPACE to a directory inside a synced Perforce client to run this test.");
			return;
		}

		VcsDetection detection = await VersionControlDetector.DetectAsync(new DirectoryInfo(workspace), new VcsDetectionOptions { EnvironmentOverride = null }, NullLogger.Instance, Token);
		using PerforceVersionControl p4 = (PerforceVersionControl)detection.VersionControl;
		Assert.IsTrue(VcsPaths.IsUnder(workspace, p4.RootDirectory.FullName), detection.Provenance);

		string? revision = await p4.GetCurrentRevisionAsync(Token);
		Assert.IsNotNull(revision);
		Assert.IsTrue(int.TryParse(revision, out _));

		IReadOnlyList<VcsFileStatus> changed = await p4.GetChangedFilesAsync(null, Token);
		Assert.IsTrue(changed.All(status => VcsPaths.IsUnder(status.Path, p4.RootDirectory.FullName)));

		string anyFile = System.IO.Directory.EnumerateFiles(workspace).First();
		IReadOnlyList<VcsFileStatus> statuses = await p4.GetFileStatusAsync([anyFile], Token);
		Assert.AreNotEqual(VcsFileState.Unknown, statuses[0].State);
		TestContext.WriteLine(await p4.DescribeAsync(Token));
	}
}
