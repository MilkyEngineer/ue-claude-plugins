// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Locking;

/// <summary>
/// A waiter's place class in the editor lock queue. High waiters are served before every Normal waiter, each class in arrival
/// order. A High waiter never preempts a holder.
/// </summary>
public enum LockPriority
{
	/// <summary>The default.</summary>
	Normal,

	/// <summary>Ahead of every Normal waiter.</summary>
	High,
}

/// <summary>Parsing and formatting of <see cref="LockPriority"/>.</summary>
public static class LockPriorities
{
	/// <summary>Parses "High" or "Normal", ignoring case. Null for anything else.</summary>
	public static LockPriority? Parse(string? text)
	{
		if (string.Equals(text, "High", StringComparison.OrdinalIgnoreCase))
		{
			return LockPriority.High;
		}
		if (string.Equals(text, "Normal", StringComparison.OrdinalIgnoreCase))
		{
			return LockPriority.Normal;
		}
		return null;
	}
}
