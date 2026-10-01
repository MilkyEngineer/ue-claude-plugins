// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentKit.Unreal;

/// <summary>
/// Echoes UBT's latest "[n/N] ..." action line at most once per interval (a minute by default), so a long build or compile
/// is visibly alive to whoever follows uak's output, without printing every action. Nothing is echoed in the first interval,
/// so a short run prints no progress at all.
/// </summary>
internal sealed partial class UbtProgress
{
	/// <summary>How often progress is echoed at most.</summary>
	public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

	readonly TimeProvider Clock;
	readonly TimeSpan Interval;
	readonly Action<string> Report;
	readonly DateTimeOffset Started;
	DateTimeOffset LastReport;

	/// <summary>Creates the reporter; the interval starts now.</summary>
	/// <param name="clock">The clock.</param>
	/// <param name="interval">The shortest time between two echoes.</param>
	/// <param name="report">Gets each echo: the action line and the time since the start.</param>
	public UbtProgress(TimeProvider clock, TimeSpan interval, Action<string> report)
	{
		Clock = clock;
		Interval = interval;
		Report = report;
		Started = clock.GetUtcNow();
		LastReport = Started;
	}

	/// <summary>Takes the next line of UBT's output.</summary>
	public void AddLine(string line)
	{
		Match Progress = ProgressPattern().Match(line);
		if (!Progress.Success)
		{
			return;
		}
		DateTimeOffset Now = Clock.GetUtcNow();
		if (Now - LastReport < Interval)
		{
			return;
		}
		LastReport = Now;
		Report($"{line.Trim()} ({FormatElapsed(Now - Started)} so far)");
	}

	/// <summary>"45s", "12m 05s" or "1h 02m".</summary>
	internal static string FormatElapsed(TimeSpan span)
	{
		long Seconds = Math.Max(0, (long)span.TotalSeconds);
		return Seconds >= 3600 ? string.Create(CultureInfo.InvariantCulture, $"{Seconds / 3600}h {Seconds / 60 % 60:00}m")
			: Seconds >= 60 ? string.Create(CultureInfo.InvariantCulture, $"{Seconds / 60}m {Seconds % 60:00}s")
			: string.Create(CultureInfo.InvariantCulture, $"{Seconds}s");
	}

	// "[12/345] Compile [x64] Foo.cpp", as BuildLogParser's actions.
	[GeneratedRegex(@"^\s*\[\d+/\d+\]\s")]
	private static partial Regex ProgressPattern();
}
