// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal.Tests;

/// <summary>An edit made while UBT ran may be older than an object compiled from the old text.</summary>
[TestClass]
public sealed class BuildEditsTests
{
	const string ActionsStart = "Using Unreal Build Accelerator local executor to run 7 action(s)";

	static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);
	static readonly string[] Compiled = ["[1/2] Compile [x64] Foo.cpp", "[2/2] Link [x64] UnrealEditor-Game.dll", "Result: Succeeded"];
	static readonly string[] UpToDate = ["Target is up to date", "Result: Succeeded"];

	static UnrealServices Services(FakeProcessRunner runner, List<TimeSpan>? delays = null, Action<string, DateTime>? setWriteTime = null, Action? duringWait = null, Func<string, DateTime?>? probe = null) => new()
	{
		SetWriteTime = setWriteTime ?? File.SetLastWriteTimeUtc,
		ProbeFileTime = probe ?? MutationJournal.ProbeFileTime,
		Runner = runner,
		Lock = new FakeLock(),
		HostPlatform = UnrealPlatform.Win64,
		GetEnvironmentVariable = _ => null,
		CaptureTree = TestSupportTrees.None,
		RecompileMargin = Margin,
		Delay = Wait =>
		{
			delays?.Add(Wait);
			duringWait?.Invoke();
			return Task.CompletedTask;
		},
	};

	/// <summary>A UBT that runs <paramref name="during"/>, prints <paramref name="lines"/>, writes an object and exits.</summary>
	static Func<ProcessInvocation, Action<string>, int> Builds(Sandbox box, Action during, int exitCode = 0, string[]? lines = null) => (_, Output) =>
	{
		during();
		foreach (string Line in lines ?? Compiled)
		{
			Output(Line);
		}
		Sandbox.Write(Object(box), "from the old text");
		return exitCode;
	};

	static string Object(Sandbox box) => Path.Combine(box.ProjectDirectory, "Intermediate", "Build", "Foo.obj");

	/// <summary>A file's path as a project names it for <see cref="ProjectSettings.SetBuildWrites"/>.</summary>
	static string FromProject(Sandbox box, string file) => Path.GetRelativePath(box.ProjectDirectory, file).Replace('\\', '/');

	/// <summary>The files as they were before anyone edited them.</summary>
	static void Age(params string[] files)
	{
		foreach (string File in files)
		{
			System.IO.File.SetLastWriteTimeUtc(File, DateTime.UtcNow.AddHours(-1));
		}
	}

	[TestMethod]
	public async Task AFileEditedDuringABuildEndsUpNewerThanWhatTheBuildWrote()
	{
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		DateTime Untouched = File.GetLastWriteTimeUtc(Box.SourceFile);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		List<TimeSpan> Delays = [];
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Box.HeaderFile, "// edited\n")) };

		Assert.AreEqual(0, await new BuildCommand(Services(Runner, Delays)).RunAsync(Box.Context(), ["-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		TimeSpan Ahead = File.GetLastWriteTimeUtc(Box.HeaderFile) - File.GetLastWriteTimeUtc(Object(Box));
		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, Ahead, "the next build must take the object as out of date");
		Assert.AreEqual("// edited\n", File.ReadAllText(Box.HeaderFile), "only the time changes");
		Assert.AreEqual(Untouched, File.GetLastWriteTimeUtc(Box.SourceFile), "a file nobody edited needs no recompile");
		Assert.AreEqual(Margin, Delays.Single(), "so the stamp isn't in the future by the time the next build runs");
		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		Assert.Contains($"{Box.HeaderFile}: build again before you test", Box.Logger.Text);
		Assert.Contains("A file the build writes itself is no edit: name it with `uak test config -build-writes=<path>`", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual(Box.HeaderFile, Result.RootElement.GetProperty("EditedDuringBuild")[0].GetString());
		Assert.AreEqual(0, Result.RootElement.GetProperty("EditNotMarked").GetArrayLength());
	}

	[TestMethod]
	public async Task ABuildNobodyEditedDuringChangesNothingAndWaitsForNothing()
	{
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		DateTime Before = File.GetLastWriteTimeUtc(Box.HeaderFile);
		List<TimeSpan> Delays = [];
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => { }) };

		Assert.AreEqual(0, await new BuildCommand(Services(Runner, Delays)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(Before, File.GetLastWriteTimeUtc(Box.HeaderFile));
		Assert.IsEmpty(Delays);
		Assert.DoesNotContain("changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AFailedBuildMarksItsEditsToo()
	{
		// As in the build that found this: units that compiled before the failure stay, with the old header in them.
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Box.HeaderFile, "// edited\n"), exitCode: 1) };

		Assert.AreEqual(1, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.HeaderFile) - File.GetLastWriteTimeUtc(Object(Box)));
		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	public async Task ABuildThatCompiledNothingMarksItsEditsAndSaysNothing()
	{
		// UBT rewrites UHT's timestamps even then, so the edit can be older than them; but its binaries mix nothing.
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Box.HeaderFile, "// edited\n"), lines: UpToDate) };

		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), ["-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.HeaderFile) - File.GetLastWriteTimeUtc(Object(Box)), "newer than the Timestamp the run wrote");
		Assert.DoesNotContain("changed while UBT ran", Box.Logger.Text);
		AssertListed(ResultFile, "EditedDuringBuild");
		AssertListed(ResultFile, "EditNotMarked");
	}

	[TestMethod]
	public async Task AFileAnUpToDateBuildCouldNotMarkIsReportedAndKept()
	{
		// UHT may have passed it by, behind the Timestamp this build wrote, and no later build picks it up (K5-F-3).
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Box.HeaderFile, "// edited\n"), lines: UpToDate) };

		Assert.AreEqual(0, await new BuildCommand(Services(Runner, setWriteTime: (_, _) => { })).RunAsync(Box.Context(), ["-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.IsTrue(Box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Warning && Entry.Message.Contains($"{Box.HeaderFile} changed while UBT ran, and could not be marked", StringComparison.Ordinal)), Box.Logger.Text);
		Assert.DoesNotContain("1 file changed while UBT ran", Box.Logger.Text);
		AssertListed(ResultFile, "EditedDuringBuild", Box.HeaderFile);
		AssertListed(ResultFile, "EditNotMarked", Box.HeaderFile);
		CollectionAssert.AreEqual(new[] { Box.HeaderFile }, RebuildNeeded.Read(Box.Context()).NotMarked.ToArray());
	}

	[TestMethod]
	public async Task AFileWrittenAtTheWindowsFirstInstantCounts()
	{
		// The window opens by the file system's clock, and a save in that very tick may follow UBT's read of the file (K5-F-4).
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		DateTime Opened = DateTime.UtcNow.AddSeconds(-30);
		File.SetLastWriteTimeUtc(Box.HeaderFile, Opened);
		UnrealServices AtOpened = Services(new FakeProcessRunner(), probe: _ => Opened);
		BuildEditWindow Window = BuildEditWindow.Open(Box.Context(), AtOpened);
		Assert.AreEqual(Opened, Window.Opened);

		BuildEditReport Report = await BuildEdits.StampAsync(Box.Context(), AtOpened, new Dictionary<string, DateTime>(), Window);

		CollectionAssert.AreEqual(new[] { Box.HeaderFile }, Report.Stamped.ToArray());
	}

	[TestMethod]
	public async Task OnlyWhatABuildCompilesCounts()
	{
		using Sandbox Box = new();
		string InPlugin = Path.Combine(Box.ProjectDirectory, "Plugins", "Tool", "Source", "Tool", "Tool.h");
		string BuildFile = Path.Combine(Box.ProjectDirectory, "Plugins", "Tool", "Source", "Tool", "Tool.Build.cs");
		string InContentFolder = Path.Combine(Box.ProjectDirectory, "Plugins", "Tool", "Source", "Tool", "Private", "Content", "Inner.h");
		string InModuleContent = Path.Combine(Box.ProjectDirectory, "Source", "Mod", "Private", "Content", "Inner.cpp");
		string Asset = Path.Combine(Box.ProjectDirectory, "Plugins", "Tool", "Content", "Python", "tool.h");
		string Generated = Path.Combine(Box.ProjectDirectory, "Plugins", "Tool", "Intermediate", "Build", "Tool.generated.h");
		string Note = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Notes.txt");
		string Outside = Path.Combine(Box.ProjectDirectory, "Config", "Foo.h");
		string Descriptor = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Tool", "Tool.uplugin"), "{}");
		string[] Others = [Asset, Generated, Note, Outside];
		string[] Counted = [InPlugin, BuildFile, InContentFolder, InModuleContent];
		foreach (string File in Others.Concat(Counted))
		{
			Sandbox.Write(File, "");
		}
		Age([.. Others, .. Counted, Descriptor, Box.SourceFile, Box.HeaderFile]);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Builds(Box, () =>
			{
				foreach (string File in Others.Concat(Counted))
				{
					System.IO.File.WriteAllText(File, "edited");
				}
			}),
		};

		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		foreach (string File in Counted)
		{
			Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, System.IO.File.GetLastWriteTimeUtc(File) - System.IO.File.GetLastWriteTimeUtc(Object(Box)), File);
		}
		foreach (string File in Others)
		{
			Assert.IsLessThan(TimeSpan.FromSeconds(1), System.IO.File.GetLastWriteTimeUtc(File) - System.IO.File.GetLastWriteTimeUtc(Object(Box)), File);
		}
		Assert.Contains("4 files changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AnEditPastTheStampIsLeftAsItIs()
	{
		using Sandbox Box = new();
		DateTime Later = DateTime.UtcNow.AddHours(1);
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.SetLastWriteTimeUtc(Box.HeaderFile, Later)) };

		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(Later, File.GetLastWriteTimeUtc(Box.HeaderFile), "a stamp only ever raises a time");
	}

	[TestMethod]
	public async Task AFileThatCantBeMarkedIsReportedAndStopsNothing()
	{
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Builds(Box, () =>
			{
				File.WriteAllText(Box.HeaderFile, "// edited\n");
				File.WriteAllText(Box.SourceFile, "// edited\n");
			}),
		};
		void SetWriteTime(string Path, DateTime Time)
		{
			if (Path == Box.SourceFile)
			{
				throw new IOException("in use");
			}
			File.SetLastWriteTimeUtc(Path, Time);
		}
		string ResultFile = Path.Combine(Box.Root, "result.json");

		Assert.AreEqual(0, await new BuildCommand(Services(Runner, setWriteTime: SetWriteTime)).RunAsync(Box.Context(), ["-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		Assert.Contains($"{Box.SourceFile} changed while UBT ran, and could not be marked for a recompile", Box.Logger.Text);
		Assert.Contains("could not be marked for a recompile (in use): save it again before the next whole build", Box.Logger.Text);
		Assert.DoesNotContain("delete its objects", Box.Logger.Text);
		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.HeaderFile) - File.GetLastWriteTimeUtc(Object(Box)), "the others are marked all the same");
		AssertListed(ResultFile, "EditNotMarked", Box.SourceFile);
		AssertListed(ResultFile, "EditedDuringBuild", Box.HeaderFile, Box.SourceFile);
	}

	static void AssertListed(string resultFile, string property, params string[] files)
	{
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(resultFile));
		CollectionAssert.AreEquivalent(files, Result.RootElement.GetProperty(property).EnumerateArray().Select(File => File.GetString()).ToArray());
	}

	[TestMethod]
	public async Task ATimeThatDidNotTakeIsNoMark()
	{
		// A share that says yes and keeps the old time: only reading it back shows that nothing makes the next build recompile it.
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Box.HeaderFile, "// edited\n")) };

		Assert.AreEqual(0, await new BuildCommand(Services(Runner, setWriteTime: (_, _) => { })).RunAsync(Box.Context(), ["-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.DoesNotContain("1 file changed while UBT ran", Box.Logger.Text);
		Assert.Contains($"{Box.HeaderFile} changed while UBT ran, and could not be marked for a recompile (its write time is ", Box.Logger.Text);
		AssertListed(ResultFile, "EditNotMarked", Box.HeaderFile);
		AssertListed(ResultFile, "EditedDuringBuild", Box.HeaderFile);
	}

	[TestMethod]
	public async Task AnOperationAbortedWhileAnEditIsMarkedLeavesItUnmarkedAndTheBuildSucceeds()
	{
		// .NET throws a bare OperationCanceledException for ERROR_OPERATION_ABORTED, which a share may give for a mark or a probe: no
		// cancel of the build, whose UBT succeeded, so the file is one that couldn't be marked (K10-F-2).
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Box.HeaderFile, "// edited\n")) };
		static OperationCanceledException Aborted() => new("The I/O operation has been aborted");
		DateTime? ProbeAborts(string Probed) => Probed == Box.HeaderFile ? throw Aborted() : MutationJournal.ProbeFileTime(Probed);

		Assert.AreEqual(0, await new BuildCommand(Services(Runner, setWriteTime: (_, _) => throw Aborted(), probe: ProbeAborts)).RunAsync(Box.Context(), ["-resultfile=" + ResultFile],
			CancellationToken.None), Box.Logger.Text);

		Assert.Contains($"{Box.HeaderFile} changed while UBT ran, and could not be marked for a recompile (The I/O operation has been aborted): save it again before the next whole build",
			Box.Logger.Text);
		AssertListed(ResultFile, "EditNotMarked", Box.HeaderFile);
	}

	[TestMethod]
	public async Task AnEditBeforeUbtsFirstActionIsAnEdit()
	{
		// UBT may have passed another unit as up to date on the old text, and UHT writes its Timestamp after it looked: the next build wouldn't see this one.
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Builds(Box, () =>
			{
				File.WriteAllText(Box.HeaderFile, "// edited\n");
				Thread.Sleep(50);
			}, lines: [ActionsStart, .. Compiled]),
		};

		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.HeaderFile) - File.GetLastWriteTimeUtc(Object(Box)));
		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AFileTheProjectNamesIsNoEdit()
	{
		// What a pre-build step writes each build: it would warn for every build, and leave no mutation a verdict.
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		ProjectSettings.SetBuildWrites(Box.Context(), [FromProject(Box, Box.HeaderFile)]);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		List<TimeSpan> Delays = [];
		DateTime Written = default;
		FakeProcessRunner Runner = new()
		{
			Behaviour = Builds(Box, () =>
			{
				File.WriteAllText(Box.HeaderFile, "// generated by a pre-build step\n");
				Written = File.GetLastWriteTimeUtc(Box.HeaderFile);
			}),
		};

		Assert.AreEqual(0, await new BuildCommand(Services(Runner, Delays)).RunAsync(Box.Context(), ["-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(Written, File.GetLastWriteTimeUtc(Box.HeaderFile), "not stamped");
		Assert.DoesNotContain("changed while UBT ran", Box.Logger.Text);
		Assert.IsEmpty(Delays);
		AssertListed(ResultFile, "EditedDuringBuild");
		AssertListed(ResultFile, "EditNotMarked");
	}

	[TestMethod]
	public async Task ANamedFileLeavesEveryOtherFileAnEdit()
	{
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		ProjectSettings.SetBuildWrites(Box.Context(), [FromProject(Box, Box.HeaderFile)]);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Builds(Box, () =>
			{
				File.WriteAllText(Box.HeaderFile, "// generated by a pre-build step\n");
				File.WriteAllText(Box.SourceFile, "// edited\n");
			}),
		};

		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.SourceFile) - File.GetLastWriteTimeUtc(Object(Box)));
	}

	[TestMethod]
	public async Task SettingsThatCantBeReadLeaveEveryFileAnEdit()
	{
		// It costs a recompile, where refusing would throw away a build that has run.
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		Sandbox.Write(ProjectSettings.GetFile(Box.Context()), "{ not json");
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Box.HeaderFile, "// edited\n")) };

		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("Could not read which files a build writes itself", Box.Logger.Text);
		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	public async Task SettingsOfTheWrongShapeLeaveEveryFileAnEdit()
	{
		// JSON, but "build" isn't an object: indexing it throws what the stamp, after a build that has run, must never see.
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		Sandbox.Write(ProjectSettings.GetFile(Box.Context()), """{ "build": 1, "test": 2 }""");
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Box.HeaderFile, "// edited\n")) };

		Assert.IsEmpty(ProjectSettings.GetBuildWrites(Box.Context()));
		Assert.IsNull(ProjectSettings.GetCookedMaps(Box.Context()));
		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AnyWayOfFailingToReadTheSettingsLeavesEveryFileAnEdit()
	{
		// A path the system refuses only shows when it's resolved, as the stamp does, after a build that has run.
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		Sandbox.Write(ProjectSettings.GetFile(Box.Context()), """{ "build": { "writes": ["Source/\u0000.h"] } }""");
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Box.HeaderFile, "// edited\n")) };

		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("Could not read which files a build writes itself", Box.Logger.Text);
		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
	}

	[TestMethod]
	public void AFileTheProjectNamesInAnotherCaseIsLeftOutWhereCaseDoesntCount()
	{
		// As the file system has it: on Windows that's the file; elsewhere it's another, which the build didn't write.
		using Sandbox Box = new();
		Dictionary<string, DateTime> Managed = new(StringComparer.OrdinalIgnoreCase);
		File.WriteAllText(Box.HeaderFile, "// written\n");
		Assert.Contains(Box.HeaderFile, BuildEdits.Find(Box.Context(), Managed, DateTime.UtcNow.AddMinutes(-1)));
		ProjectSettings.SetBuildWrites(Box.Context(), [FromProject(Box, Box.HeaderFile).ToUpperInvariant()]);

		List<string> Found = BuildEdits.Find(Box.Context(), Managed, DateTime.UtcNow.AddMinutes(-1));

		Assert.AreEqual(!OperatingSystem.IsWindows(), Found.Contains(Box.HeaderFile));
	}

	[TestMethod]
	public void ClosingAWindowAgainKeepsItsEnd()
	{
		// A cooked run closes it as the build step ends, and again as UAT does.
		using Sandbox Box = new();
		BuildEditWindow Window = BuildEditWindow.Open(Box.Context(), Services(new FakeProcessRunner()));
		DateTime Closed = Window.Close();
		Thread.Sleep(20);

		Assert.AreEqual(Closed, Window.Close());
	}

	[TestMethod]
	public void TheWindowOpensByTheFileSystemsClockWhenThatIsBehind()
	{
		using Sandbox Box = new();
		DateTime Behind = DateTime.UtcNow.AddMinutes(-3);
		UnrealServices FileServer = new() { Lock = new FakeLock(), ProbeFileTime = _ => Behind };

		Assert.AreEqual(Behind, BuildEditWindow.Open(Box.Context(), FileServer).Opened);
	}

	[TestMethod]
	public async Task AFileDeletedBeforeItsStampIsNotOneToSaveAgain()
	{
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		string Gone = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Gone.cpp"), "");
		Age(Gone);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.WriteAllText(Gone, "edited")) };

		Assert.AreEqual(0, await new BuildCommand(Services(Runner, duringWait: () => File.Delete(Gone))).RunAsync(Box.Context(), ["-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		Assert.Contains($"{Gone} changed while UBT ran, and was deleted before it could be marked for a recompile, so there is nothing to mark: the next build recompiles what used it", Box.Logger.Text);
		Assert.DoesNotContain("save it again", Box.Logger.Text);
		AssertListed(ResultFile, "EditNotMarked", Gone);
		AssertListed(ResultFile, "EditedDuringBuild", Gone);
	}

	[TestMethod]
	public void ARefusalAsksToSaveWhatIsThereAndNotWhatIsGone()
	{
		using Sandbox Box = new();
		string Gone = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Gone.cpp");

		string Why = new BuildEditReport([], [Box.HeaderFile, Gone], ["one", "two"]) { Written = new Dictionary<string, DateTime> { [Box.HeaderFile] = DateTime.UtcNow } }.DescribeRetryOverlap();

		Assert.Contains($"({Box.HeaderFile} could not be marked for a recompile: save it again first)", Why);
		Assert.Contains($"({Gone} was deleted, so there is nothing to mark: the next build recompiles what used it", Why);
	}

	[TestMethod]
	public void ALaterReportWinsForAFileBothName()
	{
		// A stamp only counts against what was built before it: the next target's build wrote objects past that stamp.
		BuildEditReport Marked = new(["A.h"], [], []);
		BuildEditReport Unmarked = new([], ["A.h"], ["why not"]);

		BuildEditReport Broken = Marked.Plus(Unmarked);
		BuildEditReport Mended = Unmarked.Plus(Marked);

		Assert.IsEmpty(Broken.Stamped);
		CollectionAssert.AreEqual(new[] { "A.h" }, Broken.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { "why not" }, Broken.Problems.ToArray());
		CollectionAssert.AreEqual(new[] { "A.h" }, Mended.Stamped.ToArray());
		Assert.IsEmpty(Mended.NotMarked);
		Assert.IsEmpty(Mended.Problems, "a line for each file that isn't marked");
	}

	[TestMethod]
	public void TwoReportsKeepWhatOnlyOneNames()
	{
		BuildEditReport Both = new BuildEditReport(["A.h"], ["B.h"], ["b"]).Plus(new BuildEditReport(["C.h"], ["D.h"], ["d"]));

		CollectionAssert.AreEqual(new[] { "A.h", "C.h" }, Both.Stamped.ToArray());
		CollectionAssert.AreEqual(new[] { "B.h", "D.h" }, Both.NotMarked.ToArray());
		CollectionAssert.AreEqual(new[] { "b", "d" }, Both.Problems.ToArray());
	}

	[TestMethod]
	public async Task AFileDatedPastTheBuildIsNoEditOfThatBuildOrTheNext()
	{
		// A clock set wrong or a copy with its date: it would count as an edit of every build from now until that date.
		using Sandbox Box = new();
		Age(Box.SourceFile);
		DateTime Later = DateTime.UtcNow.AddHours(1);
		File.SetLastWriteTimeUtc(Box.HeaderFile, Later);
		List<TimeSpan> Delays = [];
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => { }) };

		for (int Build = 0; Build < 2; Build++)
		{
			Assert.AreEqual(0, await new BuildCommand(Services(Runner, Delays)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);
		}

		Assert.HasCount(2, Runner.Invocations);
		Assert.AreEqual(Later, File.GetLastWriteTimeUtc(Box.HeaderFile));
		Assert.DoesNotContain("changed while UBT ran", Box.Logger.Text);
		Assert.IsEmpty(Delays);
	}

	[TestMethod]
	public async Task AFileOnAServerAheadOfTheClockStillCounts()
	{
		// The file's time is the server's, ahead of this machine: only the probe's time says it fell inside the build.
		using Sandbox Box = new();
		Age(Box.SourceFile, Box.HeaderFile);
		DateTime Server = DateTime.UtcNow.AddMinutes(5);
		FakeProcessRunner Runner = new() { Behaviour = Builds(Box, () => File.SetLastWriteTimeUtc(Box.HeaderFile, Server)) };
		UnrealServices Ahead = new()
		{
			Runner = Runner,
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
			CaptureTree = TestSupportTrees.None,
			Delay = _ => Task.CompletedTask,
			ProbeFileTime = Path => Path.EndsWith("uak-build-end", StringComparison.Ordinal) ? Server.AddSeconds(1) : MutationJournal.ProbeFileTime(Path),
		};

		Assert.AreEqual(0, await new BuildCommand(Ahead).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("1 file changed while UBT ran", Box.Logger.Text);
		Assert.IsGreaterThan(Server.AddSeconds(1), File.GetLastWriteTimeUtc(Box.HeaderFile), "past what the server dated the build's last object");
	}

	/// <summary>A junction (Windows) or a symbolic link, gone before the sandbox is: a recursive delete can't take a junction.</summary>
	sealed class Link : IDisposable
	{
		readonly string _path;

		public Link(string path, string target)
		{
			_path = path;
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			if (!OperatingSystem.IsWindows())
			{
				Directory.CreateSymbolicLink(path, target);
				return;
			}
			ProcessStartInfo Info = new("cmd.exe", ["/d", "/c", "mklink", "/J", path, target]) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
			using Process Junction = Process.Start(Info)!;
			Junction.StandardOutput.ReadToEnd();
			Junction.WaitForExit();
			Assert.AreEqual(0, Junction.ExitCode);
		}

		public void Dispose() => Directory.Delete(_path);
	}

	[TestMethod]
	public void AJunctionBackUpTheTreeEndsTheWalk()
	{
		using Sandbox Box = new();
		string Plugins = Path.Combine(Box.ProjectDirectory, "Plugins");
		string Header = Sandbox.Write(Path.Combine(Plugins, "Tool", "Source", "Tool.h"), "");
		using Link Back = new(Path.Combine(Plugins, "Tool", "Source", "Back"), Plugins);

		List<string> Found = BuildEdits.Find(Box.Context(), [], DateTime.UtcNow.AddMinutes(-1));

		CollectionAssert.AreEqual(new[] { Header }, Found);
	}

	[TestMethod]
	public void AJunctionedPluginIsStillLookedIn()
	{
		using Sandbox Box = new();
		string Elsewhere = Path.Combine(Box.Root, "Shared", "Tool");
		Sandbox.Write(Path.Combine(Elsewhere, "Source", "Tool.h"), "");
		string Plugin = Path.Combine(Box.ProjectDirectory, "Plugins", "Tool");
		using Link Junction = new(Plugin, Elsewhere);

		List<string> Found = BuildEdits.Find(Box.Context(), [], DateTime.UtcNow.AddMinutes(-1));

		CollectionAssert.AreEqual(new[] { Path.Combine(Plugin, "Source", "Tool.h") }, Found);
	}

	[TestMethod]
	public void TwoJunctionsToOneFolderFindItsFilesOnce()
	{
		using Sandbox Box = new();
		string Elsewhere = Path.Combine(Box.Root, "Shared", "Tool");
		Sandbox.Write(Path.Combine(Elsewhere, "Source", "Tool.h"), "");
		using Link One = new(Path.Combine(Box.ProjectDirectory, "Plugins", "One"), Elsewhere);
		using Link Two = new(Path.Combine(Box.ProjectDirectory, "Plugins", "Two"), Elsewhere);

		Assert.HasCount(1, BuildEdits.Find(Box.Context(), [], DateTime.UtcNow.AddMinutes(-1)));
	}

	[TestMethod]
	public async Task AWarningNamesTenFilesAndCountsTheRest()
	{
		using Sandbox Box = new();
		string[] Files = [.. Enumerable.Range(0, 12).Select(Index => Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", $"File{Index:00}.cpp"))];
		FakeProcessRunner Runner = new()
		{
			Behaviour = Builds(Box, () =>
			{
				foreach (string File in Files)
				{
					System.IO.File.WriteAllText(File, "edited");
				}
			}),
		};

		Assert.AreEqual(0, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("12 files changed while UBT ran", Box.Logger.Text);
		Assert.Contains("and 2 more", Box.Logger.Text);
		Assert.Contains("File00.cpp", Box.Logger.Text);
		Assert.DoesNotContain("File11.cpp", Box.Logger.Text);
	}
}
