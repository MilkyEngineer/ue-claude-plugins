// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.ComponentModel;
using System.Diagnostics;

namespace AgentKit.Core;

/// <summary>
/// A process named by its ID and its start time. The start time tells a process from a later one that reuses its ID, so
/// state files (lock tickets, run records) record both, and a reader treats the process as gone when either no longer matches.
/// </summary>
/// <param name="Pid">The process ID.</param>
/// <param name="StartTimeUtc">The process's start time in UTC, or null when it could not be read (it then matches any start).</param>
public readonly record struct ProcessIdentity(int Pid, DateTime? StartTimeUtc)
{
	/// <summary>
	/// How far two readings of one process's start time may differ and still match. Windows reports it exactly; Linux derives
	/// it from the boot time and clock ticks, which can differ by a few milliseconds between readers.
	/// </summary>
	public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

	private static readonly Lazy<ProcessIdentity> s_current = new(() =>
	{
		int pid = Environment.ProcessId;
		return TryGet(pid) ?? new ProcessIdentity(pid, null);
	});

	/// <summary>This process.</summary>
	public static ProcessIdentity Current => s_current.Value;

	/// <summary>
	/// The running process with this ID, with its start time (null when the start time cannot be read, for example for a
	/// protected process). Null when no process with this ID runs. A zombie on Linux counts as gone: it runs nothing, but
	/// <c>kill(pid, 0)</c> still finds it until its parent reaps it.
	/// </summary>
	public static ProcessIdentity? TryGet(int pid)
	{
		if (pid <= 0 || (OperatingSystem.IsLinux() && IsZombie(pid)))
		{
			return null;
		}
		try
		{
			using Process process = Process.GetProcessById(pid);
			try
			{
				return new ProcessIdentity(pid, process.StartTime.ToUniversalTime());
			}
			catch (Win32Exception)
			{
				// It exists but cannot be queried (access denied).
				return new ProcessIdentity(pid, null);
			}
			catch (NotSupportedException)
			{
				return new ProcessIdentity(pid, null);
			}
			catch (InvalidOperationException)
			{
				// It exited between the two calls.
				return null;
			}
		}
		catch (ArgumentException)
		{
			// Not running.
			return null;
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	/// <summary>
	/// Whether this process still runs: a process with <see cref="Pid"/> runs, and its start time matches
	/// <see cref="StartTimeUtc"/> within <see cref="StartTimeTolerance"/>. An unknown start time on either side matches.
	/// </summary>
	public bool IsAlive()
	{
		return IsAlive(StartTimeTolerance);
	}

	/// <summary>As <see cref="IsAlive()"/>, with start times matching within <paramref name="tolerance"/>.</summary>
	public bool IsAlive(TimeSpan tolerance)
	{
		ProcessIdentity? running = TryGet(Pid);
		return running is not null && StartTimesMatch(StartTimeUtc, running.Value.StartTimeUtc, tolerance);
	}

	/// <summary>
	/// Whether this process surely still runs: a process with <see cref="Pid"/> runs, and both start times are known and match
	/// within <see cref="StartTimeTolerance"/>. Unlike <see cref="IsAlive()"/>, an unknown start time on either side counts as
	/// gone, so a protected process (whose start time can't be read) that reuses the ID is never taken for this one. For
	/// records whose owner, taken for alive by mistake, would let a check pass that must not (a mutation journal, a quiet hold).
	/// </summary>
	public bool IsSurelyAlive()
	{
		if (StartTimeUtc is null)
		{
			return false;
		}
		ProcessIdentity? running = TryGet(Pid);
		return running?.StartTimeUtc is DateTime start && StartTimesMatch(StartTimeUtc, start);
	}

	/// <summary>Whether two start times name the same process start: either is unknown, or they are within <see cref="StartTimeTolerance"/>.</summary>
	public static bool StartTimesMatch(DateTime? a, DateTime? b)
	{
		return StartTimesMatch(a, b, StartTimeTolerance);
	}

	/// <summary>Whether two start times name the same process start: either is unknown, or they are within <paramref name="tolerance"/>.</summary>
	public static bool StartTimesMatch(DateTime? a, DateTime? b, TimeSpan tolerance)
	{
		if (a is null || b is null)
		{
			return true;
		}
		return (a.Value.ToUniversalTime() - b.Value.ToUniversalTime()).Duration() <= tolerance;
	}

	/// <summary>Whether a line of <c>/proc/&lt;pid&gt;/stat</c> tells of a zombie: "12 (name) Z ...". The name may hold spaces and parentheses, so the state is what follows the last ')'.</summary>
	internal static bool IsZombieStat(string stat)
	{
		int nameEnd = stat.LastIndexOf(')');
		return nameEnd >= 0 && nameEnd + 2 < stat.Length && stat[nameEnd + 2] == 'Z';
	}

	private static bool IsZombie(int pid)
	{
		try
		{
			return IsZombieStat(File.ReadAllText($"/proc/{pid}/stat"));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>"PID 1234 (started 2026-10-01T06:05:00.000Z)".</summary>
	public override string ToString()
	{
		return StartTimeUtc is null ? $"PID {Pid}" : $"PID {Pid} (started {UakJson.FormatTime(StartTimeUtc.Value)})";
	}
}
