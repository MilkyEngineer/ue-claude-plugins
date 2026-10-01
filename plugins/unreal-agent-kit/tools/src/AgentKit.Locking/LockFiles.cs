// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text.Json.Serialization;
using AgentKit.Core;

namespace AgentKit.Locking;

/// <summary>
/// A waiter in the editor lock queue, as its ticket in <c>&lt;State&gt;/Lock/Queue/</c> records it. The ticket's file name is
/// <c>&lt;P&gt;-&lt;Seq&gt;-&lt;PID&gt;-&lt;StartTicks&gt;.ticket</c>: P is 0 for High and 1 for Normal, Seq the arrival sequence (19
/// digits), then the waiter's process ID and start time in UTC ticks (0 when unknown). Sorting the names ordinally gives the
/// queue's order. Seq is the UTC arrival time in .NET ticks, raised above every ticket already in the queue, so it only grows:
/// a wall clock that steps back never puts a later arrival ahead of an earlier one. The content is this class as JSON.
/// </summary>
public sealed class LockTicketInfo
{
	/// <summary>Who is waiting, for status output (for example "W3-Verify/build").</summary>
	public string Name { get; set; } = "";

	/// <summary>The waiting process.</summary>
	public int Pid { get; set; }

	/// <summary>The waiting process's start time (UTC), or null when unknown.</summary>
	public DateTime? ProcessStart { get; set; }

	/// <summary>The waiter's priority.</summary>
	public LockPriority Priority { get; set; }

	/// <summary>When the waiter joined the queue (UTC).</summary>
	public DateTime Queued { get; set; }

	/// <summary>The machine the waiter runs on (the mutex, and so the queue, is per machine).</summary>
	public string? Machine { get; set; }

	/// <summary>The ticket file (not stored in it).</summary>
	[JsonIgnore]
	public string FilePath { get; set; } = "";

	/// <summary>The waiting process as an identity.</summary>
	[JsonIgnore]
	public ProcessIdentity Process => new(Pid, ProcessStart);
}

/// <summary>
/// The current holder of the editor lock, as <c>&lt;State&gt;/Lock/holder.json</c> records it. For status only: the mutex is
/// the lock. A holder that died leaves the file behind until the next holder replaces it, so readers check
/// <see cref="Process"/> is alive.
/// </summary>
public sealed class LockHolderInfo
{
	/// <summary>Who holds the lock.</summary>
	public string Name { get; set; } = "";

	/// <summary>The holding process.</summary>
	public int Pid { get; set; }

	/// <summary>The holding process's start time (UTC), or null when unknown.</summary>
	public DateTime? ProcessStart { get; set; }

	/// <summary>The holder's priority when it waited.</summary>
	public LockPriority Priority { get; set; }

	/// <summary>When the lock was acquired (UTC).</summary>
	public DateTime Acquired { get; set; }

	/// <summary>How long the holder waited in the queue.</summary>
	public double WaitedSeconds { get; set; }

	/// <summary>What the holder runs, when it said (for example the command of <c>uak lock run</c>).</summary>
	public string? Command { get; set; }

	/// <summary>The holder's machine.</summary>
	public string? Machine { get; set; }

	/// <summary>Unique per hold, so a holder only ever removes its own record.</summary>
	public string HoldId { get; set; } = "";

	/// <summary>Addition: what the lock covers, as "project &lt;dir&gt;" or "engine &lt;root&gt;" (see <see cref="EditorLock.GetScope"/>).</summary>
	public string? Scope { get; set; }

	/// <summary>
	/// Addition: the key the lock's mutex is derived from (<see cref="EditorLockScope.Key"/>), so a new holder can tell whether
	/// a record is of its own lock.
	/// </summary>
	public string? ScopeKey { get; set; }

	/// <summary>
	/// Addition: the process of the command the holder runs (<c>uak lock run</c>'s child, or the UBT or editor process of
	/// <c>uak build</c> and <c>uak test</c>). If the holder dies, the next holder waits for it to end before it goes ahead.
	/// </summary>
	public int? CommandPid { get; set; }

	/// <summary>Addition: the start time (UTC) of <see cref="CommandPid"/>, or null when unknown.</summary>
	public DateTime? CommandProcessStart { get; set; }

	/// <summary>
	/// Addition: the named Windows job object that holds the command's whole tree (<c>uak lock run</c>), named after
	/// <see cref="HoldId"/>. If the holder dies, the next holder waits until the job holds no process. Null when there is none.
	/// </summary>
	public string? CommandJob { get; set; }

