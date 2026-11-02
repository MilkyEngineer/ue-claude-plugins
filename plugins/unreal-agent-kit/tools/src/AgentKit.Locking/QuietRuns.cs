// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Locking;

/// <summary>A running <c>uak compile</c>, recorded in &lt;State&gt;/Lock/Compiles while it runs.</summary>
public sealed class CompileMarker
{
	/// <summary>The compiling uak's process ID.</summary>
	public int Pid { get; set; }

	/// <summary>When that process started (UTC), to tell it from a later process reusing the ID.</summary>
	public DateTime? ProcessStart { get; set; }

	/// <summary>When the compile started (UTC).</summary>
	public DateTime Started { get; set; }
}

/// <summary>A cooked run's build step, recorded in &lt;State&gt;/Lock/UbtReservations from before UAT starts until UAT's build step ends.</summary>
public sealed class UbtReservation
{
	/// <summary>The cooked run's process ID.</summary>
	public int Pid { get; set; }

	/// <summary>When that process started (UTC), to tell it from a later process reusing the ID.</summary>
	public DateTime? ProcessStart { get; set; }

	/// <summary>When the reservation was taken (UTC).</summary>
	public DateTime Started { get; set; }

	/// <summary>What the run is, for the waiting compile's message.</summary>
	public string Name { get; set; } = "";
}

/// <summary>
/// What <c>uak compile</c> waits for, since it takes no editor lock.
/// <para>
/// Quiet holds, for runs that measure (benchmarks): an agent's compile check can load the machine while another agent's
/// benchmark measures. A quiet hold (<see cref="LockHolderInfo.Quiet"/>) closes that gap: each compile records a marker while
/// it runs and, before it starts, waits while a live quiet holder holds the lock; the quiet holder, once it holds the lock,
/// waits until no marker is live. Both write before they read (the compile its marker, the holder its holder.json), so one of
/// any two that race sees the other.
/// </para>
/// <para>
/// UBT reservations, for cooked runs that build: UBT lets one instance run per engine, and uak's own UBT waits for that mutex
/// (-WaitMutex), but the UBT that UAT starts doesn't. It stops at once with ConflictingInstance if a compile holds the mutex,
/// and that ends the run. -ubtargs= can't pass -WaitMutex: UAT puts it among one target's arguments, not UBT's own, and the
/// editor target doesn't get it (BuildProjectCommand.Automation.cs). So a cooked run reserves UBT the same way, with its own
/// marker in place of holder.json, and gives the reservation back when UAT's build step ends. Only one project's state folder
/// sees it: a compile for another project on the same engine, or an IDE's build, can still collide.
/// </para>
/// </summary>
public static class QuietRuns
{
	/// <summary>How often a waiter looks again.</summary>
	public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

	/// <summary>The live waiters for the lock: a quiet run refuses to start behind any.</summary>
	public static IReadOnlyList<LockTicketInfo> GetWaiting(UakContext context)
	{
		return EditorLock.GetStatus(context).Queue;
	}

	/// <summary>
	/// The live quiet hold of this lock, or null: holder.json says quiet, and its holder surely runs on this machine (its
	/// start time read and matched: a killed holder whose ID a protected process reuses must not stop every compile).
	/// </summary>
	public static LockHolderInfo? GetQuietHolder(UakContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		LockHolderInfo? holder = StateFiles.ReadJson<LockHolderInfo>(new LockPaths(context.StateDirectory).HolderFile);
		if (holder is null || !holder.Quiet || LockQueue.IsOtherMachine(holder.Machine) || !holder.Process.IsSurelyAlive())
		{
			return null;
		}
		// A compile that runs inside the quiet hold itself (it inherited the hold's marker) must not wait for its own holder.
		string own = HeldMarkers.Format(EditorLock.GetMutexName(context), holder.HoldId);
		return HeldMarkers.Parse(HeldMarkers.Inherited).Any(marker => HeldMarkers.Format(marker.MutexName, marker.HoldId) == own) ? null : holder;
	}

	/// <summary>The live UBT reservation, or null; dead ones (their process has gone, or can't surely be told alive) are deleted on the way.</summary>
	public static UbtReservation? GetUbtReservation(UakContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		string directory = new LockPaths(context.StateDirectory).UbtReservationsDirectory;
		if (!Directory.Exists(directory))
		{
			return null;
		}
		UbtReservation? live = null;
		foreach (string file in Directory.GetFiles(directory, "*.json"))
		{
			UbtReservation? reservation = StateFiles.ReadJson<UbtReservation>(file);
			if (reservation is null)
			{
				// Being written, or unreadable: a marker is written whole, so count it only once it reads.
				continue;
			}
			if (new ProcessIdentity(reservation.Pid, reservation.ProcessStart).IsSurelyAlive())
			{
				live ??= reservation;
			}
			else
			{
				StateFiles.TryDelete(file);
			}
		}
		return live;
	}

