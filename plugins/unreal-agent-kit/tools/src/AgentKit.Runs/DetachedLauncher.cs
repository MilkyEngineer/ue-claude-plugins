// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentKit.Runs;

/// <summary>A process started detached, from <see cref="DetachedLauncher.Start"/>. Dispose it to close the launcher's handle; the process runs on.</summary>
public sealed class DetachedProcess : IDisposable
{
	private readonly Func<int?> _getExitCode;
	private readonly Action _dispose;

	internal DetachedProcess(int pid, string detach, Func<int?> getExitCode, Action dispose)
	{
		Pid = pid;
		Detach = detach;
		_getExitCode = getExitCode;
		_dispose = dispose;
	}

	/// <summary>The process ID.</summary>
	public int Pid { get; }

	/// <summary>How it was detached: "breakaway", "job" or "session" (see <see cref="RunRecord.Detach"/>).</summary>
	public string Detach { get; }

	/// <summary>The exit code once the process has exited, else null.</summary>
	public int? ExitCode => _getExitCode();

	/// <summary>Closes the launcher's handle to the process.</summary>
	public void Dispose()
	{
		_dispose();
	}
}

/// <summary>
/// Starts a process that outlives its caller: the caller's shell ending, its console closing, or the job it runs in being
/// closed (as Claude Code closes its shells' jobs).
/// - Windows: CreateProcess with DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB, and no inherited
///   handles (so the caller's output pipes never wait on it). When the caller's job refuses breakaway, it starts again inside
///   the job ("job"): the process then ends with the job.
/// - Unix: a normal start with every standard stream redirected; the process itself calls setsid() first thing ("session",
///   see <see cref="RunWrapper"/>). TODO(unix): untested.
/// The process gets no standard handles; the run wrapper opens its own output.
/// </summary>
public static class DetachedLauncher
{
	/// <summary>
	/// Starts the command detached.
	/// </summary>
	/// <param name="getCommand">The command line for a detach method ("breakaway", "job" or "session"), so the process can be told how it was started.</param>
	/// <param name="workingDirectory">The directory it starts in.</param>
	/// <exception cref="Win32Exception">The program cannot be started, or is a bare name that is not on PATH.</exception>
	public static DetachedProcess Start(Func<string, IReadOnlyList<string>> getCommand, string workingDirectory)
	{
		ArgumentNullException.ThrowIfNull(getCommand);
		Func<string, IReadOnlyList<string>> asGiven = getCommand;
		// A bare program name is looked up on PATH only, never in the working or current directory (ExecutableLocator).
		getCommand = detach =>
		{
			IReadOnlyList<string> command = asGiven(detach);
			return [AgentKit.Locking.CommandProcess.ResolveProgram(command[0]), .. command.Skip(1)];
		};
		if (OperatingSystem.IsWindows())
		{
			return StartWindows(getCommand, workingDirectory);
		}
		return StartUnix(getCommand, workingDirectory);
	}

	[SupportedOSPlatform("windows")]
	private static DetachedProcess StartWindows(Func<string, IReadOnlyList<string>> getCommand, string workingDirectory)
	{
		uint baseFlags = WindowsNative.DETACHED_PROCESS | WindowsNative.CREATE_NEW_PROCESS_GROUP | WindowsNative.CREATE_UNICODE_ENVIRONMENT;
		foreach (bool breakaway in new[] { true, false })
		{
			string detach = breakaway ? "breakaway" : "job";
			StringBuilder commandLine = new(WindowsNative.JoinCommandLine(getCommand(detach)));
			WindowsNative.STARTUPINFOW startupInfo = new()
			{
				cb = Marshal.SizeOf<WindowsNative.STARTUPINFOW>(),
				// No standard handles at all: nothing of the caller's (its shell's pipes) reaches the process.
				dwFlags = WindowsNative.STARTF_USESTDHANDLES,
			};
			uint flags = baseFlags | (breakaway ? WindowsNative.CREATE_BREAKAWAY_FROM_JOB : 0);
			if (WindowsNative.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workingDirectory,
				ref startupInfo, out WindowsNative.PROCESS_INFORMATION info))
			{
				WindowsNative.CloseHandle(info.hThread);
				IntPtr process = info.hProcess;
				int closed = 0;
				return new DetachedProcess(info.dwProcessId, detach,
					() =>
					{
						// The wait, not the exit code, tells whether it has exited (STILL_ACTIVE is also a possible exit code).
						if (Volatile.Read(ref closed) != 0 || WindowsNative.WaitForSingleObject(process, 0) != WindowsNative.WAIT_OBJECT_0
							|| !WindowsNative.GetExitCodeProcess(process, out uint code))
						{
							return null;
						}
						return unchecked((int)code);
					},
					() =>
					{
						if (Interlocked.Exchange(ref closed, 1) == 0)
						{
							WindowsNative.CloseHandle(process);
						}
					});
			}
			int error = Marshal.GetLastWin32Error();
			// The job does not allow breakaway (access denied): start inside it.
			if (!breakaway || error != WindowsNative.ERROR_ACCESS_DENIED)
			{
				throw new Win32Exception(error, $"Cannot start '{commandLine}': {new Win32Exception(error).Message}");
			}
		}
		throw new InvalidOperationException("Unreachable.");
	}

	private static DetachedProcess StartUnix(Func<string, IReadOnlyList<string>> getCommand, string workingDirectory)
	{
		// TODO(unix): untested. The process calls setsid() itself (RunWrapper), so it leaves the caller's session and process
		// group; its standard streams are pipes the launcher closes once the process has recorded itself.
		IReadOnlyList<string> command = getCommand("session");
		ProcessStartInfo startInfo = new(command[0])
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = workingDirectory,
		};
		foreach (string argument in command.Skip(1))
		{
			startInfo.ArgumentList.Add(argument);
		}
		Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Cannot start '{command[0]}'.");
		process.StandardInput.Close();
		return new DetachedProcess(process.Id, "session", () => process.HasExited ? process.ExitCode : null, process.Dispose);
	}
}
