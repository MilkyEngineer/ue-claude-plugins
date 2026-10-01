// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentKit.Unreal;

/// <summary>One automation test's outcome.</summary>
/// <param name="Result">The editor's result word: Success, Fail, Skipped, NotRun, ...</param>
/// <param name="Name">The test's short name.</param>
/// <param name="Path">The test's full path, e.g. "Project.Area.Test".</param>
public sealed record AutomationTestResult(string Result, string Name, string Path)
{
	/// <summary>The errors the controller logged for this test (after its "Test Completed" line).</summary>
	public List<string> Errors { get; } = [];

	/// <summary>Whether the test passed.</summary>
	public bool Passed => Result.Equals("Success", StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether the test was skipped, which is neither a pass nor a failure.</summary>
	public bool Skipped => Result.Equals("Skipped", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What an editor automation run's log says.</summary>
public sealed class AutomationLogSummary
{
	/// <summary>N of "Found N automation tests based on '...'", or null when the line is missing (the run never started).</summary>
	public int? FoundCount { get; set; }

	/// <summary>The filter a "No automation tests matched '...'" line names, or null.</summary>
	public string? NoMatchFilter { get; set; }

	/// <summary>Every completed test, in order.</summary>
	public List<AutomationTestResult> Tests { get; } = [];

	/// <summary>Controller errors that came before any test completed.</summary>
	public List<string> OtherErrors { get; } = [];

	/// <summary>The code of "**** TEST COMPLETE. EXIT CODE: N ****", or null when the queue never finished.</summary>
	public int? TestCompleteExitCode { get; set; }

	/// <summary>Tests that passed.</summary>
	public int Passed => Tests.Count(Test => Test.Passed);

	/// <summary>Tests that were skipped.</summary>
	public int Skipped => Tests.Count(Test => Test.Skipped);

	/// <summary>Tests that neither passed nor were skipped.</summary>
	public int Failed => Tests.Count(Test => !Test.Passed && !Test.Skipped);

	/// <summary>
	/// Why the run is not a clean pass, or an empty list when it is: tests found, every found test completed, at least one
	/// passed, none failed, and the queue finished with exit code 0.
	/// </summary>
	public List<string> Problems()
	{
		List<string> Result = [];
		// Every completed test skipped means nothing was checked: that must never read as a pass.
		if (Passed == 0 && Skipped > 0)
		{
			Result.Add($"NOTHING RAN: all {Skipped} completed test{(Skipped == 1 ? " was" : "s were")} skipped and none passed");
		}
		if (NoMatchFilter is not null)
		{
			Result.Add($"no automation tests matched '{NoMatchFilter}'");
		}
		else if (FoundCount is null)
		{
			Result.Add("the log has no \"Found N automation tests\" line: the editor did not start the tests");
		}
		else if (FoundCount == 0)
		{
			Result.Add("found 0 automation tests");
		}
		else if (Tests.Count != FoundCount)
		{
			Result.Add($"found {FoundCount} automation tests but {Tests.Count} completed");
		}
		if (Failed > 0)
		{
			Result.Add($"{Failed} test{(Failed == 1 ? "" : "s")} failed");
		}
		if (FoundCount > 0 && TestCompleteExitCode is null)
		{
			Result.Add("the test queue never finished (no \"TEST COMPLETE\" line): the editor crashed or was stopped");
		}
		else if (TestCompleteExitCode is int Code && Code != 0 && Failed == 0 && NoMatchFilter is null)
		{
			Result.Add($"the test queue finished with exit code {Code}");
		}
		return Result;
	}
}

/// <summary>Parses an editor log from an "Automation RunTests" run into an <see cref="AutomationLogSummary"/>.</summary>
public static partial class AutomationLogParser
{
	/// <summary>Parses the log's lines.</summary>
	public static AutomationLogSummary Parse(IEnumerable<string> lines)
	{
		AutomationLogSummary Summary = new();
		AutomationTestResult? Last = null;
		foreach (string Line in lines)
		{
			Match M;
			if ((M = CompletedPattern().Match(Line)).Success)
			{
				Last = new AutomationTestResult(M.Groups["result"].Value, M.Groups["name"].Value, M.Groups["path"].Value);
				Summary.Tests.Add(Last);
			}
			else if ((M = ControllerErrorPattern().Match(Line)).Success)
			{
				(Last?.Errors ?? Summary.OtherErrors).Add(M.Groups["msg"].Value.Trim());
			}
			else if ((M = FoundPattern().Match(Line)).Success)
			{
				// Only the first: a later RunTests in the same session would restart the count.
				Summary.FoundCount ??= int.Parse(M.Groups["count"].Value, CultureInfo.InvariantCulture);
			}
			else if ((M = NoMatchPattern().Match(Line)).Success)
			{
				Summary.NoMatchFilter = M.Groups["filter"].Value;
			}
			else if ((M = TestCompletePattern().Match(Line)).Success)
			{
				Summary.TestCompleteExitCode = int.Parse(M.Groups["code"].Value, CultureInfo.InvariantCulture);
			}
		}
		return Summary;
	}

	[GeneratedRegex(@"Test Completed\. Result=\{(?<result>\w+)\}\s*Name=\{(?<name>[^}]*)\}\s*Path=\{(?<path>[^}]*)\}")]
	private static partial Regex CompletedPattern();

	[GeneratedRegex(@"LogAutomationController: Error: (?!Test Completed)(?<msg>.*)$")]
	private static partial Regex ControllerErrorPattern();

	[GeneratedRegex(@"Found (?<count>\d+) automation tests? based on '")]
	private static partial Regex FoundPattern();

	[GeneratedRegex(@"No automation tests matched '(?<filter>[^']*)'")]
	private static partial Regex NoMatchPattern();

	[GeneratedRegex(@"\*\*\*\* TEST COMPLETE\. EXIT CODE: (?<code>-?\d+) \*\*\*\*")]
	private static partial Regex TestCompletePattern();
}
