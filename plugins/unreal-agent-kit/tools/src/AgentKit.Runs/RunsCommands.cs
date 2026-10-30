// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentKit.Core;
using AgentKit.Locking;
using Microsoft.Extensions.Logging;

namespace AgentKit.Runs;

/// <summary><c>uak runs start</c>: starts a detached, recorded run.</summary>
public sealed class RunsStartCommand : IUakCommand
{
	/// <inheritdoc />
	public string Name => "runs start";

	/// <inheritdoc />
	public string Summary => "Start a command as a detached run that outlives this shell and the agent session.";

	/// <inheritdoc />
	public string Usage => """
		uak runs start -name=<unique> -owner=<who> [-priority=High|Normal] [-output=<file>] [-result-file=<file>] [-force] [-allow-sleep] -- <command> [<arguments>...]
		  -name=         the run's unique name: letters, digits, '_', '.' and '-'
		  -owner=        who started it (an epic such as E3, or lead)
		  -priority=     the priority of the run's lock requests (sets UAK_LOCK_PRIORITY for the command)
		  -output=       the output file (default <State>/Runs/<name>.log); it is emptied when the run starts
		  -result-file=  a file whose last PASSED/FAILED line `uak runs list` shows when there is no output
		  -force         replace the record of a finished run with the same name
		  -allow-sleep   let the machine sleep during the run; by default it is kept awake (Windows: system and display)
		  The run takes the editor lock only if the command does; its lock requests show as "<name>/...".
		  `uak runs list` shows it; `uak runs wait -name=<name> -timeout=<seconds>` waits for it to end.
		""";

	/// <inheritdoc />
	public bool RequiresEngine => false;

	/// <inheritdoc />
	public async Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		UakArguments parsed = new(arguments);
		RunStartRequest request = new()
		{
			Name = parsed.GetRequiredString("name"),
			Owner = parsed.GetRequiredString("owner"),
			Priority = CommandArguments.GetPriority(parsed),
			OutputFile = parsed.GetString("output"),
			ResultFile = parsed.GetString("result-file"),
			Force = parsed.GetFlag("force"),
			AllowSleep = parsed.GetFlag("allow-sleep"),
			Command = CommandArguments.GetCommand(parsed),
		};
		parsed.ThrowIfUnknown();
		if (request.Command.Count == 0)
		{
			throw new UakUsageException("runs start needs a command: uak runs start -name= -owner= -- <command...>");
		}
		RunRegistry registry = new(context.StateDirectory);
		RunStartResult result;
		try
		{
			result = await RunStarter.StartAsync(registry, request, GetWrapperCommand(context), cancellationToken).ConfigureAwait(false);
		}
		catch (RunStartException exception)
		{
			context.Logger.LogError("{Message}", exception.Message);
			return UakExitCodes.Failure;
		}
		StringBuilder text = new();
		string detach = result.Detach switch
		{
			"job" => " (inside the caller's job, which does not allow breakaway: the run ends when the job closes)",
			_ => "",
		};
		text.Append(CultureInfo.InvariantCulture, $"Started '{request.Name}' (PID {result.WrapperPid}), owner {request.Owner}, priority {request.Priority?.ToString() ?? "default"}{detach}\n");
		text.Append(CultureInfo.InvariantCulture, $"  {result.Record.CommandLine}\n");
		text.Append(CultureInfo.InvariantCulture, $"  Output: {result.Record.OutputFile}\n");
		text.Append(CultureInfo.InvariantCulture, $"  Record: {result.RecordFile}\n");
		if (!result.Registered)
		{
			text.Append("  The wrapper has not recorded itself yet: check `uak runs list`.\n");
		}
		Console.Out.Write(text.ToString());
		return UakExitCodes.Success;
	}

	/// <summary>The command that runs this uak's wrapper for the same context: <c>[uak, -project=..., -engine=..., runs, _wrap]</c>.</summary>
	internal static List<string> GetWrapperCommand(UakContext context)
	{
		(string fileName, IReadOnlyList<string> prefix) = UakSelf.GetCommand();
		return [fileName, .. prefix, .. UakSelf.GetGlobalOptions(context), "runs", "_wrap"];
	}
}

/// <summary><c>uak runs list [-all] [-name=&lt;pattern&gt;]</c>: the recorded runs.</summary>
public sealed class RunsListCommand : IUakCommand
{
	/// <inheritdoc />
	public string Name => "runs list";

