// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using EpicGames.Core;
using Microsoft.Extensions.Logging;
using UnrealBuildBase;

namespace AgentKit.Locking;

/// <summary>Settings for <see cref="EditorLock.AcquireAsync(UakContext, string?, LockPriority?, EditorLockOptions, CancellationToken)"/>.</summary>
public sealed class EditorLockOptions
{
	/// <summary>The defaults.</summary>
	public static EditorLockOptions Default { get; } = new();

	/// <summary>
	/// How often a waiter checks its place in the queue. The first in line waits on the mutex itself and gets it as soon as it
	/// is released; this only bounds how soon a waiter that moves to the front starts waiting on it, and how soon a newly
	/// arrived High waiter takes the front from a Normal one.
	/// </summary>
	public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

	/// <summary>What the holder will run, recorded in holder.json for status output. Optional.</summary>
	public string? Command { get; init; }

	/// <summary>The mutex name, instead of the one <see cref="EditorLock.GetMutexName"/> derives. For tests.</summary>
	public string? MutexName { get; init; }

	/// <summary>
	/// How long a new holder waits for the command of a holder that died (<see cref="LockHolderInfo.CommandPid"/>) to end
	/// before it goes ahead anyway, with a warning.
	/// </summary>
	public TimeSpan OrphanedCommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// What an editor lock covers: the project directory, else the engine root, else the state directory. <see cref="Path"/> is
/// its canonical path, for reading (<see cref="CanonicalPath.Get"/>); <see cref="Key"/> is what the mutex is keyed on.
/// </summary>
/// <param name="Kind">"project", "engine" or "state".</param>
/// <param name="Path">The canonical directory.</param>
public sealed record EditorLockScope(string Kind, string Path)
{
	/// <summary>
	/// The directory's identity (<see cref="CanonicalPath.TryGetIdentity"/>: the volume and file ID on Windows), so every path
	/// to one project gives one lock, the loopback share (<c>\\localhost\C$\...</c>) included. The canonical path when the
	/// system cannot tell, and always for the "state" kind, whose directory may not exist yet (its key must not change when it
	/// is created).
	/// </summary>
	public string Key { get; init; } = Path;

	/// <summary>"project C:\Projects\Game".</summary>
	public override string ToString() => $"{Kind} {Path}";
}

/// <summary>A lock request that cannot be served: waiting for it would deadlock, or the inherited hold it names is not valid.</summary>
public sealed class EditorLockException : Exception
{
	/// <summary>Creates the exception.</summary>
	public EditorLockException(string message) : base(message)
	{
	}

	/// <summary>Creates the exception.</summary>
	public EditorLockException()
	{
	}

	/// <summary>Creates the exception with its cause.</summary>
	public EditorLockException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>
/// The queued editor lock: one editor, commandlet, game or build run at a time per project (DESIGN.md, "The queued lock").
/// The lock is an OS named mutex (<see cref="SingleInstanceMutex"/>), which the kernel releases when its holder dies. On top
/// of it, a queue of ticket files gives fair order (High before Normal, then arrival) and status; only the first in line
/// waits on the mutex.
/// <para>
/// While a hold lasts, this process's <see cref="HeldVariable"/> names it, so every process it starts inherits the marker.
/// A lock request in such a process for the same lock joins the hold instead of waiting for it (which would deadlock); a
/// request for any other lock fails at once (see <see cref="AcquireAsync(UakContext, string?, LockPriority?, EditorLockOptions, CancellationToken)"/>).
/// </para>
/// </summary>
/// <example><code>await using EditorLockHold hold = await EditorLock.AcquireAsync(context, "build", null, cancellationToken);</code></example>
public static class EditorLock
{
	/// <summary>The environment variable giving the default lock name: <c>uak runs start</c> sets it to the run's name.</summary>
	public const string NameVariable = "UAK_LOCK_NAME";

	/// <summary>The environment variable giving the default priority (High or Normal): <c>uak runs start -priority=</c> sets it.</summary>
	public const string PriorityVariable = "UAK_LOCK_PRIORITY";

