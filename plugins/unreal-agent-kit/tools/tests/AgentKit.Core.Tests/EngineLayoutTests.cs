// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Runtime.InteropServices;

namespace AgentKit.Core.Tests;

[TestClass]
public sealed class UnrealPlatformTests
{
	[TestMethod]
	public void Names_AndSuffixes()
	{
		Assert.AreEqual("Win64", UnrealPlatform.Win64.Name);
		Assert.AreEqual(".exe", UnrealPlatform.Win64.ExecutableSuffix);
		Assert.AreEqual(".bat", UnrealPlatform.Win64.ScriptSuffix);
		Assert.AreEqual("UnrealEditor-Cmd.exe", UnrealPlatform.Win64.ExecutableName("UnrealEditor-Cmd"));
		Assert.AreEqual("Build.bat", UnrealPlatform.Win64.ScriptName("Build"));
		foreach (UnrealPlatform Unix in new[] { UnrealPlatform.Linux, UnrealPlatform.LinuxArm64, UnrealPlatform.Mac })
		{
			Assert.AreEqual("", Unix.ExecutableSuffix);
			Assert.AreEqual(".sh", Unix.ScriptSuffix);
			Assert.IsFalse(Unix.IsWindows);
		}
		Assert.AreEqual("Linux", UnrealPlatform.LinuxArm64.BatchFilesFolder);
		Assert.AreEqual("", UnrealPlatform.Win64.BatchFilesFolder);
	}

	[TestMethod]
	[DataRow("Win64", Architecture.X64, "win-x64")]
	[DataRow("Win64", Architecture.Arm64, "win-arm64")]
	[DataRow("Linux", Architecture.X64, "linux-x64")]
	[DataRow("LinuxArm64", Architecture.Arm64, "linux-arm64")]
	[DataRow("Mac", Architecture.X64, "mac-x64")]
	[DataRow("Mac", Architecture.Arm64, "mac-arm64")]
	public void DotNetRid_MatchesTheEnginesSdkFolders(string platform, Architecture architecture, string expected) =>
		Assert.AreEqual(expected, UnrealPlatform.FromName(platform)!.GetDotNetRid(architecture));

	[TestMethod]
	public void FromName_IsCaseInsensitive_AndKnowsHost()
	{
		Assert.AreSame(UnrealPlatform.Win64, UnrealPlatform.FromName("win64"));
		Assert.AreSame(UnrealPlatform.Host, UnrealPlatform.FromName("HOST"));
		Assert.IsNull(UnrealPlatform.FromName("PS5"));
	}

	[TestMethod]
	public void Host_MatchesTheOperatingSystem() =>
		Assert.AreEqual(OperatingSystem.IsWindows(), UnrealPlatform.Host.IsWindows);
}

[TestClass]
public sealed class EngineLayoutTests
{
	[TestMethod]
	public void EngineLocator_NormalizesRootAndEngineFolder()
	{
		using TempTree Tree = new();
		string Root = Tree.Engine("UE");
		Assert.AreEqual(Root, EngineLocator.NormalizeEngineRoot(Root));
		Assert.AreEqual(Root, EngineLocator.NormalizeEngineRoot(Root + Path.DirectorySeparatorChar));
		Assert.AreEqual(Root, EngineLocator.NormalizeEngineRoot(Path.Combine(Root, "Engine")));
		Assert.IsNull(EngineLocator.NormalizeEngineRoot(Tree.Dir("NotAnEngine")));
		Assert.IsNull(EngineLocator.NormalizeEngineRoot(""));
	}

	[TestMethod]
	public void EngineLocator_FindsTheContainingEngine()
	{
		using TempTree Tree = new();
		string Root = Tree.Engine("UE");
		Assert.AreEqual(Root, EngineLocator.FindContainingEngineRoot(Tree.Dir("UE", "Games", "Game", "Source")));
		Assert.AreEqual(Root, EngineLocator.FindContainingEngineRoot(Root));
		Assert.IsNull(EngineLocator.FindContainingEngineRoot(Tree.Dir("Elsewhere")));
	}

