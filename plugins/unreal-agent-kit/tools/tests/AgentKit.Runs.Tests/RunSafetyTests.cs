// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

// Detached runs under contention and failure: racing starters, reused names, a record that cannot be written, an inherited
// lock marker, and read-only commands against a project that has no state yet. Every test uses its own temporary state.

using System.Diagnostics;
using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Tests;

namespace AgentKit.Runs.Tests;

[TestClass]
public sealed class RunSafetyTests
{
	private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

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

	private Task<RunStartResult> StartAsync(string name, IReadOnlyList<string> command, bool force = false)
	{
		return RunStarter.StartAsync(_registry, new RunStartRequest
		{
			Name = name,
			Owner = "test",
			Command = command,
			Force = force,
			WorkingDirectory = _state.Root,
		}, Child.Command("wrap"), TestContext.CancellationToken);
	}

	/// <summary>A run's output, read with write sharing: the wrapper's handle on it stays open until its process has gone.</summary>
	private static string ReadOutput(string path)
	{
		return StateFiles.ReadShared(path) ?? throw new AssertFailedException($"Cannot read the run's output {path}.");
	}

	private RunRecord WaitForEnd(string name)
	{
		RunRecord? record = null;
		Wait.Until(() => (record = _registry.Read(name))?.Ended is not null, s_timeout, $"run '{name}' to record its end");
		return record!;
	}

	/// <summary>Starts the same name from several threads at once; returns the results of those that started.</summary>
	private async Task<List<RunStartResult>> RaceAsync(string name, int racers, IReadOnlyList<string> command, bool force)
	{
		using Barrier barrier = new(racers);
		Task<RunStartResult>[] starts = [.. Enumerable.Range(0, racers).Select(_ => Task.Run(() =>
		{
			barrier.SignalAndWait(TestContext.CancellationToken);
			return StartAsync(name, command, force);
		}, TestContext.CancellationToken))];
		List<RunStartResult> started = [];
		List<string> refusals = [];
		foreach (Task<RunStartResult> start in starts)
		{
			try
			{
				started.Add(await start);
			}
			catch (RunStartException exception)
			{
				refusals.Add(exception.Message);
			}
		}
		Assert.HasCount(racers - started.Count, refusals);
		return started;
	}

	// M1: a name is claimed atomically.

	[TestMethod]
	public async Task ConcurrentStartsOfOneNameStartOneRun()
	{
		List<RunStartResult> started = await RaceAsync("Race", 8, Child.Command("sleep", "1000", "0"), force: false);
		Assert.HasCount(1, started, "Exactly one starter may take the name.");
		RunRecord record = WaitForEnd("Race");
		Assert.AreEqual(started[0].WrapperPid, record.Pid, "The record is the winner's run.");
		Assert.AreEqual(0, record.ExitCode);
		Assert.IsFalse(File.Exists(Path.Combine(_registry.Directory, "Race" + RunClaim.Extension)), "The claim goes once the run is recorded.");

		// -force replaces the finished run, but racing -force starters still start only one.
		List<RunStartResult> forced = await RaceAsync("Race", 6, Child.Command("sleep", "500", "4"), force: true);
		Assert.HasCount(1, forced, "Exactly one -force starter may take the name.");
		Assert.AreEqual(4, WaitForEnd("Race").ExitCode);
	}

	[TestMethod]
	public void StaleClaimOfADeadProcessIsTakenOver()
	{
		Directory.CreateDirectory(_registry.Directory);
		ProcessIdentity dead = Child.DeadProcess();
		string claimFile = Path.Combine(_registry.Directory, "Stale" + RunClaim.Extension);
		File.WriteAllText(claimFile, $"{{\"Pid\": {dead.Pid}, \"ProcessStart\": \"{UakJson.FormatTime(dead.StartTimeUtc ?? DateTime.UtcNow)}\"}}");
		using (RunClaim claim = RunClaim.Acquire(_registry, "Stale", TimeSpan.FromSeconds(5)))
		{
			// Held: a second claimer gives up after its timeout.
			Assert.ThrowsExactly<RunStartException>(() => RunClaim.Acquire(_registry, "Stale", TimeSpan.FromMilliseconds(200)));
		}
		Assert.IsFalse(File.Exists(claimFile));
	}

	// L4: a fresh output for every start.

	[TestMethod]
	public async Task EveryStartBeginsWithAnEmptyOutput()
	{
		string log = _registry.GetDefaultOutputFile("Fresh");
		Directory.CreateDirectory(_registry.Directory);
		File.WriteAllText(log, "a stale line from an older run\n");
		await StartAsync("Fresh", Child.Command("sleep", "0", "0"));
		RunRecord first = WaitForEnd("Fresh");
		Assert.AreEqual("slept 0", first.LastLine);
		Assert.DoesNotContain("stale line", ReadOutput(log));

		// A -force reuse: the old run's lines are gone too, so they can never be the new run's last line.
		await StartAsync("Fresh", Child.Command("echo-args", _state.PathOf("args.json"), "0", "x"), force: true);
		RunRecord second = WaitForEnd("Fresh");
		string text = ReadOutput(log);
		Assert.DoesNotContain("slept 0", text);
		StringAssert.Contains(text, "echo-args: first line");
		Assert.AreEqual("echo-args: 1 arguments", second.LastLine);
	}

