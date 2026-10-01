// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AgentKit.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AgentKit.Locking;

/// <summary>
/// Runs the command of <c>uak lock run</c> so that it never outlives the hold: its standard streams are this process's (it
/// shares the console, as with <see cref="Process.Start()"/>), but its whole process tree ends when the hold does.
/// <list type="bullet">
/// <item>Windows: it starts suspended, goes into a job object that kills its processes when its last handle closes
/// (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE), then resumes. Only this process holds the job's handle, so the tree dies with uak
/// even when uak is killed. The job allows breakaway (JOB_OBJECT_LIMIT_BREAKAWAY_OK), so a detached run started inside it
/// (<c>uak runs start</c>) still leaves it.</item>
/// <item>Linux and Mac: posix_spawn into a process group of its own, which is killed on cancel and when the command ends.
/// TODO(unix): untested; and if uak itself is killed the group runs on (Linux could use PR_SET_PDEATHSIG, which needs a
/// hook between fork and exec; the next holder waits for the recorded command to end, see <see cref="LockHolderInfo.CommandPid"/>).</item>
/// </list>
/// Not Core's ProcessRunner: that one pipes the child's output through uak and closes its standard input, while a locked
/// command keeps the console as it is.
/// </summary>
internal static class LockedCommand
{
	/// <summary>How long to wait for the tree's last processes to go after the job or group is killed.</summary>
	private static readonly TimeSpan s_treeExitTimeout = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Starts the command, reports its process and the name of its job (null when it has no named job) to
	/// <paramref name="onStarted"/> before it runs, and waits for it. Returns its exit code, after stopping anything it left
	/// running in its tree. Cancelling kills the whole tree, waits for it to go, and throws <see cref="OperationCanceledException"/>.
	/// </summary>
	/// <param name="command">The command and its arguments.</param>
	/// <param name="jobName">
	/// On Windows, the name to give the command's job object (<see cref="CommandJobs.NameFor"/>), so the next holder can wait
	/// for the tree if this process dies; null for an unnamed job. A name already taken falls back to an unnamed job.
	/// </param>
	/// <param name="onStarted">Gets the command's process and its job's name, before the command runs.</param>
	/// <param name="logger">For warnings.</param>
	/// <param name="cancellationToken">Kills the tree.</param>
	/// <exception cref="Win32Exception">The command cannot be started.</exception>
	public static Task<int> RunAsync(IReadOnlyList<string> command, string? jobName, Action<ProcessIdentity, string?> onStarted, ILogger logger, CancellationToken cancellationToken)
	{
		ProcessStartInfo startInfo = CommandProcess.CreateStartInfo(command, workingDirectory: null);
		if (OperatingSystem.IsWindows())
		{
			return RunWindowsAsync(startInfo, jobName, onStarted, logger, cancellationToken);
		}
		// TODO(unix): a process group has no name another process can wait on; the next holder waits for the recorded PID only.
		return RunUnixAsync(startInfo, process => onStarted(process, null), logger, cancellationToken);
	}

	[SupportedOSPlatform("windows")]
	private static async Task<int> RunWindowsAsync(ProcessStartInfo startInfo, string? jobName, Action<ProcessIdentity, string?> onStarted, ILogger logger, CancellationToken cancellationToken)
	{
		// As Process.Start builds it: the program quoted, then the arguments (already quoted by CommandProcess).
		StringBuilder commandLine = new($"\"{startInfo.FileName}\"");
		if (!string.IsNullOrEmpty(startInfo.Arguments))
		{
			commandLine.Append(' ').Append(startInfo.Arguments);
		}
		using KillOnCloseJob job = KillOnCloseJob.Create(jobName, logger);
		WindowsNative.STARTUPINFOW startup = new() { cb = Marshal.SizeOf<WindowsNative.STARTUPINFOW>() };
		// Inherit handles and no STARTF_USESTDHANDLES, as Process.Start does without redirection: the command gets this
		// process's standard streams and environment (which carries UAK_LOCK_HELD).
		if (!WindowsNative.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, WindowsNative.CREATE_SUSPENDED | WindowsNative.CREATE_UNICODE_ENVIRONMENT,
			IntPtr.Zero, null, ref startup, out WindowsNative.PROCESS_INFORMATION info))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
		using SafeProcessHandle processHandle = new(info.hProcess, ownsHandle: true);
		Process process;
		bool inJob;
		try
		{
			inJob = job.TryAssign(info.hProcess, out int assignError);
			if (!inJob)
			{
				logger.LogWarning("Cannot put the command in a job ({Message}): if uak is killed, the command may run on without the lock.", new Win32Exception(assignError).Message);
			}
			process = Process.GetProcessById(info.dwProcessId);
			onStarted(ProcessIdentity.TryGet(info.dwProcessId) ?? new ProcessIdentity(info.dwProcessId, null), inJob ? job.Name : null);
			if (WindowsNative.ResumeThread(info.hThread) == uint.MaxValue)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
		}
		catch
		{
			WindowsNative.TerminateProcess(info.hProcess, 1);
			throw;
		}
		finally
		{
			WindowsNative.CloseHandle(info.hThread);
		}

