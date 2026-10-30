// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal.Tests;

[TestClass]
public sealed class ExpectTests
{
	// ---- AutomationFilter: the engine's matching of "Automation RunTests <filter>" ----

	[TestMethod]
	[DataRow("Math.Vector", "Project.Math.Vector.Add", true)]
	[DataRow("math.vector", "Project.Math.Vector.Add", true)]
	[DataRow("Math.Matrix", "Project.Math.Vector.Add", false)]
	[DataRow("StartsWith:Project.Math", "Project.Math.Vector.Add", true)]
	[DataRow("StartsWith:Project.Math", "Project.Math", false)]
	[DataRow("StartsWith:Project.Math", "Project.MathExtra.Add", false)]
	[DataRow("StartsWith:Project.Math.", "Project.Math.Add", true)]
	[DataRow("startswith: project.math", "Project.Math.Add", true)]
	[DataRow("StartsWith:Math", "Project.Math.Add", false)]
	[DataRow("^Project.Math", "Project.MathExtra.Add", true)]
	[DataRow("^Math", "Project.Math.Add", false)]
	[DataRow("Vector.Add$", "Project.Math.Vector.Add", true)]
	[DataRow("Vector.Add$", "Project.Math.Vector.AddTwo", false)]
	[DataRow("^Project.Math.Add$", "Project.Math.Add", true)]
	[DataRow("^Project.Math.Add$", "Project.Math.Add.More", false)]
	[DataRow("Other+StartsWith:Project.", "Project.Math.Add", true)]
	[DataRow("+ Other ++Math.Add+", "Project.Math.Add", true)]
	[DataRow("Other+Else", "Project.Math.Add", false)]
	public void FiltersMatchAsTheEngineDoes(string filter, string testPath, bool expected)
	{
		Assert.AreEqual(expected, AutomationFilter.Parse(filter).Matches(testPath));
	}

	[TestMethod]
	[DataRow("Group:Smoke")]
	[DataRow("Project.Math+group:Smoke")]
	public void GroupFiltersCantBeMatched(string filter)
	{
		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(() => AutomationFilter.Parse(filter));
		Assert.Contains("group", Error.Message);
	}

	// ---- ExpectedTests ----

	static readonly string[] File =
	[
		"# Tests the cooked game must pass.",
		"",
		"Project.Math.Add",
		"  Project.Math.Sub  ",
		"Project.Math.Add",
		"Project.Ui.Open",
		"   # indented comment",
	];

	[TestMethod]
	public void TheRunExpectsTheListedNamesItsFilterMatches()
	{
		ExpectedTests Expected = ExpectedTests.Parse("expect.txt", File, "StartsWith:Project.Math");

		CollectionAssert.AreEqual(new[] { "Project.Math.Add", "Project.Math.Sub", "Project.Ui.Open" }, Expected.Listed.ToArray());
		CollectionAssert.AreEqual(new[] { "Project.Math.Add", "Project.Math.Sub" }, Expected.Names.ToArray());
		CollectionAssert.AreEqual(new[] { "Project.Math.Add", "Project.Math.Sub", "Project.Ui.Open" },
			ExpectedTests.Parse("expect.txt", File, "Project.").Names.ToArray());
	}

	[TestMethod]
	public void AFileWithoutNamesOrWithoutAMatchIsAUsageError()
	{
		Assert.ThrowsExactly<UakUsageException>(() => ExpectedTests.Parse("expect.txt", ["# nothing", "", "  "], "Project."));
		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(() => ExpectedTests.Parse("expect.txt", File, "Other."));
		Assert.Contains("matches none of the 3 test names", Error.Message);
		Assert.ThrowsExactly<UakUsageException>(() => ExpectedTests.Parse("expect.txt", File, "Group:Smoke"));
		Assert.ThrowsExactly<UakUsageException>(() => ExpectedTests.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "expect.txt"), "Project."));
	}

	static AutomationLogSummary Run(params string[] completed)
	{
		List<string> Lines = [$"LogAutomationCommandLine: Display: Found {completed.Length} automation tests based on 'Project.'"];
		foreach (string Test in completed)
		{
			string[] Parts = Test.Split('=');
			Lines.Add($"LogAutomationController: Display: Test Completed. Result={{{Parts[1]}}} Name={{{Parts[0].Split('.')[^1]}}} Path={{{Parts[0]}}}");
		}
		Lines.Add("LogAutomationCommandLine: Display: **** TEST COMPLETE. EXIT CODE: 0 ****");
		return AutomationLogParser.Parse(Lines);
	}

	[TestMethod]
	public void EveryExpectedTestMustPassAndNoOtherMayComplete()
	{
		ExpectedTests Expected = ExpectedTests.Parse("expect.txt", File, "StartsWith:Project.Math");

		ExpectCheck Clean = Expected.Check(Run("Project.Math.Add=Success", "Project.Math.Sub=Success"));
		Assert.IsEmpty(Clean.Missing);
		Assert.IsEmpty(Clean.Unexpected);
		Assert.IsEmpty(Clean.Problems());

		ExpectCheck Bad = Expected.Check(Run("Project.Math.Add=Fail", "Project.Math.Mul=Success", "Project.Math.Mul=Success"));
		CollectionAssert.AreEqual(new[] { "Project.Math.Add (completed: Fail)", "Project.Math.Sub (did not complete)" },
			Bad.Missing.Select(Missing => Missing.ToString()).ToArray());
		CollectionAssert.AreEqual(new[] { "Project.Math.Mul" }, Bad.Unexpected.ToArray());
		// An unexpected test is not a problem in itself: uak test reports it apart (exit code 3), and a failed one fails the run anyway.
		CollectionAssert.AreEqual(new[] { "2 expected tests did not pass" }, Bad.Problems());
	}

	[TestMethod]
	public void ASkippedExpectedTestDidNotPass()
	{
		ExpectedTests Expected = ExpectedTests.Parse("expect.txt", ["Project.Math.Add"], "Project.Math");

		ExpectCheck Check = Expected.Check(Run("Project.Math.Add=Skipped"));

		Assert.AreEqual("Project.Math.Add (completed: Skipped)", Check.Missing.Single().ToString());
		// Names compare exactly: the engine logs each test's full path as registered.
		Assert.AreEqual("project.math.add (did not complete)", ExpectedTests.Parse("expect.txt", ["project.math.add"], "Project.Math")
			.Check(Run("Project.Math.Add=Success")).Missing.Single().ToString());
	}
}
