// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using EpicGames.Perforce;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Vcs.Tests;

/// <summary>Detection order and overrides. Perforce uses a fake P4CONFIG and recorded p4 info output; no server is needed.</summary>
[TestClass]
public sealed class VersionControlDetectorTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	/// <summary>Options isolated from this machine's UAK_VCS and Perforce settings, with p4 replaced by a fake.</summary>
	static VcsDetectionOptions Isolated(Dictionary<string, string>? p4Environment = null, Func<IPerforceSettings, FakePerforceConnection>? connection = null, string? kind = null, string? environmentOverride = null) => new()
	{
		KindOverride = kind,
		EnvironmentOverride = environmentOverride,
		PerforceEnvironment = new FixedPerforceEnvironment(p4Environment ?? []),
		PerforceConnectionFactory = connection is null ? (_, _) => throw new AssertFailedException("p4 should not run") : (settings, _) => connection(settings),
	};

	[TestMethod]
	public async Task NothingFoundIsNone()
	{
		using TempDirectory directory = new();
		VcsDetection detection = await VersionControlDetector.DetectAsync(directory.Directory, Isolated(), NullLogger.Instance, Token);

		Assert.AreEqual(VcsKind.None, detection.VersionControl.Kind);
		Assert.IsNull(detection.VersionControl.RootDirectory);
	}

	[TestMethod]
	public async Task GitDirectoryAboveTheProject()
	{
		using TempDirectory repo = new();
		System.IO.Directory.CreateDirectory(repo.Combine(".git"));
		System.IO.Directory.CreateDirectory(repo.Combine("Games/MyGame"));

		VcsDetection detection = await VersionControlDetector.DetectAsync(new DirectoryInfo(repo.Combine("Games/MyGame")), Isolated(), NullLogger.Instance, Token);

		Assert.AreEqual(VcsKind.Git, detection.VersionControl.Kind);
		Assert.AreEqual(repo.Path, detection.VersionControl.RootDirectory!.FullName);
		StringAssert.Contains(detection.Provenance, ".git at " + repo.Path);
	}

	[TestMethod]
	public async Task GitFileCountsForWorktreesAndSubmodules()
	{
		using TempDirectory repo = new();
		repo.Write(".git", "gitdir: ../main/.git/worktrees/feature\n");

		VcsDetection detection = await VersionControlDetector.DetectAsync(repo.Directory, Isolated(), NullLogger.Instance, Token);

		Assert.AreEqual(VcsKind.Git, detection.VersionControl.Kind);
	}

	[TestMethod]
	public async Task GitWinsOverPerforce()
	{
		using TempDirectory repo = new();
		System.IO.Directory.CreateDirectory(repo.Combine(".git"));
		repo.Write(".p4config", "P4CLIENT=user-ws\n");

		VcsDetection detection = await VersionControlDetector.DetectAsync(repo.Directory, Isolated(new() { ["P4CONFIG"] = ".p4config" }), NullLogger.Instance, Token);

		Assert.AreEqual(VcsKind.Git, detection.VersionControl.Kind);
	}

	[TestMethod]
	public async Task P4ConfigFileFindsTheClientRootThroughP4Info()
	{
		using TempDirectory workspace = new();
		workspace.Write(".p4config", "P4PORT=ssl:perforce.example.com:1666\nP4USER=user\nP4CLIENT=user-ws\n");
		System.IO.Directory.CreateDirectory(workspace.Combine("Game"));
		IPerforceSettings? seen = null;

		VcsDetection detection = await VersionControlDetector.DetectAsync(
			new DirectoryInfo(workspace.Combine("Game")),
			Isolated(new() { ["P4CONFIG"] = ".p4config", ["P4PORT"] = "global:1666" }, settings =>
			{
				seen = settings;
				return new FakePerforceConnection(workspace.Path, settings).On("info", "info");
			}),
			NullLogger.Instance,
			Token);

		using IVersionControl _ = detection.VersionControl;
		Assert.AreEqual(VcsKind.Perforce, detection.VersionControl.Kind);
		Assert.AreEqual(workspace.Path, detection.VersionControl.RootDirectory!.FullName);
		StringAssert.Contains(detection.Provenance, "P4CONFIG file " + workspace.Combine(".p4config"));
		StringAssert.Contains(detection.Provenance, "client user-ws");
		Assert.AreEqual("ssl:perforce.example.com:1666", seen!.ServerAndPort, "the config file overrides the global setting");
		Assert.AreEqual("user-ws", seen.ClientName);
	}

	[TestMethod]
	public async Task P4ConfigFileStillCountsWhenTheServerIsUnreachable()
	{
		using TempDirectory workspace = new();
		workspace.Write(".p4config", "P4CLIENT=user-ws\n");

		FakePerforceConnection? connection = null;
		VcsDetection detection = await VersionControlDetector.DetectAsync(
			workspace.Directory,
			Isolated(new() { ["P4CONFIG"] = ".p4config" }, settings => connection = new FakePerforceConnection(workspace.Path, settings).On("info", "connect-failed")),
			NullLogger.Instance,
			Token);

		using IVersionControl _ = detection.VersionControl;
		Assert.AreEqual(VcsKind.Perforce, detection.VersionControl.Kind);
		Assert.AreEqual(workspace.Path, detection.VersionControl.RootDirectory!.FullName);
		StringAssert.Contains(detection.Provenance, "Connect to server failed");

		// Describing it (uak env) does not ask the unreachable server again: the fake answers nothing but info.
		string description = await detection.VersionControl.DescribeAsync(Token);
		StringAssert.Contains(description, "unreachable: ");
		StringAssert.Contains(description, "Connect to server failed");
		Assert.HasCount(1, connection!.Calls);
	}

	[TestMethod]
	public async Task SlowP4InfoCountsAsUnreachable()
	{
		using TempDirectory workspace = new();
		workspace.Write(".p4config", "P4CLIENT=user-ws\n");

		VcsDetection detection = await VersionControlDetector.DetectAsync(
			workspace.Directory,
			Isolated(new() { ["P4CONFIG"] = ".p4config" }, settings => new FakePerforceConnection(workspace.Path, settings).On("info", _ => throw new TimeoutException("p4 info did not finish within 1 s"))),
			NullLogger.Instance,
			Token);

		using IVersionControl _ = detection.VersionControl;
		Assert.AreEqual(VcsKind.Perforce, detection.VersionControl.Kind);
		StringAssert.Contains(detection.Provenance, "did not finish within 1 s");
		StringAssert.Contains(await detection.VersionControl.DescribeAsync(Token), "unreachable: p4 info did not finish");
	}

	[TestMethod]
	public async Task GlobalP4SettingsAloneAreNotAWorkspace_UnlessForced()
	{
		using TempDirectory workspace = new();
		Dictionary<string, string> environment = new() { ["P4PORT"] = "perforce.example.com:1666", ["P4CLIENT"] = "user-ws", ["P4CONFIG"] = ".p4config" };

		// No P4CONFIG file in the tree: p4 is not asked (Isolated fails the test if it is), and the reason fits on one line.
		VcsDetection detected = await VersionControlDetector.DetectAsync(workspace.Directory, Isolated(environment), NullLogger.Instance, Token);
		Assert.AreEqual(VcsKind.None, detected.VersionControl.Kind);
		StringAssert.Contains(detected.Provenance, "global P4PORT/P4CLIENT settings alone are not used");
		StringAssert.Contains(detected.Provenance, "-vcs=p4");
		Assert.DoesNotContain("\n", detected.Provenance);

		// -vcs=p4 still asks, and uses the client's root when it holds the directory.
		VcsDetection forced = await VersionControlDetector.DetectAsync(workspace.Directory, Isolated(environment, settings => new FakePerforceConnection(workspace.Path, settings).On("info", "info"), kind: "p4"), NullLogger.Instance, Token);
		using IVersionControl _ = forced.VersionControl;
		Assert.AreEqual(VcsKind.Perforce, forced.VersionControl.Kind);
		StringAssert.StartsWith(forced.Provenance, "-vcs= argument, p4 info");
	}

	[TestMethod]
	public async Task P4ConfigSetButNoFileFoundDoesNotRunP4()
	{
		using TempDirectory directory = new();
		VcsDetection detection = await VersionControlDetector.DetectAsync(directory.Directory, Isolated(new() { ["P4CONFIG"] = ".p4config" }), NullLogger.Instance, Token);

		Assert.AreEqual(VcsKind.None, detection.VersionControl.Kind);
	}

	[TestMethod]
	public async Task ArgumentOverrideWinsOverEnvironmentAndDetection()
	{
		using TempDirectory repo = new();
		System.IO.Directory.CreateDirectory(repo.Combine(".git"));

		VcsDetection none = await VersionControlDetector.DetectAsync(repo.Directory, Isolated(kind: "NONE", environmentOverride: "git"), NullLogger.Instance, Token);
		Assert.AreEqual(VcsKind.None, none.VersionControl.Kind);
		Assert.AreEqual("-vcs= argument", none.Provenance);

		VcsDetection fromEnvironment = await VersionControlDetector.DetectAsync(repo.Directory, Isolated(environmentOverride: "none"), NullLogger.Instance, Token);
		Assert.AreEqual(VcsKind.None, fromEnvironment.VersionControl.Kind);
		StringAssert.Contains(fromEnvironment.Provenance, "UAK_VCS");

		VcsDetection git = await VersionControlDetector.DetectAsync(repo.Directory, Isolated(kind: "git"), NullLogger.Instance, Token);
		Assert.AreEqual(VcsKind.Git, git.VersionControl.Kind);
	}

	[TestMethod]
	public async Task ForcedKindsThatDoNotFitAreErrors()
	{
		using TempDirectory directory = new();

		await Assert.ThrowsExactlyAsync<VcsException>(() => VersionControlDetector.DetectAsync(directory.Directory, Isolated(kind: "svn"), NullLogger.Instance, Token));
		await Assert.ThrowsExactlyAsync<VcsException>(() => VersionControlDetector.DetectAsync(directory.Directory, Isolated(kind: "git"), NullLogger.Instance, Token));
	}

	[TestMethod]
	public async Task ForcedPerforceWithoutSettingsAsksP4()
	{
		using TempDirectory directory = new();
		VcsDetection detection = await VersionControlDetector.DetectAsync(
			directory.Directory,
			Isolated(connection: settings => new FakePerforceConnection(directory.Path, settings).On("info", "info"), kind: "p4"),
			NullLogger.Instance,
			Token);

		using IVersionControl _ = detection.VersionControl;
		Assert.AreEqual(VcsKind.Perforce, detection.VersionControl.Kind);
		StringAssert.StartsWith(detection.Provenance, "-vcs= argument, p4 info");
	}

	[TestMethod]
	[DataRow("git", VcsKind.Git)]
	[DataRow(" Perforce ", VcsKind.Perforce)]
	[DataRow("P4", VcsKind.Perforce)]
	[DataRow("none", VcsKind.None)]
	public void ParsesKinds(string value, VcsKind expected)
	{
		Assert.AreEqual(expected, VersionControlDetector.ParseKind(value));
	}

	[TestMethod]
	public void P4ConfigFilesNearerTheDirectoryWin()
	{
		using TempDirectory workspace = new();
		workspace.Write(".p4config", "P4PORT=outer:1666\nP4CLIENT=outer-ws\n# a comment=ignored\n");
		workspace.Write("Inner/.p4config", "P4CLIENT = inner-ws\n");
		System.IO.Directory.CreateDirectory(workspace.Combine("Inner/Game"));

		PerforceWorkspaceEnvironment environment = new(new DirectoryInfo(workspace.Combine("Inner/Game")), new FixedPerforceEnvironment(new Dictionary<string, string> { ["P4CONFIG"] = ".p4config", ["P4USER"] = "user" }));

		Assert.AreEqual(workspace.Combine("Inner/.p4config"), environment.ConfigFile!.FullName);
		Assert.AreEqual("inner-ws", environment.GetValue("P4CLIENT"));
		Assert.AreEqual("outer:1666", environment.GetValue("P4PORT"));
		Assert.AreEqual("user", environment.GetValue("p4user"));
		Assert.IsNull(environment.GetValue("# a comment"));
	}
}
