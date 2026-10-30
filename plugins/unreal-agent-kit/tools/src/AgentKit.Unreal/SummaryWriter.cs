// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>
/// Prints a command's closing summary and keeps its lines, so they can be recorded for <c>uak runs show</c>
/// (<see cref="RunSummaries"/>).
/// </summary>
internal sealed class SummaryWriter
{
	/// <summary>
	/// The command run whose body runs now (<see cref="RecordingAsync"/>), so <see cref="Record"/> replaces its own summary
	/// entry, never another command's under the same name.
	/// </summary>
	static readonly AsyncLocal<RunSummaryInvocation?> s_invocation = new();

	readonly ILogger Logger;

	public SummaryWriter(ILogger logger)
	{
		Logger = logger;
	}

	/// <summary>The lines printed, in order.</summary>
	public List<string> Lines { get; } = [];

	public void Info(string line)
	{
		Logger.LogInformation("{Line}", line);
		Lines.Add(line);
	}

	public void Warning(string line)
	{
		Logger.LogWarning("{Line}", line);
		Lines.Add("warning: " + line);
	}

	public void Error(string line)
	{
		Logger.LogError("{Line}", line);
		Lines.Add("error: " + line);
	}

	/// <summary>
	/// Records the lines under the command's name and, inside a detached run, under the run's (<see cref="RunSummaries.RunNameVariable"/>).
	/// A failure to record is a warning, never the command's failure.
	/// </summary>
	public void Record(UakContext context, UnrealServices services, string command, string name, int exitCode)
	{
		RunSummaryEntry Entry = (s_invocation.Value ?? RunSummaryInvocation.New()).Stamp(new()
		{
			Command = command,
			Name = name,
			Finished = services.Clock.GetUtcNow().UtcDateTime,
			ExitCode = exitCode,
			Lines = [.. Lines],
		});
		foreach (string Failure in RunSummaries.Record(context.StateDirectory, Entry, services.GetEnvironmentVariable(RunSummaries.RunNameVariable)))
		{
			Logger.LogWarning("Could not record the summary for `uak runs show`: {Failure}", Failure);
		}
	}

	/// <summary>
	/// Runs a command's body with its summary begun under <paramref name="name"/> (<see cref="RunSummaries.Begin"/>, when the name
	/// can name one), as a run of its own (<see cref="RunSummaryInvocation"/>), so an earlier command's result under that name
	/// is never shown as this one's, and this one's never replaces another's that runs under it at the same time. When the
	/// body throws (refused, failed or cancelled), or returns without recording its summary, that is recorded in the
	/// result-less entry's place; a summary the command recorded itself stays.
	/// </summary>
	public static async Task<int> RecordingAsync(UakContext context, UnrealServices services, string command, string? name, Func<Task<int>> body)
	{
		string? RunName = services.GetEnvironmentVariable(RunSummaries.RunNameVariable);
		RunSummaryInvocation Invocation = RunSummaryInvocation.New();
		// Flows into the body (and only there), for its own Record.
		s_invocation.Value = Invocation;
		bool Begun = name is not null && RunSummaries.IsValidName(name);
		if (Begun)
		{
			foreach (string Failure in RunSummaries.Begin(context.StateDirectory, command, name!, services.Clock.GetUtcNow().UtcDateTime, RunName, Invocation))
			{
				context.Logger.LogWarning("Could not record the summary for `uak runs show`: {Failure}", Failure);
			}
		}
		void Finish(int exitCode, string line)
		{
			RunSummaryEntry Entry = Invocation.Stamp(new()
			{
				Command = command,
				Name = name!,
				Finished = services.Clock.GetUtcNow().UtcDateTime,
				ExitCode = exitCode,
				Lines = [line],
			});
			foreach (string Failure in RunSummaries.RecordIfUnfinished(context.StateDirectory, Entry, RunName))
			{
				context.Logger.LogWarning("Could not record the summary for `uak runs show`: {Failure}", Failure);
			}
		}
		int ExitCode;
		try
		{
			ExitCode = await body().ConfigureAwait(false);
		}
		catch (Exception Exception) when (Begun && Exception is not OutOfMemoryException)
		{
			if (Exception is UakUsageException or ProcessStartException)
			{
				Finish(UakExitCodes.UsageError, $"error: {command}: REFUSED: {Exception.Message}");
			}
			else
			{
				Finish(UakExitCodes.Failure, $"error: {command}: STOPPED ({(Exception is OperationCanceledException ? "cancelled" : Exception.Message)}): no result");
			}
			throw;
		}
		if (Begun)
		{
			Finish(ExitCode, $"{command}: exited {ExitCode} without a summary");
		}
		return ExitCode;
	}
}