	/// <summary>The live compile markers; dead ones (their process has gone, or can't surely be told alive) are deleted on the way.</summary>
	public static List<CompileMarker> GetCompiles(UakContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		string directory = new LockPaths(context.StateDirectory).CompilesDirectory;
		List<CompileMarker> live = [];
		if (!Directory.Exists(directory))
		{
			return live;
		}
		foreach (string file in Directory.GetFiles(directory, "*.json"))
		{
			CompileMarker? marker = StateFiles.ReadJson<CompileMarker>(file);
			if (marker is null)
			{
				// Being written, or unreadable: a marker is written whole, so count it only once it reads.
				continue;
			}
			if (new ProcessIdentity(marker.Pid, marker.ProcessStart).IsSurelyAlive())
			{
				live.Add(marker);
			}
			else
			{
				StateFiles.TryDelete(file);
			}
		}
		return live;
	}

	/// <summary>
	/// How long a quiet hold waits for running compiles before it goes ahead anyway, with a warning: it holds the editor lock
	/// while it waits, so a compile that hangs must not hold up every other run.
	/// </summary>
	public static readonly TimeSpan DefaultCompileWaitTimeout = TimeSpan.FromMinutes(10);

	/// <summary>
	/// Once a quiet hold is taken: waits until no <c>uak compile</c> runs, so the measured run starts on a quiet machine. After
	/// <paramref name="timeout"/> it goes ahead with a warning that the measurement may be disturbed (or what
	/// <paramref name="risk"/> says instead: <c>uak mutate</c> waits for them too). Returns whether no compile was left running.
	/// </summary>
	public static async Task<bool> WaitForCompilesAsync(UakContext context, TimeSpan poll, TimeSpan timeout, CancellationToken cancellationToken,
		string risk = "so the measurement may be disturbed by them")
	{
		ArgumentNullException.ThrowIfNull(context);
		int reported = -1;
		DateTime deadline = DateTime.UtcNow + timeout;
		while (GetCompiles(context) is { Count: > 0 } compiles)
		{
			string pids = string.Join(", ", compiles.Select(compile => compile.Pid));
			if (DateTime.UtcNow >= deadline)
			{
				context.Logger.LogWarning("Quiet: {Count} `uak compile` (PID {Pids}) still run after {Minutes:0} min: going ahead, {Risk}.",
					compiles.Count, pids, timeout.TotalMinutes, risk);
				return false;
			}
			if (compiles.Count != reported)
			{
				context.Logger.LogInformation("Quiet: waiting for {Count} running `uak compile` to finish (PID {Pids}).", compiles.Count, pids);
				reported = compiles.Count;
			}
			await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
		}
		return true;
	}

	/// <summary>What a cooked run risks when it goes ahead while a compile still runs.</summary>
	public const string UbtConflictRisk = "so UAT's UBT may stop with ConflictingInstance if one still holds UBT's mutex when it starts";

	/// <summary>
	/// For a cooked run that builds, once it holds the editor lock: reserves UBT, so a compile that starts from now on waits, and
	/// waits for the compiles already running (after <paramref name="timeout"/> it goes ahead with a warning). The reservation
	/// holds until the result is disposed, which may happen more than once.
	/// </summary>
	public static async Task<IDisposable> ReserveUbtAsync(UakContext context, string name, TimeSpan poll, TimeSpan timeout, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		ProcessIdentity self = ProcessIdentity.Current;
		string file = Path.Combine(new LockPaths(context.StateDirectory).UbtReservationsDirectory, $"{self.Pid}-{Guid.NewGuid():N}.json");
		// The marker first, then the compiles: a compile that starts now sees the marker, or this wait sees the compile.
		StateFiles.WriteJson(file, new UbtReservation { Pid = self.Pid, ProcessStart = self.StartTimeUtc, Started = DateTime.UtcNow, Name = name });
		Marker reservation = new(file);
		try
		{
			await WaitForCompilesAsync(context, poll, timeout, cancellationToken, UbtConflictRisk).ConfigureAwait(false);
			return reservation;
		}
		catch
		{
			reservation.Dispose();
			throw;
		}
	}

