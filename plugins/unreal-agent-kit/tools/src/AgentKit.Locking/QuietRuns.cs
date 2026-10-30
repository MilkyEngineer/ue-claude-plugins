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

/// <summary>
/// Quiet holds, for runs that measure (benchmarks). <c>uak compile</c> takes no editor lock, so an agent's compile check
/// can load the machine while another agent's benchmark measures. A quiet hold (<see cref="LockHolderInfo.Quiet"/>) closes
/// that gap: each compile records a marker while it runs and, before it starts, waits while a live quiet holder holds the
/// lock; the quiet holder, once it holds the lock, waits until no marker is live. Both write before they read (the compile
/// its marker, the holder its holder.json), so one of any two that race sees the other.
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
	/// <paramref name="timeout"/> it goes ahead with a warning that the measurement may be disturbed. Returns whether no
	/// compile was left running.
	/// </summary>
	public static async Task<bool> WaitForCompilesAsync(UakContext context, TimeSpan poll, TimeSpan timeout, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		int reported = -1;
		DateTime deadline = DateTime.UtcNow + timeout;
		while (GetCompiles(context) is { Count: > 0 } compiles)
		{
			string pids = string.Join(", ", compiles.Select(compile => compile.Pid));
			if (DateTime.UtcNow >= deadline)
			{
				context.Logger.LogWarning("Quiet: {Count} `uak compile` (PID {Pids}) still run after {Minutes:0} min: going ahead, so the measurement may be disturbed by them.",
					compiles.Count, pids, timeout.TotalMinutes);
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

	/// <summary>
	/// How long a compile waits for quiet runs before it goes ahead anyway, with a warning: a quiet holder that hangs (or a
	/// benchmark that runs for hours) must not hold every compile up for good.
	/// </summary>
	public static readonly TimeSpan DefaultQuietWaitTimeout = TimeSpan.FromMinutes(30);

	/// <summary>How often a compile that waits for a quiet run says it still waits.</summary>
	public static readonly TimeSpan DefaultQuietWaitReport = TimeSpan.FromMinutes(5);

	/// <summary>
	/// For <c>uak compile</c>: waits while a quiet hold is held, then records this compile until the result is disposed. Without
	/// a quiet hold it waits for nothing. It says every <see cref="DefaultQuietWaitReport"/> that it still waits, and after
	/// <see cref="DefaultQuietWaitTimeout"/> it goes ahead with a warning that it may disturb the measurement.
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
			// The marker first, then the holder: a quiet holder that takes the lock now sees the marker and waits for it.
			StateFiles.WriteJson(file, new CompileMarker { Pid = self.Pid, ProcessStart = self.StartTimeUtc, Started = DateTime.UtcNow });
			LockHolderInfo? quiet = GetQuietHolder(context);
			if (quiet is null)
			{
				return new Marker(file);
			}
			DateTime now = DateTime.UtcNow;
			if (now - started >= timeout)
			{
				// The marker stays: a quiet run that starts from now on waits for this compile.
				context.Logger.LogWarning("Quiet: the quiet run '{Name}' (PID {Pid}) still runs after this compile waited {Minutes:0} min: compiling anyway, which may disturb its measurement.",
					quiet.Name, quiet.Pid, timeout.TotalMinutes);
				return new Marker(file);
			}
			StateFiles.TryDelete(file);
			if (waitingFor != quiet.HoldId)
			{
				context.Logger.LogInformation("Waiting for the quiet run '{Name}' (PID {Pid}) to end: it is measuring, and compiles wait for it (up to {Minutes:0} min).",
					quiet.Name, quiet.Pid, timeout.TotalMinutes);
				waitingFor = quiet.HoldId;
			}
			else if (now >= nextReport)
			{
				context.Logger.LogInformation("Still waiting for the quiet run '{Name}' (PID {Pid}): {Minutes:0} min so far, up to {Limit:0}.",
					quiet.Name, quiet.Pid, (now - started).TotalMinutes, timeout.TotalMinutes);
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
