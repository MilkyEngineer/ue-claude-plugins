// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Unreal.Tests;

/// <summary>
/// A mutation's rebuild that fails on another agent's half-edited file: everyone stays refused, as the binaries may hold the
/// mutant, but they are told why and what to do, and the build is tried again when something changed.
/// </summary>
public sealed partial class MutateTests
{
	const int Line = 12;

	static string OtherFile(Sandbox box) => Path.Combine(box.ProjectDirectory, "Source", "Game", "Private", "Other.cpp");

	/// <summary>
	/// Saves Other.cpp, the change a retry fires on, before that retry, and waits until a build's window would open after it. A file
	/// time is a tick of the file system's clock, and a window opens by that clock and counts its own first instant
	/// (<see cref="BuildEdits.Start"/>), so a save just before the retry can share that tick and count as an edit during it.
	/// </summary>
	static void SaveOtherFile(Sandbox box)
	{
		string Other = OtherFile(box);
		File.WriteAllText(Other, "the whole edit");
		DateTime Written = File.GetLastWriteTimeUtc(Other);
		UnrealServices Clocks = Services(new FakeProcessRunner());
		Assert.IsTrue(SpinWait.SpinUntil(() => BuildEdits.Start(box.Context(), Clocks) > Written, TimeSpan.FromSeconds(10)), "no window opened after the save");
	}

	/// <summary>What UBT prints for a build that stops on <paramref name="file"/>.</summary>
	static string[] FailsOn(string file) => [$"[1/2] Compile [x64] {Path.GetFileName(file)}", $"{file}({Line}): error C2065: 'Half': undeclared identifier"];