	[TestMethod]
	public void Paths_OnWindows()
	{
		using TempTree Tree = new();
		string Root = Tree.Engine("UE");
		EngineLayout Layout = new(Path.Combine(Root, "Engine"), UnrealPlatform.Win64);
		string Engine = Path.Combine(Root, "Engine");
		Assert.AreEqual(Root, Layout.RootDirectory);
		Assert.AreEqual(Engine, Layout.EngineDirectory);
		Assert.AreEqual(Path.Combine(Engine, "Build", "BatchFiles", "Build.bat"), Layout.BuildScript);
		Assert.AreEqual(Path.Combine(Engine, "Build", "BatchFiles", "RunUAT.bat"), Layout.RunUatScript);
		Assert.AreEqual(Path.Combine(Engine, "Binaries", "Win64", "UnrealEditor-Cmd.exe"), Layout.EditorCommandExecutable);
		Assert.AreEqual(Path.Combine(Engine, "Binaries", "Win64", "UnrealEditor.exe"), Layout.EditorExecutable);
		Assert.AreEqual(Path.Combine(Engine, "Binaries", "DotNET", "UnrealBuildTool", "UnrealBuildTool.dll"), Layout.UnrealBuildToolAssembly);
	}

	[TestMethod]
	public void Paths_OnLinuxAndMac()
	{
		using TempTree Tree = new();
		string Root = Tree.Engine("UE");
		string Engine = Path.Combine(Root, "Engine");
		EngineLayout Linux = new(Root, UnrealPlatform.Linux);
		Assert.AreEqual(Path.Combine(Engine, "Build", "BatchFiles", "Linux", "Build.sh"), Linux.BuildScript);
		Assert.AreEqual(Path.Combine(Engine, "Build", "BatchFiles", "RunUAT.sh"), Linux.RunUatScript);
		Assert.AreEqual(Path.Combine(Engine, "Binaries", "Linux", "UnrealEditor"), Linux.EditorCommandExecutable);

		EngineLayout LinuxArm = new(Root, UnrealPlatform.LinuxArm64);
		Assert.AreEqual(Path.Combine(Engine, "Build", "BatchFiles", "Linux", "Build.sh"), LinuxArm.BuildScript);
		Assert.AreEqual(Path.Combine(Engine, "Binaries", "LinuxArm64", "UnrealEditor"), LinuxArm.EditorExecutable);

		EngineLayout Mac = new(Root, UnrealPlatform.Mac);
		Assert.AreEqual(Path.Combine(Engine, "Build", "BatchFiles", "Mac", "Build.sh"), Mac.BuildScript);
		Assert.AreEqual(Path.Combine(Engine, "Binaries", "Mac", "UnrealEditor.app", "Contents", "MacOS", "UnrealEditor"), Mac.EditorCommandExecutable);
	}

	[TestMethod]
	public void DotNet_PicksTheHighestVersionWithThisPlatformsSdk()
	{
		using TempTree Tree = new();
		string Root = Tree.Engine("UE");
		UnrealPlatform Platform = UnrealPlatform.Host;
		string Rid = Platform.DotNetRid;
		string DotNet = Platform.ExecutableName("dotnet");
		string DotNetRoot = Path.Combine("UE", "Engine", "Binaries", "ThirdParty", "DotNet");
		Tree.File(Path.Combine(DotNetRoot, "8.0", Rid, DotNet));
		Tree.File(Path.Combine(DotNetRoot, "10.0", Rid, DotNet));
		Tree.File(Path.Combine(DotNetRoot, "11.0", "some-other-rid", DotNet));
		Tree.Dir(DotNetRoot, "not-a-version");

		EngineLayout Layout = new(Root, Platform);
		Assert.AreEqual(Tree.Path(DotNetRoot, "10.0", Rid, DotNet), Layout.DotNetExecutable);

		IReadOnlyDictionary<string, string?> Environment = Layout.GetDotNetEnvironment();
		Assert.AreEqual(Tree.Path(DotNetRoot, "10.0", Rid), Environment["DOTNET_ROOT"]);
		Assert.AreEqual("0", Environment["DOTNET_MULTILEVEL_LOOKUP"]);
		Assert.AreEqual("LatestMajor", Environment["DOTNET_ROLL_FORWARD"]);
		StringAssert.StartsWith(Environment["PATH"], Tree.Path(DotNetRoot, "10.0", Rid));

		ProcessInvocation Ubt = Layout.GetUnrealBuildToolInvocation(["-Mode=QueryTargets"]);
		Assert.AreEqual(Layout.DotNetExecutable, Ubt.FileName);
		CollectionAssert.AreEqual(new[] { Layout.UnrealBuildToolAssembly, "-Mode=QueryTargets" }, Ubt.Arguments.ToArray());
		Assert.AreEqual("0", Ubt.Environment!["DOTNET_MULTILEVEL_LOOKUP"]);
	}

