// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

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
		uak runs start -name=<unique> -owner=<who> [-priority=High|Normal] [-output=<file>] [-result-file=<file>] [-force] -- <command> [<arguments>...]
		  -name=         the run's unique name: letters, digits, '_', '.' and '-'
		  -owner=        who started it (an epic such as E3, or lead)
		  -priority=     the priority of the run's lock requests (sets UAK_LOCK_PRIORITY for the command)
		  -output=       the output file (default <State>/Runs/<name>.log); it is emptied when the run starts
		  -result-file=  a file whose last PASSED/FAILED line `uak runs list` shows when there is no output
		  -force         replace the record of a finished run with the same name
		  The run takes the editor lock only if the command does; its lock requests show as "<name>/...".
		  Follow it with `uak runs list`.
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