	/// <summary>
	/// How long a compile waits for quiet runs and UBT reservations before it goes ahead anyway, with a warning: a quiet holder
	/// that hangs (or a benchmark that runs for hours) must not hold every compile up for good. A build step that long has its
	/// UBT running by then, and a compile only queues behind that on UBT's mutex.
	/// </summary>
	public static readonly TimeSpan DefaultQuietWaitTimeout = TimeSpan.FromMinutes(30);

	/// <summary>How often a compile that waits for a quiet run says it still waits.</summary>
	public static readonly TimeSpan DefaultQuietWaitReport = TimeSpan.FromMinutes(5);

	/// <summary>
	/// For <c>uak compile</c>: waits while a quiet hold is held or UBT is reserved, then records this compile until the result is
	/// disposed. Without either it waits for nothing. It says every <see cref="DefaultQuietWaitReport"/> that it still waits, and
	/// after <see cref="DefaultQuietWaitTimeout"/> it goes ahead with a warning of what that risks.
	/// </summary>
	public static Task<IDisposable> EnterCompileAsync(UakContext context, TimeSpan poll, CancellationToken cancellationToken)
	{
		return EnterCompileAsync(context, poll, DefaultQuietWaitTimeout, DefaultQuietWaitReport, cancellationToken);
	}

	/// <summary>As <see cref="EnterCompileAsync(UakContext, TimeSpan, CancellationToken)"/>, with the time limit and the report interval given (for tests).</summary>
	public static async Task<IDisposable> EnterCompileAsync(UakContext context, TimeSpan poll, TimeSpan timeout, TimeSpan report, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		string directory = new LockPaths(context.StateDirectory).CompilesDirectory;
		ProcessIdentity self = ProcessIdentity.Current;
		string file = Path.Combine(directory, $"{self.Pid}-{Guid.NewGuid():N}.json");
		string? waitingFor = null;
		DateTime started = DateTime.UtcNow;
		DateTime nextReport = started + report;
		while (true)
		{
			// The marker first, then the others: a quiet holder or a cooked run that starts now sees the marker and waits for it.
			StateFiles.WriteJson(file, new CompileMarker { Pid = self.Pid, ProcessStart = self.StartTimeUtc, Started = DateTime.UtcNow });
			LockHolderInfo? quiet = GetQuietHolder(context);
			UbtReservation? reserved = quiet is null ? GetUbtReservation(context) : null;
			if (quiet is null && reserved is null)
			{
				return new Marker(file);
			}
			string name = quiet?.Name ?? reserved!.Name;
			int pid = quiet?.Pid ?? reserved!.Pid;
			DateTime now = DateTime.UtcNow;
			if (now - started >= timeout)
			{
				// The marker stays: a quiet or cooked run that starts from now on waits for this compile.
				if (quiet is not null)
				{
					context.Logger.LogWarning("Quiet: the quiet run '{Name}' (PID {Pid}) still runs after this compile waited {Minutes:0} min: compiling anyway, which may disturb its measurement.",
						name, pid, timeout.TotalMinutes);
				}
				else
				{
					context.Logger.LogWarning("The cooked run '{Name}' (PID {Pid}) is still building after this compile waited {Minutes:0} min: compiling anyway, which queues behind its UBT.",
						name, pid, timeout.TotalMinutes);
				}
				return new Marker(file);
			}
			StateFiles.TryDelete(file);
			string blocker = quiet?.HoldId ?? "ubt:" + reserved!.Started.Ticks + ":" + pid;
			if (waitingFor != blocker)
			{
				if (quiet is not null)
				{
					context.Logger.LogInformation("Waiting for the quiet run '{Name}' (PID {Pid}) to end: it is measuring, and compiles wait for it (up to {Minutes:0} min).",
						name, pid, timeout.TotalMinutes);
				}
				else
				{
					context.Logger.LogInformation("Waiting for the cooked run '{Name}' (PID {Pid}) to finish building: UAT's UBT stops if a compile holds UBT's mutex when it starts, so compiles wait for it (up to {Minutes:0} min).",
						name, pid, timeout.TotalMinutes);
				}
				waitingFor = blocker;
			}
			else if (now >= nextReport)
			{
				context.Logger.LogInformation("Still waiting for the {Kind} run '{Name}' (PID {Pid}): {Minutes:0} min so far, up to {Limit:0}.",
					quiet is null ? "cooked" : "quiet", name, pid, (now - started).TotalMinutes, timeout.TotalMinutes);
			}
			if (now >= nextReport)
			{
				nextReport = now + report;
			}
			await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
		}
	}

	sealed class Marker(string file) : IDisposable
	{
		public void Dispose()
		{
			StateFiles.TryDelete(file);
		}
	}
}
