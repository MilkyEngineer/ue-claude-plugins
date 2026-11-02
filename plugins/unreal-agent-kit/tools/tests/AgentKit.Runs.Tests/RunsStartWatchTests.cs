// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

// What uak runs start reports of a run that stops at once. The wrapper's real run is in DetachedRunTests; here it starts the
// test child and watches its record.

using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Tests;

namespace AgentKit.Runs.Tests;

[TestClass]
public sealed class RunsStartWatchTests
{
	private static IReadOnlyList<string> Wrapper => Child.Command("wrap");

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

	private string WriteRun(string name, int? exitCode, params string[] output)
	{
		string file = Path.Combine(_state.Root, name + ".output.txt");
		File.WriteAllLines(file, output);
		_registry.Write(new RunRecord
		{
			Name = name,
			Owner = "test",
			Command = "uak",
			Arguments = ["test"],
			OutputFile = file,
			Pid = Environment.ProcessId,
			ExitCode = exitCode,
			Started = DateTime.UtcNow,
			Ended = exitCode is null ? null : DateTime.UtcNow,
		});
		return file;
	}

	private async Task<(int Exit, string Output)> WatchAsync(string name, int waitMilliseconds = 2000)
	{
		StringWriter output = new();
		RunsStartCommand command = new() { Output = output, EarlyEndWait = TimeSpan.FromMilliseconds(waitMilliseconds), EarlyEndPoll = TimeSpan.FromMilliseconds(10) };
		int exit = await command.WatchStartAsync(_registry, name, output, TestContext.CancellationToken);
		return (exit, output.ToString());
	}

	[TestMethod]
	public async Task AChildsUsageErrorIsReportedWithItsLineAndExitCode()
	{
		WriteRun("Refused", 2, "error: uak test takes no positional arguments: .txt.", "Run 'uak help test' for usage.");

		(int exit, string output) = await WatchAsync("Refused");

		Assert.AreEqual(UakExitCodes.UsageError, exit);
		Assert.Contains("It stopped at once, with exit code 2: error: uak test takes no positional arguments: .txt.", output);
		Assert.DoesNotContain("Run 'uak help", output);
	}

	[TestMethod]
	public async Task AnotherFailureExitsOneWithItsCode()
	{
		WriteRun("Failed", 7, "something went wrong");

		(int exit, string output) = await WatchAsync("Failed");

		Assert.AreEqual(UakExitCodes.Failure, exit);
		Assert.Contains("It stopped at once, with exit code 7", output);
		Assert.DoesNotContain("error:", output);
	}

	[TestMethod]
	public async Task ARunThatEndedWellIsNoFailure()
	{
		WriteRun("Quick", 0, "done");

		(int exit, string output) = await WatchAsync("Quick");

		Assert.AreEqual(UakExitCodes.Success, exit);
		Assert.Contains("It has already ended, with exit code 0.", output);
	}

	[TestMethod]
	public async Task ARunThatGoesOnSaysNothingMore()
	{
		WriteRun("Long", null);

		(int exit, string output) = await WatchAsync("Long", waitMilliseconds: 50);

		Assert.AreEqual(UakExitCodes.Success, exit);
		Assert.AreEqual("", output);
	}

	[TestMethod]
	public async Task BeingStoppedStopsOnlyTheWatching()
	{
		WriteRun("Watched", null);
		using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(50));
		RunsStartCommand command = new() { EarlyEndWait = TimeSpan.FromMinutes(5), EarlyEndPoll = TimeSpan.FromMilliseconds(10) };

		int exit = await command.WatchStartAsync(_registry, "Watched", TextWriter.Null, cancel.Token).WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

		Assert.AreEqual(UakExitCodes.Success, exit);
	}

	[TestMethod]
	public async Task ARealRunThatStopsAtOnceIsSeenEndingWhileItIsWatched()
	{
		RunStartResult result = await RunStarter.StartAsync(_registry, new RunStartRequest
		{
			Name = "RealRun",
			Owner = "test",
			Command = Child.Command("no-such-mode"),
			WorkingDirectory = _state.Root,
		}, Wrapper, TestContext.CancellationToken);
		Assert.IsTrue(result.Registered);

		(int exit, string output) = await WatchAsync("RealRun", waitMilliseconds: 30000);

		Assert.AreEqual(UakExitCodes.UsageError, exit, output);
		Assert.Contains("It stopped at once, with exit code 2", output);
	}

	[TestMethod]
	public async Task ARunsOwnerReachesItsCommand()
	{
		string file = Path.Combine(_state.Root, "environment.txt");
		await RunStarter.StartAsync(_registry, new RunStartRequest
		{
			Name = "OwnedRun",
			Owner = "E3",
			Command = Child.Command("env", file, RunSummaries.RunNameVariable, RunSummaries.RunOwnerVariable),
			WorkingDirectory = _state.Root,
		}, Wrapper, TestContext.CancellationToken);

		await WatchAsync("OwnedRun", waitMilliseconds: 30000);

		string text = File.ReadAllText(file);
		Assert.Contains("OwnedRun", text);
		Assert.Contains("E3", text);
	}
}