	/// <inheritdoc />
	public string Summary => "List the detached runs: name, owner, PID, elapsed time, state and last output line.";

	/// <inheritdoc />
	public string Usage => """
		uak runs list [-all] [-name=<pattern>]
		  Running runs first, then the rest, latest first. By default only running (and starting) runs show.
		  -all     include finished runs
		  -name=   only runs whose name matches this pattern (* and ? wildcards)
		  It exits 0 whether or not anything matches: to wait for a run to end, use `uak runs wait`.
		""";

	/// <inheritdoc />
	public bool RequiresEngine => false;

	/// <inheritdoc />
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		UakArguments parsed = new(arguments);
		bool all = parsed.GetFlag("all");
		string pattern = parsed.GetString("name") ?? "*";
		parsed.ThrowIfUnknown();
		parsed.ThrowIfMorePositionalThan(0);
		RunRegistry registry = new(context.StateDirectory);
		List<RunRow> rows = GetRows(registry, all, pattern, DateTime.UtcNow);
		if (rows.Count == 0)
		{
			Console.Out.WriteLine(all ? $"No runs recorded in {registry.Directory}." : "No runs running (-all lists finished ones too).");
			return Task.FromResult(UakExitCodes.Success);
		}
		Console.Out.Write(FormatTable(rows));
		return Task.FromResult(UakExitCodes.Success);
	}

	/// <summary>One row of the table.</summary>
	internal sealed record RunRow(string Name, string Owner, string Pid, string Elapsed, RunState State, string StateText, string LastLine, DateTime Started);

	/// <summary>The rows, running first, then latest first.</summary>
	internal static List<RunRow> GetRows(RunRegistry registry, bool all, string pattern, DateTime nowUtc)
	{
		Regex match = new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		List<RunRow> rows = [];
		foreach (RunRecord record in registry.List())
		{
			if (!match.IsMatch(record.Name))
			{
				continue;
			}
			RunState state = RunRegistry.GetState(record, nowUtc);
			if (!all && state is not (RunState.Running or RunState.Starting))
			{
				continue;
			}
			TimeSpan? elapsed = record.Started is DateTime started
				? (record.Ended ?? (state == RunState.Running ? nowUtc : null)) - started
				: null;
			string stateText = state switch
			{
				RunState.Running => record.Adopted ? "running (adopted)" : "running",
				RunState.Exited => string.Create(CultureInfo.InvariantCulture, $"exited {record.ExitCode}"),
				RunState.Ended => "ended (adopted)",
				RunState.Died => "died",
				RunState.NeverStarted => "never started",
				_ => "starting",
			};
			string line = RunRegistry.GetDisplayLine(record, state);
			if (line.Length > 110)
			{
				line = line[..107] + "...";
			}
			rows.Add(new RunRow(record.Name, record.Owner, record.Pid?.ToString(CultureInfo.InvariantCulture) ?? "", elapsed is TimeSpan span ? LockStatusCommand.FormatSpan(span) : "?",
				state, stateText, line, record.Started ?? DateTime.MinValue));
		}
		return [.. rows.OrderByDescending(row => row.State == RunState.Running).ThenByDescending(row => row.Started)];
	}

	/// <summary>The rows as an aligned table.</summary>
	internal static string FormatTable(IReadOnlyList<RunRow> rows)
	{
		string[] headers = ["Name", "Owner", "PID", "Elapsed", "State", "LastLine"];
		List<string[]> cells = [.. rows.Select(row => new[] { row.Name, row.Owner, row.Pid, row.Elapsed, row.StateText, row.LastLine })];
		int[] widths = [.. headers.Select((header, column) => Math.Max(header.Length, cells.Count == 0 ? 0 : cells.Max(cell => cell[column].Length)))];
		StringBuilder text = new();
		void AppendRow(IReadOnlyList<string> values)
		{
			text.Append(string.Join("  ", values.Select((value, column) => value.PadRight(widths[column]))).TrimEnd()).Append('\n');
		}
		AppendRow(headers);
		AppendRow([.. headers.Select(header => new string('-', header.Length))]);
		foreach (string[] cell in cells)
		{
			AppendRow(cell);
		}
		return text.ToString();
	}
}