		using (process)
		{
			try
			{
				await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Never let the command, or anything it started, run on without the lock.
				if (inJob)
				{
					job.Terminate();
				}
				else
				{
					process.Kill(entireProcessTree: true);
				}
				await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
				await job.WaitUntilEmptyAsync(s_treeExitTimeout).ConfigureAwait(false);
				throw;
			}
			int exitCode = process.ExitCode;
			int left = job.ActiveProcesses;
			if (left > 0)
			{
				logger.LogInformation("The command left {Count} process(es) running: stopping them before the lock is released.", left);
				job.Terminate();
				await job.WaitUntilEmptyAsync(s_treeExitTimeout).ConfigureAwait(false);
			}
			return exitCode;
		}
	}

	[UnsupportedOSPlatform("windows")]
	private static async Task<int> RunUnixAsync(ProcessStartInfo startInfo, Action<ProcessIdentity> onStarted, ILogger logger, CancellationToken cancellationToken)
	{
		// TODO(unix): untested; test on Linux and Mac.
		int pid = UnixNative.SpawnInNewGroup(startInfo.FileName, [startInfo.FileName, .. startInfo.ArgumentList]);
		onStarted(ProcessIdentity.TryGet(pid) ?? new ProcessIdentity(pid, null));
		Task<int> wait = Task.Factory.StartNew(() => UnixNative.WaitForExit(pid), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		try
		{
			await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			UnixNative.KillGroup(pid);
			await wait.ConfigureAwait(false);
			throw;
		}
		// Anything the command left running in its group ends with the hold.
		if (UnixNative.KillGroup(pid))
		{
			logger.LogInformation("Stopped what the command left running in its process group before releasing the lock.");
		}
		return await wait.ConfigureAwait(false);
	}

	/// <summary>A Windows job object that kills its processes when its last handle closes, and lets them break away.</summary>
	[SupportedOSPlatform("windows")]
	private sealed class KillOnCloseJob : IDisposable
	{
		private readonly SafeFileHandle _handle;

		private KillOnCloseJob(SafeFileHandle handle, string? name)
		{
			_handle = handle;
			Name = name;
		}

		/// <summary>The job's name, or null for an unnamed job.</summary>
		public string? Name { get; }

		/// <summary>Creates the job, named <paramref name="name"/> when given and free; else unnamed, with a debug message.</summary>
		public static KillOnCloseJob Create(string? name, ILogger logger)
		{
			SafeFileHandle handle;
			if (name is not null)
			{
				handle = WindowsNative.CreateJobObjectW(IntPtr.Zero, name);
				int error = Marshal.GetLastWin32Error();
				if (!handle.IsInvalid && error != WindowsNative.ERROR_ALREADY_EXISTS)
				{
					return Configure(handle, name);
				}
				// Taken (CreateJobObject then opens the existing job, which is not ours), or refused: no name.
				handle.Dispose();
				logger.LogDebug("Cannot create the job {Name} ({Message}): the command's job has no name.", name, new Win32Exception(error).Message);
			}
			handle = WindowsNative.CreateJobObjectW(IntPtr.Zero, null);
			return Configure(handle, null);
		}

		private static KillOnCloseJob Configure(SafeFileHandle handle, string? name)
		{
			if (handle.IsInvalid)
			{
				int error = Marshal.GetLastWin32Error();
				handle.Dispose();
				throw new Win32Exception(error);
			}
			WindowsNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new();
			limits.BasicLimitInformation.LimitFlags = WindowsNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | WindowsNative.JOB_OBJECT_LIMIT_BREAKAWAY_OK;
			if (!WindowsNative.SetInformationJobObject(handle, WindowsNative.JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<WindowsNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
			{
				int error = Marshal.GetLastWin32Error();
				handle.Dispose();
				throw new Win32Exception(error);
			}
			return new KillOnCloseJob(handle, name);
		}

		public bool TryAssign(IntPtr process, out int error)
		{
			bool assigned = WindowsNative.AssignProcessToJobObject(_handle, process);
			error = assigned ? 0 : Marshal.GetLastWin32Error();
			return assigned;
		}

		/// <summary>The processes in the job now.</summary>
		public int ActiveProcesses => WindowsNative.GetActiveProcesses(_handle);

		/// <summary>Kills every process in the job.</summary>
		public void Terminate()
		{
			WindowsNative.TerminateJobObject(_handle, 1);
		}

		public async Task WaitUntilEmptyAsync(TimeSpan timeout)
		{
			DateTime deadline = DateTime.UtcNow + timeout;
			while (ActiveProcesses > 0 && DateTime.UtcNow < deadline)
			{
				await Task.Delay(20).ConfigureAwait(false);
			}
		}

		/// <summary>Closes the job, which kills anything still in it.</summary>
		public void Dispose()
		{
			_handle.Dispose();
		}
	}

	[SupportedOSPlatform("windows")]
	internal static class WindowsNative
	{
		public const int ERROR_ALREADY_EXISTS = 183;
		public const uint JOB_OBJECT_QUERY = 0x0004;
		public const uint CREATE_SUSPENDED = 0x00000004;
		public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
		public const int JobObjectBasicAccountingInformation = 1;
		public const int JobObjectExtendedLimitInformation = 9;
		public const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
		public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

		[StructLayout(LayoutKind.Sequential)]
		public struct STARTUPINFOW
		{
			public int cb;
			public IntPtr lpReserved;
			public IntPtr lpDesktop;
			public IntPtr lpTitle;
			public int dwX;
			public int dwY;
			public int dwXSize;
			public int dwYSize;
			public int dwXCountChars;
			public int dwYCountChars;
			public int dwFillAttribute;
			public int dwFlags;
			public short wShowWindow;
			public short cbReserved2;
			public IntPtr lpReserved2;
			public IntPtr hStdInput;
			public IntPtr hStdOutput;
			public IntPtr hStdError;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct PROCESS_INFORMATION
		{
			public IntPtr hProcess;
			public IntPtr hThread;
			public int dwProcessId;
			public int dwThreadId;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
		{
			public long PerProcessUserTimeLimit;
			public long PerJobUserTimeLimit;
			public uint LimitFlags;
			public UIntPtr MinimumWorkingSetSize;
			public UIntPtr MaximumWorkingSetSize;
			public uint ActiveProcessLimit;
			public UIntPtr Affinity;
			public uint PriorityClass;
			public uint SchedulingClass;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct IO_COUNTERS
		{
			public ulong ReadOperationCount;
			public ulong WriteOperationCount;
			public ulong OtherOperationCount;
			public ulong ReadTransferCount;
			public ulong WriteTransferCount;
			public ulong OtherTransferCount;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
		{
			public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
			public IO_COUNTERS IoInfo;
			public UIntPtr ProcessMemoryLimit;
			public UIntPtr JobMemoryLimit;
			public UIntPtr PeakProcessMemoryUsed;
			public UIntPtr PeakJobMemoryUsed;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
		{
			public long TotalUserTime;
			public long TotalKernelTime;
			public long ThisPeriodTotalUserTime;
			public long ThisPeriodTotalKernelTime;
			public uint TotalPageFaultCount;
			public uint TotalProcesses;
			public uint ActiveProcesses;
			public uint TotalTerminatedProcesses;
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
			[MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
			ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern uint ResumeThread(IntPtr hThread);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool CloseHandle(IntPtr hObject);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern SafeFileHandle OpenJobObjectW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

		/// <summary>The processes in a job now (0 when it cannot be asked).</summary>
		public static int GetActiveProcesses(SafeFileHandle job) =>
			QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting,
				Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
				? (int)accounting.ActiveProcesses
				: 0;

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool SetInformationJobObject(SafeFileHandle hJob, int JobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool QueryInformationJobObject(SafeFileHandle hJob, int JobObjectInfoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION lpJobObjectInfo,
			int cbJobObjectInfoLength, IntPtr lpReturnLength);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);
	}

	/// <summary>
	/// posix_spawn into a new process group, waitpid and kill. Only non-variadic functions. posix_spawnattr_t is opaque and its
	/// size differs between C libraries, so it lives in a generous zeroed buffer.
	/// TODO(unix): untested; test on Linux and Mac.
	/// </summary>
	[UnsupportedOSPlatform("windows")]
	private static class UnixNative
	{
		private const short POSIX_SPAWN_SETPGROUP = 0x02;
		private const int SIGKILL = 9;
		private const int EINTR = 4;
		private const int AttributesSize = 1024;

		/// <summary>Starts the program (searched in PATH) in a process group of its own, with this process's environment. Returns its PID.</summary>
		public static int SpawnInNewGroup(string file, IReadOnlyList<string> arguments)
		{
			string?[] argv = [.. arguments, null];
			string?[] environment = [.. Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Select(entry => $"{entry.Key}={entry.Value}"), null];
			IntPtr attributes = Marshal.AllocHGlobal(AttributesSize);
			try
			{
				Marshal.Copy(new byte[AttributesSize], 0, attributes, AttributesSize);
				Check(posix_spawnattr_init(attributes), "posix_spawnattr_init");
				try
				{
					Check(posix_spawnattr_setflags(attributes, POSIX_SPAWN_SETPGROUP), "posix_spawnattr_setflags");
					Check(posix_spawnattr_setpgroup(attributes, 0), "posix_spawnattr_setpgroup");
					Check(posix_spawnp(out int pid, file, IntPtr.Zero, attributes, argv, environment), $"Cannot start '{file}'");
					return pid;
				}
				finally
				{
					posix_spawnattr_destroy(attributes);
				}
			}
			finally
			{
				Marshal.FreeHGlobal(attributes);
			}
		}

		/// <summary>Waits for the process to exit and returns its exit code (128 + the signal for a killed one).</summary>
		public static int WaitForExit(int pid)
		{
			while (true)
			{
				if (waitpid(pid, out int status, 0) == pid)
				{
					int signal = status & 0x7f;
					return signal == 0 ? (status >> 8) & 0xff : 128 + signal;
				}
				int error = Marshal.GetLastPInvokeError();
				if (error != EINTR)
				{
					throw new Win32Exception(error, $"waitpid({pid}) failed.");
				}
			}
		}

		/// <summary>Kills every process left in the group. Returns whether any was.</summary>
		public static bool KillGroup(int processGroup)
		{
			return kill(-processGroup, SIGKILL) == 0;
		}

		private static void Check(int result, string what)
		{
			if (result != 0)
			{
				throw new Win32Exception(result, $"{what}: error {result}.");
			}
		}

		[DllImport("libc", SetLastError = true)]
		private static extern int posix_spawnattr_init(IntPtr attributes);

		[DllImport("libc", SetLastError = true)]
		private static extern int posix_spawnattr_destroy(IntPtr attributes);

		[DllImport("libc", SetLastError = true)]
		private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);

		[DllImport("libc", SetLastError = true)]
		private static extern int posix_spawnattr_setpgroup(IntPtr attributes, int processGroup);

		[DllImport("libc", SetLastError = true)]
		private static extern int posix_spawnp(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, IntPtr fileActions, IntPtr attributes,
			[MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string?[] argv,
			[MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string?[] environment);

		[DllImport("libc", SetLastError = true)]
		private static extern int waitpid(int pid, out int status, int options);

		[DllImport("libc", SetLastError = true)]
		private static extern int kill(int pid, int signal);
	}
}

/// <summary>
/// The named job objects that hold the command of <c>uak lock run</c> (Windows only). The job is named after the hold, and
/// holder.json records the name (<see cref="LockHolderInfo.CommandJob"/>), so if the holder dies, the next holder can open the
/// job and wait until the processes the dead holder's job is killing have gone.
/// </summary>
internal static class CommandJobs
{
	/// <summary>The job name for a hold: <c>Global\UnrealAgentKit_EditorLock_Job_&lt;HoldId&gt;</c>, in the global namespace as the mutex is.</summary>
	public static string NameFor(string holdId)
	{
		return $@"Global\{EditorLock.MutexBaseName}_Job_{holdId}";
	}

	/// <summary>
	/// How many processes the named job holds now; 0 when the job no longer exists (it goes with its last process and its
	/// last handle) or cannot be opened, and always 0 on Linux and Mac, which have no named jobs.
	/// </summary>
	public static int GetActiveProcesses(string name)
	{
		if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(name))
		{
			return 0;
		}
		using SafeFileHandle job = LockedCommand.WindowsNative.OpenJobObjectW(LockedCommand.WindowsNative.JOB_OBJECT_QUERY, false, name);
		return job.IsInvalid ? 0 : LockedCommand.WindowsNative.GetActiveProcesses(job);
	}
}
