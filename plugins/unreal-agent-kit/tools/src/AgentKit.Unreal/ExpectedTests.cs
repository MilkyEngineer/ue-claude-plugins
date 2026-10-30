// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>An expected test that did not pass: it never completed (<see cref="Result"/> null), or completed with another result.</summary>
/// <param name="Name">The test's full path.</param>
/// <param name="Result">Its result word when it completed (Fail, Skipped, ...), or null.</param>
public sealed record MissingTest(string Name, string? Result)
{
	/// <inheritdoc/>
	public override string ToString() => Result is null ? $"{Name} (did not complete)" : $"{Name} (completed: {Result})";
}

/// <summary>How a run's completed tests compare with the expected ones.</summary>
/// <param name="Missing">Expected tests without a "Test Completed. Result={Success}" line.</param>
/// <param name="Unexpected">Completed tests (any result) that were not expected.</param>
public sealed record ExpectCheck(IReadOnlyList<MissingTest> Missing, IReadOnlyList<string> Unexpected)
{
	/// <summary>
	/// Why the run fails what was expected: an expected test that did not pass. Empty when each passed. An unexpected test is
	/// not a failure in itself (a failed one already fails the run): <c>uak test</c> reports it apart, with its own exit code.
	/// </summary>
	public List<string> Problems()
	{
		List<string> Result = [];
		if (Missing.Count > 0)
		{
			Result.Add($"{Missing.Count} expected test{(Missing.Count == 1 ? "" : "s")} did not pass");
		}
		return Result;
	}
}

/// <summary>
/// The tests a run must pass, from an -expect file: one full test name per line; blank lines and lines starting with '#'
/// are ignored, and each name is trimmed. A run expects the listed names its -filter matches (<see cref="AutomationFilter"/>),
/// so one file can serve runs with different filters.
/// </summary>
public sealed class ExpectedTests
{
	ExpectedTests(string file, IReadOnlyList<string> listed, IReadOnlyList<string> names)
	{
		File = file;
		Listed = listed;
		Names = names;
	}

	/// <summary>The -expect file's full path.</summary>
	public string File { get; }

	/// <summary>Every name the file lists, in order, without duplicates.</summary>
	public IReadOnlyList<string> Listed { get; }

	/// <summary>The listed names the run's filter matches: the tests this run must pass.</summary>
	public IReadOnlyList<string> Names { get; }

	/// <summary>Reads an -expect file and keeps the names <paramref name="filter"/> matches.</summary>
	/// <exception cref="UakUsageException">The file is missing or lists no names, the filter is a "Group:" one, or it matches none of the names.</exception>
	public static ExpectedTests Load(string path, string filter)
	{
		string Full = Path.GetFullPath(path);
		if (!System.IO.File.Exists(Full))
		{
			throw new UakUsageException($"-expect file not found: {Full}");
		}
		return Parse(Full, System.IO.File.ReadAllLines(Full), filter);
	}

	/// <summary>As <see cref="Load"/>, from the file's lines.</summary>
	public static ExpectedTests Parse(string file, IEnumerable<string> lines, string filter)
	{
		AutomationFilter Filter = AutomationFilter.Parse(filter);
		List<string> Listed = [];
		HashSet<string> Seen = new(StringComparer.Ordinal);
		foreach (string Line in lines)
		{
			string Name = Line.Trim();
			if (Name.Length > 0 && !Name.StartsWith('#') && Seen.Add(Name))
			{
				Listed.Add(Name);
			}
		}
		if (Listed.Count == 0)
		{
			throw new UakUsageException($"-expect file {file} lists no test names");
		}
		List<string> Names = Listed.Where(Filter.Matches).ToList();
		if (Names.Count == 0)
		{
			throw new UakUsageException($"-filter={filter} matches none of the {Listed.Count} test names in {file}, so the run would expect no tests");
		}
		return new ExpectedTests(file, Listed, Names);
	}

	/// <summary>
	/// Compares a run with the expected tests: each must have a "Test Completed. Result={Success}" line with its exact path
	/// (else it is missing), and any other test with a "Test Completed" line is unexpected.
	/// </summary>
	public ExpectCheck Check(AutomationLogSummary summary)
	{
		HashSet<string> Expected = new(Names, StringComparer.Ordinal);
		List<MissingTest> Missing = [];
		foreach (string Name in Names)
		{
			List<AutomationTestResult> Runs = summary.Tests.Where(Test => Test.Path.Equals(Name, StringComparison.Ordinal)).ToList();
			if (!Runs.Any(Test => Test.Passed))
			{
				Missing.Add(new MissingTest(Name, Runs.Count == 0 ? null : Runs[^1].Result));
			}
		}
		List<string> Unexpected = summary.Tests.Select(Test => Test.Path).Where(Path => !Expected.Contains(Path)).Distinct(StringComparer.Ordinal).ToList();
		return new ExpectCheck(Missing, Unexpected);
	}
}