/// <summary>
/// <c>uak runs wait -name=&lt;name&gt; [-timeout=&lt;seconds&gt;]</c>: waits until a run has ended, so one background command can
/// stand in for an agent checking again and again. Its exit code says how the run ended.
/// </summary>
public sealed class RunsWaitCommand : IUakCommand
{
	/// <inheritdoc />
	public string Name => "runs wait";

	/// <inheritdoc />
	public string Summary => "Wait until a detached run has ended, then print its end state and last line.";

	/// <inheritdoc />
	public string Usage => """
		uak runs wait -name=<exact name> [-timeout=<seconds>]
		  Reads the run's record every few seconds until the run has ended, then prints its end state and last line.
		  -name=     the run's exact name (no wildcards)
		  -timeout=  stop waiting after this many seconds (default: no limit)
		  It prints "exited <code>" and, when the run's last uak build, test or mutate recorded a summary, its verdict
		  line with that command's exit code ("Verdict: mutate 'M1' exited 6: mutate: SURVIVED: ..."), or "Verdict:
		  none" when that command recorded no result (it was killed first).
		  Exit code: 0 the run exited 0; 1 it exited with another code, or ended with no recorded exit code (an adopted
		  run, or one that died or never started); 2 a usage error, or no such run; 3 -timeout passed while it still runs.
		  1 is any code but 0: a command whose codes carry results (uak mutate's 6 survived, 3 not built, 4 inconclusive)
		  is read from the printed code and verdict, not from this exit code.
		  Run it in a background shell whose own time limit is longer than -timeout (and within the shell's limit), so it
		  costs nothing while it waits. On exit 3, start another wait.
		""";

	/// <inheritdoc />
	public bool RequiresEngine => false;

	/// <summary>How often the record is read. Reading it is cheap: one small JSON file, and a process check.</summary>
	public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>Where the result goes; null for the console.</summary>
	public TextWriter? Output { get; init; }

