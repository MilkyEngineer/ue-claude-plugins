// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Locking;

/// <summary>
/// The <see cref="EditorLock.HeldVariable"/> markers: which editor locks this process's ancestors hold, and which this process
/// holds itself. A holder adds its marker to its own environment for the length of the hold, so every process it starts
/// (the command of <c>uak lock run</c>, UBT, the editor, a nested <c>uak build</c>) inherits it. A lock request that finds an
/// inherited marker for its own lock joins that hold instead of queueing behind its own ancestor, which would never end.
/// </summary>
internal static class HeldMarkers
{
	private const char Separator = ';';

	/// <summary>
	/// The markers this process inherited from its parent, read before this process set any of its own. The type is first
	/// used by a lock request, before any hold of this process exists.
	/// </summary>
	private static readonly string? s_inherited = Environment.GetEnvironmentVariable(EditorLock.HeldVariable);

	private static readonly object s_gate = new();
	private static readonly List<string> s_own = [];

	/// <summary>The inherited value, or null when this process runs under no holder.</summary>
	public static string? Inherited => s_inherited;

	/// <summary>A marker: <c>&lt;mutex name&gt;:&lt;HoldId&gt;</c>.</summary>
	public static string Format(string mutexName, string holdId)
	{
		return $"{mutexName}:{holdId}";
	}

	/// <summary>The (mutex name, HoldId) pairs of a value. Malformed entries come back with an empty HoldId.</summary>
	public static List<(string MutexName, string HoldId)> Parse(string? value)
	{
		List<(string, string)> markers = [];
		if (string.IsNullOrWhiteSpace(value))
		{
			return markers;
		}
		foreach (string entry in value.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			// Mutex names hold no ':' (they are "Global\<base>_<hash>"), so the last one separates the HoldId.
			int colon = entry.LastIndexOf(':');
			markers.Add(colon <= 0 ? (entry, "") : (entry[..colon], entry[(colon + 1)..]));
		}
		return markers;
	}

	/// <summary>Adds one of this process's holds to its environment, so the processes it starts from now on inherit it.</summary>
	public static void Add(string marker)
	{
		lock (s_gate)
		{
			s_own.Add(marker);
			Update();
		}
	}

	/// <summary>Removes one of this process's holds from its environment.</summary>
	public static void Remove(string marker)
	{
		lock (s_gate)
		{
			s_own.Remove(marker);
			Update();
		}
	}

	private static void Update()
	{
		IEnumerable<string> all = s_own;
		if (!string.IsNullOrWhiteSpace(s_inherited))
		{
			all = all.Prepend(s_inherited.Trim(Separator));
		}
		string value = string.Join(Separator, all);
		Environment.SetEnvironmentVariable(EditorLock.HeldVariable, value.Length == 0 ? null : value);
	}

	/// <summary>
	/// Decides what an inherited marker means for a request for <paramref name="mutexName"/>:
	/// <list type="bullet">
	/// <item>no marker: null, so the request queues as usual;</item>
	/// <item>a marker for this lock whose HoldId holder.json still records, with its holder alive: a nested hold, which the
	/// request returns at once;</item>
	/// <item>anything else (a marker for another lock, or for a hold that has ended): an <see cref="EditorLockException"/>,
	/// because waiting under an ancestor's hold could deadlock.</item>
	/// </list>
	/// </summary>
	public static EditorLockHold? TryJoin(string? inherited, string mutexName, EditorLockScope scope, LockPaths paths, string name, ILogger logger)
	{
		List<(string MutexName, string HoldId)> markers = Parse(inherited);
		if (markers.Count == 0)
		{
			return null;
		}
		List<(string MutexName, string HoldId)> mine = [.. markers.Where(marker => string.Equals(marker.MutexName, mutexName, StringComparison.OrdinalIgnoreCase))];
		if (mine.Count == 0)
		{
			throw new EditorLockException(
				$"'{name}' asks for the editor lock of {scope}, but it runs under a holder of another editor lock ({EditorLock.HeldVariable}={inherited}). " +
				"Waiting for a second lock inside the first could deadlock, so the request fails. Take this lock outside the other one, " +
				$"or, if no lock is held above this process, unset {EditorLock.HeldVariable}.");
		}
		LockHolderInfo? holder = StateFiles.ReadJson<LockHolderInfo>(paths.HolderFile);
		foreach (var (_, holdId) in mine)
		{
			if (holder is not null && holdId.Length > 0 && string.Equals(holder.HoldId, holdId, StringComparison.Ordinal)
				&& !LockQueue.IsOtherMachine(holder.Machine) && holder.Process.IsAlive())
			{
				logger.LogInformation("The editor lock for {Scope} is already held above this process by '{Holder}' (PID {Pid}): '{Name}' joins that hold instead of waiting.",
					scope, holder.Name, holder.Pid, name);
				return EditorLockHold.CreateNested(paths, mutexName, scope, holder, logger);
			}
		}
		string recorded = holder is null
			? "no holder is recorded"
			: $"holder.json records hold {holder.HoldId} by '{holder.Name}' ({holder.Process}, {(holder.Process.IsAlive() ? "running" : "gone")})";
		throw new EditorLockException(
			$"'{name}' runs under a hold of the editor lock of {scope} ({EditorLock.HeldVariable}={inherited}), but that hold has ended: {recorded} in {paths.HolderFile}. " +
			"Waiting now could deadlock behind the process that started this one, so the request fails. If no lock is held above this process, " +
			$"unset {EditorLock.HeldVariable}.");
	}
}
