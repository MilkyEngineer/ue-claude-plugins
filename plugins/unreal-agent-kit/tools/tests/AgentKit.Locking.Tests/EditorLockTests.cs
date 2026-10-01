// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

// The editor lock across real processes. Every test uses its own temporary project (so its own mutex name) and state
// directory, never a real one. Each holder appends "enter <name>" and "leave <name>" to a log while it holds the lock, and
// every test checks that no two holds overlap.

using System.Diagnostics;
using AgentKit.Core;
using AgentKit.Tests;

namespace AgentKit.Locking.Tests;

[TestClass]
public sealed class EditorLockTests
{
	private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);
	private static readonly EditorLockOptions s_fast = new() { PollInterval = TimeSpan.FromMilliseconds(100) };

	private TempState _state = null!;
	private string _log = null!;

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
		_state.Dispose();
	}

	private LockPaths Paths => new(_state.Context.StateDirectory);

	private Process Waiter(string name, string priority = "default", int holdMilliseconds = 300)
	{
		return _state.StartChild("lock", _state.StateDirectory, _state.ProjectFile, name, priority, _log,
			holdMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	private Task<EditorLockHold> HoldHere(string name = "TestHolder", CancellationToken cancellationToken = default)
	{
		return EditorLock.AcquireAsync(_state.Context, name, LockPriority.Normal, s_fast, cancellationToken);
	}

	private void WaitForQueue(int count)
	{
		Wait.Until(() => LockQueue.List(Paths).Count >= count, s_timeout, $"{count} tickets in the queue");
	}

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
			string[] parts = line.Split(' ', 2);
			if (parts[0] == "enter")
			{
				Assert.IsTrue(current is null || killed.Contains(current), $"'{parts[1]}' entered while '{current}' held the lock:\n{ReadLog()}");
				current = parts[1];
				order.Add(current);
			}
			// Other lines ("log <name> ...", "pid <name> ...") are the children's messages.
			else if (parts[0] == "leave")
			{
				Assert.AreEqual(current, parts[1], $"'{parts[1]}' left without holding the lock:\n{ReadLog()}");
				current = null;
			}
		}
		return order;
	}

	private void AssertClean()
	{
		Wait.Until(() => LockQueue.List(Paths).Count == 0, TimeSpan.FromSeconds(5), "an empty queue");
		string[] left = Directory.Exists(Paths.QueueDirectory) ? Directory.GetFiles(Paths.QueueDirectory) : [];
		Assert.IsEmpty(left, "Files are left in the queue: " + string.Join(", ", left));
		Assert.IsFalse(File.Exists(Paths.HolderFile), "holder.json is left.");
	}

	[TestMethod]
	public async Task FirstInFirstOut()
	{
		EditorLockHold held = await HoldHere();
		foreach (string name in new[] { "A", "B", "C", "D" })
		{
			Waiter(name);
			WaitForQueue(name[0] - 'A' + 1);
		}
		await held.DisposeAsync();
		_state.WaitAll(s_timeout);
		CollectionAssert.AreEqual(new[] { "A", "B", "C", "D" }, Holds());
		AssertClean();
	}

	[TestMethod]
	public async Task HighPriorityGoesFirst()
	{
		EditorLockHold held = await HoldHere();
		(string Name, string Priority)[] waiters = [("N1", "Normal"), ("N2", "Normal"), ("H1", "High"), ("N3", "Normal"), ("H2", "High")];
		for (int index = 0; index < waiters.Length; index++)
		{
			Waiter(waiters[index].Name, waiters[index].Priority);
			WaitForQueue(index + 1);
		}
		List<LockTicketInfo> queue = LockQueue.List(Paths);
		CollectionAssert.AreEqual(new[] { "H1", "H2", "N1", "N2", "N3" }, queue.Select(t => t.Name).ToArray());
		// N1 was first in line (waiting on the mutex) until H1 arrived. Release only once it has seen it is no longer first:
		// it reports its new place after it has stopped waiting on the mutex.
		Wait.Until(() => ReadLog().Contains("as 'N1' (Normal), 2 ahead.", StringComparison.Ordinal), s_timeout, "N1 to step back behind H1 and H2");
		await held.DisposeAsync();
		_state.WaitAll(s_timeout);
		CollectionAssert.AreEqual(new[] { "H1", "H2", "N1", "N2", "N3" }, Holds());
		AssertClean();
	}

	[TestMethod]
	public async Task DeadTicketIsSkippedAndRemoved()
	{
		EditorLockHold held = await HoldHere();
		// A ticket left behind by a dead process (as on Unix, where delete-on-close does not survive a crash), first in line.
		ProcessIdentity dead = Child.DeadProcess();
		Directory.CreateDirectory(Paths.QueueDirectory);
		string deadTicket = Path.Combine(Paths.QueueDirectory, LockQueue.TicketFileName(LockPriority.High, 1, dead));
		File.WriteAllText(deadTicket, "{\"Name\":\"Dead\"}");
		Waiter("A");
		WaitForQueue(1);
		Assert.IsFalse(File.Exists(deadTicket), "Listing the queue removes a dead ticket.");
		await held.DisposeAsync();
		_state.WaitAll(s_timeout);
		CollectionAssert.AreEqual(new[] { "A" }, Holds());
		AssertClean();
	}

	[TestMethod]
	public async Task KilledWaiterLeavesTheQueue()
	{
		EditorLockHold held = await HoldHere();
		Process doomed = Waiter("Doomed");
		WaitForQueue(1);
		Waiter("B");
		WaitForQueue(2);
		doomed.Kill();
		await doomed.WaitForExitAsync(TestContext.CancellationToken);
		Wait.Until(() => LockQueue.List(Paths).Count == 1, TimeSpan.FromSeconds(10), "the killed waiter's ticket to go");
		Assert.AreEqual("B", LockQueue.List(Paths)[0].Name);
		await held.DisposeAsync();
		_state.WaitAll(s_timeout);
		CollectionAssert.AreEqual(new[] { "B" }, Holds());
		AssertClean();
	}

	[TestMethod]
	public async Task DeadHolderReleasesTheMutex()
	{
		Process holder = Waiter("Holder", holdMilliseconds: -1);
		Wait.Until(() => ReadLog().Contains("enter Holder", StringComparison.Ordinal), s_timeout, "the holder to take the lock");
		Waiter("A");
		// A is first in line, so it waits on the mutex itself.
		Wait.Until(() => ReadLog().Contains("as 'A' (Normal), first in line.", StringComparison.Ordinal), s_timeout, "A to wait at the front");
		Assert.DoesNotContain("enter A", ReadLog(), "A must wait while the holder lives.");

		holder.Kill();
		await holder.WaitForExitAsync(TestContext.CancellationToken);
		// The kernel abandons the dead holder's mutex; the next waiter takes it at once, not after some timeout.
		Stopwatch sinceKill = Stopwatch.StartNew();
		Wait.Until(() => ReadLog().Contains("enter A", StringComparison.Ordinal), s_timeout, "A to take the abandoned lock");
		Assert.IsLessThan(30.0, sinceKill.Elapsed.TotalSeconds);
		_state.WaitAll(s_timeout);
		CollectionAssert.AreEqual(new[] { "Holder", "A" }, Holds("Holder"));
		AssertClean();
	}

	[TestMethod]
	public async Task StaleHolderRecordIsReportedThenReplaced()
	{
		Process holder = Waiter("Holder", holdMilliseconds: -1);
		Wait.Until(() => ReadLog().Contains("enter Holder", StringComparison.Ordinal), s_timeout, "the holder to take the lock");
		EditorLockStatus status = EditorLock.GetStatus(_state.Context);
		Assert.AreEqual("Holder", status.Holder?.Name);
		Assert.IsTrue(status.HolderAlive);
		holder.Kill();
		await holder.WaitForExitAsync(TestContext.CancellationToken);
		status = EditorLock.GetStatus(_state.Context);
		Assert.AreEqual("Holder", status.Holder?.Name, "The dead holder's record stays until the next holder.");
		Assert.IsFalse(status.HolderAlive);
		StringAssert.Contains(LockStatusCommand.Format(status, DateTime.UtcNow), "HOLDER GONE");

		await using (EditorLockHold hold = await HoldHere("Next"))
		{
			Assert.AreEqual("Next", EditorLock.GetStatus(_state.Context).Holder?.Name);
		}
		AssertClean();
	}

	[TestMethod]
	public async Task CancelledHeadWaiterLeavesTheQueue()
	{
		Waiter("Holder", holdMilliseconds: 4000);
		Wait.Until(() => ReadLog().Contains("enter Holder", StringComparison.Ordinal), s_timeout, "the holder to take the lock");
		using CancellationTokenSource cancel = new();
		// First in line: it waits on the mutex itself.
		Task<EditorLockHold> waiting = HoldHere("Cancelled", cancel.Token);
		WaitForQueue(1);
		await Task.Delay(300, TestContext.CancellationToken);
		await cancel.CancelAsync();
		await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
		Assert.IsEmpty(LockQueue.List(Paths));
		EditorLockStatus status = EditorLock.GetStatus(_state.Context);
		Assert.AreEqual("Holder", status.Holder?.Name, "Cancelling a waiter leaves the holder alone.");

		Waiter("After");
		_state.WaitAll(s_timeout);
		CollectionAssert.AreEqual(new[] { "Holder", "After" }, Holds());
		AssertClean();
	}

	[TestMethod]
	public async Task CancelledWaiterBehindOthersLeavesTheQueue()
	{
		EditorLockHold held = await HoldHere();
		Waiter("A");
		WaitForQueue(1);
		using CancellationTokenSource cancel = new();
		Task<EditorLockHold> waiting = EditorLock.AcquireAsync(_state.Context, "Cancelled", LockPriority.Normal, s_fast, cancel.Token);
		WaitForQueue(2);
		Waiter("B");
		WaitForQueue(3);
		await cancel.CancelAsync();
		await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
		CollectionAssert.AreEqual(new[] { "A", "B" }, LockQueue.List(Paths).Select(t => t.Name).ToArray());
		await held.DisposeAsync();
		_state.WaitAll(s_timeout);
		CollectionAssert.AreEqual(new[] { "A", "B" }, Holds());
		AssertClean();
	}

	[TestMethod]
	public void ContentionNeverOverlaps()
	{
		// Everyone at once, in both priorities: the order is free, overlaps are not.
		for (int index = 0; index < 8; index++)
		{
			Waiter($"W{index}", index % 3 == 0 ? "High" : "Normal", holdMilliseconds: 150);
		}
		_state.WaitAll(TimeSpan.FromSeconds(120));
		List<string> holds = Holds();
		Assert.HasCount(8, holds);
		Assert.HasCount(8, holds.Distinct());
		AssertClean();
	}

	[TestMethod]
	public async Task HolderAndTicketRecords()
	{
		await using (EditorLockHold held = await EditorLock.AcquireAsync(_state.Context, "Me", LockPriority.High,
			new EditorLockOptions { PollInterval = TimeSpan.FromMilliseconds(100), Command = "make all" }, TestContext.CancellationToken))
		{
			EditorLockStatus status = EditorLock.GetStatus(_state.Context);
			Assert.IsNotNull(status.Holder);
			Assert.AreEqual("Me", status.Holder.Name);
			Assert.AreEqual(Environment.ProcessId, status.Holder.Pid);
			Assert.AreEqual(LockPriority.High, status.Holder.Priority);
			Assert.AreEqual("make all", status.Holder.Command);
			Assert.AreEqual(held.Info.HoldId, status.Holder.HoldId);
			Assert.IsTrue(status.HolderAlive);
			Assert.AreEqual(EditorLock.GetMutexName(_state.Context), held.MutexName);

			Waiter("W", "High");
			WaitForQueue(1);
			LockTicketInfo ticket = EditorLock.GetStatus(_state.Context).Queue[0];
			Assert.AreEqual("W", ticket.Name);
			Assert.AreEqual(LockPriority.High, ticket.Priority);
			Assert.IsTrue(ticket.Process.IsAlive());
			string text = LockStatusCommand.Format(EditorLock.GetStatus(_state.Context), DateTime.UtcNow);
			StringAssert.Contains(text, $"Held by   Me  PID {Environment.ProcessId}  High");
			StringAssert.Contains(text, "make all");
			StringAssert.Contains(text, " 1. W ");

			// The files are UTF-8 without a BOM, with ISO 8601 UTC times.
			byte[] bytes = File.ReadAllBytes(Paths.HolderFile);
			Assert.AreNotEqual(0xEF, bytes[0]);
			StringAssert.Matches(System.Text.Encoding.UTF8.GetString(bytes), new System.Text.RegularExpressions.Regex("\"Acquired\": \"\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\d\\.\\d+Z\""));
		}
		_state.WaitAll(s_timeout);
		AssertClean();
	}

	[TestMethod]
	public void MutexNamesAreUniquePerProject()
	{
		using TempState other = new();
		string mine = EditorLock.GetMutexName(_state.Context);
		Assert.AreNotEqual(mine, EditorLock.GetMutexName(other.Context));
		StringAssert.StartsWith(mine, "Global\\" + EditorLock.MutexBaseName + "_");
		// Case and a trailing separator do not matter.
		UakContext same = new()
		{
			ProjectFile = new FileInfo(_state.ProjectFile.ToUpperInvariant()),
			StateDirectory = _state.Context.StateDirectory,
			Logger = _state.Context.Logger,
		};
		Assert.AreEqual(mine, EditorLock.GetMutexName(same));
	}

	[TestMethod]
	public async Task LocksOfDifferentProjectsDoNotBlockEachOther()
	{
		using TempState other = new();
		await using EditorLockHold mine = await HoldHere();
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
		await using EditorLockHold theirs = await EditorLock.AcquireAsync(other.Context, "Other", null, s_fast, timeout.Token);
		Assert.IsFalse(theirs.IsReleased);
	}
}