	/// <inheritdoc />
	public async Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		UakArguments parsed = new(arguments);
		string name = parsed.GetRequiredString("name");
		int? timeoutSeconds = parsed.GetInt("timeout", 0);
		parsed.ThrowIfUnknown();
		parsed.ThrowIfMorePositionalThan(0);
		if (!RunRegistry.IsValidName(name))
		{
			throw new UakUsageException($"-name= takes a run's exact name: letters, digits, '_', '.' and '-' (no wildcards): '{name}'.");
		}
		TimeSpan? timeout = timeoutSeconds is int seconds ? TimeSpan.FromSeconds(seconds) : null;
		TextWriter output = Output ?? Console.Out;
		RunRegistry registry = new(context.StateDirectory);
		string recordFile = registry.GetRecordFile(name);
		Stopwatch waited = Stopwatch.StartNew();
		while (true)
		{
			RunRecord? record = registry.Read(name);
			if (record is null && !File.Exists(recordFile))
			{
				context.Logger.LogError("No run named '{Name}' is recorded in {Directory}. `uak runs list -all` lists the runs.", name, registry.Directory);
				return UakExitCodes.UsageError;
			}
			// A record that exists but can't be read is being rewritten (temporary file, then rename): read it again next time.
			if (record is not null)
			{
				RunState state = RunRegistry.GetState(record, DateTime.UtcNow);
				if (state is not (RunState.Running or RunState.Starting))
				{
					output.Write(Describe(record, state, RunSummaries.Read(context.StateDirectory, name)));
					return state == RunState.Exited && record.ExitCode == 0 ? UakExitCodes.Success : UakExitCodes.Failure;
				}
				if (timeout is TimeSpan limit && waited.Elapsed >= limit)
				{
					string line = RunRegistry.GetDisplayLine(record, state);
					output.Write(string.Create(CultureInfo.InvariantCulture,
						$"Run '{name}' is still {(state == RunState.Starting ? "starting" : "running")} after waiting {LockStatusCommand.FormatSpan(limit)} (-timeout): start another wait.\n"));
					if (line.Length > 0)
					{
						output.Write($"  Last line: {line}\n");
					}
					return UakExitCodes.TimedOut;
				}
			}
			TimeSpan delay = PollInterval;
			if (timeout is TimeSpan remaining && remaining - waited.Elapsed < delay)
			{
				delay = remaining - waited.Elapsed > TimeSpan.Zero ? remaining - waited.Elapsed : TimeSpan.Zero;
			}
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// A finished run's end state, how long it ran, its last line and its output file; with <paramref name="summaries"/>, the
	/// last summary's verdict line too, with its command's exit code (uak mutate exits 6 when a mutation survived, which
	/// <c>uak runs wait</c> reports as 1, so the code and the line say which it was).
	/// </summary>
	internal static string Describe(RunRecord record, RunState state, RunSummaryFile? summaries = null)
	{
		string ending = state switch
		{
			RunState.Exited => string.Create(CultureInfo.InvariantCulture, $"exited {record.ExitCode}"),
			RunState.Ended => "ended (an adopted run: no exit code is recorded, so whether it passed is unknown)",
			RunState.Died => "died (its process has gone without recording an end: killed, or its wrapper crashed; no exit code)",
			RunState.NeverStarted => "never started (its wrapper never recorded itself; no exit code)",
			_ => state.ToString().ToLowerInvariant(),
		};
		StringBuilder text = new();
		text.Append(CultureInfo.InvariantCulture, $"Run '{record.Name}' {ending}");
		if (record.Started is DateTime started && record.Ended is DateTime ended)
		{
			text.Append(CultureInfo.InvariantCulture, $" after {LockStatusCommand.FormatSpan(ended - started)}");
		}
		text.Append('\n');
		string line = RunRegistry.GetDisplayLine(record, state);
		if (line.Length > 0)
		{
			text.Append(CultureInfo.InvariantCulture, $"  Last line: {line}\n");
		}
		if (summaries?.Entries.LastOrDefault() is RunSummaryEntry last)
		{
			if (last.ExitCode is null)
			{
				text.Append(CultureInfo.InvariantCulture, $"  Verdict: none: its last command, {last.Command} '{last.Name}', {DescribeNoResult(last)}\n");
			}
			else if (last.Lines.Count > 0)
			{
				text.Append(CultureInfo.InvariantCulture, $"  Verdict: {last.Command} '{last.Name}' exited {last.ExitCode}: {last.Lines[^1]}\n");
			}
		}
		if (!string.IsNullOrEmpty(record.OutputFile))
		{
			text.Append(CultureInfo.InvariantCulture, $"  Output: {record.OutputFile}\n");
		}
		return text.ToString();
	}

	/// <summary>
	/// What a summary entry with no result says: the command started, and recorded nothing since; whether it still runs, when
	/// its process is known.
	/// </summary>
	internal static string DescribeNoResult(RunSummaryEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		string started = entry.Started is DateTime time ? " " + UakJson.FormatTime(time) : "";
		string why = entry.Pid is not int pid
			? "it still runs, or it was killed or stopped before it could (its exit code and log say which)"
			: entry.MayStillRun()
				? string.Create(CultureInfo.InvariantCulture, $"it still runs (PID {pid})")
				: string.Create(CultureInfo.InvariantCulture, $"its process (PID {pid}) is gone, so it was killed or stopped before it could (its exit code and log say which)");
		return $"started{started} and recorded no result: {why}; no earlier result under this name is its";
	}

	/// <summary>Who recorded an entry, for a line about it: " (PID n)" when its process is known.</summary>
	internal static string DescribeProcess(RunSummaryEntry entry) =>
		entry?.Pid is int pid ? string.Create(CultureInfo.InvariantCulture, $" (PID {pid})") : "";
}

/// <summary>
/// <c>uak runs show -name=&lt;name&gt;</c>: a run's end state and the summaries its <c>uak build</c>, <c>uak test</c> and
/// <c>uak mutate</c> recorded (<see cref="RunSummaries"/>), without reading its log. The name is a detached run's, or a
/// command's own -name= (a test run started directly).
/// </summary>
public sealed class RunsShowCommand : IUakCommand
{
	/// <inheritdoc />
	public string Name => "runs show";

	/// <inheritdoc />
	public string Summary => "Print a run's end state and the summaries its builds, tests and mutation checks recorded.";

