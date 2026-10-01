// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

// The editor lock's safety across real processes: one project reached two ways is one lock, a lock request under a hold of
// the same lock joins it instead of deadlocking, and the command `uak lock run` runs never outlives the hold. Each holder
// appends "enter <name>" and "leave <name>" to a log while it holds the lock, and every test checks no two holds overlap.

using System.Diagnostics;
using System.Text.Json;
using AgentKit.Core;
using AgentKit.Tests;
using Microsoft.Extensions.Logging;

namespace AgentKit.Locking.Tests;

[TestClass]
public sealed class LockSafetyTests
{
	private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);
	private static readonly EditorLockOptions s_fast = new() { PollInterval = TimeSpan.FromMilliseconds(100) };

	private TempState _state = null!;
	private string _log = null!;
	private readonly List<string> _links = [];

	public TestContext TestContext { get; set; } = null!;

	[TestInitialize]
	public void Initialize()
	{
		_state = new TempState();
		_log = _state.PathOf("holds.txt");
	}

	[TestCleanup]
	public void Cleanup()
	{
		// Links first, so deleting the temporary tree never reaches through one.
		foreach (string link in _links)
		{
			DirectoryLinks.Remove(link);
		}
		_state.Dispose();
	}

	private LockPaths Paths => new(_state.Context.StateDirectory);

	private string ReadLog()
	{
		return StateFiles.ReadShared(_log) ?? "";
	}

	/// <summary>The names in the order they held the lock, after checking no two holds overlapped. A killed holder never leaves.</summary>
	private List<string> Holds(params string[] killed)
	{
		List<string> order = [];
		string? current = null;
		foreach (string line in ReadLog().Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] parts = line.Split(' ', 3);
			if (parts[0] == "enter")
			{
				Assert.IsTrue(current is null || killed.Contains(current), $"'{parts[1]}' entered while '{current}' held the lock:\n{ReadLog()}");
				current = parts[1];
				order.Add(current);
			}
			else if (parts[0] == "leave")
			{
				Assert.AreEqual(current, parts[1], $"'{parts[1]}' left without holding the lock:\n{ReadLog()}");
				current = null;
			}
		}
		return order;
	}

	/// <summary>A link to the temporary project's folder, outside it; inconclusive where links cannot be made.</summary>
	private string LinkToProject()
	{
		string link = Path.Combine(Path.GetTempPath(), "UakTest", Guid.NewGuid().ToString("N")[..12] + "-link");
		if (!DirectoryLinks.TryCreate(link, _state.Root))
		{
			Assert.Inconclusive("This system cannot create a directory junction or link.");
		}
		_links.Add(link);
		return link;
	}

	private Process Waiter(string name, string projectFile, string stateDirectory, int holdMilliseconds = 300)
	{
		return _state.StartChild("lock", stateDirectory, projectFile, name, "default", _log, holdMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	/// <summary>Waits for a child to exit, failing (and killing it) after the timeout, so a deadlock fails the test instead of hanging it.</summary>
	private static void ExitWithin(Process process, TimeSpan timeout, string what)
	{
		if (!process.WaitForExit(timeout))
		{
			process.Kill(entireProcessTree: true);
			Assert.Fail($"{what} did not finish within {timeout.TotalSeconds} s: it hangs.");
		}
	}

	// H1: one project, two paths.

	[TestMethod]
	public void ProjectThroughAJunctionHasTheSameLockAndState()
	{
		string link = LinkToProject();
		string linkedProject = Path.Combine(link, "Test.uproject");
		UakContext direct = UakContextResolver.Resolve(new UakResolveOptions { ProjectArgument = _state.ProjectFile, RequireEngine = false, CurrentDirectory = _state.Root });
		UakContext linked = UakContextResolver.Resolve(new UakResolveOptions { ProjectArgument = linkedProject, RequireEngine = false, CurrentDirectory = link });
		Assert.AreEqual(direct.ProjectFile!.FullName, linked.ProjectFile!.FullName, "The resolver gives the canonical project.");
		Assert.AreEqual(direct.StateDirectory.FullName, linked.StateDirectory.FullName, "One project, one state directory.");
		Assert.AreEqual(EditorLock.GetMutexName(direct), EditorLock.GetMutexName(linked));

		// Even a context built from the linked path by hand (not through the resolver) gets the same mutex.
		UakContext byHand = new() { ProjectFile = new FileInfo(linkedProject), StateDirectory = new DirectoryInfo(Path.Combine(link, "Saved", "AgentKit")), Logger = _state.Context.Logger };
		Assert.AreEqual(EditorLock.GetMutexName(_state.Context), EditorLock.GetMutexName(byHand));
		Assert.AreEqual(EditorLock.GetScope(_state.Context), EditorLock.GetScope(byHand));
		StringAssert.StartsWith(EditorLock.GetScope(byHand).ToString(), "project ");
	}

	[TestMethod]
	public void HoldersThroughTwoPathsNeverOverlap()
	{
		string link = LinkToProject();
		string linkedProject = Path.Combine(link, "Test.uproject");
		string linkedState = Path.Combine(link, "Saved", "AgentKit");
		for (int index = 0; index < 3; index++)
		{
			Waiter($"Direct{index}", _state.ProjectFile, _state.StateDirectory, holdMilliseconds: 400);
			Waiter($"Linked{index}", linkedProject, linkedState, holdMilliseconds: 400);
		}
		_state.WaitAll(TimeSpan.FromSeconds(120));
		List<string> holds = Holds();
		Assert.HasCount(6, holds);
	}

	/// <summary>
	/// The temporary project's folder through the loopback administrative share (<c>\\localhost\C$\...</c>), which the
	/// canonical path does not resolve. Inconclusive off Windows, or where the share cannot be reached.
	/// </summary>
	private string LoopbackPathToProject()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Administrative shares are Windows only.");
		}
		string full = Path.GetFullPath(_state.Root);
		string root = Path.GetPathRoot(full)!;
		if (root.Length < 2 || root[1] != ':')
		{
			Assert.Inconclusive($"{full} is not on a drive letter.");
		}
		string share = $@"\\localhost\{char.ToUpperInvariant(root[0])}$\{full[root.Length..]}";
		try
		{
			if (!File.Exists(Path.Combine(share, "Test.uproject")))
			{
				Assert.Inconclusive($"The loopback share {share} cannot be reached.");
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Assert.Inconclusive($"The loopback share {share} cannot be reached: {exception.Message}");
		}
		return share;
	}

	[TestMethod]
	public void ProjectThroughTheLoopbackShareHasTheSameLock()
	{
		string share = LoopbackPathToProject();
		UakContext viaShare = new()
		{
			ProjectFile = new FileInfo(Path.Combine(share, "Test.uproject")),
			StateDirectory = new DirectoryInfo(Path.Combine(share, "Saved", "AgentKit")),
			Logger = _state.Context.Logger,
		};
		// The readable path keeps the share's spelling; the key (the folder's identity) does not.
		StringAssert.StartsWith(EditorLock.GetScope(viaShare).Path, @"\\");
		Assert.AreEqual(EditorLock.GetScope(_state.Context).Key, EditorLock.GetScope(viaShare).Key);
		Assert.AreEqual(EditorLock.GetMutexName(_state.Context), EditorLock.GetMutexName(viaShare));
	}

	[TestMethod]
	public void HoldersThroughTheLoopbackShareNeverOverlap()
	{
		string share = LoopbackPathToProject();
		string shareProject = Path.Combine(share, "Test.uproject");
		string shareState = Path.Combine(share, "Saved", "AgentKit");
		for (int index = 0; index < 3; index++)
		{
			Waiter($"Direct{index}", _state.ProjectFile, _state.StateDirectory, holdMilliseconds: 400);
			Waiter($"Share{index}", shareProject, shareState, holdMilliseconds: 400);
		}
		_state.WaitAll(TimeSpan.FromSeconds(120));
		List<string> holds = Holds();
		Assert.HasCount(6, holds);
	}

	// H2: nested lock requests.

	[TestMethod]
	public void LockRunInsideLockRunJoinsTheHold()
	{
		// lock run -> (child) lock run -> (grandchild) a lock request: all for the same project.
		List<string> inner = Child.Command("lock", _state.StateDirectory, _state.ProjectFile, "Inner", "default", _log, "200");
		List<string> middle = Child.Command(["lock-run", _state.StateDirectory, _state.ProjectFile, "-", "-name=Middle", "--", .. inner]);
		Process outer = _state.StartChild(["lock-run", _state.StateDirectory, _state.ProjectFile, "-", "-name=Outer", "--", .. middle]);
		ExitWithin(outer, TimeSpan.FromSeconds(60), "A lock run inside a lock run");
		Assert.AreEqual(0, outer.ExitCode, ReadLog());
		CollectionAssert.AreEqual(new[] { "Inner" }, Holds());
		StringAssert.Contains(ReadLog(), "joins that hold");
		Assert.IsNull(EditorLock.GetStatus(_state.Context).Holder, "The outer hold is released at the end.");
	}

	[TestMethod]
	public async Task RequestUnderAnotherLocksHoldFailsFast()
	{
		using TempState other = new();
		await using EditorLockHold held = await EditorLock.AcquireAsync(other.Context, "OtherProject", null, s_fast, TestContext.CancellationToken);
		Process child = _state.StartChildWithEnvironment(new Dictionary<string, string?> { [EditorLock.HeldVariable] = held.Marker },
			"lock", _state.StateDirectory, _state.ProjectFile, "Refused", "default", _log, "100");
		ExitWithin(child, TimeSpan.FromSeconds(30), "A request under another lock's hold");
		Assert.AreEqual(3, child.ExitCode);
		StringAssert.Contains(ReadLog(), "refused Refused");
		StringAssert.Contains(ReadLog(), "another editor lock");
		Assert.IsEmpty(Holds());
	}

	[TestMethod]
	public async Task RequestUnderAnEndedHoldFailsFast()
	{
		// A marker for this very lock, but for a hold that is not the one recorded: waiting would queue behind the holder.
		await using EditorLockHold held = await EditorLock.AcquireAsync(_state.Context, "Holder", null, s_fast, TestContext.CancellationToken);
		string stale = $"{held.MutexName}:{Guid.NewGuid():N}";
		Process child = _state.StartChildWithEnvironment(new Dictionary<string, string?> { [EditorLock.HeldVariable] = stale },
			"lock", _state.StateDirectory, _state.ProjectFile, "Stale", "default", _log, "100");
		ExitWithin(child, TimeSpan.FromSeconds(30), "A request under an ended hold");
		Assert.AreEqual(3, child.ExitCode);
		StringAssert.Contains(ReadLog(), "that hold has ended");
		Assert.IsEmpty(Holds());
	}

	[TestMethod]
	public async Task HoldMarksThisProcessAndItsChildren()
	{
		string output = _state.PathOf("env.json");
		Assert.IsNull(Environment.GetEnvironmentVariable(EditorLock.HeldVariable));
		int code = await new LockRunCommand().RunAsync(_state.Context, ["-name=Marked", "--", .. Child.Command("env", output, EditorLock.HeldVariable)], TestContext.CancellationToken);
		Assert.AreEqual(0, code);
		Dictionary<string, string?> values = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(output))!;
		string marker = values[EditorLock.HeldVariable]!;
		StringAssert.Matches(marker, new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(EditorLock.GetMutexName(_state.Context)) + ":[0-9a-f]{32}$"));
		Assert.IsNull(Environment.GetEnvironmentVariable(EditorLock.HeldVariable), "The marker goes with the hold.");
	}

	[TestMethod]
	public void MarkersParse()
	{
		Assert.IsEmpty(HeldMarkers.Parse(null));
		Assert.IsEmpty(HeldMarkers.Parse(" ; "));
		List<(string MutexName, string HoldId)> markers = HeldMarkers.Parse(@"Global\UnrealAgentKit_EditorLock_ab12:0123;Global\Other_cd:;bare");
		Assert.HasCount(3, markers);
		Assert.AreEqual((@"Global\UnrealAgentKit_EditorLock_ab12", "0123"), markers[0]);
		Assert.AreEqual((@"Global\Other_cd", ""), markers[1]);
		Assert.AreEqual(("bare", ""), markers[2]);
		Assert.AreEqual(@"Global\X:id", HeldMarkers.Format(@"Global\X", "id"));
	}

	// H3: the command never outlives the hold.

	[TestMethod]
	public async Task KilledLockRunTakesItsCommandWithIt()
	{
		Process parent = _state.StartChild(["lock-run", _state.StateDirectory, _state.ProjectFile, "-", "-name=Parent", "--", .. Child.Command("hold-log", _log, "Command", "-1")]);
		Wait.Until(() => ReadLog().Contains("enter Command", StringComparison.Ordinal), s_timeout, "the command to start under the lock");
		LockHolderInfo holder = EditorLock.GetStatus(_state.Context).Holder!;
		Assert.AreEqual("Parent", holder.Name);
		ProcessIdentity command = holder.CommandProcess!.Value;
		Assert.AreEqual(GetLoggedPid("Command"), command.Pid, "holder.json records the command's process.");
		Assert.IsTrue(command.IsAlive());
		if (OperatingSystem.IsWindows())
		{
			// And the job that holds its whole tree, named after the hold.
			Assert.AreEqual(CommandJobs.NameFor(holder.HoldId), holder.CommandJob);
			Assert.AreEqual(1, CommandJobs.GetActiveProcesses(holder.CommandJob!));
			StringAssert.Contains(LockStatusCommand.Format(EditorLock.GetStatus(_state.Context), DateTime.UtcNow), $", job {holder.CommandJob}");
		}

		Waiter("Next", _state.ProjectFile, _state.StateDirectory, holdMilliseconds: 100);
		Wait.Until(() => ReadLog().Contains("as 'Next' (Normal), first in line.", StringComparison.Ordinal), s_timeout, "Next to wait at the front");
		// Kill uak (here, the test child running lock run) alone, not its tree.
		parent.Kill(entireProcessTree: false);
		await parent.WaitForExitAsync(TestContext.CancellationToken);

		Wait.Until(() => ReadLog().Contains("enter Next", StringComparison.Ordinal), s_timeout, "Next to take the lock");
		// Next took the lock only once the command had gone (the job died with its holder; Next waits for a recorded command).
		Assert.IsFalse(command.IsAlive(), "The command outlived its lock run, and the next holder overlapped it.");
		_state.WaitAll(s_timeout);
		CollectionAssert.AreEqual(new[] { "Command", "Next" }, Holds("Command"));
	}

	[TestMethod]
	public async Task CancelledLockRunStopsItsCommandBeforeReleasing()
	{
		using CancellationTokenSource cancel = new();
		Task<int> run = new LockRunCommand().RunAsync(_state.Context, ["-name=Cancelled", "--", .. Child.Command("hold-log", _log, "Command", "-1")], cancel.Token);
		Wait.Until(() => ReadLog().Contains("enter Command", StringComparison.Ordinal), s_timeout, "the command to start under the lock");
		ProcessIdentity command = EditorLock.GetStatus(_state.Context).Holder!.CommandProcess!.Value;
		await cancel.CancelAsync();
		await Assert.ThrowsAsync<OperationCanceledException>(() => run);
		Assert.IsFalse(command.IsAlive(), "Cancelling stops the command before the lock is released.");
		Assert.IsNull(EditorLock.GetStatus(_state.Context).Holder);
	}

	[TestMethod]
	public async Task WhatTheCommandLeavesRunningStopsWithTheHold()
	{
		// The command starts a child and exits at once: the child must not run on without the lock.
		int code = await new LockRunCommand().RunAsync(_state.Context, ["-name=Spawner", "--", .. Child.Command("spawn", _log, "Leftover")], TestContext.CancellationToken);
		Assert.AreEqual(0, code);
		ProcessIdentity leftover = ProcessIdentity.TryGet(GetLoggedPid("Leftover")) ?? new ProcessIdentity(GetLoggedPid("Leftover"), null);
		Assert.IsFalse(leftover.IsAlive(), "A process the command left running outlived the hold.");
	}

	[TestMethod]
	public async Task LockRunNeverRunsAProgramPlantedInTheCurrentDirectory()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("The planted program is a .cmd script.");
			return;
		}
		string marker = _state.PathOf("planted-ran.txt");
		File.WriteAllText(_state.PathOf("uakplanted.cmd"), $"@echo planted> \"{marker}\"\r\n");
		string previous = Environment.CurrentDirectory;
		int code;
		try
		{
			// cmd.exe, and CreateProcess, would look in the current directory first.
			Environment.CurrentDirectory = _state.Root;
			code = await new LockRunCommand().RunAsync(_state.Context, ["--", "uakplanted.cmd"], TestContext.CancellationToken);
		}
		finally
		{
			Environment.CurrentDirectory = previous;
		}
		Assert.AreEqual(UakExitCodes.Failure, code);
		Assert.IsFalse(File.Exists(marker), "A program planted in the current directory ran.");
		Assert.IsNull(EditorLock.GetStatus(_state.Context).Holder);
	}

	// The orphan wait: only for a dead holder of this same lock, and for its whole job.

	/// <summary>Starts a "command" that runs until killed, and waits until it has started.</summary>
	private (Process Process, ProcessIdentity Identity) StartCommand(string name)
	{
		Process process = _state.StartChild("hold-log", _log, name, "-1");
		Wait.Until(() => ReadLog().Contains($"enter {name}", StringComparison.Ordinal), s_timeout, $"{name} to start");
		return (process, ProcessIdentity.TryGet(process.Id) ?? new ProcessIdentity(process.Id, null));
	}

	/// <summary>A holder record, as a holder that died left it.</summary>
	private static LockHolderInfo DeadHolder(string name, EditorLockScope scope, ProcessIdentity? command = null, string? job = null)
	{
		ProcessIdentity dead = Child.DeadProcess();
		return new LockHolderInfo
		{
			Name = name,
			Pid = dead.Pid,
			ProcessStart = dead.StartTimeUtc,
			Machine = Environment.MachineName,
			HoldId = Guid.NewGuid().ToString("N"),
			Acquired = DateTime.UtcNow,
			Scope = scope.ToString(),
			ScopeKey = scope.Key,
			CommandPid = command?.Pid,
			CommandProcessStart = command?.StartTimeUtc,
			CommandJob = job,
		};
	}

	[TestMethod]
	public async Task AnotherProjectsHolderRecordInASharedStateFolderIsNeverWaitedFor()
	{
		// Project B keeps its state in project A's folder, as two projects on one engine once shared the fallback folder.
		using TempState other = new();
		ListLogger logger = new();
		UakContext projectB = new() { ProjectFile = new FileInfo(other.ProjectFile), StateDirectory = _state.Context.StateDirectory, Logger = logger };
		EditorLockOptions options = new() { PollInterval = TimeSpan.FromMilliseconds(100), OrphanedCommandTimeout = TimeSpan.FromSeconds(20) };
		(Process _, ProcessIdentity command) = StartCommand("ACommand");

		// A's holder died and its command runs on: A's next holder waits for that, B does not.
		StateFiles.WriteJson(Paths.HolderFile, DeadHolder("AHolder", EditorLock.GetScope(_state.Context), command));
		Stopwatch timer = Stopwatch.StartNew();
		await using (EditorLockHold hold = await EditorLock.AcquireAsync(projectB, "B", null, options, TestContext.CancellationToken))
		{
			Assert.IsFalse(hold.IsNested);
		}
		Assert.IsLessThan(10.0, timer.Elapsed.TotalSeconds, "B waited for project A's command.");

		// A live holder of A's lock (here, this process): its command is never B's to wait for either.
		LockHolderInfo live = DeadHolder("ALive", EditorLock.GetScope(_state.Context), command);
		ProcessIdentity self = ProcessIdentity.Current;
		live.Pid = self.Pid;
		live.ProcessStart = self.StartTimeUtc;
		StateFiles.WriteJson(Paths.HolderFile, live);
		timer.Restart();
		await using (EditorLockHold hold = await EditorLock.AcquireAsync(projectB, "B2", null, options, TestContext.CancellationToken))
		{
			Assert.IsFalse(hold.IsNested);
		}
		Assert.IsLessThan(10.0, timer.Elapsed.TotalSeconds, "B waited for the command of a live holder of another lock.");

		Assert.IsFalse(logger.Lines.Any(line => line.Contains("still runs", StringComparison.Ordinal)), string.Join("\n", logger.Lines));
		Assert.IsTrue(command.IsAlive());
	}

	[TestMethod]
	public async Task ADeadHoldersCommandIsWaitedForByItsOwnLock()
	{
		ListLogger logger = new();
		UakContext context = new() { ProjectFile = _state.Context.ProjectFile, StateDirectory = _state.Context.StateDirectory, Logger = logger };
		(Process process, ProcessIdentity command) = StartCommand("Orphan");
		StateFiles.WriteJson(Paths.HolderFile, DeadHolder("Gone", EditorLock.GetScope(_state.Context), command));

		EditorLockOptions options = new() { PollInterval = TimeSpan.FromMilliseconds(100), OrphanedCommandTimeout = TimeSpan.FromSeconds(30) };
		Task<EditorLockHold> acquire = EditorLock.AcquireAsync(context, "Next", null, options, TestContext.CancellationToken);
		await Task.Delay(1500, TestContext.CancellationToken);
		Assert.IsFalse(acquire.IsCompleted, "The next holder went ahead while the dead holder's command ran.");
		process.Kill();
		await using (EditorLockHold hold = await acquire.WaitAsync(s_timeout, TestContext.CancellationToken))
		{
			Assert.IsFalse(command.IsAlive());
		}
		Assert.IsTrue(logger.Lines.Any(line => line.Contains("'Gone'", StringComparison.Ordinal) && line.Contains("still runs", StringComparison.Ordinal)), string.Join("\n", logger.Lines));
	}

	[TestMethod]
	public async Task ADeadHoldersJobIsWaitedForUntilItIsEmpty()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Named job objects are Windows only.");
			return;
		}
		ListLogger logger = new();
		UakContext context = new() { ProjectFile = _state.Context.ProjectFile, StateDirectory = _state.Context.StateDirectory, Logger = logger };
		// A process the dead holder's command left in its job (no recorded command PID covers it).
		string jobName = CommandJobs.NameFor(Guid.NewGuid().ToString("N"));
		using Microsoft.Win32.SafeHandles.SafeFileHandle job = LockedCommand.WindowsNative.CreateJobObjectW(IntPtr.Zero, jobName);
		Assert.IsFalse(job.IsInvalid, "Cannot create a named job.");
		(Process process, ProcessIdentity leftover) = StartCommand("Leftover");
		Assert.IsTrue(LockedCommand.WindowsNative.AssignProcessToJobObject(job, process.Handle), "Cannot put the process in the job.");
		Assert.AreEqual(1, CommandJobs.GetActiveProcesses(jobName));
		StateFiles.WriteJson(Paths.HolderFile, DeadHolder("Gone", EditorLock.GetScope(_state.Context), command: Child.DeadProcess(), job: jobName));

		EditorLockOptions options = new() { PollInterval = TimeSpan.FromMilliseconds(100), OrphanedCommandTimeout = TimeSpan.FromSeconds(30) };
		Task<EditorLockHold> acquire = EditorLock.AcquireAsync(context, "Next", null, options, TestContext.CancellationToken);
		await Task.Delay(1500, TestContext.CancellationToken);
		Assert.IsFalse(acquire.IsCompleted, "The next holder went ahead while the dead holder's job still held a process.");
		process.Kill();
		await using (EditorLockHold hold = await acquire.WaitAsync(s_timeout, TestContext.CancellationToken))
		{
			Assert.IsFalse(leftover.IsAlive());
			Assert.AreEqual(0, CommandJobs.GetActiveProcesses(jobName));
		}
		Assert.IsTrue(logger.Lines.Any(line => line.Contains($"job {jobName}", StringComparison.Ordinal)), string.Join("\n", logger.Lines));
	}

	private int GetLoggedPid(string name)
	{
		string? line = ReadLog().Split('\n').FirstOrDefault(l => l.StartsWith($"pid {name} ", StringComparison.Ordinal));
		Assert.IsNotNull(line, $"No PID logged for {name}.");
		return int.Parse(line.Split(' ')[2].Trim(), System.Globalization.CultureInfo.InvariantCulture);
	}

	// The scope: lock run needs a project, unless asked for the engine.

	[TestMethod]
	public async Task LockRunNeedsAProjectOrEngineScope()
	{
		DirectoryInfo state = new(_state.PathOf("NoProjectState"));
		DirectoryInfo engine = Directory.CreateDirectory(_state.PathOf("Engine Root"));
		UakContext none = new() { StateDirectory = state, Logger = _state.Context.Logger };
		UakContext engineOnly = new() { StateDirectory = state, EngineRoot = engine, Logger = _state.Context.Logger };
		string[] command = ["--", .. Child.Command("sleep", "0", "0")];

		UakUsageException refused = await Assert.ThrowsExactlyAsync<UakUsageException>(() => new LockRunCommand().RunAsync(engineOnly, command, TestContext.CancellationToken));
		StringAssert.Contains(refused.Message, "-engine-scope");
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new LockRunCommand().RunAsync(none, ["-engine-scope", .. command], TestContext.CancellationToken));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new LockRunCommand().RunAsync(_state.Context, ["-engine-scope", .. command], TestContext.CancellationToken));

		Task<int> run = new LockRunCommand().RunAsync(engineOnly, ["-engine-scope", "-name=EngineWide", "--", .. Child.Command("sleep", "1500", "0")], TestContext.CancellationToken);
		Wait.Until(() => EditorLock.GetStatus(engineOnly).Holder?.Name == "EngineWide", s_timeout, "the engine-wide hold");
		Assert.AreEqual($"engine {CanonicalPath.Get(engine.FullName)}", EditorLock.GetStatus(engineOnly).Holder!.Scope);
		Assert.AreEqual(0, await run);
	}

	[TestMethod]
	public async Task HolderLogsTheScopeItLocks()
	{
		ListLogger logger = new();
		UakContext context = new() { ProjectFile = _state.Context.ProjectFile, StateDirectory = _state.Context.StateDirectory, Logger = logger };
		await using (EditorLockHold hold = await EditorLock.AcquireAsync(context, "Scoped", null, s_fast, TestContext.CancellationToken))
		{
			Assert.AreEqual(("project", CanonicalPath.Get(_state.Root)), (hold.Scope.Kind, hold.Scope.Path));
			Assert.AreEqual(CanonicalPath.TryGetIdentity(_state.Root) ?? CanonicalPath.Get(_state.Root), hold.Scope.Key);
			Assert.AreEqual($"project {CanonicalPath.Get(_state.Root)}", EditorLock.GetStatus(context).Holder!.Scope);
			Assert.AreEqual(hold.Scope.Key, EditorLock.GetStatus(context).Holder!.ScopeKey);
			StringAssert.Contains(LockStatusCommand.Format(EditorLock.GetStatus(context), DateTime.UtcNow), $"Scope     project {CanonicalPath.Get(_state.Root)}");
		}
		Assert.IsTrue(logger.Lines.Any(line => line.Contains($"Holding the editor lock for project {CanonicalPath.Get(_state.Root)}", StringComparison.Ordinal)),
			string.Join("\n", logger.Lines));
	}

	// L5: another machine's records.

	[TestMethod]
	public async Task AnotherMachinesRecordsAreReportedAndNeverWaitedFor()
	{
		Directory.CreateDirectory(Paths.QueueDirectory);
		ProcessIdentity self = ProcessIdentity.Current;
		string foreignTicket = Path.Combine(Paths.QueueDirectory, LockQueue.TicketFileName(LockPriority.High, 1, new ProcessIdentity(self.Pid, self.StartTimeUtc)));
		File.WriteAllText(foreignTicket, "{\"Name\":\"Elsewhere\",\"Pid\":1,\"Machine\":\"OTHER-PC\"}");
		StateFiles.WriteJson(Paths.HolderFile, new LockHolderInfo { Name = "RemoteHolder", Pid = 1, Machine = "OTHER-PC", HoldId = "remote", Acquired = DateTime.UtcNow });

		EditorLockStatus status = EditorLock.GetStatus(_state.Context);
		Assert.IsEmpty(status.Queue, "Another machine's ticket is not in this machine's queue.");
		Assert.HasCount(1, status.OtherMachineTickets);
		Assert.IsFalse(status.HolderAlive);
		string text = LockStatusCommand.Format(status, DateTime.UtcNow, "THIS-PC");
		StringAssert.Contains(text, "ON ANOTHER MACHINE (OTHER-PC)");
		StringAssert.Contains(text, "Ignored   Elsewhere");
		StringAssert.Contains(text, "WARNING: records from another machine (OTHER-PC); this is THIS-PC.");

		// A waiter warns too, and does not wait behind the other machine's ticket.
		ListLogger logger = new();
		UakContext context = new() { ProjectFile = _state.Context.ProjectFile, StateDirectory = _state.Context.StateDirectory, Logger = logger };
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
		await using (EditorLockHold hold = await EditorLock.AcquireAsync(context, "Local", null, s_fast, timeout.Token))
		{
			Assert.IsFalse(hold.IsNested);
		}
		Assert.IsTrue(logger.Lines.Any(line => line.StartsWith("Warning:", StringComparison.Ordinal) && line.Contains("OTHER-PC", StringComparison.Ordinal)),
			string.Join("\n", logger.Lines));
		Assert.IsTrue(File.Exists(foreignTicket), "Another machine's ticket is not deleted.");
	}

	// L6: the queue's order does not follow the wall clock back.

	[TestMethod]
	public void TicketSequenceNeverGoesBack()
	{
		Directory.CreateDirectory(Paths.QueueDirectory);
		ProcessIdentity self = ProcessIdentity.Current;
		// A ticket from "the future": the clock has since stepped back by a day.
		long future = DateTime.UtcNow.AddDays(1).Ticks;
		string early = Path.Combine(Paths.QueueDirectory, LockQueue.TicketFileName(LockPriority.Normal, future, self));
		File.WriteAllText(early, "{\"Name\":\"Early\"}");
		using QueueTicket late = QueueTicket.Create(Paths, "Late", LockPriority.Normal, self);
		Assert.IsTrue(LockQueue.TryParseFileName(Path.GetFileName(late.FilePath), out _, out long sequence, out _));
		Assert.IsGreaterThan(future, sequence);
		CollectionAssert.AreEqual(new[] { "Early", "Late" }, LockQueue.List(Paths).Select(ticket => ticket.Name).ToArray());
		File.Delete(early);

		// Within one process, every sequence is new even when the clock stands still.
		DateTime now = DateTime.UtcNow;
		long first = QueueTicket.NextSequence(Paths, now);
		long second = QueueTicket.NextSequence(Paths, now);
		Assert.IsGreaterThan(first, second);
	}

	/// <summary>Collects log lines as "Level: message".</summary>
	private sealed class ListLogger : ILogger
	{
		private readonly object _gate = new();
		private readonly List<string> _lines = [];

		public IReadOnlyList<string> Lines
		{
			get
			{
				lock (_gate)
				{
					return [.. _lines];
				}
			}
		}

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			lock (_gate)
			{
				_lines.Add($"{logLevel}: {formatter(state, exception)}");
			}
		}
	}
}
