// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.ComponentModel;
using System.Globalization;
using System.Text;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Locking;

/// <summary><c>uak lock run [-name=] [-priority=High|Normal] [-engine-scope] -- &lt;command...&gt;</c>: holds the editor lock around a command.</summary>
public sealed class LockRunCommand : IUakCommand
{
	/// <inheritdoc />
	public string Name => "lock run";

	/// <inheritdoc />
	public string Summary => "Run a command while holding the editor lock (queued: High first, then arrival order).";

	/// <inheritdoc />
	public string Usage => """
		uak lock run [-name=<name>] [-priority=High|Normal] [-engine-scope] -- <command> [<arguments>...]
		  -name=          who is asking, for `uak lock status` (default: the command's name; inside a run, "<run>/<name>")
		  -priority=      High goes ahead of every Normal waiter (default: $UAK_LOCK_PRIORITY, else Normal)
		  -engine-scope   lock the engine instead of a project: only when no project is found (-project=, $UAK_PROJECT,
		                  or a .uproject above the current directory). Without a project and without this, it refuses.
		  Put the command after "--" when it has options of its own. Its arguments pass through untouched; a .ps1 runs
		  through PowerShell, a .bat or .cmd through cmd.exe. Exits with the command's exit code.
		  The command never outlives the lock: Ctrl+C, or uak being killed, stops it and everything it started, and
		  anything it leaves running when it exits is stopped before the lock is released (a detached run, started with
		  `uak runs start`, is not). Lock requests inside the command for the same lock join this hold ($UAK_LOCK_HELD);
		  requests for another lock fail at once.
		""";

	/// <inheritdoc />
	public bool RequiresEngine => false;

	/// <inheritdoc />
	public async Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		UakArguments parsed = new(arguments);
		string? name = parsed.GetString("name");
		LockPriority? priority = CommandArguments.GetPriority(parsed);
		bool engineScope = parsed.GetFlag("engine-scope");
		IReadOnlyList<string> command = CommandArguments.GetCommand(parsed);
		parsed.ThrowIfUnknown();
		if (command.Count == 0)
		{
			throw new UakUsageException("lock run needs a command: uak lock run [-name=] [-priority=] -- <command...>");
		}
		CheckScope(context, engineScope);
		name ??= Path.GetFileNameWithoutExtension(command[0]);
		EditorLockOptions lockOptions = new() { Command = CommandProcess.Format(command) };

		// AcquireAsync logs the scope it holds ("Holding the editor lock for project <dir> ..."), or the hold it joins.
		await using EditorLockHold hold = await EditorLock.AcquireAsync(context, name, priority, lockOptions, cancellationToken).ConfigureAwait(false);
		try
		{
			// A nested hold records nothing (the outer holder's command is the one that matters), so its job needs no name.
			string? jobName = hold.IsNested ? null : CommandJobs.NameFor(hold.Info.HoldId);
			return await LockedCommand.RunAsync(command, jobName, hold.RecordCommandProcess, context.Logger, cancellationToken).ConfigureAwait(false);
		}
		catch (Win32Exception exception)
		{
			context.Logger.LogError("Cannot start '{Command}': {Message}", command[0], exception.Message);
			return UakExitCodes.Failure;
		}
		catch (OperationCanceledException)
		{
			context.Logger.LogWarning("Cancelled: stopped '{Command}' and what it started before releasing the lock.", command[0]);
			throw;
		}
	}

	/// <summary>
	/// A lock run needs a project to lock. Without one it would lock the engine (or the state directory), which does not
	/// exclude any project's holders, so it refuses unless <c>-engine-scope</c> asks for exactly that.
	/// </summary>
	internal static void CheckScope(UakContext context, bool engineScope)
	{
		if (context.ProjectFile is not null)
		{
			if (engineScope)
			{
				throw new UakUsageException($"-engine-scope is for work with no project, but a project was found ({context.ProjectFile.FullName}). Its lock is the editor lock: leave -engine-scope out.");
			}
			return;
		}
		if (!engineScope)
		{
			throw new UakUsageException("lock run found no project, so it would not exclude any project's editor and build runs. Pass -project=<file.uproject>, " +
				$"set {UakContextResolver.ProjectVariable}, or run it inside the project; or pass -engine-scope to lock the engine itself.");
		}
		if (context.EngineRoot is null)
		{
			throw new UakUsageException("-engine-scope needs an engine: pass -engine=<engine root>, or set " + UakContextResolver.EngineVariable + ".");
		}
	}
}

/// <summary><c>uak lock status</c>: who holds the editor lock, for how long, and who waits.</summary>
public sealed class LockStatusCommand : IUakCommand
{
	/// <inheritdoc />
	public string Name => "lock status";