	/// <inheritdoc />
	public string Usage => """
		uak runs show -name=<exact name>
		  For a detached run (uak runs start): its state, last line and output file, then the summary of each uak build,
		  uak test and uak mutate it ran, oldest first. For a name that is no detached run's: the last summary recorded
		  under that -name= (uak test -name=, uak mutate -name=, uak build -name=). A command drops its name's results as
		  it starts, so a command that is still running, or was killed before it recorded a result, shows as "recorded
		  no result" (saying whether its process still runs), never with an earlier command's result. Two commands that
		  run under one name at once (the default names uak-build, uak-test and uak-test-cooked are every command's that
		  has no -name=) each keep their own entry, listed with its process ID: give each command its own -name= to tell
		  them apart. They also share the test log named after the name (<Project>/Saved/Logs/<name>.log), which each
		  test run replaces as it starts, so it holds the last one's (said when several are listed). Runs and commands
		  share these names: a command started directly with -name=X drops run X's
		  results, and `uak runs start -name=X` clears command X's, so give them different names.
		  A summary holds the lines a command printed at its end: the tree it ran on (revision and changed files), the
		  watched log categories, the expect check, its problems, and its verdict line.
		  Exit code: 0 shown, 2 usage error or nothing recorded under the name.
		""";

	/// <inheritdoc />
	public bool RequiresEngine => false;

	/// <summary>Where the result goes; null for the console.</summary>
	public TextWriter? Output { get; init; }

	/// <inheritdoc />
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		UakArguments parsed = new(arguments);
		string name = parsed.GetRequiredString("name");
		parsed.ThrowIfUnknown();
		parsed.ThrowIfMorePositionalThan(0);
		// Any name a command records a summary under (uak test -name= takes more than run names do).
		if (!RunSummaries.IsValidName(name) && !RunRegistry.IsValidName(name))
		{
			throw new UakUsageException($"-name= takes an exact name, a plain file name without spaces (no wildcards): '{name}'.");
		}
		TextWriter output = Output ?? Console.Out;
		RunRegistry registry = new(context.StateDirectory);
		RunRecord? record = RunRegistry.IsValidName(name) ? registry.Read(name) : null;
		RunSummaryFile? summaries = RunSummaries.Read(context.StateDirectory, name);
		if (record is null && summaries is null)
		{
			context.Logger.LogError("Nothing is recorded under '{Name}': no detached run (`uak runs list -all`) and no summary in {Directory}.",
				name, RunSummaries.GetDirectory(context.StateDirectory));
			return Task.FromResult(UakExitCodes.UsageError);
		}
		StringBuilder text = new();
		if (record is not null)
		{
			RunState state = RunRegistry.GetState(record, DateTime.UtcNow);
			if (state is RunState.Running or RunState.Starting)
			{
				text.Append(CultureInfo.InvariantCulture, $"Run '{record.Name}' is still {(state == RunState.Starting ? "starting" : "running")}: the summaries so far.\n");
				if (!string.IsNullOrEmpty(record.OutputFile))
				{
					text.Append(CultureInfo.InvariantCulture, $"  Output: {record.OutputFile}\n");
				}
			}
			else
			{
				text.Append(RunsWaitCommand.Describe(record, state));
			}
		}
		List<RunSummaryEntry> entries = summaries?.Entries ?? [];
		if (entries.Count == 0)
		{
			text.Append("No summary recorded: the run ran no uak build, test or mutate (or none has finished).\n");
		}
		else if (record is null && entries.Count > 1)
		{
			// A command's own name holds one entry per command that ran under it at the same time.
			text.Append(CultureInfo.InvariantCulture, $"{entries.Count} commands ran under '{name}' at the same time; each line names its process.");
			text.Append(" Give each command its own -name= to tell their results apart.");
			if (entries.Any(entry => entry.Command is "test" or "mutate"))
			{
				// The editor's log is named after the name, not the command: each test run replaces it as it starts.
				text.Append(CultureInfo.InvariantCulture, $" They also share the test log (<Project>/Saved/Logs/{name}.log, and the folder {name}/ beside it for");
				text.Append(" a cooked run), which holds the last test run's: the log an earlier one's 'Log:' line names may now be another's.");
			}
			text.Append('\n');
		}
		foreach (RunSummaryEntry entry in entries)
		{
			if (entry.ExitCode is null)
			{
				text.Append(CultureInfo.InvariantCulture, $"{entry.Command} '{entry.Name}' {RunsWaitCommand.DescribeNoResult(entry)}.\n");
				continue;
			}
			string finished = entry.Finished is DateTime time ? UakJson.FormatTime(time) : "at an unknown time";
			text.Append(CultureInfo.InvariantCulture, $"{entry.Command} '{entry.Name}'{RunsWaitCommand.DescribeProcess(entry)}, exit code {entry.ExitCode}, finished {finished}:\n");
			foreach (string line in entry.Lines)
			{
				text.Append(CultureInfo.InvariantCulture, $"  {line}\n");
			}
		}
		output.Write(text.ToString());
		return Task.FromResult(UakExitCodes.Success);
	}
}

