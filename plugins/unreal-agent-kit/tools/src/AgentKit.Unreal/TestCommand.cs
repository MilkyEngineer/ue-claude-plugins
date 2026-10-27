// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Globalization;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>
/// `uak test -filter=&lt;filter&gt;`: runs the project's automation tests under the editor lock, in an editor (headless, unless
/// -windowed) or, with -cooked, in a cooked game that UAT's BuildCookRun builds, cooks, stages and runs; and reports passed,
/// failed, skipped and total tests from the editor's log or the game's Client.log, checked against an -expect file when one
/// is given.
/// </summary>
public sealed class TestCommand : IUakCommand
{
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
		                        StartsWith:<prefix>, ^<start> or <end>$; several joined with '+'.
		  -expect=<file>        The tests the run must pass: one full test name per line; blank lines and lines starting
		                        with # are ignored. The run expects the names its -filter matches (a Group: filter can't
		                        be matched), and passes only when each of them passed and no other test completed; the
		                        missing and unexpected names are listed.
		  -name=<name>          Names the run's logs under <Project>/Saved/Logs (default uak-test; uak-test-cooked with -cooked).
		  -resultfile=<path>    Also write the result as JSON.
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
		                        (default: the project's maps).
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
		Exit code: 0 passed, 1 failed, 2 usage or setup error.
		""";

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return UnrealServices.GuardAsync(context, () => RunCheckedAsync(context, arguments, cancellationToken));
	}

	async Task<int> RunCheckedAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments Arguments = new(arguments);
		string Filter = Arguments.GetString("filter") ?? throw new UakUsageException("give -filter=<filter>");
		bool Cooked = Arguments.GetFlag("cooked");
		bool Gpu = Arguments.GetFlag("gpu");
		bool Windowed = Arguments.GetFlag("windowed");
		int? ResX = Arguments.GetInt("resx", 1, 16384);
		int? ResY = Arguments.GetInt("resy", 1, 16384);
		string? Maps = Arguments.GetString("map");
		string? Configuration = Arguments.GetString("config");
		string? PlatformName = Arguments.GetString("platform");
		bool SkipBuild = Arguments.GetFlag("skipbuild");
		bool SkipCook = Arguments.GetFlag("skipcook");
		int? Timeout = Arguments.GetInt("timeout", 1);
		string? ExpectFile = Arguments.GetString("expect");
		string Name = Arguments.GetString("name", Cooked ? "uak-test-cooked" : "uak-test")!;
		string? ResultFile = Arguments.GetString("resultfile");
		Arguments.ThrowIfUnknown();
		if (Arguments.Positional.Count > 0)
		{
			throw new UakUsageException("uak test takes no positional arguments: " + string.Join(' ', Arguments.Positional));
		}
		// The name is a file and, in a cooked run, a folder UAT empties: never a path, nor "." or "..".
		if (Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Name.Contains(' ', StringComparison.Ordinal) || Name.Trim('.').Length == 0)
		{
			throw new UakUsageException($"-name={Name} must be a plain file name without spaces");
		}
		if (Cooked)
		{
			if (Gpu || Windowed || ResX is not null || ResY is not null)
			{
				throw new UakUsageException("-gpu, -windowed, -resx= and -resy= are for editor runs: a cooked run's game renders with -nullrhi");
			}
			CookedTestCommandLine.CheckFilter(Filter);
		}
		else
		{
			if (Maps is not null || Configuration is not null || PlatformName is not null || SkipBuild || SkipCook || Timeout is not null)
			{
				throw new UakUsageException("-map=, -config=, -platform=, -skipbuild, -skipcook and -timeout= are for cooked runs: add -cooked");
			}
			if (Gpu && Windowed)
			{
				throw new UakUsageException("give -gpu or -windowed, not both: -windowed renders with a real RHI too");
			}
			if (!Windowed && (ResX is not null || ResY is not null))
			{
				throw new UakUsageException("-resx= and -resy= size the window of a -windowed run");
			}
			EditorTestCommandLine.CheckFilter(Filter);
		}
		// Read before the lock, so a bad file is a usage error and never waits for the lock.
		ExpectedTests? Expected = ExpectFile is null ? null : ExpectedTests.Load(ExpectFile, Filter);

		if (!Cooked)
		{
			EditorRendering Rendering = Windowed ? EditorRendering.Windowed : Gpu ? EditorRendering.Offscreen : EditorRendering.NullRhi;
			return await RunInEditorAsync(context, Filter, Rendering, ResX ?? EditorTestCommandLine.DefaultResX, ResY ?? EditorTestCommandLine.DefaultResY,
				EditorTestCommandLine.CheckPassThrough(Arguments.Rest), Name, Expected, ResultFile, cancellationToken).ConfigureAwait(false);
		}

		UnrealPlatform Platform = PlatformName is null || PlatformName.Equals("host", StringComparison.OrdinalIgnoreCase)
			? Services.HostPlatform
			: UnrealPlatform.FromName(PlatformName) ?? throw new UakUsageException($"unknown -platform={PlatformName} (Win64, Linux, Mac or host)");
		if (Platform != Services.HostPlatform)
		{
			throw new UakUsageException($"-platform={PlatformName}: a cooked test run runs the game on this machine, so it is for the host platform ({Services.HostPlatform.Name}) only");
		}
		string Project = UnrealServices.RequireProject(context);
		CookedTestRun Run = new()
		{
			ProjectFile = Project,
			Filter = Filter,
			Platform = Platform,
			Configuration = Configuration is null ? "Development" : CookedTestCommandLine.ParseConfiguration(Configuration),
			Maps = CookedTestCommandLine.ParseMaps(Maps),
			SkipBuild = SkipBuild,
			SkipCook = SkipCook,
			TimeoutSeconds = Timeout ?? CookedTestCommandLine.DefaultTimeoutSeconds,
			GameArguments = CookedTestCommandLine.CheckPassThrough(Arguments.Rest),
			LogFolder = Path.Combine(Path.GetDirectoryName(Project)!, "Saved", "Logs", Name),
		};
		return await RunCookedAsync(context, Run, Name, Expected, ResultFile, cancellationToken).ConfigureAwait(false);
	}

	async Task<int> RunInEditorAsync(UakContext context, string filter, EditorRendering rendering, int resX, int resY, IReadOnlyList<string> passThrough,
		string name, ExpectedTests? expected, string? resultFile, CancellationToken cancellationToken)
	{
		string Project = UnrealServices.RequireProject(context);
		EngineLayout Paths = Services.Paths(context);
		string ProjectDirectory = Path.GetDirectoryName(Project)!;
		string LogFile = Path.Combine(ProjectDirectory, "Saved", "Logs", name + ".log");
		string ReportDirectory = Path.Combine(context.StateDirectory.FullName, "TestReports", name);
		EditorLocation Editor = EditorLocator.Locate(Paths, Project);
		ProcessInvocation Invocation = EditorTestCommandLine.Build(Editor, Project, filter, rendering, LogFile, ReportDirectory, passThrough, resX, resY);
		if (!File.Exists(Invocation.FileName))
		{
			throw new UakSetupException($"editor not found: {Invocation.FileName} (from {Editor.How})");
		}

		int ExitCode;
		Stopwatch Timer;
		Queue<string> LastLines = new();
		AutomationLogSummary? Parsed;
		await using (IAsyncDisposable Lock = await Services.Lock.AcquireAsync(context, $"uak test {filter}", cancellationToken).ConfigureAwait(false))
		{
			// A log left by an earlier run must never be read as this run's.
			Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
			File.Delete(LogFile);
			string Rhi = rendering switch
			{
				EditorRendering.Windowed => $"windowed {resX}x{resY}",
				EditorRendering.Offscreen => "GPU",
				_ => "nullrhi",
			};
			context.Logger.LogInformation("Running automation tests '{Filter}' ({Rhi})...", filter, Rhi);
			Timer = Stopwatch.StartNew();
			// The editor's stdout repeats its log; keep only the end, for a crash that never reached the log.
			ExitCode = await Services.RunEditorAsync(Invocation, Line => KeepLast(LastLines, Line), Lock, cancellationToken).ConfigureAwait(false);
			Timer.Stop();
			// Read the log while the lock is still held: the next holder of the same -name deletes it before its own run.
			Parsed = File.Exists(LogFile) ? AutomationLogParser.Parse(ReadShared(LogFile)) : null;
		}

		if (Parsed is not AutomationLogSummary Summary)
		{
			// The windowed editor is a GUI program on Windows, which writes nothing to its console output.
			context.Logger.LogError("The editor wrote no log (exit code {Code}). Its last output{None}:", ExitCode, LastLines.Count == 0 ? " (none)" : "");
			foreach (string Line in LastLines)
			{
				context.Logger.LogError("    {Line}", Line);
			}
			return 1;
		}
		ExpectCheck? Check = expected?.Check(Summary);
		List<string> Problems = Summary.Problems();
		if (ExitCode != 0)
		{
			Problems.Add($"the editor exited with code {ExitCode}");
		}
		Problems.AddRange(Check?.Problems() ?? []);

		ReportDetails(context, Summary, expected, Check, Problems);
		bool Passed = Problems.Count == 0;
		bool NothingRan = Summary.Passed == 0 && Summary.Skipped > 0;
		// The skipped count is always printed: a run that skips most of its tests must be visible even when it passes.
		context.Logger.LogInformation("test: {Outcome}: {Passed} of {Total} passed, {Failed} failed, {Skipped} skipped; found {Found}. {Seconds:0.0} s. Log: {Log}",
			Outcome(Passed, NothingRan), Summary.Passed, Summary.Tests.Count, Summary.Failed, Summary.Skipped,
			Summary.FoundCount?.ToString(CultureInfo.InvariantCulture) ?? "none", Timer.Elapsed.TotalSeconds, LogFile);
		UnrealServices.WriteResultFile(resultFile, new
		{
			Mode = "editor",
			Outcome = Passed ? "passed" : NothingRan ? "nothing ran" : "failed",
			Filter = filter,
			Summary.FoundCount,
			Total = Summary.Tests.Count,
			Summary.Passed,
			Summary.Failed,
			Summary.Skipped,
			NotRun = Summary.NotRunNames,
			EditorExitCode = ExitCode,
			Summary.TestCompleteExitCode,
			Problems,
			Failures = Failures(Summary),
			Expect = ExpectResult(expected, Check),
			Seconds = Math.Round(Timer.Elapsed.TotalSeconds, 1),
			LogFile,
			ReportDirectory,
		});
		return Passed ? 0 : 1;
	}

	async Task<int> RunCookedAsync(UakContext context, CookedTestRun run, string name, ExpectedTests? expected, string? resultFile, CancellationToken cancellationToken)
	{
		EngineLayout Paths = Services.Paths(context);
		ProcessInvocation Invocation = CookedTestCommandLine.Build(Paths, run);
		string ClientLog = CookedTestCommandLine.ClientLogFile(run.LogFolder);
		string UatLog = Path.Combine(Path.GetDirectoryName(run.LogFolder)!, name + ".log");
		// Named first, so a long build, cook or run can be followed in its logs while it runs.
		context.Logger.LogInformation("Client log: {Log}", ClientLog);
		context.Logger.LogInformation("UAT log: {Log}", UatLog);

		int ExitCode;
		Stopwatch Timer;
		Queue<string> LastLines = new();
		UatOutputParser Uat = new();
		AutomationLogSummary? Parsed;
		await using (IAsyncDisposable Lock = await Services.Lock.AcquireAsync(context, $"uak test -cooked {run.Filter}", cancellationToken).ConfigureAwait(false))
		{
			// Logs left by an earlier run must never be read as this run's. (UAT also empties the folder when no other UAT runs.)
			Directory.CreateDirectory(run.LogFolder);
			File.Delete(ClientLog);
			string Steps = (run.SkipBuild ? "" : "build, ") + (run.SkipCook ? "staged build reused" : "cook, stage, pak") + ", run";
			context.Logger.LogInformation("Running automation tests '{Filter}' in a cooked {Platform} {Configuration} game ({Steps})...",
				run.Filter, run.Platform.Name, run.Configuration, Steps);
			await using (StreamWriter Log = new(UatLog, append: false, UakJson.Utf8NoBom) { AutoFlush = true })
			{
				await Log.WriteLineAsync($"> {CookedTestCommandLine.LogFolderVariable}={Path.GetFullPath(run.LogFolder)}").ConfigureAwait(false);
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
			Parsed = File.Exists(ClientLog) ? AutomationLogParser.Parse(ReadShared(ClientLog)) : null;
		}

		AutomationLogSummary Summary = Parsed ?? new AutomationLogSummary();
		ExpectCheck? Check = expected?.Check(Summary);
		List<string> Problems = Parsed is null ? [$"the game wrote no log: {ClientLog} is missing"] : Summary.Problems();
		if (Uat.Summary.TimedOutAfterSeconds is int Seconds)
		{
			Problems.Add($"the game ran past -timeout={Seconds} and UAT stopped it");
		}
		if (ExitCode != 0)
		{
			Problems.Add($"UAT exited with code {ExitCode}");
		}
		Problems.AddRange(Check?.Problems() ?? []);

		if (Parsed is null || ExitCode != 0)
		{
			context.Logger.LogError("UAT's last output{None}:", LastLines.Count == 0 ? " (none)" : "");
			foreach (string Line in LastLines)
			{
				context.Logger.LogError("    {Line}", Line);
			}
		}
		ReportDetails(context, Summary, expected, Check, Problems);
		bool Passed = Problems.Count == 0;
		bool NothingRan = Summary.Passed == 0 && Summary.Skipped > 0;
		context.Logger.LogInformation("test: {Outcome}: {Passed} of {Total} passed, {Failed} failed, {Skipped} skipped; found {Found}. UAT exit {Uat}, game exit {Game}. {Seconds:0.0} s. Client log: {Log}",
			Outcome(Passed, NothingRan), Summary.Passed, Summary.Tests.Count, Summary.Failed, Summary.Skipped,
			Summary.FoundCount?.ToString(CultureInfo.InvariantCulture) ?? "none", ExitCode,
			Uat.Summary.ClientExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none", Timer.Elapsed.TotalSeconds, ClientLog);
		UnrealServices.WriteResultFile(resultFile, new
		{
			Mode = "cooked",
			Outcome = Passed ? "passed" : NothingRan ? "nothing ran" : "failed",
			run.Filter,
			Platform = run.Platform.Name,
			run.Configuration,
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
			Problems,
			Failures = Failures(Summary),
			Expect = ExpectResult(expected, Check),
			Seconds = Math.Round(Timer.Elapsed.TotalSeconds, 1),
			ClientLog,
			UatLog,
		});
		return Passed ? 0 : 1;
	}

	static string Outcome(bool passed, bool nothingRan) => passed ? "PASSED" : nothingRan ? "FAILED (NOTHING RAN)" : "FAILED";

	/// <summary>Logs the skipped, never-run and failed tests, the expect check and the problems.</summary>
	static void ReportDetails(UakContext context, AutomationLogSummary summary, ExpectedTests? expected, ExpectCheck? check, List<string> problems)
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
		if (expected is not null && check is not null)
		{
			foreach (MissingTest Missing in check.Missing)
			{
				context.Logger.LogError("missing: {Test}", Missing);
			}
			foreach (string Unexpected in check.Unexpected)
			{
				context.Logger.LogError("unexpected: {Test}", Unexpected);
			}
			context.Logger.LogInformation("expect: {Passed} of {Expected} expected tests passed, {Unexpected} unexpected ({Listed} listed in {File})",
				expected.Names.Count - check.Missing.Count, expected.Names.Count, check.Unexpected.Count, expected.Listed.Count, expected.File);
		}
		foreach (string Problem in problems)
		{
			context.Logger.LogError("problem: {Problem}", Problem);
		}
	}

	static object Failures(AutomationLogSummary summary) =>
		summary.Tests.Where(Test => !Test.Passed && !Test.Skipped).Select(Test => new { Test.Path, Test.Result, Test.Errors }).ToList();

	static object? ExpectResult(ExpectedTests? expected, ExpectCheck? check) => expected is null || check is null ? null : new
	{
		expected.File,
		Expected = expected.Names,
		Missing = check.Missing.Select(Missing => new { Missing.Name, Missing.Result }).ToList(),
		check.Unexpected,
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
