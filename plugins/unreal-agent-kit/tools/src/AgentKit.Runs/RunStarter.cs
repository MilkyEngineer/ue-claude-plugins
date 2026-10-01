// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Runs;

/// <summary>What to start, for <see cref="RunStarter.StartAsync"/>.</summary>
public sealed class RunStartRequest
{
	/// <summary>The run's unique name (see <see cref="RunRegistry.IsValidName"/>).</summary>
	public required string Name { get; init; }

	/// <summary>Who starts it.</summary>
	public required string Owner { get; init; }

	/// <summary>The command and its arguments, passed through untouched.</summary>
	public required IReadOnlyList<string> Command { get; init; }

	/// <summary>The priority for the run's lock requests (UAK_LOCK_PRIORITY), or null for the default.</summary>
	public LockPriority? Priority { get; init; }

	/// <summary>The output file; null for <c>&lt;State&gt;/Runs/&lt;Name&gt;.log</c>. Relative paths are from <see cref="WorkingDirectory"/>.</summary>
	public string? OutputFile { get; init; }

	/// <summary>A result file whose PASSED/FAILED line stands in for the output (see <see cref="RunRecord.ResultFile"/>).</summary>
	public string? ResultFile { get; init; }

	/// <summary>Where the command runs; null for the current directory.</summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>Replace the record of a finished run with the same name (never a running one).</summary>
	public bool Force { get; init; }
}

/// <summary>What <see cref="RunStarter.StartAsync"/> started.</summary>
public sealed class RunStartResult
{
	/// <summary>The record as the wrapper last wrote it (or as the starter wrote it, if the wrapper has not recorded itself yet).</summary>
	public required RunRecord Record { get; init; }

	/// <summary>The record file.</summary>
	public required string RecordFile { get; init; }

	/// <summary>The wrapper's process ID.</summary>
	public required int WrapperPid { get; init; }

	/// <summary>How the wrapper was detached (see <see cref="RunRecord.Detach"/>).</summary>
	public required string Detach { get; init; }

	/// <summary>Whether the wrapper recorded itself within the wait. False means it is slow to start: check <c>uak runs list</c>.</summary>
	public required bool Registered { get; init; }
}

/// <summary>A run that cannot be started: its name is taken, or its wrapper failed.</summary>
public sealed class RunStartException : Exception
{
	/// <summary>Creates the exception.</summary>
	public RunStartException(string message) : base(message)
	{
	}

