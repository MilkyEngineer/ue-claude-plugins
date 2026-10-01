// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>A source file to check, with the module that owns it.</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="Module">The module's name (its *.Build.cs), or null when the file is in no module.</param>
/// <param name="OwnerDirectory">The project or plugin directory that holds the module (with its Intermediate folder), or null.</param>
public sealed record SourceFile(string Path, string? Module, string? OwnerDirectory)
{
	/// <summary>Whether the file is a header, which UBT compiles through a generated translation unit.</summary>
	public bool IsHeader => Path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase)
		|| Path.EndsWith(".inl", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The item names UBT's "[i/n] Compile" line shows when this file itself is compiled: "Foo.cpp" for a source file, and
	/// only the generated "Foo.h.cpp" (or its "Foo.h.obj") for a header. A dependent that includes the header does not count.
	/// </summary>
	public IEnumerable<string> ActionItems
	{
		get
		{
			string Name = System.IO.Path.GetFileName(Path);
			if (IsHeader)
			{
				yield return Name + ".cpp";
				yield return Name + ".obj";
			}
			else
			{
				yield return Name;
			}
		}
	}
}

/// <summary>Finds the files `uak compile` is given and the modules they belong to.</summary>
public static class SourceFiles
{
	static readonly string[] SkippedDirectories = ["Intermediate", "Binaries", "Saved", "DerivedDataCache", ".git", ".vs"];

	/// <summary>
	/// Resolves a file argument: a path (absolute, or relative to <paramref name="currentDirectory"/>), or a bare file name
	/// found exactly once under the project's Source and Plugins folders.
	/// </summary>
	/// <exception cref="UakUsageException">Not found, or a bare name found more than once.</exception>
	public static string Resolve(string argument, string currentDirectory, string? projectDirectory)
	{
		string Candidate = System.IO.Path.GetFullPath(argument, currentDirectory);
		if (File.Exists(Candidate))
		{
			return Candidate;
		}
		bool IsBareName = argument.IndexOfAny(['/', '\\']) < 0;
		if (!IsBareName || projectDirectory is null)
		{
			throw new UakUsageException("file not found: " + argument);
		}
		List<string> Matches = [];
		foreach (string Root in new[] { "Source", "Plugins" }.Select(Folder => System.IO.Path.Combine(projectDirectory, Folder)).Where(Directory.Exists))
		{
			FindByName(Root, argument, Matches);
		}
		return Matches.Count switch
		{
			1 => Matches[0],
			0 => throw new UakUsageException($"file not found: {argument} (not a path, and not under the project's Source or Plugins)"),
			_ => throw new UakUsageException($"{argument} is ambiguous: {string.Join(", ", Matches)}"),
		};
	}

	static void FindByName(string directory, string name, List<string> matches)
	{
		foreach (string File in Directory.EnumerateFiles(directory))
		{
			if (System.IO.Path.GetFileName(File).Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				matches.Add(File);
			}
		}
		foreach (string Child in Directory.EnumerateDirectories(directory))
		{
			if (!SkippedDirectories.Contains(System.IO.Path.GetFileName(Child), StringComparer.OrdinalIgnoreCase))
			{
				FindByName(Child, name, matches);
			}
		}
	}

	/// <summary>Finds the module of a file (the nearest folder above it with a *.Build.cs) and the project or plugin that holds it.</summary>
	public static SourceFile Describe(string path)
	{
		string Full = System.IO.Path.GetFullPath(path);
		string? Module = null;
		string? Directory = System.IO.Path.GetDirectoryName(Full);
		for (; Directory is not null; Directory = System.IO.Path.GetDirectoryName(Directory))
		{
			string? BuildFile = System.IO.Directory.EnumerateFiles(Directory, "*.Build.cs").FirstOrDefault();
			if (BuildFile is not null)
			{
				Module = System.IO.Path.GetFileName(BuildFile)[..^".Build.cs".Length];
				break;
			}
		}
		string? Owner = null;
		for (; Directory is not null; Directory = System.IO.Path.GetDirectoryName(Directory))
		{
			if (System.IO.Directory.EnumerateFiles(Directory, "*.uplugin").Any() || System.IO.Directory.EnumerateFiles(Directory, "*.uproject").Any())
			{
				Owner = Directory;
				break;
			}
		}
		return new SourceFile(Full, Module, Owner);
	}

	/// <summary>
	/// Deletes the object files an earlier `-SingleFile` check of this file left in its owner's Intermediate folder
	/// (…/&lt;Module&gt;/SingleFile/&lt;Name&gt;.obj or .o), so UBT compiles it again instead of calling it up to date.
	/// Only folders under <paramref name="writableRoot"/> are touched: never an engine directory.
	/// </summary>
	/// <returns>The files deleted.</returns>
	public static List<string> DeleteSingleFileOutputs(SourceFile file, string writableRoot)
	{
		List<string> Deleted = [];
		if (file.Module is null || file.OwnerDirectory is null || !IsUnder(file.OwnerDirectory, writableRoot))
		{
			return Deleted;
		}
		string Intermediate = System.IO.Path.Combine(file.OwnerDirectory, "Intermediate", "Build");
		if (!Directory.Exists(Intermediate))
		{
			return Deleted;
		}
		string Name = System.IO.Path.GetFileName(file.Path);
		foreach (string SingleFileDirectory in Directory.EnumerateDirectories(Intermediate, "SingleFile", SearchOption.AllDirectories))
		{
			if (!System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(SingleFileDirectory))!.Equals(file.Module, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			foreach (string Output in new[] { Name + ".obj", Name + ".o" }.Select(Output => System.IO.Path.Combine(SingleFileDirectory, Output)))
			{
				if (File.Exists(Output))
				{
					File.Delete(Output);
					Deleted.Add(Output);
				}
			}
		}
		return Deleted;
	}

	static bool IsUnder(string path, string root)
	{
		string Relative = System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(root), System.IO.Path.GetFullPath(path));
		return Relative == "." || !(Relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(Relative));
	}
}
