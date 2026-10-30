// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Tests;

[TestClass]
public sealed class QuietRunsTests
{
	static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

	static string Unique() => "UakQuietTest_" + Guid.NewGuid().ToString("N");

	[TestMethod]
	public async Task AQuietHoldIsRecordedAndCompilesWaitForIt()
	{
		using TempState State = new();
		string Holder = Path.Combine(State.StateDirectory, "Lock", "holder.json");

		await using (EditorLockHold Hold = await EditorLock.AcquireAsync(State.Context, "bench", null, new EditorLockOptions { Quiet = true, MutexName = Unique() }, CancellationToken.None))
		{
			Assert.IsTrue(StateFiles.ReadJson<LockHolderInfo>(Holder)!.Quiet);
			Assert.IsNotNull(QuietRuns.GetQuietHolder(State.Context));

			Task<IDisposable> Compile = QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None);
			await Task.Delay(200);
			Assert.IsFalse(Compile.IsCompleted, "a compile must wait while a quiet run holds the lock");
			Assert.IsEmpty(QuietRuns.GetCompiles(State.Context));

			await Hold.DisposeAsync();
			using IDisposable Entered = await Compile.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.HasCount(1, QuietRuns.GetCompiles(State.Context));
		}
		Assert.IsEmpty(QuietRuns.GetCompiles(State.Context));
	}

	[TestMethod]
	public async Task AnOrdinaryHoldDoesNotStopCompiles()
	{
		using TempState State = new();
		await using EditorLockHold Hold = await EditorLock.AcquireAsync(State.Context, "build", null, new EditorLockOptions { MutexName = Unique() }, CancellationToken.None);

		Assert.IsNull(QuietRuns.GetQuietHolder(State.Context));
		using IDisposable Entered = await QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
	}

	[TestMethod]
	public async Task AQuietRunWaitsForRunningCompilesButNotForDeadOnes()
	{
		using TempState State = new();
		IDisposable Running = await QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None);
		string Dead = Path.Combine(State.StateDirectory, "Lock", "Compiles", "dead.json");
		StateFiles.WriteJson(Dead, new CompileMarker { Pid = int.MaxValue, Started = DateTime.UtcNow });

		Task<bool> Wait = QuietRuns.WaitForCompilesAsync(State.Context, Poll, TimeSpan.FromMinutes(1), CancellationToken.None);
		await Task.Delay(200);
		Assert.IsFalse(Wait.IsCompleted, "a quiet run must wait for a running compile");
		Assert.IsFalse(File.Exists(Dead), "a dead compile's marker is cleared, never waited for");

		Running.Dispose();
		Assert.IsTrue(await Wait.WaitAsync(TimeSpan.FromSeconds(10)));
	}

	[TestMethod]
	public async Task AQuietRunGoesAheadAfterItsTimeoutWithAWarning()
	{
		using TempState State = new();
		using IDisposable Running = await QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None);

		Assert.IsFalse(await QuietRuns.WaitForCompilesAsync(State.Context, Poll, TimeSpan.FromMilliseconds(100), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
	}

	[TestMethod]
	public void AHolderOrCompileWhoseStartTimeIsUnknownIsNeverTakenForAlive()
	{
		// As when a protected process (whose start time can't be read) reuses a killed holder's or compile's ID: this
		// process stands for it, recorded without a start time. Lenient liveness would make every compile wait forever.
		using TempState State = new();
		LockPaths Paths = new(new DirectoryInfo(State.StateDirectory));
		StateFiles.WriteJson(Paths.HolderFile, new LockHolderInfo
		{
			Name = "killed bench", Pid = Environment.ProcessId, ProcessStart = null, Quiet = true, HoldId = "h1", Machine = Environment.MachineName,
		});
		string Marker = Path.Combine(Paths.CompilesDirectory, "unknown.json");
		StateFiles.WriteJson(Marker, new CompileMarker { Pid = Environment.ProcessId, ProcessStart = null, Started = DateTime.UtcNow });

		Assert.IsNull(QuietRuns.GetQuietHolder(State.Context));
		Assert.IsEmpty(QuietRuns.GetCompiles(State.Context));
		Assert.IsFalse(File.Exists(Marker));
	}

	[TestMethod]
	public async Task AQuietHoldCannotJoinAHoldThatIsNotQuiet()
	{
		using TempState State = new();
		LockPaths Paths = new(new DirectoryInfo(State.StateDirectory));
		await using EditorLockHold Outer = await EditorLock.AcquireAsync(State.Context, "lock run", null, new EditorLockOptions { MutexName = Unique() }, CancellationToken.None);
		using EditorLockHold Nested = EditorLockHold.CreateNested(Paths, Outer.MutexName, Outer.Scope, Outer.Info, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

		Assert.ThrowsExactly<UakUsageException>(() => EditorLock.CheckJoinable(Nested, new EditorLockOptions { Quiet = true }, "uak test -quiet"));
		EditorLock.CheckJoinable(Nested, EditorLockOptions.Default, "uak test");
	}

	[TestMethod]
	public async Task AQuietHoldThatCannotRecordItselfDoesNotStart()
	{
		using TempState State = new();
		string Mutex = Unique();
		// holder.json can't be written: a folder stands in its place.
		Directory.CreateDirectory(new LockPaths(new DirectoryInfo(State.StateDirectory)).HolderFile);

		await Assert.ThrowsExactlyAsync<EditorLockException>(() => EditorLock.AcquireAsync(State.Context, "bench", null, new EditorLockOptions { Quiet = true, MutexName = Mutex }, CancellationToken.None));
		// The lock was let go: a hold of the same mutex from a state folder that can record it takes it at once.
		using TempState Other = new();
		await using EditorLockHold Hold = await EditorLock.AcquireAsync(Other.Context, "build", null, new EditorLockOptions { MutexName = Mutex }, CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(30));
	}

	[TestMethod]
	public void QuietRunsSeeTheWaiters()
	{
		using TempState State = new();
		Assert.IsEmpty(QuietRuns.GetWaiting(State.Context));
	}

	[TestMethod]
	public async Task ACompileGoesAheadAfterItsTimeoutWhileAQuietRunStillMeasures()
	{
		using TempState State = new();
		await using EditorLockHold Hold = await EditorLock.AcquireAsync(State.Context, "bench", null, new EditorLockOptions { Quiet = true, MutexName = Unique() }, CancellationToken.None);
		Assert.IsNotNull(QuietRuns.GetQuietHolder(State.Context));

		using IDisposable Entered = await QuietRuns.EnterCompileAsync(State.Context, Poll, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50), CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.HasCount(1, QuietRuns.GetCompiles(State.Context), "it is recorded, so a quiet run that starts after it waits for it");
	}
}