	// M7: the end is never lost.

	[TestMethod]
	public void EndGoesToTheOutputWhenTheRecordCannotBeWritten()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Only Windows lets an open file block its replacement.");
			return;
		}
		RunRecord record = new() { Name = "Locked", Owner = "test", Started = DateTime.UtcNow };
		_registry.Write(record);
		string recordFile = _registry.GetRecordFile("Locked");
		List<string> lines = [];
		using (new FileStream(recordFile, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			bool recorded = RunWrapper.RecordEnd(recordFile, record, _state.PathOf("missing.log"), 3, null, lines.Add, attempts: 1, retryDelay: TimeSpan.Zero);
			Assert.IsFalse(recorded);
		}
		Assert.HasCount(1, lines);
		StringAssert.StartsWith(lines[0], RunRegistry.WrapperLinePrefix + "Locked': could not record the end");
		StringAssert.Contains(lines[0], "exit code 3");

		// Once the file is free again, the end is recorded.
		Assert.IsTrue(RunWrapper.RecordEnd(recordFile, record, _state.PathOf("missing.log"), 4, "it failed", lines.Add, attempts: 2, retryDelay: TimeSpan.Zero));
		RunRecord read = _registry.Read("Locked")!;
		Assert.AreEqual(4, read.ExitCode);
		Assert.AreEqual("it failed", read.LastLine);
		Assert.IsNotNull(read.Ended);
	}

	// H2: a run outlives its starter's lock.

	[TestMethod]
	public async Task RunCommandDoesNotInheritAHeldLock()
	{
		string output = _state.PathOf("env.json");
		string? old = Environment.GetEnvironmentVariable(EditorLock.HeldVariable);
		try
		{
			// As if `uak runs start` ran inside `uak lock run`.
			Environment.SetEnvironmentVariable(EditorLock.HeldVariable, @"Global\UnrealAgentKit_EditorLock_test:0123");
			await StartAsync("Unmarked", Child.Command("env", output, EditorLock.HeldVariable, EditorLock.NameVariable));
		}
		finally
		{
			Environment.SetEnvironmentVariable(EditorLock.HeldVariable, old);
		}
		WaitForEnd("Unmarked");
		Dictionary<string, string?> values = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(output))!;
		Assert.IsNull(values[EditorLock.HeldVariable], "A detached run's lock requests must queue, not join a hold that ends without it.");
		Assert.AreEqual("Unmarked", values[EditorLock.NameVariable]);
	}

	// H6: a bare program name is looked up on PATH only.

	[TestMethod]
	public async Task ProgramPlantedInTheWorkingDirectoryNeverRuns()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("The planted program is a .cmd script.");
			return;
		}
		string marker = _state.PathOf("planted-ran.txt");
		File.WriteAllText(_state.PathOf("uakplanted.cmd"), $"@echo planted> \"{marker}\"\r\n");
		RunStartException refused = await Assert.ThrowsExactlyAsync<RunStartException>(() => StartAsync("Planted", ["uakplanted.cmd"]));
		StringAssert.Contains(refused.Message, "not on PATH");
		RunRecord record = _registry.Read("Planted")!;
		Assert.AreEqual(UakExitCodes.Failure, record.ExitCode);
		StringAssert.Contains(record.LastLine, "'uakplanted.cmd' is not on PATH");
		Assert.AreEqual(RunState.Exited, RunRegistry.GetState(record, DateTime.UtcNow));
		await Task.Delay(500, TestContext.CancellationToken);
		Assert.IsFalse(File.Exists(marker), "A program planted in the working directory ran.");

		// A bare name that is on PATH runs, and the record names the program found.
		await StartAsync("OnPath", ["cmd", "/d", "/c", "exit 3"]);
		RunRecord onPath = WaitForEnd("OnPath");
		Assert.AreEqual(3, onPath.ExitCode);
		Assert.IsTrue(Path.IsPathFullyQualified(onPath.Command), onPath.Command);
		StringAssert.EndsWith(onPath.Command.ToLowerInvariant(), "cmd.exe");
	}

	// L8: read-only commands create nothing.

	[TestMethod]
	public async Task ReadOnlyCommandsCreateNoStateFolder()
	{
		string root = _state.PathOf("Bare");
		Directory.CreateDirectory(root);
		File.WriteAllText(Path.Combine(root, "Bare.uproject"), "{}");
		UakContext context = UakContextResolver.Resolve(new UakResolveOptions { ProjectArgument = root, RequireEngine = false, CurrentDirectory = root });
		string saved = Path.Combine(CanonicalPath.Get(root), "Saved");
		Assert.AreEqual(Path.Combine(saved, "AgentKit"), context.StateDirectory.FullName);
		Assert.IsFalse(Directory.Exists(saved), "Resolving the context creates no folder.");

		Assert.AreEqual(UakExitCodes.Success, await new LockStatusCommand().RunAsync(context, [], TestContext.CancellationToken));
		Assert.AreEqual(UakExitCodes.Success, await new RunsListCommand().RunAsync(context, ["-all"], TestContext.CancellationToken));
		Assert.IsFalse(Directory.Exists(saved), "lock status and runs list create no folder.");
	}
}
