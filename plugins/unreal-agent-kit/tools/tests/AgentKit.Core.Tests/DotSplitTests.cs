// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Tests;

/// <summary>Windows PowerShell 5.1 splits an unquoted -key=a.b at the first dot for a native program.</summary>
[TestClass]
public sealed class DotSplitTests
{
	[TestMethod]
	public void AProjectCommandBuiltAgainstAnOlderKitStillFindsItsConstructor()
	{
		Assert.IsNotNull(typeof(UakArguments).GetConstructor([typeof(IEnumerable<string>)]));
	}

	[TestMethod]
	public void TheOldConstructorJoinsNothing()
	{
		// As in 0.4.1: a project command that takes dot paths must not change meaning under a newer kit.
		UakArguments Arguments = new(["-key=a", ".b", "-empty=", @"..\c", "-Last=d", "--", "-Rest=e", ".f"]);

		Assert.AreEqual("a", Arguments.GetString("key"));
		Assert.AreEqual("d", Arguments.GetString("Last"));
		CollectionAssert.AreEqual(new[] { ".b", @"..\c" }, Arguments.Positional.ToArray());
		CollectionAssert.AreEqual(new[] { "-Rest=e", ".f" }, Arguments.Rest.ToArray());
	}

	[TestMethod]
	public void ASplitValueIsJoinedAgain()
	{
		UakArguments Arguments = new(["-expect=x", ".txt", "-filter=SKIM", ".", "-name=one"], DotSplitRule.Options);

		Assert.AreEqual("x.txt", Arguments.GetString("expect"));
		Assert.AreEqual("SKIM.", Arguments.GetString("filter"));
		Assert.AreEqual("one", Arguments.GetString("name"));
		Assert.IsEmpty(Arguments.Positional);
	}

	[TestMethod]
	public void AValueThatStartsWithADotIsJoinedToItsEmptyOption()
	{
		Assert.AreEqual(@"..\x.txt", new UakArguments(["-expect=", @"..\x.txt"], DotSplitRule.Options).GetString("expect"));
		Assert.AreEqual(@"..\x.txt", new UakArguments(["-expect=", @"..\x.txt"], DotSplitRule.Paths).GetString("expect"));
	}

	[TestMethod]
	public void OnlyOneTokenJoinsAnOption()
	{
		// Only the first dot splits, so a second token isn't the same argument's.
		UakArguments Arguments = new(["-a=b", ".c", ".d"], DotSplitRule.Options);

		Assert.AreEqual("b.c", Arguments.GetString("a"));
		CollectionAssert.AreEqual(new[] { ".d" }, Arguments.Positional.ToArray());
	}

	[TestMethod]
	public void AFlagJoinsNothing()
	{
		UakArguments Arguments = new(["-flag", ".txt", "plain", ".c"], DotSplitRule.Options);

		Assert.IsTrue(Arguments.GetFlag("flag"));
		CollectionAssert.AreEqual(new[] { ".txt", "plain", ".c" }, Arguments.Positional.ToArray());
	}

	[TestMethod]
	[DataRow(".")]
	[DataRow("..")]
	[DataRow(@".\Foo.cpp")]
	[DataRow("./Foo.cpp")]
	[DataRow(@"..\Foo.cpp")]
	[DataRow("../Foo.cpp")]
	public void ACommandThatTakesPathsKeepsAPathWhereItIs(string path)
	{
		UakArguments Arguments = new(["-MaxParallelActions=2", path], DotSplitRule.Paths);

		Assert.AreEqual("2", Arguments.GetString("MaxParallelActions"));
		CollectionAssert.AreEqual(new[] { path }, Arguments.Positional.ToArray());
	}

	[TestMethod]
	public void ACommandThatTakesPathsStillJoinsWhatIsNoPath()
	{
		UakArguments Arguments = new(["-filter=x", ".txt", @".\Foo.cpp"], DotSplitRule.Paths);

		Assert.AreEqual("x.txt", Arguments.GetString("filter"));
		CollectionAssert.AreEqual(new[] { @".\Foo.cpp" }, Arguments.Positional.ToArray());
	}

	[TestMethod]
	public void AFileOfThatNameStaysAPath()
	{
		string Name = "." + Guid.NewGuid().ToString("N");
		string File = Path.Combine(Directory.GetCurrentDirectory(), Name);
		System.IO.File.WriteAllText(File, "");
		try
		{
			CollectionAssert.AreEqual(new[] { Name }, new UakArguments(["-name=x", Name], DotSplitRule.Paths).Positional.ToArray());
			Assert.AreEqual("x" + Name, new UakArguments(["-name=x", Name], DotSplitRule.Options).GetString("name"), "a command that takes no paths can't mean a path");
		}
		finally
		{
			System.IO.File.Delete(File);
		}
	}

	[TestMethod]
	public void ACommandRunnersDottedWordNeverAsksTheDisk()
	{
		// `uak lock run -name=x .tool.bat arg`: a command in one reading, the split -name=x.tool.bat in the other.
		string Name = "." + Guid.NewGuid().ToString("N");
		string[] Given = ["-name=x", Name, "arg"];

		string Absent = Assert.ThrowsExactly<UakUsageException>(() => new UakArguments(Given, DotSplitRule.Command)).Message;
		string File = Path.Combine(Directory.GetCurrentDirectory(), Name);
		System.IO.File.WriteAllText(File, "");
		try
		{
			Assert.AreEqual(Absent, Assert.ThrowsExactly<UakUsageException>(() => new UakArguments(Given, DotSplitRule.Command)).Message, "the same words, the same meaning, in any folder");
		}
		finally
		{
			System.IO.File.Delete(File);
		}
		Assert.Contains(Name, Absent);
		Assert.Contains("nothing was run", Absent);
		Assert.Contains("lone \"--\"", Absent);
		Assert.Contains(UakArguments.DotSplitHint, Absent);
	}

