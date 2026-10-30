// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.RegularExpressions;
using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>A watched log category's Error and Warning lines in a run's log (<c>uak test -watch=</c>).</summary>
/// <param name="Category">The category as given, e.g. LogSaveGame.</param>
/// <param name="Errors">Its "Error:" lines.</param>
/// <param name="Warnings">Its "Warning:" lines.</param>
public sealed record LogCategoryCount(string Category, int Errors, int Warnings)
{
	/// <inheritdoc/>
	public override string ToString() => $"{Category}: {Errors} error{(Errors == 1 ? "" : "s")}, {Warnings} warning{(Warnings == 1 ? "" : "s")}";
}

/// <summary>
/// Counts the Error and Warning lines of given log categories in an engine log: lines of the form
/// "[time][frame]LogCategory: Error: ..." (the time and frame are absent before the log is set up). Only real Errors and
/// Warnings count: the automation framework logs a message a test expects (AddExpectedError, AddExpectedMessage) at
/// Verbose instead, and Display and Log lines are not counted, whatever their text.
/// </summary>
public static partial class LogCategoryCounts
{
	/// <summary>Parses -watch=A,B: category names (letters, digits and '_'), without duplicates.</summary>
	/// <exception cref="UakUsageException">A name is empty or holds another character.</exception>
	public static IReadOnlyList<string> ParseWatch(string? watch)
	{
		if (watch is null)
		{
			return [];
		}
		List<string> Categories = [];
		foreach (string Part in watch.Split(','))
		{
			string Category = Part.Trim();
			if (!CategoryPattern().IsMatch(Category))
			{
				throw new UakUsageException($"-watch={watch} must be log categories joined with ',', e.g. -watch=LogSaveGame,LogAutomationController");
			}
			if (!Categories.Contains(Category, StringComparer.OrdinalIgnoreCase))
			{
				Categories.Add(Category);
			}
		}
		return Categories;
	}

	/// <summary>Counts each category's Error and Warning lines. Category names match ignoring case, as FName does.</summary>
	public static List<LogCategoryCount> Count(IEnumerable<string> lines, IReadOnlyList<string> categories)
	{
		Dictionary<string, (int Errors, int Warnings)> Counts = new(StringComparer.OrdinalIgnoreCase);
		foreach (string Category in categories)
		{
			Counts[Category] = (0, 0);
		}
		if (categories.Count > 0)
		{
			foreach (string Line in lines)
			{
				Match M = LinePattern().Match(Line);
				if (M.Success && Counts.TryGetValue(M.Groups["category"].Value, out (int Errors, int Warnings) Count))
				{
					Counts[M.Groups["category"].Value] = M.Groups["verbosity"].Value == "Error" ? (Count.Errors + 1, Count.Warnings) : (Count.Errors, Count.Warnings + 1);
				}
			}
		}
		return categories.Select(Category => new LogCategoryCount(Category, Counts[Category].Errors, Counts[Category].Warnings)).ToList();
	}

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
	private static partial Regex CategoryPattern();

	// At most two bracketed prefixes ([2026.10.29-12.00.00:123][  0]), then "Category: Error: " or "Category: Warning: ".
	[GeneratedRegex(@"^(?:\[[^\]]*\]){0,2}(?<category>[A-Za-z_][A-Za-z0-9_]*): (?<verbosity>Error|Warning): ")]
	private static partial Regex LinePattern();
}
