// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentKit.Core.Tests;

/// <summary>Logs its arguments (joined with '|') and where it runs, so host tests can see exactly what reached it.</summary>
public sealed class TestEchoCommand : IUakCommand
{
	public string Name => "test echo";
	public string Summary => "Echoes its arguments.";
	public string Usage => "uak test echo [anything...]\nSecond line.";
	public bool RequiresEngine => false;

	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		context.Logger.LogInformation("args={Arguments}", string.Join('|', arguments));
		context.Logger.LogInformation("project={Project}", context.ProjectFile?.FullName ?? "none");
		context.Logger.LogInformation("vcs={Vcs}", context.RequestedVersionControl ?? "none");
		return Task.FromResult(UakExitCodes.Success);
	}
}

/// <summary>Fails in the way its first argument asks: "usage", "throw", "cancel", or "code=N".</summary>
public sealed class TestFailCommand : IUakCommand
{
	public string Name => "test fail";
	public string Summary => "Fails on request.";
	public string Usage => "uak test fail usage|throw|cancel|code=N";
	public bool RequiresEngine => false;

	public async Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		string Mode = arguments.Count > 0 ? arguments[0] : "";
		if (Mode == "usage")
		{
			throw new UakUsageException("bad usage");
		}
		if (Mode == "throw")
		{
			throw new InvalidOperationException("it broke");
		}
		if (Mode == "cancel")
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}
		return Mode.StartsWith("code=", StringComparison.Ordinal) ? int.Parse(Mode[5..], System.Globalization.CultureInfo.InvariantCulture) : 0;
	}
}

/// <summary>Needs an engine, and logs it.</summary>
public sealed class TestEngineCommand : IUakCommand
{
	public string Name => "test engine";
	public string Summary => "Needs an engine.";
	public string Usage => "uak test engine";

	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		context.Logger.LogInformation("engine={Engine}", context.RequireEngine().RootDirectory);
		return Task.FromResult(UakExitCodes.Success);
	}
}

/// <summary>A three-word name, for longest-match tests.</summary>
public sealed class TestGroupDeepCommand : IUakCommand
{
	public string Name => "test group deep";
	public string Summary => "Deep.";
	public string Usage => "uak test group deep";
	public bool RequiresEngine => false;

	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		context.Logger.LogInformation("deep={Arguments}", string.Join('|', arguments));
		return Task.FromResult(UakExitCodes.Success);
	}
}

/// <summary>Two commands with one name: the catalog keeps one and reports the other.</summary>
public sealed class TestDuplicateOneCommand : IUakCommand
{
	public string Name => "test dup";
	public string Summary => "One.";
	public string Usage => "";
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>See <see cref="TestDuplicateOneCommand"/>.</summary>
public sealed class TestDuplicateTwoCommand : IUakCommand
{
	public string Name => "TEST  dup";
	public string Summary => "Two.";
	public string Usage => "";
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>A hidden command: it runs, but listings leave it out.</summary>
public sealed class TestSecretCommand : IUakCommand
{
	public string Name => "test secret";
	public string Summary => "Hidden from listings.";
	public string Usage => "uak test secret (hidden usage)";
	public bool RequiresEngine => false;
	public bool Hidden => true;

	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		context.Logger.LogInformation("secret ran");
		return Task.FromResult(UakExitCodes.Success);
	}
}

/// <summary>The only command of its group, and hidden: the group itself is not listed.</summary>
public sealed class TestHiddenGroupCommand : IUakCommand
{
	public string Name => "hiddengroup only";
	public string Summary => "Hidden.";
	public string Usage => "uak hiddengroup only";
	public bool RequiresEngine => false;
	public bool Hidden => true;
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>A reporter for the catalog's CreateAll test.</summary>
public sealed class TestEnvReporter : IUakEnvReporter
{
	public Task<IReadOnlyList<KeyValuePair<string, string>>> ReportAsync(UakContext context, CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([new("Test row", "from the test reporter")]);
}

[TestClass]
public sealed class UakHostTests
{
	sealed class Run
	{
		public int ExitCode;
		public string Out = "";
		public string Err = "";
	}

	static async Task<Run> RunHost(TempTree tree, params string[] arguments)
	{
		StringWriter Out = new();
		StringWriter Err = new();
		UakHostOptions Options = new()
		{
			Out = Out,
			Error = Err,
			LoadCatalog = _ => UakCommandCatalog.FromAssemblies([typeof(UakHostTests).Assembly]),
			ResolveOptions = new UakResolveOptions
			{
				CurrentDirectory = tree.Dir("cwd"),
				GetEnvironmentVariable = new FakeEnvironment().Get,
				AssociationSource = new FakeAssociationSource(),
				UserHomeDirectory = tree.Dir("home"),
			},
		};
		int Code = await UakHost.RunAsync(arguments, Options);
		return new Run { ExitCode = Code, Out = Out.ToString(), Err = Err.ToString() };
	}

