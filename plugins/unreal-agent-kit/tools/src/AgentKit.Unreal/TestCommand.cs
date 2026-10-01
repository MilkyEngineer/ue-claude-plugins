// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>
/// `uak test -filter=&lt;prefix&gt;`: runs the project's automation tests in a headless editor under the editor lock, and
/// reports passed, failed and total tests from the editor's log.
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
	public string Summary => "Run automation tests in a headless editor, under the editor lock.";

	/// <inheritdoc/>
	public string Usage =>
		"""
		uak test -filter=<prefix> [options]
		  -filter=<prefix>      The tests to run: a test path prefix, several joined with '+'.
		  -gpu                  Render with a real RHI off screen (-RenderOffscreen) instead of -nullrhi.
		  -name=<name>          Names the editor log, <Project>/Saved/Logs/<name>.log (default uak-test).
		  -resultfile=<path>    Also write the result as JSON.
		The run passes only when the editor found tests, every found test completed ("Found N automation tests"),
		at least one passed (all skipped is NOTHING RAN), none failed, the queue finished, and the editor exited with 0.
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
		string Filter = Arguments.GetString("filter") ?? throw new UakUsageException("give -filter=<test path prefix>");
		bool Gpu = Arguments.GetFlag("gpu");
		string Name = Arguments.GetString("name", "uak-test")!;
		string? ResultFile = Arguments.GetString("resultfile");
		Arguments.ThrowIfUnknown();
		if (Arguments.Positional.Count > 0)
		{
			throw new UakUsageException("uak test takes no positional arguments: " + string.Join(' ', Arguments.Positional));
		}
		if (Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Name.Contains(' ', StringComparison.Ordinal))
		{
			throw new UakUsageException($"-name={Name} must be a plain file name without spaces");
		}

		string Project = UnrealServices.RequireProject(context);
		EngineLayout Paths = Services.Paths(context);
		string ProjectDirectory = Path.GetDirectoryName(Project)!;
		string LogFile = Path.Combine(ProjectDirectory, "Saved", "Logs", Name + ".log");
		string ReportDirectory = Path.Combine(context.StateDirectory.FullName, "TestReports", Name);
		EditorLocation Editor = EditorLocator.Locate(Paths, Project);
		ProcessInvocation Invocation = EditorTestCommandLine.Build(Editor.CommandExecutable, Project, Filter, Gpu, LogFile, ReportDirectory);
		if (!File.Exists(Invocation.FileName))
		{
			throw new UakSetupException($"editor not found: {Invocation.FileName} (from {Editor.How})");
		}

		int ExitCode;
		Stopwatch Timer;
		Queue<string> LastLines = new();
		AutomationLogSummary? Parsed;
		await using (IAsyncDisposable Lock = await Services.Lock.AcquireAsync(context, $"uak test {Filter}", cancellationToken).ConfigureAwait(false))
		{
			// A log left by an earlier run must never be read as this run's.
			Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
			File.Delete(LogFile);
			context.Logger.LogInformation("Running automation tests '{Filter}' ({Rhi})...", Filter, Gpu ? "GPU" : "nullrhi");
			Timer = Stopwatch.StartNew();
			ExitCode = await Services.RunEditorAsync(Invocation, Line =>
			{
				// The editor's stdout repeats its log; keep only the end, for a crash that never reached the log.
				LastLines.Enqueue(Line);
				if (LastLines.Count > 20)
				{
					LastLines.Dequeue();
				}
			}, Lock, cancellationToken).ConfigureAwait(false);
			Timer.Stop();
			// Read the log while the lock is still held: the next holder of the same -name deletes it before its own run.
			Parsed = File.Exists(LogFile) ? AutomationLogParser.Parse(ReadShared(LogFile)) : null;
		}

		if (Parsed is not AutomationLogSummary Summary)
		{
			context.Logger.LogError("The editor wrote no log (exit code {Code}). Its last output:", ExitCode);
			foreach (string Line in LastLines)
			{
				context.Logger.LogError("    {Line}", Line);
			}
			return 1;
		}
		List<string> Problems = Summary.Problems();
		if (ExitCode != 0)
		{
			Problems.Add($"the editor exited with code {ExitCode}");
		}

		foreach (AutomationTestResult Test in Summary.Tests.Where(Test => Test.Skipped))
		{
			context.Logger.LogWarning("{Result,-8} {Path}", Test.Result, Test.Path);
		}
		foreach (AutomationTestResult Test in Summary.Tests.Where(Test => !Test.Passed && !Test.Skipped))
		{
			context.Logger.LogError("{Result,-8} {Path}", Test.Result, Test.Path);
			foreach (string Error in Test.Errors)
			{
				context.Logger.LogError("    {Error}", Error);
			}
		}
		foreach (string Error in Summary.OtherErrors)
		{
			context.Logger.LogError("    {Error}", Error);
		}
		foreach (string Problem in Problems)
		{
			context.Logger.LogError("problem: {Problem}", Problem);
		}
		bool Passed = Problems.Count == 0;
		bool NothingRan = Summary.Passed == 0 && Summary.Skipped > 0;
		// The skipped count is always printed: a run that skips most of its tests must be visible even when it passes.
		context.Logger.LogInformation("test: {Outcome}: {Passed} of {Total} passed, {Failed} failed, {Skipped} skipped; found {Found}. {Seconds:0.0} s. Log: {Log}",
			Passed ? "PASSED" : NothingRan ? "FAILED (NOTHING RAN)" : "FAILED", Summary.Passed, Summary.Tests.Count, Summary.Failed, Summary.Skipped,
			Summary.FoundCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none", Timer.Elapsed.TotalSeconds, LogFile);
		UnrealServices.WriteResultFile(ResultFile, new
		{
			Outcome = Passed ? "passed" : NothingRan ? "nothing ran" : "failed",
			Filter,
			Summary.FoundCount,
			Total = Summary.Tests.Count,
			Summary.Passed,
			Summary.Failed,
			Summary.Skipped,
			EditorExitCode = ExitCode,
			Summary.TestCompleteExitCode,
			Problems,
			Failures = Summary.Tests.Where(Test => !Test.Passed && !Test.Skipped).Select(Test => new { Test.Path, Test.Result, Test.Errors }).ToList(),
			Seconds = Math.Round(Timer.Elapsed.TotalSeconds, 1),
			LogFile,
			ReportDirectory,
		});
		return Passed ? 0 : 1;
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
