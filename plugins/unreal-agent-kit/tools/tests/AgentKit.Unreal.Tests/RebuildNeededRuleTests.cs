// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Unreal.Tests;

/// <summary>
/// The rule for what leaves the marker (<see cref="RebuildNeeded.WhatStays"/>), and what stands in for the marker when it can't
/// be written: a file leaves only on evidence, and nothing refuses less because something went wrong.
/// </summary>
public sealed partial class MutateTests
{
	static readonly DateTime Recorded = new(2026, 3, 1, 12, 0, 0, 500, DateTimeKind.Utc);

	static RebuildNeededEntry EntryOf(string file, DateTime? recorded, params string[] stamped)
	{
		RebuildNeededEntry Entry = new() { Target = "GameEditor", Platform = "Win64", Configuration = "Development", Stamped = [.. stamped], NotMarked = [file], Problems = ["could not be marked"] };
		if (recorded is DateTime Time)
		{
			Entry.NotMarkedWritten[file] = Time;
		}
		return Entry;
	}

	/// <summary>A build that writes <paramref name="file"/> while it runs, and compiles; <paramref name="failsOn"/> makes it fail on that file.</summary>
	static FakeProcessRunner EditsDuringBuild(string file, string? failsOn = null) => new()
	{
		Behaviour = Plays("test-passed.log", 0, [failsOn is null ? 0 : 1], buildOutput: _ =>
		{
			File.WriteAllText(file, "another agent's edit, during the build");
			return failsOn is null ? CompileLines : FailsOn(failsOn);
		}),
	};

	/// <summary>A file system that refuses the mark of one file.</summary>
	static Action<string, DateTime> NoMarkOf(string file) => (Path, Time) =>
	{
		if (Path != file)
		{
			File.SetLastWriteTimeUtc(Path, Time);
		}
	};

	/// <summary>A mark that moves the file's time and doesn't take: it lands on the reference, not past it by more than UBT's slop.</summary>
	static void MarkFallsShort(string file, DateTime stamp) => File.SetLastWriteTimeUtc(file, stamp - TimeSpan.FromSeconds(2));

	/// <summary>A mark that moves the file's time past the reference and doesn't take: within UBT's slop of it.</summary>
	static void MarkFallsJustShort(string file, DateTime stamp) => File.SetLastWriteTimeUtc(file, stamp - TimeSpan.FromMilliseconds(1500));

	/// <summary>Puts journal <paramref name="name"/>'s stamp in the past, as a real restore's wait does, which a fake delay doesn't: a relink is then past it.</summary>
	static void MoveStampBack(Sandbox box, string name)
	{
		MutationRecord Stopped = Record(box, name);
		Stopped.RecompileStamp = DateTime.UtcNow.AddMinutes(-1);
		StateFiles.WriteJson(JournalFile(box, name), Stopped);
	}