	[TestMethod]
	public async Task ArgumentsAfterTheCommandPath_ReachItVerbatim()
	{
		using TempTree Tree = new();
		Run Result = await RunHost(Tree, "-vcs=git", "TEST", "Echo", "-project=not-global", "x", "--", "-engine=child", "--");
		Assert.AreEqual(0, Result.ExitCode, Result.Err);
		StringAssert.Contains(Result.Out, "args=-project=not-global|x|--|-engine=child|--");
		StringAssert.Contains(Result.Out, "project=none");
		StringAssert.Contains(Result.Out, "vcs=git");
	}

	[TestMethod]
	public async Task GlobalProject_IsResolved()
	{
		using TempTree Tree = new();
		string Project = Tree.Project("Game/Game.uproject");
		Run Result = await RunHost(Tree, "-Project=" + Project, "test", "echo");
		Assert.AreEqual(0, Result.ExitCode, Result.Err);
		StringAssert.Contains(Result.Out, "project=" + Project);
	}

	[TestMethod]
	public async Task LongestNameWins()
	{
		using TempTree Tree = new();
		Run Result = await RunHost(Tree, "test", "group", "deep", "tail");
		Assert.AreEqual(0, Result.ExitCode, Result.Err);
		StringAssert.Contains(Result.Out, "deep=tail");
	}

	[TestMethod]
	public async Task UnknownCommand_IsAUsageError()
	{
		using TempTree Tree = new();
		Run Result = await RunHost(Tree, "nonsense");
		Assert.AreEqual(UakExitCodes.UsageError, Result.ExitCode);
		StringAssert.Contains(Result.Err, "unknown command \"nonsense\"");
	}

	[TestMethod]
	public async Task GroupWithoutSubcommand_ListsIt()
	{
		using TempTree Tree = new();
		Run Result = await RunHost(Tree, "test", "group");
		Assert.AreEqual(UakExitCodes.UsageError, Result.ExitCode);
		StringAssert.Contains(Result.Err, "needs a subcommand");
		StringAssert.Contains(Result.Err, "test group deep");
	}

	[TestMethod]
	public async Task NoCommand_ShowsTheOverviewAsAUsageError()
	{
		using TempTree Tree = new();
		Run Result = await RunHost(Tree);
		Assert.AreEqual(UakExitCodes.UsageError, Result.ExitCode);
		StringAssert.Contains(Result.Err, "Usage: uak");
	}

	[TestMethod]
	public async Task UnknownGlobalOption_IsAUsageError()
	{
		using TempTree Tree = new();
		Run Result = await RunHost(Tree, "-bogus", "test", "echo");
		Assert.AreEqual(UakExitCodes.UsageError, Result.ExitCode);
		StringAssert.Contains(Result.Err, "-bogus");
	}

	[TestMethod]
	public async Task GlobalOptionWithoutValue_IsAUsageError()
	{
		using TempTree Tree = new();
		Run Result = await RunHost(Tree, "-engine", "test", "echo");
		Assert.AreEqual(UakExitCodes.UsageError, Result.ExitCode);
	}

	[TestMethod]
	public async Task Help_ListsCommands_AndShowsOne()
	{
		using TempTree Tree = new();
		Run List = await RunHost(Tree, "help");
		Assert.AreEqual(0, List.ExitCode);
		StringAssert.Contains(List.Out, "test echo");
		StringAssert.Contains(List.Out, "Echoes its arguments.");
		StringAssert.Contains(List.Out, "help [-all] [command]");

		Run One = await RunHost(Tree, "help", "test", "echo");
		Assert.AreEqual(0, One.ExitCode);
		StringAssert.Contains(One.Out, "uak test echo [anything...]");
		StringAssert.Contains(One.Out, "  Second line.");
		StringAssert.Contains(One.Out, "It runs without an engine.");

		Run Group = await RunHost(Tree, "help", "test", "group");
		Assert.AreEqual(0, Group.ExitCode);
		StringAssert.Contains(Group.Out, "test group deep");

		Run Missing = await RunHost(Tree, "help", "nope");
		Assert.AreEqual(UakExitCodes.UsageError, Missing.ExitCode);

		Run Flag = await RunHost(Tree, "test", "echo", "-help");
		Assert.AreEqual(0, Flag.ExitCode);
		StringAssert.Contains(Flag.Out, "uak test echo [anything...]");

		Run Global = await RunHost(Tree, "-help");
		Assert.AreEqual(0, Global.ExitCode);
		StringAssert.Contains(Global.Out, "Commands:");
	}

