// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>
/// A test filter matched as "Automation RunTests &lt;filter&gt;" matches it, so uak can tell which expected tests a run
/// covers (AutomationCommandline.cpp, GenerateTestNamesFromCommandLine; FAutomatedTestFilterBase::PassesFilter in
/// AutomationControllerSettings.h). The filter is split on '+' (empty parts dropped) and each part trimmed; a test runs when
/// any part matches its full path:
/// <list type="bullet">
/// <item>"StartsWith:X": the path starts with X, with a '.' appended to X when it doesn't end with one (so
/// "StartsWith:A.B" covers A.B.C, never A.B itself or A.BC).</item>
/// <item>"^X": the path starts with X; "X$": it ends with X; "^X$": both (StartsWith and EndsWith, not equality).</item>
/// <item>Anything else: X anywhere in the path.</item>
/// </list>
/// Every comparison ignores case, as FString's StartsWith, EndsWith and Contains do by default. "Group:X" expands to the
/// filters of a group in the project's automation settings, which uak can't read, so <see cref="Parse"/> rejects it.
/// </summary>
public sealed class AutomationFilter
{
	readonly record struct Part(string Text, bool FromStart, bool FromEnd);

	readonly List<Part> Parts;

	AutomationFilter(string text, List<Part> parts)
	{
		Text = text;
		Parts = parts;
	}

	/// <summary>The filter as given.</summary>
	public string Text { get; }

	/// <summary>Parses a filter the way the engine does.</summary>
	/// <exception cref="UakUsageException">A part is a "Group:" filter.</exception>
	public static AutomationFilter Parse(string filter)
	{
		List<Part> Parts = [];
		foreach (string Raw in filter.Split('+', StringSplitOptions.RemoveEmptyEntries))
		{
			string Argument = Raw.Trim();
			if (Argument.StartsWith("StartsWith:", StringComparison.OrdinalIgnoreCase))
			{
				string Prefix = Argument["StartsWith:".Length..].TrimStart();
				Parts.Add(new Part(Prefix.EndsWith('.') ? Prefix : Prefix + ".", FromStart: true, FromEnd: false));
			}
			else if (Argument.StartsWith("Group:", StringComparison.OrdinalIgnoreCase))
			{
				throw new UakUsageException($"-filter part '{Argument}' names a group from the project's automation settings, which uak can't expand to"
					+ " match -expect against: give the group's filters instead");
			}
			else
			{
				bool FromStart = Argument.StartsWith('^');
				string Text = FromStart ? Argument[1..] : Argument;
				bool FromEnd = Text.EndsWith('$');
				Parts.Add(new Part(FromEnd ? Text[..^1] : Text, FromStart, FromEnd));
			}
		}
		return new AutomationFilter(filter, Parts);
	}

	/// <summary>Whether the engine would run the test with this full path.</summary>
	public bool Matches(string testPath) => Parts.Any(Part => Matches(Part, testPath));

	/// <summary>
	/// Warnings about how the filter matched a run's tests (<paramref name="testPaths"/>: every test the run found), for
	/// mistakes that still run some tests:
	/// <list type="bullet">
	/// <item>a part that matched none of them (only when <paramref name="complete"/>: the paths are all the tests found);</item>
	/// <item>a part without a trailing '.' that starts several names: "Game" runs Game.* and GameBench.* alike.</item>
	/// </list>
	/// </summary>
	public List<string> Warnings(IReadOnlyCollection<string> testPaths, bool complete)
	{
		List<string> Result = [];
		foreach (Part Part in Parts)
		{
			string Shown = (Part.FromStart ? "^" : "") + Part.Text + (Part.FromEnd ? "$" : "");
			List<string> Matched = testPaths.Where(Path => Matches(Part, Path)).ToList();
			if (Matched.Count == 0)
			{
				if (complete && Part.Text.Length > 0)
				{
					Result.Add(Parts.Count == 1
						? $"-filter '{Shown}' matched none of the tests"
						: $"-filter part '{Shown}' matched none of the {testPaths.Count} tests the rest of the filter found");
				}
				continue;
			}
			// StartsWith: always ends at a '.', and a part anchored at the end names a whole tail.
			if (Part.FromEnd || Part.Text.Length == 0 || Part.Text.EndsWith('.'))
			{
				continue;
			}
			// The name each match continues the part into, up to its next '.': "Game" in GameBench.A is GameBench.
			List<string> Names = Matched.Where(Path => Path.StartsWith(Part.Text, StringComparison.OrdinalIgnoreCase))
				.Select(Path => Path.IndexOf('.', Part.Text.Length) is int Dot and >= 0 ? Path[..Dot] : Path)
				.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
			if (Names.Count > 1)
			{
				string Others = string.Join(", ", Names.Where(Name => !Name.Equals(Part.Text, StringComparison.OrdinalIgnoreCase)));
				Result.Add($"-filter part '{Shown}' has no trailing '.', so it also matches tests under {Others} ({Matched.Count} tests in all):"
					+ $" give '{Part.Text}.' (or StartsWith:{Part.Text}) for {Part.Text}.* only");
			}
		}
		return Result;
	}

	static bool Matches(Part part, string testPath) => part.FromStart || part.FromEnd
		? (!part.FromStart || testPath.StartsWith(part.Text, StringComparison.OrdinalIgnoreCase))
			&& (!part.FromEnd || testPath.EndsWith(part.Text, StringComparison.OrdinalIgnoreCase))
		: testPath.Contains(part.Text, StringComparison.OrdinalIgnoreCase);

	/// <inheritdoc/>
	public override string ToString() => Text;
}
