// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentKit.Core;

/// <summary>
/// A readable, resolved spelling of a path (<see cref="Get"/>), and an identity for an existing file or directory
/// (<see cref="TryGetIdentity"/>).
/// <para>
/// <see cref="Get"/> resolves what the local file system resolves: a junction, a symbolic link or a subst drive gives its
/// target's drive-letter path, and a mapped drive gives its UNC path. It does not make every spelling of one place equal: a
/// share that reaches a local folder (<c>\\localhost\C$\x</c>, or this machine's own share by name) keeps its UNC spelling,
/// and so does a folder reached through two different shares. The state directory is derived from it, and it is what uak
/// shows (in holder.json and <c>uak lock status</c>).
/// </para>
/// <list type="bullet">
/// <item>Windows: the path is opened (FILE_FLAG_BACKUP_SEMANTICS, so directories open too) and GetFinalPathNameByHandle gives
/// the final path, with the on-disk case. "\\?\C:\x" becomes "C:\x", and "\\?\UNC\server\share\x" becomes
/// "\\server\share\x", so a mapped drive and its UNC path agree.</item>
/// <item>Linux and Mac: realpath(3). TODO(unix): untested; test on Linux and Mac.</item>
/// </list>
/// A path that does not exist yet keeps its missing part as given, under its nearest existing ancestor's canonical path.
/// When the operating system cannot resolve a path, the full path (<see cref="Path.GetFullPath(string)"/>) is used.
/// <para>
/// <see cref="TryGetIdentity"/> names the file or directory itself, whatever path reaches it, the loopback share included.
/// The editor lock's mutex is keyed on it.
/// </para>
/// </summary>
public static class CanonicalPath
{
	/// <summary>
	/// The identity of an existing file or directory, the same through every path that reaches it (a junction, a link, a subst
	/// or mapped drive, or a share of this machine's own folder, such as <c>\\localhost\C$\x</c>). Null when it does not exist
	/// or the system cannot tell.
	/// <list type="bullet">
	/// <item>Windows: "win:&lt;volume serial&gt;:&lt;file ID&gt;", from GetFileInformationByHandleEx(FileIdInfo): the 64-bit volume
	/// serial number and the 128-bit file ID, in hex.</item>
	/// <item>Linux and Mac: "unix:&lt;st_dev&gt;:&lt;st_ino&gt;", from stat(2). TODO(unix): untested; test on Linux and Mac.</item>
	/// </list>
	/// A file ID is stable while the file exists, so a folder deleted and made again has a new identity.
	/// </summary>
	/// <param name="path">A file or directory, absolute or relative to the current directory.</param>
	public static string? TryGetIdentity(string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		string full = Path.GetFullPath(path);
		try
		{
			if (OperatingSystem.IsWindows())
			{
				return GetIdentityWindows(full);
			}
			return GetIdentityUnix(full);
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
		{
			return null;
		}
	}

	[SupportedOSPlatform("windows")]
	private static string? GetIdentityWindows(string fullPath)
	{
		using SafeFileHandle handle = WindowsNative.CreateFileW(fullPath, 0, WindowsNative.FILE_SHARE_ALL, IntPtr.Zero, WindowsNative.OPEN_EXISTING,
			WindowsNative.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			return null;
		}
		// FILE_ID_INFO: ULONGLONG VolumeSerialNumber, then FILE_ID_128 FileId (16 bytes).
		byte[] info = new byte[24];
		if (!WindowsNative.GetFileInformationByHandleEx(handle, WindowsNative.FileIdInfo, info, (uint)info.Length))
		{
			return null;
		}
		ulong volume = BitConverter.ToUInt64(info, 0);
		if (volume == 0 && info.AsSpan(8).IndexOfAnyExcept((byte)0) < 0)
		{
			// Nothing filled in: a file system that cannot tell.
			return null;
		}
		return $"win:{volume:x16}:{Convert.ToHexStringLower(info, 8, 16)}";
	}

	[UnsupportedOSPlatform("windows")]
	private static string? GetIdentityUnix(string fullPath)
	{
		// TODO(unix): untested; test on Linux and Mac. struct stat starts with st_dev (8 bytes on Linux, 4 on Mac), and has st_ino
		// (8 bytes) at offset 8 on both 64-bit Linux and 64-bit-inode Mac.
		byte[] buffer = new byte[512];
		int result;
		if (OperatingSystem.IsMacOS())
		{
			result = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == Architecture.X64
				? UnixNative.stat_inode64(fullPath, buffer)
				: UnixNative.stat(fullPath, buffer);
		}
		else
		{
			try
			{
				result = UnixNative.stat(fullPath, buffer);
			}
			catch (EntryPointNotFoundException)
			{
				// glibc before 2.33 exports stat only as __xstat (version 1 is the 64-bit layout).
				result = UnixNative.__xstat(1, fullPath, buffer);
			}
		}
		if (result != 0)
		{
			return null;
		}
		ulong device = OperatingSystem.IsMacOS() ? BitConverter.ToUInt32(buffer, 0) : BitConverter.ToUInt64(buffer, 0);
		ulong inode = BitConverter.ToUInt64(buffer, 8);
		return $"unix:{device:x}:{inode:x}";
	}

	/// <summary>The canonical form of a path: absolute, links resolved, with no trailing separator (except for a root).</summary>
	/// <param name="path">A file or directory, absolute or relative to the current directory. It need not exist.</param>
	public static string Get(string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
		string? resolved = TryResolve(full);
		if (resolved is not null)
		{
			return Path.TrimEndingDirectorySeparator(resolved);
		}
		// Missing (or not resolvable): resolve the parent and keep this name as given.
		string? parent = Path.GetDirectoryName(full);
		if (string.IsNullOrEmpty(parent))
		{
			return full;
		}
		return Path.Combine(Get(parent), Path.GetFileName(full));
	}

	/// <summary>Whether two paths name the same file or directory, once both are canonical. Case is ignored on Windows.</summary>
	public static bool AreSame(string first, string second)
	{
		return string.Equals(Get(first), Get(second), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	/// <summary>The resolved path of an existing file or directory, or null when it does not exist or cannot be resolved.</summary>
	private static string? TryResolve(string fullPath)
	{
		try
		{
			if (OperatingSystem.IsWindows())
			{
				return ResolveWindows(fullPath);
			}
			return ResolveUnix(fullPath);
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>Strips the "\\?\" prefix GetFinalPathNameByHandle adds: "\\?\UNC\server\share" becomes "\\server\share".</summary>
	internal static string StripWindowsPrefix(string path)
	{
		const string UncPrefix = @"\\?\UNC\";
		const string LocalPrefix = @"\\?\";
		if (path.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return @"\\" + path[UncPrefix.Length..];
		}
		if (path.StartsWith(LocalPrefix, StringComparison.Ordinal))
		{
			return path[LocalPrefix.Length..];
		}
		return path;
	}

	[SupportedOSPlatform("windows")]
	private static string? ResolveWindows(string fullPath)
	{
		// No access rights are needed to ask for the final path; share everything, so this never disturbs another process.
		using SafeFileHandle handle = WindowsNative.CreateFileW(fullPath, 0, WindowsNative.FILE_SHARE_ALL, IntPtr.Zero, WindowsNative.OPEN_EXISTING,
			WindowsNative.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			return null;
		}
		char[] buffer = new char[512];
		while (true)
		{
			// FILE_NAME_NORMALIZED | VOLUME_NAME_DOS: a drive-letter (or UNC) path, with junctions and links resolved.
			uint length = WindowsNative.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
			if (length == 0)
			{
				return null;
			}
			if (length < buffer.Length)
			{
				return StripWindowsPrefix(new string(buffer, 0, (int)length));
			}
			// Too small: length is the size needed, including the terminating null.
			buffer = new char[length];
		}
	}

	[UnsupportedOSPlatform("windows")]
	private static string? ResolveUnix(string fullPath)
	{
		// TODO(unix): untested; test on Linux and Mac.
		IntPtr resolved = UnixNative.realpath(fullPath, IntPtr.Zero);
		if (resolved == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			return Marshal.PtrToStringUTF8(resolved);
		}
		finally
		{
			UnixNative.free(resolved);
		}
	}

	[SupportedOSPlatform("windows")]
	private static class WindowsNative
	{
		public const uint FILE_SHARE_ALL = 0x00000007;
		public const uint OPEN_EXISTING = 3;
		public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
			uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

		/// <summary>FILE_INFO_BY_HANDLE_CLASS.FileIdInfo.</summary>
		public const int FileIdInfo = 18;

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int FileInformationClass, [Out] byte[] lpFileInformation, uint dwBufferSize);
	}

	[UnsupportedOSPlatform("windows")]
	private static class UnixNative
	{
		[DllImport("libc", EntryPoint = "stat", SetLastError = true)]
		public static extern int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

		[DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
		public static extern int stat_inode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

		[DllImport("libc", SetLastError = true)]
		public static extern int __xstat(int version, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

		[DllImport("libc", SetLastError = true)]
		public static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolvedPath);

		[DllImport("libc")]
		public static extern void free(IntPtr pointer);
	}
}