	[TestMethod]
	[DataRow("usage", UakExitCodes.UsageError, "bad usage")]
	[DataRow("throw", UakExitCodes.Failure, "it broke")]
	[DataRow("code=5", 5, "")]
	[DataRow("code=0", 0, "")]
	public async Task ExitCodes(string mode, int expected, string message)
	{
		using TempTree Tree = new();
		Run Result = await RunHost(Tree, "test", "fail", mode);
		Assert.AreEqual(expected, Result.ExitCode);
		StringAssert.Contains(Result.Err, message);
	}

	[TestMethod]
	public async Task Cancellation_IsAFailure()
	{
		using TempTree Tree = new();
		using CancellationTokenSource Cancel = new(TimeSpan.FromMilliseconds(200));
		StringWriter Err = new();
		int Code = await UakHost.RunAsync(["test", "fail", "cancel"], new UakHostOptions
		{
			Out = new StringWriter(),
			Error = Err,
			CancellationToken = Cancel.Token,
			LoadCatalog = _ => UakCommandCatalog.FromAssemblies([typeof(UakHostTests).Assembly]),
			ResolveOptions = new UakResolveOptions { CurrentDirectory = Tree.Dir("cwd"), GetEnvironmentVariable = new FakeEnvironment().Get, UserHomeDirectory = Tree.Dir("home") },
		});
		Assert.AreEqual(UakExitCodes.Failure, Code);
		StringAssert.Contains(Err.ToString(), "cancelled");
	}

	[TestMethod]
	public async Task HiddenCommands_AreLeftOutOfListings_ButRun()
	{
		using TempTree Tree = new();
		Run List = await RunHost(Tree, "help");
		Assert.AreEqual(0, List.ExitCode);
		Assert.IsFalse(List.Out.Contains("test secret", StringComparison.Ordinal), List.Out);
		Assert.IsFalse(List.Out.Contains("hiddengroup", StringComparison.Ordinal), List.Out);

		Run Group = await RunHost(Tree, "help", "test");
		StringAssert.Contains(Group.Out, "test echo");
		Assert.IsFalse(Group.Out.Contains("test secret", StringComparison.Ordinal), Group.Out);

		Run Partial = await RunHost(Tree, "test");
		Assert.AreEqual(UakExitCodes.UsageError, Partial.ExitCode);
		StringAssert.Contains(Partial.Err, "test echo");
		Assert.IsFalse(Partial.Err.Contains("test secret", StringComparison.Ordinal), Partial.Err);

		Run OnlyHidden = await RunHost(Tree, "hiddengroup");
		Assert.AreEqual(UakExitCodes.UsageError, OnlyHidden.ExitCode);
		StringAssert.Contains(OnlyHidden.Err, "unknown command");

		Run Ran = await RunHost(Tree, "test", "secret");
		Assert.AreEqual(0, Ran.ExitCode, Ran.Err);
		StringAssert.Contains(Ran.Out, "secret ran");

		Run Help = await RunHost(Tree, "help", "test", "secret");
		Assert.AreEqual(0, Help.ExitCode);
		StringAssert.Contains(Help.Out, "(hidden usage)");
	}

	[TestMethod]
	public async Task RequiresEngine_IsHonoured()
	{
		using TempTree Tree = new();
		Run Missing = await RunHost(Tree, "test", "engine");
		Assert.AreEqual(UakExitCodes.UsageError, Missing.ExitCode);
		StringAssert.Contains(Missing.Err, "No engine found");

		string Engine = Tree.Engine("UE");
		Run Found = await RunHost(Tree, "-engine=" + Engine, "test", "engine");
		Assert.AreEqual(0, Found.ExitCode, Found.Err);
		StringAssert.Contains(Found.Out, "engine=" + Engine);
	}

	[TestMethod]
	public async Task BadProjectArgument_IsASetupError()
	{
		using TempTree Tree = new();
		Run Result = await RunHost(Tree, "-project=" + Tree.Path("missing.uproject"), "test", "echo");
		Assert.AreEqual(UakExitCodes.UsageError, Result.ExitCode);
		StringAssert.Contains(Result.Err, "does not exist");
	}

