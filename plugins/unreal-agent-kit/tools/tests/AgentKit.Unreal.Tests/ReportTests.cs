// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;
using AgentKit.Vcs;

namespace AgentKit.Unreal.Tests;

/// <summary>What uak test and uak build report beyond pass and fail: the tree, watched categories, filter warnings, summaries.</summary>
[TestClass]
public sealed class ReportTests
{
	static UnrealServices Services(FakeProcessRunner runner, FakeLock? lockProvider = null, VcsTreeState? tree = null, Dictionary<string, string>? environment = null,
		long? freeMemory = null) => new()
	{
		Runner = runner,
		Lock = lockProvider ?? new FakeLock(),
		HostPlatform = UnrealPlatform.Win64,
		GetEnvironmentVariable = Name => environment is not null && environment.TryGetValue(Name, out string? Value) ? Value : null,
		CaptureTree = (_, _) => Task.FromResult(tree ?? new VcsTreeState { Kind = "None" }),
		FreeMemoryBytes = () => freeMemory,
	};

	static Func<ProcessInvocation, Action<string>, int> WritesLog(int exitCode, params string[] lines) => (Invocation, _) =>
	{
		string LogFile = Invocation.Arguments.Single(Argument => Argument.StartsWith("-abslog=", StringComparison.Ordinal))["-abslog=".Length..];
		File.WriteAllLines(LogFile, lines);
		return exitCode;
	};

	static string[] Run(string filter, params string[] completed)
	{
		List<string> Lines = [$"[2026.10.29-12.00.00:000][  1]LogAutomationCommandLine: Display: Found {completed.Length} automation tests based on '{filter}'"];
		Lines.AddRange(completed.Select(Test => $"[2026.10.29-12.00.00:000][  1]LogAutomationCommandLine: Display: \t{Test}"));
		Lines.AddRange(completed.Select(Test =>
			$"[2026.10.29-12.00.01:000][  2]LogAutomationController: Display: Test Completed. Result={{Success}} Name={{{Test.Split('.')[^1]}}} Path={{{Test}}}"));
		Lines.Add("[2026.10.29-12.00.02:000][  3]LogAutomationCommandLine: Display: **** TEST COMPLETE. EXIT CODE: 0 ****");
		return [.. Lines];
	}

	// ---- the tree a run tested ----

