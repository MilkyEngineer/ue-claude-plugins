// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;

namespace AgentKit.Unreal.Tests;

[TestClass]
public sealed class CookedTestTests
{
	static UnrealServices Services(FakeProcessRunner runner, FakeLock? lockProvider = null) => new()
	{
		Runner = runner,
		Lock = lockProvider ?? new FakeLock(),
		HostPlatform = UnrealPlatform.Win64,
		GetEnvironmentVariable = _ => null,
		CaptureTree = TestSupportTrees.None,
	};

	/// <summary>A sandbox with the engine's RunUAT script.</summary>
	static Sandbox WithRunUat(UnrealPlatform? platform = null)
	{
		Sandbox Box = new(platform);
		Sandbox.Write(Box.Layout.RunUatScript, "");
		return Box;
	}

	static string LogFolder(Sandbox box, string name = "uak-test-cooked") => Path.Combine(box.ProjectDirectory, "Saved", "Logs", name);

	/// <summary>UAT as the fake runs it: writes the fixture as Client.log in its log folder, prints the lines, exits with the code.</summary>
	static Func<ProcessInvocation, Action<string>, int> RunsGame(string? fixture, int exitCode, params string[] uatLines) => (Invocation, OnLine) =>
	{
		foreach (string Line in uatLines)
		{
			OnLine(Line);
		}
		if (fixture is not null)
		{
			string Folder = Invocation.Environment![CookedTestCommandLine.LogFolderVariable]!;
			File.WriteAllLines(CookedTestCommandLine.ClientLogFile(Folder), Fixtures.Lines(fixture));
		}
		return exitCode;
	};

	static readonly string[] RunOutput =
	[
		"********** STAGE COMMAND STARTED **********",
		"Creating pak using staging manifest.",
		"********** STAGE COMMAND COMPLETED **********",
		"********** RUN COMMAND STARTED **********",
		"Client exited with error code: 0 (see the client log for more info)",
		"********** RUN COMMAND COMPLETED **********",
		"AutomationTool exiting with ExitCode=0 (Success)",
	];

	// ---- the UAT command line ----

	[TestMethod]
	public void TheCommandBuildsCooksStagesAndRunsTheGame()
	{
		using Sandbox Box = WithRunUat();
		CookedTestRun Run = new() { ProjectFile = Box.ProjectFile, Filter = "Project.Math", Platform = UnrealPlatform.Win64, LogFolder = LogFolder(Box) };

		ProcessInvocation Invocation = CookedTestCommandLine.Build(Box.Layout, Run);

		Assert.AreEqual(Box.Layout.RunUatScript, Invocation.FileName);
		Assert.AreEqual("RunUAT.bat", Path.GetFileName(Invocation.FileName));
		CollectionAssert.AreEqual(new[]
		{
			"BuildCookRun", "-project=" + Box.ProjectFile, "-platform=Win64", "-clientconfig=Development", "-unattended", "-nop4", "-utf8output",
			"-build", "-cook", "-stage", "-pak", "-run", "-nullrhi", "-RunAutomationTest=Project.Math", "-RunTimeoutSeconds=1800",
		}, Invocation.Arguments.ToArray());
		Assert.AreEqual(Box.ProjectDirectory, Invocation.WorkingDirectory);
		Assert.AreEqual(LogFolder(Box), Invocation.Environment![CookedTestCommandLine.LogFolderVariable]);
	}

	[TestMethod]
	public void TheCommandReusesTheStagedBuildAndPassesGameArguments()
	{
		using Sandbox Box = WithRunUat();
		CookedTestRun Run = new()
		{
			ProjectFile = Box.ProjectFile,
			Filter = "StartsWith:Project.",
			Platform = UnrealPlatform.Win64,
			Configuration = "Test",
			Maps = ["/Engine/Maps/Entry", "/Game/Maps/Main"],
			SkipBuild = true,
			SkipCook = true,
			TimeoutSeconds = 600,
			GameArguments = ["-GameFlag", "-Key=a b"],
			LogFolder = LogFolder(Box),
		};

		CollectionAssert.AreEqual(new[]
		{
			"BuildCookRun", "-project=" + Box.ProjectFile, "-platform=Win64", "-clientconfig=Test", "-unattended", "-nop4", "-utf8output",
			"-skipcook", "-skipstage", "-pak", "-map=/Engine/Maps/Entry+/Game/Maps/Main", "-run", "-nullrhi",
			"-RunAutomationTest=StartsWith:Project.", "-RunTimeoutSeconds=600", "-addcmdline=-GameFlag -Key='a b' ",
		}, CookedTestCommandLine.Build(Box.Layout, Run).Arguments.ToArray());
	}