/// <summary><c>uak runs adopt</c>: records a run started some other way.</summary>
public sealed class RunsAdoptCommand : IUakCommand
{
	/// <inheritdoc />
	public string Name => "runs adopt";

	/// <inheritdoc />
	public string Summary => "Record a process started some other way as a run (no end time or exit code will be recorded).";

	/// <inheritdoc />
	public string Usage => """
		uak runs adopt -pid=<PID> -name=<unique> -owner=<who> [-output=<file>] [-result-file=<file>] [-force] [-- <what it runs>...]
		  The process must be running. `uak runs list` then shows it as running until it ends.
		""";

	/// <inheritdoc />
	public bool RequiresEngine => false;

	/// <inheritdoc />
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		UakArguments parsed = new(arguments);
		int pid = parsed.GetInt("pid", 1) ?? throw new UakUsageException("-pid=... is required.");
		string name = parsed.GetRequiredString("name");
		string owner = parsed.GetRequiredString("owner");
		string? outputFile = parsed.GetString("output");
		string? resultFile = parsed.GetString("result-file");
		bool force = parsed.GetFlag("force");
		IReadOnlyList<string> command = CommandArguments.GetCommand(parsed);
		parsed.ThrowIfUnknown();
		if (!RunRegistry.IsValidName(name))
		{
			throw new UakUsageException($"The run name may hold only letters, digits, '_', '.' and '-': '{name}'.");
		}
		ProcessIdentity? process = ProcessIdentity.TryGet(pid);
		if (process is null)
		{
			context.Logger.LogError("No process with ID {Pid} is running.", pid);
			return Task.FromResult(UakExitCodes.Failure);
		}
		RunRegistry registry = new(context.StateDirectory);
		string cwd = Environment.CurrentDirectory;
		RunRecord record = new()
		{
			Name = name,
			Owner = owner,
			Pid = pid,
			ProcessStart = process.Value.StartTimeUtc,
			Command = command.Count > 0 ? command[0] : "",
			Arguments = [.. command.Skip(1)],
			CommandLine = CommandProcess.Format(command),
			OutputFile = outputFile is null ? null : Path.GetFullPath(outputFile, cwd),
			ResultFile = resultFile is null ? null : Path.GetFullPath(resultFile, cwd),
			Adopted = true,
			Started = process.Value.StartTimeUtc ?? DateTime.UtcNow,
		};
		try
		{
			// As runs start does: the check and the write that takes the name are one step.
			using (RunClaim.Acquire(registry, name))
			{
				RunStarter.CheckNameFree(registry, name, force);
				registry.Write(record);
			}
		}
		catch (RunStartException exception)
		{
			context.Logger.LogError("{Message}", exception.Message);
			return Task.FromResult(UakExitCodes.Failure);
		}
		Console.Out.WriteLine($"Recorded '{name}' (adopted, PID {pid}): {registry.GetRecordFile(name)}");
		return Task.FromResult(UakExitCodes.Success);
	}
}

/// <summary><c>uak runs _wrap</c>: the wrapper of a detached run. Internal: <c>uak runs start</c> starts it.</summary>
public sealed class RunsWrapCommand : IUakCommand
{
	/// <inheritdoc />
	public string Name => "runs _wrap";

	/// <inheritdoc />
	public bool Hidden => true;

	/// <inheritdoc />
	public string Summary => "(internal) The wrapper `uak runs start` runs a detached command in.";

	/// <inheritdoc />
	public string Usage => """
		uak runs _wrap -record=<run record file> [-detach=breakaway|job|session]
		  Internal: started by `uak runs start`, never directly. Runs the recorded command with its output in the run's output
		  file, and records the end time, exit code and last line.
		""";

	/// <inheritdoc />
	public bool RequiresEngine => false;

	/// <inheritdoc />
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments parsed = new(arguments);
		string record = parsed.GetRequiredString("record");
		string? detach = parsed.GetString("detach");
		parsed.ThrowIfUnknown();
		parsed.ThrowIfMorePositionalThan(0);
		return RunWrapper.RunAsync(record, detach, cancellationToken);
	}
}