	/// <summary>Leaves a run <paramref name="name"/> whose rebuild failed on Other.cpp, which is older than that rebuild; and returns the mutant binary.</summary>
	static async Task<string> LeavePendingAsync(Sandbox box, string patch, string name, Action? duringRebuild = null, string[]? rebuildLines = null)
	{
		string Dll = WriteReceipt(box);
		string Other = Sandbox.Write(OtherFile(box), "half an edit");
		File.SetLastWriteTimeUtc(Other, DateTime.UtcNow.AddMinutes(-5));
		string[] Rebuild(int Build)
		{
			if (Build == 0)
			{
				File.WriteAllText(Dll, "mutant");
				return CompileLines;
			}
			duringRebuild?.Invoke();
			return rebuildLines ?? FailsOn(Other);
		}
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 1], buildOutput: Rebuild) };
		Assert.AreEqual(MutateCommand.SurvivedExitCode, await new MutateCommand(Services(Runner)).RunAsync(box.Context(), ["-name=" + name, "-patch=" + patch, "-filter=Project.Math"], CancellationToken.None), box.Logger.Text);
		return Dll;
	}

	static int Builds(FakeProcessRunner runner) => runner.Invocations.Count(IsBuild);

	[TestMethod]
	public async Task TheRefusalNamesTheFileTheRunAndTheNextCommand()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P1");
		string Other = OtherFile(Box);

		MutationRebuildFailure Failure = Record(Box, "P1").RebuildFailure!;
		Assert.AreEqual(Other, Failure.File);
		Assert.AreEqual(Line, Failure.Line);
		Assert.Contains("'Half': undeclared identifier", Failure.Message!);
		Assert.Contains($"the rebuild after failed in {Other}({Line}) (its errors are above", Box.Logger.Text);
		Assert.Contains("retry that build themselves once a source file changes", Box.Logger.Text);

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));

		string Refusal = Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context())).Message;
		Assert.Contains($"the mutation run 'P1' (PID {Environment.ProcessId}, started ", Refusal);
		Assert.Contains($"the last rebuild failed in {Other}({Line}), which this patch doesn't touch: another agent's edit, most likely", Refusal);
		Assert.Contains("'Half': undeclared identifier", Refusal);
		Assert.Contains("; log ", Refusal);
		Assert.Contains($"once {Other} builds again, the next `uak build` of GameEditor (Win64 Development) that shows a compile of", Refusal.Replace("a `uak build`", "the next `uak build`", StringComparison.Ordinal));
		Assert.Contains("`uak mutate -restore -name=P1`", Refusal);
	}

	[TestMethod]
	public async Task StatusAndRestoreNameTheFileToo()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P2");
		string Mark = $"the last rebuild failed in {OtherFile(Box)}({Line})";

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => FailsOn(OtherFile(Box))) };
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-status"], CancellationToken.None), Box.Logger.Text);
		int Seen = Box.Logger.Text.Split(Mark).Length;
		Assert.AreEqual(2, Seen, Box.Logger.Text);

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=P2"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("restore: 'P2': ", Box.Logger.Text);
		Assert.IsGreaterThan(Seen, Box.Logger.Text.Split(Mark).Length, Box.Logger.Text);
	}

	[TestMethod]
	public async Task AFailureInTheFileOfThePatchSaysSo()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 1], buildOutput: Build => Build == 0 ? CompileLines : FailsOn(Box.SourceFile)) };
		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=P3", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		string Refusal = Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context())).Message;

		Assert.Contains($"failed in {Box.SourceFile}({Line}), a file of this patch", Refusal);
	}

	[TestMethod]
	public void AJournalFromBeforeTheseFieldsReadsAsItDid()
	{
		using Sandbox Box = new();
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "Old", "p.diff", [Box.SourceFile]);
		Journal.MarkApplied();
		Journal.Record.Phase = MutationPhase.Restored;
		Journal.Record.RecompileStamp = DateTime.UtcNow.AddHours(1);
		Journal.Record.BuildTarget = "GameEditor";
		Journal.Record.BuildPlatform = "Win64";
		Journal.Record.BuildConfiguration = "Development";
		Journal.Record.Pid = int.MaxValue;
		StateFiles.WriteJson(JournalFile(Box, "Old"), Journal.Record);

		string Refusal = Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context())).Message;

		Assert.Contains("the mutation run 'Old' (PID", Refusal);
		Assert.Contains("no build since has recompiled them and succeeded, so", Refusal);
		Assert.DoesNotContain("once ", Refusal);
	}

	[TestMethod]
	public async Task AnEditToAnotherFileBringsTheRetryThatClearsIt()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "P4");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => { File.WriteAllText(Dll, "rebuilt"); return CompileLines; }) };
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(1, Builds(Later));
		Assert.IsFalse(File.Exists(JournalFile(Box, "P4")));
		Assert.Contains("retrying it, as a `uak build` of GameEditor would", Box.Logger.Text);
		CollectionAssert.IsSubsetOf(new[] { "GameEditor", "Win64", "Development" }, Later.Invocations.First(IsBuild).Arguments.ToList(), "the build its mutant was built with");
	}

	[TestMethod]
	public async Task AnEditDuringTheFailedRebuildCountsAsAChange()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "P5", () => File.WriteAllText(OtherFile(Box), "the whole edit"));

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => { File.WriteAllText(Dll, "rebuilt"); return CompileLines; }) };
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(1, Builds(Later));
	}

	[TestMethod]
	public async Task NothingChangedNothingIsTried()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P6");

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Builds(Later), "the same tree would fail the same way");
		Assert.Contains("no source file changed since", Box.Logger.Text);
	}

	[TestMethod]
	public async Task ARetryThatFailsRefusesAndRecordsItsOwnFailure()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P7");
		DateTime FirstFailure = Record(Box, "P7").RebuildFailure!.Started;

		string Third = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Third.cpp"), "half of another edit");
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => FailsOn(Third)) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(1, Builds(Later));
		Assert.HasCount(1, Later.Invocations, "no test runs");
		MutationRebuildFailure Failure = Record(Box, "P7").RebuildFailure!;
		Assert.AreEqual(Third, Failure.File);
		Assert.IsGreaterThanOrEqualTo(FirstFailure, Failure.Started);

		// Nothing changed since that one, so the next command doesn't try again.
		File.SetLastWriteTimeUtc(Third, DateTime.UtcNow.AddMinutes(-5));
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(1, Builds(Later));
	}

	[TestMethod]
	public async Task ARetryThatCompilesNothingClearsNothing()
	{
		// The guard's own case: a build can succeed and leave the mutant's objects where they are.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P8");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => UpToDate) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(1, Builds(Later));
		Assert.HasCount(1, Later.Invocations, "no test runs");
		Assert.IsTrue(File.Exists(JournalFile(Box, "P8")));
		Assert.IsNull(Record(Box, "P8").RebuildFailure, "that build didn't fail, and another try would show the same");
	}

	[TestMethod]
	public async Task AQuietRunNeverBuilds()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P9");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math", "-quiet"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Builds(Later), "a run that measures must not load the machine first");
	}

	[TestMethod]
	public async Task ACommandADeadHolderLeftRunningStopsTheRetry()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P10");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		FakeLock Orphaned = new() { OrphanedCommand = "uak build (PID 1)" };
		Assert.AreEqual(2, await new TestCommand(Services(Later, Orphaned)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Builds(Later), "that command may be building too");
	}

	[TestMethod]
	public async Task ARebuildThatShowedNoCompileIsNotRetried()
	{
		// It didn't fail on anyone's file, and what would clear it is a build that compiles the patch's files.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 0], buildOutput: MutantThen(Dll, UpToDate)) };
		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=P11", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);
		Assert.IsNull(Record(Box, "P11").RebuildFailure);

		Sandbox.Write(OtherFile(Box), "an edit");
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Builds(Later));
	}

	[TestMethod]
	public async Task AnotherJournalInTheWayStopsTheRetry()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P12");
		string Second = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Second.cpp");
		Sandbox.Write(Second, "second");
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "Stopped", "p.diff", [Second]);
		File.WriteAllText(Second, "a mutant");
		Journal.MarkApplied();
		Journal.Record.Pid = int.MaxValue;
		Journal.Record.ProcessStart = null;
		StateFiles.WriteJson(JournalFile(Box, "Stopped"), Journal.Record);

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Builds(Later), "a patch still in the tree must not be built");
	}

	[TestMethod]
	public async Task AnUnreadableJournalStopsTheRetry()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P13");
		Sandbox.Write(JournalFile(Box, "Broken"), "{ not json");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Builds(Later));
	}

	[TestMethod]
	public async Task AJournalOfAnotherPlatformIsLeftForItsOwnHost()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P14");
		MutationRecord Changed = Record(Box, "P14");
		Changed.BuildPlatform = "Linux";
		StateFiles.WriteJson(JournalFile(Box, "P14"), Changed);

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Builds(Later));
	}

	[TestMethod]
	public async Task AMutationRetriesBeforeItStarts()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "P15");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0, 0, 0], buildOutput: _ => { File.WriteAllText(Dll, "built"); return CompileLines; }) };
		Assert.AreEqual(MutateCommand.SurvivedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-name=P16", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "P15")));
		Assert.AreEqual(3, Builds(Later), "the retry, the mutant, the rebuild");
	}

	[TestMethod]
	public async Task ACookedRunRetriesBeforeUat()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		Sandbox.Write(Box.Layout.RunUatScript, "");
		string Dll = await LeavePendingAsync(Box, Patch, "P17");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new()
		{
			Behaviour = (Invocation, Output) =>
			{
				if (Invocation.FileName != Box.Layout.RunUatScript)
				{
					File.WriteAllText(Dll, "rebuilt");
					Array.ForEach(CompileLines, Output);
					return 0;
				}
				File.WriteAllLines(CookedTestCommandLine.ClientLogFile(Invocation.Environment![CookedTestCommandLine.LogFolderVariable]!), Fixtures.Lines("cooked-passed.log"));
				return 0;
			},
		};
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "P17")));
		Assert.HasCount(2, Later.Invocations);
		Assert.AreEqual(Box.Layout.RunUatScript, Later.Invocations[1].FileName);
	}

	[TestMethod]
	public async Task ABuildThatFailsRecordsWhereAndOneOfAnotherTargetDoesNot()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P18");

		string Third = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Third.cpp"), "edit");
		FakeProcessRunner Failing = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => FailsOn(Third)) };
		Assert.AreEqual(1, await new BuildCommand(Services(Failing)).RunAsync(Box.Context(), ["-target=Game"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(OtherFile(Box), Record(Box, "P18").RebuildFailure!.File, "another target says nothing of this one's rebuild");

		Assert.AreEqual(1, await new BuildCommand(Services(Failing)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Third, Record(Box, "P18").RebuildFailure!.File);
	}

	[TestMethod]
	public async Task AJournalsOwnFilesAreNotEdits()
	{
		// Their stamp is in the future with the wait skipped, and the build clears the journal: neither makes them an edit.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P19");
		DateTime Stamped = File.GetLastWriteTimeUtc(Box.SourceFile);
		List<TimeSpan> Delays = [];

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => { File.WriteAllText(Binary(Box, "UnrealEditor-Game.dll"), "rebuilt"); return CompileLines; }) };
		Assert.AreEqual(0, await new BuildCommand(Services(Later, delays: Delays)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "P19")));
		Assert.AreEqual(Stamped, File.GetLastWriteTimeUtc(Box.SourceFile));
		Assert.DoesNotContain("changed while UBT ran", Box.Logger.Text);
		Assert.IsEmpty(Delays);
	}

	[TestMethod]
	public async Task AnEditPastAJournalsStampIsAnEdit()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "P20");
		MutationRecord Pending = Record(Box, "P20");

		File.SetLastWriteTimeUtc(Box.SourceFile, Pending.RecompileStamp!.Value.AddSeconds(5));

		Assert.Contains(Box.SourceFile, BuildEdits.Find(Box.Context(), BuildEdits.GetManaged(Box.Context()), Pending.RebuildFailure!.Started));
	}

	[TestMethod]
	public async Task AMutantsOwnEditIsNotAnEdit()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		List<int> Stamps = [];
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: _ =>
			{
				// As the build runs, with the patch in the file, on top of the journal's Applied phase.
				Stamps.Add(BuildEdits.Find(Box.Context(), BuildEdits.GetManaged(Box.Context()), DateTime.UtcNow.AddHours(-1)).Count(File => File == Box.SourceFile));
				return CompileLines;
			}),
		};

		Assert.AreEqual(MutateCommand.CaughtExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=P21", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Stamps.Sum(), "the journal's file, managed by it while the run lasts");
	}

	[TestMethod]
	[DoNotParallelize]
	public async Task AJournalSaysWhoseDetachedRunItIs()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		Environment.SetEnvironmentVariable(RunSummaries.RunNameVariable, "Nightly");
		Environment.SetEnvironmentVariable(RunSummaries.RunOwnerVariable, "E3");
		try
		{
			await LeavePendingAsync(Box, Patch, "P22");
		}
		finally
		{
			Environment.SetEnvironmentVariable(RunSummaries.RunNameVariable, null);
			Environment.SetEnvironmentVariable(RunSummaries.RunOwnerVariable, null);
		}

		Assert.AreEqual("run Nightly, owner E3", Record(Box, "P22").StartedBy);
		Assert.Contains($"the mutation run 'P22' (run Nightly, owner E3, PID {Environment.ProcessId}, started ", Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context())).Message);
	}

	// ---- PowerShell 5.1's split, through the commands ----

	[TestMethod]
	public async Task TheCommandsJoinASplitValue()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string List = Sandbox.Write(Path.Combine(Box.Root, "list.txt"), string.Join('\n', Enumerable.Range(0, 0)));

		// uak test -filter=Project.Math. -expect=list.txt
		FakeProcessRunner Testing = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		int Exit = await new TestCommand(Services(Testing)).RunAsync(Box.Context(), ["-filter=Project", ".Math", "-expect=" + List[..^4], ".txt"], CancellationToken.None);
		Assert.Contains(".txt", Box.Logger.Text, "the list, or a refusal of it, names the whole path");
		Assert.DoesNotContain("positional", Box.Logger.Text);
		Assert.AreEqual(2, Exit, "an empty list is a usage error");

		// uak mutate -patch=mutation.diff
		FakeProcessRunner Mutating = new() { Behaviour = Plays("test-failed.log", 255, [0, 0]) };
		Assert.AreEqual(MutateCommand.CaughtExitCode, await new MutateCommand(Services(Mutating)).RunAsync(Box.Context(), ["-name=D1", "-patch=" + Patch[..^5], ".diff", "-filter=Project", ".Math"], CancellationToken.None), Box.Logger.Text);

		// uak build -target=Game.x -- -Log=a.b
		FakeProcessRunner Building = new();
		await new BuildCommand(Services(Building)).RunAsync(Box.Context(), ["-target=Game", ".x", "--", "-Log=a", ".b"], CancellationToken.None);
		Assert.Contains("Game.x", Building.Invocations.Single().Arguments);
		Assert.Contains("-Log=a.b", Building.Invocations.Single().Arguments);
	}

	[TestMethod]
	public async Task AStrayDotArgumentIsRefusedWithItsCause()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new();

		Assert.AreEqual(2, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-gpu", ".txt", "-filter=Project.Math"], CancellationToken.None));
		StringAssert.Contains(Box.Logger.Text, "PowerShell");
		Assert.IsEmpty(Runner.Invocations);
	}

	[TestMethod]
	public async Task ARelativeExpectIsFoundInTheProject()
	{
		using Sandbox Box = new();
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Lists", "math.txt"), string.Join('\n', Fixtures.Lines("test-passed.log")
			.Select(Line => System.Text.RegularExpressions.Regex.Match(Line, @"Test Completed\. Result=\{Success\} Name=\{\w+\} Path=\{([^}]+)\}"))
			.Where(Match => Match.Success).Select(Match => Match.Groups[1].Value)));

		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math", "-expect=Lists/math.txt", "-allow-unexpected"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
	}

	[TestMethod]
	public async Task ARelativeExpectIsFoundForAMutationToo()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Lists", "math.txt"), "Project.Math.VectorAdd\n");

		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0, 0]) };
		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=E1", "-patch=" + Patch, "-filter=Project.Math", "-expect=Lists/math.txt"], CancellationToken.None);

		Assert.AreNotEqual(2, Exit, Box.Logger.Text);
		Assert.DoesNotContain("-expect file not found", Box.Logger.Text);
	}
}
