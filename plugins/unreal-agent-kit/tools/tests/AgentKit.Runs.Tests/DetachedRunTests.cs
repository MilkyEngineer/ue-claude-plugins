// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

// Detached runs across real processes: the starter, the wrapper and the command are the test child in different modes.
// Every test uses its own temporary state directory, never a real one.

using System.Diagnostics;
using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Tests;

namespace AgentKit.Runs.Tests;

[TestClass]
public sealed class DetachedRunTests
{
	private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

	/// <summary>Arguments that break naive quoting: spaces, quotes, ';', backslashes before quotes and at the end, empty.</summary>
	private static readonly string[] s_trickyArguments =
	[
		"plain",
		"with space",
		"semi;colon",
		"Alpha=800;Beta=4000",
		"-Settings=A=1;B=2",
		"-Key=value with spaces",
		"quote\"inside",
		"\"",
		"\"quoted whole\"",
		"back\\slash",
		"trailing\\",
		"trailing space\\",
		"back\\\"quote",
		"",
		"tab\there",
		"-ExecCmds=Automation RunTests Project.Math;Quit",
		"ünïcödé ✓",
		"%PATH%",
		"a&b|c<d>e^f",
	];

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
		// Stop any run a failed test left running (only processes this test started, by their recorded identity).
		foreach (RunRecord record in _registry.List())
		{
			if (record.Process is ProcessIdentity process && process.IsAlive())
			{
				try
				{
					using Process running = Process.GetProcessById(process.Pid);
					running.Kill(entireProcessTree: true);
				}
				catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
				{
				}
			}
		}
		_state.Dispose();
	}

	private static List<string> Wrapper => Child.Command("wrap");

	private Task<RunStartResult> StartAsync(string name, IReadOnlyList<string> command, LockPriority? priority = null, bool force = false, bool allowSleep = false)
	{
		return RunStarter.StartAsync(_registry, new RunStartRequest
		{
			Name = name,
			Owner = "test",
			Command = command,
			Priority = priority,
			Force = force,
			AllowSleep = allowSleep,
			WorkingDirectory = _state.Root,
		}, Wrapper, TestContext.CancellationToken);
	}

	/// <summary>
	/// A run's output. The wrapper records the end and exits, but its handle on the output stays open until its process has
	/// gone, so the output is read with write sharing (File.ReadAllText would fail on that handle).
	/// </summary>
	private static string ReadOutput(string path)
	{
		return StateFiles.ReadShared(path) ?? throw new AssertFailedException($"Cannot read the run's output {path}.");
	}

	private RunRecord WaitForEnd(string name, TimeSpan? timeout = null)
	{
		RunRecord? record = null;
		Wait.Until(() => (record = _registry.Read(name))?.Ended is not null, timeout ?? s_timeout, $"run '{name}' to record its end");
		return record!;
	}

	[TestMethod]
	public async Task ArgumentsPassThroughIntact()
	{
		string output = _state.PathOf("args.json");
		RunStartResult result = await StartAsync("Args", Child.Command(["echo-args", output, "0", .. s_trickyArguments]));
		Assert.IsTrue(result.Registered);
		RunRecord record = WaitForEnd("Args");
		string[] received = JsonSerializer.Deserialize<string[]>(File.ReadAllText(output))!;
		CollectionAssert.AreEqual(s_trickyArguments, received, "Received: " + string.Join(" | ", received));
		CollectionAssert.AreEqual(new[] { "echo-args", output, "0" }.Concat(s_trickyArguments).ToArray(), record.Arguments.ToArray(), "The record keeps the arguments exactly.");
		Assert.AreEqual(0, record.ExitCode);
	}

	[TestMethod]
	public async Task EndExitCodeAndLastLineAreRecorded()
	{
		RunStartResult result = await StartAsync("Sleeper", Child.Command("sleep", "300", "7"));
		Assert.AreEqual(Child.ExecutablePath, result.Record.Command);
		Assert.IsNotNull(result.Record.Pid, "The wrapper recorded itself before the start returned.");
		Assert.AreEqual(result.WrapperPid, result.Record.Pid);
		Assert.AreEqual(result.Detach, result.Record.Detach);

		RunRecord record = WaitForEnd("Sleeper");
		Assert.AreEqual(7, record.ExitCode);
		Assert.AreEqual("slept 300", record.LastLine);
		Assert.IsGreaterThanOrEqualTo(record.Started!.Value, record.Ended!.Value);
		Assert.AreEqual(RunState.Exited, RunRegistry.GetState(record, DateTime.UtcNow));
		string log = ReadOutput(record.OutputFile!);
		StringAssert.Contains(log, "sleeping");
		StringAssert.Contains(log, $"{RunRegistry.WrapperLinePrefix}Sleeper' (owner test) started");
		StringAssert.Contains(log, "exit code 7");
	}

	[TestMethod]
	public async Task KeepAwakeIsTheDefaultAndAllowSleepIsRecorded()
	{
		await StartAsync("Awake", Child.Command("echo-args", _state.PathOf("awake.json"), "0"));
		await StartAsync("Sleepy", Child.Command("echo-args", _state.PathOf("sleepy.json"), "0"), allowSleep: true);
		RunRecord awake = WaitForEnd("Awake");
		RunRecord sleepy = WaitForEnd("Sleepy");
		Assert.IsFalse(awake.AllowSleep, "Runs keep the machine awake unless -allow-sleep.");
		Assert.IsTrue(sleepy.AllowSleep);
		Assert.AreEqual(0, awake.ExitCode);
		Assert.AreEqual(0, sleepy.ExitCode);
		// A normal user process is granted the request: the wrapper writes a line only when Windows refuses it.
		Assert.DoesNotContain("refused the keep-awake request", ReadOutput(awake.OutputFile!));
	}

	[TestMethod]
	public async Task StandardErrorGoesToTheOutputToo()
	{
		await StartAsync("Echo", Child.Command("echo-args", _state.PathOf("a.json"), "0", "x"));
		RunRecord record = WaitForEnd("Echo");
		string log = ReadOutput(record.OutputFile!);
		StringAssert.Contains(log, "echo-args: first line");
		StringAssert.Contains(log, "echo-args: to standard error");
		Assert.AreEqual("echo-args: 1 arguments", record.LastLine);
	}

	[TestMethod]
	public async Task CommandSeesTheRunsLockDefaultsAndWorkingDirectory()
	{
		string output = _state.PathOf("env.json");
		await StartAsync("EnvRun", Child.Command("env", output, EditorLock.NameVariable, EditorLock.PriorityVariable), LockPriority.High);
		WaitForEnd("EnvRun");
		Dictionary<string, string?> values = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(output))!;
		Assert.AreEqual("EnvRun", values[EditorLock.NameVariable]);
		Assert.AreEqual("High", values[EditorLock.PriorityVariable]);
		Assert.AreEqual(Path.TrimEndingDirectorySeparator(_state.Root), Path.TrimEndingDirectorySeparator(values["CurrentDirectory"]!), ignoreCase: OperatingSystem.IsWindows());
	}

	[TestMethod]
	public async Task MissingCommandRecordsTheFailure()
	{
		await StartAsync("Missing", [_state.PathOf("no-such-program.exe"), "x"]);
		RunRecord record = WaitForEnd("Missing");
		Assert.AreEqual(UakExitCodes.Failure, record.ExitCode);
		StringAssert.StartsWith(record.LastLine, "The run could not start:");
	}

	[TestMethod]
	public async Task WrapperThatFailsBeforeRecordingItselfIsReported()
	{
		// A "wrapper" that exits at once without touching the record.
		RunStartException exception = await Assert.ThrowsExactlyAsync<RunStartException>(() => RunStarter.StartAsync(_registry,
			new RunStartRequest { Name = "Broken", Owner = "test", Command = Child.Command("sleep", "0", "0") }, Child.Command("no-such-mode"), TestContext.CancellationToken));
		StringAssert.Contains(exception.Message, "before recording itself");
		RunRecord record = _registry.Read("Broken")!;
		Assert.AreEqual(2, record.ExitCode);
		Assert.IsNotNull(record.Ended);
		Assert.AreEqual(RunState.Exited, RunRegistry.GetState(record, DateTime.UtcNow));
	}

	[TestMethod]
	public async Task NamesAreCheckedAndNeverReplaceARunningRun()
	{
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => StartAsync("bad name", Child.Command("sleep", "0", "0")));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => StartAsync("../escape", Child.Command("sleep", "0", "0")));

		await StartAsync("Once", Child.Command("sleep", "3000", "0"));
		RunStartException running = await Assert.ThrowsExactlyAsync<RunStartException>(() => StartAsync("Once", Child.Command("sleep", "0", "0"), force: true));
		StringAssert.Contains(running.Message, "still running");
		WaitForEnd("Once");

		RunStartException recorded = await Assert.ThrowsExactlyAsync<RunStartException>(() => StartAsync("Once", Child.Command("sleep", "0", "0")));
		StringAssert.Contains(recorded.Message, "-force");
		await StartAsync("Once", Child.Command("sleep", "0", "4"), force: true);
		Assert.AreEqual(4, WaitForEnd("Once").ExitCode);
	}

	[TestMethod]
	public void DetachedLauncherPassesArgumentsIntact()
	{
		// The wrapper's own command line goes through CreateProcess on Windows: check its quoting with the tricky arguments.
		string output = _state.PathOf("direct.json");
		using DetachedProcess process = DetachedLauncher.Start(_ => Child.Command(["echo-args", output, "5", .. s_trickyArguments]), _state.Root);
		Wait.Until(() => process.ExitCode is not null, s_timeout, "the detached process to exit");
		Assert.AreEqual(5, process.ExitCode);
		CollectionAssert.AreEqual(s_trickyArguments, JsonSerializer.Deserialize<string[]>(File.ReadAllText(output)));
	}

	[TestMethod]
	public async Task RunSurvivesItsStarterBeingKilled()
	{
		(Process starter, Dictionary<string, JsonElement> started) = await StartFromChildAsync("Orphan", job: null);
		starter.Kill();
		await starter.WaitForExitAsync(TestContext.CancellationToken);
		Assert.AreEqual(RunState.Running, RunRegistry.GetState(_registry.Read("Orphan")!, DateTime.UtcNow));
		RunRecord record = WaitForEnd("Orphan");
		Assert.AreEqual(5, record.ExitCode);
		Assert.AreEqual("slept 2000", record.LastLine);
		Assert.AreEqual(started["WrapperPid"].GetInt32(), record.Pid);
	}

	[TestMethod]
	public async Task RunSurvivesItsStartersJobClosing()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Job objects are Windows only; the Unix path (setsid) is a TODO test.");
			return;
		}
		(Process starter, Dictionary<string, JsonElement> started) result;
		using (JobObject job = new(allowBreakaway: true))
		{
			result = await StartFromChildAsync("Survivor", job);
			if (result.started["Detach"].GetString() != "breakaway")
			{
				Assert.Inconclusive("The environment the tests run in refuses breakaway from its job, so this cannot be tested here.");
			}
			// Closing the job kills the starter, as Claude Code closing a shell's job does.
		}
		await result.starter.WaitForExitAsync(TestContext.CancellationToken);
		// The starter's output pipe closes at once: the run never inherited it (a shell waiting on it would not hang).
		string rest = await result.starter.StandardOutput.ReadToEndAsync(TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
		Assert.IsNotNull(rest);
		Assert.AreEqual(RunState.Running, RunRegistry.GetState(_registry.Read("Survivor")!, DateTime.UtcNow));

		RunRecord record = WaitForEnd("Survivor");
		Assert.AreEqual(5, record.ExitCode);
		Assert.AreEqual("slept 2000", record.LastLine);
		Assert.AreEqual("breakaway", record.Detach);
	}

	[TestMethod]
	public async Task JobThatRefusesBreakawayFallsBackInsideIt()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Job objects are Windows only.");
			return;
		}
		using (JobObject job = new(allowBreakaway: false))
		{
			(Process starter, Dictionary<string, JsonElement> started) = await StartFromChildAsync("Trapped", job);
			Assert.AreEqual("job", started["Detach"].GetString());
			Assert.AreEqual("job", _registry.Read("Trapped")!.Detach);
		}
		// Inside the job, the run dies with it: no end is recorded.
		Wait.Until(() => RunRegistry.GetState(_registry.Read("Trapped")!, DateTime.UtcNow) == RunState.Died, TimeSpan.FromSeconds(10), "the run to die with its job");
		Assert.IsNull(_registry.Read("Trapped")!.ExitCode);
	}

	/// <summary>
	/// Starts a run of "sleep 2000, exit 5" from a separate starter process (the test child), optionally inside a job, and
	/// returns the starter (still running) and what it started.
	/// </summary>
	private async Task<(Process Starter, Dictionary<string, JsonElement> Started)> StartFromChildAsync(string name, JobObject? job)
	{
		string resultFile = _state.PathOf(name + ".started.json");
		Process starter = _state.StartChildWithPipes(["start-run", _state.StateDirectory, name, resultFile, "--", .. Child.Command("sleep", "2000", "5")]);
		if (job is not null && OperatingSystem.IsWindows())
		{
			job.Assign(starter);
		}
		await starter.StandardInput.WriteLineAsync("go");
		await starter.StandardInput.FlushAsync(TestContext.CancellationToken);
		string? line = await starter.StandardOutput.ReadLineAsync(TestContext.CancellationToken).AsTask().WaitAsync(s_timeout, TestContext.CancellationToken);
		Assert.AreEqual("STARTED", line, "The starter did not start the run.");
		Dictionary<string, JsonElement> started = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(resultFile))!;
		Assert.IsTrue(started["Registered"].GetBoolean());
		return (starter, started);
	}
}