	/// <summary>
	/// The environment variable that marks the holds of a process's ancestors: <c>&lt;mutex name&gt;:&lt;HoldId&gt;</c>, several
	/// separated by ';'. A holder sets it in its own environment for the length of the hold, so its children inherit it.
	/// </summary>
	public const string HeldVariable = "UAK_LOCK_HELD";

	/// <summary>The base name of the mutex, made unique per project by <see cref="GetMutexName"/>.</summary>
	public const string MutexBaseName = "UnrealAgentKit_EditorLock";

	/// <summary>
	/// Waits in the queue and takes the editor lock. Dispose the returned hold to release it; if the process dies, the kernel
	/// releases the mutex and the next waiter takes it.
	/// </summary>
	/// <param name="context">Gives the state directory (for the queue) and the project or engine (for the mutex name).</param>
	/// <param name="name">Who is asking, for status output. Null takes the default (see <see cref="ResolveName"/>).</param>
	/// <param name="priority">Null takes the default: <see cref="PriorityVariable"/>, else Normal.</param>
	/// <param name="cancellationToken">Cancels the wait; the waiter then leaves the queue and the call throws <see cref="OperationCanceledException"/>.</param>
	public static Task<EditorLockHold> AcquireAsync(UakContext context, string? name, LockPriority? priority, CancellationToken cancellationToken)
	{
		return AcquireAsync(context, name, priority, EditorLockOptions.Default, cancellationToken);
	}

