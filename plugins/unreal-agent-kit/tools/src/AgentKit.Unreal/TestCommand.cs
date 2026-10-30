// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Globalization;
using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Vcs;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>How a test run ended, which decides <c>uak test</c>'s exit code and <c>uak mutate</c>'s verdict.</summary>
internal enum TestVerdict
{
	/// <summary>A clean pass (with -allow-unexpected, also one that completed tests -expect doesn't list).</summary>
	Passed,

	/// <summary>Every check passed, and every listed test, but tests -expect doesn't list completed too.</summary>
	PassedWithUnexpected,

	/// <summary>A failure: a test failed, a listed test did not pass, the run crashed or never started its tests.</summary>
	Failed,

	/// <summary>No test passed and some were skipped: nothing was checked.</summary>
	NothingRan,
}

/// <summary>What a test run found.</summary>
internal sealed class TestRunOutcome
{
	public required TestVerdict Verdict { get; init; }

	/// <summary>The parsed log; empty when the editor or game wrote none.</summary>
	public AutomationLogSummary Summary { get; init; } = new();

	/// <summary>Why the run failed; empty when it did not.</summary>
	public List<string> Problems { get; init; } = [];

	/// <summary>
	/// Listed tests (-expect) that completed with a failure (Result Fail): a test result, unlike a listed test that was skipped
	/// or never completed, which may be the machine's doing.
	/// </summary>
	public int ListedFailed { get; init; }

	/// <summary>Whether the run started its tests: its log has "Found N automation tests" with N above 0.</summary>
	public bool TestsStarted => Summary.FoundCount > 0;

	/// <summary><c>uak test</c>'s exit code.</summary>
	public int ExitCode => Verdict switch
	{
		TestVerdict.Passed => UakExitCodes.Success,
		TestVerdict.PassedWithUnexpected => TestCommand.UnexpectedTestsExitCode,
		_ => UakExitCodes.Failure,
	};

	/// <summary>The result file's Outcome.</summary>
	public string ResultOutcome => Verdict switch
	{
		TestVerdict.Passed => "passed",
		TestVerdict.PassedWithUnexpected => "unexpected tests",
		TestVerdict.NothingRan => "nothing ran",
		_ => "failed",
	};

	/// <summary>The summary's closing lines.</summary>
	public List<string> Lines { get; init; } = [];
}

/// <summary>
/// `uak test -filter=&lt;filter&gt;`: runs the project's automation tests under the editor lock, in an editor (headless, unless
/// -windowed) or, with -cooked, in a cooked game that UAT's BuildCookRun builds, cooks, stages and runs; and reports passed,
/// failed, skipped and total tests from the editor's log or the game's Client.log, checked against an -expect file when one
/// is given, with the tree it ran on, the watched log categories' errors and warnings, and filter mistakes.
/// </summary>
public sealed class TestCommand : IUakCommand
{
	/// <summary>The exit code of a run whose listed tests all passed, but which also completed tests -expect doesn't list.</summary>
	public const int UnexpectedTestsExitCode = 3;

	readonly UnrealServices Services;

	/// <summary>Creates the command with the real services.</summary>
	public TestCommand() : this(new UnrealServices())
	{
	}

	/// <summary>Creates the command with the given services (for tests).</summary>
	public TestCommand(UnrealServices services)
	{
		Services = services;
	}

	/// <inheritdoc/>
	public string Name => "test";

	/// <inheritdoc/>
	public string Summary => "Run automation tests in an editor (headless by default) or a cooked game, under the editor lock.";

