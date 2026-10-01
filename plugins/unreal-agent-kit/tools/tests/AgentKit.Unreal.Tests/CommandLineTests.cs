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