	[TestMethod]
	public void DotNet_None_GivesNullAndASetupErrorForUbt()
	{
		using TempTree Tree = new();
		EngineLayout Layout = new(Tree.Engine("UE"));
		Assert.IsNull(Layout.DotNetExecutable);
		Assert.IsEmpty(Layout.GetDotNetEnvironment());
		Assert.ThrowsExactly<UakSetupException>(() => Layout.GetUnrealBuildToolInvocation([]));
	}

	[TestMethod]
	public void Version_AndInstalledBuild()
	{
		using TempTree Tree = new();
		string Root = Tree.Engine("UE", minor: 7);
		EngineLayout Layout = new(Root);
		Assert.AreEqual(new EngineVersion(5, 7, 1, 123, "++UE5+Release-5.7"), Layout.ReadVersion());
		Assert.AreEqual("5.7.1-123+++UE5+Release-5.7", Layout.ReadVersion()!.ToString());
		Assert.IsFalse(Layout.IsInstalledBuild);
		Tree.File(Path.Combine("UE", "Engine", "Build", "InstalledBuild.txt"), "");
		Assert.IsTrue(Layout.IsInstalledBuild);

		File.WriteAllText(Layout.BuildVersionFile, "{ broken");
		Assert.IsNull(Layout.ReadVersion());
	}
}

//// <summary>
/// Against a real engine and project on this machine. The project comes from UAK_TEST_PROJECT (a .uproject, or a directory
/// holding one) and its engine is found as uak finds it; the bundled dotnet test uses UAK_TEST_ENGINE, else the default UE 5.8
/// install. Each test is inconclusive when what it needs is missing.
/// </summary>
[TestClass]
public sealed class RealEngineIntegrationTests
{
	const string DefaultEngineRoot = @"C:\Program Files\Epic Games\UE_5.8";

	static string EngineRoot => Environment.GetEnvironmentVariable("UAK_TEST_ENGINE") is { Length: > 0 } Engine ? Engine : DefaultEngineRoot;

	[TestMethod]
	public void ResolvesTheTestProjectToItsEngine()
	{
		string? ProjectArgument = Environment.GetEnvironmentVariable("UAK_TEST_PROJECT");
		if (string.IsNullOrWhiteSpace(ProjectArgument))
		{
			Assert.Inconclusive("Set UAK_TEST_PROJECT to a .uproject whose engine is installed to run this test.");
			return;
		}

		using TempTree Tree = new();
		UakContext Context = UakContextResolver.Resolve(new UakResolveOptions
		{
			ProjectArgument = ProjectArgument,
			CurrentDirectory = Tree.Root,
			GetEnvironmentVariable = new FakeEnvironment().Get,
			// Keep the real project's Saved folder untouched.
			IsWritable = Path => Path.StartsWith(Tree.Root, StringComparison.OrdinalIgnoreCase) && Directory.CreateDirectory(Path).Exists,
			UserHomeDirectory = Tree.Dir("home"),
		});

		Assert.IsNotNull(Context.ProjectFile);
		Assert.IsNotNull(Context.EngineRoot, Context.Provenance[UakContextResolver.EngineKey]);
		EngineLayout Engine = Context.RequireEngine();
		Assert.AreEqual(5, Engine.ReadVersion()!.Major);
		Assert.IsTrue(File.Exists(Engine.DotNetExecutable), Engine.DotNetExecutable);
		Assert.IsTrue(File.Exists(Engine.UnrealBuildToolAssembly), Engine.UnrealBuildToolAssembly);
		Assert.IsTrue(File.Exists(Engine.BuildScript), Engine.BuildScript);
		Assert.IsTrue(File.Exists(Engine.EditorCommandExecutable), Engine.EditorCommandExecutable);
		StringAssert.StartsWith(Context.StateDirectory.FullName, Tree.Root);
	}

	[TestMethod]
	public async Task BundledDotNetRuns()
	{
		if (!EngineLocator.IsEngineRoot(EngineRoot))
		{
			Assert.Inconclusive($"There is no engine at {EngineRoot}; set UAK_TEST_ENGINE to one.");
		}
		EngineLayout Engine = new(EngineRoot);
		ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(
			new ProcessInvocation(Engine.DotNetExecutable!, ["--version"], Environment: Engine.GetDotNetEnvironment()), CancellationToken.None);
		Assert.AreEqual(0, Result.ExitCode, Result.StandardError);
		Assert.IsTrue(Version.TryParse(Result.StandardOutput.Trim().Split('-')[0], out _), Result.StandardOutput);
	}
}
