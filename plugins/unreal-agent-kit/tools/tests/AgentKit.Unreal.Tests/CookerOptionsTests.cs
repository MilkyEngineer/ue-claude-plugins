// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal.Tests;

/// <summary><c>uak test -cooked -cooker-options=</c> and its saved default: the cooker arguments UAT gets through -cookdir= and -AdditionalCookerOptions.</summary>
[TestClass]
public sealed class CookerOptionsTests
{
	static UnrealServices Services(FakeProcessRunner runner, FakeLock? editorLock = null) => new()
	{
		Runner = runner,
		Lock = editorLock ?? new FakeLock(),
		HostPlatform = UnrealPlatform.Win64,
		GetEnvironmentVariable = _ => null,
		CaptureTree = TestSupportTrees.None,
	};

	static Sandbox WithRunUat()
	{
		Sandbox Box = new();
		Sandbox.Write(Box.Layout.RunUatScript, "");
		return Box;
	}

	static readonly string[] RunOutput =
	[
		"********** RUN COMMAND STARTED **********",
		"Client exited with error code: 0 (see the client log for more info)",
		"********** RUN COMMAND COMPLETED **********",
	];

	/// <summary>UAT as the fake runs it: the game passes its one test.</summary>
	static FakeProcessRunner PassingUat() => new()
	{
		Behaviour = (Invocation, OnLine) =>
		{
			foreach (string Line in RunOutput)
			{
				OnLine(Line);
			}
			string Folder = Invocation.Environment![CookedTestCommandLine.LogFolderVariable]!;
			File.WriteAllLines(CookedTestCommandLine.ClientLogFile(Folder), Fixtures.Lines("cooked-passed.log"));
			return 0;
		},
	};

	/// <summary>Two folders that exist, so no -COOKDIR= warning is printed for them.</summary>
	static (string A, string B) CookDirs(Sandbox box)
	{
		string A = Directory.CreateDirectory(Path.Combine(box.ProjectDirectory, "Plugins", "Tests", "Content", "Golden")).FullName;
		string B = Directory.CreateDirectory(Path.Combine(box.ProjectDirectory, "Plugins", "Tests", "Content", "RenameSlots")).FullName;
		return (A, B);
	}

	static string? CookerArgument(ProcessInvocation invocation) =>
		invocation.Arguments.SingleOrDefault(Argument => Argument.StartsWith(CookedTestCommandLine.AdditionalCookerOptionsPrefix, StringComparison.Ordinal));

	static string? CookDirArgument(ProcessInvocation invocation) =>
		invocation.Arguments.SingleOrDefault(Argument => Argument.StartsWith(CookedTestCommandLine.CookDirListPrefix, StringComparison.Ordinal));