	/// <inheritdoc cref="AcquireAsync(UakContext, string?, LockPriority?, CancellationToken)"/>
	/// <param name="context">Gives the state directory (for the queue) and the project or engine (for the mutex name).</param>
	/// <param name="name">Who is asking, for status output. Null takes the default (see <see cref="ResolveName"/>).</param>
	/// <param name="priority">Null takes the default: <see cref="PriorityVariable"/>, else Normal.</param>
	/// <param name="options">Poll interval, the command for status output, and the mutex name for tests.</param>
	/// <param name="cancellationToken">Cancels the wait; the waiter then leaves the queue and the call throws <see cref="OperationCanceledException"/>.</param>
	/// <exception cref="EditorLockException">
	/// This process inherited <see cref="HeldVariable"/> from a holder, and the request is for another lock, or the hold it
	/// names is no longer held: waiting could deadlock, so the request fails at once.
	/// </exception>
	public static async Task<EditorLockHold> AcquireAsync(UakContext context, string? name, LockPriority? priority, EditorLockOptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(options);
		string resolvedName = ResolveName(name);
		LockPriority resolvedPriority = ResolvePriority(priority);
		string mutexName = options.MutexName ?? GetMutexName(context);
		EditorLockScope scope = GetScope(context);
		LockPaths paths = new(context.StateDirectory);
		ProcessIdentity self = ProcessIdentity.Current;
		ILogger logger = context.Logger;
		TimeSpan poll = options.PollInterval > TimeSpan.Zero ? options.PollInterval : EditorLockOptions.Default.PollInterval;

		EditorLockHold? nested = HeldMarkers.TryJoin(HeldMarkers.Inherited, mutexName, scope, paths, resolvedName, logger);
		if (nested is not null)
		{
			return nested;
		}

		DateTime queued = DateTime.UtcNow;
		using QueueTicket ticket = QueueTicket.Create(paths, resolvedName, resolvedPriority, self);
		int reported = -1;
		bool warnedOtherMachine = false;
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int position = GetPosition(paths, ticket, out List<LockTicketInfo> otherMachines);
			if (position != reported)
			{
				logger.LogInformation("Waiting for the editor lock ({Scope}) as '{Name}' ({Priority}), {Place}.", scope, resolvedName, resolvedPriority,
					position == 0 ? "first in line" : $"{position} ahead");
				reported = position;
			}
			if (!warnedOtherMachine)
			{
				warnedOtherMachine = WarnAboutOtherMachines(paths, otherMachines, logger);
			}
			if (position == 0)
			{
				IDisposable? mutex = await WaitAtFrontAsync(paths, ticket, mutexName, poll, cancellationToken).ConfigureAwait(false);
				if (mutex is not null)
				{
					// Leave the queue before anything else, so the next waiter moves up.
					ticket.Dispose();
					try
					{
						await WaitForOrphanedCommandAsync(paths, scope, options.OrphanedCommandTimeout, poll, logger, cancellationToken).ConfigureAwait(false);
						return EditorLockHold.Create(mutex, paths, mutexName, scope, resolvedName, resolvedPriority, self, queued, options.Command, logger);
					}
					catch
					{
						mutex.Dispose();
						throw;
					}
				}
				// Someone moved ahead (a High waiter arrived): back in line.
				continue;
			}
			await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Warns (once) when the state directory holds another machine's records: the lock is per machine, so a shared state
	/// directory does not exclude the other machine. Returns whether it warned.
	/// </summary>
	private static bool WarnAboutOtherMachines(LockPaths paths, List<LockTicketInfo> otherMachines, ILogger logger)
	{
		LockHolderInfo? holder = StateFiles.ReadJson<LockHolderInfo>(paths.HolderFile);
		List<string> machines = [.. otherMachines.Select(ticket => ticket.Machine!)];
		if (holder is not null && LockQueue.IsOtherMachine(holder.Machine))
		{
			machines.Add(holder.Machine!);
		}
		if (machines.Count == 0)
		{
			return false;
		}
		logger.LogWarning("The editor lock's state in {Directory} has records from another machine ({Machines}); this is {Machine}. The lock is per machine: keep the state directory local, or the other machine's runs are not excluded.",
			paths.LockDirectory, string.Join(", ", machines.Distinct(StringComparer.OrdinalIgnoreCase)), Environment.MachineName);
		return true;
	}

	/// <summary>
	/// After taking the mutex: if holder.json records a holder of this same lock (<paramref name="scope"/>) that died, and the
	/// command it ran still runs, waits for that command to end, so the new hold never overlaps it. The command is the
	/// recorded process (<see cref="LockHolderInfo.CommandPid"/>: the child of <c>uak lock run</c>, UBT, or the editor) and,
	/// for <c>uak lock run</c> on Windows, every process still in its named job (<see cref="LockHolderInfo.CommandJob"/>). On
	/// Windows the job dies with its holder, so this takes moments. After the timeout it goes ahead, with a warning.
	/// <para>
	/// A record of a holder that still runs, or of another lock (a state folder that several locks share), is never waited
	/// for: that holder's command is not this lock's.
	/// </para>
	/// </summary>
	private static async Task WaitForOrphanedCommandAsync(LockPaths paths, EditorLockScope scope, TimeSpan timeout, TimeSpan poll, ILogger logger, CancellationToken cancellationToken)
	{
		LockHolderInfo? previous = StateFiles.ReadJson<LockHolderInfo>(paths.HolderFile);
		if (previous is null || LockQueue.IsOtherMachine(previous.Machine) || !IsSameScope(previous, scope) || previous.Process.IsAlive())
		{
			return;
		}
		ProcessIdentity? command = previous.CommandProcess;
		string? job = previous.CommandJob;
		bool Running() => (command?.IsAlive() ?? false) || (job is not null && CommandJobs.GetActiveProcesses(job) > 0);
		if (!Running())
		{
			return;
		}
		string what = command is ProcessIdentity process ? $"{process}" : "";
		if (job is not null)
		{
			what += (what.Length > 0 ? ", " : "") + $"job {job}";
		}
		logger.LogWarning("The previous holder '{Name}' (PID {Pid}) has gone, but its command ({Command}) still runs: waiting for it to end.",
			previous.Name, previous.Pid, what);
		DateTime deadline = DateTime.UtcNow + timeout;
		TimeSpan step = poll < TimeSpan.FromMilliseconds(100) ? poll : TimeSpan.FromMilliseconds(100);
		while (Running())
		{
			if (DateTime.UtcNow > deadline)
			{
				// TODO(unix): without PDEATHSIG an orphaned command can outlive its holder on Linux and Mac.
				logger.LogWarning("The previous holder's command ({Command}) still runs after {Seconds:0} s: going ahead without it ending.", what, timeout.TotalSeconds);
				return;
			}
			await Task.Delay(step, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Whether a holder record is of the lock for <paramref name="scope"/>: the same <see cref="LockHolderInfo.ScopeKey"/>, else
	/// (a record without one) the same <see cref="LockHolderInfo.Scope"/>. A record with neither counts as the same lock, so a
	/// command that may be this lock's is waited for.
	/// </summary>
	internal static bool IsSameScope(LockHolderInfo holder, EditorLockScope scope)
	{
		StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (!string.IsNullOrEmpty(holder.ScopeKey))
		{
			return string.Equals(holder.ScopeKey, scope.Key, comparison);
		}
		return string.IsNullOrEmpty(holder.Scope) || string.Equals(holder.Scope, scope.ToString(), comparison);
	}

	/// <summary>
	/// The first in line waits on the mutex. Every poll it checks it is still first; when a waiter has moved ahead of it, it
	/// stops waiting and returns null. Returns the mutex once held (even if a waiter moved ahead at that same moment).
	/// </summary>
	private static async Task<IDisposable?> WaitAtFrontAsync(LockPaths paths, QueueTicket ticket, string mutexName, TimeSpan poll, CancellationToken cancellationToken)
	{
		using CancellationTokenSource waitCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task<IDisposable> wait = SingleInstanceMutex.AcquireAsync(mutexName, waitCancel.Token);
		while (!wait.IsCompleted)
		{
			using (CancellationTokenSource delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
			{
				Task delay = Task.Delay(poll, delayCancel.Token);
				await Task.WhenAny(wait, delay).ConfigureAwait(false);
				delayCancel.Cancel();
			}
			if (wait.IsCompleted)
			{
				break;
			}
			if (cancellationToken.IsCancellationRequested || GetPosition(paths, ticket, out _) != 0)
			{
				await waitCancel.CancelAsync().ConfigureAwait(false);
				break;
			}
		}
		try
		{
			return await wait.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return null;
		}
	}

	/// <summary>This ticket's place in the queue: 0 is first in line.</summary>
	private static int GetPosition(LockPaths paths, QueueTicket ticket, out List<LockTicketInfo> otherMachines)
	{
		ticket.EnsureExists();
		List<LockTicketInfo> tickets = LockQueue.List(paths, out otherMachines);
		int index = tickets.FindIndex(t => string.Equals(t.FilePath, ticket.FilePath, StringComparison.OrdinalIgnoreCase));
		// A ticket that cannot be listed (the queue folder is unreadable) must not wait forever: try the mutex.
		return index < 0 ? 0 : index;
	}

	/// <summary>
	/// What a context's lock covers: the project directory, else the engine root, else the state directory. Its path is
	/// canonical (junctions, links, subst and mapped drives resolved; see <see cref="CanonicalPath"/>), and its key is the
	/// directory's identity (<see cref="EditorLockScope.Key"/>).
	/// </summary>
	public static EditorLockScope GetScope(UakContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		if (context.ProjectDirectory is DirectoryInfo project)
		{
			string path = CanonicalPath.Get(project.FullName);
			return new EditorLockScope("project", path) { Key = CanonicalPath.TryGetIdentity(path) ?? path };
		}
		if (context.EngineRoot is DirectoryInfo engine)
		{
			string path = CanonicalPath.Get(engine.FullName);
			return new EditorLockScope("engine", path) { Key = CanonicalPath.TryGetIdentity(path) ?? path };
		}
		return new EditorLockScope("state", CanonicalPath.Get(context.StateDirectory.FullName));
	}

	/// <summary>
	/// The mutex name for a context: <see cref="MutexBaseName"/> made unique for its scope's key (<see cref="GetScope"/>,
	/// <see cref="EditorLockScope.Key"/>), through <see cref="GlobalSingleInstanceMutex.GetUniqueMutexForPath(string, string)"/>.
	/// </summary>
	public static string GetMutexName(UakContext context)
	{
		return GlobalSingleInstanceMutex.GetUniqueMutexForPath(MutexBaseName, GetScope(context).Key);
	}

	/// <summary>
	/// The lock name to show: with <see cref="NameVariable"/> set (inside a run), "&lt;run&gt;/&lt;name&gt;", or the run's name
	/// alone when no name is given; without it, the name, else this program's name.
	/// </summary>
	public static string ResolveName(string? name)
	{
		string? outer = Environment.GetEnvironmentVariable(NameVariable);
		bool hasName = !string.IsNullOrWhiteSpace(name);
		if (!string.IsNullOrWhiteSpace(outer))
		{
			return hasName ? $"{outer}/{name}" : outer;
		}
		if (hasName)
		{
			return name!;
		}
		string? program = Environment.ProcessPath;
		return string.IsNullOrEmpty(program) ? "unknown" : Path.GetFileNameWithoutExtension(program);
	}

	/// <summary>The priority to use: the given one, else <see cref="PriorityVariable"/>, else Normal.</summary>
	public static LockPriority ResolvePriority(LockPriority? priority)
	{
		return priority ?? LockPriorities.Parse(Environment.GetEnvironmentVariable(PriorityVariable)) ?? LockPriority.Normal;
	}

	/// <summary>Who holds the lock and who waits, for status output. Removes dead tickets on the way.</summary>
	public static EditorLockStatus GetStatus(UakContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		LockPaths paths = new(context.StateDirectory);
		LockHolderInfo? holder = StateFiles.ReadJson<LockHolderInfo>(paths.HolderFile);
		List<LockTicketInfo> queue = LockQueue.List(paths, out List<LockTicketInfo> otherMachines);
		return new EditorLockStatus
		{
			LockDirectory = paths.LockDirectory,
			MutexName = GetMutexName(context),
			Scope = GetScope(context),
			Holder = holder,
			HolderAlive = holder is not null && !LockQueue.IsOtherMachine(holder.Machine) && holder.Process.IsAlive(),
			Queue = queue,
			OtherMachineTickets = otherMachines,
		};
	}
}

/// <summary>A snapshot of the editor lock, from <see cref="EditorLock.GetStatus"/>.</summary>
public sealed class EditorLockStatus
{
	/// <summary>&lt;State&gt;/Lock.</summary>
	public required string LockDirectory { get; init; }

	/// <summary>The OS mutex name.</summary>
	public required string MutexName { get; init; }

	/// <summary>What the lock covers.</summary>
	public EditorLockScope? Scope { get; init; }

	/// <summary>The last holder.json, or null when there is none (nobody holds the lock).</summary>
	public LockHolderInfo? Holder { get; init; }

	/// <summary>
	/// Whether <see cref="Holder"/>'s process still runs. False means the record is stale: its holder died (or it is another
	/// machine's record, which cannot be checked from here).
	/// </summary>
	public bool HolderAlive { get; init; }

	/// <summary>The live waiters, first in line first.</summary>
	public required IReadOnlyList<LockTicketInfo> Queue { get; init; }

	/// <summary>Tickets another machine wrote: the state directory is shared, which the lock does not support.</summary>
	public IReadOnlyList<LockTicketInfo> OtherMachineTickets { get; init; } = [];
}

/// <summary>
/// A held editor lock, from <see cref="EditorLock.AcquireAsync(UakContext, string?, LockPriority?, CancellationToken)"/>.
/// Disposing it releases the lock. Dispose it on any thread: the mutex is released on the thread that took it. A nested hold
/// (<see cref="IsNested"/>) joined an ancestor's hold: disposing it releases nothing.
/// </summary>
public sealed class EditorLockHold : IAsyncDisposable, IDisposable
{
	private readonly IDisposable? _mutex;
	private readonly LockPaths _paths;
	private readonly ILogger _logger;
	private int _released;

	private EditorLockHold(IDisposable? mutex, LockPaths paths, LockHolderInfo info, string mutexName, EditorLockScope scope, ILogger logger)
	{
		_mutex = mutex;
		_paths = paths;
		_logger = logger;
		Info = info;
		MutexName = mutexName;
		Scope = scope;
		if (mutex is not null)
		{
			HeldMarkers.Add(Marker);
		}
	}

	internal static EditorLockHold Create(IDisposable mutex, LockPaths paths, string mutexName, EditorLockScope scope, string name, LockPriority priority,
		ProcessIdentity self, DateTime queued, string? command, ILogger logger)
	{
		DateTime now = DateTime.UtcNow;
		LockHolderInfo info = new()
		{
			Name = name,
			Pid = self.Pid,
			ProcessStart = self.StartTimeUtc,
			Priority = priority,
			Acquired = now,
			WaitedSeconds = Math.Round((now - queued).TotalSeconds, 3),
			Command = command,
			Machine = Environment.MachineName,
			HoldId = Guid.NewGuid().ToString("N"),
			Scope = scope.ToString(),
			ScopeKey = scope.Key,
		};
		WriteHolder(paths, info, logger);
		logger.LogInformation("Holding the editor lock for {Scope} as '{Name}' (waited {Waited:0.0} s).", scope, name, info.WaitedSeconds);
		return new EditorLockHold(mutex, paths, info, mutexName, scope, logger);
	}

	/// <summary>A hold that joins an ancestor's: it releases nothing. <paramref name="outer"/> is the ancestor's holder.json.</summary>
	internal static EditorLockHold CreateNested(LockPaths paths, string mutexName, EditorLockScope scope, LockHolderInfo outer, ILogger logger)
	{
		return new EditorLockHold(null, paths, outer, mutexName, scope, logger);
	}

	private static void WriteHolder(LockPaths paths, LockHolderInfo info, ILogger logger)
	{
		try
		{
			// For status only: the lock is held even if this fails.
			StateFiles.WriteJson(paths.HolderFile, info);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			logger.LogWarning("Could not write {File}: {Message}", paths.HolderFile, exception.Message);
		}
	}

	/// <summary>What holder.json records for this hold (for a nested hold, the ancestor's record).</summary>
	public LockHolderInfo Info { get; }

	/// <summary>The OS mutex held.</summary>
	public string MutexName { get; }

	/// <summary>What the lock covers.</summary>
	public EditorLockScope Scope { get; }

	/// <summary>Whether this hold joined an ancestor's hold (through <see cref="EditorLock.HeldVariable"/>) instead of taking the lock.</summary>
	public bool IsNested => _mutex is null;

	/// <summary>The value this hold adds to <see cref="EditorLock.HeldVariable"/>: <c>&lt;mutex name&gt;:&lt;HoldId&gt;</c>.</summary>
	public string Marker => HeldMarkers.Format(MutexName, Info.HoldId);

	/// <summary>Whether the lock has been released.</summary>
	public bool IsReleased => Volatile.Read(ref _released) != 0;

	/// <summary>
	/// Records the process this hold runs its command in (<see cref="LockHolderInfo.CommandPid"/>), so that if this holder
	/// dies, the next holder waits for the command to end. Does nothing for a nested hold.
	/// </summary>
	public void RecordCommandProcess(ProcessIdentity process)
	{
		RecordCommandProcess(process, job: null);
	}

	/// <summary>
	/// Records the process this hold runs its command in, and the named job object holding the command's whole tree
	/// (<see cref="LockHolderInfo.CommandJob"/>; null for none), so that if this holder dies, the next holder waits until
	/// both have ended. Does nothing for a nested hold.
	/// </summary>
	public void RecordCommandProcess(ProcessIdentity process, string? job)
	{
		if (IsNested || IsReleased)
		{
			return;
		}
		Info.CommandPid = process.Pid;
		Info.CommandProcessStart = process.StartTimeUtc;
		Info.CommandJob = job;
		WriteHolder(_paths, Info, _logger);
	}

	/// <summary>Releases the lock. Safe to call more than once.</summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _released, 1) != 0)
		{
			return;
		}
		if (_mutex is null)
		{
			_logger.LogDebug("Left the nested hold of the editor lock ('{Name}' still holds it).", Info.Name);
			return;
		}
		HeldMarkers.Remove(Marker);
		// The record first, while the lock is still held, so this never removes the next holder's.
		LockHolderInfo? current = StateFiles.ReadJson<LockHolderInfo>(_paths.HolderFile);
		if (current is not null && current.HoldId == Info.HoldId)
		{
			StateFiles.TryDelete(_paths.HolderFile);
		}
		_mutex.Dispose();
		_logger.LogDebug("Released the editor lock ('{Name}').", Info.Name);
	}

	/// <summary>Releases the lock.</summary>
	public ValueTask DisposeAsync()
	{
		Dispose();
		return ValueTask.CompletedTask;
	}
}