	[TestMethod]
	public async Task Env_LeavesNoStateFolderBehind()
	{
		using TempTree Tree = new();
		string Project = Tree.Project("Game/Game.uproject");
		StringWriter Err = new();
		int Code = await UakHost.RunAsync(["-project=" + Project, "env"], new UakHostOptions
		{
			Out = new StringWriter(),
			Error = Err,
			LoadCatalog = _ => UakCommandCatalog.FromAssemblies([typeof(EnvCommand).Assembly]),
			// The real writability check, which must create nothing.
			ResolveOptions = new UakResolveOptions { CurrentDirectory = Tree.Root, GetEnvironmentVariable = new FakeEnvironment().Get, AssociationSource = new FakeAssociationSource(), UserHomeDirectory = Tree.Dir("home") },
		});
		Assert.AreEqual(0, Code, Err.ToString());
		Assert.IsFalse(Directory.Exists(Tree.Path("Game", "Saved")), "uak env created the project's Saved folder");
	}

	[TestMethod]
	public void ParseGlobalOptions_StopsAtTheCommandPath()
	{
		(UakHost.GlobalOptions Options, IReadOnlyList<string> Remaining) = UakHost.ParseGlobalOptions(["-project=a", "-ENGINE=b", "-verbose", "cmd", "-vcs=git"]);
		Assert.AreEqual("a", Options.Project);
		Assert.AreEqual("b", Options.Engine);
		Assert.IsNull(Options.Vcs);
		Assert.IsTrue(Options.Verbose);
		CollectionAssert.AreEqual(new[] { "cmd", "-vcs=git" }, Remaining.ToArray());
	}
}

[TestClass]
public sealed class UakCommandCatalogTests
{
	static string s_projectDirectory = "";
	static string s_commandsFolder = "";

	[ClassInitialize]
	public static void CreateProjectWithCommands(TestContext context)
	{
		// One folder for the whole class: a process loads an assembly from one path only.
		s_projectDirectory = Path.Combine(Path.GetTempPath(), "uak-core-tests", "project-commands-" + Guid.NewGuid().ToString("N"));
		s_commandsFolder = Path.Combine(s_projectDirectory, ".uak", "commands");
		Directory.CreateDirectory(s_commandsFolder);
		File.WriteAllText(Path.Combine(s_projectDirectory, "Game.uproject"), """{ "EngineAssociation": "5.8" }""");
		string Sample = Path.Combine(AppContext.BaseDirectory, "SampleCommands", "UakSampleCommands.dll");
		Assert.IsTrue(File.Exists(Sample), "The build copies UakSampleCommands.dll to " + Sample);
		File.Copy(Sample, Path.Combine(s_commandsFolder, "UakSampleCommands.dll"));
		// Neither of these is a command assembly: a copy of Core (already loaded) and a file that is not .NET.
		File.Copy(typeof(IUakCommand).Assembly.Location, Path.Combine(s_commandsFolder, "AgentKit.Core.dll"));
		File.WriteAllText(Path.Combine(s_commandsFolder, "native.dll"), "not a PE file");
	}

	[ClassCleanup]
	public static void DeleteProject()
	{
		try
		{
			Directory.Delete(s_projectDirectory, recursive: true);
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			// The loaded sample DLL stays locked until the test process ends.
		}
	}

	[TestMethod]
	public void FromAssemblies_FindsCommands_SortsThem_AndReportsDuplicates()
	{
		UakCommandCatalog Catalog = UakCommandCatalog.FromAssemblies([typeof(UakCommandCatalogTests).Assembly]);
		Assert.IsNotNull(Catalog.Match(["test", "echo"]).Command);
		Assert.AreEqual(1, Catalog.Commands.Count(Command => string.Join(' ', UakCommandCatalog.SplitName(Command.Name)).Equals("test dup", StringComparison.OrdinalIgnoreCase)));
		Assert.IsTrue(Catalog.Problems.Any(Problem => Problem.Contains("\"TEST dup\"", StringComparison.OrdinalIgnoreCase)), string.Join('\n', Catalog.Problems));
		CollectionAssert.AreEqual(Catalog.Commands.Select(Command => Command.Name).OrderBy(Name => Name, StringComparer.OrdinalIgnoreCase).ToArray(), Catalog.Commands.Select(Command => Command.Name).ToArray());
	}

	[TestMethod]
	public void Match_TakesTheLongestName_AndCountsItsWords()
	{
		UakCommandCatalog Catalog = UakCommandCatalog.FromAssemblies([typeof(UakCommandCatalogTests).Assembly]);
		(IUakCommand? Command, int Words) = Catalog.Match(["Test", "GROUP", "deep", "extra"]);
		Assert.IsInstanceOfType<TestGroupDeepCommand>(Command);
		Assert.AreEqual(3, Words);
		Assert.IsNull(Catalog.Match(["test", "group"]).Command);
		Assert.IsNull(Catalog.Match([]).Command);
		Assert.IsGreaterThanOrEqualTo(4, Catalog.WithPrefix(["test"]).Count);
		Assert.HasCount(1, Catalog.WithPrefix(["test", "group"]));
	}

