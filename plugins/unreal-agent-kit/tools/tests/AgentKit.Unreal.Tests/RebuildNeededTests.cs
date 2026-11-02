// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Unreal.Tests;

/// <summary>
/// What clears the marker a rebuild that overlapped an edit leaves, what mustn't, and in what order: nothing that refuses goes
/// until what replaces it is on disk, and a refusal says how to clear it.
/// </summary>
public sealed partial class MutateTests
{
	static readonly string[] UpToDateLines = ["Target is up to date", "Result: Succeeded"];

	/// <summary>The marker of the build a plain <c>uak build</c> makes.</summary>
	static void KeepForEditor(Sandbox box, BuildEditReport edits) => RebuildNeeded.Keep(box.Context(), "GameEditor", "Win64", "Development", edits);

	/// <summary>A file that couldn't be marked, as <see cref="BuildEdits.MarkAsync"/> reports it: with the time it was left with.</summary>
	static BuildEditReport NotMarkedAsItIs(string file) => new([], [file], ["could not be marked"]) { Written = new Dictionary<string, DateTime> { [file] = File.GetLastWriteTimeUtc(file) } };

	/// <summary>A file system that refuses every mark.</summary>
	static void NoMark(string _, DateTime __)
	{
	}

	static FakeProcessRunner BuildsUpToDate() => new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => UpToDateLines) };

	static FakeProcessRunner BuildsAndCompiles() => new() { Behaviour = Plays("test-passed.log", 0, [0]) };

	static Task<int> BuildAsync(Sandbox box, UnrealServices services, params string[] arguments) => new BuildCommand(services).RunAsync(box.Context(), arguments, CancellationToken.None);

	/// <summary>Whether a <c>uak test</c> runs, for the marker's sake: refused, it starts nothing.</summary>
	static async Task AssertTestRefusedAsync(Sandbox box)
	{
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Runner)).RunAsync(box.Context(), ["-filter=Project.Math"], CancellationToken.None), box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations, "nothing ran");
	}

	static async Task AssertTestRunsAsync(Sandbox box)
	{
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(box.Context(), ["-filter=Project.Math"], CancellationToken.None), box.Logger.Text);
		Assert.HasCount(1, Runner.Invocations);
	}

	// ---- A build that compiled nothing ----

	[TestMethod]
	public async Task AnUpToDateBuildKeepsAFileThatCouldNotBeMarked()
	{
		// Nothing made it compile that file: this build says nothing of what the binaries hold.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		KeepForEditor(Box, new BuildEditReport([], [Third], ["could not be marked"]));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsUpToDate(), setWriteTime: (_, _) => { })), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
		Assert.Contains("go on refusing", Box.Logger.Text);
		// It has no time to show a save by, so a save is no way out.
		Assert.Contains($"{Third} has no recorded time to show a save by, so saving clears nothing", Box.Logger.Text);
		Assert.Contains($"if none can, delete {MarkerFile(Box)}", Box.Logger.Text);
		Assert.DoesNotContain($"save {Third}, then run a whole build", Box.Logger.Text);
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public async Task SavingAFileThatCouldNotBeMarkedIsTheWayOut()
	{
		// On a share or an ACL no mark ever takes, so nothing but the save can clear it.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		KeepForEditor(Box, NotMarkedAsItIs(Third));
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsUpToDate(), setWriteTime: NoMark)), Box.Logger.Text);
		Assert.Contains($"save {Third}, then run a whole build", Box.Logger.Text);
		await AssertTestRefusedAsync(Box);

		SaveAfterTheRecord(Box, Third);
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)));
		await AssertTestRunsAsync(Box);
	}

	[TestMethod]
	public async Task ABuildThatCompiledSomethingElseKeepsAFileThatCouldNotBeMarked()
	{
		// It compiled another file, which says nothing of this one: the binaries may still lack it.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		KeepForEditor(Box, new BuildEditReport([], [Third], ["could not be marked"]));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: (_, _) => { })), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
		Assert.Contains("go on refusing", Box.Logger.Text);
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public async Task AnUpToDateBuildMarksAgainAndTheBuildAfterItClears()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		DateTime Before = File.GetLastWriteTimeUtc(Third);
		string Earlier = OldFile(OtherFile(Box));
		KeepForEditor(Box, new BuildEditReport([Earlier], [Third], ["could not be marked"]));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsUpToDate())), Box.Logger.Text);

		Assert.IsGreaterThan(Before, File.GetLastWriteTimeUtc(Third), "the next build compiles it");
		BuildEditReport Standing = RebuildNeeded.Read(Box.Context());
		Assert.IsEmpty(Standing.NotMarked);
		CollectionAssert.AreEqual(new[] { Third }, Standing.Stamped.ToArray(), "what was stamped before this build leaves with it");
		Assert.Contains("compiles it and clears the refusal", Box.Logger.Text);
		await AssertTestRefusedAsync(Box);

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)));
	}

	[TestMethod]
	public async Task OnlyTheMarksThatTakeMoveAndAFileThatIsGoneGoes()
	{
		using Sandbox Box = new();
		UakContext Context = Box.Context();
		string Third = OldFile(ThirdFile(Box));
		string Other = OldFile(OtherFile(Box));
		string Gone = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Gone.cpp");
		KeepForEditor(Box, new BuildEditReport([], [Third, Other, Gone], ["one", "two", "three"]));
		RebuildNeeded.Keep(Context, "Game", "Win64", "Development", new BuildEditReport(["Game.h"], [], []));
		UnrealServices Marks = Services(BuildsUpToDate(), setWriteTime: (Path, Time) =>
		{
			if (Path != Other)
			{
				File.SetLastWriteTimeUtc(Path, Time);
			}
		});

		Assert.AreEqual(0, await BuildAsync(Box, Marks), Box.Logger.Text);

		BuildEditReport Standing = RebuildNeeded.Read(Context);
		CollectionAssert.AreEqual(new[] { Other }, Standing.NotMarked.ToArray());
		// A relative path in the marker is read as the project's.
		CollectionAssert.AreEquivalent(new[] { Third, Path.Join(Box.ProjectDirectory, "Game.h") }, Standing.Stamped.ToArray(), "another target's entry stays as it was");
		Assert.HasCount(1, Standing.Problems);
		Assert.Contains(Other, Standing.Problems[0]);
	}

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public async Task AnUpToDateBuildClearsAnEntryWithNothingLeftToMark(bool stamped)
	{
		// What was stamped would have been compiled; what is gone needs no mark.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Gone = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Gone.cpp");
		KeepForEditor(Box, stamped ? new BuildEditReport([Third], [], []) : new BuildEditReport([], [Gone], ["gone"]));
		DateTime Before = File.GetLastWriteTimeUtc(Third);

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsUpToDate())), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)));
		Assert.AreEqual(Before, File.GetLastWriteTimeUtc(Third));
		Assert.DoesNotContain("go on refusing", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AnUpToDateBuildWithArgumentsTriesNothing()
	{
		// UBT arguments may build part of the target, so it can say nothing either way.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		DateTime Before = File.GetLastWriteTimeUtc(Third);
		KeepForEditor(Box, new BuildEditReport([], [Third], ["could not be marked"]));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsUpToDate()), "--", "-Module=Game"), Box.Logger.Text);

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
		Assert.AreEqual(Before, File.GetLastWriteTimeUtc(Third));
	}

	// ---- A build that overlapped an edit ----

	[TestMethod]
	public async Task ABuildThatOverlappedAnEditAddsItsReportToWhatStands()
	{
		// It shows nothing, as its own edit may have mixed the binaries again: nothing leaves on a build like that.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = (await LeaveRefusedAsync(Box, Patch, "K1")).Third;
		string Other = OtherFile(Box);
		File.SetLastWriteTimeUtc(Other, DateTime.UtcNow.AddMinutes(-5));
		FakeProcessRunner Editing = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Other, "another agent's edit, during the build");
				return CompileLines;
			}),
		};

		Assert.AreEqual(0, await BuildAsync(Box, Services(Editing)), Box.Logger.Text);

		BuildEditReport Standing = RebuildNeeded.Read(Box.Context());
		CollectionAssert.AreEquivalent(new[] { Third, Other }, Standing.Stamped.ToArray());
		await AssertTestRefusedAsync(Box);

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)));
		await AssertTestRunsAsync(Box);
	}

	[TestMethod]
	public async Task AnOverlappedBuildWithNothingStandingLeavesOnlyItsWarning()
	{
		// Its warning says to build again, and no journal it clears was stopping anyone.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		FakeProcessRunner Editing = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Third, "another agent's edit, during the build");
				return CompileLines;
			}),
		};

		Assert.AreEqual(0, await BuildAsync(Box, Services(Editing)), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		Assert.IsFalse(File.Exists(MarkerFile(Box)));
	}

	[TestMethod]
	public async Task AFailedBuildBesideAnEditAddsToWhatStands()
	{
		// It mended nothing, so the file that couldn't be marked still needs saving.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Other = OldFile(OtherFile(Box));
		KeepForEditor(Box, new BuildEditReport([], [Third], ["could not be marked"]));
		FakeProcessRunner Failing = new()
		{
			Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ =>
			{
				File.WriteAllText(Other, "another agent's edit, during the build");
				return FailsOn(Other);
			}),
		};

		Assert.AreEqual(1, await BuildAsync(Box, Services(Failing)), Box.Logger.Text);

		BuildEditReport Standing = RebuildNeeded.Read(Box.Context());
		CollectionAssert.AreEqual(new[] { Third }, Standing.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { Other }, Standing.Stamped.ToArray());
	}

	[TestMethod]
	public async Task ABuildThatClearsAJournalBesideAnEditLeavesTheRefusal()
	{
		// The journal was stopping the next command, and it goes.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "K2");
		string Third = OldFile(ThirdFile(Box));

		Assert.AreEqual(0, await BuildAsync(Box, Services(new FakeProcessRunner { Behaviour = RetryOverlaps(Box, Dll) })), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "K2")));
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray());
		await AssertTestRefusedAsync(Box);
	}

	// ---- The order ----

	static void Throws(string _, DateTime __) => throw new InvalidOperationException("the stamp stopped here");

	[TestMethod]
	public async Task AStampThatThrowsLeavesTheMarker()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = (await LeaveRefusedAsync(Box, Patch, "K3")).Third;
		string Other = OtherFile(Box);
		File.SetLastWriteTimeUtc(Other, DateTime.UtcNow.AddMinutes(-5));
		FakeProcessRunner Editing = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Other, "another agent's edit, during the build");
				return CompileLines;
			}),
		};

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => BuildAsync(Box, Services(Editing, setWriteTime: Throws)));

		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray());
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public async Task AStampThatThrowsLeavesTheJournal()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "K4");
		OldFile(ThirdFile(Box));

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => BuildAsync(Box, Services(new FakeProcessRunner { Behaviour = RetryOverlaps(Box, Dll) }, setWriteTime: Throws)));

		Assert.IsTrue(File.Exists(JournalFile(Box, "K4")));
		Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context()));
	}

	[TestMethod]
	public async Task AStampThatThrowsInARetryLeavesTheJournal()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "K5");
		OldFile(ThirdFile(Box));
		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later, setWriteTime: Throws)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations, "the retry, and no test");
		Assert.IsTrue(File.Exists(JournalFile(Box, "K5")));
		Assert.Contains("the stamp stopped here", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AMarkerThatCantBeWrittenLeavesTheJournal()
	{
		// Nothing would stop the next command otherwise: the marker comes first.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "K6");
		OldFile(ThirdFile(Box));
		SaveOtherFile(Box);
		BlockMarker(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.IsTrue(File.Exists(JournalFile(Box, "K6")));
		Assert.Contains("Could not record that a build overlapped an edit", Box.Logger.Text);
		Assert.Contains("The mutation journals stay", Box.Logger.Text);
		UnblockMarker(Box);
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math", "-quiet"], CancellationToken.None), Box.Logger.Text);
		Assert.HasCount(1, Later.Invocations, "the journal refuses the next run");
	}

	// ---- The refusal ----

	[TestMethod]
	public async Task TheRefusalSaysWhatToBuildForEachTargetAndWhatToDeleteOtherwise()
	{
		using Sandbox Box = new();
		UakContext Context = Box.Context();
		string Third = OldFile(ThirdFile(Box));
		RebuildNeeded.Keep(Context, "GameEditor", "Win64", "Development", new BuildEditReport(["A.h", "B.h"], [], []));
		RebuildNeeded.Keep(Context, "Game", "Win64", "Shipping", NotMarkedAsItIs(Third) with { Stamped = ["C.h"] });
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = BuildsAndCompiles();

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Runner)).RunAsync(Context, ["-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.IsEmpty(Runner.Invocations);
		using JsonDocument Result = ReadResult(ResultFile);
		string Message = Result.RootElement.GetProperty("Message").GetString()!;
		// A relative path in the marker is read as the project's.
		string Project(string Name) => Path.Join(Box.ProjectDirectory, Name);
		Assert.Contains($"`uak build -target=GameEditor -platform=Win64 -config=Development` for GameEditor Win64 Development ({Project("A.h")}, {Project("B.h")})", Message);
		Assert.Contains($"`uak build -target=Game -platform=Win64 -config=Shipping` for Game Win64 Shipping ({Project("C.h")}; not marked, so save first: {Third})", Message);
		Assert.Contains($"delete {MarkerFile(Box)}", Message);
		Assert.Contains("not inside `uak lock run`", Message);
	}

	[TestMethod]
	public void TheWayOutOutlivesAReportAddedToTheMarkers()
	{
		// What a command refuses with is the marker's report plus its own retry's.
		using Sandbox Box = new();
		KeepForEditor(Box, new BuildEditReport(["A.h"], [], []));

		Assert.Contains($"delete {MarkerFile(Box)}", RebuildNeeded.Read(Box.Context()).Plus(new BuildEditReport(["B.h"], [], [])).DescribeRetryOverlap());
		Assert.Contains($"delete {MarkerFile(Box)}", BuildEditReport.None.Plus(RebuildNeeded.Read(Box.Context())).DescribeRetryOverlap());
	}

	[TestMethod]
	public async Task AMarkerWhoseTargetIsGoneRefusesAndDeletingItIsTheWayOut()
	{
		// No build can clear it, so the refusal has to say so.
		using Sandbox Box = new();
		RebuildNeeded.Keep(Box.Context(), "Deleted", "Win64", "Development", new BuildEditReport(["A.h"], [], []));
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = BuildsAndCompiles();

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);
		using (JsonDocument Result = ReadResult(ResultFile))
		{
			string Message = Result.RootElement.GetProperty("Message").GetString()!;
			Assert.Contains("`uak build -target=Deleted -platform=Win64 -config=Development`", Message);
			Assert.Contains($"delete {MarkerFile(Box)}", Message);
		}

		// The project's own target isn't that one, and UBT can't build the one it names.
		Assert.AreEqual(0, await BuildAsync(Box, Services(Runner)), Box.Logger.Text);
		FakeProcessRunner NoSuchTarget = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => ["ERROR: Couldn't find target rules file for target 'Deleted' in rules assembly", "Result: Failed"]) };
		Assert.AreNotEqual(0, await BuildAsync(Box, Services(NoSuchTarget), "-target=Deleted"), Box.Logger.Text);
		Assert.IsTrue(File.Exists(MarkerFile(Box)));
		await AssertTestRefusedAsync(Box);

		File.Delete(MarkerFile(Box));
		await AssertTestRunsAsync(Box);
	}

	[TestMethod]
	[DataRow("[null]")]
	[DataRow("""[{ "Target": null, "Platform": "Win64", "Configuration": "Development" }]""")]
	[DataRow("""[{ "Target": "GameEditor", "Platform": "Win64", "Configuration": "Development", "Stamped": null }]""")]
	[DataRow("""[{ "Target": "GameEditor", "Platform": "Win64", "Configuration": "Development", "NotMarkedWritten": null }]""")]
	public async Task AMarkerWithANullInItCantBeReadAndABuildClearsIt(string text)
	{
		// Read as it stands, it would throw in whatever reads it, the build that clears it among them.
		using Sandbox Box = new();
		Sandbox.Write(MarkerFile(Box), text);

		await AssertTestRefusedAsync(Box);
		Assert.Contains("can't be read", Box.Logger.Text);

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)));
	}

	// ---- The rebuild after a check ----

	[TestMethod]
	public async Task AnEditDuringTheRestoreRebuildLeavesTheRefusal()
	{
		// The journal goes, as that rebuild showed the files back, so nothing else stops the next command.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = OldFile(ThirdFile(Box));
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: Build =>
			{
				if (Build == 1)
				{
					File.WriteAllText(Third, "another agent's edit, during the rebuild");
				}
				return CompileLines;
			}),
		};

		Assert.AreEqual(MutateCommand.CaughtExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=K8", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "K8")));
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray());
		await AssertTestRefusedAsync(Box);

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)));
		await AssertTestRunsAsync(Box);
	}

	[TestMethod]
	public async Task AnEditDuringAFailedRestoreRebuildLeavesTheRefusalBesideTheJournal()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = OldFile(ThirdFile(Box));

		await LeavePendingAsync(Box, Patch, "K9", () => File.WriteAllText(Third, "another agent's edit, during the rebuild"));

		Assert.IsTrue(File.Exists(JournalFile(Box, "K9")));
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).Stamped.ToArray());
	}

	[TestMethod]
	public async Task AMarkerThatCantBeWrittenLeavesTheRestoresJournal()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = OldFile(ThirdFile(Box));
		BlockMarker(Box);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: Build =>
			{
				if (Build == 1)
				{
					File.WriteAllText(Third, "another agent's edit, during the rebuild");
				}
				return CompileLines;
			}),
		};

		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=K10", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.IsTrue(File.Exists(JournalFile(Box, "K10")), Box.Logger.Text);
	}

	// ---- A retry beside an earlier command's marker ----

	[TestMethod]
	public async Task AMutationRefusesAnEarlierEntryBesideItsOwnRetry()
	{
		// Its mutant build mends what its retry overlapped, for its own target, and nothing of the other target's.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "K11");
		string Third = OldFile(ThirdFile(Box));
		RebuildNeeded.Keep(Box.Context(), "Game", "Win64", "Development", new BuildEditReport(["Earlier.h"], [], []));
		SaveOtherFile(Box);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };

		Assert.AreEqual(UakExitCodes.UsageError, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-name=K12", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(1, Builds(Later), "the retry, and no mutant build");
		Assert.DoesNotContain("return A - B;", File.ReadAllText(Box.SourceFile));
		using JsonDocument Result = ReadResult(ResultFile);
		CollectionAssert.AreEquivalent(new[] { Path.Join(Box.ProjectDirectory, "Earlier.h"), Third }, Listed(Result, "EditedDuringBuild"));
	}
}
