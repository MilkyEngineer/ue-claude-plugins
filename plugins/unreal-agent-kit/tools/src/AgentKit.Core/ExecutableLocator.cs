// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core;

/// <summary>
/// Finds a program by name on PATH, and nowhere else.
/// <para>
/// Windows' CreateProcess looks for a bare name ("git") in the application's folder and the current directory before PATH,
/// and .NET's Process does the same on Linux and Mac. A repository could then plant a git.exe or p4.exe that uak runs.
/// So every bare name is resolved here to an absolute path first: only absolute PATH entries are searched (empty, "." and
/// relative entries are skipped), and on Windows the extensions in PATHEXT are tried.
/// </para>
/// </summary>
public static class ExecutableLocator
{
	/// <summary>The extensions tried on Windows when PATHEXT is unset.</summary>
	public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

	/// <summary>
	/// Whether <paramref name="fileName"/> is a bare program name (no directory part), which a process start would search for.
	/// A path such as "tools/x" or "C:\x.exe" is not.
	/// </summary>
	public static bool IsBareName(string fileName) =>
		fileName.Length > 0 && !Path.IsPathRooted(fileName) && fileName.IndexOfAny(OperatingSystem.IsWindows() ? ['/', '\\', ':'] : ['/']) < 0;

	/// <summary>
	/// The absolute path of the program <paramref name="name"/> found on PATH, or null when it is not there.
	/// </summary>
	/// <param name="name">A bare program name, such as "git" or "git.exe".</param>
	/// <param name="pathVariable">The PATH to search; null for this process's PATH.</param>
	/// <param name="pathExtVariable">PATHEXT (Windows only); null for this process's PATHEXT, else <see cref="DefaultPathExt"/>.</param>
	public static string? FindOnPath(string name, string? pathVariable = null, string? pathExtVariable = null)
	{
		if (!IsBareName(name))
		{
			throw new ArgumentException($"\"{name}\" is not a bare program name.", nameof(name));
		}
		pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? "";
		bool Windows = OperatingSystem.IsWindows();
		List<string> Candidates = Windows ? GetWindowsCandidates(name, pathExtVariable ?? Environment.GetEnvironmentVariable("PATHEXT")) : [name];

		foreach (string RawEntry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			// Windows allows quoted entries ("C:\Program Files\x").
			string Entry = Windows ? RawEntry.Trim('"') : RawEntry;
			if (Entry.Length == 0 || Entry == "." || !Path.IsPathFullyQualified(Entry))
			{
				continue;
			}
			foreach (string Candidate in Candidates)
			{
				string Full;
				try
				{
					Full = Path.GetFullPath(Path.Combine(Entry, Candidate));
				}
				catch (Exception Error) when (Error is ArgumentException or NotSupportedException or PathTooLongException)
				{
					continue;
				}
				if (IsExecutableFile(Full))
				{
					return Full;
				}
			}
		}
		return null;
	}

	/// <summary>
	/// <paramref name="fileName"/> made safe to start: a bare name becomes its absolute path on PATH (see <see cref="FindOnPath"/>);
	/// any other path is returned unchanged. Null when a bare name is not on PATH.
	/// </summary>
	/// <param name="fileName">The program, as a caller gave it.</param>
	/// <param name="environment">Overrides of the child's environment; a PATH or PATHEXT there is searched instead of this process's.</param>
	public static string? Resolve(string fileName, IReadOnlyDictionary<string, string?>? environment = null)
	{
		if (!IsBareName(fileName))
		{
			return fileName;
		}
		string? PathValue = null, PathExtValue = null;
		if (environment is not null)
		{
			foreach ((string Key, string? Value) in environment)
			{
				if (ProcessInvocation.EnvironmentComparer.Equals(Key, "PATH"))
				{
					PathValue = Value ?? "";
				}
				else if (ProcessInvocation.EnvironmentComparer.Equals(Key, "PATHEXT"))
				{
					PathExtValue = Value;
				}
			}
		}
		return FindOnPath(fileName, PathValue, PathExtValue);
	}

	/// <summary>On Windows: the name itself when it already ends in a PATHEXT extension, else the name with each extension.</summary>
	static List<string> GetWindowsCandidates(string name, string? pathExt)
	{
		string[] Extensions = (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt)
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(Extension => Extension.StartsWith('.'))
			.ToArray();
		string Extension = Path.GetExtension(name);
		if (Extension.Length > 0 && Extensions.Contains(Extension, StringComparer.OrdinalIgnoreCase))
		{
			return [name];
		}
		return [.. Extensions.Select(Extension => name + Extension)];
	}

	static bool IsExecutableFile(string path)
	{
		if (!File.Exists(path))
		{
			return false;
		}
		if (OperatingSystem.IsWindows())
		{
			return true;
		}
		try
		{
			return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}
}