	/// <inheritdoc/>
	public string Usage =>
		"""
		uak test -filter=<filter> [options] [-- <editor argument>...]
		uak test -cooked -filter=<filter> [options] [-- <game argument>...]
		  -filter=<filter>      The tests to run, as "Automation RunTests" takes them: text anywhere in the test path,
		                        StartsWith:<prefix>, ^<start> or <end>$; several joined with '+'. A part that matches
		                        no test, and a part without a trailing '.' that also matches another name (Game
		                        matching GameBench.*), are warned about.
		  -expect=<file>        The tests the run must pass: one full test name per line; blank lines and lines starting
		                        with # are ignored. The run expects the listed names its -filter matches (a Group: filter
		                        can't be matched); each must pass. Other tests that complete are "unexpected": listed,
		                        and exit code 3 when nothing else is wrong.
		  -allow-unexpected     With -expect: unexpected tests don't change the exit code (still listed).
		  -watch=<cat>[,<cat>]  Count these log categories' Error and Warning lines in the run's log (Client.log for
		                        -cooked), e.g. -watch=LogSaveGame. A message a test expects is logged at Verbose by the
		                        automation framework, so only real Errors and Warnings count.
		  -quiet                For benchmarks: refuse to start while others wait for the lock, and hold it so that
		                        `uak compile` waits too (running compiles finish first).
		  -name=<name>          Names the run's logs under <Project>/Saved/Logs (default uak-test; uak-test-cooked with
		                        -cooked), and its summary for `uak runs show -name=<name>`. Each run replaces its
		                        name's logs as it starts, so every run without -name= shares the default ones: give a
		                        name of its own to keep a run's log.
		  -resultfile=<path>    Also write the result as JSON. Deleted first, so an old one is never read
		                        as this run's; a refused or stopped run writes Outcome refused or stopped.
		Editor runs. The editor's log is <Project>/Saved/Logs/<name>.log.
		  -gpu                  Render with a real RHI off screen (-RenderOffscreen) instead of -nullrhi.
		  -windowed             Run the editor itself (not -Cmd) in a window (-windowed -ResX= -ResY=), for tests that
		                        need a real viewport and Slate windows. A window opens on this desktop. Not with -gpu.
		  -resx=<n> -resy=<n>   The window size with -windowed (default 1600 x 900).
		  -- <editor argument>  More editor arguments, passed as given, e.g. -- -SCCProvider=None. Rejected because uak
		                        sets them: -ExecCmds, -testexit, -abslog, -ReportExportPath, -nullrhi, -RenderOffscreen,
		                        -windowed, -ResX, -ResY.
		Cooked runs (-cooked). UAT BuildCookRun builds the game, cooks, stages and paks the project, and runs the game
		with -nullrhi until the test queue empties. The game's log is <Project>/Saved/Logs/<name>/Client.log (UAT's log
		folder for the run, which UAT empties first); UAT's output is <Project>/Saved/Logs/<name>.log.
		  -map=<map>[+<map>...] The maps to cook, the first of which the game opens, e.g. -map=/Engine/Maps/Entry
		                        (default: those `uak test config -cooked-maps=` saved, else the project's maps).
		  -config=<config>      The game's configuration: Debug, DebugGame, Development (default) or Test.
		  -platform=<platform>  The host platform, the default: the game runs on this machine.
		  -skipbuild            Use the game's binaries as built (no -build).
		  -skipcook             Use the build as staged (-skipcook -skipstage instead of -cook -stage).
		  -timeout=<seconds>    How long the game may run before UAT stops it (-RunTimeoutSeconds, default 1800).
		  -- <game argument>    More game arguments, through UAT's -addcmdline. Rejected: -ExecCmds, -testexit, -abslog,
		                        -ReportExportPath, quotes, and % ! & | < > ^.
		An editor run passes only when the editor found tests, every found test completed ("Found N automation tests"),
		at least one passed (all skipped is NOTHING RAN), none failed, the queue finished with exit code 0 ("TEST
		COMPLETE. EXIT CODE: 0"), and the editor exited with 0. A cooked run makes the same checks of Client.log, and
		passes only when UAT exited with 0; the game's own exit code is reported, not judged. -expect adds its check.
		The summary names the tree as the run found it once it held the lock: the revision and the changed files (or
		their count and hash). It is read alongside the run: no proof of what was tested (-skipbuild -skipcook reuse an
		earlier staged build). It refuses to run while a stopped `uak mutate` has left its patch in the tree or its binaries
		(`uak mutate -restore`, on its own).
		Exit code: 0 passed, 1 failed, 2 usage or setup error, 3 passed except that tests -expect doesn't list
		completed too (0 with -allow-unexpected).
		""";

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return UnrealServices.GuardAsync(context, () => SummaryWriter.RecordingAsync(context, Services, "test", PeekName(arguments),
			() => UnrealServices.WithFreshResultFileAsync("test", arguments, () => RunCheckedAsync(context, arguments, cancellationToken))));
	}

	/// <summary>The -name= the run records its summary under, read before anything else is checked (null when it can't be read).</summary>
	static string? PeekName(IReadOnlyList<string> arguments)
	{
		try
		{
			UakArguments Arguments = new(arguments);
			return Arguments.GetString("name") ?? (Arguments.GetFlag("cooked") ? "uak-test-cooked" : "uak-test");
		}
		catch (UakUsageException)
		{
			return null;
		}
	}

	async Task<int> RunCheckedAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments Arguments = new(arguments);
		TestOptions Options = TestOptions.Parse(Arguments, "uak-test");
		Arguments.ThrowIfUnknown();
		if (Arguments.Positional.Count > 0)
		{
			throw new UakUsageException("uak test takes no positional arguments: " + string.Join(' ', Arguments.Positional));
		}
		// Read before the lock, so a bad file is a usage error and never waits for the lock.
		ExpectedTests? Expected = Options.ExpectFile is null ? null : ExpectedTests.Load(Options.ExpectFile, Options.Filter);
		if (Options.Quiet)
		{
			CheckQuietStart(context);
		}

		TestRunOutcome Outcome = Options.Cooked
			? await RunCookedAsync(context, Options, Expected, cancellationToken).ConfigureAwait(false)
			: await RunInEditorAsync(context, Options, Expected, heldLock: null, cancellationToken).ConfigureAwait(false);
		return Outcome.ExitCode;
	}

	/// <summary>A quiet run starts only when nobody else waits for the lock: it would otherwise hold them up, measuring.</summary>
	void CheckQuietStart(UakContext context)
	{
		int Waiting = Services.Lock.CountWaiting(context);
		if (Waiting > 0)
		{
			throw new UakUsageException($"-quiet: {Waiting} other {(Waiting == 1 ? "run waits" : "runs wait")} in line for the editor lock (`uak lock status`);"
				+ " a quiet run starts only when nobody waits. Start it again once the queue is empty.");
		}
	}

	/// <summary>
	/// Runs editor tests and reports them. Without <paramref name="heldLock"/> it takes the editor lock (quiet with -quiet)
	/// and first refuses to run while a mutation run's changes may be left in the tree; <c>uak mutate</c> passes the lock it
	/// holds, and checks that itself.
	/// </summary>
	internal async Task<TestRunOutcome> RunInEditorAsync(UakContext context, TestOptions options, ExpectedTests? expected, IAsyncDisposable? heldLock,
		CancellationToken cancellationToken)
	{
		string Project = UnrealServices.RequireProject(context);
		EngineLayout Paths = Services.Paths(context);
		string ProjectDirectory = Path.GetDirectoryName(Project)!;
		string LogFile = Path.Combine(ProjectDirectory, "Saved", "Logs", options.Name + ".log");
		string ReportDirectory = Path.Combine(context.StateDirectory.FullName, "TestReports", options.Name);
		EditorLocation Editor = EditorLocator.Locate(Paths, Project);
		ProcessInvocation Invocation = EditorTestCommandLine.Build(Editor, Project, options.Filter, options.Rendering, LogFile, ReportDirectory, options.PassThrough,
			options.ResX, options.ResY);
		if (!File.Exists(Invocation.FileName))
		{
			throw new UakSetupException($"editor not found: {Invocation.FileName} (from {Editor.How})");
		}

		int ExitCode;
		Stopwatch Timer;
		Queue<string> LastLines = new();
		List<string>? LogLines;
		Task<VcsTreeState> TreeTask;
		IAsyncDisposable? OwnLock = heldLock is null
			? await Services.Lock.AcquireAsync(context, $"uak test {options.Filter}", options.Quiet, cancellationToken).ConfigureAwait(false)
			: null;
		await using (OwnLock)
		{
			IAsyncDisposable Lock = heldLock ?? OwnLock!;
			if (heldLock is null)
			{
				MutationJournal.ThrowIfUnfinished(context);
			}
			// Read alongside the run, so it never holds the lock up.
			TreeTask = Services.StartCaptureTree(context, cancellationToken);
			// A log left by an earlier run must never be read as this run's.
			Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
			File.Delete(LogFile);
			string Rhi = options.Rendering switch
			{
				EditorRendering.Windowed => $"windowed {options.ResX}x{options.ResY}",
				EditorRendering.Offscreen => "GPU",
				_ => "nullrhi",
			};
			context.Logger.LogInformation("Running automation tests '{Filter}' ({Rhi})...", options.Filter, Rhi);
			Timer = Stopwatch.StartNew();
			// The editor's stdout repeats its log; keep only the end, for a crash that never reached the log.
			ExitCode = await Services.RunEditorAsync(Invocation, Line => KeepLast(LastLines, Line), Lock, cancellationToken).ConfigureAwait(false);
			Timer.Stop();
			// Read the log while the lock is still held: the next holder of the same -name deletes it before its own run.
			LogLines = File.Exists(LogFile) ? ReadShared(LogFile) : null;
		}
		VcsTreeState Tree = await TreeTask.ConfigureAwait(false);

		if (LogLines is null)
		{
			// The windowed editor is a GUI program on Windows, which writes nothing to its console output.
			context.Logger.LogError("The editor wrote no log (exit code {Code}). Its last output{None}:", ExitCode, LastLines.Count == 0 ? " (none)" : "");
			foreach (string Line in LastLines)
			{
				context.Logger.LogError("    {Line}", Line);
			}
			SummaryWriter NoLog = new(context.Logger);
			NoLog.Info("tree: " + Tree);
			NoLog.Error(string.Create(CultureInfo.InvariantCulture, $"test: FAILED: the editor wrote no log (exit code {ExitCode}). {Timer.Elapsed.TotalSeconds:0.0} s. Log: {LogFile}"));
			if (options.RecordSummary)
			{
				NoLog.Record(context, Services, "test", options.Name, UakExitCodes.Failure);
			}
			UnrealServices.WriteResultFile(options.ResultFile, new
			{
				Mode = "editor",
				Outcome = "failed",
				options.Filter,
				EditorExitCode = ExitCode,
				Problems = new[] { "the editor wrote no log" },
				Tree,
				Seconds = Math.Round(Timer.Elapsed.TotalSeconds, 1),
				LogFile,
			});
			return new TestRunOutcome { Verdict = TestVerdict.Failed, Problems = ["the editor wrote no log"], Lines = NoLog.Lines };
		}
		AutomationLogSummary Summary = AutomationLogParser.Parse(LogLines);
		List<string> Extra = ExitCode != 0 ? [$"the editor exited with code {ExitCode}"] : [];
		List<LogCategoryCount> Watch = LogCategoryCounts.Count(LogLines, options.Watch);
		string Tail = string.Create(CultureInfo.InvariantCulture, $". {Timer.Elapsed.TotalSeconds:0.0} s. Log: {LogFile}");
		(TestRunOutcome Outcome, ExpectCheck? Check, List<string> FilterWarnings) = Finish(context, options, expected, Summary, Extra, Tree, Watch, "", Tail);
		UnrealServices.WriteResultFile(options.ResultFile, new
		{
			Mode = "editor",
			Outcome = Outcome.ResultOutcome,
			options.Filter,
			Summary.FoundCount,
			Total = Summary.Tests.Count,
			Summary.Passed,
			Summary.Failed,
			Summary.Skipped,
			NotRun = Summary.NotRunNames,
			EditorExitCode = ExitCode,
			Summary.TestCompleteExitCode,
			Outcome.Problems,
			Failures = Failures(Summary),
			Expect = ExpectResult(expected, Check, options.AllowUnexpected),
			Tree,
			Watch,
			FilterWarnings,
			Seconds = Math.Round(Timer.Elapsed.TotalSeconds, 1),
			LogFile,
			ReportDirectory,
		});
		return Outcome;
	}

	async Task<TestRunOutcome> RunCookedAsync(UakContext context, TestOptions options, ExpectedTests? expected, CancellationToken cancellationToken)
	{
		UnrealPlatform Platform = options.PlatformName is null || options.PlatformName.Equals("host", StringComparison.OrdinalIgnoreCase)
			? Services.HostPlatform
			: UnrealPlatform.FromName(options.PlatformName) ?? throw new UakUsageException($"unknown -platform={options.PlatformName} (Win64, Linux, Mac or host)");
		if (Platform != Services.HostPlatform)
		{
			throw new UakUsageException($"-platform={options.PlatformName}: a cooked test run runs the game on this machine, so it is for the host platform ({Services.HostPlatform.Name}) only");
		}
		string Project = UnrealServices.RequireProject(context);
		string? Maps = options.Maps;
		if (Maps is null && ProjectSettings.GetCookedMaps(context) is string Saved)
		{
			Maps = Saved;
			context.Logger.LogInformation("Maps: {Maps} (saved by `uak test config`; -map= overrides)", Saved);
		}
		CookedTestRun Run = new()
		{
			ProjectFile = Project,
			Filter = options.Filter,
			Platform = Platform,
			Configuration = options.Configuration is null ? "Development" : CookedTestCommandLine.ParseConfiguration(options.Configuration),
			Maps = CookedTestCommandLine.ParseMaps(Maps),
			SkipBuild = options.SkipBuild,
			SkipCook = options.SkipCook,
			TimeoutSeconds = options.Timeout ?? CookedTestCommandLine.DefaultTimeoutSeconds,
			GameArguments = options.PassThrough,
			LogFolder = Path.Combine(Path.GetDirectoryName(Project)!, "Saved", "Logs", options.Name),
		};

		EngineLayout Paths = Services.Paths(context);
		ProcessInvocation Invocation = CookedTestCommandLine.Build(Paths, Run);
		string ClientLog = CookedTestCommandLine.ClientLogFile(Run.LogFolder);
		string UatLog = Path.Combine(Path.GetDirectoryName(Run.LogFolder)!, options.Name + ".log");
		// Named first, so a long build, cook or run can be followed in its logs while it runs.
		context.Logger.LogInformation("Client log: {Log}", ClientLog);
		context.Logger.LogInformation("UAT log: {Log}", UatLog);

		int ExitCode;
		Stopwatch Timer;
		Queue<string> LastLines = new();
		UatOutputParser Uat = new();
		List<string>? LogLines;
		Task<VcsTreeState> TreeTask;
		await using (IAsyncDisposable Lock = await Services.Lock.AcquireAsync(context, $"uak test -cooked {Run.Filter}", options.Quiet, cancellationToken).ConfigureAwait(false))
		{
			MutationJournal.ThrowIfUnfinished(context);
			// Read alongside the run, so it never holds the lock up.
			TreeTask = Services.StartCaptureTree(context, cancellationToken);
			// Logs left by an earlier run must never be read as this run's. (UAT also empties the folder when no other UAT runs.)
			Directory.CreateDirectory(Run.LogFolder);
			File.Delete(ClientLog);
			string Steps = (Run.SkipBuild ? "" : "build, ") + (Run.SkipCook ? "staged build reused" : "cook, stage, pak") + ", run";
			context.Logger.LogInformation("Running automation tests '{Filter}' in a cooked {Platform} {Configuration} game ({Steps})...",
				Run.Filter, Run.Platform.Name, Run.Configuration, Steps);
			await using (StreamWriter Log = new(UatLog, append: false, UakJson.Utf8NoBom) { AutoFlush = true })
			{
				await Log.WriteLineAsync($"> {CookedTestCommandLine.LogFolderVariable}={Path.GetFullPath(Run.LogFolder)}").ConfigureAwait(false);
				await Log.WriteLineAsync("> " + Invocation).ConfigureAwait(false);
				Timer = Stopwatch.StartNew();
				ExitCode = await Services.RunEditorAsync(Invocation, Line =>
				{
					Log.WriteLine(Line);
					KeepLast(LastLines, Line);
					if (Uat.AddLine(Line) is string Step)
					{
						context.Logger.LogInformation("  {Step}", Step);
					}
				}, Lock, cancellationToken).ConfigureAwait(false);
				Timer.Stop();
			}
			// Read the log while the lock is still held: the next run with the same -name deletes it before its own.
			LogLines = File.Exists(ClientLog) ? ReadShared(ClientLog) : null;
		}
		VcsTreeState Tree = await TreeTask.ConfigureAwait(false);

		AutomationLogSummary Summary = LogLines is null ? new AutomationLogSummary() : AutomationLogParser.Parse(LogLines);
		List<string> Extra = [];
		if (LogLines is null)
		{
			Extra.Add($"the game wrote no log: {ClientLog} is missing");
		}
		if (Uat.Summary.TimedOutAfterSeconds is int Seconds)
		{
			Extra.Add($"the game ran past -timeout={Seconds} and UAT stopped it");
		}
		if (ExitCode != 0)
		{
			Extra.Add($"UAT exited with code {ExitCode}");
		}
		if (LogLines is null || ExitCode != 0)
		{
			context.Logger.LogError("UAT's last output{None}:", LastLines.Count == 0 ? " (none)" : "");
			foreach (string Line in LastLines)
			{
				context.Logger.LogError("    {Line}", Line);
			}
		}
		List<LogCategoryCount> Watch = LogCategoryCounts.Count(LogLines ?? [], options.Watch);
		string Tail = string.Create(CultureInfo.InvariantCulture,
			$". UAT exit {ExitCode}, game exit {Uat.Summary.ClientExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}. {Timer.Elapsed.TotalSeconds:0.0} s. Client log: {ClientLog}");
		(TestRunOutcome Outcome, ExpectCheck? Check, List<string> FilterWarnings) = Finish(context, options, expected, Summary, Extra, Tree, Watch, " in Client.log", Tail);
		UnrealServices.WriteResultFile(options.ResultFile, new
		{
			Mode = "cooked",
			Outcome = Outcome.ResultOutcome,
			Run.Filter,
			Platform = Run.Platform.Name,
			Run.Configuration,
			Run.Maps,
			Summary.FoundCount,
			Total = Summary.Tests.Count,
			Summary.Passed,
			Summary.Failed,
			Summary.Skipped,
			NotRun = Summary.NotRunNames,
			UatExitCode = ExitCode,
			GameExitCode = Uat.Summary.ClientExitCode,
			Uat.Summary.TimedOutAfterSeconds,
			Summary.TestCompleteExitCode,
			Outcome.Problems,
			Failures = Failures(Summary),
			Expect = ExpectResult(expected, Check, options.AllowUnexpected),
			Tree,
			Watch,
			FilterWarnings,
			Seconds = Math.Round(Timer.Elapsed.TotalSeconds, 1),
			ClientLog,
			UatLog,
		});
		return Outcome;
	}

	/// <summary>
	/// Judges a run and prints its report: the skipped, never-run and failed tests, then the summary (recorded for
	/// <c>uak runs show</c>): the tree, the watched categories, the filter's warnings, the expect check, the problems and the
	/// verdict line, which ends with <paramref name="tail"/>.
	/// </summary>
	(TestRunOutcome Outcome, ExpectCheck? Check, List<string> FilterWarnings) Finish(UakContext context, TestOptions options, ExpectedTests? expected,
		AutomationLogSummary summary, List<string> extraProblems, VcsTreeState tree, List<LogCategoryCount> watch, string watchSource, string tail)
	{
		ExpectCheck? Check = expected?.Check(summary);
		List<string> Problems = [.. summary.Problems(), .. extraProblems, .. Check?.Problems() ?? []];
		bool NothingRan = summary.Passed == 0 && summary.Skipped > 0;
		bool Unexpected = Check is not null && Check.Unexpected.Count > 0;
		TestVerdict Verdict = Problems.Count > 0
			? NothingRan ? TestVerdict.NothingRan : TestVerdict.Failed
			: Unexpected && !options.AllowUnexpected ? TestVerdict.PassedWithUnexpected : TestVerdict.Passed;

		ReportTests(context, summary, Check);
		SummaryWriter Out = new(context.Logger);
		Out.Info("tree: " + tree);
		foreach (LogCategoryCount Count in watch)
		{
			string Line = $"watch: {Count}{watchSource}";
			if (Count.Errors > 0)
			{
				Out.Error(Line);
			}
			else if (Count.Warnings > 0)
			{
				Out.Warning(Line);
			}
			else
			{
				Out.Info(Line);
			}
		}
		List<string> FilterWarnings = GetFilterWarnings(options.Filter, summary);
		foreach (string Warning in FilterWarnings)
		{
			Out.Warning(Warning);
		}
		if (expected is not null && Check is not null)
		{
			Out.Info($"expect: {expected.Names.Count - Check.Missing.Count} of {expected.Names.Count} expected tests passed, {Check.Unexpected.Count} unexpected ({expected.Listed.Count} listed in {expected.File})");
			if (Unexpected && Problems.Count == 0)
			{
				string Count = Check.Unexpected.Count == 1 ? "1 test" : $"{Check.Unexpected.Count} tests";
				Out.Warning(options.AllowUnexpected
					? $"unexpected: {Count} the -expect file doesn't list completed, accepted (-allow-unexpected)"
					: $"unexpected: {Count} the -expect file doesn't list completed: exit code {UnexpectedTestsExitCode} (-allow-unexpected accepts them)");
			}
		}
		foreach (string Problem in Problems)
		{
			Out.Error("problem: " + Problem);
		}
		string Word = Verdict switch
		{
			TestVerdict.Passed or TestVerdict.PassedWithUnexpected => "PASSED",
			TestVerdict.NothingRan => "FAILED (NOTHING RAN)",
			_ => "FAILED",
		};
		string Listed = expected is null || Check is null ? "" : $"{expected.Names.Count - Check.Missing.Count} of {expected.Names.Count} listed passed; {DescribeUnexpected(Check, summary)}; ";
		// The skipped count is always printed: a run that skips most of its tests must be visible even when it passes.
		string VerdictLine = string.Create(CultureInfo.InvariantCulture,
			$"test: {Word}: {Listed}{summary.Passed} of {summary.Tests.Count} passed, {summary.Failed} failed, {summary.Skipped} skipped; found {summary.FoundCount?.ToString(CultureInfo.InvariantCulture) ?? "none"}{tail}");
		if (Verdict == TestVerdict.Passed)
		{
			Out.Info(VerdictLine);
		}
		else if (Verdict == TestVerdict.PassedWithUnexpected)
		{
			Out.Warning(VerdictLine);
		}
		else
		{
			Out.Error(VerdictLine);
		}
		TestRunOutcome Outcome = new()
		{
			Verdict = Verdict,
			Summary = summary,
			Problems = Problems,
			Lines = Out.Lines,
			ListedFailed = Check?.Missing.Count(Missing => AutomationTestResult.IsFailureResult(Missing.Result)) ?? 0,
		};
		if (options.RecordSummary)
		{
			Out.Record(context, Services, "test", options.Name, Outcome.ExitCode);
		}
		return (Outcome, Check, FilterWarnings);
	}

	/// <summary>"2 unexpected (1 passed, 1 failed)", from each unexpected test's last result.</summary>
	static string DescribeUnexpected(ExpectCheck check, AutomationLogSummary summary)
	{
		if (check.Unexpected.Count == 0)
		{
			return "0 unexpected";
		}
		List<AutomationTestResult> Results = check.Unexpected.Select(Name => summary.Tests.Last(Test => Test.Path.Equals(Name, StringComparison.Ordinal))).ToList();
		List<string> Parts = [];
		void Add(int count, string what)
		{
			if (count > 0)
			{
				Parts.Add($"{count} {what}");
			}
		}
		Add(Results.Count(Test => Test.Passed), "passed");
		Add(Results.Count(Test => !Test.Passed && !Test.Skipped), "failed");
		Add(Results.Count(Test => Test.Skipped), "skipped");
		return $"{check.Unexpected.Count} unexpected ({string.Join(", ", Parts)})";
	}

	/// <summary>
	/// The filter's warnings (<see cref="AutomationFilter.Warnings"/>) against the tests the run found: the "Found N" listing,
	/// with the completed tests. A run that found none already fails for it; a Group: filter can't be matched.
	/// </summary>
	static List<string> GetFilterWarnings(string filter, AutomationLogSummary summary)
	{
		if (summary.FoundCount is not int Found || Found == 0 || summary.NoMatchFilter is not null)
		{
			return [];
		}
		HashSet<string> Names = new(summary.FoundNames.Concat(summary.Tests.Select(Test => Test.Path)), StringComparer.Ordinal);
		try
		{
			return AutomationFilter.Parse(filter).Warnings(Names, complete: Names.Count >= Found);
		}
		catch (UakUsageException)
		{
			return [];
		}
	}

	/// <summary>Logs the skipped, never-run and failed tests, and the expect check's missing and unexpected tests.</summary>
	static void ReportTests(UakContext context, AutomationLogSummary summary, ExpectCheck? check)
	{
		foreach (AutomationTestResult Test in summary.Tests.Where(Test => Test.Skipped))
		{
			context.Logger.LogWarning("{Result,-8} {Path}", Test.Result, Test.Path);
		}
		foreach (string Test in summary.NotRunNames)
		{
			context.Logger.LogWarning("{Result,-8} {Path}", "NotRun", Test);
		}
		foreach (AutomationTestResult Test in summary.Tests.Where(Test => !Test.Passed && !Test.Skipped))
		{
			context.Logger.LogError("{Result,-8} {Path}", Test.Result, Test.Path);
			foreach (string Error in Test.Errors)
			{
				context.Logger.LogError("    {Error}", Error);
			}
		}
		foreach (string Error in summary.OtherErrors)
		{
			context.Logger.LogError("    {Error}", Error);
		}
		if (check is not null)
		{
			foreach (MissingTest Missing in check.Missing)
			{
				context.Logger.LogError("missing: {Test}", Missing);
			}
			foreach (string Unexpected in check.Unexpected)
			{
				context.Logger.LogWarning("unexpected: {Test}", Unexpected);
			}
		}
	}

	static object Failures(AutomationLogSummary summary) =>
		summary.Tests.Where(Test => !Test.Passed && !Test.Skipped).Select(Test => new { Test.Path, Test.Result, Test.Errors }).ToList();

	static object? ExpectResult(ExpectedTests? expected, ExpectCheck? check, bool allowUnexpected) => expected is null || check is null ? null : new
	{
		expected.File,
		Expected = expected.Names,
		Missing = check.Missing.Select(Missing => new { Missing.Name, Missing.Result }).ToList(),
		check.Unexpected,
		AllowUnexpected = allowUnexpected,
	};

	static void KeepLast(Queue<string> lines, string line)
	{
		lines.Enqueue(line);
		if (lines.Count > 20)
		{
			lines.Dequeue();
		}
	}

	/// <summary>Reads a log another process may still hold open.</summary>
	static List<string> ReadShared(string path)
	{
		using FileStream Stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using StreamReader Reader = new(Stream);
		List<string> Lines = [];
		for (string? Line = Reader.ReadLine(); Line is not null; Line = Reader.ReadLine())
		{
			Lines.Add(Line);
		}
		return Lines;
	}
}
