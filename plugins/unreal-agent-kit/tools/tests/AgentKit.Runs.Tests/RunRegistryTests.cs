// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Text;
using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Tests;

namespace AgentKit.Runs.Tests;

[TestClass]
public sealed class RunRegistryTests
{
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

	[TestMethod]
	public void RecordsRoundTripWithTheProtocolFields()
	{
		DateTime started = new(2026, 10, 1, 6, 5, 0, DateTimeKind.Utc);
		RunRecord record = new()
		{
			Name = "E3-Verify",
			Owner = "E3",
			Pid = 1234,
			ProcessStart = started,
			Command = "Build.bat",
			Arguments = ["a b", "c;d"],
			CommandLine = "Build.bat \"a b\" c;d",
			OutputFile = "out.log",
			Priority = LockPriority.High,
			Started = started,
		};
		_registry.Write(record);
		string text = File.ReadAllText(_registry.GetRecordFile("E3-Verify"));
		foreach (string field in new[] { "Name", "Owner", "Pid", "ProcessStart", "Command", "Arguments", "CommandLine", "OutputFile", "ResultFile",
			"Priority", "Adopted", "Started", "Ended", "ExitCode", "LastLine" })
		{
			StringAssert.Contains(text, $"\"{field}\":");
		}
		StringAssert.Contains(text, "\"Priority\": \"High\"");
		StringAssert.Contains(text, "\"Ended\": null");

		RunRecord read = _registry.Read("E3-Verify")!;
		Assert.AreEqual(1234, read.Pid);
		Assert.AreEqual(started, read.ProcessStart);
		CollectionAssert.AreEqual(new[] { "a b", "c;d" }, read.Arguments);
		Assert.AreEqual(LockPriority.High, read.Priority);
		Assert.HasCount(1, _registry.List());
	}

	[TestMethod]
	public void ReadsALegacyRecord()
	{
		// A record as another tool writes it, with only the protocol fields: its readers and ours share the protocol.
		Directory.CreateDirectory(_registry.Directory);
		File.WriteAllText(_registry.GetRecordFile("Old"), """
			{
			    "Name": "Old", "Owner": "E8", "Pid": 4321, "ProcessStart": "2026-10-01T05:00:00.123Z",
			    "Command": "C:\\x\\Verify.ps1", "Arguments": ["-Keep"], "CommandLine": "Verify.ps1 -Keep",
			    "OutputFile": null, "ResultFile": null, "Priority": "Normal", "Adopted": false,
			    "Started": "2026-10-01T05:00:00.456Z", "Ended": "2026-10-01T06:00:00.000Z", "ExitCode": 0, "LastLine": "PASSED"
			}
			""");
		RunRecord record = _registry.Read("Old")!;
		Assert.AreEqual(LockPriority.Normal, record.Priority);
		Assert.AreEqual(0, record.ExitCode);
		Assert.AreEqual(RunState.Exited, RunRegistry.GetState(record, DateTime.UtcNow));
	}

	[TestMethod]
	public void StatesFollowTheProcessAndTheRecordedEnd()
	{
		DateTime now = DateTime.UtcNow;
		ProcessIdentity self = ProcessIdentity.Current;
		ProcessIdentity dead = Child.DeadProcess();
		Assert.AreEqual(RunState.Starting, RunRegistry.GetState(new RunRecord { Started = now }, now));
		Assert.AreEqual(RunState.NeverStarted, RunRegistry.GetState(new RunRecord { Started = now.AddMinutes(-5) }, now));
		Assert.AreEqual(RunState.Running, RunRegistry.GetState(new RunRecord { Pid = self.Pid, ProcessStart = self.StartTimeUtc }, now));
		Assert.AreEqual(RunState.Died, RunRegistry.GetState(new RunRecord { Pid = dead.Pid, ProcessStart = dead.StartTimeUtc }, now));
		Assert.AreEqual(RunState.Ended, RunRegistry.GetState(new RunRecord { Pid = dead.Pid, ProcessStart = dead.StartTimeUtc, Adopted = true }, now));
		Assert.AreEqual(RunState.Exited, RunRegistry.GetState(new RunRecord { Pid = dead.Pid, ProcessStart = dead.StartTimeUtc, ExitCode = 3 }, now));
		// This process's ID with another start time is a different process: the run's has gone.
		Assert.AreEqual(RunState.Died, RunRegistry.GetState(new RunRecord { Pid = self.Pid, ProcessStart = self.StartTimeUtc!.Value.AddHours(-1) }, now));
		// A recorded end wins over a process that still lingers (the wrapper exits just after recording it).
		Assert.AreEqual(RunState.Exited, RunRegistry.GetState(new RunRecord { Pid = self.Pid, ProcessStart = self.StartTimeUtc, Ended = now, ExitCode = 0 }, now));
		// An exit code alone (no end time) does not: the process still runs.
		Assert.AreEqual(RunState.Running, RunRegistry.GetState(new RunRecord { Pid = self.Pid, ProcessStart = self.StartTimeUtc, ExitCode = 0 }, now));
	}

