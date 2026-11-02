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
		                        with # are ignored. A relative path is looked for in the current folder, then the
		                        project folder. The run expects the listed names its -filter matches (a Group: filter
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
		A cooked run that builds reserves UBT until UAT's build step ends, and waits for running compiles first, up to 10
		minutes; uak compile waits for it: UAT's UBT stops with ConflictingInstance when another holds its mutex (exit 10,
		which UAT prints as Error_SDKNotFound, and the report says so). Files edited during that step are handled as by
		uak build, as soon as the step ends, so a run cancelled later has marked them too: the exit code doesn't change,
		a problem line in the summary and in -resultfile's Problems names them, and EditedDuringBuild and EditNotMarked
		list them.
		The summary names the tree as the run found it once it held the lock: the revision and the changed files (or
		their count and hash). It is read alongside the run: no proof of what was tested (-skipbuild -skipcook reuse an
		earlier staged build). It refuses to run while a stopped `uak mutate` has left its patch in the tree or its binaries
		(`uak mutate -restore`, on its own). When that run's rebuild failed and a source file has changed since, it first
		retries that rebuild, as the `uak build` the refusal asks for (not with -quiet), and refuses if it doesn't clear it,
		or if a source file changed while that rebuild ran, naming the files in the message and in -resultfile's
		EditedDuringBuild. A rebuild by `uak mutate`, or a `uak build` that clears such a run, leaves the same refusal.
		It stands, for every later `uak test`, `uak mutate` and `uak lock run` and with -quiet too, until a whole `uak build`
		of that target (no UBT arguments) succeeds with no edit overlapping it. Such a build also keeps a file that couldn't
		be marked, unless it is gone, or it has a recorded time and was saved after that and before this build: it tries
		that mark again, and one that takes makes the next build compile the file and clear it. A file with no recorded
		time clears only through a mark that takes. Any build that reports such a file, with UBT arguments or not, leaves
		the refusal itself, as no later build would pick the file up. The refusal names the `uak build` for each target,
		and the file to delete for what no build can clear: a target that no longer exists, or a file that can be neither
		marked nor saved.
		A cooked run's UAT build makes the editor's binaries too, so a file it couldn't mark is kept for the editor target
		on this machine in Development, which leaves the same refusal, cooked runs included, until a whole `uak build` of
		that target clears it. A stamped file only gets a note, and -skipbuild tests whatever was staged.
		Exit code: 0 passed, 1 failed, 2 usage or setup error, or a refusal, 3 passed except that tests -expect doesn't list
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
			UakArguments Arguments = new(arguments, DotSplitRule.Options);
			return Arguments.GetString("name") ?? (Arguments.GetFlag("cooked") ? "uak-test-cooked" : "uak-test");
		}
		catch (UakUsageException)
		{
			return null;
		}
	}

	async Task<int> RunCheckedAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments Arguments = new(arguments, DotSplitRule.Options);
		TestOptions Options = TestOptions.Parse(Arguments, "uak-test");
		Arguments.ThrowIfUnknown();
		Arguments.ThrowIfPositional("uak test");
		// Read before the lock, so a bad file is a usage error and never waits for the lock.
		ExpectedTests? Expected = Options.ExpectFile is null ? null : ExpectedTests.Load(Options.ExpectFile, Options.Filter, context.ProjectDirectory?.FullName);
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
				await RetryOrRefuseAsync(context, options, Lock, cancellationToken).ConfigureAwait(false);
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
				EditedDuringBuild = Array.Empty<string>(),
				EditNotMarked = Array.Empty<string>(),
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
			// Always empty here: an editor run builds only in a retry, and one that overlaps an edit refuses the run, with these filled in.
			EditedDuringBuild = Array.Empty<string>(),
			EditNotMarked = Array.Empty<string>(),
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

	/// <summary>
	/// Under the lock, before a run of its own: retries a failed rebuild of a stopped mutation run (not with -quiet), then refuses
	/// while a journal is in the way. A retry fires because a source file changed, so it is likely to overlap the next save, and
	/// binaries that mix old and new are nothing to test: the run is refused, with its reason, even when the retry cleared the
	/// journal, and so is every run after it until a whole build mends them (<see cref="RebuildNeeded"/>). The journal stays cleared:
	/// what cleared it holds whatever else that build compiled.
	/// </summary>
	async Task RetryOrRefuseAsync(UakContext context, TestOptions options, IAsyncDisposable heldLock, CancellationToken cancellationToken)
	{
		BuildEditReport Retried = options.Quiet ? BuildEditReport.None : await new BuildCommand(Services).RetryPendingRebuildAsync(context, heldLock, cancellationToken).ConfigureAwait(false);
		void WriteRefusal(string Why, BuildEditReport Overlap) => UnrealServices.WriteResultFile(options.ResultFile, new
		{
			Command = "test",
			Outcome = "refused",
			ExitCode = UakExitCodes.UsageError,
			Message = Why,
			EditedDuringBuild = Overlap.Overlapped,
			EditNotMarked = Overlap.NotMarked,
		});
		BuildEditReport Edits;
		try
		{
			Edits = RebuildNeeded.Read(context).Plus(Retried);
		}
		catch (UakUsageException Refusal)
		{
			// A marker that can't be read refuses as well, and what the retry overlapped is still this run's to name.
			WriteRefusal(Refusal.Message, Retried);
			throw;
		}
		try
		{
			MutationJournal.ThrowIfUnfinished(context);
		}
		catch (UakUsageException Refusal) when (Edits.Overlapped.Count > 0)
		{
			WriteRefusal(Refusal.Message, Edits);
			throw;
		}
		if (Edits.Overlapped.Count > 0)
		{
			string Why = Edits.DescribeRetryOverlap();
			WriteRefusal(Why, Edits);
			throw new UakRefusalException(Why);
		}
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
		BuildEditWindow? Window = null;
		Task<BuildEditReport>? Stamping = null;
		BuildEditReport Edits = BuildEditReport.None;
		RebuildNeededEntry? EditorKept = null;
		OverlapKeptIn KeptIn = OverlapKeptIn.Nowhere;
		// As `uak build` does: an edit during UAT's build can leave a game object with the old text. The time is read as the step
		// ends (or UAT does), and a run that ends later waits for it.
		Task<BuildEditReport> StartStamping(BuildEditWindow Build)
		{
			Build.Close();
			bool CompiledNothing = Uat.Summary.UbtUpToDate && !Uat.Summary.UbtRanActions;
			return Task.Run(async () => BuildEdits.Reported(await BuildEdits.StampAsync(context, Services, BuildEdits.GetManaged(context), Build).ConfigureAwait(false), CompiledNothing));
		}
		await using (IAsyncDisposable Lock = await Services.Lock.AcquireAsync(context, $"uak test -cooked {Run.Filter}", options.Quiet, cancellationToken).ConfigureAwait(false))
		{
			await RetryOrRefuseAsync(context, options, Lock, cancellationToken).ConfigureAwait(false);
			// UAT's UBT stops at once if a `uak compile` holds UBT's mutex, so keep compiles out until UAT's build step ends.
			using IDisposable? UbtHeld = Run.SkipBuild ? null : await Services.ReserveUbt(context, $"uak test -cooked {Run.Filter}", cancellationToken).ConfigureAwait(false);
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
				Window = Run.SkipBuild ? null : BuildEditWindow.Open(context, Services);
				Timer = Stopwatch.StartNew();
				try
				{
					ExitCode = await Services.RunEditorAsync(Invocation, Line =>
					{
						Log.WriteLine(Line);
						KeepLast(LastLines, Line);
						if (Uat.AddLine(Line) is string Step)
						{
							context.Logger.LogInformation("  {Step}", Step);
							if (Step == UatRunSummary.BuildCompleted)
							{
								Stamping ??= Window is null ? null : StartStamping(Window);
								UbtHeld?.Dispose();
							}
						}
					}, Lock, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception) when (Stamping is not null)
				{
					// Cancelled in the cook, the stage or the tests: the build is over, and its edits go stale all the same.
					BuildEditReport Stamped = await Stamping.ConfigureAwait(false);
					foreach (string Warning in Stamped.Warnings("build"))
					{
						context.Logger.LogWarning("{Warning}", Warning);
					}
					_ = RebuildNeeded.KeepForEditor(context, Services, Stamped);
					throw;
				}
				Timer.Stop();
			}
			if (Window is not null)
			{
				Edits = await (Stamping ?? StartStamping(Window)).ConfigureAwait(false);
				(KeptIn, EditorKept) = RebuildNeeded.KeepForEditor(context, Services, Edits);
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
			Extra.Add(DescribeUatExit(ExitCode, Uat.Summary));
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
		// Told, and not judged: the tests ran what the build made, and a cooked run that mixes old and new may well pass.
		// UAT builds the editor's binaries too, and `uak test` runs those.
		List<string> Notes = Edits.Overlapped.Count == 0 ? [] : [$"the binaries UAT's build made, the game's and the editor's, may mix old and new: {BuildEdits.Name(Edits.Overlapped)} changed while it ran: build again before you test"];
		List<string> Unsaved = [.. Edits.NotMarked.Where(File => !RebuildNeededFile.IsGone(File))];
		if (Unsaved.Count > 0)
		{
			// No cooked entry: the editor target's is the one a build clears, and it refuses cooked runs too.
			string Them = Unsaved.Count == 1 ? "it" : "them";
			string For = EditorKept is null ? "" : $" for {EditorKept.Target} {EditorKept.Platform} {EditorKept.Configuration}";
			string Refuse = "`uak test` (cooked too), `uak mutate` and `uak lock run` refuse until a whole `uak build` of that target clears it";
			string Kept = KeptIn switch
			{
				OverlapKeptIn.Marker => $"that is kept{For}, so {Refuse}",
				OverlapKeptIn.Journals => $"{RebuildNeededFile.FileName} couldn't take that, so the mutation journals hold it{For} until a build that can write the marker takes it over, and {Refuse}",
				OverlapKeptIn.Unknown => $"{RebuildNeededFile.FileName} couldn't take that, and the mutation journals couldn't all be written, so it may be recorded nowhere",
				_ => "that is recorded nowhere, so nothing refuses for it",
			};
			// Only a file with a recorded time can show a save; one without leaves through a mark that takes, or by hand.
			List<string> Saveable = [.. Unsaved.Where(Edits.Written.ContainsKey)];
			List<string> Timeless = [.. Unsaved.Except(Saveable, StringComparer.OrdinalIgnoreCase)];
			string Save = Saveable.Count == 0 ? "Build" : $"Save {(Timeless.Count == 0 ? Them : BuildEdits.Name(Saveable))}, then build";
			string TimelessNote = Timeless.Count == 0 ? "" : $" ({BuildEdits.Name(Timeless)} {(Timeless.Count == 1 ? "has" : "have")} no recorded time to show a save by:"
				+ $" a build whose mark takes clears {(Timeless.Count == 1 ? "it" : "them")}, else delete {RebuildNeededFile.FileName})";
			Notes.Add($"{BuildEdits.Name(Unsaved)} could not be marked for a recompile, so no later build picks {Them} up: {Kept}."
				+ $" {Save} the editor with `uak build` and the game with a cooked run{TimelessNote}, before you test; -skipbuild tests whatever was staged");
		}
		(TestRunOutcome Outcome, ExpectCheck? Check, List<string> FilterWarnings) = Finish(context, options, expected, Summary, Extra, Tree, Watch, " in Client.log", Tail, Edits.Warnings("build"), Notes);
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
			Uat.Summary.UbtConflict,
			EditedDuringBuild = Edits.Overlapped,
			EditNotMarked = Edits.NotMarked,
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
	/// The problem line for UAT's exit code. UAT turns a failed UBT's exit code into its own (UBTUtils.cs), and UBT's
	/// ConflictingInstance and UAT's Error_SDKNotFound are both 10, so the UAT log blames a missing SDK.
	/// </summary>
	internal static string DescribeUatExit(int exitCode, UatRunSummary summary)
	{
		if (summary.UbtConflict)
		{
			return $"UAT exited with code {exitCode} because another UBT held UBT's mutex: its UBT stopped at once with ConflictingInstance, which UAT prints as Error_SDKNotFound (no SDK is missing)."
				+ " A `uak compile` that outwaited this run, a compile for another project on this engine, or an IDE's build holds it; run the test again once it ends";
		}
		return exitCode == UatRunSummary.ConflictingInstanceExitCode
			? $"UAT exited with code {exitCode}: Error_SDKNotFound, or UBT's ConflictingInstance, which has the same number (another UBT held its mutex): the UAT log says which"
			: $"UAT exited with code {exitCode}";
	}

	/// <summary>
	/// Judges a run and prints its report: the skipped, never-run and failed tests, then the summary (recorded for
	/// <c>uak runs show</c>): the tree, the watched categories, the filter's warnings, the expect check, the problems and the
	/// verdict line, which ends with <paramref name="tail"/>. <paramref name="notes"/> are problems that aren't judged: each is
	/// printed and reported as a problem, and the verdict stays as it was.
	/// </summary>
	(TestRunOutcome Outcome, ExpectCheck? Check, List<string> FilterWarnings) Finish(UakContext context, TestOptions options, ExpectedTests? expected,
		AutomationLogSummary summary, List<string> extraProblems, VcsTreeState tree, List<LogCategoryCount> watch, string watchSource, string tail, IEnumerable<string>? warnings = null,
		IReadOnlyList<string>? notes = null)
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
		foreach (string Warning in warnings ?? [])
		{
			Out.Warning(Warning);
		}
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
		foreach (string Note in notes ?? [])
		{
			Out.Warning("problem: " + Note);
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
			Problems = [.. Problems, .. notes ?? []],
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
