// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal.Tests;

[TestClass]
public sealed class CommandLineTests
{
	[TestMethod]
	[DataRow("-ExecCmds=Automation RunTests Game.;Quit", "-ExecCmds=\"Automation RunTests Game.;Quit\"")]
	[DataRow("-testexit=Automation Test Queue Empty", "-testexit=\"Automation Test Queue Empty\"")]
	[DataRow(@"-Project=C:\My Games\Game.uproject", @"-Project=""C:\My Games\Game.uproject""")]
	[DataRow(@"C:\My Games\Game.uproject", @"""C:\My Games\Game.uproject""")]
	[DataRow(@"-abslog=C:\Logs\x.log", @"-abslog=C:\Logs\x.log")]
	[DataRow(@"-Dir=C:\My Dir\", @"-Dir=""C:\My Dir\\""")]
	[DataRow("say \"hi\"", "\"say \\\"hi\\\"\"")]
	[DataRow("", "\"\"")]
	public void WindowsArgumentsAreQuotedAsUnrealExpects(string argument, string expected)
	{
		Assert.AreEqual(expected, ProcessInvocation.QuoteForUnreal(argument));
	}

	[TestMethod]
	public void InstalledEngineRunsUbtOnTheBundledDotNet()
	{
		using Sandbox Box = new(UnrealPlatform.Win64);
		ProcessInvocation Invocation = UbtCommandLine.Build(Box.Layout, new UbtTarget("GameEditor", UnrealPlatform.Win64, "Development", Box.ProjectFile, 3),
			["-SingleFile=" + Box.SourceFile]);

		Assert.AreEqual(Box.Layout.DotNetExecutable, Invocation.FileName);
		Assert.EndsWith("dotnet.exe", Invocation.FileName);
		CollectionAssert.AreEqual(new[]
		{
			Box.Layout.UnrealBuildToolAssembly, "GameEditor", "Win64", "Development", "-Project=" + Box.ProjectFile,
			"-WaitMutex", "-NoHotReloadFromIDE", "-MaxParallelActions=3", "-SingleFile=" + Box.SourceFile,
		}, Invocation.Arguments.ToArray());
		Assert.AreEqual(Path.Combine(Box.Layout.EngineDirectory, "Source"), Invocation.WorkingDirectory);
		Assert.AreEqual(Path.GetDirectoryName(Invocation.FileName), Invocation.Environment!["DOTNET_ROOT"]);
		Assert.AreEqual("0", Invocation.Environment["DOTNET_MULTILEVEL_LOOKUP"]);
	}

	[TestMethod]
	public void LinuxCommandLinesUseLinuxPaths()
	{
		using Sandbox Box = new(UnrealPlatform.Linux);
		ProcessInvocation Invocation = UbtCommandLine.Build(Box.Layout, new UbtTarget("GameEditor", UnrealPlatform.Linux, "Development", Box.ProjectFile, null));

		Assert.AreEqual("dotnet", Path.GetFileName(Invocation.FileName));
		Assert.Contains(Path.Combine("DotNet", "10.0", UnrealPlatform.Linux.DotNetRid), Invocation.FileName);
		Assert.AreEqual("Linux", Invocation.Arguments[2]);
		Assert.DoesNotContain("-MaxParallelActions=", string.Join(' ', Invocation.Arguments));

		ProcessInvocation Editor = EditorTestCommandLine.Build(Box.Layout, Box.ProjectFile, "Game.", gpu: false, "/tmp/x.log", "/tmp/report");
		Assert.AreEqual(Path.Combine(Box.Layout.EngineDirectory, "Binaries", "Linux", "UnrealEditor"), Editor.FileName);
	}

	[TestMethod]
	public void SourceEngineRunsTheBuildScript()
	{
		using Sandbox Box = new(UnrealPlatform.Win64, installed: false);
		ProcessInvocation Invocation = UbtCommandLine.Build(Box.Layout, new UbtTarget("GameEditor", UnrealPlatform.Win64, "Debug", null, null));

		Assert.AreEqual(Path.Combine(Box.Layout.EngineDirectory, "Build", "BatchFiles", "Build.bat"), Invocation.FileName);
		CollectionAssert.AreEqual(new[] { "GameEditor", "Win64", "Debug", "-WaitMutex", "-NoHotReloadFromIDE" }, Invocation.Arguments.ToArray());

		using Sandbox Linux = new(UnrealPlatform.Linux, installed: false);
		Assert.AreEqual(Path.Combine(Linux.Layout.EngineDirectory, "Build", "BatchFiles", "Linux", "Build.sh"),
			UbtCommandLine.Build(Linux.Layout, new UbtTarget("GameEditor", UnrealPlatform.Linux, "Development", null, null)).FileName);
	}

	[TestMethod]
	public void MissingDotNetIsASetupError()
	{
		using Sandbox Box = new(UnrealPlatform.Win64);
		Directory.Delete(Box.Layout.DotNetRootDirectory, recursive: true);

		Assert.ThrowsExactly<UakSetupException>(() => UbtCommandLine.Build(Box.Layout, new UbtTarget("GameEditor", UnrealPlatform.Win64, "Development", null, null)));
	}

	[TestMethod]
	public void EditorTestRunIsHeadlessAndQuits()
	{
		using Sandbox Box = new(UnrealPlatform.Win64);
		string Log = Path.Combine(Box.Root, "Logs", "t.log");
		string Report = Path.Combine(Box.Root, "Report");
		ProcessInvocation Invocation = EditorTestCommandLine.Build(Box.Layout, Box.ProjectFile, "Game.Area+Game.Other", gpu: false, Log, Report);

		Assert.AreEqual(Box.Layout.EditorCommandExecutable, Invocation.FileName);
		Assert.EndsWith("UnrealEditor-Cmd.exe", Invocation.FileName);
		CollectionAssert.AreEqual(new[]
		{
			Box.ProjectFile, "-ExecCmds=Automation RunTests Game.Area+Game.Other;Quit", "-unattended", "-nullrhi", "-nosplash", "-nopause",
			"-NoSound", "-abslog=" + Log, "-ReportExportPath=" + Report, "-testexit=Automation Test Queue Empty",
		}, Invocation.Arguments.ToArray());
		Assert.Contains("-ExecCmds=\"Automation RunTests Game.Area+Game.Other;Quit\"", Invocation.ToWindowsCommandLine());

		ProcessInvocation Gpu = EditorTestCommandLine.Build(Box.Layout, Box.ProjectFile, "Game.", gpu: true, Log, Report);
		Assert.Contains("-RenderOffscreen", Gpu.Arguments);
		Assert.DoesNotContain("-nullrhi", Gpu.Arguments);
	}

	[TestMethod]
	public void EditorTestRunUsesAUniqueEditorFromItsReceipt()
	{
		// A unique build environment: UBT names the editor after its target and puts it in the project's Binaries.
		using Sandbox Box = new(UnrealPlatform.Win64);
		string Binaries = Path.Combine(Box.ProjectDirectory, "Binaries", "Win64");
		string Cmd = Sandbox.Write(Path.Combine(Binaries, "GameEditor-Cmd.exe"), "");
		Sandbox.Write(Path.Combine(Binaries, "GameEditor.target"),
			"""{ "TargetName": "GameEditor", "TargetType": "Editor", "TargetBuildEnvironment": "Unique", "Launch": "$(ProjectDir)/Binaries/Win64/GameEditor.exe", "LaunchCmd": "$(ProjectDir)/Binaries/Win64/GameEditor-Cmd.exe" }""");

		ProcessInvocation Invocation = EditorTestCommandLine.Build(Box.Layout, Box.ProjectFile, "Game.", gpu: false, "x.log", "r");
		Assert.AreEqual(Cmd, Invocation.FileName);

		// A windowed run takes the receipt's Launch, the editor itself.
		ProcessInvocation Windowed = EditorTestCommandLine.Build(EditorLocator.Locate(Box.Layout, Box.ProjectFile), Box.ProjectFile, "Game.", EditorRendering.Windowed, "x.log", "r");
		Assert.AreEqual(Path.Combine(Binaries, "GameEditor.exe"), Windowed.FileName);
	}

	[TestMethod]
	public void WindowedTestRunUsesTheEditorItselfInAWindow()
	{
		using Sandbox Box = new(UnrealPlatform.Win64);
		string Log = Path.Combine(Box.Root, "Logs", "t.log");
		string Report = Path.Combine(Box.Root, "Report");
		EditorLocation Editor = EditorLocator.Locate(Box.Layout, Box.ProjectFile);
		ProcessInvocation Invocation = EditorTestCommandLine.Build(Editor, Box.ProjectFile, "Game.", EditorRendering.Windowed, Log, Report, ["-SCCProvider=None", "-Custom"], 1280, 720);

		Assert.AreEqual(Box.Layout.EditorExecutable, Invocation.FileName);
		Assert.EndsWith("UnrealEditor.exe", Invocation.FileName);
		CollectionAssert.AreEqual(new[]
		{
			Box.ProjectFile, "-ExecCmds=Automation RunTests Game.;Quit", "-unattended", "-windowed", "-ResX=1280", "-ResY=720", "-nosplash", "-nopause",
			"-NoSound", "-abslog=" + Log, "-ReportExportPath=" + Report, "-testexit=Automation Test Queue Empty", "-SCCProvider=None", "-Custom",
		}, Invocation.Arguments.ToArray());

		ProcessInvocation Default = EditorTestCommandLine.Build(Editor, Box.ProjectFile, "Game.", EditorRendering.Windowed, Log, Report);
		CollectionAssert.IsSubsetOf(new[] { "-windowed", "-ResX=1600", "-ResY=900" }, Default.Arguments.ToArray());
		ProcessInvocation Headless = EditorTestCommandLine.Build(Editor, Box.ProjectFile, "Game.", EditorRendering.NullRhi, Log, Report, ["-SCCProvider=None"]);
		Assert.AreEqual(Box.Layout.EditorCommandExecutable, Headless.FileName);
		Assert.AreEqual("-SCCProvider=None", Headless.Arguments[^1]);
		Assert.DoesNotContain("-windowed", Headless.Arguments);
	}

	[TestMethod]
	[DataRow("-ExecCmds=Quit")]
	[DataRow("-testexit=Something")]
	[DataRow("-ABSLOG=C:/x.log")]
	[DataRow("--ReportExportPath=C:/r")]
	[DataRow("/abslog=C:/x.log")]
	[DataRow("-nullrhi")]
	[DataRow("-RenderOffscreen")]
	[DataRow("-windowed")]
	[DataRow("-ResX=800")]
	[DataRow("-resy=600")]
	public void EditorPassThroughRejectsWhatUakSets(string argument)
	{
		Assert.ThrowsExactly<UakUsageException>(() => EditorTestCommandLine.CheckPassThrough(["-SCCProvider=None", argument]));
	}

	[TestMethod]
	public void EditorPassThroughKeepsEverythingElseAsGiven()
	{
		string[] Arguments = ["-SCCProvider=None", "-GLIPPCGSandbox", "/Game/Maps/Test", "-log", "-ini:Engine:[Core.Log]:LogTemp=Verbose", "-ExecCmdsLater"];
		CollectionAssert.AreEqual(Arguments, EditorTestCommandLine.CheckPassThrough(Arguments).ToArray());
	}

	[TestMethod]
	public void UbtPassThroughGoesLastAndAsGiven()
	{
		using Sandbox Box = new(UnrealPlatform.Win64);
		IReadOnlyList<string> PassThrough = UbtCommandLine.CheckPassThrough(["-DisableAdaptiveUnity", "-Module=Foo", "-Module=Bar"]);
		ProcessInvocation Invocation = UbtCommandLine.Build(Box.Layout, new UbtTarget("Game", UnrealPlatform.Win64, "Development", Box.ProjectFile, null), PassThrough);

		CollectionAssert.AreEqual(new[]
		{
			Box.Layout.UnrealBuildToolAssembly, "Game", "Win64", "Development", "-Project=" + Box.ProjectFile, "-WaitMutex", "-NoHotReloadFromIDE",
			"-DisableAdaptiveUnity", "-Module=Foo", "-Module=Bar",
		}, Invocation.Arguments.ToArray());
	}

	[TestMethod]
	[DataRow("-WaitMutex")]
	[DataRow("-NoMutex")]
	[DataRow("-project=C:/Other/Other.uproject")]
	[DataRow("--Target=Other Win64 Development")]
	[DataRow("-TargetList=targets.txt")]
	[DataRow("-Mode=JsonExport")]
	[DataRow("-NoHotReloadFromIDE")]
	[DataRow("-ForceHotReload")]
	[DataRow("-LiveCoding")]
	[DataRow("-MaxParallelActions=32")]
	[DataRow("OtherEditor")]
	[DataRow("Shipping")]
	[DataRow("@arguments.txt")]
	[DataRow("-")]
	public void UbtPassThroughRejectsWhatUakSetsAndBareWords(string argument)
	{
		Assert.ThrowsExactly<UakUsageException>(() => UbtCommandLine.CheckPassThrough(["-DisableAdaptiveUnity", argument]));
	}

	[TestMethod]
	public void UbtPassThroughRejectsWhatTheCommandAlsoSets()
	{
		CollectionAssert.AreEqual(new[] { "-SingleFile=Foo.cpp" }, UbtCommandLine.CheckPassThrough(["-SingleFile=Foo.cpp"]).ToArray());
		Assert.ThrowsExactly<UakUsageException>(() => UbtCommandLine.CheckPassThrough(["-singlefile=Foo.cpp"], "SingleFile", "SingleFileBuildDependents"));
		Assert.ThrowsExactly<UakUsageException>(() => UbtCommandLine.CheckPassThrough(["-SingleFileBuildDependents"], "SingleFile", "SingleFileBuildDependents"));
	}

	/// <summary>cmd.exe's command line limit. Build.bat runs through cmd.exe.</summary>
	const int CmdLineLimit = 8191;

	/// <summary>400 project files with long paths: far more than one cmd.exe line holds as -SingleFile= options.</summary>
	static string[] ManyFiles(Sandbox box) => Enumerable.Range(0, 400)
		.Select(Index => Path.Combine(box.ProjectDirectory, "Source", "Game", "Private", "Gameplay Systems", $"SomeQuiteLongGameplayComponentName{Index:D3}.cpp"))
		.ToArray();

	[TestMethod]
	public void ManyFilesDoNotFitAsSingleFileOptions()
	{
		// The bug, from a real run: about 150 -SingleFile= options made Build.bat fail with "The command line is too long".
		using Sandbox Box = new(UnrealPlatform.Win64, installed: false);
		ProcessInvocation Old = UbtCommandLine.Build(Box.Layout, new UbtTarget("GameEditor", UnrealPlatform.Win64, "Development", Box.ProjectFile, null),
			ManyFiles(Box).Select(File => "-SingleFile=" + File));

		Assert.IsGreaterThan(CmdLineLimit, ProcessRunner.GetWindowsCommand(Old).CommandLine.Length);
	}

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public void ManyFilesGoToUbtInAListFileAndTheCommandLineStaysShort(bool dependents)
	{
		using Sandbox Box = new(UnrealPlatform.Win64, installed: false);
		string[] Files = ManyFiles(Box);
		string ListFile = Path.Combine(Box.StateDirectory, "Logs", "compile-20261002-010203-42.files.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(ListFile)!);

		UbtCommandLine.WriteFileList(ListFile, Files);
		ProcessInvocation Invocation = UbtCommandLine.Build(Box.Layout, new UbtTarget("GameEditor", UnrealPlatform.Win64, "Development", Box.ProjectFile, null),
			[.. UbtCommandLine.SingleFileArguments(ListFile, dependents), "-DisableAdaptiveUnity"]);

		// Build.bat, through cmd.exe: the line names the list, not the files, so it is as short for 400 files as for one.
		Assert.AreEqual(Box.Layout.BuildScript, Invocation.FileName);
		string CommandLine = ProcessRunner.GetWindowsCommand(Invocation).CommandLine;
		Assert.IsLessThan(1024, CommandLine.Length, CommandLine);
		string[] Expected = dependents
			? ["-FileList=" + ListFile, "-SingleFileBuildDependents", "-DisableAdaptiveUnity"]
			: ["-FileList=" + ListFile, "-DisableAdaptiveUnity"];
		CollectionAssert.AreEqual(Expected, Invocation.Arguments.Skip(Invocation.Arguments.Count - Expected.Length).ToArray());
		Assert.IsFalse(Invocation.Arguments.Any(Argument => Argument.StartsWith("-SingleFile=", StringComparison.OrdinalIgnoreCase)));

		// What UBT's TargetDescriptor reads: each non-blank line is one file, a rooted path kept as it is.
		CollectionAssert.AreEqual(Files, File.ReadAllLines(ListFile));
		byte[] Bytes = File.ReadAllBytes(ListFile);
		Assert.IsFalse(Bytes.Length >= 3 && Bytes[0] == 0xEF && Bytes[1] == 0xBB && Bytes[2] == 0xBF, "no byte order mark");
	}

	[TestMethod]
	public void TheListFileArgumentIsAFullPath()
	{
		Assert.AreEqual("-FileList=" + Path.GetFullPath("compile.files.txt"), UbtCommandLine.SingleFileArguments("compile.files.txt", dependents: false).Single());
	}

	[TestMethod]
	[DataRow("Foo.cpp")]
	[DataRow("Source/Game/Foo.cpp")]
	[DataRow("C:/Game/Foo.cpp\nC:/Game/Bar.cpp")]
	[DataRow("/Game/Foo.cpp\r")]
	public void TheListFileTakesOnlyFullPathsOnOneLine(string file)
	{
		using Sandbox Box = new();
		string ListFile = Path.Combine(Box.Root, "files.txt");
		Assert.ThrowsExactly<UakSetupException>(() => UbtCommandLine.WriteFileList(ListFile, [Box.SourceFile, file]));
	}

	[TestMethod]
	[DataRow("Game;Quit")]
	[DataRow("Game Area")]
	[DataRow("Game\"")]
	[DataRow("Game.A,Quit")]
	[DataRow("Game.A'")]
	[DataRow("Game.A|Quit")]
	[DataRow("Game.A`")]
	[DataRow("Game\tA")]
	[DataRow("")]
	public void FiltersThatWouldBreakTheCommandAreRejected(string filter)
	{
		using Sandbox Box = new();
		Assert.ThrowsExactly<UakUsageException>(() => EditorTestCommandLine.Build(Box.Layout, Box.ProjectFile, filter, false, "x.log", "r"));
	}
}
