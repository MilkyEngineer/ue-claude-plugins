// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentKit.Runs;

/// <summary>The Win32 calls detached runs need: CreateProcess with detaching flags, and standard handles for the wrapper.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsNative
{
	public const uint DETACHED_PROCESS = 0x00000008;
	public const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
	public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
	public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
	public const int STARTF_USESTDHANDLES = 0x00000100;
	public const int ERROR_ACCESS_DENIED = 5;
	public const uint WAIT_OBJECT_0 = 0;
	public const uint STILL_ACTIVE = 259;

	public const int STD_INPUT_HANDLE = -10;
	public const int STD_OUTPUT_HANDLE = -11;
	public const int STD_ERROR_HANDLE = -12;

	public const uint GENERIC_READ = 0x80000000;
	public const uint FILE_APPEND_DATA = 0x00000004;
	public const uint SYNCHRONIZE = 0x00100000;
	public const uint FILE_SHARE_ALL = 0x00000007;
	public const uint OPEN_EXISTING = 3;
	public const uint OPEN_ALWAYS = 4;
	public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
	public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

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
	public struct SECURITY_ATTRIBUTES
	{
		public int nLength;
		public IntPtr lpSecurityDescriptor;
		public int bInheritHandle;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
		[MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
		ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool CloseHandle(IntPtr hObject);

	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
	public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SECURITY_ATTRIBUTES lpSecurityAttributes,
		uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

	/// <summary>Opens a handle that child processes inherit.</summary>
	public static IntPtr OpenInheritable(string path, uint access, uint disposition)
	{
		SECURITY_ATTRIBUTES attributes = new() { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
		return CreateFile(path, access, FILE_SHARE_ALL, ref attributes, disposition, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
	}

	/// <summary>
	/// A command line that .NET (and the C runtime) splits back into exactly these arguments: each quoted by the
	/// CommandLineToArgvW rules when needed (<see cref="AgentKit.Core.ProcessInvocation.QuoteWindows"/>).
	/// </summary>
	public static string JoinCommandLine(IEnumerable<string> arguments)
	{
		return string.Join(' ', arguments.Select(AgentKit.Core.ProcessInvocation.QuoteWindows));
	}
}

/// <summary>
/// The libc calls detached runs need on Linux and Mac. Only non-variadic functions (no open or fcntl), so the calls are
/// correct on every ABI, including Apple arm64.
/// TODO(unix): untested; test on Linux and Mac (DESIGN.md, "Detached runs").
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class UnixNative
{
	public const int SEEK_END = 2;

	[DllImport("libc", SetLastError = true)]
	public static extern int setsid();

	[DllImport("libc", SetLastError = true)]
	public static extern int dup2(int oldfd, int newfd);

	[DllImport("libc", SetLastError = true)]
	public static extern long lseek(int fd, long offset, int whence);

	[DllImport("libc", SetLastError = true)]
	public static extern nint write(int fd, byte[] buffer, nint count);
}