	[TestMethod]
	public void CreateAll_FindsReporters()
	{
		UakCommandCatalog Catalog = UakCommandCatalog.FromAssemblies([typeof(UakCommandCatalogTests).Assembly]);
		Assert.IsTrue(Catalog.CreateAll<IUakEnvReporter>().Any(Reporter => Reporter is TestEnvReporter));
	}

	[TestMethod]
	public void Load_FindsTheKitsOwnAssemblies()
	{
		UakCommandCatalog Catalog = UakCommandCatalog.Load(AppContext.BaseDirectory, null, new FakeEnvironment().Get);
		Assert.IsInstanceOfType<EnvCommand>(Catalog.Match(["env"]).Command);
		Assert.IsTrue(Catalog.Assemblies.Contains(typeof(IUakCommand).Assembly));
	}

	[TestMethod]
	public void Load_FindsProjectCommands_OnlyWhenOptedIn_AndOnlyWhenAsked()
	{
		FakeEnvironment Environment = new() { [UakCommandCatalog.ProjectCommandsVariable] = "1" };
		UakCommandCatalog Catalog = UakCommandCatalog.Load(AppContext.BaseDirectory, s_projectDirectory, Environment.Get);
		Assert.IsNull(Catalog.SkippedProjectCommands);
		CollectionAssert.AreEqual(new[] { s_commandsFolder }, Catalog.ExternalFolders.ToArray());
		Assert.IsFalse(Catalog.ExternalLoaded);
		Assert.IsNull(Catalog.Match(["sample", "hello"]).Command, "nothing outside the kit loads until asked");

		Assert.IsTrue(Catalog.LoadExternal());
		Assert.IsFalse(Catalog.LoadExternal(), "loads once");
		IUakCommand? Sample = Catalog.Match(["sample", "hello"]).Command;
		Assert.IsNotNull(Sample, string.Join('\n', Catalog.Problems));
		Assert.AreEqual(Path.Combine(s_commandsFolder, "UakSampleCommands.dll"), Sample.GetType().Assembly.Location, ignoreCase: true);
		Assert.IsFalse(Catalog.Problems.Any(Problem => Problem.Contains("native.dll", StringComparison.Ordinal) || Problem.Contains("AgentKit.Core.dll", StringComparison.Ordinal)), string.Join('\n', Catalog.Problems));
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("0")]
	[DataRow("yes")]
	public void Load_WithoutOptIn_SkipsTheProjectFolder(string? value)
	{
		FakeEnvironment Environment = new();
		if (value is not null)
		{
			Environment[UakCommandCatalog.ProjectCommandsVariable] = value;
		}
		UakCommandCatalog Catalog = UakCommandCatalog.Load(AppContext.BaseDirectory, s_projectDirectory, Environment.Get);
		Assert.AreEqual(s_commandsFolder, Catalog.SkippedProjectCommands);
		Assert.IsEmpty(Catalog.ExternalFolders);
		Assert.IsFalse(Catalog.LoadExternal());
		Assert.IsNull(Catalog.Match(["sample", "hello"]).Command);
	}

	[TestMethod]
	[DataRow("1", true)]
	[DataRow(" TRUE ", true)]
	[DataRow("true", true)]
	[DataRow("0", false)]
	[DataRow("", false)]
	[DataRow(null, false)]
	public void ProjectCommandsOptIn_Values(string? value, bool expected) => Assert.AreEqual(expected, UakCommandCatalog.IsProjectCommandsOptIn(value));

	[TestMethod]
	public void Load_ProjectFolderOnTheCommandPaths_IsAnOptIn()
	{
		FakeEnvironment Environment = new() { [UakCommandCatalog.CommandPathsVariable] = s_commandsFolder + Path.DirectorySeparatorChar };
		UakCommandCatalog Catalog = UakCommandCatalog.Load(AppContext.BaseDirectory, s_projectDirectory, Environment.Get);
		Assert.IsNull(Catalog.SkippedProjectCommands);
		Assert.HasCount(1, Catalog.ExternalFolders);
		Catalog.LoadExternal();
		Assert.IsNotNull(Catalog.Match(["sample", "hello"]).Command, string.Join('\n', Catalog.Problems));
	}