	[TestMethod]
	public void ForceReplacesARunWhoseEndIsRecordedWhileItsWrapperLingers()
	{
		// As the wrapper leaves it: the end recorded, its own process not yet gone.
		ProcessIdentity self = ProcessIdentity.Current;
		_registry.Write(new RunRecord { Name = "Lingers", Owner = "test", Pid = self.Pid, ProcessStart = self.StartTimeUtc, Started = DateTime.UtcNow, Ended = DateTime.UtcNow, ExitCode = 0 });
		RunStarter.CheckNameFree(_registry, "Lingers", force: true);
		RunStartException refused = Assert.ThrowsExactly<RunStartException>(() => RunStarter.CheckNameFree(_registry, "Lingers", force: false));
		StringAssert.Contains(refused.Message, "-force");
	}

	[TestMethod]
	public void LastLineSkipsWrapperLinesAndReadsUtf16()
	{
		string utf8 = _state.PathOf("utf8.log");
		File.WriteAllText(utf8, "first\r\nResult: PASSED\r\nlast one  \r\n\r\nUakRun 'x' ended, exit code 0\r\n");
		Assert.AreEqual("last one", RunRegistry.GetLastLine(utf8));
		Assert.AreEqual("Result: PASSED", RunRegistry.GetLastLine(utf8, RunRegistry.VerdictPattern()));

		string utf16 = _state.PathOf("utf16.log");
		File.WriteAllText(utf16, "one\ntwo ✓\n", Encoding.Unicode);
		Assert.AreEqual("two ✓", RunRegistry.GetLastLine(utf16));

		Assert.AreEqual("", RunRegistry.GetLastLine(_state.PathOf("missing.log")));
		Assert.AreEqual("", RunRegistry.GetLastLine(null));
	}

	[TestMethod]
	public void DisplayLineFallsBackToTheResultFile()
	{
		string result = _state.PathOf("result.txt");
		RunRecord record = new() { ResultFile = result };
		Assert.AreEqual("(result file not written yet)", RunRegistry.GetDisplayLine(record, RunState.Running));
		File.WriteAllText(result, "Step 1 PASSED\nStep 2 FAILED\nsummary\n");
		Assert.AreEqual("Step 2 FAILED", RunRegistry.GetDisplayLine(record, RunState.Running));
		record.LastLine = "recorded";
		Assert.AreEqual("recorded", RunRegistry.GetDisplayLine(record, RunState.Exited));
	}

	[TestMethod]
	public void NamesAreValidated()
	{
		foreach (string name in new[] { "A", "E3-Verify3", "run_1.2" })
		{
			Assert.IsTrue(RunRegistry.IsValidName(name), name);
		}
		foreach (string? name in new[] { null, "", "a b", "../x", "-dash", ".dot", "a/b", "a\\b", "a:b", new string('x', 129) })
		{
			Assert.IsFalse(RunRegistry.IsValidName(name), name);
		}
	}

