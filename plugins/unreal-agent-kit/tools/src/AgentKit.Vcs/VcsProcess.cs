// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using AgentKit.Core;

namespace AgentKit.Vcs;

/// <summary>
/// Runs a version-control command line tool through Core's <see cref="ProcessRunner"/>, so it never stops to ask for
/// credentials or take optional locks, never runs longer than its time limit, and turns a missing program into a
/// <see cref="VcsException"/>.
/// </summary>
/// <remarks>
/// <see cref="ProcessRunner.CaptureAsync(ProcessInvocation, CancellationToken)"/> is line based: it rejoins lines with '\n', so a file name holding a carriage
/// return or line feed does not survive <c>git -z</c> output intact. Such names are not supported.
/// </remarks>
internal static class VcsProcess
{
	/// <summary>The environment variable giving the time limit of each git command, in seconds.</summary>
	public const string GitTimeoutVariable = "UAK_GIT_TIMEOUT";

	/// <summary>The time limit of a git command when <see cref="GitTimeoutVariable"/> is unset.</summary>
	public static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromSeconds(30);

	static readonly Dictionary<string, string?> s_environment = new(ProcessInvocation.EnvironmentComparer)
	{
		["GIT_TERMINAL_PROMPT"] = "0",
		["GIT_OPTIONAL_LOCKS"] = "0",
	};

	/// <summary>The git time limit from <see cref="GitTimeoutVariable"/> (positive seconds), else <see cref="DefaultGitTimeout"/>.</summary>
	public static TimeSpan GetGitTimeout(Func<string, string?>? getEnvironmentVariable = null)
	{
		string? value = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(GitTimeoutVariable);
		return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0 && seconds < int.MaxValue / 1000.0
			? TimeSpan.FromSeconds(seconds)
			: DefaultGitTimeout;
	}

	/// <summary>
	/// Runs the tool to completion and captures its output. A bare name ("git") is found on PATH only, never in the current
	/// directory (<see cref="ExecutableLocator"/>). After <paramref name="timeout"/> the tool and everything it started are
	/// killed, and a <see cref="VcsException"/> says so (naming <paramref name="timeoutVariable"/>, which sets the limit).
	/// Throws <see cref="VcsException"/> too when it cannot be started.
	/// </summary>
	/// <param name="runner">Runs the tool.</param>
	/// <param name="fileName">The tool: a bare name or a path.</param>
	/// <param name="arguments">Its arguments.</param>
	/// <param name="what">The command, for the time-limit message (for example "git status").</param>
	/// <param name="timeout">How long it may run.</param>
	/// <param name="timeoutVariable">The environment variable that changes the limit, for the message.</param>
	/// <param name="cancellationToken">Cancels the run (and kills the tool); throws <see cref="OperationCanceledException"/>.</param>
	public static async Task<ProcessCapture> RunAsync(ProcessRunner runner, string fileName, IReadOnlyList<string> arguments, string what, TimeSpan timeout,
		string timeoutVariable, CancellationToken cancellationToken)
	{
		string resolved = ExecutableLocator.Resolve(fileName)
			?? throw new VcsException($"Could not run '{fileName}': it was not found on PATH. Is it installed and on PATH?");
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(timeout);
		try
		{
			return await runner.CaptureAsync(new ProcessInvocation(resolved, arguments, null, s_environment), limit.Token).ConfigureAwait(false);
		}
		catch (ProcessStartException exception)
		{
			throw new VcsException($"Could not run '{fileName}': {exception.Message} Is it installed and on PATH?", exception);
		}
		catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			// The runner killed it, and what it started, on the way out.
			throw new VcsException(string.Create(CultureInfo.InvariantCulture,
				$"{what} did not finish within {timeout.TotalSeconds:0.#} s, so it was stopped (set {timeoutVariable} to change the limit)."), exception);
		}
	}
}
