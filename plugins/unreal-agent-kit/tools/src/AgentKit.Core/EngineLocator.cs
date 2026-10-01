// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core;

/// <summary>Recognises engine directories (DESIGN.md, "Resolving the engine and project").</summary>
public static class EngineLocator
{
	/// <summary>
	/// The engine root for a path given as the root or as its "Engine" folder: the directory that contains
	/// Engine/Build/Build.version. Returns null when the path is neither.
	/// </summary>
	public static string? NormalizeEngineRoot(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		string Full = TrimSeparators(Path.GetFullPath(path));
		if (IsEngineRoot(Full))
		{
			return Full;
		}
		// Given the Engine folder: its parent is the root.
		if (File.Exists(Path.Combine(Full, "Build", "Build.version")))
		{
			string? Parent = Path.GetDirectoryName(Full);
			if (Parent is not null && IsEngineRoot(Parent))
			{
				return Parent;
			}
		}
		return null;
	}

	/// <summary>Whether the directory is an engine root: it holds Engine/Build/Build.version.</summary>
	public static bool IsEngineRoot(string directory) => File.Exists(Path.Combine(directory, "Engine", "Build", "Build.version"));

	/// <summary>
	/// The engine root that contains the directory, as in a source build with the project inside it: the nearest ancestor
	/// (or the directory itself) that is an engine root. Returns null when there is none.
	/// </summary>
	public static string? FindContainingEngineRoot(string directory)
	{
		for (DirectoryInfo? Current = new(Path.GetFullPath(directory)); Current is not null; Current = Current.Parent)
		{
			if (IsEngineRoot(Current.FullName))
			{
				return TrimSeparators(Current.FullName);
			}
		}
		return null;
	}

	/// <summary>Removes trailing directory separators, except from a drive or file-system root.</summary>
	internal static string TrimSeparators(string path)
	{
		string Trimmed = Path.TrimEndingDirectorySeparator(path);
		return Trimmed.Length == 0 ? path : Trimmed;
	}
}
