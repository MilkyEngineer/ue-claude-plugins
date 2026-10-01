// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Vcs.Tests;

[TestClass]
public sealed class GitStatusParserTests
{
	static readonly string s_root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo"));

	static string Full(string relative) => Path.GetFullPath(Path.Combine(s_root, relative));

	[TestMethod]
	public void ParsesEveryEntryType()
	{
		string output = string.Join('\0',
			"# branch.oid 1a2b3c",
			"# branch.head main",
			"1 .M N... 100644 100644 100644 aaaa bbbb Source/Game.cpp",
			"1 A. N... 000000 100644 100644 0000 cccc Source/New File.cpp",
			"1 .D N... 100644 100644 000000 aaaa aaaa Config/Gone.ini",
			"1 AM N... 000000 100644 100644 0000 dddd Source/AddedThenEdited.cpp",
			"1 T. N... 100644 120000 120000 aaaa eeee Link",
			"2 R. N... 100644 100644 100644 aaaa aaaa R100 Content/Renamed.uasset",
			"Content/Original.uasset",
			"2 C. N... 100644 100644 100644 aaaa aaaa C75 Copy.txt",
			"Source.txt",
			"u UU N... 100644 100644 100644 100644 aaaa bbbb cccc Merge.txt",
			"? Untracked dir/file.txt",
			"! Saved/Logs/Game.log",
			"");

		List<VcsFileStatus> statuses = GitStatusParser.Parse(output, s_root);

		CollectionAssert.AreEqual(
			new[]
			{
				new VcsFileStatus(Full("Source/Game.cpp"), VcsFileState.Modified, null, ".M"),
				new VcsFileStatus(Full("Source/New File.cpp"), VcsFileState.Added, null, "A."),
				new VcsFileStatus(Full("Config/Gone.ini"), VcsFileState.Deleted, null, ".D"),
				new VcsFileStatus(Full("Source/AddedThenEdited.cpp"), VcsFileState.Added, null, "AM"),
				new VcsFileStatus(Full("Link"), VcsFileState.TypeChanged, null, "T."),
				new VcsFileStatus(Full("Content/Renamed.uasset"), VcsFileState.Renamed, Full("Content/Original.uasset"), "R."),
				new VcsFileStatus(Full("Copy.txt"), VcsFileState.Copied, Full("Source.txt"), "C."),
				new VcsFileStatus(Full("Merge.txt"), VcsFileState.Conflicted, null, "UU"),
				new VcsFileStatus(Full("Untracked dir/file.txt"), VcsFileState.Untracked, null, "??"),
				new VcsFileStatus(Full("Saved/Logs/Game.log"), VcsFileState.Ignored, null, "!!"),
			},
			statuses);
	}

	[TestMethod]
	public void EmptyOutputHasNoEntries()
	{
		Assert.IsEmpty(GitStatusParser.Parse(string.Empty, s_root));
	}

	[TestMethod]
	[DataRow(".M", VcsFileState.Modified)]
	[DataRow("M.", VcsFileState.Modified)]
	[DataRow("MM", VcsFileState.Modified)]
	[DataRow("A.", VcsFileState.Added)]
	[DataRow("AD", VcsFileState.Deleted)]
	[DataRow("D.", VcsFileState.Deleted)]
	[DataRow(".T", VcsFileState.TypeChanged)]
	public void ClassifiesOrdinaryCodes(string xy, VcsFileState expected)
	{
		Assert.AreEqual(expected, GitStatusParser.ClassifyOrdinary(xy));
	}
}