	/// <summary>The holding process as an identity.</summary>
	[JsonIgnore]
	public ProcessIdentity Process => new(Pid, ProcessStart);

	/// <summary>The command's process as an identity, or null when none was recorded.</summary>
	[JsonIgnore]
	public ProcessIdentity? CommandProcess => CommandPid is int pid ? new ProcessIdentity(pid, CommandProcessStart) : null;
}

/// <summary>Where the lock's state files live under a state directory.</summary>
internal sealed class LockPaths
{
	public LockPaths(DirectoryInfo stateDirectory)
	{
		LockDirectory = Path.Combine(stateDirectory.FullName, "Lock");
		QueueDirectory = Path.Combine(LockDirectory, "Queue");
		HolderFile = Path.Combine(LockDirectory, "holder.json");
	}

	/// <summary>&lt;State&gt;/Lock.</summary>
	public string LockDirectory { get; }

	/// <summary>&lt;State&gt;/Lock/Queue: the tickets.</summary>
	public string QueueDirectory { get; }

	/// <summary>&lt;State&gt;/Lock/holder.json.</summary>
	public string HolderFile { get; }
}

/// <summary>The queue's tickets: naming, listing (with dead tickets removed) and creating.</summary>
internal static class LockQueue
{
	public const string TicketExtension = ".ticket";

	public static string TicketFileName(LockPriority priority, long sequence, ProcessIdentity process)
	{
		long startTicks = process.StartTimeUtc?.Ticks ?? 0;
		return string.Create(CultureInfo.InvariantCulture, $"{(priority == LockPriority.High ? 0 : 1)}-{sequence:D19}-{process.Pid}-{startTicks}{TicketExtension}");
	}