	[TestMethod]
	public void ACommandRunnersSeparatorSettlesIt()
	{
		UakArguments Arguments = new(["-name=my", ".lock", "--", "cmd", "-Key=x", ".y"], DotSplitRule.Command);

		Assert.AreEqual("my.lock", Arguments.GetString("name"));
		Assert.IsEmpty(Arguments.Positional);
		CollectionAssert.AreEqual(new[] { "cmd", "-Key=x", ".y" }, Arguments.Rest.ToArray(), "a wrapper's command stays as it came");
	}

	[TestMethod]
	[DataRow(@".\tool.bat")]
	[DataRow("./tool.sh")]
	[DataRow(@"..\tool.bat")]
	public void ACommandWrittenAsAPathStartsTheCommand(string path)
	{
		UakArguments Arguments = new(["-name=x", path, "arg"], DotSplitRule.Command);

		Assert.AreEqual("x", Arguments.GetString("name"));
		CollectionAssert.AreEqual(new[] { path, "arg" }, Arguments.Positional.ToArray());
	}

	[TestMethod]
	public void ACommandRunnersEmptyOptionAlwaysTakesTheToken()
	{
		Assert.AreEqual(@"..\x", new UakArguments(["-output=", @"..\x", "cmd"], DotSplitRule.Command).GetString("output"));
	}

	[TestMethod]
	public void ADottedWordAfterAFlagOrAWordIsNoSplit()
	{
		UakArguments Arguments = new(["-force", ".tool.bat", "arg", ".other"], DotSplitRule.Command);

		CollectionAssert.AreEqual(new[] { ".tool.bat", "arg", ".other" }, Arguments.Positional.ToArray());
	}

	[TestMethod]
	public void WhatFollowsASeparatorStaysVerbatimUntilItIsPassedOn()
	{
		UakArguments Arguments = new(["-a=b", "--", "-Key=x", ".txt", "-Other=y", @".\z", "-Empty=", @".\z", "--", "-Last=x", ".y"], DotSplitRule.Options);

		CollectionAssert.AreEqual(new[] { "-Key=x", ".txt", "-Other=y", @".\z", "-Empty=", @".\z", "--", "-Last=x", ".y" }, Arguments.Rest.ToArray(), "a wrapper's command stays as it came: a uak it runs joins them itself");
		CollectionAssert.AreEqual(new[] { "-Key=x.txt", "-Other=y", @".\z", @"-Empty=.\z", "--", "-Last=x", ".y" }, Arguments.PassThrough.ToArray());
	}

	[TestMethod]
	public void ASeparatorIsNeverJoinedAcross()
	{
		UakArguments Arguments = new(["-a=b", "--", ".txt"], DotSplitRule.Options);

		Assert.AreEqual("b", Arguments.GetString("a"));
		CollectionAssert.AreEqual(new[] { ".txt" }, Arguments.Rest.ToArray());
	}

	[TestMethod]
	public void ARefusalNamesTheSplitWhenADotTokenIsLeft()
	{
		// -a.b=c arrives as -a and .b=c: nothing to join it to.
		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(() => new UakArguments(["-flag", ".b=c"], DotSplitRule.Options).ThrowIfPositional("uak test"));

		Assert.Contains("uak test takes no positional arguments: .b=c.", Error.Message);
		Assert.Contains(UakArguments.DotSplitHint, Error.Message);
		Assert.Contains("--key=a.b", Error.Message);
		Assert.Contains("'-key=a.b'", Error.Message);
	}

	[TestMethod]
	public void ARefusalOfAnotherArgumentSaysNothingOfTheSplit()
	{
		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(() => new UakArguments(["plain"]).ThrowIfPositional("uak test"));

		Assert.DoesNotContain("PowerShell", Error.Message);
	}

	[TestMethod]
	public void ThrowIfMorePositionalThanNamesTheSplitToo()
	{
		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(() => new UakArguments(["one", ".two"]).ThrowIfMorePositionalThan(1));

		Assert.Contains("Unexpected argument .two.", Error.Message);
		Assert.Contains(UakArguments.DotSplitHint, Error.Message);
		Assert.DoesNotContain("PowerShell", Assert.ThrowsExactly<UakUsageException>(() => new UakArguments(["one", "two"]).ThrowIfMorePositionalThan(1)).Message);
	}

	[TestMethod]
	public void TheHostsOwnOptionsAreJoinedToo()
	{
		(UakHost.GlobalOptions Options, IReadOnlyList<string> Remaining) = UakHost.ParseGlobalOptions(["-project=Game", ".uproject", "-engine=", @"..\UE", "test", "-filter=A", ".B"]);

		Assert.AreEqual("Game.uproject", Options.Project);
		Assert.AreEqual(@"..\UE", Options.Engine);
		CollectionAssert.AreEqual(new[] { "test", "-filter=A", ".B" }, Remaining.ToArray(), "the command's own arguments are the command's to join");
	}
}