	/// <summary>
	/// A marker that can be read and not written: an empty one, made read-only, which Windows neither replaces nor deletes. A
	/// folder in its place can't be written either, but it can't be read, and a marker that can't be read refuses every command.
	/// Elsewhere a read-only file is replaced all the same, so the tests that need a marker no build can write, those of what the
	/// mutation journals hold in its place among them, run on Windows only: an accepted limit, as nothing else makes a marker
	/// readable and not writable for every system.
	/// </summary>
	static void BlockMarker(Sandbox box)
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("only Windows refuses to replace a read-only file");
		}
		Sandbox.Write(MarkerFile(box), "[]");
		File.SetAttributes(MarkerFile(box), FileAttributes.ReadOnly);
	}

	/// <summary>Undoes <see cref="BlockMarker"/>: no marker, as before it.</summary>
	static void UnblockMarker(Sandbox box)
	{
		File.SetAttributes(MarkerFile(box), FileAttributes.Normal);
		File.Delete(MarkerFile(box));
	}

	/// <summary>
	/// Saves <paramref name="file"/> as an agent would once the build that recorded its time has ended: its time gets past that
	/// record, to the millisecond the marker keeps, and then a build's window would open after it (as <see cref="SaveOtherFile"/>).
	/// </summary>
	static void SaveAfterTheRecord(Sandbox box, string file)
	{
		long Record = RebuildNeeded.Read(box.Context()).Written[file].Ticks / TimeSpan.TicksPerMillisecond;
		Assert.IsTrue(SpinWait.SpinUntil(() =>
		{
			File.WriteAllText(file, "saved after the build");
			return File.GetLastWriteTimeUtc(file).Ticks / TimeSpan.TicksPerMillisecond > Record;
		}, TimeSpan.FromSeconds(10)), "no save got past the record");
		DateTime Written = File.GetLastWriteTimeUtc(file);
		UnrealServices Clocks = Services(new FakeProcessRunner());
		Assert.IsTrue(SpinWait.SpinUntil(() => BuildEdits.Start(box.Context(), Clocks) > Written, TimeSpan.FromSeconds(10)), "no window opened after the save");
	}

	// ---- The rule ----

	[TestMethod]
	[DataRow(1000, 2000, false, DisplayName = "a second past the record, before the window")]
	[DataRow(1, 2000, false, DisplayName = "a millisecond past the record")]
	[DataRow(1000, 1000, true, DisplayName = "at the window")]
	[DataRow(1000, 500, true, DisplayName = "in the window")]
	[DataRow(0, 2000, true, DisplayName = "not moved")]
	[DataRow(-1000, 2000, true, DisplayName = "moved back")]
	public void ASavedFileLeavesOnlyIfItsTimeMovedPastTheRecordAndBeforeTheWindow(int movedMilliseconds, int openedMilliseconds, bool stays)
	{
		using Sandbox Box = new();
		string Third = Sandbox.Write(ThirdFile(Box), "saved");
		File.SetLastWriteTimeUtc(Third, Recorded.AddMilliseconds(movedMilliseconds));

		BuildEditReport Left = RebuildNeeded.WhatStays(EntryOf(Third, Recorded), Recorded.AddMilliseconds(openedMilliseconds));

		CollectionAssert.AreEqual(stays ? new[] { Third } : [], Left.NotMarked.ToArray());
	}

	[TestMethod]
	public void AFileNobodyTouchedDoesNotMoveByWhatTheMarkerRoundsOff()
	{
		// A marker keeps times to the millisecond, and a file keeps ticks.
		using Sandbox Box = new();
		string Third = Sandbox.Write(ThirdFile(Box), "as it was");
		File.SetLastWriteTimeUtc(Third, Recorded.AddTicks(1234));

		BuildEditReport Left = RebuildNeeded.WhatStays(EntryOf(Third, Recorded), Recorded.AddHours(1));

		CollectionAssert.AreEqual(new[] { Third }, Left.NotMarked.ToArray());
	}

	[TestMethod]
	public void AFileWithNoTimeShowsNoSave()
	{
		using Sandbox Box = new();
		string Third = Sandbox.Write(ThirdFile(Box), "saved");
		File.SetLastWriteTimeUtc(Third, Recorded);

		BuildEditReport Left = RebuildNeeded.WhatStays(EntryOf(Third, null), Recorded.AddHours(1));

		CollectionAssert.AreEqual(new[] { Third }, Left.NotMarked.ToArray());
		Assert.IsEmpty(Left.Written);
	}

	[TestMethod]
	public void OnlyAWholeCleanBuildTakesAFileOut()
	{
		using Sandbox Box = new();
		string Third = Sandbox.Write(ThirdFile(Box), "saved");
		string Other = Sandbox.Write(OtherFile(Box), "stamped");
		File.SetLastWriteTimeUtc(Third, Recorded.AddSeconds(1));
		RebuildNeededEntry Entry = EntryOf(Third, Recorded, Other);

		BuildEditReport Any = RebuildNeeded.WhatStays(Entry, null);

		CollectionAssert.AreEqual(new[] { Other }, Any.Stamped.ToArray());
		CollectionAssert.AreEqual(new[] { Third }, Any.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { "could not be marked" }, Any.Problems.ToArray());
		Assert.AreEqual(Recorded, Any.Written[Third]);

		BuildEditReport Clean = RebuildNeeded.WhatStays(Entry, Recorded.AddHours(1));

		Assert.IsEmpty(Clean.Stamped);
		Assert.IsEmpty(Clean.NotMarked);
	}

	[TestMethod]
	public void AFileThatIsGoneLeavesWithAnyBuild()
	{
		using Sandbox Box = new();
		string Third = ThirdFile(Box);
		string Other = OtherFile(Box);

		BuildEditReport Left = RebuildNeeded.WhatStays(EntryOf(Third, Recorded, Other), null);

		Assert.IsEmpty(Left.Overlapped);
		Assert.IsEmpty(Left.Problems);
		Assert.IsEmpty(Left.Written);
	}

	// ---- Carried through an overlapped build (K4-F-1) ----

	[TestMethod]
	public async Task AnOverlappedBuildThatSucceedsCarriesAFileThatCouldNotBeMarked()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Other = OldFile(OtherFile(Box));
		KeepForEditor(Box, NotMarkedAsItIs(Third));

		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Other))), Box.Logger.Text);

		BuildEditReport Standing = RebuildNeeded.Read(Box.Context());
		CollectionAssert.AreEqual(new[] { Third }, Standing.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { "could not be marked" }, Standing.Problems.ToArray());
		CollectionAssert.AreEqual(new[] { Other }, Standing.Stamped.ToArray());
		await AssertTestRefusedAsync(Box);

		// What was stamped goes with a clean build, which still shows nothing of the other.
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		Standing = RebuildNeeded.Read(Box.Context());
		CollectionAssert.AreEqual(new[] { Third }, Standing.NotMarked.ToArray());
		Assert.IsEmpty(Standing.Stamped);
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public async Task ATimeOutlivesAnOverlappedBuild()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Other = OldFile(OtherFile(Box));
		KeepForEditor(Box, NotMarkedAsItIs(Third));
		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Other))), Box.Logger.Text);

		File.SetLastWriteTimeUtc(Third, DateTime.UtcNow.AddMinutes(-1));
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)), Box.Logger.Text);
	}

	[TestMethod]
	public async Task ALaterRetryThatSucceedsKeepsWhatAnEarlierOneCouldNotMark()
	{
		// One retry fails beside a file it can't mark, the next succeeds beside another edit and clears the journal: the
		// mutation that asked for it must not go on, as its mutant build wouldn't recompile what uses the first file.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "E1");
		string Third = OldFile(ThirdFile(Box));
		string Fourth = OldFile(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Fourth.cpp"));
		Action<string, DateTime> NoMarkOfThird = NoMarkOf(Third);
		SaveOtherFile(Box);
		FakeProcessRunner Failing = new() { Behaviour = RetryFailsAndOverlaps(Box) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Failing, setWriteTime: NoMarkOfThird)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());

		FakeProcessRunner Later = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Fourth, "another agent's edit, during the retry");
				File.WriteAllText(Dll, "rebuilt");
				return CompileLines;
			}),
		};
		string ResultFile = Path.Combine(Box.Root, "result.json");
		int Exit = await new MutateCommand(Services(Later, setWriteTime: NoMarkOfThird)).RunAsync(Box.Context(), ["-name=E2", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(UakExitCodes.UsageError, Exit, Box.Logger.Text);
		Assert.AreEqual(1, Builds(Later), "the retry, and no mutant build");
		Assert.DoesNotContain("return A - B;", File.ReadAllText(Box.SourceFile));
		Assert.IsFalse(File.Exists(JournalFile(Box, "E1")), "the retry showed what clears it");
		using JsonDocument Result = ReadResult(ResultFile);
		CollectionAssert.Contains(Listed(Result, "EditNotMarked"), Third);
	}

	// ---- A compiled build that says it is up to date (K4-F-5) ----

	[TestMethod]
	public async Task UpToDateBesideActionsIsABuildThatCompiled()
	{
		// A pre-build step's own UBT prints it, and the build goes on to compile.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Other = OldFile(OtherFile(Box));
		KeepForEditor(Box, new BuildEditReport([Other], [], []));
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Third, "another agent's edit, during the build");
				return ["Target is up to date", .. CompileLines, "Result: Succeeded"];
			}),
		};

		Assert.AreEqual(0, await BuildAsync(Box, Services(Runner)), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		CollectionAssert.AreEquivalent(new[] { Other, Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray(), "an overlapped build clears nothing");
	}

	// ---- An overlap the marker can't take (K4-F-2) ----

	[TestMethod]
	public async Task AJournalStandsForAnOverlapTheMarkerCouldNotTakeEvenOnceRelinked()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "F1");
		string Third = OldFile(ThirdFile(Box));
		SaveOtherFile(Box);
		BlockMarker(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("The mutation journals stay", Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, Record(Box, "F1").UnkeptOverlap!.Single().Stamped.ToArray());

		// The relink left no binary with the patch, and that isn't what the journal stands for now.
		MoveStampBack(Box, "F1");
		Assert.IsFalse(MutationJournal.List(Box.Context()).Journals.Single().IsSpent());
		UakUsageException Refused = Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context()));
		Assert.Contains(Third, Refused.Message);
		Assert.Contains("hands them over", Refused.Message);

		// A clean retry that still can't write the marker refuses as well: all it can hand over is still in the journal.
		FakeProcessRunner Again = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Dll, "rebuilt");
				return CompileLines;
			}),
		};
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Again)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.HasCount(1, Again.Invocations, "the retry, and no test");
		CollectionAssert.AreEqual(new[] { Third }, Record(Box, "F1").UnkeptOverlap!.Single().Stamped.ToArray());

		// The first build that can write the marker hands it over, and only then can the journal go.
		UnblockMarker(Box);
		FakeProcessRunner Failing = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => FailsOn(OtherFile(Box))) };
		Assert.AreEqual(1, await BuildAsync(Box, Services(Failing)), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "F1")));
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray());
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public async Task ABuildThatCanWriteTheMarkerHandsOverAndClearsBoth()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "F2");
		string Third = OldFile(ThirdFile(Box));
		SaveOtherFile(Box);
		BlockMarker(Box);
		await new TestCommand(Services(new FakeProcessRunner { Behaviour = RetryOverlaps(Box, Dll) })).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None);
		Assert.IsNotNull(Record(Box, "F2").UnkeptOverlap);
		UnblockMarker(Box);

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		// Stamped before this clean build, so the rule lets it go with the journal.
		Assert.IsFalse(File.Exists(JournalFile(Box, "F2")), Box.Logger.Text);
		Assert.IsFalse(File.Exists(MarkerFile(Box)), Box.Logger.Text);
		await AssertTestRunsAsync(Box);
	}

	/// <summary>A check whose rebuild overlaps Third.cpp beside a marker that can't be written, with a real wait, so the relink is past the stamp and the journal would be spent.</summary>
	static async Task<string> LeaveHeldByTheRestoreAsync(Sandbox box, string patch, string name)
	{
		string Dll = WriteReceipt(box);
		string Third = OldFile(ThirdFile(box));
		BlockMarker(box);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0, 0], buildOutput: Build =>
			{
				if (Build == 1)
				{
					File.WriteAllText(Third, "another agent's edit, during the rebuild");
				}
				File.WriteAllText(Dll, Build == 0 ? "mutant" : "rebuilt");
				return CompileLines;
			}),
		};
		await new MutateCommand(Services(Runner, realDelay: true)).RunAsync(box.Context(), ["-name=" + name, "-patch=" + patch, "-filter=Project.Math"], CancellationToken.None);
		return Third;
	}

	[TestMethod]
	public async Task TheRestoresJournalStandsForAnOverlapTheMarkerCouldNotTake()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = await LeaveHeldByTheRestoreAsync(Box, Patch, "F3");

		CollectionAssert.AreEqual(new[] { Third }, Record(Box, "F3").UnkeptOverlap!.Single().Stamped.ToArray(), Box.Logger.Text);
		Assert.Contains("BINARIES PENDING: the files are back and no binary holds the mutation", Box.Logger.Text);
		Assert.DoesNotContain("shows no compile", Box.Logger.Text);

		// The next command doesn't take it for spent.
		UnblockMarker(Box);
		await AssertTestRefusedAsync(Box);
		Assert.IsTrue(File.Exists(JournalFile(Box, "F3")));

		// -restore hands it over once the marker can take it.
		Assert.AreEqual(0, await new MutateCommand(Services(BuildsAndCompiles())).RunAsync(Box.Context(), ["-restore", "-name=F3"], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "F3")));
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray());
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public async Task ForceSaysWhatItSetAside()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = await LeaveHeldByTheRestoreAsync(Box, Patch, "F4");

		Assert.AreEqual(0, await new MutateCommand(Services(BuildsAndCompiles())).RunAsync(Box.Context(), ["-restore", "-name=F4", "-force"], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "F4")));
		string Line = Box.Logger.Text.Split('\n').Single(Text => Text.Contains("-force sets that aside too", StringComparison.Ordinal));
		Assert.Contains(Third, Line);
		Assert.Contains("nothing remembers it now", Line);
		Assert.DoesNotContain("no compile", Line);
	}

	[TestMethod]
	public async Task ForceHandsOverWhatTheMarkerCanTake()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = await LeaveHeldByTheRestoreAsync(Box, Patch, "F5");
		UnblockMarker(Box);

		Assert.AreEqual(0, await new MutateCommand(Services(BuildsAndCompiles())).RunAsync(Box.Context(), ["-restore", "-name=F5", "-force"], CancellationToken.None), Box.Logger.Text);

		Assert.DoesNotContain("-force sets that aside too", Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray());
	}

	[TestMethod]
	public async Task TheRunsJournalStandsForAnOverlapOfItsMutantBuild()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = OldFile(ThirdFile(Box));
		BlockMarker(Box);
		Action<string, DateTime> NoMarkOfThird = NoMarkOf(Third);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0, 0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Third, "another agent's edit, during the mutant build");
				}
				return CompileLines;
			}),
		};

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, await new MutateCommand(Services(Runner, setWriteTime: NoMarkOfThird)).RunAsync(Box.Context(), ["-name=F6", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, Record(Box, "F6").UnkeptOverlap!.Single().NotMarked.ToArray(), Box.Logger.Text);
	}

	[TestMethod]
	public async Task SavingAFileAJournalHandedOverIsTheWayOut()
	{
		// The time goes with the file, or the marker has nothing to show a save by.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "F7");
		string Third = OldFile(ThirdFile(Box));
		MutationJournal Journal = MutationJournal.List(Box.Context()).Journals.Single();
		RebuildNeeded.HoldInJournal(Box.Context(), Journal, RebuildNeeded.EntryOf("GameEditor", "Win64", "Development", NotMarkedAsItIs(Third)));
		Assert.IsTrue(RebuildNeeded.KeepWhatItHolds(Box.Context(), Journal));

		File.SetLastWriteTimeUtc(Third, DateTime.UtcNow.AddMinutes(-1));
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)), Box.Logger.Text);
	}

	[TestMethod]
	public async Task ABuildLeavesAJournalThatHoldsAnOverlapWhateverItShowed()
	{
		// What a project command calls, with no hand-over before it: the journal is all that remembers the overlap.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "F8");
		MutationJournal Journal = MutationJournal.List(Box.Context()).Journals.Single();
		RebuildNeeded.HoldInJournal(Box.Context(), Journal, RebuildNeeded.EntryOf("GameEditor", "Win64", "Development", new BuildEditReport(["A.h"], [], [])));

		Assert.IsEmpty(MutationJournal.ClearAfterBuild(Box.Context(), "GameEditor", "Win64", "Development", succeeded: true, _ => true));

		Assert.IsTrue(File.Exists(JournalFile(Box, "F8")));
		Assert.IsTrue(MutationJournal.List(Box.Context()).Journals.Single().HasRecompiled(), "what the build showed still counts");
	}

	[TestMethod]
	public async Task ABuildThatCouldNotHandOverClearsNoJournalAndSaysSo()
	{
		// The journal that holds the overlap would stay anyway. Nothing else goes on the word of a build that left it standing.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeaveHeldByTheRestoreAsync(Box, Patch, "F9");
		Assert.DoesNotContain("The mutation journals stay", Box.Logger.Text);
		MutationJournal Beside = MutationJournal.Begin(Box.Context(), "F10", "p.diff", [Box.SourceFile]);
		File.WriteAllText(Box.SourceFile, "mutant");
		Beside.MarkApplied();
		Assert.IsEmpty(Beside.Restore().Conflicts);
		Beside.SetBuild("GameEditor", "Win64", "Development");
		DateTime Artifact = DateTime.UtcNow;
		Beside.StampForRecompile(Artifact.AddSeconds(2), Artifact);

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		Assert.Contains("The mutation journals stay", Box.Logger.Text);
		Assert.IsTrue(File.Exists(JournalFile(Box, "F9")));
		Assert.IsTrue(File.Exists(JournalFile(Box, "F10")), "its build showed its files recompiled, and left an overlap it couldn't hand over");
	}

	[TestMethod]
	public async Task ARefusalForAnOverlapAloneDoesNotBlameTheBinaries()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = await LeaveHeldByTheRestoreAsync(Box, Patch, "F11");

		string Refused = Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context())).Message;

		Assert.Contains("no binary holds its patch", Refused);
		Assert.Contains(Third, Refused);
		Assert.DoesNotContain("may still hold", Refused);
	}

	[TestMethod]
	public async Task ARefusalAfterARetryThatRelinkedDoesNotBlameTheBinariesOrTheFailureBehindIt()
	{
		// The retry succeeded and relinked, and only its overlap stands: the failure that asked for it is the journal's last, not a reason.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "F12");
		OldFile(ThirdFile(Box));
		SaveOtherFile(Box);
		BlockMarker(Box);
		await new TestCommand(Services(new FakeProcessRunner { Behaviour = RetryOverlaps(Box, Dll) })).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None);
		MoveStampBack(Box, "F12");

		string Refused = Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context())).Message;

		Assert.Contains("no binary holds its patch", Refused);
		Assert.DoesNotContain("may still hold", Refused);
		Assert.DoesNotContain("builds again", Refused);
	}

	[TestMethod]
	public async Task AFailedRebuildThatCompiledItsFilesLeavesThePatchInTheBinaries()
	{
		// It stopped before the link, so what it compiled is out of the objects and not out of what the mutant build linked.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "F13");
		MutationJournal Journal = MutationJournal.List(Box.Context()).Journals.Single();
		Journal.NoteRecompiled(_ => true);
		RebuildNeeded.HoldInJournal(Box.Context(), Journal, RebuildNeeded.EntryOf("GameEditor", "Win64", "Development", new BuildEditReport(["A.h"], [], [])));

		Assert.IsTrue(Journal.HasRecompiled());
		Assert.IsFalse(Journal.WaitsOnlyOnUnkeptOverlap());
		Assert.Contains("may still hold", Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context())).Message);
	}

	[TestMethod]
	public async Task ARefusalBesideABinaryStillHeldNamesTheBinaryAndTheOverlap()
	{
		// Neither reason stands in for the other: a build takes the patch out of the binary, and only a hand-over lets the overlap go.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "F15");
		string Third = OldFile(ThirdFile(Box));
		MutationJournal Journal = MutationJournal.List(Box.Context()).Journals.Single();
		RebuildNeeded.HoldInJournal(Box.Context(), Journal, RebuildNeeded.EntryOf("GameEditor", "Win64", "Development", new BuildEditReport([Third], [], [])));

		string Refused = Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context())).Message;

		Assert.Contains($"{Dll} may still hold its patch", Refused);
		Assert.Contains(Third, Refused);
		Assert.Contains("hands them over", Refused);
	}

	[TestMethod]
	public async Task ARestoreThatCouldNotHandOverSaysSoAndNotThatNothingCompiled()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeaveHeldByTheRestoreAsync(Box, Patch, "F14");

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(BuildsAndCompiles())).RunAsync(Box.Context(), ["-restore", "-name=F14"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("what the journal holds in place of", Box.Logger.Text);
		Assert.DoesNotContain("shows no compile", Box.Logger.Text);
		Assert.IsTrue(File.Exists(JournalFile(Box, "F14")));
	}

	// ---- A file that can't be marked, and its time (K4-F-3) ----

	[TestMethod]
	public async Task AFailedMarkRecordsNoTimeBeforeTheBuildEnded()
	{
		// The edit's own time is older than what the build made of it, so only a write after the build shows a save: a time set
		// between the two is none (K5-F-8). The build's end is read as the earlier of the clock and the file system's, as
		// BuildEdits.Start reads a start: the record comes from the file system's clock, which NTFS keeps up to a tick behind.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		DateTime Ended = default;
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Third, "another agent's edit, during the build");
				Thread.Sleep(50);
				DateTime Clock = DateTime.UtcNow;
				Ended = MutationJournal.ProbeFileTime(Third) is DateTime Probed && Probed < Clock ? Probed : Clock;
				return CompileLines;
			}),
		};
		static long Milliseconds(DateTime Time) => Time.Ticks / TimeSpan.TicksPerMillisecond;

		Assert.AreEqual(0, await BuildAsync(Box, Services(Runner, setWriteTime: NoMark)), Box.Logger.Text);

		long Kept = Milliseconds(RebuildNeededFile.Read(Box.Context()).Single().NotMarkedWritten[Third]);
		Assert.IsGreaterThanOrEqualTo(Milliseconds(Ended), Kept, "not before the build ended");
		Assert.IsGreaterThanOrEqualTo(Milliseconds(File.GetLastWriteTimeUtc(Third)), Kept);

		File.SetLastWriteTimeUtc(Third, Ended.AddMilliseconds(-25));
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray(), "a time set before the build ended");

		// Nor does a file nobody saved look saved by what the marker rounded off.
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
	}

	[TestMethod]
	public async Task AMarkThatMovedTheTimeAndDidNotTakeIsNoSave()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Third), setWriteTime: MarkFallsShort)), Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
	}

	[TestMethod]
	public async Task AMarkThatMovedTheTimePastTheBuildsEndIsNoSave()
	{
		// The record is the later of the file's time and the build's end (K5-F-8): the end alone would take the mark's own move for a save.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Third), setWriteTime: MarkFallsJustShort)), Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
		DateTime Moved = File.GetLastWriteTimeUtc(Third);
		while (DateTime.UtcNow <= Moved.AddMilliseconds(50))
		{
			Thread.Sleep(20);
		}

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
	}

	[TestMethod]
	public async Task AMarkTriedAgainThatMovedTheTimeIsNoSaveEither()
	{
		// Each try records the time it leaves, or the next build would take its own mark's move for a save.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		KeepForEditor(Box, NotMarkedAsItIs(Third));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: MarkFallsShort)), Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
	}

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public async Task AMarkerOlderThanTheTimesLeavesAFileOnlyByAMarkOrByItBeingGone(bool marks)
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		Sandbox.Write(MarkerFile(Box), $$"""[{ "Target": "GameEditor", "Platform": "Win64", "Configuration": "Development", "Stamped": [], "NotMarked": [{{JsonSerializer.Serialize(Third)}}], "Problems": ["could not be marked"] }]""");
		File.SetLastWriteTimeUtc(Third, DateTime.UtcNow.AddMinutes(-1));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray(), "a save shows nothing with no time to compare");
		Assert.IsEmpty(RebuildNeededFile.Read(Box.Context()).Single().NotMarkedWritten, "and none is made up later");

		if (marks)
		{
			Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);
			CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray());
			Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);
		}
		else
		{
			File.Delete(Third);
			Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);
		}

		Assert.IsFalse(File.Exists(MarkerFile(Box)), Box.Logger.Text);
	}

	[TestMethod]
	public void TheRefusalNamesTheDeleteForAFileThatCanBeNeitherMarkedNorSaved()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		const string Clause = "or a file that can't be marked can't be saved either, no build can clear this: delete";
		KeepForEditor(Box, new BuildEditReport(["A.h"], [], []));
		Assert.DoesNotContain(Clause, RebuildNeeded.Read(Box.Context()).WayOut!);

		KeepForEditor(Box, NotMarkedAsItIs(Third));

		Assert.Contains($"{Clause} {MarkerFile(Box)}", RebuildNeeded.Read(Box.Context()).WayOut!);
	}

	[TestMethod]
	public async Task SavingAFileThatJoinedAnEntryIsTheWayOut()
	{
		// An entry stood when it couldn't be marked, so its time came through the merge with what stays.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Other = OldFile(OtherFile(Box));
		KeepForEditor(Box, new BuildEditReport([Other], [], []));
		KeepForEditor(Box, NotMarkedAsItIs(Third));

		File.SetLastWriteTimeUtc(Third, DateTime.UtcNow.AddMinutes(-1));
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)), Box.Logger.Text);
	}

	// ---- A build with nothing standing that couldn't mark its edit (K4-F-4) ----

	[TestMethod]
	public async Task AnOverlappedBuildThatCouldNotMarkItsEditLeavesTheRefusal()
	{
		// The partner of a stamped edit, which the next build recompiles: nothing picks this one up.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));

		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Third), setWriteTime: NoMark)), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public async Task AFailedBuildThatCouldNotMarkItsEditLeavesTheRefusal()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));

		Assert.AreEqual(1, await BuildAsync(Box, Services(EditsDuringBuild(Third, failsOn: Third), setWriteTime: NoMark)), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
	}

	[TestMethod]
	public async Task ABuildGivenArgumentsLeavesTheRefusalForAnEditItCouldNotMark()
	{
		// No later build picks the file up, whatever part of the target this one built (K5-F-1).
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));

		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Third), setWriteTime: NoMark), "--", "-Module=Game"), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
		await AssertTestRefusedAsync(Box);

		// Clearing still takes a whole build.
		SaveAfterTheRecord(Box, Third);
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark), "--", "-Module=Game"), Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);
		Assert.IsFalse(File.Exists(MarkerFile(Box)), Box.Logger.Text);
	}

	[TestMethod]
	public async Task ABuildGivenArgumentsLeavesNoRefusalForAnEditItMarked()
	{
		// The next build recompiles it, as after a whole build.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));

		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Third)), "--", "-Module=Game"), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		Assert.IsFalse(File.Exists(MarkerFile(Box)));
	}

	[TestMethod]
	public async Task AnOverlapNothingCanHoldIsSaidToBeRecordedNowhere()
	{
		// With no journal to stay, saying the journals stay would be false (K5-F-6).
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		BlockMarker(Box);

		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Third), setWriteTime: NoMark)), Box.Logger.Text);

		Assert.Contains("Could not record that a build overlapped an edit", Box.Logger.Text);
		Assert.Contains($"No mutation journal stands to hold it either, so it is recorded nowhere and nothing will refuse for it: {Third}", Box.Logger.Text);
		Assert.DoesNotContain("The mutation journals stay", Box.Logger.Text);
	}

	[TestMethod]
	public async Task TheMutantBuildLeavesTheRefusalForAnEditItCouldNotMark()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = OldFile(ThirdFile(Box));
		Action<string, DateTime> NoMarkOfThird = NoMarkOf(Third);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0, 0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Third, "another agent's edit, during the mutant build");
				}
				return CompileLines;
			}),
		};

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, await new MutateCommand(Services(Runner, setWriteTime: NoMarkOfThird)).RunAsync(Box.Context(), ["-name=G1", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray(), Box.Logger.Text);
		await AssertTestRefusedAsync(Box);
	}

	// ---- What a message says (K4-F-6, K4-F-8) ----

	[TestMethod]
	public void EveryRefusalEndsWithTheHintForAFileTheBuildWrites()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));

		Assert.Contains(BuildEdits.OwnWritesHint, new BuildEditReport(["A.h"], [], []).DescribeRetryOverlap(), "a retry's own");
		Assert.Contains(BuildEdits.OwnWritesHint, new BuildEditReport([], [Third], ["no mark"]).DescribeRetryOverlap(), "a retry's own that couldn't be marked");

		KeepForEditor(Box, new BuildEditReport([], [Third], ["no mark"]));
		Assert.Contains(BuildEdits.OwnWritesHint, RebuildNeeded.Read(Box.Context()).DescribeRetryOverlap());
		Assert.Contains(BuildEdits.OwnWritesHint, Assert.ThrowsExactly<UakRefusalException>(() => RebuildNeededFile.ThrowIfStanding(Box.Context())).Message);

		File.Delete(MarkerFile(Box));
		KeepForEditor(Box, new BuildEditReport(["A.h"], [], []));
		Assert.Contains(BuildEdits.OwnWritesHint, RebuildNeeded.Read(Box.Context()).DescribeRetryOverlap());
	}

	[TestMethod]
	public async Task AWarningForAFileThatCouldNotBeMarkedHasTheHintToo()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));

		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Third), setWriteTime: NoMark)), Box.Logger.Text);

		Assert.Contains($"build: {BuildEdits.OwnWritesHint}", Box.Logger.Text);
	}

	[TestMethod]
	public void ARefusalSaysADeletedFileHasNothingToSave()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Gone = OtherFile(Box);
		KeepForEditor(Box, new BuildEditReport([], [Third, Gone], ["no mark", "deleted"]) { Written = new Dictionary<string, DateTime> { [Third] = DateTime.UtcNow } });

		string Message = Assert.ThrowsExactly<UakRefusalException>(() => RebuildNeededFile.ThrowIfStanding(Box.Context())).Message;

		Assert.Contains($"not marked, so save first: {Third}; deleted, so nothing to save: {Gone}", Message);
		Assert.DoesNotContain($"not marked, so save first: {Third}, {Gone}", Message);
	}

	// ---- A marker that can't be read (K4-F-7) ----

	[TestMethod]
	public async Task AMarkerSomethingHoldsIsNotCorruptAndNoBuildClearsIt()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		KeepForEditor(Box, new BuildEditReport([Third], [], []));
		using (new FileStream(MarkerFile(Box), FileMode.Open, FileAccess.Read, FileShare.None))
		{
			Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);
			Assert.Contains("nothing in it is cleared", Box.Logger.Text);

			Assert.IsFalse(RebuildNeeded.Keep(Box.Context(), "GameEditor", "Win64", "Development", new BuildEditReport([Third], [], [])), "it can't tell what it would lose");

			UakUsageException Refused = Assert.ThrowsExactly<UakRefusalException>(() => RebuildNeededFile.ThrowIfStanding(Box.Context()));
			Assert.Contains("No build clears that", Refused.Message);
			Assert.DoesNotContain("A whole `uak build`", Refused.Message);
			await AssertTestRefusedAsync(Box);
		}

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray(), "nothing was cleared or written");
	}

	[TestMethod]
	[DataRow("-target=Game", DisplayName = "another target")]
	[DataRow("-config=DebugGame", DisplayName = "another configuration")]
	[DataRow("-platform=Linux", DisplayName = "another platform")]
	public async Task OnlyABuildOfTheTargetTestsUseClearsACorruptMarker(string other)
	{
		using Sandbox Box = new();
		Sandbox.Write(MarkerFile(Box), "{ not json");

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles()), other), Box.Logger.Text);

		Assert.IsTrue(File.Exists(MarkerFile(Box)));
		Assert.Contains("only a whole build of the target `uak test` uses clears it", Box.Logger.Text);
		await AssertTestRefusedAsync(Box);

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)));
	}

	[TestMethod]
	public async Task AFailedBuildLeavesACorruptMarker()
	{
		using Sandbox Box = new();
		Sandbox.Write(MarkerFile(Box), "{ not json");
		FakeProcessRunner Failing = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => FailsOn(OtherFile(Box))) };

		Assert.AreEqual(1, await BuildAsync(Box, Services(Failing)), Box.Logger.Text);

		Assert.IsTrue(File.Exists(MarkerFile(Box)));
	}

	[TestMethod]
	public void ACorruptMarkerIsNeverWrittenOver()
	{
		// What it held may stand: reading it as nothing would drop every other target's entry.
		using Sandbox Box = new();
		Sandbox.Write(MarkerFile(Box), "{ not json");

		Assert.IsFalse(RebuildNeeded.Keep(Box.Context(), "GameEditor", "Win64", "Development", new BuildEditReport(["A.h"], [], [])));

		Assert.AreEqual("{ not json", File.ReadAllText(MarkerFile(Box)));
		Assert.Contains("Could not record that a build overlapped an edit", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AMarkerThatFileExistsCanNotSeeStillRefuses()
	{
		// File.Exists says no for a path it can't read, as for a file on a share that didn't answer: that is no sign the marker is
		// absent (K5-F-5). A folder in its place shows it on any system.
		using Sandbox Box = new();
		Directory.CreateDirectory(MarkerFile(Box));
		Assert.IsFalse(File.Exists(MarkerFile(Box)));

		Assert.IsNotNull(RebuildNeededFile.Load(Box.Context()).ReadFailure);
		Assert.Contains("can't be read", Assert.ThrowsExactly<UakRefusalException>(() => RebuildNeededFile.ThrowIfStanding(Box.Context())).Message);
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public void AFileTheFileSystemCanNotReachStays()
	{
		// Only a file the file system says isn't there is gone: File.Exists says no for one it can't reach too (K5-F-5).
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("an ACL that denies reading a file's attributes is Windows'");
			return;
		}
		using Sandbox Box = new();
		string Third = Sandbox.Write(ThirdFile(Box), "out of reach");
		string Other = Sandbox.Write(OtherFile(Box), "stamped, out of reach");
		File.SetLastWriteTimeUtc(Third, Recorded.AddSeconds(1));
		SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
		FileSystemAccessRule NoAttributes = new(Me, FileSystemRights.ReadAttributes, AccessControlType.Deny);
		FileSystemAccessRule NoListing = new(Me, FileSystemRights.ListDirectory, AccessControlType.Deny);
		DirectoryInfo Folder = new(Path.GetDirectoryName(Third)!);
		void ChangeAcl(Action<FileSecurity> file, Action<DirectorySecurity> folder)
		{
			foreach (string Reached in new[] { Third, Other })
			{
				FileInfo Info = new(Reached);
				FileSecurity Security = Info.GetAccessControl();
				file(Security);
				Info.SetAccessControl(Security);
			}
			DirectorySecurity Listing = Folder.GetAccessControl();
			folder(Listing);
			Folder.SetAccessControl(Listing);
		}
		// Each rule goes on its own, the folder's first, so the files can be reached, and one that was never added (the deny failed
		// part way) or can't be taken off stops none of the others; a failure is reported once all have been tried.
		void RestoreAcl()
		{
			List<Exception> Failures = [];
			void Try(Action Undo)
			{
				try
				{
					Undo();
				}
				catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException or InvalidOperationException)
				{
					Failures.Add(Exception);
				}
			}
			Try(() =>
			{
				DirectorySecurity Listing = Folder.GetAccessControl();
				Listing.RemoveAccessRuleSpecific(NoListing);
				Folder.SetAccessControl(Listing);
			});
			foreach (string Reached in new[] { Third, Other })
			{
				Try(() =>
				{
					FileInfo Info = new(Reached);
					FileSecurity Security = Info.GetAccessControl();
					Security.RemoveAccessRuleSpecific(NoAttributes);
					Info.SetAccessControl(Security);
				});
			}
			if (Failures.Count > 0)
			{
				throw new AggregateException("the test's ACLs could not all be taken off", Failures);
			}
		}

		try
		{
			// Inside the try, so a deny that fails part way is taken off all the same.
			ChangeAcl(Security => Security.AddAccessRule(NoAttributes), Security => Security.AddAccessRule(NoListing));
			Assert.IsFalse(File.Exists(Third), "what the rule mustn't go by");

			BuildEditReport Any = RebuildNeeded.WhatStays(EntryOf(Third, Recorded, Other), null);
			BuildEditReport Clean = RebuildNeeded.WhatStays(EntryOf(Third, Recorded, Other), Recorded.AddHours(1));

			CollectionAssert.AreEqual(new[] { Other }, Any.Stamped.ToArray());
			CollectionAssert.AreEqual(new[] { Third }, Any.NotMarked.ToArray());
			CollectionAssert.AreEqual(new[] { Third }, Clean.NotMarked.ToArray(), "and a time that can't be read shows no save");
		}
		finally
		{
			RestoreAcl();
		}
	}

	[TestMethod]
	public void AMarkerThatCantBeReadIsNotWrittenOverEvenIfARenameWouldTakeIt()
	{
		// An ACL that denies reading keeps readers out and leaves a rename alone, as that asks the folder: the write would succeed, and
		// drop every entry. A held file can't show this: a rename onto a file anything has open fails, whatever the holder shares.
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("an ACL that denies reading a file is Windows'");
			return;
		}
		using Sandbox Box = new();
		RebuildNeeded.Keep(Box.Context(), "Game", "Win64", "Development", new BuildEditReport(["A.h"], [], []));
		string Before = File.ReadAllText(MarkerFile(Box));
		FileSystemAccessRule NoReading = new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
		void ChangeAcl(Action<FileSecurity> change)
		{
			FileInfo Marker = new(MarkerFile(Box));
			FileSecurity Security = Marker.GetAccessControl();
			change(Security);
			Marker.SetAccessControl(Security);
		}

		bool Kept;
		try
		{
			// Inside the try, so a deny that fails part way is taken off all the same; taking off a rule never added changes nothing.
			ChangeAcl(Security => Security.AddAccessRule(NoReading));
			Assert.IsNotNull(RebuildNeededFile.Load(Box.Context()).ReadFailure);
			Kept = RebuildNeeded.Keep(Box.Context(), "GameEditor", "Win64", "Development", new BuildEditReport(["B.h"], [], []));
		}
		finally
		{
			ChangeAcl(Security => Security.RemoveAccessRuleSpecific(NoReading));
		}

		Assert.IsFalse(Kept, "it can't tell what it would lose");
		Assert.AreEqual(Before, File.ReadAllText(MarkerFile(Box)));
		Assert.Contains("Could not record that a build overlapped an edit", Box.Logger.Text);
	}
}