	[TestMethod]
	public void TheCommandUsesTheHostsRunUatScript()
	{
		using Sandbox Box = WithRunUat(UnrealPlatform.Linux);
		CookedTestRun Run = new() { ProjectFile = Box.ProjectFile, Filter = "Project.", Platform = UnrealPlatform.Linux, LogFolder = LogFolder(Box) };

		ProcessInvocation Invocation = CookedTestCommandLine.Build(Box.Layout, Run);

		Assert.AreEqual("RunUAT.sh", Path.GetFileName(Invocation.FileName));
		Assert.Contains("-platform=Linux", Invocation.Arguments);
	}

	[TestMethod]
	public void AnEngineWithoutRunUatIsASetupError()
	{
		using Sandbox Box = new();
		CookedTestRun Run = new() { ProjectFile = Box.ProjectFile, Filter = "Project.", Platform = UnrealPlatform.Win64, LogFolder = LogFolder(Box) };

		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => CookedTestCommandLine.Build(Box.Layout, Run));
		Assert.Contains(Box.Layout.RunUatScript, Error.Message);
	}

	[TestMethod]
	public void GameArgumentsEndWithASpaceForUatsTestExit()
	{
		// UAT appends -testexit=... straight after -addcmdline's value.
		Assert.AreEqual("-A ", CookedTestCommandLine.AddCmdLine(["-A"]));
		Assert.AreEqual("-A -B=1 plain ", CookedTestCommandLine.AddCmdLine(["-A", "-B=1", "plain"]));
		// UAT turns ' into ", so -Key='a b' reaches the game as -Key="a b".
		Assert.AreEqual("-Key='a b' ", CookedTestCommandLine.AddCmdLine(["-Key=a b"]));
		Assert.AreEqual("'a b' '-x y=z' ", CookedTestCommandLine.AddCmdLine(["a b", "-x y=z"]));
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("-Key=\"a\"")]
	[DataRow("-Key='a'")]
	[DataRow("100%")]
	[DataRow("-Hi!")]
	[DataRow("a&b")]
	[DataRow("a|b")]
	[DataRow("<a")]
	[DataRow("a>")]
	[DataRow("a^")]
	[DataRow("a\tb")]
	[DataRow("-ExecCmds=Quit")]
	[DataRow("-EXECCMDS=Quit")]
	[DataRow("-testexit=Done")]
	[DataRow("--abslog=other.log")]
	[DataRow("/ReportExportPath=Reports")]
	[DataRow("-ReportExportPath")]
	public void GameArgumentsUatCantPassOrUakSetsAreRejected(string argument)
	{
		Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.CheckPassThrough(["-Fine", argument]));
	}

	[TestMethod]
	public void OtherGameArgumentsPass()
	{
		string[] Arguments = ["-GameFlag", "-Key=a b", "-ini:Engine:[Core.Log]:LogTemp=Verbose", "plain", "-ExecCmdsLater"];
		Assert.AreSame(Arguments, CookedTestCommandLine.CheckPassThrough(Arguments));
	}

	[TestMethod]
	[DataRow("Project.Math", true)]
	[DataRow("StartsWith:Project.", true)]
	[DataRow("^Project.Math$", true)]
	[DataRow("Other+^Project.Math", true)]
	[DataRow("Project^Math", false)]
	[DataRow("Project.Math^", false)]
	[DataRow("Project%PATH%", false)]
	[DataRow("Project!Math", false)]
	[DataRow("Project&Math", false)]
	[DataRow("Project<Math", false)]
	[DataRow("Project>Math", false)]
	[DataRow("Project Math", false)]
	[DataRow("Project;Math", false)]
	[DataRow("", false)]
	public void CookedFiltersPassThroughRunUat(string filter, bool valid)
	{
		if (valid)
		{
			CookedTestCommandLine.CheckFilter(filter);
		}
		else
		{
			Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.CheckFilter(filter));
		}
	}

	[TestMethod]
	public void ConfigurationsAndMapsAreChecked()
	{
		Assert.AreEqual("DebugGame", CookedTestCommandLine.ParseConfiguration("debuggame"));
		Assert.AreEqual("Test", CookedTestCommandLine.ParseConfiguration("TEST"));
		Assert.Contains("automation worker", Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.ParseConfiguration("Shipping")).Message);
		Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.ParseConfiguration("Release"));

		Assert.IsEmpty(CookedTestCommandLine.ParseMaps(null));
		CollectionAssert.AreEqual(new[] { "/Engine/Maps/Entry", "/Game/My_Maps/Level-1.Level-1" },
			CookedTestCommandLine.ParseMaps("/Engine/Maps/Entry+/Game/My_Maps/Level-1.Level-1").ToArray());
		Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.ParseMaps(""));
		Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.ParseMaps("/Game/A+"));
		Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.ParseMaps("/Game/A&B"));
		Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.ParseMaps("/Game/A B"));
		Assert.Contains("MSYS_NO_PATHCONV=1", Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.ParseMaps("C:/Program Files/Git/Engine/Maps/Entry")).Message);
	}

	[TestMethod]
	public void UatOutputNamesItsStepsTheGamesExitAndATimeout()
	{
		UatOutputParser Parser = new();

		Assert.AreEqual("COOK COMMAND STARTED", Parser.AddLine("********** COOK COMMAND STARTED **********"));
		Assert.AreEqual("RUN COMMAND COMPLETED", Parser.AddLine("  ********** RUN COMMAND COMPLETED **********  "));
		Assert.IsNull(Parser.AddLine("Client exited with error code: -1 (see the client log for more info)"));
		Assert.IsNull(Parser.AddLine("The run timed out after 60 seconds. Stopping client..."));
		Assert.IsNull(Parser.AddLine("**** TEST COMPLETE. EXIT CODE: 0 ****"));

		CollectionAssert.AreEqual(new[] { "COOK COMMAND STARTED", "RUN COMMAND COMPLETED" }, Parser.Summary.Steps);
		Assert.AreEqual(-1, Parser.Summary.ClientExitCode);
		Assert.AreEqual(60, Parser.Summary.TimedOutAfterSeconds);
	}

	// ---- uak test -cooked ----

	[TestMethod]
	public async Task ACookedRunPassesWhenTheGamePassedEveryTest()
	{
		using Sandbox Box = WithRunUat();
		FakeLock Lock = new();
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-passed.log", 0, RunOutput) };
		string ResultFile = Path.Combine(Box.Root, "r.json");

		int Exit = await new TestCommand(Services(Runner, Lock)).RunAsync(Box.Context(),
			["-cooked", "-filter=StartsWith:Project.", "-skipbuild", "-skipcook", "-map=/Engine/Maps/Entry", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { "uak test -cooked StartsWith:Project." }, Lock.Names);
		Assert.AreEqual(1, Lock.Released);
		ProcessInvocation Invocation = Runner.Invocations.Single();
		Assert.AreEqual(Box.Layout.RunUatScript, Invocation.FileName);
		CollectionAssert.IsSubsetOf(new[] { "-skipcook", "-skipstage", "-pak", "-map=/Engine/Maps/Entry", "-RunAutomationTest=StartsWith:Project." }, Invocation.Arguments.ToArray());
		Assert.DoesNotContain("-build", Invocation.Arguments);
		Assert.AreEqual(LogFolder(Box), Invocation.Environment![CookedTestCommandLine.LogFolderVariable]);

		// The logs are named first, so they can be followed while UAT runs.
		string ClientLog = Path.Combine(LogFolder(Box), "Client.log");
		string UatLog = Path.Combine(Box.ProjectDirectory, "Saved", "Logs", "uak-test-cooked.log");
		Assert.AreEqual("Client log: " + ClientLog, Box.Logger.Entries[0].Message);
		Assert.AreEqual("UAT log: " + UatLog, Box.Logger.Entries[1].Message);
		Assert.Contains("in a cooked Win64 Development game (staged build reused, run)...", Box.Logger.Text);
		Assert.Contains("  RUN COMMAND STARTED", Box.Logger.Text);
		Assert.Contains("test: PASSED: 1 of 1 passed, 0 failed, 0 skipped; found 1. UAT exit 0, game exit 0.", Box.Logger.Text);
		Assert.DoesNotContain("UAT's last output", Box.Logger.Text);

		string[] Uat = File.ReadAllLines(UatLog);
		Assert.AreEqual("> uebp_LogFolder=" + LogFolder(Box), Uat[0]);
		Assert.StartsWith("> ", Uat[1]);
		CollectionAssert.AreEqual(RunOutput, Uat.Skip(2).ToArray());

		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		JsonElement Root = Result.RootElement;
		Assert.AreEqual("cooked", Root.GetProperty("Mode").GetString());
		Assert.AreEqual("passed", Root.GetProperty("Outcome").GetString());
		Assert.AreEqual("Win64", Root.GetProperty("Platform").GetString());
		Assert.AreEqual("Development", Root.GetProperty("Configuration").GetString());
		Assert.AreEqual(1, Root.GetProperty("FoundCount").GetInt32());
		Assert.AreEqual(0, Root.GetProperty("UatExitCode").GetInt32());
		Assert.AreEqual(0, Root.GetProperty("GameExitCode").GetInt32());
		Assert.AreEqual(ClientLog, Root.GetProperty("ClientLog").GetString());
		Assert.AreEqual(JsonValueKind.Null, Root.GetProperty("Expect").ValueKind);
	}

	[TestMethod]
	public async Task ACookedRunWithAFailedTestFails()
	{
		using Sandbox Box = WithRunUat();
		// UAT itself succeeds: the game's -1 is only reported.
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-failed.log", 0, "Client exited with error code: -1 (see the client log for more info)") };

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.Verdict.Fails", "-skipbuild", "-skipcook"], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains("Fail     Project.Verdict.Fails.Run", Box.Logger.Text);
		Assert.Contains("    Project.Verdict.Fails always fails", Box.Logger.Text);
		Assert.Contains("problem: 1 test failed", Box.Logger.Text);
		Assert.Contains("test: FAILED: 0 of 1 passed, 1 failed, 0 skipped; found 1. UAT exit 0, game exit -1.", Box.Logger.Text);
	}

	[TestMethod]
	public async Task ACookedRunWhoseTestNeverCompletedFailsAsNothingRan()
	{
		// The game's queue ends with exit code 0, but the one test it found never completed.
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-skipped.log", 0, RunOutput) };
		string ResultFile = Path.Combine(Box.Root, "r.json");

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(),
			["-cooked", "-filter=Project.Verdict.Skipped", "-skipbuild", "-skipcook", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains("NotRun   Project.Verdict.Skipped.Run", Box.Logger.Text);
		Assert.Contains("problem: found 1 automation tests but 0 completed", Box.Logger.Text);
		Assert.Contains("test: FAILED (NOTHING RAN): 0 of 0 passed, 0 failed, 1 skipped; found 1.", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual("nothing ran", Result.RootElement.GetProperty("Outcome").GetString());
		Assert.AreEqual("Project.Verdict.Skipped.Run", Result.RootElement.GetProperty("NotRun")[0].GetString());
		Assert.AreEqual(0, Result.RootElement.GetProperty("TestCompleteExitCode").GetInt32());
	}

	[TestMethod]
	public async Task ACookedRunFailsWhenUatFails()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-passed.log", 1, "ERROR: Stage failed: the staged build is missing.", "AutomationTool exiting with ExitCode=1 (Error_Unknown)") };

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-skipbuild", "-skipcook"], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains("problem: UAT exited with code 1", Box.Logger.Text);
		Assert.Contains("UAT's last output:", Box.Logger.Text);
		Assert.Contains("    ERROR: Stage failed: the staged build is missing.", Box.Logger.Text);
		Assert.Contains("test: FAILED: 1 of 1 passed, 0 failed, 0 skipped; found 1. UAT exit 1, game exit none.", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AStaleClientLogIsNeverRead()
	{
		using Sandbox Box = WithRunUat();
		// A passing Client.log from an earlier run; this run's UAT fails before the game starts.
		string ClientLog = Sandbox.Write(Path.Combine(LogFolder(Box), "Client.log"), string.Join('\n', Fixtures.Lines("cooked-passed.log")));
		FakeProcessRunner Runner = new() { Behaviour = RunsGame(null, 0, "Nothing to run.") };

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-skipbuild", "-skipcook"], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains($"problem: the game wrote no log: {ClientLog} is missing", Box.Logger.Text);
		Assert.Contains("    Nothing to run.", Box.Logger.Text);
		Assert.Contains("test: FAILED: 0 of 0 passed, 0 failed, 0 skipped; found none.", Box.Logger.Text);
		Assert.IsFalse(File.Exists(ClientLog));
	}

	[TestMethod]
	public async Task ACookedRunThatTimedOutFails()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-passed.log", 0, "The run timed out after 60 seconds. Stopping client...") };
		string ResultFile = Path.Combine(Box.Root, "r.json");

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(),
			["-cooked", "-filter=Project.", "-skipbuild", "-skipcook", "-timeout=60", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains("-RunTimeoutSeconds=60", Runner.Invocations[0].Arguments);
		Assert.Contains("problem: the game ran past -timeout=60 and UAT stopped it", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual(60, Result.RootElement.GetProperty("TimedOutAfterSeconds").GetInt32());
	}

	[TestMethod]
	public async Task ACookedRunPassesGameArgumentsAndItsOptions()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-passed.log", 0, RunOutput) };

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(),
			["-cooked", "-filter=Project.", "-platform=win64", "-config=test", "-name=Cooked", "--", "-GameFlag", "-Key=a b"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		ProcessInvocation Invocation = Runner.Invocations.Single();
		CollectionAssert.IsSubsetOf(new[] { "-platform=Win64", "-clientconfig=Test", "-build", "-cook", "-stage" }, Invocation.Arguments.ToArray());
		Assert.AreEqual("-addcmdline=-GameFlag -Key='a b' ", Invocation.Arguments[^1]);
		Assert.AreEqual(LogFolder(Box, "Cooked"), Invocation.Environment![CookedTestCommandLine.LogFolderVariable]);
		Assert.Contains("(build, cook, stage, pak, run)", Box.Logger.Text);
		Assert.IsTrue(File.Exists(Path.Combine(Box.ProjectDirectory, "Saved", "Logs", "Cooked.log")));
	}

	[TestMethod]
	public async Task TheClientLogIsReadBeforeTheLockIsReleased()
	{
		using Sandbox Box = WithRunUat();
		// The next run with the same -name deletes Client.log as soon as it holds the lock (and UAT empties the folder).
		FakeLock Lock = new() { OnRelease = () => Directory.Delete(LogFolder(Box), recursive: true) };
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-passed.log", 0, RunOutput) };

		int Exit = await new TestCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.Contains("test: PASSED: 1 of 1 passed", Box.Logger.Text);
	}

	// ---- -expect ----

	static string ExpectFile(Sandbox box, params string[] lines) => Sandbox.Write(Path.Combine(box.Root, "expect.txt"), string.Join('\n', lines));

	[TestMethod]
	public async Task ACookedRunPassesWhenItPassedExactlyTheExpectedTests()
	{
		using Sandbox Box = WithRunUat();
		string Expect = ExpectFile(Box, "# Cooked tests", "", "Project.Scaffold.ModulesLoaded", "Other.Suite.NotThisRun");
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-passed.log", 0, RunOutput) };
		string ResultFile = Path.Combine(Box.Root, "r.json");

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(),
			["-cooked", "-filter=StartsWith:Project.", "-expect=" + Expect, "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.Contains($"expect: 1 of 1 expected tests passed, 0 unexpected (2 listed in {Expect})", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		JsonElement Check = Result.RootElement.GetProperty("Expect");
		Assert.AreEqual("Project.Scaffold.ModulesLoaded", Check.GetProperty("Expected")[0].GetString());
		Assert.AreEqual(0, Check.GetProperty("Missing").GetArrayLength());
		Assert.AreEqual(0, Check.GetProperty("Unexpected").GetArrayLength());
	}

	[TestMethod]
	public async Task ACookedRunFailsOnAMissingOrUnexpectedTest()
	{
		using Sandbox Box = WithRunUat();
		string Expect = ExpectFile(Box, "Project.Scaffold.Other");
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-passed.log", 0, RunOutput) };
		string ResultFile = Path.Combine(Box.Root, "r.json");

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(),
			["-cooked", "-filter=Project.Scaffold", "-expect=" + Expect, "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains("missing: Project.Scaffold.Other (did not complete)", Box.Logger.Text);
		Assert.Contains("unexpected: Project.Scaffold.ModulesLoaded", Box.Logger.Text);
		Assert.Contains("problem: 1 expected test did not pass", Box.Logger.Text);
		Assert.DoesNotContain("not expected", Box.Logger.Text);
		Assert.Contains("test: FAILED: 0 of 1 listed passed; 1 unexpected (1 passed); 1 of 1 passed", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		JsonElement Missing = Result.RootElement.GetProperty("Expect").GetProperty("Missing")[0];
		Assert.AreEqual("Project.Scaffold.Other", Missing.GetProperty("Name").GetString());
		Assert.AreEqual(JsonValueKind.Null, Missing.GetProperty("Result").ValueKind);
	}

	[TestMethod]
	public async Task AnEditorRunChecksTheExpectFileToo()
	{
		using Sandbox Box = new();
		string[] Math =
		[
			"Project.Math.VectorAdd", "Project.Math.VectorCross", "Project.Math.VectorDot", "Project.Math.Vector.Normalize",
			"Project.Math.Vector.RoundTrip", "Project.Math.Matrix.RoundTrip", "Project.Math.Matrix.Inverse", "Project.Math.Matrix.Multiply",
			"Project.Math.Matrix.Transpose", "Project.Math.Quat.Normalize", "Project.Math.Quat.RoundTrip", "Project.Math.Quat.Slerp",
			"Project.Math.Clamp", "Project.Math.Lerp", "Project.Math.SqrtSpeed", "Project.Math.Wrap", "Project.Math.Remap",
		];
		FakeProcessRunner Runner = new() { Behaviour = WritesEditorLog(Fixtures.Lines("test-passed.log")) };
		TestCommand Command = new(Services(Runner));

		string All = ExpectFile(Box, [.. Math, "Project.Ui.Open"]);
		Assert.AreEqual(0, await Command.RunAsync(Box.Context(), ["-filter=Project.Math", "-expect=" + All], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("expect: 17 of 17 expected tests passed, 0 unexpected (18 listed in", Box.Logger.Text);

		// Every listed test passed, and others completed too: reported apart from a failure, with its own exit code.
		string One = ExpectFile(Box, "Project.Math.Clamp");
		Assert.AreEqual(TestCommand.UnexpectedTestsExitCode, await Command.RunAsync(Box.Context(), ["-filter=Project.Math", "-expect=" + One], CancellationToken.None));
		Assert.Contains("unexpected: Project.Math.Lerp", Box.Logger.Text);
		Assert.Contains("test: PASSED: 1 of 1 listed passed; 16 unexpected (16 passed); 17 of 17 passed, 0 failed, 0 skipped; found 17.", Box.Logger.Text);
		Assert.Contains("unexpected: 16 tests the -expect file doesn't list completed: exit code 3 (-allow-unexpected accepts them)", Box.Logger.Text);
		Assert.AreEqual(0, await Command.RunAsync(Box.Context(), ["-filter=Project.Math", "-expect=" + One, "-allow-unexpected"], CancellationToken.None));
		Assert.Contains("accepted (-allow-unexpected)", Box.Logger.Text);
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), ["-filter=Project.Math", "-allow-unexpected"], CancellationToken.None));
	}

	// ---- the editor's never-completed tests ----

	static Func<ProcessInvocation, Action<string>, int> WritesEditorLog(string[] lines) => (Invocation, _) =>
	{
		string LogFile = Invocation.Arguments.Single(Argument => Argument.StartsWith("-abslog=", StringComparison.Ordinal))["-abslog=".Length..];
		File.WriteAllLines(LogFile, lines);
		return 0;
	};

	[TestMethod]
	public async Task AnEditorTestThatNeverCompletedIsSkippedAndFailsTheRun()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new()
		{
			Behaviour = WritesEditorLog(
			[
				"LogAutomationCommandLine: Display: Found 2 automation tests based on 'Game'",
				"LogAutomationCommandLine: Display: \tGame.A",
				"LogAutomationCommandLine: Display: \tGame.B",
				"LogAutomationController: Display: Test Completed. Result={Success} Name={A} Path={Game.A}",
				"LogAutomationCommandLine: Display: **** TEST COMPLETE. EXIT CODE: 0 ****",
			]),
		};
		string ResultFile = Path.Combine(Box.Root, "r.json");

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Game", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains("NotRun   Game.B", Box.Logger.Text);
		Assert.Contains("problem: found 2 automation tests but 1 completed", Box.Logger.Text);
		Assert.Contains("test: FAILED: 1 of 1 passed, 0 failed, 1 skipped; found 2.", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual("editor", Result.RootElement.GetProperty("Mode").GetString());
		Assert.AreEqual(1, Result.RootElement.GetProperty("Skipped").GetInt32());
		Assert.AreEqual("Game.B", Result.RootElement.GetProperty("NotRun")[0].GetString());
	}

	// ---- usage errors ----

	[TestMethod]
	public async Task CookedAndExpectUsageErrors()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = new();
		FakeLock Lock = new();
		TestCommand Command = new(Services(Runner, Lock));
		string Expect = ExpectFile(Box, "Project.Math.Add");
		string[][] Errors =
		[
			// Editor options in a cooked run, and cooked options in an editor run.
			["-cooked", "-filter=A", "-gpu"],
			["-cooked", "-filter=A", "-windowed"],
			["-cooked", "-filter=A", "-resx=800"],
			["-filter=A", "-map=/Game/A"],
			["-filter=A", "-config=Test"],
			["-filter=A", "-platform=Win64"],
			["-filter=A", "-skipbuild"],
			["-filter=A", "-skipcook"],
			["-filter=A", "-timeout=60"],
			// Bad values.
			["-cooked", "-filter=A", "-config=Shipping"],
			["-cooked", "-filter=A", "-config=Release"],
			["-cooked", "-filter=A", "-platform=Linux"],
			["-cooked", "-filter=A", "-platform=Amiga"],
			["-cooked", "-filter=A", "-map=/Game/A+"],
			["-cooked", "-filter=A", "-timeout=0"],
			["-cooked", "-filter=A^B"],
			["-cooked", "-filter=A%B"],
			["-cooked", "-filter=A", "-name=.."],
			["-cooked", "-filter=A", "-name=."],
			["-cooked", "-filter=A", "-name=a/b"],
			// Game arguments uak sets or RunUAT can't pass.
			["-cooked", "-filter=A", "--", "-ExecCmds=Quit"],
			["-cooked", "-filter=A", "--", "-testexit=Done"],
			["-cooked", "-filter=A", "--", "-abslog=x.log"],
			["-cooked", "-filter=A", "--", "-ReportExportPath=x"],
			["-cooked", "-filter=A", "--", "a&b"],
			["-cooked", "-filter=A", "--", "-Key=\"x\""],
			// Expect files the filter can't be matched against.
			["-cooked", "-filter=Project.Math", "-expect=" + Path.Combine(Box.Root, "missing.txt")],
			["-cooked", "-filter=Project.Ui", "-expect=" + Expect],
			["-cooked", "-filter=Group:Smoke", "-expect=" + Expect],
			["-filter=Project.Ui", "-expect=" + Expect],
		];

		foreach (string[] Arguments in Errors)
		{
			Assert.AreEqual(2, await Command.RunAsync(Box.Context(), Arguments, CancellationToken.None), string.Join(' ', Arguments));
		}
		Assert.IsEmpty(Runner.Invocations);
		Assert.IsEmpty(Lock.Names);
	}

	[TestMethod]
	public async Task ACookedRunWithoutRunUatIsASetupError()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new();

		Assert.AreEqual(2, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=A"], CancellationToken.None));
		Assert.Contains("RunUAT script not found: " + Box.Layout.RunUatScript, Box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations);
	}
}
