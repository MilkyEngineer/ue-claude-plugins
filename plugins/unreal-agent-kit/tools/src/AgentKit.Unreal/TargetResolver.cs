// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.RegularExpressions;
using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>Finds a project's UBT targets from its Source/*.Target.cs files.</summary>
public static partial class TargetResolver
{
	/// <summary>
	/// The project's editor target: the Source/*.Target.cs whose rules set Type = TargetType.Editor. With several, the one
	/// named &lt;Project&gt;Editor wins, else it is an error naming them. A project without Source (content only) uses the
	/// engine's UnrealEditor target.
	/// </summary>
	/// <exception cref="UakSetupException">No editor target, or an ambiguous choice.</exception>
	public static string ResolveEditorTarget(string projectFile)
	{
		string ProjectName = Path.GetFileNameWithoutExtension(projectFile);
		string SourceDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectFile))!, "Source");
		if (!Directory.Exists(SourceDirectory))
		{
			return "UnrealEditor";
		}
		List<string> EditorTargets = Directory.EnumerateFiles(SourceDirectory, "*.Target.cs", SearchOption.TopDirectoryOnly)
			.Where(File => EditorTypePattern().IsMatch(System.IO.File.ReadAllText(File)))
			.Select(File => Path.GetFileName(File)[..^".Target.cs".Length])
			.OrderBy(Name => Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (EditorTargets.Count == 1)
		{
			return EditorTargets[0];
		}
		string? Preferred = EditorTargets.FirstOrDefault(Name => Name.Equals(ProjectName + "Editor", StringComparison.OrdinalIgnoreCase));
		if (Preferred is not null)
		{
			return Preferred;
		}
		if (EditorTargets.Count == 0)
		{
			throw new UakSetupException($"no editor target (Type = TargetType.Editor) in {SourceDirectory}; give -target=");
		}
		throw new UakSetupException($"several editor targets in {SourceDirectory} ({string.Join(", ", EditorTargets)}); give -target=");
	}

	[GeneratedRegex(@"\bType\s*=\s*TargetType\.Editor\b")]
	private static partial Regex EditorTypePattern();
}
