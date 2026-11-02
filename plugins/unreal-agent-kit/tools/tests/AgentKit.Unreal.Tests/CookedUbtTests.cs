// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Unreal.Tests;

/// <summary>A cooked run and <c>uak compile</c> take turns at UBT's mutex; the report says when something else held it.</summary>
public sealed partial class CookedTestTests
{
	const string BuildStarted = "********** BUILD COMMAND STARTED **********";
	const string BuildCompleted = "********** BUILD COMMAND COMPLETED **********";
	const string UbtConflictLine = @"A conflicting instance of Global\UnrealBuildTool_Mutex_0123456789abcdef is already running.";

	[TestMethod]
	public async Task ARunThatBuildsReservesUbtUntilUatsBuildStepEnds()
	{
		using Sandbox Box = WithRunUat();
		UbtReservation? AtTheStart = null;
		UbtReservation? AfterTheBuild = null;
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				AtTheStart = QuietRuns.GetUbtReservation(Box.Context());
				OnLine(BuildStarted);
				OnLine(BuildCompleted);
				AfterTheBuild = QuietRuns.GetUbtReservation(Box.Context());
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		Assert.IsNotNull(AtTheStart, "the reservation is taken before UAT starts");
		Assert.Contains("Project.", AtTheStart.Name);
		Assert.IsNull(AfterTheBuild, "a compile need not wait for the cook and the tests");
		Assert.IsNull(QuietRuns.GetUbtReservation(Box.Context()));
	}

	[TestMethod]
	public async Task ARunThatDoesNotBuildReservesNothing()
	{
		using Sandbox Box = WithRunUat();
		bool Reserved = true;
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				Reserved = QuietRuns.GetUbtReservation(Box.Context()) is not null;
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-skipbuild", "-skipcook"], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(Reserved);
	}

	[TestMethod]
	public async Task ARunThatEndsDuringItsBuildGivesTheReservationBack()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = new() { Behaviour = RunsGame(null, 25, BuildStarted) };

