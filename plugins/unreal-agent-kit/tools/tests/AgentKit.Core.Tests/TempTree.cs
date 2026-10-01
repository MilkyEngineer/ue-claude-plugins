// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core.Tests;

/// <summary>A temporary directory tree for one test, deleted on dispose, with helpers to lay out fake engines and projects.</summary>
internal sealed class TempTree : IDisposable
{
	public TempTree()
	{
		Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uak-core-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Root);
	}

	/// <summary>The tree's root directory.</summary>
	public string Root { get; }

	/// <summary>A path under the root.</summary>
	public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

	/// <summary>Creates a directory under the root and returns its path.</summary>
	public string Dir(params string[] parts)
	{
		string Result = Path(parts);
		Directory.CreateDirectory(Result);
		return Result;
	}

	/// <summary>Writes a file under the root (creating its directory) and returns its path.</summary>
	public string File(string relativePath, string content = "")
	{
		string Result = Path(relativePath);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Result)!);
		System.IO.File.WriteAllText(Result, content);
		return Result;
	}

	/// <summary>Lays out a minimal engine root (Engine/Build/Build.version) and returns the root's path.</summary>
	public string Engine(string relativeRoot, int minor = 8)
	{
		File(System.IO.Path.Combine(relativeRoot, "Engine", "Build", "Build.version"),
			$$"""{ "MajorVersion": 5, "MinorVersion": {{minor}}, "PatchVersion": 1, "Changelist": 123, "BranchName": "++UE5+Release-5.{{minor}}" }""");
		return System.IO.Path.GetFullPath(Path(relativeRoot));
	}

	/// <summary>Writes a .uproject with the given EngineAssociation and returns its path.</summary>
	public string Project(string relativeFile, string association = "5.8") => System.IO.Path.GetFullPath(
		File(relativeFile, $$"""{ "FileVersion": 3, "EngineAssociation": "{{association.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "Modules": [] }"""));

	public void Dispose()
	{
		try
		{
			Directory.Delete(Root, recursive: true);
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			// A file still open on a slow machine; the temp folder is cleaned eventually.
		}
	}
}

/// <summary>Environment variables for a test, instead of the process's.</summary>
internal sealed class FakeEnvironment : Dictionary<string, string>
{
	public FakeEnvironment() : base(StringComparer.OrdinalIgnoreCase)
	{
	}

	public string? Get(string name) => TryGetValue(name, out string? Value) ? Value : null;
}

/// <summary>An association source that answers from a list, for tests.</summary>
internal sealed class FakeAssociationSource(params (string Association, string Root)[] entries) : IEngineAssociationSource
{
	public string Description => "the fake association list";

	public IEnumerable<EngineAssociationMatch> Find(string association) => entries
		.Where(Entry => Entry.Association == association)
		.Select(Entry => new EngineAssociationMatch(Entry.Root, "fake:" + Entry.Association));
}

/// <summary>A registry in memory, for tests.</summary>
internal sealed class FakeRegistry : IRegistryReader
{
	readonly Dictionary<(RegistryRoot, string), Dictionary<string, string>> Keys = [];

	public FakeRegistry Set(RegistryRoot root, string key, string name, string value)
	{
		if (!Keys.TryGetValue((root, key.ToLowerInvariant()), out Dictionary<string, string>? Values))
		{
			Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Keys[(root, key.ToLowerInvariant())] = Values;
		}
		Values[name] = value;
		return this;
	}

	public IReadOnlyList<string> GetValueNames(RegistryRoot root, string key) =>
		Keys.TryGetValue((root, key.ToLowerInvariant()), out Dictionary<string, string>? Values) ? [.. Values.Keys] : [];

	public string? GetString(RegistryRoot root, string key, string valueName) =>
		Keys.TryGetValue((root, key.ToLowerInvariant()), out Dictionary<string, string>? Values) && Values.TryGetValue(valueName, out string? Value) ? Value : null;
}
