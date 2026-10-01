// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core;

/// <summary>The exit codes every `uak` command uses (DESIGN.md, "Command line").</summary>
public static class UakExitCodes
{
	/// <summary>The command did what was asked.</summary>
	public const int Success = 0;

	/// <summary>The command ran and failed: a failed build or test, a failed child process, an unexpected error.</summary>
	public const int Failure = 1;

	/// <summary>The command could not run: bad arguments, an unknown command, or no project or engine could be found.</summary>
	public const int UsageError = 2;
}

/// <summary>
/// A usage error: bad or missing arguments. The host prints the message (and where to find the command's help) and exits
/// with <see cref="UakExitCodes.UsageError"/>.
/// </summary>
public class UakUsageException : Exception
{
	/// <summary>Creates the exception with a message for the user.</summary>
	public UakUsageException(string message) : base(message)
	{
	}

	/// <summary>Creates the exception with a message for the user and the failure that caused it.</summary>
	public UakUsageException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>
/// A setup error: the project or engine cannot be found or is not valid. The host prints the message and exits with
/// <see cref="UakExitCodes.UsageError"/>.
/// </summary>
public class UakSetupException : UakUsageException
{
	/// <summary>Creates the exception with a message for the user.</summary>
	public UakSetupException(string message) : base(message)
	{
	}

	/// <summary>Creates the exception with a message for the user and the failure that caused it.</summary>
	public UakSetupException(string message, Exception innerException) : base(message, innerException)
	{
	}
}
