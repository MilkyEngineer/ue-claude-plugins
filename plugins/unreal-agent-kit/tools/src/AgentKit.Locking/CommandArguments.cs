// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Locking;

/// <summary>Argument helpers the lock and run commands share, on top of <see cref="UakArguments"/>.</summary>
internal static class CommandArguments
{
	/// <summary>Parses <c>-priority=High|Normal</c>: null when it was not given; a usage error for anything else.</summary>
	public static LockPriority? GetPriority(UakArguments arguments)
	{
		string? text = arguments.GetString("priority");
		if (text is null)
		{
			return null;
		}
		return LockPriorities.Parse(text) ?? throw new UakUsageException($"-priority must be High or Normal, not '{text}'.");
	}

	/// <summary>
	/// The command to run: everything after "--", verbatim; without "--", the positional arguments (which cannot carry options
	/// of their own). Call after reading every option and before <see cref="UakArguments.ThrowIfUnknown"/>.
	/// </summary>
	public static IReadOnlyList<string> GetCommand(UakArguments arguments)
	{
		if (arguments.HasSeparator)
		{
			arguments.ThrowIfMorePositionalThan(0);
			return arguments.Rest;
		}
		return arguments.Positional;
	}
}
