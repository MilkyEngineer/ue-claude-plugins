// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

// `uak runs wait`: its exit code says how the run ended, and -timeout stops it with exit code 3 while the run still runs.
// Records are written by hand (this process stands in for a running run), and one real detached run checks the whole path.
// Every test uses its own temporary state directory, never a real one.

using System.Diagnostics;
using AgentKit.Core;
using AgentKit.Tests;

namespace AgentKit.Runs.Tests;

[TestClass]
public sealed class RunsWaitTests
{
	private static readonly TimeSpan s_poll = TimeSpan.FromMilliseconds(50);

	private TempState _state = null!;
	private RunRegistry _registry = null!;

	public TestContext TestContext { get; set; } = null!;

	[TestInitialize]
	public void Initialize()
	{
		_state = new TempState();
		_registry = new RunRegistry(new DirectoryInfo(_state.StateDirectory));
	}

	[TestCleanup]
	public void Cleanup()
	{
		_state.Dispose();
	}

	private async Task<(int Code, string Output)> WaitAsync(params string[] arguments)
	{
		using StringWriter output = new();
		RunsWaitCommand command = new() { PollInterval = s_poll, Output = output };
		int code = await command.RunAsync(_state.Context, arguments, TestContext.CancellationToken);
		return (code, output.ToString());
	}

	private static RunRecord Finished(string name, int? exitCode, string lastLine)
	{
		ProcessIdentity dead = Child.DeadProcess();
		DateTime now = DateTime.UtcNow;
		return new RunRecord
		{
			Name = name,
			Owner = "test",
			Pid = dead.Pid,
			ProcessStart = dead.StartTimeUtc,
			Started = now.AddMinutes(-2),
			Ended = exitCode is null ? null : now,
			ExitCode = exitCode,
			LastLine = exitCode is null ? null : lastLine,
		};
	}

	[TestMethod]
	public async Task RunThatExitedZeroGivesZero()
	{
		_registry.Write(Finished("Good", 0, "Result: PASSED"));
		(int code, string output) = await WaitAsync("-name=Good");
		Assert.AreEqual(UakExitCodes.Success, code);
		StringAssert.Contains(output, "Run 'Good' exited 0 after 2m");
		StringAssert.Contains(output, "Last line: Result: PASSED");
	}

	[TestMethod]
	public async Task RunThatExitedWithAnotherCodeGivesOne()
	{
		_registry.Write(Finished("Bad", 7, "broke"));
		(int code, string output) = await WaitAsync("-name=Bad");
		Assert.AreEqual(UakExitCodes.Failure, code);
		StringAssert.Contains(output, "exited 7");
		StringAssert.Contains(output, "broke");
	}

	[TestMethod]
	public async Task RunThatEndedWithNoExitCodeGivesOneAndSaysSo()
	{
		RunRecord adopted = Finished("Adopted", null, "");
		adopted.Adopted = true;
		_registry.Write(adopted);
		(int code, string output) = await WaitAsync("-name=Adopted");
		Assert.AreEqual(UakExitCodes.Failure, code);
		StringAssert.Contains(output, "no exit code is recorded");

		_registry.Write(Finished("Died", null, ""));
		(code, output) = await WaitAsync("-name=Died");
		Assert.AreEqual(UakExitCodes.Failure, code);
		StringAssert.Contains(output, "died");
		StringAssert.Contains(output, "no exit code");

		_registry.Write(new RunRecord { Name = "Never", Owner = "test", Started = DateTime.UtcNow.AddMinutes(-5) });
		(code, output) = await WaitAsync("-name=Never");
		Assert.AreEqual(UakExitCodes.Failure, code);
		StringAssert.Contains(output, "never started");
	}

	[TestMethod]
	public async Task NoSuchRunGivesTwo()
	{
		(int code, string output) = await WaitAsync("-name=Missing");
		Assert.AreEqual(UakExitCodes.UsageError, code);
		Assert.AreEqual("", output);
	}

	[TestMethod]
	public async Task BadUsageIsAUsageError()
	{
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => WaitAsync());
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => WaitAsync("-name=E3-*"));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => WaitAsync("-name=x", "-timeout=-1"));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => WaitAsync("-name=x", "-bogus"));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => WaitAsync("-name=x", "extra"));
	}

	[TestMethod]
	public async Task TimeoutWhileRunningGivesThree()
	{
		// This test process stands in for the run's live process.
		ProcessIdentity self = ProcessIdentity.Current;
		_registry.Write(new RunRecord { Name = "Live", Owner = "test", Pid = self.Pid, ProcessStart = self.StartTimeUtc, Started = DateTime.UtcNow });
		Stopwatch timer = Stopwatch.StartNew();
		(int code, string output) = await WaitAsync("-name=Live", "-timeout=1");
		Assert.AreEqual(UakExitCodes.TimedOut, code);
		Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.9), timer.Elapsed);
		Assert.IsLessThan(TimeSpan.FromSeconds(10), timer.Elapsed);
		StringAssert.Contains(output, "still running");
		StringAssert.Contains(output, "start another wait");

		// -timeout=0 checks once.
		(code, _) = await WaitAsync("-name=Live", "-timeout=0");
		Assert.AreEqual(UakExitCodes.TimedOut, code);
	}

	[TestMethod]
	public async Task WaitEndsWhenTheRunRecordsItsEnd()
	{
		ProcessIdentity self = ProcessIdentity.Current;
		DateTime started = DateTime.UtcNow;
		_registry.Write(new RunRecord { Name = "Ends", Owner = "test", Pid = self.Pid, ProcessStart = self.StartTimeUtc, Started = started });
		Task<(int Code, string Output)> waiting = WaitAsync("-name=Ends", "-timeout=60");
		await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.CancellationToken);
		Assert.IsFalse(waiting.IsCompleted, "The wait goes on while the run runs.");
		_registry.Write(new RunRecord { Name = "Ends", Owner = "test", Pid = self.Pid, ProcessStart = self.StartTimeUtc, Started = started, Ended = DateTime.UtcNow, ExitCode = 0, LastLine = "done" });
		(int code, string output) = await waiting.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
		Assert.AreEqual(UakExitCodes.Success, code);
		StringAssert.Contains(output, "Run 'Ends' exited 0");
		StringAssert.Contains(output, "done");
	}

	[TestMethod]
	public async Task WaitFollowsARealDetachedRun()
	{
		RunStartResult result = await RunStarter.StartAsync(_registry, new RunStartRequest
		{
			Name = "Sleeper",
			Owner = "test",
			Command = Child.Command("sleep", "500", "4"),
			WorkingDirectory = _state.Root,
		}, Child.Command("wrap"), TestContext.CancellationToken);
		Assert.IsTrue(result.Registered);
		(int code, string output) = await WaitAsync("-name=Sleeper", "-timeout=60");
		Assert.AreEqual(UakExitCodes.Failure, code);
		StringAssert.Contains(output, "exited 4");
		StringAssert.Contains(output, "slept 500");
	}

	[TestMethod]
	public void WaitIsARunsCommandThatNeedsNoEngine()
	{
		RunsWaitCommand command = new();
		Assert.AreEqual("runs wait", command.Name);
		Assert.IsFalse(command.RequiresEngine);
		StringAssert.Contains(command.Usage, "3 -timeout passed");
	}
}
