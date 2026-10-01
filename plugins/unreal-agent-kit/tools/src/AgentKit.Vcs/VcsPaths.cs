// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Vcs;

/// <summary>Path helpers shared by the implementations.</summary>
internal static class VcsPaths
{
	/// <summary>
	/// How file paths compare on this platform: case-insensitive on Windows and macOS (their default file systems), case-sensitive elsewhere.
	/// </summary>
	public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	/// <summary>The <see cref="StringComparison"/> matching <see cref="Comparer"/>.</summary>
	public static StringComparison Comparison { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	/// <summary>Removes any trailing separator, except from a file-system root.</summary>
	public static string TrimEnd(string path)
	{
		string root = Path.GetPathRoot(path) ?? string.Empty;
		return path.Length > root.Length ? Path.TrimEndingDirectorySeparator(path) : path;
	}

	/// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or lies under it. Both must be absolute.</summary>
	public static bool IsUnder(string path, string directory)
	{
		string fullPath = TrimEnd(Path.GetFullPath(path));
		string fullDirectory = TrimEnd(Path.GetFullPath(directory));
		if (fullPath.Equals(fullDirectory, Comparison))
		{
			return true;
		}
		string prefix = Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar;
		return fullPath.StartsWith(prefix, Comparison);
	}

	/// <summary>Joins a multi-line message (p4 errors span several indented lines) into one line.</summary>
	public static string OneLine(string text)
		=> string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

	/// <summary>Splits a list into batches, to keep command lines well under every platform's length limit.</summary>
	public static IEnumerable<List<string>> Batch(IEnumerable<string> items, int maxCount = 100, int maxChars = 16000)
	{
		List<string> batch = [];
		int chars = 0;
		foreach (string item in items)
		{
			if (batch.Count > 0 && (batch.Count >= maxCount || chars + item.Length + 3 > maxChars))
			{
				yield return batch;
				batch = [];
				chars = 0;
			}
			batch.Add(item);
			chars += item.Length + 3;
		}
		if (batch.Count > 0)
		{
			yield return batch;
		}
	}
}
