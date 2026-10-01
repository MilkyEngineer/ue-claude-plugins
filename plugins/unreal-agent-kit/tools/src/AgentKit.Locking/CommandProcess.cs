// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.ComponentModel;
using System.Diagnostics;
using AgentKit.Core;

namespace AgentKit.Locking;

/// <summary>Starting the command a lock or a run wraps, with its standard streams left to the caller.</summary>
internal static class CommandProcess
{
	/// <summary>
	/// The start info for a command and its arguments. A <c>.ps1</c> runs through PowerShell (<c>powershell.exe</c> on Windows,
	/// <c>pwsh</c> elsewhere). On Windows the command line is built as <see cref="ProcessRunner.GetWindowsCommand"/> builds it
	/// (Unreal-style quoting, which .NET and C programs read back exactly; .bat and .cmd through cmd.exe); elsewhere the
	/// arguments pass as a list. The caller sets any redirection.
	/// <para>
	/// A bare program name ("git", "build.cmd", and the PowerShell that runs a .ps1) is resolved on PATH only
	/// (<see cref="ExecutableLocator"/>): never in the current or working directory, where a repository could plant one.
	/// </para>
	/// </summary>
	/// <exception cref="Win32Exception">A bare program name is not on PATH.</exception>
	public static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> command, string? workingDirectory)
	{
		if (command.Count == 0)
		{
			throw new ArgumentException("The command is empty.", nameof(command));
		}
		ProcessInvocation invocation = new(ResolveProgram(command[0]), [.. command.Skip(1)], workingDirectory);
		if (string.Equals(Path.GetExtension(command[0]), ".ps1", StringComparison.OrdinalIgnoreCase))
		{
			invocation = invocation with
			{
				FileName = ResolveProgram(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh"),
				Arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", command[0], .. command.Skip(1)],
			};
		}
		ProcessStartInfo startInfo = new() { UseShellExecute = false };
		if (OperatingSystem.IsWindows())
		{
			(string fileName, string commandLine) = ProcessRunner.GetWindowsCommand(invocation);
			// A .bat or .cmd runs through %ComSpec%, else a bare "cmd.exe": that one is resolved on PATH too.
			startInfo.FileName = ResolveProgram(fileName);
			startInfo.Arguments = commandLine;
		}
		else
		{
			startInfo.FileName = invocation.FileName;
			foreach (string argument in invocation.Arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}
		}
		if (!string.IsNullOrEmpty(workingDirectory))
		{
			startInfo.WorkingDirectory = workingDirectory;
		}
		return startInfo;
	}

	/// <summary>
	/// A program to start: a bare name becomes its absolute path on PATH; a path is kept (a script given as a bare name, such
	/// as "x.ps1", is a file argument, not a program, and is never passed here).
	/// </summary>
	/// <exception cref="Win32Exception">A bare name is not on PATH (ERROR_FILE_NOT_FOUND).</exception>
	public static string ResolveProgram(string program)
	{
		if (string.Equals(Path.GetExtension(program), ".ps1", StringComparison.OrdinalIgnoreCase))
		{
			// Run through PowerShell, which gets it as a file argument.
			return program;
		}
		return ExecutableLocator.Resolve(program)
			?? throw new Win32Exception(2, $"'{program}' is not on PATH. uak never runs a bare program name from the current directory: give its path, or add its folder to PATH.");
	}

	/// <summary>A command as one line, for reading only.</summary>
	public static string Format(IReadOnlyList<string> command)
	{
		return command.Count == 0 ? "" : new ProcessInvocation(command[0], [.. command.Skip(1)]).ToString();
	}
}