	/// <summary>Parses a ticket file name. False for any other file.</summary>
	public static bool TryParseFileName(string fileName, out LockPriority priority, out long sequence, out ProcessIdentity process)
	{
		priority = LockPriority.Normal;
		sequence = 0;
		process = default;
		if (!fileName.EndsWith(TicketExtension, StringComparison.Ordinal))
		{
			return false;
		}
		string[] parts = fileName[..^TicketExtension.Length].Split('-');
		if (parts.Length != 4 || (parts[0] != "0" && parts[0] != "1") || parts[1].Length != 19
			|| !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
			|| !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
			|| !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long startTicks))
		{
			return false;
		}
		priority = parts[0] == "0" ? LockPriority.High : LockPriority.Normal;
		DateTime? start = startTicks > 0 && startTicks <= DateTime.MaxValue.Ticks ? new DateTime(startTicks, DateTimeKind.Utc) : null;
		process = new ProcessIdentity(pid, start);
		return true;
	}

	/// <summary>Whether a recorded machine name is another machine's. An unrecorded one counts as this machine's.</summary>
	public static bool IsOtherMachine(string? machine)
	{
		return !string.IsNullOrEmpty(machine) && !string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The live tickets, first in line first. A ticket whose process has died (not running, or a different process with the
	/// same ID) is deleted on the way, and left out.
	/// </summary>
	public static List<LockTicketInfo> List(LockPaths paths)
	{
		return List(paths, out _);
	}

	/// <summary>
	/// The live tickets, first in line first, as <see cref="List(LockPaths)"/>. Tickets that another machine wrote (the
	/// state directory is shared, which the lock does not support) go to <paramref name="otherMachines"/> instead: they are
	/// neither waited for (their process cannot be checked from here) nor deleted.
	/// </summary>
	public static List<LockTicketInfo> List(LockPaths paths, out List<LockTicketInfo> otherMachines)
	{
		otherMachines = [];
		string[] files;
		try
		{
			files = Directory.GetFiles(paths.QueueDirectory, "*" + TicketExtension);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
		Array.Sort(files, StringComparer.Ordinal);
		List<LockTicketInfo> tickets = [];
		foreach (string file in files)
		{
			string fileName = Path.GetFileName(file);
			if (!TryParseFileName(fileName, out LockPriority priority, out long sequence, out ProcessIdentity process))
			{
				continue;
			}
			LockTicketInfo? content = StateFiles.ReadJson<LockTicketInfo>(file);
			if (IsOtherMachine(content?.Machine))
			{
				content!.FilePath = file;
				otherMachines.Add(content);
				continue;
			}
			if (!process.IsAlive())
			{
				// On Windows the file went with its process (delete-on-close); elsewhere it is left behind.
				StateFiles.TryDelete(file);
				continue;
			}
			LockTicketInfo ticket = content ?? new LockTicketInfo { Name = "(unreadable ticket)" };
			// The name is the ticket's truth for order and liveness.
			ticket.FilePath = file;
			ticket.Priority = priority;
			ticket.Pid = process.Pid;
			ticket.ProcessStart = process.StartTimeUtc ?? ticket.ProcessStart;
			if (ticket.Queued == default)
			{
				ticket.Queued = new DateTime(Math.Clamp(sequence, 0, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
			}
			tickets.Add(ticket);
		}
		return tickets;
	}

	/// <summary>The highest sequence of any ticket in the queue (live or not), or 0 when there is none.</summary>
	public static long GetHighestSequence(LockPaths paths)
	{
		long highest = 0;
		try
		{
			foreach (string file in Directory.EnumerateFiles(paths.QueueDirectory, "*" + TicketExtension))
			{
				if (TryParseFileName(Path.GetFileName(file), out _, out long sequence, out _))
				{
					highest = Math.Max(highest, sequence);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
		return highest;
	}
}

/// <summary>
/// This process's ticket. The file is created create-new, shared for reading and deleting only, and deleted when closed; on
/// Windows it also goes when the process dies. Kept open while waiting.
/// </summary>
internal sealed class QueueTicket : IDisposable
{
	/// <summary>The last sequence this process used, so its own tickets never share one.</summary>
	private static long s_lastSequence;

	private readonly LockPaths _paths;
	private readonly byte[] _content;
	private FileStream? _stream;

	private QueueTicket(LockPaths paths, string filePath, byte[] content, FileStream stream)
	{
		_paths = paths;
		FilePath = filePath;
		_content = content;
		_stream = stream;
	}

	/// <summary>The ticket file.</summary>
	public string FilePath { get; }

	/// <summary>
	/// The sequence for a new ticket: the wall clock in UTC ticks, raised above every ticket in the queue and every sequence
	/// this process used, so the queue's order follows arrival even when the clock steps back.
	/// </summary>
	internal static long NextSequence(LockPaths paths, DateTime nowUtc)
	{
		long floor = Math.Max(LockQueue.GetHighestSequence(paths), Interlocked.Read(ref s_lastSequence)) + 1;
		long sequence = Math.Max(nowUtc.Ticks, floor);
		long last;
		do
		{
			last = Interlocked.Read(ref s_lastSequence);
			sequence = Math.Max(sequence, last + 1);
		}
		while (Interlocked.CompareExchange(ref s_lastSequence, sequence, last) != last);
		return sequence;
	}

	/// <summary>Creates a ticket at the back of the queue for its priority.</summary>
	public static QueueTicket Create(LockPaths paths, string name, LockPriority priority, ProcessIdentity process)
	{
		Directory.CreateDirectory(paths.QueueDirectory);
		for (int attempt = 0; ; attempt++)
		{
			DateTime queued = DateTime.UtcNow;
			long sequence = NextSequence(paths, queued);
			string filePath = Path.Combine(paths.QueueDirectory, LockQueue.TicketFileName(priority, sequence, process));
			LockTicketInfo info = new()
			{
				Name = name,
				Pid = process.Pid,
				ProcessStart = process.StartTimeUtc,
				Priority = priority,
				Queued = queued,
				Machine = Environment.MachineName,
			};
			byte[] content = UakJson.SerializeToUtf8(info);
			FileStream? stream = TryOpen(filePath, content);
			if (stream is not null)
			{
				return new QueueTicket(paths, filePath, content, stream);
			}
			if (attempt >= 50)
			{
				throw new IOException($"Cannot create a lock ticket in {paths.QueueDirectory}.");
			}
			// The name is taken (another ticket of this process with the same sequence): the next sequence is higher.
		}
	}

	private static FileStream? TryOpen(string filePath, byte[] content)
	{
		FileStream stream;
		try
		{
			stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1, FileOptions.DeleteOnClose);
		}
		catch (IOException)
		{
			return null;
		}
		try
		{
			stream.Write(content);
			stream.Flush(flushToDisk: true);
			return stream;
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Makes sure the ticket file still exists (something may have removed it, for example a status reader that could not
	/// tell this process was alive), recreating it under the same name and so the same place.
	/// </summary>
	public void EnsureExists()
	{
		if (_stream is null || File.Exists(FilePath))
		{
			return;
		}
		_stream.Dispose();
		Directory.CreateDirectory(_paths.QueueDirectory);
		_stream = TryOpen(FilePath, _content);
	}

	/// <summary>Leaves the queue: closes the ticket, which deletes it.</summary>
	public void Dispose()
	{
		_stream?.Dispose();
		_stream = null;
	}
}