	/// <summary>Creates the exception with its cause.</summary>
	public RunStartException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>Starts detached runs (DESIGN.md, "Detached runs").</summary>
public static class RunStarter
{
	/// <summary>How long the starter waits for the wrapper to record itself.</summary>
	public static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Records the run, starts its wrapper detached, and waits until the wrapper has recorded its own process.
	/// </summary>
	/// <param name="registry">The registry to record the run in.</param>
	/// <param name="request">What to run.</param>
	/// <param name="wrapperCommand">
	/// The command that runs the wrapper, before its options: for <c>uak</c>, <c>[uak, -project=..., runs, _wrap]</c>. The
	/// starter adds <c>-record=&lt;file&gt;</c> and <c>-detach=&lt;method&gt;</c>.
	/// </param>
	/// <param name="cancellationToken">Stops waiting for the wrapper to record itself (the run goes on).</param>
	/// <exception cref="UakUsageException">The name is not valid, or the command is empty.</exception>
	/// <exception cref="RunStartException">A run with the name is running, or recorded without <see cref="RunStartRequest.Force"/>, or the wrapper failed.</exception>
	public static async Task<RunStartResult> StartAsync(RunRegistry registry, RunStartRequest request, IReadOnlyList<string> wrapperCommand, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(wrapperCommand);
		if (!RunRegistry.IsValidName(request.Name))
		{
			throw new UakUsageException($"The run name may hold only letters, digits, '_', '.' and '-' (and start with a letter or digit): '{request.Name}'.");
		}
		if (request.Command.Count == 0 || string.IsNullOrWhiteSpace(request.Command[0]))
		{
			throw new UakUsageException("The run needs a command.");
		}
		string recordFile = registry.GetRecordFile(request.Name);

		string workingDirectory = Path.GetFullPath(request.WorkingDirectory ?? Environment.CurrentDirectory);
		string command = ResolveCommand(request.Command[0], workingDirectory);
		List<string> arguments = [.. request.Command.Skip(1)];
		string outputFile = request.OutputFile is null ? registry.GetDefaultOutputFile(request.Name) : Path.GetFullPath(request.OutputFile, workingDirectory);
		RunRecord record = new()
		{
			Name = request.Name,
			Owner = request.Owner,
			Command = command,
			Arguments = arguments,
			CommandLine = CommandProcess.Format([command, .. arguments]),
			OutputFile = outputFile,
			ResultFile = request.ResultFile is null ? null : Path.GetFullPath(request.ResultFile, workingDirectory),
			Priority = request.Priority,
			Adopted = false,
			Started = DateTime.UtcNow,
			WorkingDirectory = workingDirectory,
		};
		// Claim the name: check it is free and record the run as starting, as one step. A second starter of the same name
		// waits for the claim, then finds this run starting and refuses, even with -force.
		// ResolveCommand left a bare name only when it is not on PATH.
		string? notOnPath = ExecutableLocator.IsBareName(command) && !command.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
			? $"The run could not start: '{command}' is not on PATH. uak never runs a bare program name from the current directory: give its path, or add its folder to PATH."
			: null;
		using (RunClaim.Acquire(registry, request.Name))
		{
			CheckNameFree(registry, request.Name, request.Force);
			Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
			// A fresh output, so its last line can never be an older run's.
			TruncateOutput(outputFile);
			if (notOnPath is not null)
			{
				// Recorded as a run that ended at once, so `uak runs list -all` shows why.
				record.Ended = DateTime.UtcNow;
				record.ExitCode = UakExitCodes.Failure;
				record.LastLine = notOnPath;
			}
			// The starter writes the record only before the start: the wrapper records its own process, then the end.
			registry.Write(record);
		}
		if (notOnPath is not null)
		{
			throw new RunStartException($"Run '{request.Name}': {notOnPath}");
		}

		DetachedProcess wrapper;
		try
		{
			wrapper = DetachedLauncher.Start(detach => [.. wrapperCommand, $"-record={recordFile}", $"-detach={detach}"], workingDirectory);
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			record.Ended = DateTime.UtcNow;
			record.ExitCode = UakExitCodes.Failure;
			record.LastLine = $"The wrapper could not start: {exception.Message}";
			registry.Write(record);
			throw new RunStartException(record.LastLine, exception);
		}
		using (wrapper)
		{
			DateTime deadline = DateTime.UtcNow + RegisterTimeout;
			while (true)
			{
				RunRecord? current = RunRegistry.ReadFile(recordFile);
				if (current?.Pid is not null)
				{
					return new RunStartResult { Record = current, RecordFile = recordFile, WrapperPid = wrapper.Pid, Detach = wrapper.Detach, Registered = true };
				}
				if (wrapper.ExitCode is int exitCode)
				{
					// It exited before recording itself: record that, so the run does not show as starting forever.
					current ??= record;
					if (current.Pid is null)
					{
						current.Ended = DateTime.UtcNow;
						current.ExitCode = exitCode;
						current.LastLine = $"The wrapper exited (code {exitCode}) before recording itself.";
						registry.Write(current);
						throw new RunStartException($"Run '{request.Name}': {current.LastLine}");
					}
				}
				if (DateTime.UtcNow > deadline)
				{
					return new RunStartResult { Record = current ?? record, RecordFile = recordFile, WrapperPid = wrapper.Pid, Detach = wrapper.Detach, Registered = false };
				}
				await Task.Delay(50, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	/// <summary>Empties (or creates) the run's output file. Another process may hold it open, as long as it shares writing.</summary>
	private static void TruncateOutput(string outputFile)
	{
		try
		{
			using FileStream stream = new(outputFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new RunStartException($"Cannot empty the output file {outputFile} for the new run: {exception.Message}", exception);
		}
	}

	/// <summary>
	/// Throws <see cref="RunStartException"/> when the name is taken: by a running run, or by any record unless <paramref name="force"/>.
	/// Call it inside a <see cref="RunClaim"/>, with the write that takes the name, so no other starter takes it in between.
	/// </summary>
	internal static void CheckNameFree(RunRegistry registry, string name, bool force)
	{
		string recordFile = registry.GetRecordFile(name);
		if (!File.Exists(recordFile))
		{
			return;
		}
		RunRecord? old = registry.Read(name);
		if (old is not null)
		{
			RunState state = RunRegistry.GetState(old, DateTime.UtcNow);
			if (state is RunState.Running or RunState.Starting)
			{
				throw new RunStartException($"Run '{name}' is still {(state == RunState.Running ? "running" : "starting")} (PID {old.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not yet recorded"}).");
			}
		}
		if (!force)
		{
			throw new RunStartException($"Run '{name}' is already recorded ({recordFile}). Choose another name, or pass -force to replace the finished run.");
		}
	}

	/// <summary>
	/// A command with a directory part (relative, like "Scripts/Build.bat") becomes absolute, from the working directory; a
	/// bare name ("git", "build.cmd") becomes its absolute path on PATH, and is never looked for in the working or current
	/// directory (<see cref="ExecutableLocator"/>). A bare name that is not on PATH is kept, and the start fails. A bare
	/// ".ps1" is a script file for PowerShell, and is kept as given.
	/// </summary>
	private static string ResolveCommand(string command, string workingDirectory)
	{
		if (ExecutableLocator.IsBareName(command))
		{
			return command.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? command : ExecutableLocator.Resolve(command) ?? command;
		}
		return Path.GetFullPath(command, workingDirectory);
	}
}