	[TestMethod]
	public async Task TestAndBuildPrintAndRecordTheTreeTheyRanOn()
	{
		using Sandbox Box = new();
		VcsTreeState Tree = new() { Kind = "Git", Revision = "0123456789abcdef", ChangedCount = 2, Changed = ["Modified Source/A.cpp", "Untracked notes.txt"], ChangesHash = "a1b2c3d4e5f6" };
		FakeProcessRunner Runner = new() { Behaviour = WritesLog(0, Run("Project.", "Project.A")) };
		string ResultFile = Path.Combine(Box.Root, "r.json");

		Assert.AreEqual(0, await new TestCommand(Services(Runner, tree: Tree)).RunAsync(Box.Context(), ["-filter=Project.", "-name=TreeRun", "-resultfile=" + ResultFile], CancellationToken.None),
			Box.Logger.Text);

		Assert.Contains("tree: Git 0123456789abcdef + 2 changed (hash a1b2c3d4e5f6): Modified Source/A.cpp, Untracked notes.txt", Box.Logger.Text);
		using (JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile)))
		{
			Assert.AreEqual("0123456789abcdef", Result.RootElement.GetProperty("Tree").GetProperty("Revision").GetString());
			Assert.AreEqual(2, Result.RootElement.GetProperty("Tree").GetProperty("ChangedCount").GetInt32());
		}

		FakeProcessRunner Ubt = new() { Behaviour = FakeProcessRunner.Prints(0, "Result: Succeeded") };
		Assert.AreEqual(0, await new BuildCommand(Services(Ubt, tree: Tree)).RunAsync(Box.Context(), ["-resultfile=" + ResultFile], CancellationToken.None));
		Assert.AreEqual(2, Box.Logger.Entries.Count(Entry => Entry.Message.StartsWith("tree: Git 0123456789abcdef", StringComparison.Ordinal)));
		RunSummaryFile Build = RunSummaries.Read(new DirectoryInfo(Box.StateDirectory), "uak-build")!;
		Assert.AreEqual("build", Build.Entries.Single().Command);
		Assert.StartsWith("tree: Git", Build.Entries.Single().Lines[0]);
	}

	[TestMethod]
	public void TheTreeLineNamesTheFilesOrTheirCountAndHash()
	{
		Assert.AreEqual("none (no version control)", new VcsTreeState { Kind = "None" }.ToString());
		Assert.AreEqual("Perforce 1234, no changes", new VcsTreeState { Kind = "Perforce", Revision = "1234" }.ToString());
		Assert.AreEqual("Git abc + 42 changed (hash 0123456789ab)", new VcsTreeState { Kind = "Git", Revision = "abc", ChangedCount = 42, ChangesHash = "0123456789ab" }.ToString());
		Assert.AreEqual("unknown (p4 timed out)", new VcsTreeState { Kind = "Perforce", Error = "p4 timed out" }.ToString());
	}

	// ---- -expect: unexpected tests apart from failures ----

	[TestMethod]
	public async Task AFailedUnexpectedTestIsAFailure()
	{
		using Sandbox Box = new();
		string Expect = Sandbox.Write(Path.Combine(Box.Root, "expect.txt"), "Project.A\n");
		string[] Log = Run("Project.", "Project.A", "Project.B");
		Log[^2] = Log[^2].Replace("{Success}", "{Fail}", StringComparison.Ordinal);
		FakeProcessRunner Runner = new() { Behaviour = WritesLog(0, Log) };

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.", "-expect=" + Expect, "-allow-unexpected"], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains("test: FAILED: 1 of 1 listed passed; 1 unexpected (1 failed); 1 of 2 passed, 1 failed, 0 skipped; found 2.", Box.Logger.Text);
	}

	// ---- -watch ----

	[TestMethod]
	public void WatchCountsOnlyRealErrorsAndWarningsOfTheCategories()
	{
		string[] Lines =
		[
			"[2026.10.29-12.00.00:000][  1]LogSaveGame: Error: real error",
			"[2026.10.29-12.00.00:000][  1]LogSaveGame: Warning: real warning",
			"[2026.10.29-12.00.00:000][  1]LogSaveGame: Warning: another",
			"[2026.10.29-12.00.00:000][  1]LogSaveGame: Verbose: Error: an expected error, lowered to Verbose",
			"[2026.10.29-12.00.00:000][  1]LogSaveGame: Display: Error: a display line",
			"LogSaveGame: Error: before the log's time prefix",
			"[2026.10.29-12.00.00:000][  1]LogOther: Error: not watched",
			"[2026.10.29-12.00.00:000][  1]LogAutomationController: Error: LogSaveGame: Error: re-logged by the controller",
		];

		List<LogCategoryCount> Counts = LogCategoryCounts.Count(Lines, LogCategoryCounts.ParseWatch("logsavegame, LogMissing"));

		Assert.AreEqual("logsavegame: 2 errors, 2 warnings", Counts[0].ToString());
		Assert.AreEqual("LogMissing: 0 errors, 0 warnings", Counts[1].ToString());
		Assert.ThrowsExactly<UakUsageException>(() => LogCategoryCounts.ParseWatch("LogA,,LogB"));
		Assert.ThrowsExactly<UakUsageException>(() => LogCategoryCounts.ParseWatch("Log A"));
	}

	[TestMethod]
	public async Task AWatchedCategoryIsCountedInTheSummary()
	{
		using Sandbox Box = new();
		string[] Log = [.. Run("Project.", "Project.A"), "[2026.10.29-12.00.03:000][  4]LogSaveGame: Warning: slow save"];
		FakeProcessRunner Runner = new() { Behaviour = WritesLog(0, Log) };
		string ResultFile = Path.Combine(Box.Root, "r.json");

		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.", "-watch=LogSaveGame", "-resultfile=" + ResultFile], CancellationToken.None));

		Assert.Contains("watch: LogSaveGame: 0 errors, 1 warning", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual(1, Result.RootElement.GetProperty("Watch")[0].GetProperty("Warnings").GetInt32());
	}

	// ---- filter warnings ----

	[TestMethod]
	public void APrefixWithoutADotThatCrossesIntoAnotherNameIsWarnedAbout()
	{
		string[] Tests = ["Game.Save.A", "Game.Load.B", "GameBench.Save.C"];

		string Warning = AutomationFilter.Parse("Game").Warnings(Tests, complete: true).Single();
		Assert.Contains("'Game' has no trailing '.', so it also matches tests under GameBench (3 tests in all)", Warning);
		Assert.IsEmpty(AutomationFilter.Parse("Game.").Warnings(Tests, complete: true));
		Assert.IsEmpty(AutomationFilter.Parse("StartsWith:Game").Warnings(Tests, complete: true));
		Assert.IsEmpty(AutomationFilter.Parse("GameBench").Warnings(Tests, complete: true));
		Assert.HasCount(1, AutomationFilter.Parse("^Game.Save").Warnings(["Game.Save.A", "Game.SaveGame.B"], complete: true));
	}

	[TestMethod]
	public void AFilterPartThatMatchesNothingIsWarnedAboutOnlyWhenEveryTestIsKnown()
	{
		string[] Tests = ["Project.A", "Project.B"];

		Assert.AreEqual("-filter part 'Projcet.' matched none of the 2 tests the rest of the filter found",
			AutomationFilter.Parse("Project.+Projcet.").Warnings(Tests, complete: true).Single());
		Assert.IsEmpty(AutomationFilter.Parse("Project.+Projcet.").Warnings(Tests, complete: false));
	}

	[TestMethod]
	public async Task TheRunWarnsAboutItsFilter()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = WritesLog(0, Run("Game", "Game.A", "GameBench.B")) };

		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Game"], CancellationToken.None));

		Assert.Contains("also matches tests under GameBench", Box.Logger.Text);
	}

	// ---- summaries for uak runs show ----

	[TestMethod]
	public async Task ATestRecordsItsSummaryUnderItsNameAndItsRuns()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = WritesLog(0, Run("Project.", "Project.A")) };
		Dictionary<string, string> InRun = new() { [RunSummaries.RunNameVariable] = "bench-1" };
		DirectoryInfo State = new(Box.StateDirectory);

		Assert.AreEqual(0, await new TestCommand(Services(Runner, environment: InRun)).RunAsync(Box.Context(), ["-filter=Project.", "-name=First"], CancellationToken.None));
		Assert.AreEqual(0, await new TestCommand(Services(Runner, environment: InRun)).RunAsync(Box.Context(), ["-filter=Project.", "-name=Second"], CancellationToken.None));

		RunSummaryEntry First = RunSummaries.Read(State, "First")!.Entries.Single();
		Assert.AreEqual("test", First.Command);
		Assert.AreEqual(0, First.ExitCode);
		Assert.StartsWith("test: PASSED: 1 of 1 passed", First.Lines[^1]);
		CollectionAssert.AreEqual(new[] { "First", "Second" }, RunSummaries.Read(State, "bench-1")!.Entries.Select(Entry => Entry.Name).ToArray());
	}

	// ---- -quiet ----

	[TestMethod]
	public async Task AQuietRunRefusesWhileOthersWaitAndTakesAQuietHold()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = WritesLog(0, Run("Project.", "Project.A")) };

		FakeLock Busy = new() { Waiting = 2 };
		Assert.AreEqual(2, await new TestCommand(Services(Runner, Busy)).RunAsync(Box.Context(), ["-filter=Project.", "-quiet"], CancellationToken.None));
		Assert.Contains("-quiet: 2 other runs wait in line", Box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations);

		FakeLock Free = new();
		Assert.AreEqual(0, await new TestCommand(Services(Runner, Free)).RunAsync(Box.Context(), ["-filter=Project.", "-quiet"], CancellationToken.None));
		CollectionAssert.AreEqual(new[] { true }, Free.Quiet.ToArray());
	}

	// ---- uak test config ----

	[TestMethod]
	public async Task SavedCookedMapsAreTheDefaultForACookedRun()
	{
		using Sandbox Box = new();
		Sandbox.Write(Box.Layout.RunUatScript, "");
		TestConfigCommand Config = new();

		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), ["-cooked-maps=/Game/Maps/Test+/Engine/Maps/Entry"], CancellationToken.None));
		Assert.AreEqual("/Game/Maps/Test+/Engine/Maps/Entry", ProjectSettings.GetCookedMaps(Box.Context()));
		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), [], CancellationToken.None));
		Assert.Contains("cooked-maps: /Game/Maps/Test+/Engine/Maps/Entry", Box.Logger.Text);
		Assert.AreEqual(2, await Config.RunAsync(Box.Context(), ["-cooked-maps=C:/Program Files/Git/Game/Maps/Test"], CancellationToken.None));

		FakeProcessRunner Runner = new() { Behaviour = (_, _) => 1 };
		await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None);
		Assert.Contains("-map=/Game/Maps/Test+/Engine/Maps/Entry", Runner.Invocations[0].Arguments);
		await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-map=/Engine/Maps/Entry"], CancellationToken.None);
		Assert.Contains("-map=/Engine/Maps/Entry", Runner.Invocations[1].Arguments);

		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), ["-reset"], CancellationToken.None));
		Assert.IsNull(ProjectSettings.GetCookedMaps(Box.Context()));
	}

	[TestMethod]
	public async Task TestConfigKeepsSettingsItDoesNotKnow()
	{
		using Sandbox Box = new();
		string File = ProjectSettings.GetFile(Box.Context());
		Sandbox.Write(File, """{ "other": { "kept": true }, "test": { "future": 1 } }""");

		Assert.AreEqual(0, await new TestConfigCommand().RunAsync(Box.Context(), ["-cooked-maps=/Engine/Maps/Entry"], CancellationToken.None));

		using JsonDocument Settings = JsonDocument.Parse(System.IO.File.ReadAllText(File));
		Assert.IsTrue(Settings.RootElement.GetProperty("other").GetProperty("kept").GetBoolean());
		Assert.AreEqual(1, Settings.RootElement.GetProperty("test").GetProperty("future").GetInt32());
		Assert.AreEqual("/Engine/Maps/Entry", Settings.RootElement.GetProperty("test").GetProperty("cookedMaps").GetString());
	}

	[TestMethod]
	public async Task SavedBuildWritesArePrintedAndForgotten()
	{
		using Sandbox Box = new();
		TestConfigCommand Config = new();
		Assert.IsEmpty(ProjectSettings.GetBuildWrites(Box.Context()));

		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), [@"-build-writes=Source\Game\Private\Version.gen.h+Source/Game/Public/Stamp.h"], CancellationToken.None), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { "Source/Game/Private/Version.gen.h", "Source/Game/Public/Stamp.h" }, ProjectSettings.GetBuildWrites(Box.Context()).ToArray());
		Assert.Contains("build-writes: Source/Game/Private/Version.gen.h+Source/Game/Public/Stamp.h", Box.Logger.Text);

		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), ["-reset"], CancellationToken.None), Box.Logger.Text);

		Assert.IsEmpty(ProjectSettings.GetBuildWrites(Box.Context()));
		Assert.Contains("build-writes: (none", Box.Logger.Text);
	}

	[TestMethod]
	public async Task BuildWritesSurviveWindowsPowerShellSplittingThem()
	{
		// Windows PowerShell 5.1 splits -build-writes=Source/Version.gen.h at its first dot.
		using Sandbox Box = new();

		Assert.AreEqual(0, await new TestConfigCommand().RunAsync(Box.Context(), ["-build-writes=Source/Version", ".gen.h"], CancellationToken.None), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { "Source/Version.gen.h" }, ProjectSettings.GetBuildWrites(Box.Context()).ToArray());
	}

	[TestMethod]
	[DataRow("Source/*.h")]
	[DataRow("Source/Game/Version?.h")]
	[DataRow("C:/Project/Source/Foo.h")]
	[DataRow("/Source/Foo.h")]
	[DataRow(@"\Source\Foo.h")]
	[DataRow("../Other/Foo.h")]
	[DataRow("Source/../Foo.h")]
	[DataRow("./Source/Foo.h")]
	[DataRow("Source//Foo.h")]
	[DataRow("Source/Good.h+Source/*.h")]
	[DataRow("+")]
	public async Task BuildWritesTakeOnlyExactPathsFromTheProject(string paths)
	{
		using Sandbox Box = new();

		Assert.AreEqual(2, await new TestConfigCommand().RunAsync(Box.Context(), ["-build-writes=" + paths], CancellationToken.None));

		Assert.IsEmpty(ProjectSettings.GetBuildWrites(Box.Context()));
	}

	[TestMethod]
	public async Task NothingIsSavedBesideAValueThatIsRefused()
	{
		using Sandbox Box = new();

		Assert.AreEqual(2, await new TestConfigCommand().RunAsync(Box.Context(), ["-cooked-maps=/Game/Maps/Test", "-build-writes=Source/*.h"], CancellationToken.None));

		Assert.IsNull(ProjectSettings.GetCookedMaps(Box.Context()));
		Assert.IsEmpty(ProjectSettings.GetBuildWrites(Box.Context()));
		Assert.IsFalse(System.IO.File.Exists(ProjectSettings.GetFile(Box.Context())));
	}

	[TestMethod]
	public async Task AFileThatIsntThereIsWarnedAboutAndSaved()
	{
		// A build may yet write it, but a typing error would otherwise say nothing.
		using Sandbox Box = new();

		Assert.AreEqual(0, await new TestConfigCommand().RunAsync(Box.Context(), ["-build-writes=Source/Game/Public/Missing.h+Source/Game/Public/Foo.h"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("Source/Game/Public/Missing.h doesn't exist", Box.Logger.Text);
		Assert.DoesNotContain("Foo.h doesn't exist", Box.Logger.Text);
		Assert.DoesNotContain("isn't under Source or Plugins", Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { "Source/Game/Public/Missing.h", "Source/Game/Public/Foo.h" }, ProjectSettings.GetBuildWrites(Box.Context()).ToArray());
	}

	[TestMethod]
	public async Task AFileOutsideTheFoldersUakLooksInIsWarnedAboutAndSaved()
	{
		using Sandbox Box = new();

		Assert.AreEqual(0, await new TestConfigCommand().RunAsync(Box.Context(), ["-build-writes=Game.uproject"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("Game.uproject isn't under Source or Plugins", Box.Logger.Text);
		Assert.DoesNotContain("doesn't exist", Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { "Game.uproject" }, ProjectSettings.GetBuildWrites(Box.Context()).ToArray());
	}

	[TestMethod]
	public async Task AFileUnderPluginsIsInTheFoldersUakLooksIn()
	{
		using Sandbox Box = new();
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Foo", "Stamp.h"), "");

		Assert.AreEqual(0, await new TestConfigCommand().RunAsync(Box.Context(), ["-build-writes=plugins/Foo/Stamp.h"], CancellationToken.None), Box.Logger.Text);

		Assert.DoesNotContain("isn't under Source or Plugins", Box.Logger.Text);
	}

	[TestMethod]
	public async Task ResetIsRefusedBesideANewValue()
	{
		using Sandbox Box = new();
		ProjectSettings.SetBuildWrites(Box.Context(), ["Source/Stamp.h"]);

		Assert.AreEqual(2, await new TestConfigCommand().RunAsync(Box.Context(), ["-build-writes=Source/Foo.h", "-reset"], CancellationToken.None));

		CollectionAssert.AreEqual(new[] { "Source/Stamp.h" }, ProjectSettings.GetBuildWrites(Box.Context()).ToArray());
	}

	[TestMethod]
	public async Task BuildWritesKeepTheOtherSettings()
	{
		using Sandbox Box = new();
		string File = ProjectSettings.GetFile(Box.Context());
		Sandbox.Write(File, """{ "other": { "kept": true }, "build": { "future": 1 } }""");

		Assert.AreEqual(0, await new TestConfigCommand().RunAsync(Box.Context(), ["-build-writes=Source/Foo.h", "-cooked-maps=/Engine/Maps/Entry"], CancellationToken.None), Box.Logger.Text);

		using JsonDocument Settings = JsonDocument.Parse(System.IO.File.ReadAllText(File));
		Assert.IsTrue(Settings.RootElement.GetProperty("other").GetProperty("kept").GetBoolean());
		Assert.AreEqual(1, Settings.RootElement.GetProperty("build").GetProperty("future").GetInt32());
		Assert.AreEqual("Source/Foo.h", Settings.RootElement.GetProperty("build").GetProperty("writes")[0].GetString());
		Assert.AreEqual("/Engine/Maps/Entry", ProjectSettings.GetCookedMaps(Box.Context()));
	}

	// ---- memory-aware compile -dependents ----

	[TestMethod]
	public void MemoryLimitsAGivenParallelismAsUbtLimitsItsDefault()
	{
		const long GiB = 1024L * 1024 * 1024;
		Assert.AreEqual(8, MemoryLimits.Limit(8, 64 * GiB));
		Assert.AreEqual(2, MemoryLimits.Limit(8, 3 * GiB));
		Assert.AreEqual(1, MemoryLimits.Limit(8, GiB / 2));
		Assert.AreEqual(8, MemoryLimits.Limit(8, null));
		Assert.IsNull(MemoryLimits.Limit(null, GiB));
		Assert.IsNotNull(MemoryLimits.GetFreeMemoryBytes());
	}

	[TestMethod]
	public async Task CompileDependentsLowersTheParallelismToTheFreeMemory()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0) };
		Dictionary<string, string> Environment = new() { ["UAK_MAX_PARALLEL_ACTIONS"] = "8" };

		await new CompileCommand(Services(Runner, environment: Environment, freeMemory: 3L * 1024 * 1024 * 1024)).RunAsync(Box.Context(), [Box.HeaderFile, "-dependents"], CancellationToken.None);
		await new CompileCommand(Services(Runner, environment: Environment, freeMemory: 3L * 1024 * 1024 * 1024)).RunAsync(Box.Context(), [Box.HeaderFile], CancellationToken.None);

		Assert.Contains("-MaxParallelActions=2", Runner.Invocations[0].Arguments);
		Assert.Contains("-MaxParallelActions=8", Runner.Invocations[1].Arguments);
		Assert.Contains("-MaxParallelActions=8 lowered to 2", Box.Logger.Text);
	}
}
