// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Runs;

namespace AgentKit.Tests;

[TestClass]
public sealed class RunsShowTests
{
	static RunSummaryEntry Entry(string command, string name, int exitCode, params string[] lines) =>
		new() { Command = command, Name = name, ExitCode = exitCode, Finished = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc), Lines = [.. lines] };

	static async Task<(int Exit, string Text)> ShowAsync(TempState state, string name)
	{
		using StringWriter Output = new();
		int Exit = await new RunsShowCommand { Output = Output }.RunAsync(state.Context, ["-name=" + name], CancellationToken.None);
		return (Exit, Output.ToString());
	}

	[TestMethod]
	public async Task ARunShowsItsEndAndEverySummaryItRecorded()
	{
		using TempState State = new();
		new RunRegistry(State.Context.StateDirectory).Write(new RunRecord
		{
			Name = "bench-1", Owner = "E7", Pid = 1, Started = DateTime.UtcNow.AddMinutes(-5), Ended = DateTime.UtcNow, ExitCode = 0, LastLine = "test: PASSED", OutputFile = "out.log",
		});
		RunSummaries.Record(State.Context.StateDirectory, Entry("build", "uak-build", 0, "tree: Git abc, no changes", "build: SUCCEEDED"), "bench-1");
		RunSummaries.Record(State.Context.StateDirectory, Entry("test", "bench-1", 0, "watch: LogSaveGame: 0 errors, 1 warning", "test: PASSED: 3 of 3 passed"), "bench-1");

		(int Exit, string Text) = await ShowAsync(State, "bench-1");

		Assert.AreEqual(0, Exit);
		Assert.Contains("Run 'bench-1' exited 0", Text);
		Assert.Contains("build 'uak-build', exit code 0, finished 2026-10-29T12:00:00.000Z:\n  tree: Git abc, no changes\n  build: SUCCEEDED\n", Text);
		Assert.Contains("test 'bench-1', exit code 0", Text);
		Assert.IsLessThan(Text.IndexOf("test 'bench-1'", StringComparison.Ordinal), Text.IndexOf("build 'uak-build'", StringComparison.Ordinal));
	}

	[TestMethod]
	public async Task ACommandsOwnNameShowsItsLastSummaryAndAnUnknownNameIsAnError()
	{
		using TempState State = new();
		RunSummaries.Record(State.Context.StateDirectory, Entry("test", "smoke", 1, "test: FAILED: 0 of 1 passed"), null);
		RunSummaries.Record(State.Context.StateDirectory, Entry("test", "smoke", 0, "test: PASSED: 1 of 1 passed"), null);

		(int Exit, string Text) = await ShowAsync(State, "smoke");
		Assert.AreEqual(0, Exit);
		Assert.Contains("test: PASSED: 1 of 1 passed", Text);
		Assert.DoesNotContain("FAILED", Text);

		Assert.AreEqual(2, (await ShowAsync(State, "nothing")).Exit);
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => ShowAsync(State, "bad*name"));
	}

	[TestMethod]
	public async Task RunsWaitPrintsTheLastVerdictWithItsCommandsExitCode()
	{
		using TempState State = new();
		new RunRegistry(State.Context.StateDirectory).Write(new RunRecord
		{
			Name = "mut-1", Owner = "E7", Pid = 1, Started = DateTime.UtcNow.AddMinutes(-5), Ended = DateTime.UtcNow, ExitCode = 6, LastLine = "Exit code 6", OutputFile = "out.log",
		});
		RunSummaries.Record(State.Context.StateDirectory, Entry("mutate", "M1", 6, "tree: Git abc, no changes", "error: mutate: SURVIVED: the tests passed with the mutation"), "mut-1");
		using StringWriter Output = new();

		int Exit = await new RunsWaitCommand { Output = Output, PollInterval = TimeSpan.FromMilliseconds(10) }.RunAsync(State.Context, ["-name=mut-1"], CancellationToken.None);

		// 1 for any code but 0: the code and the verdict line say what it was.
		Assert.AreEqual(1, Exit);
		Assert.Contains("Run 'mut-1' exited 6", Output.ToString());
		Assert.Contains("Verdict: mutate 'M1' exited 6: error: mutate: SURVIVED", Output.ToString());
	}

	[TestMethod]
	public async Task ShowTakesEveryNameACommandRecordsUnder()
	{
		using TempState State = new();
		// uak test -name= takes more than run names do: '+' names no run, but a test.
		RunSummaries.Record(State.Context.StateDirectory, Entry("test", "smoke+gpu", 0, "test: PASSED: 1 of 1 passed"), null);

		(int Exit, string Text) = await ShowAsync(State, "smoke+gpu");

		Assert.AreEqual(0, Exit);
		Assert.Contains("test: PASSED", Text);
		foreach (string Bad in new[] { "a b", "a/b", "a\\b", "..", "bad*name", "", "Dot.", "NUL", "nul.txt", "COM1" })
		{
			Assert.IsFalse(RunSummaries.IsValidName(Bad), Bad);
		}
	}

	[TestMethod]
	public async Task CommandsOfOneRunThatEndTogetherLoseNoSummary()
	{
		using TempState State = new();

		await Task.WhenAll(Enumerable.Range(0, 16).Select(Index =>
			Task.Run(() => Assert.IsEmpty(RunSummaries.Record(State.Context.StateDirectory, Entry("test", $"t{Index}", 0, "test: PASSED"), "busy")))));

		Assert.HasCount(16, RunSummaries.Read(State.Context.StateDirectory, "busy")!.Entries);
	}

	[TestMethod]
	public void ARunStartsWithoutTheSummariesOfAnEarlierRunOfItsName()
	{
		using TempState State = new();
		RunSummaries.Record(State.Context.StateDirectory, Entry("test", "t", 0, "old"), "reused");

		RunSummaries.Clear(State.Context.StateDirectory, "reused");

		Assert.IsNull(RunSummaries.Read(State.Context.StateDirectory, "reused"));
		Assert.IsNotNull(RunSummaries.Read(State.Context.StateDirectory, "t"));
	}

	[TestMethod]
	public async Task ACommandThatRecordedNoResultNeverShowsAnEarlierOne()
	{
		using TempState State = new();
		DirectoryInfo Directory = State.Context.StateDirectory;
		RunSummaries.Record(Directory, Entry("mutate", "M1", 6, "error: mutate: SURVIVED: the tests passed with the mutation"), null);

		RunSummaries.Begin(Directory, "mutate", "M1", new DateTime(2026, 10, 29, 13, 0, 0, DateTimeKind.Utc), null);
		(int Exit, string Text) = await ShowAsync(State, "M1");

		Assert.AreEqual(0, Exit);
		Assert.Contains("mutate 'M1' started 2026-10-29T13:00:00.000Z and recorded no result", Text);
		Assert.IsFalse(Text.Contains("SURVIVED", StringComparison.Ordinal), Text);
		Assert.IsNull(RunSummaries.Read(Directory, "M1")!.Entries.Single().ExitCode, "never read as exit 0");
	}

	[TestMethod]
	public void InARunACommandsResultTakesThePlaceOfItsEntryWithoutOne()
	{
		using TempState State = new();
		DirectoryInfo Directory = State.Context.StateDirectory;
		RunRecord Run = new() { Name = "run-1", Owner = "E1", Pid = 1, Started = DateTime.UtcNow.AddMinutes(-1), Ended = DateTime.UtcNow, ExitCode = 1 };
		RunSummaries.Record(Directory, Entry("build", "uak-build", 0, "build: SUCCEEDED"), "run-1");

		RunSummaryInvocation Test = RunSummaryInvocation.New();
		RunSummaries.Begin(Directory, "test", "t1", DateTime.UtcNow, "run-1", Test);
		Assert.Contains("Verdict: none: its last command, test 't1', started", RunsWaitCommand.Describe(Run, RunState.Exited, RunSummaries.Read(Directory, "run-1")));

		RunSummaries.Record(Directory, Test.Stamp(Entry("test", "t1", 0, "test: PASSED: 1 of 1 passed")), "run-1");
		CollectionAssert.AreEqual(new int?[] { 0, 0 }, RunSummaries.Read(Directory, "run-1")!.Entries.Select(Entry => Entry.ExitCode).ToArray());
		Assert.Contains("Verdict: test 't1' exited 0", RunsWaitCommand.Describe(Run, RunState.Exited, RunSummaries.Read(Directory, "run-1")));

		// A refusal recorded after the command's own result never replaces it.
		RunSummaries.RecordIfUnfinished(Directory, Test.Stamp(Entry("test", "t1", 2, "error: test: REFUSED")), "run-1");
		Assert.AreEqual(0, RunSummaries.Read(Directory, "t1")!.Entries.Single().ExitCode);
		Assert.AreEqual(0, RunSummaries.Read(Directory, "run-1")!.Entries[^1].ExitCode);
	}

	[TestMethod]
	public async Task TwoCommandsUnderOneNameEachKeepTheirOwnEntry()
	{
		using TempState State = new();
		DirectoryInfo Directory = State.Context.StateDirectory;
		RunSummaryInvocation A = RunSummaryInvocation.New();
		RunSummaryInvocation B = RunSummaryInvocation.New();
		DateTime Started = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

		// Two agents' `uak build` without -name= share uak-build: B starts while A runs (queued for the lock), and A ends first.
		RunSummaries.Begin(Directory, "build", "uak-build", Started, null, A);
		RunSummaries.Begin(Directory, "build", "uak-build", Started, null, B);
		RunSummaries.Record(Directory, A.Stamp(Entry("build", "uak-build", 1, "error: build: FAILED (A's)")), null);

		List<RunSummaryEntry> Entries = RunSummaries.Read(Directory, "uak-build")!.Entries;
		Assert.HasCount(2, Entries);
		Assert.IsNull(Entries.Single(Existing => Existing.Invocation == B.Id).ExitCode, "A's result never takes B's place");
		(int Exit, string Text) = await ShowAsync(State, "uak-build");
		Assert.AreEqual(0, Exit);
		Assert.Contains("2 commands ran under 'uak-build' at the same time", Text);
		Assert.Contains($"recorded no result: it still runs (PID {B.Pid})", Text);
		Assert.Contains($"build 'uak-build' (PID {A.Pid}), exit code 1", Text);

		// B's own result takes only its own entry's place.
		RunSummaries.Record(Directory, B.Stamp(Entry("build", "uak-build", 0, "build: SUCCEEDED (B's)")), null);
		CollectionAssert.AreEquivalent(new int?[] { 1, 0 }, RunSummaries.Read(Directory, "uak-build")!.Entries.Select(Existing => Existing.ExitCode).ToArray());

		// The next command drops both results; one whose process is gone is said so, and dropped by the next.
		RunSummaryInvocation Gone = new("gone", int.MaxValue, DateTime.UtcNow);
		RunSummaries.Begin(Directory, "build", "uak-build", Started, null, Gone);
		Assert.Contains($"its process (PID {int.MaxValue}) is gone", (await ShowAsync(State, "uak-build")).Text);
		RunSummaryInvocation C = RunSummaryInvocation.New();
		RunSummaries.Begin(Directory, "build", "uak-build", Started, null, C);
		Assert.AreEqual(C.Id, RunSummaries.Read(Directory, "uak-build")!.Entries.Single().Invocation);
	}

	[TestMethod]
	public void AnEntryRecordedWithoutAnInvocationNeverTakesAnotherCommandsPlace()
	{
		using TempState State = new();
		DirectoryInfo Directory = State.Context.StateDirectory;
		RunSummaryInvocation Running = RunSummaryInvocation.New();
		RunSummaries.Begin(Directory, "test", "uak-test", DateTime.UtcNow, null, Running);

		// Recorded with no ID: no command's own entry, so the running command's entry stays, waiting for its result.
		RunSummaries.Record(Directory, Entry("test", "uak-test", 0, "test: PASSED"), null);

		List<RunSummaryEntry> Entries = RunSummaries.Read(Directory, "uak-test")!.Entries;
		Assert.HasCount(2, Entries);
		Assert.IsNull(Entries.Single(Existing => Existing.Invocation == Running.Id).ExitCode);
	}

	[TestMethod]
	public void AnEntryWhoseProcessStartTimeIsUnknownIsNoRunningCommand()
	{
		// Its ID runs (this process), but without a start time a reused ID can't be told from it.
		Assert.IsFalse(new RunSummaryEntry { Pid = Environment.ProcessId, ProcessStart = null }.MayStillRun());
		Assert.IsTrue(new RunSummaryEntry { Pid = Environment.ProcessId, ProcessStart = ProcessIdentity.Current.StartTimeUtc }.MayStillRun());
	}

	[TestMethod]
	public async Task TestsUnderOneNameAreSaidToShareTheirLog()
	{
		using TempState State = new();
		DirectoryInfo Directory = State.Context.StateDirectory;
		RunSummaryInvocation A = RunSummaryInvocation.New();
		RunSummaryInvocation B = RunSummaryInvocation.New();
		RunSummaries.Begin(Directory, "test", "uak-test", DateTime.UtcNow, null, A);
		RunSummaries.Begin(Directory, "test", "uak-test", DateTime.UtcNow, null, B);
		RunSummaries.Record(Directory, A.Stamp(Entry("test", "uak-test", 0, "test: PASSED: 1 of 1 passed. 2.0 s. Log: uak-test.log")), null);

		(int Exit, string Text) = await ShowAsync(State, "uak-test");

		Assert.AreEqual(0, Exit);
		Assert.Contains("They also share the test log (<Project>/Saved/Logs/uak-test.log", Text);
	}
}
