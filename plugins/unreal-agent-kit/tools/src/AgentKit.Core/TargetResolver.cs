// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.RegularExpressions;

namespace AgentKit.Core;

/// <summary>
/// Finds a project's UBT targets from its Source/*.Target.cs files. It reads the files as text, so it sees only what a
/// constructor sets in plain sight: <see cref="EditorLocator"/> trusts UBT's receipt over it.
/// </summary>
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
			.Where(File => EditorTypePattern().IsMatch(StripComments(System.IO.File.ReadAllText(File))))
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

	/// <summary>
	/// C# source with its // and /* */ comments blanked out, so a commented-out "Type = TargetType.Editor" doesn't count. String
	/// and character literals (regular, verbatim and raw) are kept as they are, so a "//" inside one is not taken for a comment.
	/// Line breaks are kept.
	/// </summary>
	internal static string StripComments(string source)
	{
		System.Text.StringBuilder Result = new(source.Length);
		int Index = 0;
		while (Index < source.Length)
		{
			char Current = source[Index];
			char Next = Index + 1 < source.Length ? source[Index + 1] : '\0';
			if (Current == '/' && Next == '/')
			{
				while (Index < source.Length && source[Index] != '\n')
				{
					Index++;
				}
			}
			else if (Current == '/' && Next == '*')
			{
				int End = source.IndexOf("*/", Index + 2, StringComparison.Ordinal);
				int Stop = End < 0 ? source.Length : End + 2;
				Result.Append(' ');
				for (; Index < Stop; Index++)
				{
					if (source[Index] == '\n')
					{
						Result.Append('\n');
					}
				}
			}
			else if (Current == '"' || Current == '\'' || (Current == '@' && Next == '"'))
			{
				int Start = Index;
				Index = SkipLiteral(source, Index);
				Result.Append(source, Start, Index - Start);
			}
			else
			{
				Result.Append(Current);
				Index++;
			}
		}
		return Result.ToString();
	}

	/// <summary>The index just past the string or character literal that starts at <paramref name="index"/>.</summary>
	static int SkipLiteral(string source, int index)
	{
		if (source[index] == '@')
		{
			// Verbatim: "" is a quote, and there are no other escapes.
			for (int Index = index + 2; Index < source.Length; Index++)
			{
				if (source[Index] == '"')
				{
					if (Index + 1 < source.Length && source[Index + 1] == '"')
					{
						Index++;
						continue;
					}
					return Index + 1;
				}
			}
			return source.Length;
		}
		char Quote = source[index];
		int Quotes = 1;
		while (Quote == '"' && index + Quotes < source.Length && source[index + Quotes] == '"')
		{
			Quotes++;
		}
		if (Quotes >= 3)
		{
			// Raw: ends at the same number of quotes.
			int End = source.IndexOf(new string('"', Quotes), index + Quotes, StringComparison.Ordinal);
			return End < 0 ? source.Length : End + Quotes;
		}
		if (Quotes == 2)
		{
			// "": an empty string.
			return index + 2;
		}
		for (int Index = index + 1; Index < source.Length; Index++)
		{
			if (source[Index] == '\\')
			{
				Index++;
			}
			else if (source[Index] == Quote || source[Index] == '\n')
			{
				return Index + 1;
			}
		}
		return source.Length;
	}

	[GeneratedRegex(@"\bType\s*=\s*TargetType\.Editor\b")]
	private static partial Regex EditorTypePattern();
}