	[TestMethod]
	public void Load_SkipsRelativeCommandPaths_WithAWarning()
	{
		// A relative folder would be found from the current directory: a cloned repository could opt its own code in.
		string Relative = Path.Combine(".uak", "commands");
		string Rooted = Path.DirectorySeparatorChar + Path.Combine("uak", "commands");
		FakeEnvironment Environment = new() { [UakCommandCatalog.CommandPathsVariable] = string.Join(Path.PathSeparator, Relative, s_commandsFolder, Rooted) };
		UakCommandCatalog Catalog = UakCommandCatalog.Load(AppContext.BaseDirectory, null, Environment.Get);
		CollectionAssert.AreEqual(OperatingSystem.IsWindows() ? new[] { s_commandsFolder } : new[] { s_commandsFolder, Rooted }, Catalog.ExternalFolders.ToArray());
		Assert.IsTrue(Catalog.Problems.Any(Problem => Problem.Contains($"skipped \"{Relative}\"", StringComparison.Ordinal) && Problem.Contains("only absolute folders", StringComparison.Ordinal)),
			string.Join('\n', Catalog.Problems));
		if (OperatingSystem.IsWindows())
		{
			// "\uak\commands" is rooted but relative to the current drive: skipped too.
			Assert.IsTrue(Catalog.Problems.Any(Problem => Problem.Contains($"skipped \"{Rooted}\"", StringComparison.Ordinal)), string.Join('\n', Catalog.Problems));
		}
		Assert.AreEqual(OperatingSystem.IsWindows() ? 2 : 1, Catalog.Problems.Count(Problem => Problem.StartsWith(UakCommandCatalog.CommandPathsVariable, StringComparison.Ordinal)));
	}

	[TestMethod]
	public void Load_FindsCommandsOnTheCommandPaths_AndReportsMissingFolders()
	{
		string Missing = Path.Combine(s_projectDirectory, "missing");
		FakeEnvironment Environment = new() { [UakCommandCatalog.CommandPathsVariable] = s_commandsFolder + Path.PathSeparator + Missing };
		UakCommandCatalog Catalog = UakCommandCatalog.Load(AppContext.BaseDirectory, null, Environment.Get);
		Assert.IsFalse(Catalog.Problems.Any(Problem => Problem.Contains(Missing, StringComparison.Ordinal)), "folders are not touched until loaded");
		Catalog.LoadExternal();
		Assert.IsNotNull(Catalog.Match(["sample", "hello"]).Command, string.Join('\n', Catalog.Problems));
		Assert.IsTrue(Catalog.Problems.Any(Problem => Problem.Contains(Missing, StringComparison.Ordinal)));
	}

	[TestMethod]
	public void Load_WithoutTheFolder_HasNoProjectCommands()
	{
		FakeEnvironment Environment = new() { [UakCommandCatalog.ProjectCommandsVariable] = "1" };
		UakCommandCatalog Catalog = UakCommandCatalog.Load(AppContext.BaseDirectory, Path.GetTempPath(), Environment.Get);
		Catalog.LoadExternal();
		Assert.IsNull(Catalog.Match(["sample", "hello"]).Command);
		Assert.IsNull(Catalog.SkippedProjectCommands);
	}

	sealed class HostRun
	{
		public int ExitCode;
		public string Out = "";
		public string Err = "";
		public UakCommandCatalog? Catalog;
	}

	/// <summary>Runs the host in <paramref name="projectDirectory"/> with the real catalog loader, and keeps the catalog it built.</summary>
	static async Task<HostRun> RunInProject(TempTree tree, string projectDirectory, FakeEnvironment environment, params string[] arguments)
	{
		HostRun Run = new();
		StringWriter Out = new();
		StringWriter Err = new();
		UakHostOptions Options = new()
		{
			Out = Out,
			Error = Err,
			LoadCatalog = Directory => Run.Catalog = UakCommandCatalog.Load(AppContext.BaseDirectory, Directory, environment.Get),
			ResolveOptions = new UakResolveOptions
			{
				CurrentDirectory = projectDirectory,
				GetEnvironmentVariable = environment.Get,
				AssociationSource = new FakeAssociationSource(),
				UserHomeDirectory = tree.Dir("home"),
				// Keep the state folder out of the project folder.
				IsWritable = Path => Path.StartsWith(tree.Path("home"), StringComparison.OrdinalIgnoreCase),
			},
		};
		Run.ExitCode = await UakHost.RunAsync(arguments, Options);
		Run.Out = Out.ToString();
		Run.Err = Err.ToString();
		return Run;
	}

	/// <summary>A new project whose .uak/commands holds a copy of the sample command assembly, in a folder no other test uses.</summary>
	static string FreshProjectWithCommands(TempTree tree)
	{
		string Project = tree.Dir("FreshProject");
		File.WriteAllText(Path.Combine(Project, "Fresh.uproject"), """{ "EngineAssociation": "5.8" }""");
		string Commands = tree.Dir("FreshProject", ".uak", "commands");
		File.Copy(Path.Combine(AppContext.BaseDirectory, "SampleCommands", "UakSampleCommands.dll"), Path.Combine(Commands, "UakSampleCommands.dll"));
		return Project;
	}

