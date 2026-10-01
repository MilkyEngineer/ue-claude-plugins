// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Net.Http;

namespace AgentKit.Horde;

/// <summary>The exit codes of <c>uak horde job</c> and <c>uak horde preflight -wait</c> (DESIGN.md, "Horde").</summary>
public static class HordeExitCodes
{
	/// <summary>The job succeeded (or, without -wait, the preflight was started).</summary>
	public const int Success = 0;

	/// <summary>The job failed, or ended without completing (skipped or aborted steps, a batch that couldn't run).</summary>
	public const int Failure = 1;

	/// <summary>The job finished with warnings. (Not 2: 2 is uak's usage error.)</summary>
	public const int Warnings = 7;

	/// <summary>-timeout passed, or without -wait the job is still running: wait again.</summary>
	public const int StillRunning = 3;

	/// <summary>Not signed in to Horde: nobody finished the sign-in in time, or -no-login.</summary>
	public const int NotLoggedIn = 4;

	/// <summary>Any other error: Horde refused a request (including 403, not allowed), the job doesn't exist, an answer couldn't be read.</summary>
	public const int Error = 5;

	/// <summary>uak horde preflight: no usable build settings are saved for the stream, so nothing was started; the user chooses them.</summary>
	public const int BuildSettingsNeeded = 6;

	/// <summary>The exit code for a job's result.</summary>
	public static int For(HordeJobResult result) => result switch
	{
		HordeJobResult.Success => Success,
		HordeJobResult.Warnings => Warnings,
		HordeJobResult.Running => StillRunning,
		_ => Failure,
	};
}

/// <summary>
/// Waits for a Horde job to finish, quietly: it polls with <c>modifiedAfter</c>, so an unchanged job costs an empty answer;
/// starts at <see cref="MinimumInterval"/> and backs off to <see cref="MaximumInterval"/> while nothing changes; prints one line
/// when a running job's state changes (Waiting to Running; the end is the caller's summary) and one when Horde can't be
/// reached; and survives transient errors.
/// </summary>
public sealed class HordeJobWaiter
{
	/// <summary>The first poll interval, and the interval after a change.</summary>
	public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromSeconds(15);

	/// <summary>The longest poll interval, reached while nothing changes.</summary>
	public TimeSpan MaximumInterval { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>Waits between polls; tests replace it.</summary>
	public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

	/// <summary>The clock; tests replace it.</summary>
	public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

	/// <summary>After this many transient failures in a row, the waiter rebuilds a <see cref="HordeApiSession"/>'s client (a fresh connection).</summary>
	public int RebuildAfterFailures { get; init; } = 4;

	/// <summary>
	/// Polls until the job's result is known, or <paramref name="timeout"/> passes. Returns the last state read (null only if
	/// none could be read before the time-out) and whether it timed out. Throws <see cref="HordeAuthException"/> when the
	/// server refuses, and <see cref="HordeApiException"/> for a non-transient error (such as no such job).
	/// </summary>
	public async Task<(HordeJob? Job, bool TimedOut)> WaitAsync(IHordeApi api, string jobId, TimeSpan? timeout, TextWriter output, CancellationToken cancellationToken)
	{
		DateTime? deadline = timeout is null ? null : UtcNow() + timeout.Value;
		HordeJob? job = null;
		TimeSpan interval = MinimumInterval;
		bool unreachable = false;
		int failures = 0;
		for (; ; )
		{
			try
			{
				HordeJob? update = await api.GetJobAsync(jobId, job?.UpdateTime, cancellationToken).ConfigureAwait(false);
				failures = 0;
				if (unreachable)
				{
					await output.WriteLineAsync("Horde answers again.").ConfigureAwait(false);
					unreachable = false;
				}
				if (update is null)
				{
					interval = Min(interval + MinimumInterval, MaximumInterval);
				}
				else
				{
					if (job is not null && !job.State.Equals(update.State, StringComparison.OrdinalIgnoreCase) && update.Result == HordeJobResult.Running)
					{
						await output.WriteLineAsync($"Job {jobId}: {update.State}.").ConfigureAwait(false);
					}
					job = update;
					interval = MinimumInterval;
					if (job.Result != HordeJobResult.Running)
					{
						return (job, false);
					}
				}
			}
			catch (Exception exception) when (IsTransient(exception, cancellationToken))
			{
				if (!unreachable)
				{
					await output.WriteLineAsync($"Horde can't be reached ({exception.Message.Trim()}); still waiting.").ConfigureAwait(false);
					unreachable = true;
				}
				interval = MaximumInterval;
				if (++failures >= RebuildAfterFailures && api is HordeApiSession session)
				{
					failures = 0;
					await session.RebuildAsync(cancellationToken).ConfigureAwait(false);
				}
			}

			TimeSpan wait = interval;
			if (deadline is not null)
			{
				TimeSpan left = deadline.Value - UtcNow();
				if (left <= TimeSpan.Zero)
				{
					return (job, true);
				}
				wait = Min(wait, left);
			}
			await Delay(wait, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Errors a later poll may get past: no connection, a time-out, 408, 429 or 5xx.</summary>
	public static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
	{
		HordeApiException api => api.IsTransient,
		HttpRequestException => true,
		IOException => true,
		TaskCanceledException => !cancellationToken.IsCancellationRequested,
		TimeoutException => true,
		_ => false,
	};

	static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
