// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Unreal.Tests;

/// <summary>
/// What a build that overlapped another agent's edit is worth to the command that asked for it: no verdict for a mutation, and
/// no run for a test.
/// </summary>
public sealed partial class MutateTests
{
	const string ActionsStart = "Using Unreal Build Accelerator local executor to run 7 action(s)";

	static string ThirdFile(Sandbox box) => Path.Combine(box.ProjectDirectory, "Source", "Game", "Private", "Third.cpp");

	/// <summary>Another agent's file, older than any build the test runs.</summary>
	static string OldFile(string path)
	{
		Sandbox.Write(path, "as it was");
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
		return path;
	}

	static JsonDocument ReadResult(string file) => JsonDocument.Parse(File.ReadAllText(file));

	static string[] Listed(JsonDocument result, string property) => [.. result.RootElement.GetProperty(property).EnumerateArray().Select(Item => Item.GetString()!)];

	/// <summary>A file's path as a project names it for <see cref="ProjectSettings.SetBuildWrites"/>.</summary>
	static string FromProject(Sandbox box, string file) => Path.GetRelativePath(box.ProjectDirectory, file).Replace('\\', '/');

	static string MarkerFile(Sandbox box) => Path.Combine(box.StateDirectory, RebuildNeeded.FileName);

	[TestMethod]
	[DataRow("test-failed.log", 255)]
	[DataRow("test-passed.log", 0)]
	public async Task AnEditDuringTheMutantBuildIsInconclusiveHoweverTheTestsEnd(string fixture, int exitCode)
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Other = OldFile(OtherFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays(fixture, exitCode, [0, 0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Other, "another agent's edit");
				}
				return CompileLines;
			}),
		};

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=O1", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Exit, Box.Logger.Text);
		Assert.Contains($"mutate: INCONCLUSIVE: {Other} changed while the mutant build ran", Box.Logger.Text);
		Assert.Contains("A file the build writes itself is no edit: name it with `uak test config -build-writes=<path>`", Box.Logger.Text.Split('\n').First(Line => Line.Contains("mutate: INCONCLUSIVE", StringComparison.Ordinal)));
		Assert.DoesNotContain("mutate: CAUGHT", Box.Logger.Text);
		Assert.DoesNotContain("mutate: SURVIVED", Box.Logger.Text);
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.AreEqual("inconclusive", Result.RootElement.GetProperty("Verdict").GetString());
		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Result.RootElement.GetProperty("ExitCode").GetInt32());
		Assert.AreEqual(1, Runner.Invocations.Count(Invocation => !IsBuild(Invocation)), "the tests ran, and that they did changes nothing");
		Assert.IsGreaterThan(0, Result.RootElement.GetProperty("Tests").GetProperty("Found").GetInt32());
		CollectionAssert.Contains(Listed(Result, "Problems"), $"changed while the mutant build ran: {Other}");
		CollectionAssert.AreEqual(new[] { Other }, Listed(Result, "EditedDuringBuild"));
		Assert.IsEmpty(Listed(Result, "EditNotMarked"));
	}

	[TestMethod]
	public async Task AMutantThatDidNotBuildBesideAnEditIsInconclusiveToo()
	{
		// The error may be the edit's, so it says nothing of the mutation.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Other = OldFile(OtherFile(Box));
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [1, 0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Other, "another agent's edit");
					return FailsOn(Box.SourceFile);
				}
				return CompileLines;
			}),
		};

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=O2", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.DoesNotContain("mutate: NOT BUILT", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AnEditThatCouldNotBeMarkedCountsAsOverlapped()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Other = OldFile(OtherFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");
		void SetWriteTime(string Path, DateTime Time)
		{
			if (Path == Other)
			{
				throw new IOException("in use");
			}
			File.SetLastWriteTimeUtc(Path, Time);
		}
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Other, "another agent's edit");
				}
				return CompileLines;
			}),
		};

		int Exit = await new MutateCommand(Services(Runner, setWriteTime: SetWriteTime)).RunAsync(Box.Context(), ["-name=O3", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Exit, Box.Logger.Text);
		using JsonDocument Result = ReadResult(ResultFile);
		CollectionAssert.AreEqual(new[] { Other }, Listed(Result, "EditNotMarked"));
	}

	[TestMethod]
	public async Task ThePatchIsNoEditOfItsOwnBuild()
	{
		// The window holds a patched file's every write of the run, as the journal manages it from before the patch to the stamp.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.SetLastWriteTimeUtc(Box.SourceFile, DateTime.UtcNow);
				}
				return CompileLines;
			}),
		};

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=O4", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.IsEmpty(Listed(Result, "EditedDuringBuild"));
		Assert.DoesNotContain("changed while", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AnEditBeforeTheMutantsActionsStartIsInconclusive()
	{
		// UBT may have passed another unit as up to date on the old text when the patched one compiles with the new: the verdict would be on a mix.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Other = OldFile(OtherFile(Box));
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Other, "another agent's edit");
					Thread.Sleep(50);
				}
				return [ActionsStart, .. CompileLines];
			}),
		};

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=O5", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains($"mutate: INCONCLUSIVE: {Other} changed while the mutant build ran", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AFileTheProjectNamesLeavesTheVerdict()
	{
		// What a pre-build step writes each build: otherwise every mutation would be inconclusive.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Generated = OldFile(OtherFile(Box));
		ProjectSettings.SetBuildWrites(Box.Context(), [FromProject(Box, Generated)]);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: _ =>
			{
				File.WriteAllText(Generated, "generated");
				return CompileLines;
			}),
		};

		Assert.AreEqual(MutateCommand.CaughtExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=O6", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.DoesNotContain("changed while UBT ran", Box.Logger.Text);
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.IsEmpty(Listed(Result, "EditedDuringBuild"));
	}

	// ---- A retry that overlaps an edit ----

	/// <summary>A retry that clears journal <paramref name="name"/>'s failure, with <see cref="ThirdFile"/> edited while it runs.</summary>
	static Func<ProcessInvocation, Action<string>, int> RetryOverlaps(Sandbox box, string dll, string fixture = "test-passed.log", int[]? builds = null) =>
		Plays(fixture, 0, builds ?? [0, 0, 0], buildOutput: Build =>
		{
			if (Build == 0)
			{
				File.WriteAllText(ThirdFile(box), "another agent's edit, during the retry");
			}
			File.WriteAllText(dll, "rebuilt");
			return CompileLines;
		});

	[TestMethod]
	public async Task AnEditDuringTheRetryRefusesTheTest()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "R1");
		string Third = OldFile(ThirdFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations, "the retry, and no test");
		Assert.Contains($"source files changed while a build ran, so its binaries may mix old and new: {Third}", Box.Logger.Text);
		Assert.Contains("Build again before you test", Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "R1")), "the retry showed what clears it, whatever else it compiled");
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.AreEqual("refused", Result.RootElement.GetProperty("Outcome").GetString());
		Assert.AreEqual(UakExitCodes.UsageError, Result.RootElement.GetProperty("ExitCode").GetInt32());
		Assert.Contains("Build again before you test", Result.RootElement.GetProperty("Message").GetString()!);
		CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditedDuringBuild"));
		Assert.IsEmpty(Listed(Result, "EditNotMarked"));
	}

	[TestMethod]
	public async Task ARefusalForAMarkerThatCantBeReadStillListsWhatTheRetryOverlapped()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "R9");
		string Third = OldFile(ThirdFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");
		SaveOtherFile(Box);
		Sandbox.Write(MarkerFile(Box), "{ not json");
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations, "the retry, and no test");
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.AreEqual("refused", Result.RootElement.GetProperty("Outcome").GetString());
		Assert.Contains("can't be read", Result.RootElement.GetProperty("Message").GetString()!);
		CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditedDuringBuild"));
	}

	/// <summary>A test refused after a retry that overlapped an edit of Third.cpp and cleared journal <paramref name="name"/>, so that only the marker refuses from here on.</summary>
	static async Task<(string Third, FakeProcessRunner Later)> LeaveRefusedAsync(Sandbox box, string patch, string name)
	{
		string Dll = await LeavePendingAsync(box, patch, name);
		string Third = OldFile(ThirdFile(box));
		SaveOtherFile(box);
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(box, Dll) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(box.Context(), ["-filter=Project.Math"], CancellationToken.None), box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(box, name)));
		Assert.HasCount(1, Later.Invocations);
		return (Third, Later);
	}

	[TestMethod]
	public async Task TheRefusalStandsForTheNextTestAndMutationUntilABuildSucceeds()
	{
		// The retry cleared the journal, so nothing else stops them, perhaps another agent's, and the binaries still mix old and new.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		(string Third, FakeProcessRunner Later) = await LeaveRefusedAsync(Box, Patch, "R2");
		string ResultFile = Path.Combine(Box.Root, "result.json");

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations, "nothing ran");
		using (JsonDocument Result = ReadResult(ResultFile))
		{
			Assert.AreEqual("refused", Result.RootElement.GetProperty("Outcome").GetString());
			Assert.Contains($"source files changed while a build ran, so its binaries may mix old and new: {Third}", Result.RootElement.GetProperty("Message").GetString()!);
			CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditedDuringBuild"));
		}

		Assert.AreEqual(UakExitCodes.UsageError, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-name=R2b", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations, "no mutant build");
		Assert.DoesNotContain("return A - B;", File.ReadAllText(Box.SourceFile));
		using (JsonDocument Result = ReadResult(ResultFile))
		{
			Assert.AreEqual("refused", Result.RootElement.GetProperty("Verdict").GetString());
			CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditedDuringBuild"));
		}

		// As the refusal says: `uak build`, then the test.
		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(File.Exists(MarkerFile(Box)));
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.HasCount(3, Later.Invocations, "the retry, the build, the test");
	}

	[TestMethod]
	public async Task AFailedBuildLeavesTheRefusal()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeaveRefusedAsync(Box, Patch, "R12");
		FakeProcessRunner Failing = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => FailsOn(OtherFile(Box))) };

		Assert.AreEqual(1, await new BuildCommand(Services(Failing)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Failing)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.HasCount(1, Failing.Invocations, "the build, and no test");
	}

	[TestMethod]
	public async Task ABuildOfAnotherTargetOrOfPartOfThisOneLeavesTheRefusal()
	{
		// As for a journal: those needn't relink what the retry left mixed. A plain `uak build` always clears it.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Later = (await LeaveRefusedAsync(Box, Patch, "R13")).Later;

		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), ["-target=Game"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), ["--", "-SingleFile=" + Box.SourceFile], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.HasCount(3, Later.Invocations, "the retry and the two builds, and no test");
	}

	[TestMethod]
	public async Task AQuietTestRefusesWhileTheRefusalStands()
	{
		// It retries nothing, so it has no retry of its own to go by.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		(string Third, FakeProcessRunner Later) = await LeaveRefusedAsync(Box, Patch, "R14");
		string ResultFile = Path.Combine(Box.Root, "result.json");

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math", "-quiet", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations);
		using JsonDocument Result = ReadResult(ResultFile);
		CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditedDuringBuild"));
	}

	[TestMethod]
	public async Task ACookedRunRefusesWhileTheRefusalStands()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		Sandbox.Write(Box.Layout.RunUatScript, "");
		(string Third, FakeProcessRunner Later) = await LeaveRefusedAsync(Box, Patch, "R15");
		string ResultFile = Path.Combine(Box.Root, "result.json");

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations, "no UAT");
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.AreEqual("refused", Result.RootElement.GetProperty("Outcome").GetString());
		Assert.Contains($"source files changed while a build ran, so its binaries may mix old and new: {Third}", Result.RootElement.GetProperty("Message").GetString()!);
	}

	[TestMethod]
	public async Task AMarkerThatCantBeReadRefusesAndABuildClearsIt()
	{
		// Unread, it may hold a refusal; and nothing must be able to leave a project stuck.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		Sandbox.Write(MarkerFile(Box), "{ not json");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("A whole `uak build` (no UBT arguments) of the target `uak test` uses, on its own, not inside `uak lock run`, that succeeds clears it", Box.Logger.Text);
		Assert.AreEqual(UakExitCodes.UsageError, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=R16", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations);

		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)));
		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task ABuildClearsOnlyItsOwnTarget()
	{
		using Sandbox Box = new();
		UakContext Context = Box.Context();
		string Unmarked = OldFile(OtherFile(Box));
		UnrealServices NoMarks = Services(BuildsAndCompiles(), setWriteTime: (_, _) => { });
		RebuildNeeded.Keep(Context, "GameEditor", "Win64", "Development", new BuildEditReport(["A.h"], [], []));
		RebuildNeeded.Keep(Context, "Game", "Win64", "Development", new BuildEditReport([], [Unmarked], ["why not"]));

		await RebuildNeeded.AfterBuildAsync(Context, NoMarks, "GameEditor", "Win64", "DebugGame", DateTime.UtcNow);
		await RebuildNeeded.AfterBuildAsync(Context, NoMarks, "GameEditor", "Linux", "Development", DateTime.UtcNow);
		Assert.HasCount(2, RebuildNeeded.Read(Context).Overlapped);

		await RebuildNeeded.AfterBuildAsync(Context, NoMarks, "GameEditor", "Win64", "Development", DateTime.UtcNow);
		BuildEditReport Left = RebuildNeeded.Read(Context);
		Assert.IsEmpty(Left.Stamped);
		CollectionAssert.AreEqual(new[] { Unmarked }, Left.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { "why not" }, Left.Problems.ToArray());

		File.Delete(Unmarked);
		await RebuildNeeded.AfterBuildAsync(Context, NoMarks, "Game", "Win64", "Development", DateTime.UtcNow);
		Assert.IsFalse(File.Exists(MarkerFile(Box)));
		Assert.IsEmpty(RebuildNeeded.Read(Context).Overlapped);
	}

	[TestMethod]
	public void ANewRetryOfATargetAddsToItsOneEntry()
	{
		using Sandbox Box = new();
		UakContext Context = Box.Context();
		string First = OldFile(OtherFile(Box));
		string Second = OldFile(ThirdFile(Box));
		RebuildNeeded.Keep(Context, "GameEditor", "Win64", "Development", new BuildEditReport([First], [], []));
		RebuildNeeded.Keep(Context, "GameEditor", "Win64", "Development", new BuildEditReport([Second], [], []));
		RebuildNeeded.Keep(Context, "GameEditor", "Win64", "Development", BuildEditReport.None);

		Assert.HasCount(1, RebuildNeededFile.Read(Context));
		CollectionAssert.AreEquivalent(new[] { First, Second }, RebuildNeeded.Read(Context).Stamped.ToArray());
	}

	/// <summary>A retry that fails with <see cref="ThirdFile"/> edited while it runs: the journal stays, with what it overlapped to report.</summary>
	static Func<ProcessInvocation, Action<string>, int> RetryFailsAndOverlaps(Sandbox box) => Plays("test-passed.log", 0, [1], buildOutput: _ =>
	{
		File.WriteAllText(ThirdFile(box), "another agent's edit, during the retry");
		return FailsOn(OtherFile(box));
	});

	[TestMethod]
	public async Task ATestTheJournalRefusesListsWhatTheFailedRetryOverlapped()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "R17");
		string Third = OldFile(ThirdFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryFailsAndOverlaps(Box) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.IsTrue(File.Exists(JournalFile(Box, "R17")));
		Assert.IsTrue(File.Exists(MarkerFile(Box)), "a build of the target has yet to succeed");
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.AreEqual("refused", Result.RootElement.GetProperty("Outcome").GetString());
		Assert.Contains("'R17'", Result.RootElement.GetProperty("Message").GetString()!);
		CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditedDuringBuild"));
	}

	[TestMethod]
	public async Task AMutationTheJournalRefusesListsWhatTheFailedRetryOverlapped()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "R18");
		string Third = OldFile(ThirdFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryFailsAndOverlaps(Box) };
		Assert.AreEqual(UakExitCodes.UsageError, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-name=R19", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.IsTrue(File.Exists(JournalFile(Box, "R18")));
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.AreEqual("refused", Result.RootElement.GetProperty("Verdict").GetString());
		CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditedDuringBuild"));
	}

	[TestMethod]
	public async Task AnEditDuringTheRetryThatCouldNotBeMarkedRefusesToo()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "R3");
		string Third = OldFile(ThirdFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };
		UnrealServices Unmarked = Services(Later, setWriteTime: (_, _) => { });
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Unmarked).RunAsync(Box.Context(), ["-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		using JsonDocument Result = ReadResult(ResultFile);
		CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditNotMarked"));
		Assert.Contains("could not be marked for a recompile: save it again first", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AnEditDuringTheRetryRefusesACookedRunBeforeUat()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		Sandbox.Write(Box.Layout.RunUatScript, "");
		string Dll = await LeavePendingAsync(Box, Patch, "R4");
		string Third = OldFile(ThirdFile(Box));

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations, "the retry, and no UAT");
		Assert.Contains(Third, Box.Logger.Text);
	}

	[TestMethod]
	public async Task AMutationGoesOnAfterARetryThatOverlappedAnEdit()
	{
		// With the real wait, as a run has it: the stamp is past all the retry wrote and before the mutant build, which recompiles what
		// uses the edit; and it isn't an edit of that build.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "R5");
		string Third = OldFile(ThirdFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");
		DateTime RetryWrote = default;
		DateTime StampedBeforeMutant = default;
		int Build = 0;
		Func<ProcessInvocation, Action<string>, int> Plays = MutateTests.Plays("test-failed.log", 255, [0, 0, 0], buildOutput: _ =>
		{
			if (Build == 0)
			{
				File.WriteAllText(Third, "another agent's edit, during the retry");
				File.WriteAllText(Dll, "rebuilt");
				RetryWrote = File.GetLastWriteTimeUtc(Dll);
			}
			else if (Build == 1)
			{
				StampedBeforeMutant = File.GetLastWriteTimeUtc(Third);
			}
			Build++;
			return CompileLines;
		});

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = Plays };
		UnrealServices Waiting = Services(Later, realDelay: true, recompileMargin: TimeSpan.FromSeconds(1.2));
		int Exit = await new MutateCommand(Waiting).RunAsync(Box.Context(), ["-name=R6", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.AreEqual(3, Builds(Later), "the retry, the mutant, the rebuild");
		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, StampedBeforeMutant - RetryWrote, "the mutant build takes what the retry compiled from the old text as out of date");
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.IsEmpty(Listed(Result, "EditedDuringBuild"), "the mutant build's own");
		Assert.IsFalse(File.Exists(MarkerFile(Box)), "those builds succeeded");
	}

	[TestMethod]
	public async Task AMutationStoppedAfterItsRetryLeavesTheRefusal()
	{
		// It went on because its own mutant build would mend the binaries, and that build never ran.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "R20");
		OldFile(ThirdFile(Box));
		string Changed = Original.Replace("return A + B;", "return A * B;", StringComparison.Ordinal);
		File.WriteAllText(Box.SourceFile, Changed);
		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };

		Assert.AreEqual(UakExitCodes.UsageError, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-name=R21", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("doesn't apply cleanly", Box.Logger.Text);
		Assert.AreEqual(1, Builds(Later), "the retry, and no mutant build");
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.HasCount(1, Later.Invocations, "no test");
	}

	[TestMethod]
	public async Task AMutationRefusesARetryEditItCouldNotMark()
	{
		// Nothing would make the mutant build recompile what uses it, so its binaries would mix old and new unnoticed.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = await LeavePendingAsync(Box, Patch, "R7");
		string Third = OldFile(ThirdFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");

		SaveOtherFile(Box);
		FakeProcessRunner Later = new() { Behaviour = RetryOverlaps(Box, Dll) };
		UnrealServices Unmarked = Services(Later, setWriteTime: (Path, Time) =>
		{
			if (Path != Third)
			{
				File.SetLastWriteTimeUtc(Path, Time);
			}
		});
		Assert.AreEqual(UakExitCodes.UsageError, await new MutateCommand(Unmarked).RunAsync(Box.Context(), ["-name=R8", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(1, Builds(Later), "no mutant build");
		Assert.AreEqual("the whole edit", File.ReadAllText(OtherFile(Box)));
		Assert.DoesNotContain("return A - B;", File.ReadAllText(Box.SourceFile));
		using JsonDocument Result = ReadResult(ResultFile);
		Assert.AreEqual("refused", Result.RootElement.GetProperty("Verdict").GetString());
		CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditNotMarked"));
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.HasCount(1, Later.Invocations, "that refusal stands for the next command too");
	}

	[TestMethod]
	public async Task ARetryThatCannotRunRecordsThatAndIsNotTriedAgain()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "R9");
		MutationRebuildFailure First = Record(Box, "R9").RebuildFailure!;

		SaveOtherFile(Box);
		File.SetLastWriteTimeUtc(OtherFile(Box), First.Started.AddSeconds(1));
		FakeProcessRunner Later = new() { Behaviour = (_, _) => throw new IOException("the log can't be opened") };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations);
		MutationRebuildFailure Failure = Record(Box, "R9").RebuildFailure!;
		Assert.AreEqual("the log can't be opened", Failure.Message);
		Assert.IsNull(Failure.File);
		Assert.IsGreaterThan(First.Started, Failure.Started);

		// That change is older than this failure, so the same tree isn't tried again.
		File.SetLastWriteTimeUtc(OtherFile(Box), Failure.Started.AddSeconds(-1));
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.HasCount(1, Later.Invocations);
		Assert.Contains("the last rebuild failed: the log can't be opened", Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(Box.Context())).Message);
	}

	[TestMethod]
	public async Task WhatARebuildWroteBeforeItsActionsStartBringsARetry()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Edited = OldFile(ThirdFile(Box));
		await LeavePendingAsync(Box, Patch, "R10", () =>
		{
			File.WriteAllText(Edited, "another agent's edit");
			Thread.Sleep(50);
		}, [ActionsStart, .. FailsOn(OtherFile(Box))]);

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => FailsOn(OtherFile(Box))) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(1, Builds(Later));
	}

	[TestMethod]
	public async Task AFileTheProjectNamesBringsNoRetry()
	{
		// A pre-build step that writes a source file each build: as a change, it would retry on every `uak test`.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Generated = OldFile(ThirdFile(Box));
		ProjectSettings.SetBuildWrites(Box.Context(), [FromProject(Box, Generated)]);
		await LeavePendingAsync(Box, Patch, "R22", () => File.WriteAllText(Generated, "generated"));

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Builds(Later));
		Assert.Contains("no source file changed since", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AFileDatedInTheFutureBringsNoRetry()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "R11");

		File.SetLastWriteTimeUtc(OldFile(ThirdFile(Box)), DateTime.UtcNow.AddHours(1));
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, Builds(Later));
	}

	[TestMethod]
	[DataRow(9, 1)]
	[DataRow(11, 0)]
	public async Task AFileCountsAsAChangeUpToTheMarginPastNow(int minutesAhead, int retries)
	{
		// The margin lets in what a build stamped with its wait cut short, and no more.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "R23");

		File.SetLastWriteTimeUtc(OldFile(ThirdFile(Box)), DateTime.UtcNow.AddMinutes(minutesAhead));
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => FailsOn(OtherFile(Box))) };
		await new TestCommand(Services(Later, recompileMargin: TimeSpan.FromMinutes(10))).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(retries, Builds(Later), Box.Logger.Text);
	}

	[TestMethod]
	public async Task AnEditorRunsResultFileHasTheFieldsACookedRunsHas()
	{
		using Sandbox Box = new();
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };

		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		using JsonDocument Result = ReadResult(ResultFile);
		Assert.IsEmpty(Listed(Result, "EditedDuringBuild"));
		Assert.IsEmpty(Listed(Result, "EditNotMarked"));
	}
}