	static void AssertNothingLoadedFrom(string folder)
	{
		string Prefix = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
		string[] Loaded = AppDomain.CurrentDomain.GetAssemblies()
			.Where(Assembly => !Assembly.IsDynamic && Assembly.Location.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
			.Select(Assembly => Assembly.Location)
			.ToArray();
		Assert.IsEmpty(Loaded, "loaded from the project: " + string.Join(", ", Loaded));
	}

	[TestMethod]
	public async Task Host_NotOptedIn_NeverLoadsProjectCode_AndSaysSoForHelpAndEnv()
	{
		using TempTree Tree = new();
		string Project = FreshProjectWithCommands(Tree);
		FakeEnvironment Environment = new();

		HostRun Env = await RunInProject(Tree, Project, Environment, "env");
		Assert.AreEqual(0, Env.ExitCode, Env.Err);
		StringAssert.Contains(Env.Err, UakCommandCatalog.ProjectCommandsVariable + "=1");
		Assert.AreEqual(1, Env.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(Line => Line.Contains(".uak", StringComparison.Ordinal)), Env.Err);

		HostRun Help = await RunInProject(Tree, Project, Environment, "help");
		Assert.AreEqual(0, Help.ExitCode, Help.Err);
		StringAssert.Contains(Help.Err, UakCommandCatalog.ProjectCommandsVariable);

		HostRun All = await RunInProject(Tree, Project, Environment, "help", "-all");
		Assert.AreEqual(0, All.ExitCode, All.Err);
		Assert.IsFalse(All.Out.Contains("sample hello", StringComparison.Ordinal), All.Out);

		HostRun Sample = await RunInProject(Tree, Project, Environment, "sample", "hello");
		Assert.AreEqual(UakExitCodes.UsageError, Sample.ExitCode);
		StringAssert.Contains(Sample.Err, "unknown command");
		Assert.IsFalse(Sample.Err.Contains(UakCommandCatalog.ProjectCommandsVariable, StringComparison.Ordinal), "the note is only for help and env");

		HostRun Kit = await RunInProject(Tree, Project, Environment, "test", "echo");
		Assert.AreEqual(0, Kit.ExitCode, Kit.Err);
		Assert.IsFalse(Kit.Err.Contains("project commands", StringComparison.Ordinal), Kit.Err);

		AssertNothingLoadedFrom(Path.Combine(Project, ".uak"));
	}

	[TestMethod]
	public async Task Host_OptedIn_LoadsProjectCode_OnlyForACommandTheKitLacks()
	{
		using TempTree Tree = new();
		FakeEnvironment Environment = new() { [UakCommandCatalog.ProjectCommandsVariable] = "1" };

		// A folder no other test loads from: env, help and kit commands leave it alone.
		string Fresh = FreshProjectWithCommands(Tree);
		foreach (string[] Arguments in new[] { new[] { "env" }, ["help"], ["help", "env"], ["test", "echo"] })
		{
			HostRun Run = await RunInProject(Tree, Fresh, Environment, Arguments);
			Assert.AreEqual(0, Run.ExitCode, Run.Err);
			Assert.IsFalse(Run.Catalog!.ExternalLoaded, string.Join(' ', Arguments));
			Assert.IsFalse(Run.Err.Contains("holds project commands", StringComparison.Ordinal), Run.Err);
			StringAssert.Contains(Run.Catalog.ExternalFolders.Single(), Fresh);
		}
		AssertNothingLoadedFrom(Path.Combine(Fresh, ".uak"));

		// The class's shared folder (one load path per process): an unknown command loads it, and runs.
		HostRun Sample = await RunInProject(Tree, s_projectDirectory, Environment, "sample", "hello", "world");
		Assert.AreEqual(0, Sample.ExitCode, Sample.Err);
		Assert.IsTrue(Sample.Catalog!.ExternalLoaded);

		HostRun All = await RunInProject(Tree, s_projectDirectory, Environment, "help", "-all");
		Assert.IsTrue(All.Catalog!.ExternalLoaded);
		StringAssert.Contains(All.Out, "sample hello");

		HostRun HelpOne = await RunInProject(Tree, s_projectDirectory, Environment, "help", "sample", "hello");
		Assert.AreEqual(0, HelpOne.ExitCode, HelpOne.Err);
		StringAssert.Contains(HelpOne.Out, "uak sample hello [words...]");

		HostRun Overview = await RunInProject(Tree, s_projectDirectory, Environment, "help");
		Assert.IsFalse(Overview.Catalog!.ExternalLoaded);
		StringAssert.Contains(Overview.Out, "'uak help -all' lists them");
	}

	[TestMethod]
	public async Task Host_RunsAProjectCommand()
	{
		using TempTree Tree = new();
		UakHostOptions Options = new()
		{
			Out = new StringWriter(),
			Error = new StringWriter(),
			ResolveOptions = new UakResolveOptions
			{
				CurrentDirectory = s_projectDirectory,
				GetEnvironmentVariable = new FakeEnvironment { [UakCommandCatalog.ProjectCommandsVariable] = "1" }.Get,
				AssociationSource = new FakeAssociationSource(),
				UserHomeDirectory = Tree.Dir("home"),
				// Keep the state folder out of the shared project folder.
				IsWritable = Path => Path.StartsWith(Tree.Root, StringComparison.OrdinalIgnoreCase),
			},
		};
		Assert.AreEqual(0, await UakHost.RunAsync(["sample", "hello", "world"], Options), Options.Error.ToString());
		Assert.AreEqual(1, await UakHost.RunAsync(["sample", "hello", "fail"], Options), Options.Error.ToString());
	}
}

[TestClass]
public sealed class EnvCommandTests
{
	sealed class ThrowingReporter : IUakEnvReporter
	{
		public Task<IReadOnlyList<KeyValuePair<string, string>>> ReportAsync(UakContext context, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("reporter broke");
	}

