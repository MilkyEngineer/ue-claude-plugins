// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal.Tests;

/// <summary>
/// What a build records of an edit it couldn't mark, by whose clock, and where; what it says of the marker; and what a refusal
/// says, and calls itself.
/// </summary>
public sealed partial class MutateTests
{
	const string UatBuildStarted = "********** BUILD COMMAND STARTED **********";
	const string UatBuildCompleted = "********** BUILD COMMAND COMPLETED **********";

	static readonly string[] CookedRunOutput =
	[
		"********** STAGE COMMAND STARTED **********",
		"Creating pak using staging manifest.",
		"********** STAGE COMMAND COMPLETED **********",
		"********** RUN COMMAND STARTED **********",
		"Client exited with error code: 0 (see the client log for more info)",
		"********** RUN COMMAND COMPLETED **********",
		"AutomationTool exiting with ExitCode=0 (Success)",
	];

	/// <summary>What the log said since entry <paramref name="from"/>: one command's lines, after another's.</summary>
	static string LogSince(Sandbox box, int from) => string.Join('\n', box.Logger.Entries.Skip(from).Select(Entry => Entry.Message));

	/// <summary>Every file under Source, older by <paramref name="age"/>: for a file system whose clock is behind, older than its window.</summary>
	static void AgeSource(Sandbox box, TimeSpan age)
	{
		foreach (string File in Directory.EnumerateFiles(Path.Combine(box.ProjectDirectory, "Source"), "*", SearchOption.AllDirectories))
		{
			System.IO.File.SetLastWriteTimeUtc(File, DateTime.UtcNow - age);
		}
	}

	/// <summary>UAT as a cooked run's fake: <paramref name="file"/> is edited during its build step, after <paramref name="duringBuild"/>; the game passes.</summary>
	static Func<ProcessInvocation, Action<string>, int> UatEdits(string file, Action? duringBuild = null) => (Invocation, OnLine) =>
	{
		OnLine(UatBuildStarted);
		duringBuild?.Invoke();
		File.WriteAllText(file, "// another agent's edit, during UAT's build\n");
		OnLine(UatBuildCompleted);
		string Folder = Invocation.Environment![CookedTestCommandLine.LogFolderVariable]!;
		File.WriteAllLines(CookedTestCommandLine.ClientLogFile(Folder), Fixtures.Lines("cooked-passed.log"));
		foreach (string Line in CookedRunOutput)
		{
			OnLine(Line);
		}
		return 0;
	};

	static Task<int> RunCookedAsync(Sandbox box, UnrealServices services) => new TestCommand(services).RunAsync(box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None);

	// ---- By the clock of the file's own file system (K6-F-1) ----