		await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None);

		Assert.IsNull(QuietRuns.GetUbtReservation(Box.Context()));
	}

	[TestMethod]
	public async Task ARunWaitsForACompileBeforeUatStarts()
	{
		using Sandbox Box = WithRunUat();
		bool WaitedFirst = false;
		bool Waited = false;
		UnrealServices Waiting = new()
		{
			Runner = new FakeProcessRunner { Behaviour = (Invocation, OnLine) => { WaitedFirst = Waited; return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine); } },
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
			CaptureTree = TestSupportTrees.None,
			ReserveUbt = (_, _, _) =>
			{
				Waited = true;
				return Task.FromResult<IDisposable>(new MemoryStream());
			},
		};

		Assert.AreEqual(0, await new TestCommand(Waiting).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		// The reservation is a fake here, so this proves the order only; the next test has the real one.
		Assert.IsTrue(WaitedFirst);
	}

	[TestMethod]
	public async Task UatDoesNotStartWhileARealCompileRuns()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = new() { Behaviour = RunsGame("cooked-passed.log", 0, RunOutput) };
		IDisposable Compile = await QuietRuns.EnterCompileAsync(Box.Context(), TimeSpan.FromMilliseconds(20), CancellationToken.None);

		Task<int> Run = new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None);
		await Task.Delay(300);
		Assert.IsEmpty(Runner.Invocations);

		Compile.Dispose();
		Assert.AreEqual(0, await Run.WaitAsync(TimeSpan.FromSeconds(30)), Box.Logger.Text);
		Assert.HasCount(1, Runner.Invocations);
	}

	[TestMethod]
	public async Task AnExitOfTenWithUbtsLineSaysAnotherUbtHeldItsMutex()
	{
		using Sandbox Box = WithRunUat();
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = RunsGame(null, 10, BuildStarted, UbtConflictLine, "AutomationTool exiting with ExitCode=10 (Error_SDKNotFound)") };

		Assert.AreNotEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None));

		Assert.Contains("another UBT held UBT's mutex", Box.Logger.Text);
		Assert.Contains("no SDK is missing", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.IsTrue(Result.RootElement.GetProperty("UbtConflict").GetBoolean());
	}

	[TestMethod]
	public async Task AnExitOfTenWithoutThatLineSaysWhatItMayBe()
	{
		using Sandbox Box = WithRunUat();
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = RunsGame(null, 10, "AutomationTool exiting with ExitCode=10 (Error_SDKNotFound)") };

		await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.Contains("Error_SDKNotFound, or UBT's ConflictingInstance", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.IsFalse(Result.RootElement.GetProperty("UbtConflict").GetBoolean());
	}

	[TestMethod]
	public void AutomationToolsOwnConflictIsNotUbts()
	{
		UatOutputParser Parser = new();

		Parser.AddLine("A conflicting instance of AutomationTool is already running.");

		Assert.IsFalse(Parser.Summary.UbtConflict);
		Parser.AddLine(UbtConflictLine);
		Assert.IsTrue(Parser.Summary.UbtConflict);
	}

	[TestMethod]
	public void AnotherExitSaysOnlyItsCode()
	{
		Assert.AreEqual("UAT exited with code 25", TestCommand.DescribeUatExit(25, new UatRunSummary()));
	}

	[TestMethod]
	public async Task AnEditDuringUatsBuildIsMarkedAndOneAfterItIsNot()
	{
		using Sandbox Box = WithRunUat();
		File.SetLastWriteTimeUtc(Box.HeaderFile, DateTime.UtcNow.AddHours(-1));
		File.SetLastWriteTimeUtc(Box.SourceFile, DateTime.UtcNow.AddHours(-1));
		string ResultFile = Path.Combine(Box.Root, "result.json");
		string Object = Path.Combine(Box.ProjectDirectory, "Intermediate", "Foo.obj");
		DateTime BuildEnded = default;
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited during the build\n");
				Sandbox.Write(Object, "from the old text");
				OnLine(BuildCompleted);
				BuildEnded = DateTime.UtcNow;
				Thread.Sleep(50);
				File.WriteAllText(Box.SourceFile, "// edited during the cook\n");
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};
		UnrealServices NoWaiting = new()
		{
			Runner = Runner,
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
			CaptureTree = TestSupportTrees.None,
			Delay = _ => Task.CompletedTask,
		};

		Assert.AreEqual(0, await new TestCommand(NoWaiting).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.HeaderFile) - File.GetLastWriteTimeUtc(Object));
		Assert.IsLessThan(TimeSpan.FromSeconds(1), File.GetLastWriteTimeUtc(Box.SourceFile) - BuildEnded, "no build ran during the cook, so the next one sees that edit by itself");
		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		Assert.Contains($"problem: the binaries UAT's build made, the game's and the editor's, may mix old and new: {Box.HeaderFile} changed while it ran: build again before you test", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual("passed", Result.RootElement.GetProperty("Outcome").GetString(), "told, not judged");
		Assert.AreEqual(Box.HeaderFile, Result.RootElement.GetProperty("EditedDuringBuild").EnumerateArray().Single().GetString());
		Assert.AreEqual(0, Result.RootElement.GetProperty("EditNotMarked").GetArrayLength());
		Assert.Contains(Box.HeaderFile, Result.RootElement.GetProperty("Problems").EnumerateArray().Single().GetString()!);
	}

	/// <summary>A run's services with the wait before a stamp replaced by <paramref name="delay"/>, by default none.</summary>
	static UnrealServices Stamping(FakeProcessRunner runner, FakeLock? lockProvider = null, Func<TimeSpan, Task>? delay = null, Func<string, DateTime?>? probe = null, Action<string, DateTime>? setWriteTime = null) => new()
	{
		Runner = runner,
		Lock = lockProvider ?? new FakeLock(),
		HostPlatform = UnrealPlatform.Win64,
		GetEnvironmentVariable = _ => null,
		CaptureTree = TestSupportTrees.None,
		Delay = delay ?? (_ => Task.CompletedTask),
		ProbeFileTime = probe ?? MutationJournal.ProbeFileTime,
		SetWriteTime = setWriteTime ?? File.SetLastWriteTimeUtc,
	};

	static void Age(params string[] files)
	{
		foreach (string File in files)
		{
			System.IO.File.SetLastWriteTimeUtc(File, DateTime.UtcNow.AddHours(-1));
		}
	}

	static string ObjectFile(Sandbox box) => Path.Combine(box.ProjectDirectory, "Intermediate", "Foo.obj");

	[TestMethod]
	public async Task AnEditIsMarkedAsTheBuildStepEndsNotAsTheRunDoes()
	{
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		TimeSpan AheadDuringTheCook = default;
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited during the build\n");
				Sandbox.Write(ObjectFile(Box), "from the old text");
				OnLine(BuildCompleted);
				DateTime Deadline = DateTime.UtcNow.AddSeconds(10);
				while (DateTime.UtcNow < Deadline && AheadDuringTheCook <= MutationJournal.UbtTimestampSlop)
				{
					AheadDuringTheCook = File.GetLastWriteTimeUtc(Box.HeaderFile) - File.GetLastWriteTimeUtc(ObjectFile(Box));
					Thread.Sleep(10);
				}
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Stamping(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, AheadDuringTheCook, "marked while the cook runs");
	}

	[TestMethod]
	public async Task ARunCancelledInTheCookHasMarkedItsEditsBeforeItLeavesTheLock()
	{
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		TimeSpan AheadAtRelease = default;
		FakeLock Lock = new();
		Lock.OnRelease = () => AheadAtRelease = File.GetLastWriteTimeUtc(Box.HeaderFile) - File.GetLastWriteTimeUtc(ObjectFile(Box));
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited during the build\n");
				Sandbox.Write(ObjectFile(Box), "from the old text");
				OnLine(BuildCompleted);
				OnLine("********** COOK COMMAND STARTED **********");
				throw new OperationCanceledException();
			},
		};
		// The wait outlasts the run, so only awaiting the stamp gets it done before the lock goes.
		UnrealServices Slow = Stamping(Runner, Lock, _ => Task.Delay(200));

		await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new TestCommand(Slow).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None));

		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, AheadAtRelease, "the next build must take the object as out of date");
		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		Assert.Contains("build again before you test", Box.Logger.Text);
		Assert.AreEqual(1, Lock.Released);
	}

	[TestMethod]
	public async Task ARunThatFailsInItsBuildStepMarksItsEditsToo()
	{
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited during the build\n");
				return RunsGame(null, 25)(Invocation, OnLine);
			},
		};

		Assert.AreNotEqual(0, await new TestCommand(Stamping(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None));

		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual(Box.HeaderFile, Result.RootElement.GetProperty("EditedDuringBuild").EnumerateArray().Single().GetString());
	}

	[TestMethod]
	[DataRow("Target is up to date")]
	[DataRow("Targets are up to date")]
	[DataRow("Log UnrealBuildTool: Targets are up to date")]
	public async Task ABuildStepThatCompiledNothingMarksItsEditsAndSaysNothing(string upToDate)
	{
		// As in `uak build`: UBT rewrites UHT's timestamps even then, but the game's binaries mix nothing.
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited\n");
				Sandbox.Write(ObjectFile(Box), "as the run's own Timestamp");
				OnLine(upToDate);
				OnLine(BuildCompleted);
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Stamping(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.HeaderFile) - File.GetLastWriteTimeUtc(ObjectFile(Box)));
		Assert.DoesNotContain("changed while UBT ran", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual(0, Result.RootElement.GetProperty("EditedDuringBuild").GetArrayLength());
		Assert.AreEqual(0, Result.RootElement.GetProperty("Problems").GetArrayLength());
	}

	[TestMethod]
	public async Task OneTargetUpToDateBesideOneThatCompilesStillMarks()
	{
		// UAT may run one UBT for each target.
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				OnLine("Target is up to date");
				OnLine("Using Unreal Build Accelerator local executor to run 3 action(s)");
				File.WriteAllText(Box.HeaderFile, "// edited\n");
				OnLine(BuildCompleted);
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Stamping(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AnEditBeforeUatsUbtStartsItsActionsIsAnEdit()
	{
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited\n");
				Thread.Sleep(50);
				OnLine("Using Unreal Build Accelerator local executor to run 3 action(s)");
				OnLine(BuildCompleted);
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Stamping(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AFileTheProjectNamesIsNoEditOfUatsBuild()
	{
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		ProjectSettings.SetBuildWrites(Box.Context(), [Path.GetRelativePath(Box.ProjectDirectory, Box.HeaderFile).Replace('\\', '/')]);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// generated by a pre-build step\n");
				OnLine("Using Unreal Build Accelerator local executor to run 3 action(s)");
				OnLine(BuildCompleted);
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Stamping(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.DoesNotContain("changed while UBT ran", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual(0, Result.RootElement.GetProperty("EditedDuringBuild").GetArrayLength());
	}

	[TestMethod]
	[DataRow("Using Unreal Build Accelerator local executor to run 7 action(s)")]
	[DataRow("Using Parallel executor to run 12 action(s)")]
	[DataRow("------ Building 7 action(s) started ------")]
	[DataRow("Building 7 actions with 16 processes...")]
	[DataRow("Building 1 action with 16 processes...")]
	public void EachWayUbtSaysItRunsActionsCounts(string line)
	{
		UatOutputParser Uat = new();
		Uat.AddLine(BuildStarted);

		Uat.AddLine(line);

		Assert.IsTrue(Uat.Summary.UbtRanActions);
	}

	[TestMethod]
	[DataRow("Building GameEditor...")]
	[DataRow("Building GameEditor and Game...")]
	[DataRow("Target is up to date")]
	[DataRow("Total execution time: 1.23 seconds")]
	public void OtherLinesSayNoActionsRan(string line)
	{
		UatOutputParser Uat = new();
		Uat.AddLine(BuildStarted);

		Uat.AddLine(line);

		Assert.IsFalse(Uat.Summary.UbtRanActions);
	}

	[TestMethod]
	public async Task TheBuildStepsEndIsTheFileSystemsTimeNotTheClocks()
	{
		// A file server ahead of this machine dates an edit made during the build past the local clock.
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		DateTime Server = DateTime.UtcNow.AddMinutes(5);
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.SetLastWriteTimeUtc(Box.HeaderFile, Server);
				OnLine(BuildCompleted);
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};
		UnrealServices Ahead = Stamping(Runner, probe: Path => Path.EndsWith("uak-build-end", StringComparison.Ordinal) ? Server.AddSeconds(1) : MutationJournal.ProbeFileTime(Path));

		Assert.AreEqual(0, await new TestCommand(Ahead).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	[DataRow(false, DisplayName = "a build that compiled")]
	[DataRow(true, DisplayName = "a build that compiled nothing")]
	public async Task AnEditThatCouldNotBeMarkedIsKeptForTheEditorAndLeavesTheExitCode(bool upToDate)
	{
		// UAT's build makes the editor's binaries too, which `uak test` runs, and no later build picks the file up (K5-F-2), even
		// behind a Timestamp a build that compiled nothing wrote (K5-F-3).
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited\n");
				if (upToDate)
				{
					OnLine("Target is up to date");
				}
				OnLine(BuildCompleted);
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Stamping(Runner, setWriteTime: (_, _) => { })).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		using (JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile)))
		{
			Assert.AreEqual(Box.HeaderFile, Result.RootElement.GetProperty("EditNotMarked").EnumerateArray().Single().GetString());
			Assert.AreEqual(Box.HeaderFile, Result.RootElement.GetProperty("EditedDuringBuild").EnumerateArray().Single().GetString());
		}
		Assert.Contains($"problem: {Box.HeaderFile} could not be marked for a recompile, so no later build picks it up: that is kept for GameEditor Win64 Development", Box.Logger.Text);
		Assert.Contains("-skipbuild tests whatever was staged", Box.Logger.Text);
		RebuildNeededEntry Kept = RebuildNeededFile.Read(Box.Context()).Single();
		Assert.IsTrue(Kept.Is("GameEditor", "Win64", "Development"), "the editor target's entry, which a whole `uak build` of it clears; no cooked one");
		CollectionAssert.AreEqual(new[] { Box.HeaderFile }, Kept.NotMarked);
		Assert.IsEmpty(Kept.Stamped);
		Assert.IsTrue(Kept.NotMarkedWritten.ContainsKey(Box.HeaderFile), "a save after this run is the way out");

		// It refuses what runs on those binaries, cooked runs too.
		Assert.ThrowsExactly<UakRefusalException>(() => RebuildNeededFile.ThrowIfStanding(Box.Context()));
		FakeProcessRunner Next = new() { Behaviour = RunsGame("cooked-passed.log", 0, RunOutput) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Stamping(Next)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);
		Assert.IsEmpty(Next.Invocations);
	}

	[TestMethod]
	public async Task AStampedEditBesideOneItCouldNotMarkIsNotKept()
	{
		// Only what no later build picks up is kept for the editor: the stamped file recompiles at the next build (K5-F-2).
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited\n");
				File.WriteAllText(Box.SourceFile, "// edited\n");
				OnLine(BuildCompleted);
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};
		void MarksAllButTheHeader(string File, DateTime Time)
		{
			if (File != Box.HeaderFile)
			{
				System.IO.File.SetLastWriteTimeUtc(File, Time);
			}
		}

		Assert.AreEqual(0, await new TestCommand(Stamping(Runner, setWriteTime: MarksAllButTheHeader)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		RebuildNeededEntry Kept = RebuildNeededFile.Read(Box.Context()).Single();
		CollectionAssert.AreEqual(new[] { Box.HeaderFile }, Kept.NotMarked);
		Assert.IsEmpty(Kept.Stamped, "a stamped file stays a note");
	}

	[TestMethod]
	public async Task AStampedEditOfACookedRunIsOnlyANote()
	{
		// The next build recompiles it, as after `uak build`.
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited\n");
				OnLine(BuildCompleted);
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Stamping(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		Assert.IsFalse(File.Exists(Path.Combine(Box.StateDirectory, RebuildNeeded.FileName)));
	}

	[TestMethod]
	public async Task ARunCancelledAfterItsBuildKeepsAnEditItCouldNotMark()
	{
		using Sandbox Box = WithRunUat();
		Age(Box.HeaderFile, Box.SourceFile);
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				OnLine(BuildStarted);
				File.WriteAllText(Box.HeaderFile, "// edited during the build\n");
				OnLine(BuildCompleted);
				OnLine("********** COOK COMMAND STARTED **********");
				throw new OperationCanceledException();
			},
		};

		await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new TestCommand(Stamping(Runner, setWriteTime: (_, _) => { })).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None));

		CollectionAssert.AreEqual(new[] { Box.HeaderFile }, RebuildNeededFile.Read(Box.Context()).Single().NotMarked);
	}

	[TestMethod]
	public async Task ARunThatDoesNotBuildLooksForNoEdits()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, OnLine) =>
			{
				File.WriteAllText(Box.HeaderFile, "// edited\n");
				return RunsGame("cooked-passed.log", 0, RunOutput)(Invocation, OnLine);
			},
		};

		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-skipbuild", "-skipcook"], CancellationToken.None), Box.Logger.Text);

		Assert.DoesNotContain("changed while UBT ran", Box.Logger.Text);
	}
}
