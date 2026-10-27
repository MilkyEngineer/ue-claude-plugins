// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Unreal.Tests;

[TestClass]
public sealed class AutomationLogParserTests
{
	[TestMethod]
	public void APassingRunHasNoProblems()
	{
		AutomationLogSummary Summary = AutomationLogParser.Parse(Fixtures.Lines("test-passed.log"));

		Assert.AreEqual(17, Summary.FoundCount);
		Assert.HasCount(17, Summary.Tests);
		Assert.AreEqual(17, Summary.Passed);
		Assert.AreEqual(0, Summary.Failed);
		Assert.AreEqual(0, Summary.TestCompleteExitCode);
		Assert.AreEqual(("Success", "VectorAdd", "Project.Math.VectorAdd"), (Summary.Tests[0].Result, Summary.Tests[0].Name, Summary.Tests[0].Path));
		Assert.IsEmpty(Summary.Problems());
	}

	[TestMethod]
	public void FailuresCarryTheirErrors()
	{
		// Trimmed: the log found 15 tests; the excerpt keeps the two that failed.
		AutomationLogSummary Summary = AutomationLogParser.Parse(Fixtures.Lines("test-failed.log"));

		Assert.AreEqual(15, Summary.FoundCount);
		Assert.AreEqual(2, Summary.Failed);
		Assert.AreEqual(-1, Summary.TestCompleteExitCode);
		AutomationTestResult Damage = Summary.Tests[1];
		Assert.AreEqual("Project.Gameplay.DamageReducesHealth", Damage.Path);
		Assert.HasCount(3, Damage.Errors);
		Assert.StartsWith("Expected 'Health after 10 damage is 90' to be true.", Damage.Errors[0]);
		Assert.HasCount(1, Summary.Tests[0].Errors);
		Assert.IsEmpty(Summary.OtherErrors);

		List<string> Problems = Summary.Problems();
		Assert.Contains("found 15 automation tests but 2 completed", Problems);
		Assert.Contains("2 tests failed", Problems);
	}

	[TestMethod]
	public void NoMatchingTestsIsAProblem()
	{
		AutomationLogSummary Summary = AutomationLogParser.Parse(Fixtures.Lines("test-no-match.log"));

		Assert.AreEqual("Project.Missing", Summary.NoMatchFilter);
		Assert.IsNull(Summary.FoundCount);
		CollectionAssert.AreEqual(new[] { "no automation tests matched 'Project.Missing'" }, Summary.Problems());
	}