	/// <summary>The run's host is Win64 here (<see cref="Services"/>), so a test that types full paths of this machine needs Windows ones.</summary>
	static void NeedsWindows()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("The run's host is Win64 here, so the full paths it types must be Windows ones.");
		}
	}

	/// <summary>A typed -COOKDIR= can't hold a space, so a test that types a sandbox folder needs a temp path without one.</summary>
	static void NeedsSpaceFree(Sandbox box)
	{
		if (box.Root.Any(char.IsWhiteSpace))
		{
			Assert.Inconclusive("The temp path holds a space, which a typed -COOKDIR= can't: " + box.Root);
		}
	}

	static JsonElement ReadResult(string file)
	{
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(file));
		return Result.RootElement.Clone();
	}

	static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(Element => Element.GetString()!)];

	// ---- parsing and checks ----

	[TestMethod]
	public void CookerOptionsAreSwitchesSeparatedBySpaces()
	{
		CollectionAssert.AreEqual(new[] { "-COOKDIR=/A", "-COOKDIR=/B" }, CookedTestCommandLine.ParseCookerOptions("-COOKDIR=/A -COOKDIR=/B", "-cooker-options").ToArray());
		CollectionAssert.AreEqual(new[] { "-A", "-B=1" }, CookedTestCommandLine.ParseCookerOptions("  -A   -B=1 ", "-cooker-options").ToArray());
		CollectionAssert.AreEqual(new[] { "-ini:Engine:[Core.System]:X=Y", @"-COOKDIR=C:\Game\Content\A", "--Double" },
			CookedTestCommandLine.ParseCookerOptions(@"-ini:Engine:[Core.System]:X=Y -COOKDIR=C:\Game\Content\A --Double", "-cooker-options").ToArray());
		CollectionAssert.AreEqual(new[] { "-NEVERCOOKDIR=/Game/A+/Game/B", "-cookdir=C:/A" },
			CookedTestCommandLine.ParseCookerOptions("-NEVERCOOKDIR=/Game/A+/Game/B -cookdir=C:/A", "-cooker-options").ToArray());
		Assert.IsEmpty(CookedTestCommandLine.ParseCookerOptions("", "-cooker-options"));
		Assert.IsEmpty(CookedTestCommandLine.ParseCookerOptions("   ", "-cooker-options"));
	}

	[TestMethod]
	[DataRow("-COOKDIR=\"C:/a b\"", "no quotes")]
	[DataRow("-COOKDIR='a'", "no quotes")]
	[DataRow("-A=%PATH%", "no quotes")]
	[DataRow("-A!", "no quotes")]
	[DataRow("-A&calc", "no quotes")]
	[DataRow("-A|more", "no quotes")]
	[DataRow("-A<x", "no quotes")]
	[DataRow("-A>x", "no quotes")]
	[DataRow("-A^", "no quotes")]
	[DataRow("-A\t-B", "tabs")]
	[DataRow("-A\n-B", "control characters")]
	[DataRow("-A\u00A0-B", "tabs")]
	[DataRow("COOKDIR=/A", "isn't a cooker switch")]
	[DataRow("-COOKDIR=/A /B", "isn't a cooker switch")]
	[DataRow("-1", "isn't a cooker switch")]
	[DataRow("-", "isn't a cooker switch")]
	[DataRow("-=x", "isn't a cooker switch")]
	[DataRow("-run=Other", "UAT starts the cook commandlet")]
	[DataRow("-RUN=Cook", "UAT starts the cook commandlet")]
	[DataRow("-A --run=Cook", "UAT starts the cook commandlet")]
	[DataRow("-TargetPlatform=Linux", "UAT starts the cook commandlet")]
	[DataRow("-abslog=C:/x.log", "UAT starts the cook commandlet")]
	[DataRow("-X_abslog=C:/x.log", "UAT starts the cook commandlet")]
	[DataRow("-COOKDIR=C:/x/ABSLOG=y", "UAT starts the cook commandlet")]
	[DataRow("--COOKDIR=C:/A", "the cooker ignores")]
	[DataRow("-/COOKDIR=C:/A", "the cooker ignores")]
	[DataRow("-COOKDIR", "the cooker ignores")]
	[DataRow("--NEVERCOOKDIR=/Game/A", "the cooker ignores")]
	[DataRow("-COOKDIR=", "names one folder")]
	[DataRow("-COOKDIR=/Game/A+/Game/B", "names one folder")]
	public void CookerOptionsUatCantPassOrSetsItselfAreRefused(string value, string why)
	{
		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.ParseCookerOptions(value, "-cooker-options"));
		Assert.StartsWith("-cooker-options", Error.Message);
		Assert.Contains(why, Error.Message);
	}

	[TestMethod]
	public void ACookDirThatNamesNoFolderIsWarnedAbout()
	{
		using Sandbox Box = new();
		(string Existing, _) = CookDirs(Box);
		string Missing = Path.Combine(Box.ProjectDirectory, "Plugins", "Tests", "Content", "Missing");

		Assert.IsEmpty(CookedTestCommandLine.CookDirWarnings(["-COOKDIR=" + Existing, "-cookdir=" + Existing + Path.DirectorySeparatorChar, "-Other=/Plugin/Folder", "-COOKDIR"]));
		IReadOnlyList<string> Warnings = CookedTestCommandLine.CookDirWarnings(["-COOKDIR=/GameTests/Golden", "-CookDir=Relative/Folder", "-COOKDIR=" + Missing]);

		Assert.HasCount(3, Warnings);
		Assert.Contains("-COOKDIR=/GameTests/Golden", Warnings[0]);
		Assert.Contains("the cooker reads -COOKDIR= as a folder on disk", Warnings[0]);
		Assert.Contains("isn't a full path", Warnings[1]);
		Assert.Contains("names no folder on this machine", Warnings[2]);
	}

	// ---- the UAT argument ----

	[TestMethod]
	public void CookDirsGoToUatsCookDirAndTheRestAsOneArgument()
	{
		using Sandbox Box = WithRunUat();
		CookedTestRun Run = new()
		{
			ProjectFile = Box.ProjectFile,
			Filter = "Project.",
			Platform = UnrealPlatform.Win64,
			Maps = ["/Engine/Maps/Entry"],
			CookerOptions = ["-COOKDIR=C:/Game/Golden", "-A", "-cookdir=C:/Unreal Projects (Tests)/Game/Content/Slots", "-NEVERCOOKDIR=/Game/Golden/Old"],
			LogFolder = Path.Combine(Box.ProjectDirectory, "Saved", "Logs", "c"),
		};

		ProcessInvocation Invocation = CookedTestCommandLine.Build(Box.Layout, Run);

		CollectionAssert.AreEqual(new[]
		{
			"BuildCookRun", "-project=" + Box.ProjectFile, "-platform=Win64", "-clientconfig=Development", "-unattended", "-nop4", "-utf8output",
			"-build", "-cook", "-stage", "-pak", "-map=/Engine/Maps/Entry",
			"-cookdir=C:/Game/Golden+C:/Unreal Projects (Tests)/Game/Content/Slots", "-AdditionalCookerOptions=-A -NEVERCOOKDIR=/Game/Golden/Old",
			"-run", "-nullrhi", "-RunAutomationTest=Project.", "-RunTimeoutSeconds=1800",
		}, Invocation.Arguments.ToArray());
		// cmd.exe runs RunUAT.bat: each value is quoted, so it reaches AutomationTool as one argument, which UAT reads whole.
		Assert.Contains(" -cookdir=\"C:/Game/Golden+C:/Unreal Projects (Tests)/Game/Content/Slots\" -AdditionalCookerOptions=\"-A -NEVERCOOKDIR=/Game/Golden/Old\" ",
			Invocation.ToCmdCommandLine());
		// A folder BuildCookRun's -cookdir= can't carry is a caller's error: the checks before refuse it.
		Assert.ThrowsExactly<ArgumentException>(() => CookedTestCommandLine.Build(Box.Layout, Run with { CookerOptions = ["-COOKDIR=C:/A+B"] }));
	}

	[TestMethod]
	public async Task TheCookDirsGoThroughRunUatsArgumentScanWhole()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Batch files are Windows-only.");
		}
		using Sandbox Box = new();
		// The way RunUAT.bat scans its arguments before it forwards them with %*: each %1, quotes stripped, compared in quotes.
		Sandbox.Write(Box.Layout.RunUatScript, string.Join("\r\n",
			"@echo off",
			":Scan",
			"set ARGUMENT=%1",
			"if not defined ARGUMENT goto Scanned",
			"set ARGUMENT=%ARGUMENT:\"=%",
			"if /I \"%ARGUMENT%\" == \"-compile\" echo compile",
			"shift",
			"goto Scan",
			":Scanned",
			"echo %*",
			""));
		CookedTestRun Run = new()
		{
			ProjectFile = Box.ProjectFile,
			Filter = "Project.",
			Platform = UnrealPlatform.Win64,
			CookerOptions = ["-COOKDIR=C:/Game/Golden", "-A", "-COOKDIR=C:/Unreal Projects (Tests)/Game/Content/Slots", "-NEVERCOOKDIR=/Game/Golden/Old"],
			LogFolder = Path.Combine(Box.Root, "logs"),
		};

		ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(CookedTestCommandLine.Build(Box.Layout, Run), CancellationToken.None);

		Assert.AreEqual(0, Result.ExitCode, Result.StandardError);
		Assert.AreEqual("", Result.StandardError.Trim(), "The scan broke on an argument.");
		// AutomationTool gets the values whole, quoted as cmd was given them: split the MSVC way, each is one argument.
		Assert.Contains(" -cookdir=\"C:/Game/Golden+C:/Unreal Projects (Tests)/Game/Content/Slots\" -AdditionalCookerOptions=\"-A -NEVERCOOKDIR=/Game/Golden/Old\" ",
			Result.StandardOutput);
	}

	[TestMethod]
	public void NoCookerOptionsGoToUatWithSkipCookOrNone()
	{
		using Sandbox Box = WithRunUat();
		CookedTestRun Run = new()
		{
			ProjectFile = Box.ProjectFile,
			Filter = "Project.",
			Platform = UnrealPlatform.Win64,
			SkipCook = true,
			CookerOptions = ["-COOKDIR=C:/Game/Golden"],
			LogFolder = Path.Combine(Box.ProjectDirectory, "Saved", "Logs", "c"),
		};

		Assert.IsNull(CookerArgument(CookedTestCommandLine.Build(Box.Layout, Run)));
		Assert.IsNull(CookDirArgument(CookedTestCommandLine.Build(Box.Layout, Run)));
		Assert.IsNull(CookerArgument(CookedTestCommandLine.Build(Box.Layout, Run with { SkipCook = false, CookerOptions = [] })));
		Assert.IsNull(CookDirArgument(CookedTestCommandLine.Build(Box.Layout, Run with { SkipCook = false, CookerOptions = [] })));
	}

	// ---- uak test -cooked ----

	[TestMethod]
	public async Task TheRunOptionReachesUatAndTheSummaryAndResultFile()
	{
		NeedsWindows();
		using Sandbox Box = WithRunUat();
		NeedsSpaceFree(Box);
		(string A, string B) = CookDirs(Box);
		FakeProcessRunner Runner = PassingUat();
		string ResultFile = Path.Combine(Box.Root, "r.json");

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(),
			["-cooked", "-filter=Project.", "-name=co", $"-cooker-options=-COOKDIR={A}  -COOKDIR={B}", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.AreEqual($"-cookdir={A}+{B}", CookDirArgument(Runner.Invocations.Single()));
		Assert.IsNull(CookerArgument(Runner.Invocations.Single()));
		Assert.Contains($"cooker options: -COOKDIR={A} -COOKDIR={B} (-cooker-options=)", Box.Logger.Text);
		Assert.IsFalse(Box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Warning), Box.Logger.Text);
		Assert.IsTrue(RunSummaries.Read(Box.Context().StateDirectory, "co")!.Entries.Single().Lines.Any(Line => Line.Contains($"cooker options: -COOKDIR={A} -COOKDIR={B}", StringComparison.Ordinal)));

		JsonElement Root = ReadResult(ResultFile);
		CollectionAssert.AreEqual(new[] { "-COOKDIR=" + A, "-COOKDIR=" + B }, Strings(Root.GetProperty("CookerOptions")));
		Assert.AreEqual("run", Root.GetProperty("CookerOptionsSource").GetString());
		Assert.AreEqual(0, Root.GetProperty("CookerOptionsUnused").GetArrayLength());
	}

	[TestMethod]
	public async Task TheSavedDefaultIsUsedAndTheRunOptionReplacesIt()
	{
		NeedsWindows();
		using Sandbox Box = WithRunUat();
		NeedsSpaceFree(Box);
		(string A, string B) = CookDirs(Box);
		TestConfigCommand Config = new();
		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), [$"-cooked-cooker-options=-COOKDIR={A} -COOKDIR={B}"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual($"-COOKDIR={A} -COOKDIR={B}", ProjectSettings.GetCookedCookerOptions(Box.Context()));
		Assert.Contains($"cooked-cooker-options: -COOKDIR={A} -COOKDIR={B}", Box.Logger.Text);
		FakeProcessRunner Runner = PassingUat();
		TestCommand Test = new(Services(Runner));
		string ResultFile = Path.Combine(Box.Root, "r.json");

		// The saved default.
		Assert.AreEqual(0, await Test.RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual($"-cookdir={A}+{B}", CookDirArgument(Runner.Invocations[0]));
		Assert.Contains("(saved by `uak test config`; -cooker-options= overrides)", Box.Logger.Text);
		Assert.AreEqual("saved", ReadResult(ResultFile).GetProperty("CookerOptionsSource").GetString());

		// A run option replaces it, whole.
		Assert.AreEqual(0, await Test.RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-cooker-options=-COOKDIR=" + A], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual("-cookdir=" + A, CookDirArgument(Runner.Invocations[1]));

		// An empty one passes none.
		Assert.AreEqual(0, await Test.RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-cooker-options=", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);
		Assert.IsNull(CookDirArgument(Runner.Invocations[2]));
		Assert.Contains($"cooker options: none (-cooker-options= sets aside the saved -COOKDIR={A} -COOKDIR={B})", Box.Logger.Text);
		JsonElement Root = ReadResult(ResultFile);
		Assert.AreEqual(0, Root.GetProperty("CookerOptions").GetArrayLength());
		Assert.AreEqual("run", Root.GetProperty("CookerOptionsSource").GetString());

		// An empty saved value forgets them, and -reset does too.
		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), ["-cooked-cooker-options="], CancellationToken.None), Box.Logger.Text);
		Assert.IsNull(ProjectSettings.GetCookedCookerOptions(Box.Context()));
		Assert.Contains("cooked-cooker-options: (none", Box.Logger.Text);
		Assert.AreEqual(0, await Test.RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);
		Assert.IsNull(CookDirArgument(Runner.Invocations[3]));
		Assert.AreEqual(JsonValueKind.Null, ReadResult(ResultFile).GetProperty("CookerOptionsSource").ValueKind);
		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), ["-cooked-cooker-options=-COOKDIR=" + A], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), ["-reset"], CancellationToken.None), Box.Logger.Text);
		Assert.IsNull(ProjectSettings.GetCookedCookerOptions(Box.Context()));
	}

	[TestMethod]
	public async Task TheSavedDefaultIsKeptBesideTheCookedMaps()
	{
		using Sandbox Box = new();
		string File = ProjectSettings.GetFile(Box.Context());
		Sandbox.Write(File, """{ "test": { "cookedMaps": "/Engine/Maps/Entry", "future": 1 } }""");

		Assert.AreEqual(0, await new TestConfigCommand().RunAsync(Box.Context(), ["-cooked-cooker-options=-A -B=1"], CancellationToken.None), Box.Logger.Text);

		using JsonDocument Settings = JsonDocument.Parse(System.IO.File.ReadAllText(File));
		JsonElement Test = Settings.RootElement.GetProperty("test");
		Assert.AreEqual("-A -B=1", Test.GetProperty("cookedCookerOptions").GetString());
		Assert.AreEqual("/Engine/Maps/Entry", Test.GetProperty("cookedMaps").GetString());
		Assert.AreEqual(1, Test.GetProperty("future").GetInt32());
	}

	[TestMethod]
	public async Task ARefusedValueSavesNothing()
	{
		using Sandbox Box = new();
		TestConfigCommand Config = new();
		ProjectSettings.SetCookedCookerOptions(Box.Context(), "-A");

		Assert.AreEqual(2, await Config.RunAsync(Box.Context(), ["-cooked-cooker-options=-A&calc"], CancellationToken.None));
		Assert.AreEqual(2, await Config.RunAsync(Box.Context(), ["-cooked-cooker-options=-run=Other"], CancellationToken.None));
		Assert.AreEqual(2, await Config.RunAsync(Box.Context(), ["-cooked-cooker-options=-B", "-reset"], CancellationToken.None));
		Assert.AreEqual(2, await Config.RunAsync(Box.Context(), ["-cooked-cooker-options"], CancellationToken.None));
		// A refused value of another setting in the same call saves none of them.
		Assert.AreEqual(2, await Config.RunAsync(Box.Context(), ["-cooked-cooker-options=-B", "-cooked-maps=/Game/A+"], CancellationToken.None));

		Assert.AreEqual("-A", ProjectSettings.GetCookedCookerOptions(Box.Context()));
	}

	[TestMethod]
	public async Task SavingAPackagePathThatResolvesNowhereWarnsAndSavesIt()
	{
		using Sandbox Box = new();

		Assert.AreEqual(0, await new TestConfigCommand().RunAsync(Box.Context(), ["-cooked-cooker-options=-COOKDIR=/GameTests/Golden"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual("-COOKDIR=/GameTests/Golden", ProjectSettings.GetCookedCookerOptions(Box.Context()));
		Assert.IsTrue(Box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Warning && Entry.Message.Contains("a cooked run here would refuse them", StringComparison.Ordinal)
			&& Entry.Message.Contains("no plugin there is named GameTests", StringComparison.Ordinal)), Box.Logger.Text);
	}

	[TestMethod]
	public async Task ACookDirThatNamesNoFolderIsWarnedAboutInTheRun()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = PassingUat();

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-name=cd", "-cooker-options=-COOKDIR=Relative/Golden"], CancellationToken.None);

		// Not a package path: passed on as given and warned about, before the run and in its summary, never refused.
		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.AreEqual("-cookdir=Relative/Golden", CookDirArgument(Runner.Invocations.Single()));
		Assert.AreEqual(2, Box.Logger.Entries.Count(Entry => Entry.Level == LogLevel.Warning && Entry.Message.Contains("-COOKDIR=Relative/Golden isn't a full path", StringComparison.Ordinal)), Box.Logger.Text);
		Assert.IsTrue(RunSummaries.Read(Box.Context().StateDirectory, "cd")!.Entries.Single().Lines.Any(Line => Line.Contains("cooks nothing from it", StringComparison.Ordinal)));
	}

	// ---- -COOKDIR= package paths ----

	static string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine(parts)).FullName;

	static string Slashed(string path) => path.Replace('\\', '/');

	/// <summary>A plugin &lt;name&gt;.uplugin in <paramref name="folder"/>, with a Content/Golden folder; returns its Content folder.</summary>
	static string Plugin(string folder, string name)
	{
		Sandbox.Write(Path.Combine(folder, name + ".uplugin"), "{}");
		Folder(folder, "Content", "Golden");
		return Path.Combine(folder, "Content");
	}

	static string EngineDirectory(Sandbox box) => Path.Combine(box.EngineRoot, "Engine");

	static string[] Resolve(Sandbox box, bool windows, params string[] options) =>
		[.. CookedTestCommandLine.ResolveCookDirs(options, "-cooker-options", box.ProjectDirectory, EngineDirectory(box), windows)];

	static UakUsageException Refused(Sandbox box, bool windows, string option, string? engineDirectory = "") =>
		Assert.ThrowsExactly<UakUsageException>(() => CookedTestCommandLine.ResolveCookDirs([option], "-cooker-options", box.ProjectDirectory,
			engineDirectory == "" ? EngineDirectory(box) : engineDirectory, windows));

	[TestMethod]
	public void PackagePathsResolveToTheirContentFolders()
	{
		using Sandbox Box = new();
		string Tests = Folder(Box.ProjectDirectory, "Content", "Tests");
		string Content = Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "Group", "Nested", "TestContent"), "TestContent");
		string Maps = Folder(EngineDirectory(Box), "Content", "Maps");
		// A full path on this host: on Windows it doesn't start with '/', elsewhere its first part is no mount point.
		string Absolute = Folder(Box.Root, "Absolute");

		CollectionAssert.AreEqual(new[]
		{
			"-COOKDIR=" + Slashed(Tests),
			"-CookDir=" + Slashed(Path.Combine(Content, "Golden")),
			"-COOKDIR=" + Slashed(Maps),
			"-COOKDIR=" + Slashed(Path.Combine(Box.ProjectDirectory, "Content")),
			"-A=/Game/Tests",
			"-NEVERCOOKDIR=/Game/Tests",
			"-COOKDIR=" + Absolute,
			"-Other",
		}, Resolve(Box, OperatingSystem.IsWindows(), "-COOKDIR=/Game/Tests", "-CookDir=/TestContent/Golden/", "-COOKDIR=/Engine/Maps", "-COOKDIR=/Game", "-A=/Game/Tests",
			"-NEVERCOOKDIR=/Game/Tests", "-COOKDIR=" + Absolute, "-Other"));
	}

	[TestMethod]
	public void NamesIgnoreCaseOnWindowsOnlyAndElsewhereAFullPathIsKept()
	{
		using Sandbox Box = new();
		string Content = Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "TestContent"), "TestContent");
		string Golden = "-COOKDIR=" + Slashed(Path.Combine(Content, "Golden"));

		CollectionAssert.AreEqual(new[] { Golden }, Resolve(Box, true, "-COOKDIR=/testcontent/Golden"));
		CollectionAssert.AreEqual(new[] { Golden }, Resolve(Box, false, "-COOKDIR=/TestContent/Golden"));
		// Elsewhere a full path starts with '/' too: a first part that is no mount point is a folder on disk, kept as given.
		CollectionAssert.AreEqual(new[] { "-COOKDIR=/testcontent/Golden" }, Resolve(Box, false, "-COOKDIR=/testcontent/Golden"));
		CollectionAssert.AreEqual(new[] { "-COOKDIR=/usr/share/content.v2" }, Resolve(Box, false, "-COOKDIR=/usr/share/content.v2"));
		// On Windows no full path starts with one '/', so it is a package path that names no plugin.
		Assert.Contains("no plugin there is named usr", Refused(Box, true, "-COOKDIR=/usr/share/content").Message);
		Assert.StartsWith("-cooker-options: -COOKDIR=/usr/share/content", Refused(Box, true, "-COOKDIR=/usr/share/content").Message);
	}

	[TestMethod]
	public void TheEnginesPluginsAreSearchedAfterTheProjects()
	{
		using Sandbox Box = new();
		string EngineOnly = Plugin(Path.Combine(EngineDirectory(Box), "Plugins", "Runtime", "EngineOnly"), "EngineOnly");
		string Project = Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "Shared"), "Shared");
		Plugin(Path.Combine(EngineDirectory(Box), "Plugins", "Shared"), "Shared");
		Folder(EngineDirectory(Box), "Content", "Maps");

		CollectionAssert.AreEqual(new[] { "-COOKDIR=" + Slashed(Path.Combine(EngineOnly, "Golden")), "-COOKDIR=" + Slashed(Path.Combine(Project, "Golden")) },
			Resolve(Box, true, "-COOKDIR=/EngineOnly/Golden", "-COOKDIR=/Shared/Golden"));
		// Without an engine, only the project's plugins are searched.
		Assert.Contains("no plugin there is named EngineOnly", Refused(Box, true, "-COOKDIR=/EngineOnly/Golden", engineDirectory: null).Message);
		Assert.Contains("uak knows no engine", Refused(Box, true, "-COOKDIR=/Engine/Maps", engineDirectory: null).Message);
	}

	[TestMethod]
	public void AFolderMayHoldASpaceButWhatCantReachTheCookerIsRefused()
	{
		using Sandbox Box = new();
		Folder(Box.ProjectDirectory, "Content");
		// BuildCookRun quotes each -cookdir= folder for the cooker, so a space is fine (Epic's default is Documents/Unreal Projects).
		string Spaced = Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "Unreal Projects (Tests)", "Spaced"), "Spaced");
		Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "A", "Dup"), "Dup");
		Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "B", "Dup"), "Dup");
		// The engine stops looking in a folder that holds a .uplugin: a plugin inside another isn't found.
		Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "Outer"), "Outer");
		Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "Outer", "Inner"), "Inner");
		Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "C++", "Plus"), "Plus");
		Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "100%", "Percent"), "Percent");

		CollectionAssert.AreEqual(new[] { "-COOKDIR=" + Slashed(Path.Combine(Spaced, "Golden")) }, Resolve(Box, true, "-COOKDIR=/Spaced/Golden"));
		Assert.Contains("which doesn't exist", Refused(Box, true, "-COOKDIR=/Game/Missing").Message);
		Assert.Contains("2 plugins are named Dup", Refused(Box, true, "-COOKDIR=/Dup/Golden").Message);
		Assert.Contains("2 plugins are named Dup", Refused(Box, false, "-COOKDIR=/Dup/Golden").Message);
		Assert.Contains("no plugin there is named Inner", Refused(Box, true, "-COOKDIR=/Inner/Golden").Message);
		Assert.Contains("it names none", Refused(Box, true, "-COOKDIR=/").Message);
		// Parts that no package name holds, which could also lead out of the content folder.
		foreach (string Option in new[] { "-COOKDIR=/Game/../Content", "-COOKDIR=/Game/.", @"-COOKDIR=/Game/..\..\Plugins", "-COOKDIR=/Game/C:/Windows", "-COOKDIR=/Game/A+B" })
		{
			Assert.Contains("isn't a package path uak can resolve", Refused(Box, true, Option).Message, Option);
		}
		// Folders whose path can't go through RunUAT, or would split BuildCookRun's -cookdir= list.
		Assert.Contains("can't go through RunUAT to the cooker", Refused(Box, true, "-COOKDIR=/Plus/Golden").Message);
		Assert.Contains("can't go through RunUAT to the cooker", Refused(Box, true, "-COOKDIR=/Percent/Golden").Message);
	}

	[TestMethod]
	public async Task TheRunPassesTheResolvedFolderAndReportsWhatWasTyped()
	{
		using Sandbox Box = WithRunUat();
		string Golden = Slashed(Path.Combine(Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "With Space", "TestContent"), "TestContent"), "Golden"));
		FakeProcessRunner Runner = PassingUat();
		TestCommand Test = new(Services(Runner));
		string ResultFile = Path.Combine(Box.Root, "r.json");

		Assert.AreEqual(0, await Test.RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-name=pp", "-cooker-options=-COOKDIR=/TestContent/Golden -A -NEVERCOOKDIR=/TestContent/Golden/Old",
			"-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);

		// The folder goes through -cookdir=, quoted for cmd as it holds a space; the other switches through -AdditionalCookerOptions.
		ProcessInvocation Invocation = Runner.Invocations.Single();
		Assert.AreEqual("-cookdir=" + Golden, CookDirArgument(Invocation));
		Assert.AreEqual("-AdditionalCookerOptions=-A -NEVERCOOKDIR=/TestContent/Golden/Old", CookerArgument(Invocation));
		Assert.Contains($" -cookdir=\"{Golden}\" ", Invocation.ToCmdCommandLine());
		string Line = $"cooker options: -COOKDIR={Golden} -A -NEVERCOOKDIR=/TestContent/Golden/Old (-cooker-options=; given as -COOKDIR=/TestContent/Golden -A -NEVERCOOKDIR=/TestContent/Golden/Old)";
		Assert.Contains(Line, Box.Logger.Text);
		Assert.IsTrue(RunSummaries.Read(Box.Context().StateDirectory, "pp")!.Entries.Single().Lines.Any(Summary => Summary.Contains(Line, StringComparison.Ordinal)));
		Assert.IsFalse(Box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Warning), Box.Logger.Text);
		JsonElement Root = ReadResult(ResultFile);
		CollectionAssert.AreEqual(new[] { "-COOKDIR=" + Golden, "-A", "-NEVERCOOKDIR=/TestContent/Golden/Old" }, Strings(Root.GetProperty("CookerOptions")));
		CollectionAssert.AreEqual(new[] { "-COOKDIR=/TestContent/Golden", "-A", "-NEVERCOOKDIR=/TestContent/Golden/Old" }, Strings(Root.GetProperty("CookerOptionsGiven")));
	}

	[TestMethod]
	public async Task ARefusedPackagePathNeverTakesTheLock()
	{
		using Sandbox Box = WithRunUat();
		Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "TestContent"), "TestContent");
		FakeProcessRunner Runner = PassingUat();
		FakeLock Lock = new();

		Assert.AreEqual(2, await new TestCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-cooker-options=-COOKDIR=/TestContent/Missing"],
			CancellationToken.None));

		Assert.IsEmpty(Runner.Invocations);
		Assert.IsEmpty(Lock.Names);
		Assert.AreEqual(0, Lock.Held);
		Assert.Contains("-cooker-options: -COOKDIR=/TestContent/Missing is ", Box.Logger.Text);
	}

	[TestMethod]
	public async Task TheSavedDefaultKeepsItsPackagePathsAndEachRunResolvesThem()
	{
		using Sandbox Box = WithRunUat();
		string Content = Plugin(Path.Combine(Box.ProjectDirectory, "Plugins", "TestContent"), "TestContent");
		string Golden = Slashed(Path.Combine(Content, "Golden"));
		TestConfigCommand Config = new();

		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), ["-cooked-cooker-options=-COOKDIR=/TestContent/Golden"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual("-COOKDIR=/TestContent/Golden", ProjectSettings.GetCookedCookerOptions(Box.Context()));
		Assert.Contains("cooked-cooker-options: -COOKDIR=/TestContent/Golden", Box.Logger.Text);
		Assert.Contains("  resolves here to: -COOKDIR=" + Golden, Box.Logger.Text);
		FakeProcessRunner Runner = PassingUat();
		string ResultFile = Path.Combine(Box.Root, "r.json");
		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-resultfile=" + ResultFile], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual("-cookdir=" + Golden, CookDirArgument(Runner.Invocations.Single()));
		JsonElement Root = ReadResult(ResultFile);
		Assert.AreEqual("saved", Root.GetProperty("CookerOptionsSource").GetString());
		CollectionAssert.AreEqual(new[] { "-COOKDIR=/TestContent/Golden" }, Strings(Root.GetProperty("CookerOptionsGiven")));

		// Resolved again: once the folder is gone, the setting stays, the display says a run would refuse it, and a run does.
		Directory.Delete(Path.Combine(Content, "Golden"));
		Assert.AreEqual(0, await Config.RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);
		Assert.IsTrue(Box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Warning && Entry.Message.Contains("a cooked run here would refuse them", StringComparison.Ordinal)), Box.Logger.Text);
		Assert.AreEqual(2, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None));
		Assert.HasCount(1, Runner.Invocations);
		Assert.Contains("cookedCookerOptions (saved by `uak test config`): -COOKDIR=/TestContent/Golden is ", Box.Logger.Text);
	}

	// ---- -skipcook ----

	[TestMethod]
	public async Task SkipCookWarnsThatTheRunOptionIsUnused()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = PassingUat();
		string ResultFile = Path.Combine(Box.Root, "r.json");

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(),
			["-cooked", "-filter=Project.", "-skipbuild", "-skipcook", "-cooker-options=-COOKDIR=/GameTests/Golden", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.IsNull(CookerArgument(Runner.Invocations.Single()));
		string Unused = "cooker options: unused, as -skipcook runs no cook: -COOKDIR=/GameTests/Golden (-cooker-options=)";
		Assert.IsTrue(Box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Warning && Entry.Message.Contains(Unused, StringComparison.Ordinal)), Box.Logger.Text);
		// No cook runs, so the package path isn't resolved (it names no plugin here), nor the folder checked.
		Assert.DoesNotContain("cooks nothing from it", Box.Logger.Text);
		Assert.DoesNotContain("no plugin", Box.Logger.Text);
		JsonElement Root = ReadResult(ResultFile);
		Assert.AreEqual(0, Root.GetProperty("CookerOptions").GetArrayLength());
		CollectionAssert.AreEqual(new[] { "-COOKDIR=/GameTests/Golden" }, Strings(Root.GetProperty("CookerOptionsUnused")));
		Assert.AreEqual("passed", Root.GetProperty("Outcome").GetString());
	}

	[TestMethod]
	public async Task SkipCookOnlyNotesTheSavedDefault()
	{
		using Sandbox Box = WithRunUat();
		ProjectSettings.SetCookedCookerOptions(Box.Context(), "-COOKDIR=/GameTests/Golden");
		FakeProcessRunner Runner = PassingUat();

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-skipbuild", "-skipcook"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.IsNull(CookerArgument(Runner.Invocations.Single()));
		Assert.Contains("cooker options: unused, as -skipcook runs no cook: -COOKDIR=/GameTests/Golden (saved by `uak test config`", Box.Logger.Text);
		Assert.IsFalse(Box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Warning), Box.Logger.Text);

		// Unused, so not checked either: a hand-edited value a cooking run would refuse doesn't stop this one.
		ProjectSettings.SetCookedCookerOptions(Box.Context(), "-A|more -COOKDIR=/GameTests/Golden");
		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-skipbuild", "-skipcook"], CancellationToken.None), Box.Logger.Text);
		Assert.HasCount(2, Runner.Invocations);
		Assert.Contains("cooker options: unused, as -skipcook runs no cook: -A|more -COOKDIR=/GameTests/Golden (saved by", Box.Logger.Text);
		Assert.IsFalse(Box.Logger.Entries.Any(Entry => Entry.Level == LogLevel.Warning), Box.Logger.Text);
	}

	// ---- usage errors ----

	[TestMethod]
	public async Task CookerOptionUsageErrors()
	{
		using Sandbox Box = WithRunUat();
		FakeProcessRunner Runner = new();
		TestCommand Command = new(Services(Runner));
		string[][] Errors =
		[
			["-filter=A", "-cooker-options=-A"],
			["-filter=A", "-cooker-options="],
			["-cooked", "-filter=A", "-cooker-options"],
			["-cooked", "-filter=A", "-cooker-options=-A&calc"],
			["-cooked", "-filter=A", "-cooker-options=-A \"-B\""],
			["-cooked", "-filter=A", "-cooker-options=-TargetPlatform=Linux"],
			["-cooked", "-filter=A", "-cooker-options=COOKDIR=/A"],
		];

		foreach (string[] Arguments in Errors)
		{
			Assert.AreEqual(2, await Command.RunAsync(Box.Context(), Arguments, CancellationToken.None), string.Join(' ', Arguments));
		}
		Assert.IsEmpty(Runner.Invocations);
		Assert.Contains("are for cooked runs: add -cooked", Box.Logger.Text);
		Assert.Contains("-cooker-options needs a value (-cooker-options=..., or -cooker-options= for none)", Box.Logger.Text);
	}

	[TestMethod]
	public async Task ASavedValueUakCantPassIsRefusedNamingTheFile()
	{
		using Sandbox Box = WithRunUat();
		// Edited by hand: uak test config would have refused it.
		ProjectSettings.SetCookedCookerOptions(Box.Context(), "-A|more");
		FakeProcessRunner Runner = PassingUat();

		Assert.AreEqual(2, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project."], CancellationToken.None));
		Assert.IsEmpty(Runner.Invocations);
		Assert.Contains("cookedCookerOptions in " + ProjectSettings.GetFile(Box.Context()), Box.Logger.Text);

		// A run option replaces it, so it is never read.
		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-cooked", "-filter=Project.", "-cooker-options="], CancellationToken.None), Box.Logger.Text);
	}
}