	[TestMethod]
	public async Task ASaveAfterTheBuildByAFileServersClockClearsTheFileThoughThatClockRunsBehind()
	{
		// Source and Intermediate on a file server an hour behind this machine: every time there is the server's, and so is the record.
		using Sandbox Box = new();
		TimeSpan Behind = TimeSpan.FromHours(1);
		AgeSource(Box, TimeSpan.FromHours(2));
		string Third = ThirdFile(Box);
		DateTime? Server(string _) => DateTime.UtcNow - Behind;
		FakeProcessRunner Overlapping = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				Sandbox.Write(Third, "another agent's edit, during the build");
				File.SetLastWriteTimeUtc(Third, DateTime.UtcNow - Behind);
				return CompileLines;
			}),
		};

		Assert.AreEqual(0, await BuildAsync(Box, Services(Overlapping, probe: Server, setWriteTime: NoMark)), Box.Logger.Text);

		DateTime Recorded = RebuildNeeded.Read(Box.Context()).Written[Third];
		Assert.IsLessThan(DateTime.UtcNow - TimeSpan.FromMinutes(30), Recorded, $"recorded by the server's clock, not this machine's: {Recorded:O}");

		// Saved after that build by the server's clock, which this machine's clock puts long before the build ended.
		DateTime SavedAt = Recorded.AddMilliseconds(500);
		File.WriteAllText(Third, "saved after the build");
		File.SetLastWriteTimeUtc(Third, SavedAt);
		Assert.IsTrue(SpinWait.SpinUntil(() => DateTime.UtcNow - Behind > SavedAt.AddMilliseconds(50), TimeSpan.FromSeconds(10)), "no window opened after the save");

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), probe: Server, setWriteTime: NoMark)), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)), Box.Logger.Text);
		await AssertTestRunsAsync(Box);
	}

	[TestMethod]
	public async Task WithNoProbeBesideTheFileTheRecordIsTheBuildsEndOncePerFolder()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Other = OldFile(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Other.cpp"));
		string Private = Path.GetDirectoryName(Third)!;
		string Source = Path.Combine(Box.ProjectDirectory, "Source");
		int InPrivate = 0;
		DateTime? NoProbeInSource(string Path)
		{
			if (Path.StartsWith(Private, StringComparison.OrdinalIgnoreCase))
			{
				Interlocked.Increment(ref InPrivate);
			}
			return Path.StartsWith(Source, StringComparison.OrdinalIgnoreCase) ? null : MutationJournal.ProbeFileTime(Path);
		}
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Third, "another agent's edit, during the build");
				File.WriteAllText(Other, "and another");
				// The build ends well after the edits.
				Thread.Sleep(50);
				return CompileLines;
			}),
		};

		Assert.AreEqual(0, await BuildAsync(Box, Services(Runner, probe: NoProbeInSource, setWriteTime: NoMark)), Box.Logger.Text);

		IReadOnlyDictionary<string, DateTime> Written = RebuildNeeded.Read(Box.Context()).Written;
		Assert.IsGreaterThan(File.GetLastWriteTimeUtc(Third), Written[Third], "the end of the build, by this machine's clock, and not the file's own time");
		Assert.IsGreaterThan(File.GetLastWriteTimeUtc(Other), Written[Other]);
		Assert.AreEqual(1, InPrivate, "one probe for the folder both files are in");
	}

	[TestMethod]
	public async Task AProbeThatThrowsFallsBackToTheBuildsEnd()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Source = Path.Combine(Box.ProjectDirectory, "Source");
		DateTime? DeniedInSource(string Path) => Path.StartsWith(Source, StringComparison.OrdinalIgnoreCase)
			? throw new UnauthorizedAccessException("no writes in this folder")
			: MutationJournal.ProbeFileTime(Path);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Third, "another agent's edit, during the build");
				Thread.Sleep(50);
				return CompileLines;
			}),
		};

		Assert.AreEqual(0, await BuildAsync(Box, Services(Runner, probe: DeniedInSource, setWriteTime: NoMark)), Box.Logger.Text);

		Assert.IsGreaterThan(File.GetLastWriteTimeUtc(Third), RebuildNeeded.Read(Box.Context()).Written[Third], "the end of the build, by this machine's clock");
	}

	// ---- A path no file can have, and a root that can't be reached (K6-F-2, K7-F-1, K7-F-3) ----

	/// <summary>
	/// Paths no file can have, as a marker edited by hand may hold: empty, with a NUL, and on Windows a file name with a wildcard
	/// or another character no name holds, a folder's name with one, a name over 255 characters, and a path past any length
	/// Windows takes, and a share path with no share, a server alone (K9-F-4), spelled with <c>.</c> or <c>..</c> too (K10-F-1).
	/// </summary>
	static string[] PathsNoFileCanHave(Sandbox box)
	{
		List<string> Paths = ["", "Bad\0Name.h"];
		if (OperatingSystem.IsWindows())
		{
			string Private = Path.Combine(box.ProjectDirectory, "Source", "Game", "Private");
			Paths.AddRange([
				@"\\nosuchserver-k9",
				@"\\nosuchserver-k9\",
				@"\\?\UNC\nosuchserver-k9",
				@"\\.\UNC\nosuchserver-k9\",
				@"\\nosuchserver-k10\.",
				@"\\nosuchserver-k10\..",
				@"\\localhost\.",
				Path.Combine(Private, "Bad?Name.h"),
				Path.Combine(Private, "Bad*Name.h"),
				Path.Combine(Private, "Bad|Name.h"),
				Path.Combine(Private, "Bad<Folder", "Name.h"),
				Path.Combine(Private, new string('L', 300) + ".h"),
				Path.Combine(Private, string.Join('\\', Enumerable.Repeat(new string('D', 200), 200)), "Name.h"),
			]);
		}
		return [.. Paths];
	}

	[TestMethod]
	public async Task AMarkerEditedByHandWithPathsNoFileCanHaveIsClearedByAWholeBuild()
	{
		using Sandbox Box = new();
		string[] Bad = PathsNoFileCanHave(Box);
		StateFiles.WriteJson(MarkerFile(Box), new[]
		{
			new RebuildNeededEntry
			{
				Target = "GameEditor", Platform = "Win64", Configuration = "Development", Stamped = [.. Bad], NotMarked = [.. Bad], Problems = [.. Bad.Select(_ => "by hand")],
				NotMarkedWritten = Bad.Distinct().ToDictionary(File => File, _ => Recorded, StringComparer.OrdinalIgnoreCase),
			},
		});

		// The refusal names them as gone, and nothing throws on the way.
		await AssertTestRefusedAsync(Box);
		Assert.Contains("deleted, so nothing to save", Box.Logger.Text);
		Assert.DoesNotContain("save first", Box.Logger.Text);

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)), Box.Logger.Text);
		await AssertTestRunsAsync(Box);
	}

	[TestMethod]
	public void APathNoFileCanHaveIsGoneAndNotMarked()
	{
		using Sandbox Box = new();
		foreach (string Bad in PathsNoFileCanHave(Box))
		{
			Assert.IsTrue(RebuildNeededFile.IsGone(Bad), Bad.Length > 100 ? Bad[..100] : Bad);
			Assert.IsEmpty(RebuildNeeded.WhatStays(EntryOf(Bad, Recorded), Recorded.AddHours(1)).NotMarked);
		}
	}

	/// <summary>
	/// A junction at <c>Source/Linked</c> of <paramref name="box"/>'s project to <c>Elsewhere</c>, whose <c>Private</c> folder holds
	/// <c>Linked.cpp</c>; the target folder and the file behind the link.
	/// </summary>
	static (string Target, string Linked, string Missing) Junction(Sandbox box)
	{
		string Target = Path.Combine(box.Root, "Elsewhere");
		Directory.CreateDirectory(Path.Combine(Target, "Private"));
		File.WriteAllText(Path.Combine(Target, "Private", "Linked.cpp"), "// behind the link\n");
		string Link = Path.Combine(box.ProjectDirectory, "Source", "Linked");
		ProcessStartInfo Start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (string Argument in new[] { "/c", "mklink", "/J", Link, Target })
		{
			Start.ArgumentList.Add(Argument);
		}
		using (Process Mklink = Process.Start(Start)!)
		{
			string Said = Mklink.StandardOutput.ReadToEnd() + Mklink.StandardError.ReadToEnd();
			Mklink.WaitForExit();
			Assert.AreEqual(0, Mklink.ExitCode, Said);
		}
		return (Target, Path.Combine(Link, "Private", "Linked.cpp"), Path.Combine(Link, "Private", "Missing.cpp"));
	}

	[TestMethod]
	public void AFileMissingBehindALiveLinkIsGone()
	{
		// A junction to a folder that is there says "not found" only of a file that isn't: a file deleted in a junctioned project
		// leaves the marker (K8-F-1).
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("a junction is Windows'");
			return;
		}
		using Sandbox Box = new();
		(string _, string Linked, string Missing) = Junction(Box);

		Assert.IsFalse(RebuildNeededFile.IsGone(Linked), "there, through the link");
		Assert.IsTrue(RebuildNeededFile.IsGone(Missing), "behind a link whose target is there");
		Assert.IsTrue(RebuildNeededFile.IsGone(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Linked)!)!, "NoSuchFolder", "Missing.cpp")), "and in a folder that isn't there either");
		Assert.IsEmpty(RebuildNeeded.WhatStays(EntryOf(Missing, Recorded), Recorded.AddHours(1)).NotMarked);
	}

	[TestMethod]
	public void AFileUnderALinkWhoseTargetIsGoneStays()
	{
		// A junction to a folder that went (a drive unplugged, a share gone) is there all the same, and says "not found" of every
		// file behind it (K7-F-3).
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("a junction is Windows'");
			return;
		}
		using Sandbox Box = new();
		(string Target, string Linked, string Missing) = Junction(Box);
		string Link = Path.GetDirectoryName(Path.GetDirectoryName(Linked)!)!;
		Assert.IsFalse(RebuildNeededFile.IsGone(Linked), "there, through the link");

		Directory.Delete(Target, recursive: true);

		Assert.IsFalse(RebuildNeededFile.IsGone(Linked), "behind a link whose target is gone");
		Assert.IsFalse(RebuildNeededFile.IsGone(Missing), "and so is any file behind it");
		Assert.IsTrue(RebuildNeededFile.IsGone(Path.Combine(Link, "Private", "Bad?Name.h")), "but a name no file can have is gone, told before the disk is asked");
		CollectionAssert.AreEqual(new[] { Linked }, RebuildNeeded.WhatStays(EntryOf(Linked, Recorded), Recorded.AddHours(1)).NotMarked.ToArray());
		Assert.IsTrue(RebuildNeededFile.IsGone(Path.Combine(Box.ProjectDirectory, "Source", "NotLinked", "Private", "Missing.cpp")), "with no link on the way, not found is gone");
	}

	[TestMethod]
	[DataRow(false, DisplayName = "with no recorded time")]
	[DataRow(true, DisplayName = "saved since its recorded time")]
	public async Task ARelativePathInAMarkerIsTheProjectsAndNotTheWorkingFolders(bool recorded)
	{
		// Read against the folder uak runs in, a file of the project's would name nothing, read as gone, and leave the marker with
		// no evidence (K7-F-7). Its recorded time is found under the same path.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Relative = Path.GetRelativePath(Box.ProjectDirectory, Third);
		Assert.IsFalse(File.Exists(Relative), $"the test runs in another folder: {Environment.CurrentDirectory}");
		StateFiles.WriteJson(MarkerFile(Box), new[] { EntryOf(Relative, recorded ? Recorded : null) });

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		if (recorded)
		{
			Assert.IsFalse(File.Exists(MarkerFile(Box)), "saved since its recorded time, so it leaves: " + Box.Logger.Text);
			return;
		}
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray(), "it is there, and wasn't saved");
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public void APathInAMarkerThatIsntFullyQualifiedIsTheProjectsWithTheBuildsSeparators()
	{
		// From the root, or from a drive's own folder, it would be read against the folder uak runs in; with forward slashes it
		// wouldn't match the build's own path for its file (K8-F-3). One that can't be resolved stays as it is.
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("a drive and a backslash are Windows'");
			return;
		}
		using Sandbox Box = new();
		string Project = Box.ProjectDirectory;
		string Third = Path.Combine(Project, "Source", "Game", "Private", "Third.cpp");
		string FromRoot = Third[(Path.GetPathRoot(Third)!.Length - 1)..];
		string FromDrive = Project[..2] + Path.GetRelativePath(Project, Third);
		string Forward = "Source/Game/Private/Third.cpp";
		Assert.IsTrue(FromRoot.StartsWith('\\') && FromDrive[1] == ':' && FromDrive[2] != '\\', $"{FromRoot}, {FromDrive}");
		string[] Given = [FromRoot, FromDrive, Forward, "Bad\0Name.h"];
		StateFiles.WriteJson(MarkerFile(Box), new[]
		{
			new RebuildNeededEntry
			{
				Target = "GameEditor", Platform = "Win64", Configuration = "Development", Stamped = [.. Given], NotMarked = [.. Given], Problems = [.. Given.Select(_ => "by hand")],
			},
		});

		RebuildNeededEntry Entry = RebuildNeededFile.Load(Box.Context()).Entries.Single();

		// Once each, as the marker's paths are keys (K9-F-2).
		string[] Expected = [Third, "Bad\0Name.h"];
		CollectionAssert.AreEqual(Expected, Entry.Stamped.ToArray());
		CollectionAssert.AreEqual(Expected, Entry.NotMarked.ToArray());
		Assert.HasCount(2, Entry.Problems);
	}

	[TestMethod]
	[DataRow(true, DisplayName = "the later first")]
	[DataRow(false, DisplayName = "the later last")]
	public void TwoRecordedTimesForOneFileKeepTheLater(bool laterFirst)
	{
		// The earlier would read a write made between the two as a save, and let the file go with no evidence (K8-F-3).
		using Sandbox Box = new();
		string Third = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Third.cpp");
		string Relative = Path.GetRelativePath(Box.ProjectDirectory, Third);
		(string File, DateTime Time) Later = ("Source/Game/Private/Third.cpp", Recorded.AddHours(1));
		(string File, DateTime Time) Earlier = (Relative, Recorded);
		RebuildNeededEntry Written = EntryOf(Third, null);
		foreach ((string File, DateTime Time) in laterFirst ? new[] { Later, Earlier } : new[] { Earlier, Later })
		{
			Written.NotMarkedWritten[File] = Time;
		}
		StateFiles.WriteJson(MarkerFile(Box), new[] { Written });

		RebuildNeededEntry Entry = RebuildNeededFile.Load(Box.Context()).Entries.Single();

		Assert.AreEqual(Recorded.AddHours(1), Entry.NotMarkedWritten.Single().Value, string.Join(", ", Entry.NotMarkedWritten));
		Assert.AreEqual(Third, Entry.NotMarkedWritten.Single().Key);
	}

	[TestMethod]
	public void ASharePathWithNoShareIsGoneAndNotMarked()
	{
		// ERROR_BAD_PATHNAME (161) comes at once for a share path with no share, which no file can have: it is told so before the
		// disk is asked, and a marker that holds one is no longer kept for good. A server or a file system may give 161 for a path
		// a file could have, so for any other path it is still asked as "not found" is (K9-F-4).
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("a share path is Windows'");
			return;
		}
		foreach (string Share in new[] { @"\\nosuchserver-k9", @"\\nosuchserver-k9\" })
		{
			IOException Said = Assert.Throws<IOException>(() => File.GetAttributes(Share), Share);
			Assert.AreEqual(unchecked((int)0x800700A1), Said.HResult, $"{Share}: {Said.Message}");
			Assert.IsTrue(RebuildNeededFile.IsGone(Share), Share);
			Assert.IsEmpty(RebuildNeeded.WhatStays(EntryOf(Share, Recorded), Recorded.AddHours(1)).NotMarked, Share);
		}
	}

	// ---- One file, one key (K9-F-2) ----

	/// <summary>
	/// Another way to name <paramref name="file"/>, a file of <paramref name="box"/>'s project: in other case, or as a marker
	/// edited by hand may name it, relative to the project.
	/// </summary>
	static string OtherSpelling(Sandbox box, string file, bool inCase) => inCase ? file.ToUpperInvariant() : Path.GetRelativePath(box.ProjectDirectory, file);

	/// <summary>
	/// An entry of GameEditor Win64 Development that names <paramref name="file"/> twice, as itself and as
	/// <paramref name="other"/>: the first with the line "first" and the time <paramref name="first"/>, the second with "second"
	/// and <paramref name="second"/>. Its times are keyed by case, as a marker's are once read.
	/// </summary>
	static RebuildNeededEntry NamedTwice(string file, string other, DateTime first, DateTime second) => new()
	{
		Target = "GameEditor", Platform = "Win64", Configuration = "Development", Stamped = [file, other], NotMarked = [file, other], Problems = ["first", "second"],
		NotMarkedWritten = new Dictionary<string, DateTime>(StringComparer.Ordinal) { [file] = first, [other] = second },
	};

	[TestMethod]
	[DataRow(true, true, DisplayName = "in two cases, the later last")]
	[DataRow(true, false, DisplayName = "in two cases, the later first")]
	[DataRow(false, true, DisplayName = "in two spellings, the later last")]
	[DataRow(false, false, DisplayName = "in two spellings, the later first")]
	public void AMarkerThatNamesOneFileTwiceIsReadAsNamingItOnce(bool inCase, bool laterLast)
	{
		// The first keeps its line, and the later time stands: an earlier one would read a write made before it as a save.
		using Sandbox Box = new();
		string Third = ThirdFile(Box);
		string Other = OtherSpelling(Box, Third, inCase);
		(DateTime First, DateTime Second) = laterLast ? (Recorded, Recorded.AddHours(1)) : (Recorded.AddHours(1), Recorded);
		StateFiles.WriteJson(MarkerFile(Box), new[] { NamedTwice(Third, Other, First, Second) });

		RebuildNeededEntry Entry = RebuildNeededFile.Load(Box.Context()).Entries.Single();

		CollectionAssert.AreEqual(new[] { Third }, Entry.Stamped.ToArray());
		CollectionAssert.AreEqual(new[] { Third }, Entry.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { "first" }, Entry.Problems.ToArray());
		(string Key, DateTime Time) = Entry.NotMarkedWritten.Single();
		Assert.AreEqual(Third, Key, ignoreCase: true);
		Assert.AreEqual(Recorded.AddHours(1), Time);
		Assert.IsTrue(Entry.NotMarkedWritten.ContainsKey(Third.ToLowerInvariant()), "keyed whatever the case");
	}

	[TestMethod]
	public void AMarkerLineMissingForAFileIsGivenOneAndTheFileStays()
	{
		// A marker edited by hand may give a file no line; what pairs a file with its line would drop that file with no evidence.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		string Fourth = OldFile(Path.Combine(Path.GetDirectoryName(Third)!, "Fourth.cpp"));
		StateFiles.WriteJson(MarkerFile(Box), new[]
		{
			new RebuildNeededEntry { Target = "GameEditor", Platform = "Win64", Configuration = "Development", NotMarked = [Third, Fourth], Problems = ["by hand"] },
		});

		RebuildNeededEntry Entry = RebuildNeededFile.Load(Box.Context()).Entries.Single();

		CollectionAssert.AreEqual(new[] { Third, Fourth }, Entry.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { "by hand", $"{Fourth} could not be marked for a recompile" }, Entry.Problems.ToArray());
		CollectionAssert.AreEqual(new[] { Third, Fourth }, RebuildNeeded.WhatStays(Entry, DateTime.UtcNow).NotMarked.ToArray());
	}

	[TestMethod]
	[DataRow(true, true, DisplayName = "in two cases, unsaved")]
	[DataRow(true, false, DisplayName = "in two cases, saved")]
	[DataRow(false, true, DisplayName = "in two spellings, unsaved")]
	[DataRow(false, false, DisplayName = "in two spellings, saved")]
	public async Task AWholeBuildOfAMarkerThatNamesOneFileTwiceThrowsNothingAndGoesByTheEvidence(bool inCase, bool unsaved)
	{
		// Two keys for one file threw on every build after UBT finished. Unsaved, its later time is past the file's own write, and
		// the file stays; the earlier time alone would let it go. Saved, both times are before its write, and it leaves.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		DateTime Later = unsaved ? DateTime.UtcNow.AddHours(1) : Recorded.AddHours(1);
		StateFiles.WriteJson(MarkerFile(Box), new[] { NamedTwice(Third, OtherSpelling(Box, Third, inCase), Recorded, Later) });

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		Assert.DoesNotContain("could not clear what", Box.Logger.Text);
		if (!unsaved)
		{
			Assert.IsFalse(File.Exists(MarkerFile(Box)), "saved since its recorded times, so it leaves: " + Box.Logger.Text);
			return;
		}
		RebuildNeededEntry Entry = RebuildNeededFile.Load(Box.Context()).Entries.Single();
		CollectionAssert.AreEqual(new[] { Third }, Entry.NotMarked.ToArray(), Box.Logger.Text);
		Assert.HasCount(1, Entry.NotMarkedWritten, "a time recorded again, at this build's end");
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public void WhatStaysOfAnEntryThatNamesOneFileTwiceNamesItOnce()
	{
		// An entry that wasn't loaded, as a journal's may be, is taken a file at a time all the same.
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		RebuildNeededEntry Entry = NamedTwice(Third, Third.ToUpperInvariant(), DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(1));

		BuildEditReport Stays = RebuildNeeded.WhatStays(Entry, DateTime.UtcNow);

		CollectionAssert.AreEqual(new[] { Third }, Stays.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { "first" }, Stays.Problems.ToArray());
		Assert.HasCount(1, Stays.Written);
	}

	[TestMethod]
	public void AnEntryMadeFromAReportThatNamesOneFileTwiceNamesItOnce()
	{
		// A report may come from a journal's entry, whose times are keyed by case: the entry made from it keeps the later time.
		using Sandbox Box = new();
		string Third = ThirdFile(Box);
		string Upper = Third.ToUpperInvariant();
		BuildEditReport Report = new([Third, Upper], [Upper, Third], ["first", "second"])
		{
			Written = new Dictionary<string, DateTime>(StringComparer.Ordinal) { [Upper] = Recorded.AddHours(1), [Third] = Recorded },
		};

		RebuildNeededEntry Entry = RebuildNeeded.EntryOf("GameEditor", "Win64", "Development", Report);

		CollectionAssert.AreEqual(new[] { Third }, Entry.Stamped.ToArray());
		CollectionAssert.AreEqual(new[] { Upper }, Entry.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { "first" }, Entry.Problems.ToArray());
		Assert.AreEqual(Recorded.AddHours(1), Entry.NotMarkedWritten.Single().Value);
		Assert.IsTrue(Entry.NotMarkedWritten.ContainsKey(Third), "keyed whatever the case");
	}

	[TestMethod]
	public void ATimeKeyedInAnotherCaseThanItsFileIsKeptWhenReportsAreAdded()
	{
		// A marker edited by hand may name a file in one case and key its time in another: what adds two targets' reports, as a
		// refusal does, keeps that time, else the refusal calls the file one with no recorded time (K10-F-5).
		using Sandbox Box = new();
		string Third = ThirdFile(Box);
		BuildEditReport Report = new([], [Third], ["could not be marked"])
		{
			Written = new Dictionary<string, DateTime>(StringComparer.Ordinal) { [Third.ToUpperInvariant()] = Recorded },
		};

		BuildEditReport Both = Report.Plus(new BuildEditReport(["Other.h"], [], []));

		Assert.AreEqual(Recorded, Both.Written[Third]);
	}

	[TestMethod]
	public void KeepingAnEditBesideAMarkerThatNamesOneFileTwiceThrowsNothing()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		StateFiles.WriteJson(MarkerFile(Box), new[] { NamedTwice(Third, Third.ToUpperInvariant(), DateTime.UtcNow.AddHours(1), Recorded) });

		Assert.IsTrue(RebuildNeeded.Keep(Box.Context(), "GameEditor", "Win64", "Development", new BuildEditReport([], [Third.ToUpperInvariant()], ["again"])), Box.Logger.Text);

		RebuildNeededEntry Entry = RebuildNeededFile.Load(Box.Context()).Entries.Single();
		Assert.AreEqual(Third, Entry.NotMarked.Single(), ignoreCase: true);
		CollectionAssert.AreEqual(new[] { "again" }, Entry.Problems.ToArray(), "the later build's word on it wins");
	}

	// ---- Nothing throws out of a build UBT finished (K9-F-2) ----

	[TestMethod]
	public async Task AMarkerABuildCantClearBecauseItCantTellTheEditorTargetStaysAndTheBuildSucceeds()
	{
		// A marker that holds no entries goes with a build of the target `uak test` uses, which two editor targets leave untold: the
		// build UBT finished succeeds, leaves the marker as it was, and says to delete it, as no build clears it (K10-F-3).
		using Sandbox Box = new();
		string Targets = Path.Combine(Box.ProjectDirectory, "Source");
		File.Delete(Path.Combine(Targets, "GameEditor.Target.cs"));
		foreach (string Name in new[] { "AEditor", "BEditor" })
		{
			string Target = Sandbox.Write(Path.Combine(Targets, Name + ".Target.cs"),
				$"public class {Name}Target : TargetRules {{ public {Name}Target(TargetInfo Target) : base(Target) {{ Type = TargetType.Editor; }} }}");
			File.SetLastWriteTimeUtc(Target, DateTime.UtcNow.AddMinutes(-5));
		}
		Sandbox.Write(MarkerFile(Box), "{ not json");
		byte[] Before = File.ReadAllBytes(MarkerFile(Box));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles()), "-target=AEditor"), Box.Logger.Text);

		CollectionAssert.AreEqual(Before, File.ReadAllBytes(MarkerFile(Box)), "the marker stays as it was");
		Assert.Contains($"build: {MarkerFile(Box)} can't be read, and the project's editor target can't be told (several editor targets in ", Box.Logger.Text);
		Assert.Contains($"), so no build clears it, and `uak test`, `uak mutate` and `uak lock run` go on refusing: delete {MarkerFile(Box)} once no build that"
			+ " overlapped an edit is left unbuilt", Box.Logger.Text);
		Assert.DoesNotContain("give -target=", Box.Logger.Text, "this build was given one, and no -target= clears it");
		Assert.DoesNotContain("could not clear what", Box.Logger.Text);
	}

	/// <summary>
	/// The services of a whole build that compiles, whose wait before the marks of what the marker keeps throws what .NET throws for
	/// ERROR_OPERATION_ABORTED (an <see cref="OperationCanceledException"/>), after it cancels <paramref name="cancel"/> when given.
	/// </summary>
	static UnrealServices AbortsTheMarks(CancellationTokenSource? cancel = null) => new()
	{
		Runner = BuildsAndCompiles(),
		Lock = new FakeLock(),
		HostPlatform = UnrealPlatform.Win64,
		GetEnvironmentVariable = _ => null,
		CaptureTree = TestSupportTrees.None,
		SetWriteTime = NoMark,
		WaitForCompiles = _ => Task.FromResult(true),
		Delay = _ =>
		{
			cancel?.Cancel();
			throw cancel is null ? new OperationCanceledException("The I/O operation has been aborted") : new OperationCanceledException(cancel.Token);
		},
	};

	[TestMethod]
	public async Task AnOperationAbortedWhileABuildClearsTheMarkerLeavesItAsItWasAndTheBuildSucceeds()
	{
		// .NET throws a bare OperationCanceledException for ERROR_OPERATION_ABORTED, which a file system or a share may give: no
		// cancel of the build, whose UBT succeeded (K10-F-2).
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		StateFiles.WriteJson(MarkerFile(Box), new[] { EntryOf(Third, DateTime.UtcNow.AddHours(1)) });
		byte[] Before = File.ReadAllBytes(MarkerFile(Box));

		Assert.AreEqual(0, await BuildAsync(Box, AbortsTheMarks()), Box.Logger.Text);

		CollectionAssert.AreEqual(Before, File.ReadAllBytes(MarkerFile(Box)), "the marker stays as it was");
		Assert.Contains($"build: could not clear what {MarkerFile(Box)} holds for GameEditor Win64 Development, so it stays as it was, and `uak test`, `uak mutate` and"
			+ $" `uak lock run` go on refusing: The I/O operation has been aborted; if no build can clear it, delete {MarkerFile(Box)}", Box.Logger.Text);
	}

	[TestMethod]
	public async Task ACancelOfTheBuildWhileItClearsTheMarkerIsACancel()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		StateFiles.WriteJson(MarkerFile(Box), new[] { EntryOf(Third, DateTime.UtcNow.AddHours(1)) });
		byte[] Before = File.ReadAllBytes(MarkerFile(Box));
		using CancellationTokenSource Cancel = new();

		await Assert.ThrowsAsync<OperationCanceledException>(() => new BuildCommand(AbortsTheMarks(Cancel)).RunAsync(Box.Context(), [], Cancel.Token));

		CollectionAssert.AreEqual(Before, File.ReadAllBytes(MarkerFile(Box)), "the marker stays as it was");
		Assert.DoesNotContain("could not clear what", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AMarkOfAPathNoFileCanHaveReportsItAsGoneAndThrowsNothing()
	{
		using Sandbox Box = new();
		string[] Bad = ["", "Bad\0Name.h"];

		BuildEditReport Report = await BuildEdits.MarkAsync(Box.Context(), Services(new FakeProcessRunner()), Bad);

		Assert.IsEmpty(Report.Stamped);
		CollectionAssert.AreEqual(Bad, Report.NotMarked.ToArray());
		Assert.IsTrue(Report.Problems.All(Problem => Problem.Contains("was deleted before it could be marked", StringComparison.Ordinal)), string.Join('\n', Report.Problems));
		Assert.IsEmpty(Report.Written, "a path no file can have has no time to show a save by");
	}

	[TestMethod]
	public void AFileOnADriveThatIsNotThereStays()
	{
		// A disconnected share, or a drive that is gone, says "not found" of every file on it: that is no sign the file is gone.
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("drive letters are Windows'");
			return;
		}
		HashSet<char> Used = [.. DriveInfo.GetDrives().Select(Drive => char.ToUpperInvariant(Drive.Name[0]))];
		char Free = "QRSTUVWXYZJKLMNOP".FirstOrDefault(Letter => !Used.Contains(Letter));
		if (Free == default)
		{
			Assert.Inconclusive("every drive letter is in use");
			return;
		}
		string Away = $@"{Free}:\Project\Source\Game\Private\Third.cpp";
		using Sandbox Box = new();

		Assert.IsFalse(RebuildNeededFile.IsGone(Away), "behind a root that can't be reached");
		Assert.IsTrue(RebuildNeededFile.IsGone(ThirdFile(Box)), "where the root answers, not found is gone");
		CollectionAssert.AreEqual(new[] { Away }, RebuildNeeded.WhatStays(EntryOf(Away, Recorded), Recorded.AddHours(1)).NotMarked.ToArray());
	}

	// ---- Only a file with a recorded time is told to save (K6-F-6) ----

	[TestMethod]
	public void ARefusalTellsAFileWithNoRecordedTimeNotToSaveButWhatClearsIt()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));
		KeepForEditor(Box, new BuildEditReport([], [Third], ["no mark"]));

		string Message = Assert.ThrowsExactly<UakRefusalException>(() => RebuildNeededFile.ThrowIfStanding(Box.Context())).Message;

		Assert.Contains($"not marked, with no recorded time, so a save shows nothing: a build whose mark takes clears it, else delete the marker: {Third}", Message);
		Assert.DoesNotContain("save first", Message);
	}

	[TestMethod]
	public void ARetrysRefusalTellsAFileWithNoRecordedTimeNotToSave()
	{
		using Sandbox Box = new();
		string Timed = Box.SourceFile;

		string Why = new BuildEditReport([], [Box.HeaderFile, Timed], ["one", "two"]) { Written = new Dictionary<string, DateTime> { [Timed] = Recorded } }.DescribeRetryOverlap();

		Assert.Contains($"({Timed} could not be marked for a recompile: save it again first)", Why);
		Assert.Contains($"({Box.HeaderFile} could not be marked for a recompile, and has no recorded time to show a save by: a build whose mark takes clears it, else delete {RebuildNeeded.FileName})", Why);
		Assert.DoesNotContain($"({Box.HeaderFile} could not be marked for a recompile: save it again first)", Why);
		Assert.Contains("source files changed while a build ran", Why);
	}

	// ---- Where a build that can't write the marker holds an overlap (K6-F-4, K6-F-8) ----

	[TestMethod]
	public async Task ABuildGivenArgumentsHoldsInTheJournalsWhatTheMarkerCanNotTake()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "K6A");
		string Third = OldFile(ThirdFile(Box));
		BlockMarker(Box);

		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Third), setWriteTime: NoMark), "--", "-Module=Game"), Box.Logger.Text);

		RebuildNeededEntry Held = Record(Box, "K6A").UnkeptOverlap!.Single();
		Assert.IsTrue(Held.Is("GameEditor", "Win64", "Development"));
		CollectionAssert.AreEqual(new[] { Third }, Held.NotMarked);
		Assert.Contains("The mutation journals stay", Box.Logger.Text);

		// A whole build that can write the marker hands it over, and it still refuses: nothing showed a save.
		UnblockMarker(Box);
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "K6A")), Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public async Task ACookedRunHoldsInTheJournalsWhatTheMarkerCanNotTakeAndSaysSo()
	{
		// A journal that refuses stops a cooked run before it starts, so this one is put back while UAT builds, as only a journal
		// the run let through can be there (K6-F-4).
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		Sandbox.Write(Box.Layout.RunUatScript, "");
		await LeavePendingAsync(Box, Patch, "K6B");
		string Journal = Path.GetDirectoryName(JournalFile(Box, "K6B"))!;
		string Aside = Path.Combine(Box.Root, "K6B-aside");
		Directory.Move(Journal, Aside);
		BlockMarker(Box);
		FakeProcessRunner Uat = new() { Behaviour = UatEdits(Box.HeaderFile, () => Directory.Move(Aside, Journal)) };

		Assert.AreEqual(0, await RunCookedAsync(Box, Services(Uat, setWriteTime: NoMark)), Box.Logger.Text);

		RebuildNeededEntry Held = Record(Box, "K6B").UnkeptOverlap!.Single();
		Assert.IsTrue(Held.Is("GameEditor", "Win64", "Development"), "the editor target's, as for the marker");
		CollectionAssert.AreEqual(new[] { Box.HeaderFile }, Held.NotMarked);
		Assert.Contains($"{Box.HeaderFile} could not be marked for a recompile, so no later build picks it up: {RebuildNeeded.FileName} couldn't take that, so the mutation journals hold it"
			+ " for GameEditor Win64 Development until a build that can write the marker takes it over, and `uak test` (cooked too)", Box.Logger.Text);
		Assert.DoesNotContain("recorded nowhere", Box.Logger.Text);
		Assert.DoesNotContain("that is kept for", Box.Logger.Text);
	}

	[TestMethod]
	public async Task ACookedRunThatCanHoldItNowhereSaysSo()
	{
		using Sandbox Box = new();
		Sandbox.Write(Box.Layout.RunUatScript, "");
		BlockMarker(Box);

		Assert.AreEqual(0, await RunCookedAsync(Box, Services(new FakeProcessRunner { Behaviour = UatEdits(Box.HeaderFile) }, setWriteTime: NoMark)), Box.Logger.Text);

		Assert.Contains($"No mutation journal stands to hold it either, so it is recorded nowhere and nothing will refuse for it: {Box.HeaderFile}", Box.Logger.Text);
		Assert.Contains($"{Box.HeaderFile} could not be marked for a recompile, so no later build picks it up: that is recorded nowhere, so nothing refuses for it", Box.Logger.Text);
		Assert.DoesNotContain("the mutation journals hold it", Box.Logger.Text);
	}

	[TestMethod]
	public async Task ACookedRunThatCouldNotWriteTheJournalsSaysItMayBeRecordedNowhere()
	{
		// The marker can't take it, and the journal that stands can't be written either: it may be held nowhere, and the note
		// mustn't say the journals hold it (K7-F-4). The journal is put back during UAT's build, as in the test above.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		Sandbox.Write(Box.Layout.RunUatScript, "");
		await LeavePendingAsync(Box, Patch, "K7U");
		string Journal = Path.GetDirectoryName(JournalFile(Box, "K7U"))!;
		string Aside = Path.Combine(Box.Root, "K7U-aside");
		Directory.Move(Journal, Aside);
		BlockMarker(Box);
		void PutBackUnwritable()
		{
			Directory.Move(Aside, Journal);
			File.SetAttributes(JournalFile(Box, "K7U"), FileAttributes.ReadOnly);
		}
		FakeProcessRunner Uat = new() { Behaviour = UatEdits(Box.HeaderFile, PutBackUnwritable) };

		int Exit;
		try
		{
			Exit = await RunCookedAsync(Box, Services(Uat, setWriteTime: NoMark));
		}
		finally
		{
			if (File.Exists(JournalFile(Box, "K7U")))
			{
				File.SetAttributes(JournalFile(Box, "K7U"), FileAttributes.Normal);
			}
		}

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.IsNull(Record(Box, "K7U").UnkeptOverlap, "the journal couldn't be written");
		Assert.Contains($"{Box.HeaderFile} could not be marked for a recompile, so no later build picks it up: {RebuildNeeded.FileName} couldn't take that, and the mutation"
			+ " journals couldn't all be written, so it may be recorded nowhere", Box.Logger.Text);
		Assert.DoesNotContain("the mutation journals hold it", Box.Logger.Text);
		Assert.DoesNotContain("recorded nowhere, so nothing refuses", Box.Logger.Text);
	}

	[TestMethod]
	[SupportedOSPlatform("windows")]
	public async Task ACookedRunTellsAFileWithNoRecordedTimeNotToSave()
	{
		// A file whose time can't be read once its mark failed (an ACL that denies reading its attributes, K6-F-6) has no
		// recorded time, so no save can show it: the note says what clears it instead.
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("an ACL that denies reading a file's attributes is Windows'");
			return;
		}
		using Sandbox Box = new();
		Sandbox.Write(Box.Layout.RunUatScript, "");
		SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
		FileSystemAccessRule NoAttributes = new(Me, FileSystemRights.ReadAttributes, AccessControlType.Deny);
		FileSystemAccessRule NoListing = new(Me, FileSystemRights.ListDirectory, AccessControlType.Deny);
		FileInfo Header = new(Box.HeaderFile);
		DirectoryInfo Folder = Header.Directory!;
		bool Denied = false;
		// The mark fails, and from then on the file's time can't be read, though the file is there.
		void DenyAndFail(string File, DateTime Time)
		{
			Denied = true;
			FileSecurity Security = Header.GetAccessControl();
			Security.AddAccessRule(NoAttributes);
			Header.SetAccessControl(Security);
			DirectorySecurity Listing = Folder.GetAccessControl();
			Listing.AddAccessRule(NoListing);
			Folder.SetAccessControl(Listing);
			throw new UnauthorizedAccessException($"no write-attributes access to {File}");
		}

		int Exit;
		string Text;
		try
		{
			Exit = await RunCookedAsync(Box, Services(new FakeProcessRunner { Behaviour = UatEdits(Box.HeaderFile) }, setWriteTime: DenyAndFail));
			Text = Box.Logger.Text;
			Assert.IsFalse(File.Exists(Box.HeaderFile), "a file whose time can't be read");
		}
		finally
		{
			if (Denied)
			{
				DirectorySecurity Listing = Folder.GetAccessControl();
				Listing.RemoveAccessRuleSpecific(NoListing);
				Folder.SetAccessControl(Listing);
				FileSecurity Security = Header.GetAccessControl();
				Security.RemoveAccessRuleSpecific(NoAttributes);
				Header.SetAccessControl(Security);
			}
		}

		Assert.AreEqual(0, Exit, Text);
		Assert.IsTrue(Denied, "the mark was tried");
		Assert.Contains($"{Box.HeaderFile} could not be marked for a recompile, so no later build picks it up: that is kept for ", Text);
		Assert.Contains($" Build the editor with `uak build` and the game with a cooked run ({Box.HeaderFile} has no recorded time to show a save by:"
			+ $" a build whose mark takes clears it, else delete {RebuildNeededFile.FileName}), before you test", Text);
		Assert.DoesNotContain("Save it, then build", Text);
		// The build's own line for it says the same (K7-F-2).
		Assert.Contains($"build: {Box.HeaderFile} changed while UBT ran, and could not be marked for a recompile (", Text);
		Assert.Contains($"), with no recorded time, so a save shows nothing: a build whose mark takes clears it; if none can, delete {RebuildNeededFile.FileName}", Text);
		Assert.DoesNotContain("save it again", Text);
	}

	[TestMethod]
	[DataRow(false, DisplayName = "a mark that doesn't take")]
	[DataRow(true, DisplayName = "marks that take")]
	public async Task ACookedRunInAProjectWithTwoEditorTargetsRecordsNowhereAndSaysSo(bool marks)
	{
		// No editor target can be told, so there is no entry to keep it under; a run whose marks all took has nothing to keep.
		using Sandbox Box = new();
		Sandbox.Write(Box.Layout.RunUatScript, "");
		string Targets = Path.Combine(Box.ProjectDirectory, "Source");
		File.Delete(Path.Combine(Targets, "GameEditor.Target.cs"));
		foreach (string Name in new[] { "AEditor", "BEditor" })
		{
			string Target = Sandbox.Write(Path.Combine(Targets, Name + ".Target.cs"),
				$"public class {Name}Target : TargetRules {{ public {Name}Target(TargetInfo Target) : base(Target) {{ Type = TargetType.Editor; }} }}");
			File.SetLastWriteTimeUtc(Target, DateTime.UtcNow.AddMinutes(-5));
		}
		UnrealServices Run = Services(new FakeProcessRunner { Behaviour = UatEdits(Box.HeaderFile) }, setWriteTime: marks ? null : new Action<string, DateTime>(NoMark));

		Assert.AreEqual(0, await RunCookedAsync(Box, Run), Box.Logger.Text);

		Assert.IsFalse(File.Exists(MarkerFile(Box)), Box.Logger.Text);
		if (marks)
		{
			Assert.DoesNotContain("Could not tell the project's editor target", Box.Logger.Text);
			Assert.DoesNotContain("recorded nowhere", Box.Logger.Text);
			return;
		}
		Assert.Contains($"Could not tell the project's editor target, so nothing records that {Box.HeaderFile} could not be marked", Box.Logger.Text);
		Assert.Contains($"{Box.HeaderFile} could not be marked for a recompile, so no later build picks it up: that is recorded nowhere, so nothing refuses for it", Box.Logger.Text);
	}

	// ---- What a build says of the marker (S-3, S-4, S-5) ----

	[TestMethod]
	public async Task ABuildThatKeepsAFileItCouldNotMarkSaysWhatRefusesAndWhatClearsIt()
	{
		using Sandbox Box = new();
		string Third = OldFile(ThirdFile(Box));

		Assert.AreEqual(0, await BuildAsync(Box, Services(EditsDuringBuild(Third), setWriteTime: NoMark)), Box.Logger.Text);

		Assert.Contains($"build: {Third} could not be marked, so {RebuildNeeded.FileName} keeps it for GameEditor Win64 Development: `uak test`, `uak mutate` and `uak lock run` refuse"
			+ " until a whole `uak build` of GameEditor Win64 Development (no UBT arguments) clears it", Box.Logger.Text);
		Assert.DoesNotContain("cleared what an earlier build left", Box.Logger.Text);

		// The next build words it as kept from that one, and says why its own compile lines don't clear it.
		int Before = Box.Logger.Entries.Count;
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles(), setWriteTime: NoMark)), Box.Logger.Text);

		string Later = LogSince(Box, Before);
		Assert.Contains($"build: {Third}, kept from an earlier build that overlapped its edit, still could not be marked for a recompile (", Later);
		// Each says the way out when no build can clear it (K9-F-4).
		Assert.Contains($"); if no build can clear it, delete {RebuildNeeded.FileName}", Later);
		Assert.Contains($"save {Third}, then run a whole build, which clears it whether or not the mark takes; if it can't be saved, delete {MarkerFile(Box)}", Later);
		Assert.Contains($"build: {Third} is kept from an earlier build that overlapped its edit and could not mark it. No build can prove it compiled the edit to a file it couldn't"
			+ " mark, so a compile of it in this build's log doesn't clear it", Later);
		Assert.DoesNotContain("changed while UBT ran", Later);
		Assert.DoesNotContain("could not be marked, so", Later);
		await AssertTestRefusedAsync(Box);
	}

	[TestMethod]
	public async Task AWholeBuildSaysWhatItClearedAndWhatStillStands()
	{
		using Sandbox Box = new();
		KeepForEditor(Box, new BuildEditReport(["A.h"], [], []));
		RebuildNeeded.Keep(Box.Context(), "Game", "Win64", "Shipping", new BuildEditReport(["B.h"], [], []));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		Assert.IsTrue(Box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Information && Entry.Message.Contains(
			$"build: cleared what an earlier build left for GameEditor Win64 Development in {RebuildNeeded.FileName}; what stands for Game Win64 Shipping still refuses",
			StringComparison.Ordinal)), Box.Logger.Text);

		// Nothing stood for it this time: nothing to say.
		int Before = Box.Logger.Entries.Count;
		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);
		Assert.DoesNotContain("cleared what an earlier build left", LogSince(Box, Before));
	}

	[TestMethod]
	public async Task AWholeBuildThatClearsTheLastEntrySaysSo()
	{
		using Sandbox Box = new();
		KeepForEditor(Box, new BuildEditReport(["A.h"], [], []));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles())), Box.Logger.Text);

		Assert.Contains($"build: cleared what an earlier build left for GameEditor Win64 Development in {RebuildNeeded.FileName}", Box.Logger.Text);
		Assert.DoesNotContain("still refuses", Box.Logger.Text);
	}

	[TestMethod]
	public async Task ABuildGivenArgumentsSaysItLeftTheEntryStanding()
	{
		using Sandbox Box = new();
		KeepForEditor(Box, new BuildEditReport(["A.h"], [], []));

		Assert.AreEqual(0, await BuildAsync(Box, Services(BuildsAndCompiles()), "--", "-Module=Game"), Box.Logger.Text);

		Assert.Contains($"build: {RebuildNeeded.FileName} still holds what an earlier build left for GameEditor Win64 Development, and a build given UBT arguments clears nothing:"
			+ " `uak test`, `uak mutate` and `uak lock run` refuse until a whole `uak build` of GameEditor Win64 Development (no UBT arguments) clears it", Box.Logger.Text);
		Assert.DoesNotContain("cleared what an earlier build left", Box.Logger.Text);
		await AssertTestRefusedAsync(Box);
	}

	// ---- What a refusal calls itself (S-2, K7 section 5) ----

	/// <summary>That the command logged a refusal, which the host prints with no pointer to the help, and no usage error.</summary>
	static void AssertLoggedAsARefusal(Sandbox box)
	{
		Assert.IsTrue(box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Error && Entry.Message.StartsWith("refused: ", StringComparison.Ordinal)), box.Logger.Text);
		Assert.IsFalse(box.Logger.Entries.Any(Entry => Entry.Message.StartsWith("usage: ", StringComparison.Ordinal)), box.Logger.Text);
		Assert.IsFalse(box.Logger.Entries.Any(Entry => Entry.Message.StartsWith("refused: refused", StringComparison.Ordinal)), box.Logger.Text);
	}

	[TestMethod]
	public async Task ARefusalIsLoggedAsARefusalNotAUsageError()
	{
		using Sandbox Box = new();
		KeepForEditor(Box, new BuildEditReport(["A.h"], [], []));

		await AssertTestRefusedAsync(Box);

		AssertLoggedAsARefusal(Box);
	}

	[TestMethod]
	public async Task AFileInTheWayOfThePatchIsARefusalThatSaysWhatClearsIt()
	{
		(Sandbox Box, string Patch) = WithPatch("--- /dev/null\n+++ b/Source/Game/Private/New.cpp\n@@ -0,0 +1 @@\n+int New;\n");
		using Sandbox _ = Box;
		string New = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "New.cpp"), "someone's own");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 0]) };

		Assert.AreEqual(UakExitCodes.UsageError, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=K7P", "-patch=" + Patch, "-filter=Project.Math"],
			CancellationToken.None), Box.Logger.Text);

		Assert.Contains($"refused: {New} exists, but the patch creates it: move or delete it first: nothing was changed", Box.Logger.Text);
		AssertLoggedAsARefusal(Box);
		Assert.AreEqual("someone's own", File.ReadAllText(New));
		Assert.IsEmpty(Runner.Invocations);
	}

	[TestMethod]
	public async Task AFileChangedWhileThePatchIsPreparedIsARefusal()
	{
		// The check was given right: an edit on disk stops it, which waiting for that edit and running it again clears, and its
		// help says nothing of (K8).
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "K8C", "p.diff", [Box.SourceFile]);
		File.WriteAllText(Box.SourceFile, "another agent's edit\n");

		MutationFileChangedException Refused = Assert.ThrowsExactly<MutationFileChangedException>(Journal.ThrowIfChangedSinceBegin);
		Assert.IsInstanceOfType<UakRefusalException>(Refused);
		Assert.Contains("Run the check again once that edit is done", Refused.Message);

		// As uak mutate's guard prints it: a refusal, with no pointer to the help.
		Assert.AreEqual(UakExitCodes.UsageError, await UnrealServices.GuardAsync(Box.Context(), () =>
		{
			Journal.ThrowIfChangedSinceBegin();
			return Task.FromResult(0);
		}));
		AssertLoggedAsARefusal(Box);
		Journal.Delete();
	}

	[TestMethod]
	public async Task AFileChangedWhileGitWritesThePatchIsARefusal()
	{
		// As one changed while the patch is prepared: waiting for that edit and running the check again clears it, and its help
		// says nothing of it (K9-F-5).
		using Sandbox Box = new();
		UakUsageException Stopped = MutateCommand.ChangedWhileGitWrote([Box.SourceFile]);
		Assert.IsInstanceOfType<UakRefusalException>(Stopped);
		Assert.AreEqual($"{Box.SourceFile} changed while git wrote the patch: no verdict. Run the check again once that edit is done", Stopped.Message);

		Assert.AreEqual(UakExitCodes.UsageError, await UnrealServices.GuardAsync(Box.Context(), () => throw Stopped));
		AssertLoggedAsARefusal(Box);
	}

	/// <summary>A logger that passes each line to <paramref name="inner"/>, after <paramref name="onLine"/> has seen it.</summary>
	sealed class WatchingLogger(ILogger inner, Action<string> onLine) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			onLine(formatter(state, exception));
			inner.Log(logLevel, eventId, state, exception, formatter);
		}
	}

	[TestMethod]
	public async Task AnEditThatGetsInWhileGitWritesThePatchAndIsUndoneBeforeTheRestoreStopsTheCheckAsARefusal()
	{
		// The check itself (K10-F-6): an edit gets in while git writes the patch, and is undone before the restore, so the files are
		// back, and the check stops with the refusal; an edit still there ends RESTORE FAILED first.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		using GitObjectsWritable __ = new(Box.ProjectDirectory);
		string Lf = Original.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n// end\n";
		File.WriteAllText(Box.SourceFile, Lf);
		await GitAsync(Box.ProjectDirectory, "init", "-q");
		await GitAsync(Box.ProjectDirectory, "add", "-A");
		await GitAsync(Box.ProjectDirectory, "-c", "user.name=UAK Tests", "-c", "user.email=uak-tests@example.invalid", "commit", "-q", "-m", "base");
		bool Undone = false;
		UakContext Context = new()
		{
			ProjectFile = new FileInfo(Box.ProjectFile),
			EngineRoot = new DirectoryInfo(Box.EngineRoot),
			StateDirectory = new DirectoryInfo(Box.StateDirectory),
			Logger = new WatchingLogger(Box.Logger, Line =>
			{
				if (!Undone && Line.Contains("an edit got in while git wrote the patch", StringComparison.Ordinal))
				{
					Undone = true;
					File.WriteAllText(Box.SourceFile, Lf);
				}
			}),
		};
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };
		UnrealServices Racing = new()
		{
			Runner = Runner,
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
			CaptureTree = TestSupportTrees.None,
			Capture = (Invocation, Token) =>
			{
				// Another agent's edit lands after uak's last look, before git reads the file.
				if (!Invocation.Arguments.Contains("--check") && !Invocation.Arguments.Contains("--numstat"))
				{
					File.AppendAllText(Box.SourceFile, "// agent B's edit\n");
				}
				return ProcessRunner.Default.CaptureAsync(Invocation, Token);
			},
		};

		int Exit = await new MutateCommand(Racing).RunAsync(Context, ["-name=GU", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.IsTrue(Undone, Box.Logger.Text);
		Assert.AreEqual(UakExitCodes.UsageError, Exit, Box.Logger.Text);
		Assert.Contains($"{Box.SourceFile} changed while git wrote the patch: no verdict. Run the check again once that edit is done", Box.Logger.Text);
		AssertLoggedAsARefusal(Box);
		Assert.AreEqual(Lf, File.ReadAllText(Box.SourceFile));
		Assert.IsEmpty(Runner.Invocations, "no verdict: nothing built or tested");
	}

	[TestMethod]
	public void AJournalOfThatNameLeftIsARefusal()
	{
		// Behind ThrowIfUnfinished, which refuses first, but a refusal all the same if anything reaches it.
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "K7J", 0, null);

		UakRefusalException Refused = Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.Begin(Box.Context(), "K7J", "patch", [Box.SourceFile]));

		Assert.Contains("a mutation journal named 'K7J' is left", Refused.Message);
	}

	// ---- uak mutate and a file a build that compiled nothing couldn't mark ----

	[TestMethod]
	public async Task AnUpToDateMutantBuildThatCouldNotMarkAnotherFileIsInconclusive()
	{
		// A build that compiled nothing reports only what it couldn't mark, and that is an edit the binaries may lack.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Third = OldFile(ThirdFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0, 0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Third, "another agent's edit, during the mutant build");
					return UpToDateLines;
				}
				return CompileLines;
			}),
		};

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, await new MutateCommand(Services(Runner, setWriteTime: NoMarkOf(Third))).RunAsync(Box.Context(),
			["-name=K6M", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.Contains($"mutate: INCONCLUSIVE: {Third} changed while the mutant build ran", Box.Logger.Text);
		using JsonDocument Result = ReadResult(ResultFile);
		CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditNotMarked"));
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
	}

	[TestMethod]
	public async Task ARetryThatCompiledNothingAndCouldNotMarkAFileRefusesTheTest()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		await LeavePendingAsync(Box, Patch, "K6R");
		string Third = OldFile(ThirdFile(Box));
		string ResultFile = Path.Combine(Box.Root, "result.json");
		SaveOtherFile(Box);
		FakeProcessRunner Later = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				File.WriteAllText(Third, "another agent's edit, during the retry");
				return UpToDateLines;
			}),
		};

		Assert.AreEqual(UakExitCodes.UsageError, await new TestCommand(Services(Later, setWriteTime: NoMarkOf(Third))).RunAsync(Box.Context(),
			["-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.HasCount(1, Later.Invocations, "the retry, and no test");
		using JsonDocument Result = ReadResult(ResultFile);
		CollectionAssert.AreEqual(new[] { Third }, Listed(Result, "EditNotMarked"));
		CollectionAssert.AreEqual(new[] { Third }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray(), "kept, for the commands after the journal goes");
	}
}
