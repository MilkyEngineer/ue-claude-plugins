// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentKit.Runs.Tests;

/// <summary>
/// A Windows job object that kills its processes when closed, as Claude Code's shells run in: the test puts a run's starter
/// in one and closes it, as a shell ending does.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class JobObject : IDisposable
{
	private const int JobObjectExtendedLimitInformation = 9;
	private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
	private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

	private IntPtr _handle;

	public JobObject(bool allowBreakaway)
	{
		_handle = CreateJobObjectW(IntPtr.Zero, null);
		if (_handle == IntPtr.Zero)
		{
			throw new Win32Exception();
		}
		JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new();
		limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | (allowBreakaway ? JOB_OBJECT_LIMIT_BREAKAWAY_OK : 0);
		if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
		{
			throw new Win32Exception();
		}
	}

	/// <summary>Puts a running process in the job.</summary>
	public void Assign(System.Diagnostics.Process process)
	{
		if (!AssignProcessToJobObject(_handle, process.Handle))
		{
			throw new Win32Exception();
		}
	}

	/// <summary>Closes the job, which kills every process still in it.</summary>
	public void Dispose()
	{
		if (_handle != IntPtr.Zero)
		{
			CloseHandle(_handle);
			_handle = IntPtr.Zero;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
	private struct IO_COUNTERS
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public UIntPtr ProcessMemoryLimit;
		public UIntPtr JobMemoryLimit;
		public UIntPtr PeakProcessMemoryUsed;
		public UIntPtr PeakJobMemoryUsed;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr hObject);
}