	sealed class VcsReporter : IUakEnvReporter
	{
		public Task<IReadOnlyList<KeyValuePair<string, string>>> ReportAsync(UakContext context, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([new(EnvCommand.VersionControlLabel, "Git at somewhere")]);
	}

	static UakContext Context(TempTree tree, bool withEngine)
	{
		string? Engine = withEngine ? tree.Engine("UE") : null;
		string Project = tree.Project("Game/Game.uproject");
		return UakContextResolver.Resolve(new UakResolveOptions
		{
			ProjectArgument = Project,
			EngineArgument = Engine,
			RequireEngine = false,
			CurrentDirectory = tree.Root,
			GetEnvironmentVariable = new FakeEnvironment().Get,
			AssociationSource = new FakeAssociationSource(),
			UserHomeDirectory = tree.Dir("home"),
		});
	}

	[TestMethod]
	public async Task Text_ShowsPathsProvenanceAndReporters()
	{
		using TempTree Tree = new();
		StringWriter Out = new();
		EnvCommand Command = new() { Output = Out, Reporters = [new VcsReporter(), new ThrowingReporter()] };
		Assert.AreEqual(0, await Command.RunAsync(Context(Tree, withEngine: true), [], CancellationToken.None));
		string Text = Out.ToString();
		StringAssert.Contains(Text, "Project:");
		StringAssert.Contains(Text, "-project argument");
		StringAssert.Contains(Text, "Engine version:");
		StringAssert.Contains(Text, "5.8.1-123");
		StringAssert.Contains(Text, "Bundled dotnet:");
		StringAssert.Contains(Text, "(missing)");
		StringAssert.Contains(Text, "Version control:");
		StringAssert.Contains(Text, "Git at somewhere");
		StringAssert.Contains(Text, "error: reporter broke");
	}

	[TestMethod]
	public async Task NoReporter_SaysUnknown_AndWorksWithoutAnEngine()
	{
		using TempTree Tree = new();
		StringWriter Out = new();
		EnvCommand Command = new() { Output = Out, Reporters = [] };
		Assert.AreEqual(0, await Command.RunAsync(Context(Tree, withEngine: false), [], CancellationToken.None));
		StringAssert.Contains(Out.ToString(), "Version control:");
		StringAssert.Contains(Out.ToString(), "unknown");
		StringAssert.Contains(Out.ToString(), "Engine:");
	}

	[TestMethod]
	public async Task Json_IsOneObject()
	{
		using TempTree Tree = new();
		StringWriter Out = new();
		EnvCommand Command = new() { Output = Out, Reporters = [new VcsReporter()] };
		Assert.AreEqual(0, await Command.RunAsync(Context(Tree, withEngine: true), ["-json"], CancellationToken.None));
		Dictionary<string, string> Values = JsonSerializer.Deserialize<Dictionary<string, string>>(Out.ToString())!;
		Assert.AreEqual(Tree.Path("Game", "Game.uproject"), Values["Project"]);
		Assert.AreEqual("Git at somewhere", Values[EnvCommand.VersionControlLabel]);
	}

	[TestMethod]
	public async Task UnknownOption_IsAUsageError()
	{
		using TempTree Tree = new();
		EnvCommand Command = new() { Output = new StringWriter(), Reporters = [] };
		await Assert.ThrowsAsync<UakUsageException>(() => Command.RunAsync(Context(Tree, withEngine: false), ["-nope"], CancellationToken.None));
	}
}