	[TestMethod]
	public void ACrashBeforeTheQueueFinishedIsAProblem()
	{
		string[] Lines = Fixtures.Lines("test-passed.log");
		// Cut the log after the fifth test, as a crash would.
		int Cut = Array.FindIndex(Lines, Line => Line.Contains("Path={Project.Math.Matrix.RoundTrip}", StringComparison.Ordinal));
		AutomationLogSummary Summary = AutomationLogParser.Parse(Lines.Take(Cut));

		Assert.AreEqual(17, Summary.FoundCount);
		Assert.AreEqual(0, Summary.Failed);
		List<string> Problems = Summary.Problems();
		Assert.Contains($"found 17 automation tests but {Summary.Tests.Count} completed", Problems);
		Assert.IsTrue(Problems.Any(Problem => Problem.Contains("never finished", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void AnEmptyLogMeansTheTestsNeverStarted()
	{
		AutomationLogSummary Summary = AutomationLogParser.Parse([]);

		Assert.IsNull(Summary.FoundCount);
		Assert.HasCount(1, Summary.Problems());
		Assert.Contains("did not start", Summary.Problems()[0]);
	}

	[TestMethod]
	public void SkippedTestsAreNeitherPassedNorFailed()
	{
		AutomationLogSummary Summary = AutomationLogParser.Parse(
		[
			"LogAutomationCommandLine: Display: Found 2 automation tests based on 'Game'",
			"LogAutomationController: Display: Test Completed. Result={Success} Name={A} Path={Game.A}",
			"LogAutomationController: Display: Test Completed. Result={Skipped} Name={B} Path={Game.B}",
			"LogAutomationCommandLine: Display: **** TEST COMPLETE. EXIT CODE: 0 ****",
		]);

		Assert.AreEqual(1, Summary.Passed);
		Assert.AreEqual(1, Summary.Skipped);
		Assert.AreEqual(0, Summary.Failed);
		Assert.IsEmpty(Summary.Problems());
	}

	[TestMethod]
	public void EveryTestSkippedMeansNothingRan()
	{
		AutomationLogSummary Summary = AutomationLogParser.Parse(
		[
			"LogAutomationCommandLine: Display: Found 2 automation tests based on 'Game'",
			"LogAutomationController: Display: Test Completed. Result={Skipped} Name={A} Path={Game.A}",
			"LogAutomationController: Display: Test Completed. Result={Skipped} Name={B} Path={Game.B}",
			"LogAutomationCommandLine: Display: **** TEST COMPLETE. EXIT CODE: 0 ****",
		]);

		Assert.AreEqual(0, Summary.Passed);
		Assert.AreEqual(2, Summary.Skipped);
		Assert.AreEqual(0, Summary.Failed);
		CollectionAssert.AreEqual(new[] { "NOTHING RAN: no test passed and 2 were skipped" }, Summary.Problems());
	}

	[TestMethod]
	public void FoundTestsAreListedFromTheLog()
	{
		// A cooked game's Client.log (trimmed): the found tests follow the "Found N" line, each as <tab><full path>.
		AutomationLogSummary Summary = AutomationLogParser.Parse(Fixtures.Lines("cooked-passed.log"));

		Assert.AreEqual(1, Summary.FoundCount);
		CollectionAssert.AreEqual(new[] { "Project.Scaffold.ModulesLoaded" }, Summary.FoundNames);
		Assert.AreEqual(1, Summary.Passed);
		Assert.AreEqual(0, Summary.Skipped);
		Assert.IsEmpty(Summary.NotRunNames);
		Assert.AreEqual(0, Summary.TestCompleteExitCode);
		Assert.IsEmpty(Summary.Problems());
	}

	[TestMethod]
	public void AFailedTestInACookedGameIsAFailure()
	{
		AutomationLogSummary Summary = AutomationLogParser.Parse(Fixtures.Lines("cooked-failed.log"));

		Assert.AreEqual(1, Summary.Failed);
		Assert.AreEqual(-1, Summary.TestCompleteExitCode);
		Assert.AreEqual("Project.Verdict.Fails.Run", Summary.Tests[0].Path);
		Assert.StartsWith("Project.Verdict.Fails always fails", Summary.Tests[0].Errors.Single());
		CollectionAssert.AreEqual(new[] { "1 test failed" }, Summary.Problems());
	}

	[TestMethod]
	public void AFoundTestWithoutACompletedLineWasSkipped()
	{
		// The controller skips a test that needs more participants than the run has without a "Test Completed" line; the
		// queue still finishes with exit code 0.
		AutomationLogSummary Summary = AutomationLogParser.Parse(Fixtures.Lines("cooked-skipped.log"));

		Assert.AreEqual(1, Summary.FoundCount);
		Assert.IsEmpty(Summary.Tests);
		Assert.AreEqual(0, Summary.TestCompleteExitCode);
		CollectionAssert.AreEqual(new[] { "Project.Verdict.Skipped.Run" }, Summary.NotRunNames);
		Assert.AreEqual(1, Summary.NotRun);
		Assert.AreEqual(1, Summary.Skipped);
		CollectionAssert.AreEqual(new[] { "NOTHING RAN: no test passed and 1 was skipped", "found 1 automation tests but 0 completed" }, Summary.Problems());
	}

	[TestMethod]
	public void FoundTestsThatNeverRanAreCountedWithoutAListing()
	{
		AutomationLogSummary Summary = AutomationLogParser.Parse(
		[
			"LogAutomationCommandLine: Display: Found 3 automation tests based on 'Game'",
			"LogAutomationController: Display: Test Completed. Result={Success} Name={A} Path={Game.A}",
			"LogAutomationCommandLine: Display: **** TEST COMPLETE. EXIT CODE: 0 ****",
		]);

		Assert.IsEmpty(Summary.FoundNames);
		Assert.IsEmpty(Summary.NotRunNames);
		Assert.AreEqual(2, Summary.NotRun);
		Assert.AreEqual(2, Summary.Skipped);
		CollectionAssert.AreEqual(new[] { "found 3 automation tests but 1 completed" }, Summary.Problems());
	}

	[TestMethod]
	public void OnlyTheLinesAfterTheFoundLineListTests()
	{
		AutomationLogSummary Summary = AutomationLogParser.Parse(
		[
			"LogAutomationCommandLine: Display: \tBefore.Found",
			"LogAutomationCommandLine: Display: Found 2 automation tests based on 'Game'",
			"LogAutomationCommandLine: Display: \tGame.A",
			"LogAutomationCommandLine: Display: \tGame.B",
			"LogAutomationController: Sending StopTests to 0",
			"LogAutomationCommandLine: Display: \tAfter.Listing",
			"LogAutomationController: Display: Test Completed. Result={Success} Name={A} Path={Game.A}",
			"LogAutomationCommandLine: Display: **** TEST COMPLETE. EXIT CODE: 0 ****",
		]);

		CollectionAssert.AreEqual(new[] { "Game.A", "Game.B" }, Summary.FoundNames);
		CollectionAssert.AreEqual(new[] { "Game.B" }, Summary.NotRunNames);
		Assert.AreEqual(1, Summary.Skipped);
	}

	[TestMethod]
	public void TestsLostToACrashAreNotSkipped()
	{
		string[] Lines = Fixtures.Lines("cooked-passed.log");
		int Cut = Array.FindIndex(Lines, Line => Line.Contains("Test Completed.", StringComparison.Ordinal));
		AutomationLogSummary Summary = AutomationLogParser.Parse(Lines.Take(Cut));

		Assert.AreEqual(0, Summary.NotRun);
		Assert.AreEqual(0, Summary.Skipped);
		List<string> Problems = Summary.Problems();
		Assert.Contains("found 1 automation tests but 0 completed", Problems);
		Assert.IsTrue(Problems.Any(Problem => Problem.Contains("never finished", StringComparison.Ordinal)));
		Assert.IsFalse(Problems.Any(Problem => Problem.StartsWith("NOTHING RAN", StringComparison.Ordinal)));
	}
}
