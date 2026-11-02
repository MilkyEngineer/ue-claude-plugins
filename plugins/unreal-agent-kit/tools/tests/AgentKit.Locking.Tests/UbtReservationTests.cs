// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Tests;

/// <summary>A cooked run's UBT stops if a compile holds UBT's mutex when it starts, so the two take turns.</summary>
[TestClass]
public sealed class UbtReservationTests
{
	static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);
	static readonly TimeSpan NoWait = TimeSpan.FromSeconds(10);

	static string Reservations(TempState state) => Path.Combine(state.StateDirectory, "Lock", "UbtReservations");

	[TestMethod]
	public async Task ACompileWaitsUntilTheReservationIsGivenBack()
	{
		using TempState State = new();
		IDisposable Reservation = await QuietRuns.ReserveUbtAsync(State.Context, "cooked run", Poll, TimeSpan.FromMinutes(1), CancellationToken.None).WaitAsync(NoWait);
		Assert.AreEqual("cooked run", QuietRuns.GetUbtReservation(State.Context)!.Name);

		Task<IDisposable> Compile = QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None);
		await Task.Delay(200);
		Assert.IsFalse(Compile.IsCompleted, "a compile must wait while a cooked run's build is reserved");
		// A waiting compile writes its marker each poll and deletes it while blocked: by the next look it is gone.
		Assert.IsTrue(SpinWait.SpinUntil(() => QuietRuns.GetCompiles(State.Context).Count == 0, TimeSpan.FromSeconds(5)), "a waiting compile keeps no marker");
		Assert.IsFalse(Compile.IsCompleted, "a compile must wait while a cooked run's build is reserved");

		Reservation.Dispose();
		using IDisposable Entered = await Compile.WaitAsync(NoWait);
		Assert.IsNull(QuietRuns.GetUbtReservation(State.Context));
		Assert.HasCount(1, QuietRuns.GetCompiles(State.Context));
	}

	[TestMethod]
	public async Task GivingAReservationBackTwiceIsNothing()
	{
		using TempState State = new();
		IDisposable First = await QuietRuns.ReserveUbtAsync(State.Context, "one", Poll, TimeSpan.FromMinutes(1), CancellationToken.None).WaitAsync(NoWait);
		First.Dispose();
		using IDisposable Second = await QuietRuns.ReserveUbtAsync(State.Context, "two", Poll, TimeSpan.FromMinutes(1), CancellationToken.None).WaitAsync(NoWait);

		First.Dispose();

		Assert.AreEqual("two", QuietRuns.GetUbtReservation(State.Context)!.Name, "the build step's end and the run's end both give it back");
	}

	[TestMethod]
	public async Task AReservationWaitsForARunningCompileAndHoldsNewOnesBack()
	{
		using TempState State = new();
		IDisposable Running = await QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None).WaitAsync(NoWait);

		Task<IDisposable> Reservation = QuietRuns.ReserveUbtAsync(State.Context, "cooked run", Poll, TimeSpan.FromMinutes(1), CancellationToken.None);
		await Task.Delay(200);
		Assert.IsFalse(Reservation.IsCompleted, "UAT's UBT must not start while a compile holds its mutex");

		// The marker comes first: of two that race, one sees the other.
		Assert.IsNotNull(QuietRuns.GetUbtReservation(State.Context));
		Task<IDisposable> Later = QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None);
		await Task.Delay(200);
		Assert.IsFalse(Later.IsCompleted, "a compile that starts while the run waits waits for the run");

		Running.Dispose();
		IDisposable Reserved = await Reservation.WaitAsync(NoWait);
		Reserved.Dispose();
		using IDisposable Entered = await Later.WaitAsync(NoWait);
	}

	[TestMethod]
	public async Task AReservationGoesAheadAfterItsTimeout()
	{
		using TempState State = new();
		using IDisposable Running = await QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None).WaitAsync(NoWait);

		using IDisposable Reservation = await QuietRuns.ReserveUbtAsync(State.Context, "cooked run", Poll, TimeSpan.FromMilliseconds(100), CancellationToken.None).WaitAsync(NoWait);

		Assert.IsNotNull(QuietRuns.GetUbtReservation(State.Context));
	}

	[TestMethod]
	public async Task ACancelledReservationLeavesNoMarker()
	{
		using TempState State = new();
		using IDisposable Running = await QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None).WaitAsync(NoWait);
		using CancellationTokenSource Cancel = new();

		Task<IDisposable> Reservation = QuietRuns.ReserveUbtAsync(State.Context, "cooked run", Poll, TimeSpan.FromMinutes(1), Cancel.Token);
		await Task.Delay(100);
		await Cancel.CancelAsync();

		await Assert.ThrowsAsync<OperationCanceledException>(async () => await Reservation.WaitAsync(NoWait));
		Assert.IsNull(QuietRuns.GetUbtReservation(State.Context));
	}

	[TestMethod]
	public async Task ADeadRunsReservationHoldsNothingUpAndIsCleared()
	{
		using TempState State = new();
		string Dead = Path.Combine(Reservations(State), "dead.json");
		StateFiles.WriteJson(Dead, new UbtReservation { Pid = int.MaxValue, Started = DateTime.UtcNow, Name = "killed" });

		using IDisposable Entered = await QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None).WaitAsync(NoWait);

		Assert.IsFalse(File.Exists(Dead));
	}

	[TestMethod]
	public async Task ACompileGoesAheadAfterItsTimeoutWhileARunStillBuilds()
	{
		using TempState State = new();
		using IDisposable Reservation = await QuietRuns.ReserveUbtAsync(State.Context, "cooked run", Poll, TimeSpan.FromMinutes(1), CancellationToken.None).WaitAsync(NoWait);

		using IDisposable Entered = await QuietRuns.EnterCompileAsync(State.Context, Poll, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50), CancellationToken.None).WaitAsync(NoWait);

		Assert.HasCount(1, QuietRuns.GetCompiles(State.Context), "it queues behind UBT's mutex from there");
	}

	[TestMethod]
	public async Task AnOrdinaryCompileDoesNotCareAboutOtherCompiles()
	{
		using TempState State = new();
		using IDisposable First = await QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None).WaitAsync(NoWait);

		using IDisposable Second = await QuietRuns.EnterCompileAsync(State.Context, Poll, CancellationToken.None).WaitAsync(NoWait);

		Assert.HasCount(2, QuietRuns.GetCompiles(State.Context));
	}
}