	[TestMethod]
	public void ListShowsRunningFirstThenLatestAndFilters()
	{
		DateTime now = DateTime.UtcNow;
		ProcessIdentity self = ProcessIdentity.Current;
		_registry.Write(new RunRecord { Name = "Old", Owner = "E1", Pid = 1, ProcessStart = now.AddYears(-1), Started = now.AddHours(-3), Ended = now.AddHours(-2), ExitCode = 0, LastLine = "done" });
		_registry.Write(new RunRecord { Name = "New", Owner = "E2", Pid = 1, ProcessStart = now.AddYears(-1), Started = now.AddHours(-1), Ended = now.AddMinutes(-30), ExitCode = 1, LastLine = "broke" });
		_registry.Write(new RunRecord { Name = "Live", Owner = "E3", Pid = self.Pid, ProcessStart = self.StartTimeUtc, Started = now.AddHours(-5) });

		List<RunsListCommand.RunRow> all = RunsListCommand.GetRows(_registry, all: true, "*", now);
		CollectionAssert.AreEqual(new[] { "Live", "New", "Old" }, all.Select(r => r.Name).ToArray());
		Assert.AreEqual("exited 1", all[1].StateText);
		Assert.AreEqual("30m 00s", all[1].Elapsed);

		List<RunsListCommand.RunRow> running = RunsListCommand.GetRows(_registry, all: false, "*", now);
		CollectionAssert.AreEqual(new[] { "Live" }, running.Select(r => r.Name).ToArray());
		CollectionAssert.AreEqual(new[] { "New" }, RunsListCommand.GetRows(_registry, all: true, "n?w", now).Select(r => r.Name).ToArray());

		string table = RunsListCommand.FormatTable(all);
		StringAssert.StartsWith(table, "Name  Owner  PID");
		StringAssert.Contains(table, "broke");
	}

	[TestMethod]
	public async Task AdoptRecordsARunningProcess()
	{
		Process sleeper = _state.StartChild("sleep", "1500", "0");
		int code = await new RunsAdoptCommand().RunAsync(_state.Context,
			[$"-pid={sleeper.Id}", "-name=Adopted", "-owner=lead", "--", "sleep", "1500"], TestContext.CancellationToken);
		Assert.AreEqual(UakExitCodes.Success, code);
		RunRecord record = _registry.Read("Adopted")!;
		Assert.IsTrue(record.Adopted);
		Assert.AreEqual(sleeper.Id, record.Pid);
		Assert.AreEqual("sleep", record.Command);
		Assert.AreEqual(RunState.Running, RunRegistry.GetState(record, DateTime.UtcNow));
		await sleeper.WaitForExitAsync(TestContext.CancellationToken);
		Assert.AreEqual(RunState.Ended, RunRegistry.GetState(record, DateTime.UtcNow));

		// A name in use without -force, and a process that is not running, both fail.
		Assert.AreEqual(UakExitCodes.Failure, await new RunsAdoptCommand().RunAsync(_state.Context,
			[$"-pid={Environment.ProcessId}", "-name=Adopted", "-owner=lead"], TestContext.CancellationToken));
		Assert.AreEqual(UakExitCodes.Failure, await new RunsAdoptCommand().RunAsync(_state.Context,
			[$"-pid={sleeper.Id}", "-name=Gone", "-owner=lead"], TestContext.CancellationToken));
	}

	[TestMethod]
	public async Task CommandsRejectBadUsage()
	{
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new RunsStartCommand().RunAsync(_state.Context, ["-name=x", "-owner=y"], TestContext.CancellationToken));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new RunsStartCommand().RunAsync(_state.Context, ["-owner=y", "--", "x"], TestContext.CancellationToken));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new RunsStartCommand().RunAsync(_state.Context, ["-name=x", "-owner=y", "-priority=Top", "--", "x"], TestContext.CancellationToken));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new RunsListCommand().RunAsync(_state.Context, ["-bogus"], TestContext.CancellationToken));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new RunsAdoptCommand().RunAsync(_state.Context, ["-name=x", "-owner=y"], TestContext.CancellationToken));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new RunsWrapCommand().RunAsync(_state.Context, [], TestContext.CancellationToken));
	}

	[TestMethod]
	public void WrapperCommandRelaunchesThisUakForTheSameContext()
	{
		List<string> command = RunsStartCommand.GetWrapperCommand(_state.Context);
		Assert.AreEqual(Environment.ProcessPath, command[0]);
		CollectionAssert.Contains(command, "-project=" + _state.ProjectFile);
		CollectionAssert.AreEqual(new[] { "runs", "_wrap" }, command.TakeLast(2).ToArray());
		foreach (IUakCommand each in new IUakCommand[] { new RunsStartCommand(), new RunsListCommand(), new RunsAdoptCommand(), new RunsWrapCommand() })
		{
			Assert.IsFalse(each.RequiresEngine, each.Name);
			StringAssert.StartsWith(each.Name, "runs ");
		}
	}
}