	/// <inheritdoc />
	public string Summary => "Show who holds the editor lock and the queue waiting for it.";

	/// <inheritdoc />
	public string Usage => """
		uak lock status
		  Prints the holder (name, PID, how long it has held the lock) and the waiters in the order they will be served.
		""";

	/// <inheritdoc />
	public bool RequiresEngine => false;

	/// <inheritdoc />
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		UakArguments parsed = new(arguments);
		parsed.ThrowIfUnknown();
		parsed.ThrowIfMorePositionalThan(0);
		Console.Out.Write(Format(EditorLock.GetStatus(context), DateTime.UtcNow));
		return Task.FromResult(UakExitCodes.Success);
	}

	/// <summary>The status as text, as <c>uak lock status</c> prints it.</summary>
	public static string Format(EditorLockStatus status, DateTime nowUtc)
	{
		return Format(status, nowUtc, Environment.MachineName);
	}

	/// <summary>The status as text, for a reader on <paramref name="thisMachine"/>: records from other machines get a warning.</summary>
	public static string Format(EditorLockStatus status, DateTime nowUtc, string thisMachine)
	{
		ArgumentNullException.ThrowIfNull(status);
		bool IsOther(string? machine) => !string.IsNullOrEmpty(machine) && !string.Equals(machine, thisMachine, StringComparison.OrdinalIgnoreCase);
		StringBuilder text = new();
		text.Append(CultureInfo.InvariantCulture, $"Editor lock: {status.LockDirectory}\n");
		if (status.Scope is not null)
		{
			text.Append(CultureInfo.InvariantCulture, $"Scope     {status.Scope}\n");
		}
		LockHolderInfo? holder = status.Holder;
		List<string> otherMachines = [];
		if (holder is null)
		{
			text.Append("Held by   nobody\n");
		}
		else
		{
			string state = IsOther(holder.Machine) ? $"ON ANOTHER MACHINE ({holder.Machine}): cannot be checked from here"
				: status.HolderAlive ? "running" : "HOLDER GONE: stale record, the next holder replaces it";
			text.Append(CultureInfo.InvariantCulture, $"Held by   {holder.Name}  PID {holder.Pid}  {holder.Priority}  for {FormatSpan(nowUtc - holder.Acquired)}  [{state}]\n");
			if (!string.IsNullOrEmpty(holder.Command))
			{
				text.Append(CultureInfo.InvariantCulture, $"          {holder.Command}\n");
			}
			if (holder.CommandPid is int commandPid)
			{
				text.Append(CultureInfo.InvariantCulture, $"          command PID {commandPid}{(holder.CommandJob is null ? "" : $", job {holder.CommandJob}")}\n");
			}
			if (IsOther(holder.Machine))
			{
				otherMachines.Add(holder.Machine!);
			}
		}
		if (status.Queue.Count == 0)
		{
			text.Append("Queue     empty\n");
		}
		else
		{
			text.Append(CultureInfo.InvariantCulture, $"Queue ({status.Queue.Count}):\n");
			int position = 1;
			foreach (LockTicketInfo ticket in status.Queue)
			{
				text.Append(CultureInfo.InvariantCulture, $"  {position,2}. {ticket.Name,-40} PID {ticket.Pid,-6} {ticket.Priority,-6} waiting {FormatSpan(nowUtc - ticket.Queued)}\n");
				position++;
			}
		}
		foreach (LockTicketInfo ticket in status.OtherMachineTickets)
		{
			text.Append(CultureInfo.InvariantCulture, $"Ignored   {ticket.Name}  PID {ticket.Pid}  on {ticket.Machine}: another machine's ticket\n");
			otherMachines.Add(ticket.Machine ?? "?");
		}
		if (otherMachines.Count > 0)
		{
			text.Append(CultureInfo.InvariantCulture,
				$"WARNING: records from another machine ({string.Join(", ", otherMachines.Distinct(StringComparer.OrdinalIgnoreCase))}); this is {thisMachine}. The lock is per machine: keep the state directory local.\n");
		}
		return text.ToString();
	}

	/// <summary>"1h 02m", "3m 05s" or "12s".</summary>
	internal static string FormatSpan(TimeSpan span)
	{
		long seconds = Math.Max(0, (long)span.TotalSeconds);
		if (seconds >= 3600)
		{
			return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}h {seconds / 60 % 60:00}m");
		}
		if (seconds >= 60)
		{
			return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m {seconds % 60:00}s");
		}
		return string.Create(CultureInfo.InvariantCulture, $"{seconds}s");
	}
}
