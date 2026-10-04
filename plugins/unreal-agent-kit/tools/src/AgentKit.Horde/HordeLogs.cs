// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EpicGames.Horde.Logs;

namespace AgentKit.Horde;

/// <summary>
/// One event of a step's log, as <c>GET api/v1/logs/{id}/events</c> returns it (EpicGames.Horde's GetLogEventResponse): the
/// severity Horde gave it when the log was written, and its lines.
/// </summary>
/// <param name="Severity">Error, Warning (or Information/Unspecified, which uak ignores).</param>
/// <param name="LineIndex">The event's first line in the log, from 0.</param>
/// <param name="LineCount">How many lines the event spans.</param>
/// <param name="Lines">The event's lines as text: each structured line's rendered message.</param>
public sealed record HordeLogEvent(LogEventSeverity Severity, int LineIndex, int LineCount, IReadOnlyList<string> Lines)
{
	/// <summary>The Horde issue the event belongs to, when there is one.</summary>
	public int? IssueId { get; init; }
}

/// <summary>Reads Horde's log responses.</summary>
public static partial class HordeLogParser
{
	/// <summary>Reads <c>GET api/v1/logs/{id}/events</c>: an array of events. Entries that aren't objects are skipped.</summary>
	public static IReadOnlyList<HordeLogEvent> ParseEvents(JsonNode? node)
	{
		if (node is not JsonArray array)
		{
			return [];
		}
		List<HordeLogEvent> events = [];
		foreach (JsonObject item in array.OfType<JsonObject>())
		{
			List<string> lines = Json.Get(item, "lines") is JsonArray values ? values.Select(LineText).ToList() : [];
			int index = Json.Int(item, "lineIndex") ?? 0;
			events.Add(new HordeLogEvent(ParseSeverity(Json.String(item, "severity")), index, Math.Max(Json.Int(item, "lineCount") ?? lines.Count, 1), lines)
			{
				IssueId = Json.Int(item, "issueId"),
			});
		}
		return events;
	}

	/// <summary>
	/// Reads <c>GET api/v1/logs/{id}/lines</c>: <c>{"index":..., "lines":[...]}</c>, each line a structured object (a JSON
	/// log) or a string (a text log). Returns each line's text.
	/// </summary>
	public static IReadOnlyList<string> ParseLines(JsonNode? node)
		=> Json.Get(node as JsonObject, "lines") is JsonArray lines ? lines.Select(LineText).ToList() : [];

	/// <summary>A severity as Horde writes it: a name ("Error") or a number (3). Anything else is Unspecified.</summary>
	public static LogEventSeverity ParseSeverity(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return LogEventSeverity.Unspecified;
		}
		if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
		{
			return Enum.IsDefined(typeof(LogEventSeverity), number) ? (LogEventSeverity)number : LogEventSeverity.Unspecified;
		}
		return Enum.TryParse(text, ignoreCase: true, out LogEventSeverity severity) ? severity : LogEventSeverity.Unspecified;
	}

	/// <summary>
	/// The text of one structured log line: its rendered "message", else its "format", else the JSON itself. A plain string is
	/// itself.
	/// </summary>
	public static string LineText(JsonNode? line) => line switch
	{
		JsonValue value => value.ToString(),
		JsonObject structured => Json.String(structured, "message") ?? Json.String(structured, "format") ?? structured.ToJsonString(),
		null => "",
		_ => line.ToJsonString(),
	};

	/// <summary>
	/// A line for display and comparison: without the Unreal log prefix "[2026.01.01-00.00.00:000][ 12]", which differs on every
	/// line, without trailing white space or line breaks, and at most <paramref name="maxLength"/> characters.
	/// </summary>
	public static string Clean(string line, int maxLength = int.MaxValue)
	{
		string text = UnrealPrefix().Replace(line, "").TrimEnd().ReplaceLineEndings(" ");
		return text.Length > maxLength ? text[..maxLength] + "..." : text;
	}

	[GeneratedRegex(@"^\[\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\d{3}\]\[\s*\d+\]")]
	private static partial Regex UnrealPrefix();
}

/// <summary>What <c>uak horde log</c> (and <c>-issues</c>) reports.</summary>
/// <param name="Errors">Report errors.</param>
/// <param name="Warnings">Report warnings.</param>
/// <param name="Max">How many distinct events each step shows at most, errors first.</param>
/// <param name="ErrorContext">Log lines shown before each error.</param>
/// <param name="WarningContext">Log lines shown before each warning.</param>
public sealed record HordeLogOptions(bool Errors = true, bool Warnings = true, int Max = HordeLogOptions.DefaultMax, int ErrorContext = HordeLogOptions.DefaultErrorContext, int WarningContext = 0)
{
	/// <summary>The default of -max.</summary>
	public const int DefaultMax = 10;

	/// <summary>The lines shown before each error by default.</summary>
	public const int DefaultErrorContext = 2;
}

/// <summary>A distinct event of a step: its first occurrence, and how often it occurred.</summary>
/// <param name="Event">The first occurrence.</param>
/// <param name="Count">How many events of the step had the same severity and text.</param>
/// <param name="Lines">The event's lines, cleaned (<see cref="HordeLogParser.Clean"/>) and capped.</param>
/// <param name="MoreLines">Lines of the event left out by the cap.</param>
public sealed record HordeLogIssue(HordeLogEvent Event, int Count, IReadOnlyList<string> Lines, int MoreLines)
{
	/// <summary>The log lines before the event, cleaned, with their indices (from 0).</summary>
	public IReadOnlyList<(int Index, string Text)> Context { get; init; } = [];
}

/// <summary>The errors and warnings of one step.</summary>
/// <param name="Step">The step.</param>
public sealed record HordeStepIssues(HordeStep Step)
{
	/// <summary>Error events in the events read.</summary>
	public int Errors { get; init; }

	/// <summary>Warning events in the events read.</summary>
	public int Warnings { get; init; }

	/// <summary>Distinct events of the severities asked for.</summary>
	public int Distinct { get; init; }

	/// <summary>How many events were read.</summary>
	public int EventsRead { get; init; }

	/// <summary>Whether the log had more events than uak reads (<see cref="HordeLogReader.MaxEvents"/>).</summary>
	public bool Truncated { get; init; }

	/// <summary>The distinct events shown, errors first, each in log order.</summary>
	public IReadOnlyList<HordeLogIssue> Shown { get; init; } = [];

	/// <summary>Distinct errors not shown, past -max.</summary>
	public int MoreErrors { get; init; }

	/// <summary>Distinct warnings not shown, past -max.</summary>
	public int MoreWarnings { get; init; }

	/// <summary>Distinct events left out because an earlier step already showed the same one.</summary>
	public int AlreadyShown { get; init; }

	/// <summary>Where the full log was saved (-save), or null.</summary>
	public string? SavedPath { get; init; }

	/// <summary>Why the events (or the saved log) couldn't be read, or null.</summary>
	public string? Problem { get; init; }
}

/// <summary>
/// Reads steps' log events from Horde and reduces them to what an agent needs: errors and warnings, de-duplicated (within a
/// step, and across steps), capped, with a little context; never the whole log.
/// </summary>
public sealed class HordeLogReader
{
	/// <summary>Events read per request.</summary>
	public int PageSize { get; init; } = 250;

	/// <summary>The most events read from one log: past this, the step says it was truncated.</summary>
	public int MaxEvents { get; init; } = 5000;

	/// <summary>The most lines shown of one event.</summary>
	public int MaxEventLines { get; init; } = 12;

	/// <summary>The longest line shown, in characters.</summary>
	public int MaxLineLength { get; init; } = 300;

	/// <summary>
	/// Reads each step's events, in the order given. An event already shown under an earlier step is counted, not shown again.
	/// A step whose log can't be read (or saved) gets a <see cref="HordeStepIssues.Problem"/>; a refused sign-in throws.
	/// </summary>
	/// <param name="api">The Horde client.</param>
	/// <param name="steps">The steps.</param>
	/// <param name="options">What to report.</param>
	/// <param name="savePath">With -save: where to save a step's full log; null saves nothing.</param>
	/// <param name="cancellationToken">Cancels the reads.</param>
	public async Task<IReadOnlyList<HordeStepIssues>> ReadAsync(IHordeApi api, IReadOnlyList<HordeStep> steps, HordeLogOptions options, Func<HordeStep, string>? savePath, CancellationToken cancellationToken)
	{
		HashSet<string> shownBefore = new(StringComparer.Ordinal);
		List<HordeStepIssues> results = [];
		foreach (HordeStep step in steps)
		{
			results.Add(await ReadStepAsync(api, step, options, savePath, shownBefore, cancellationToken).ConfigureAwait(false));
		}
		return results;
	}

	async Task<HordeStepIssues> ReadStepAsync(IHordeApi api, HordeStep step, HordeLogOptions options, Func<HordeStep, string>? savePath, HashSet<string> shownBefore, CancellationToken cancellationToken)
	{
		if (step.LogId is null)
		{
			return new HordeStepIssues(step) { Problem = "it has no log yet" };
		}

		List<HordeLogEvent> events = [];
		bool truncated = false;
		string? problem = null;
		try
		{
			while (true)
			{
				int count = Math.Min(PageSize, MaxEvents - events.Count);
				IReadOnlyList<HordeLogEvent> page = await api.GetLogEventsAsync(step.LogId, events.Count, count, cancellationToken).ConfigureAwait(false);
				events.AddRange(page);
				if (page.Count < count)
				{
					break;
				}
				if (events.Count >= MaxEvents)
				{
					// A full last page: there may be more. Ask for one more event to tell.
					truncated = (await api.GetLogEventsAsync(step.LogId, events.Count, 1, cancellationToken).ConfigureAwait(false)).Count > 0;
					break;
				}
			}
		}
		catch (Exception exception) when (IsReadFailure(exception, cancellationToken))
		{
			problem = "its events could not be read: " + exception.Message;
		}

		string? saved = null;
		if (savePath is not null)
		{
			string path = savePath(step);
			try
			{
				await api.SaveLogAsync(step.LogId, path, cancellationToken).ConfigureAwait(false);
				saved = path;
			}
			catch (Exception exception) when (IsReadFailure(exception, cancellationToken) || exception is IOException or UnauthorizedAccessException)
			{
				problem = (problem is null ? "" : problem + "; ") + "the log could not be saved: " + exception.Message;
			}
		}

		// Distinct events of the severities asked for, by severity and cleaned text, in log order.
		List<(string Key, HordeLogEvent First, int Count)> distinct = [];
		Dictionary<string, int> byKey = new(StringComparer.Ordinal);
		foreach (HordeLogEvent logEvent in events.Where(item => Wanted(item.Severity, options)).OrderBy(item => item.LineIndex))
		{
			string key = logEvent.Severity + "\n" + string.Join("\n", logEvent.Lines.Select(line => HordeLogParser.Clean(line)));
			if (byKey.TryGetValue(key, out int at))
			{
				distinct[at] = (key, distinct[at].First, distinct[at].Count + 1);
			}
			else
			{
				byKey[key] = distinct.Count;
				distinct.Add((key, logEvent, 1));
			}
		}

		int alreadyShown = distinct.Count(item => shownBefore.Contains(item.Key));
		List<(string Key, HordeLogEvent First, int Count)> candidates = distinct.Where(item => !shownBefore.Contains(item.Key))
			.OrderBy(item => item.First.Severity == LogEventSeverity.Error ? 0 : 1).ThenBy(item => item.First.LineIndex).ToList();
		List<(string Key, HordeLogEvent First, int Count)> chosen = candidates.Take(options.Max).ToList();
		List<HordeLogIssue> shown = [];
		foreach ((string key, HordeLogEvent first, int count) in chosen)
		{
			shownBefore.Add(key);
			int context = first.Severity == LogEventSeverity.Error ? options.ErrorContext : options.WarningContext;
			shown.Add(new HordeLogIssue(first, count, first.Lines.Take(MaxEventLines).Select(line => HordeLogParser.Clean(line, MaxLineLength)).ToList(), Math.Max(first.Lines.Count - MaxEventLines, 0))
			{
				Context = await ReadContextAsync(api, step.LogId, first.LineIndex, context, cancellationToken).ConfigureAwait(false),
			});
		}
		List<(string Key, HordeLogEvent First, int Count)> rest = candidates.Skip(options.Max).ToList();
		return new HordeStepIssues(step)
		{
			Errors = events.Count(item => item.Severity == LogEventSeverity.Error),
			Warnings = events.Count(item => item.Severity == LogEventSeverity.Warning),
			Distinct = distinct.Count,
			EventsRead = events.Count,
			Truncated = truncated,
			Shown = shown,
			MoreErrors = rest.Count(item => item.First.Severity == LogEventSeverity.Error),
			MoreWarnings = rest.Count(item => item.First.Severity == LogEventSeverity.Warning),
			AlreadyShown = alreadyShown,
			SavedPath = saved,
			Problem = problem,
		};
	}

	/// <summary>
	/// The <paramref name="count"/> lines before line <paramref name="lineIndex"/>. Context is a help, never a failure: when it
	/// can't be read (a text log's lines aren't JSON, a transient error), there is none.
	/// </summary>
	async Task<IReadOnlyList<(int Index, string Text)>> ReadContextAsync(IHordeApi api, string logId, int lineIndex, int count, CancellationToken cancellationToken)
	{
		int first = Math.Max(lineIndex - count, 0);
		if (count <= 0 || first >= lineIndex)
		{
			return [];
		}
		try
		{
			IReadOnlyList<string> lines = await api.GetLogLinesAsync(logId, first, lineIndex - first, cancellationToken).ConfigureAwait(false);
			return lines.Take(lineIndex - first).Select((line, offset) => (first + offset, HordeLogParser.Clean(line, MaxLineLength))).ToList();
		}
		catch (Exception exception) when (IsReadFailure(exception, cancellationToken))
		{
			return [];
		}
	}

	static bool Wanted(LogEventSeverity severity, HordeLogOptions options)
		=> (severity == LogEventSeverity.Error && options.Errors) || (severity == LogEventSeverity.Warning && options.Warnings);

	/// <summary>A failure that leaves one step (or its context) without data, not the command: anything but a refused sign-in or a cancel.</summary>
	static bool IsReadFailure(Exception exception, CancellationToken cancellationToken)
		=> exception is HordeApiException or HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

	/// <summary>
	/// Writes the steps' errors and warnings for an agent: per step one line with the counts and the log's URL, then each
	/// distinct event with its line number (from 1, as the dashboard shows), how often it occurred, its lines ("&gt;") and the
	/// context before it ("|").
	/// </summary>
	public static async Task WriteTextAsync(TextWriter output, Uri server, IReadOnlyList<HordeStepIssues> steps)
	{
		foreach (HordeStepIssues issues in steps)
		{
			HordeStep step = issues.Step;
			string counts = issues.Problem is not null && issues.EventsRead == 0 ? "" : $", {Plural(issues.Errors, "error")}, {Plural(issues.Warnings, "warning")} ({issues.Distinct} distinct)";
			string outcome = step.IsPending ? step.State : step.Outcome;
			await output.WriteLineAsync($"Step {step.Name}: {outcome}{counts}" + (step.LogId is null ? "" : $"  log: {HordeCommandBase.LogUrl(server, step.LogId)}")).ConfigureAwait(false);
			if (issues.Problem is not null)
			{
				await output.WriteLineAsync($"  Note: {issues.Problem}.").ConfigureAwait(false);
			}
			foreach (HordeLogIssue issue in issues.Shown)
			{
				string kind = issue.Event.Severity == LogEventSeverity.Error ? "error" : "warning";
				string times = issue.Count > 1 ? $" ({issue.Count} times)" : "";
				int line = issue.Event.LineIndex + 1;
				if (issue.Context.Count == 0 && issue.Lines.Count == 1)
				{
					await output.WriteLineAsync($"  {kind}, line {line}{times}: {issue.Lines[0]}").ConfigureAwait(false);
					continue;
				}
				await output.WriteLineAsync($"  {kind}, line {line}{times}:").ConfigureAwait(false);
				foreach ((int index, string text) in issue.Context)
				{
					await output.WriteLineAsync($"    {index + 1}| {text}").ConfigureAwait(false);
				}
				for (int offset = 0; offset < issue.Lines.Count; offset++)
				{
					await output.WriteLineAsync($"    {line + offset}> {issue.Lines[offset]}").ConfigureAwait(false);
				}
				if (issue.MoreLines > 0)
				{
					await output.WriteLineAsync($"    ... {Plural(issue.MoreLines, "more line")}").ConfigureAwait(false);
				}
			}
			List<string> more = [];
			if (issues.MoreErrors > 0)
			{
				more.Add(Plural(issues.MoreErrors, "more distinct error"));
			}
			if (issues.MoreWarnings > 0)
			{
				more.Add(Plural(issues.MoreWarnings, "more distinct warning"));
			}
			if (more.Count > 0)
			{
				await output.WriteLineAsync($"  ... {string.Join(" and ", more)} not shown (-max= shows more; -errors only errors).").ConfigureAwait(false);
			}
			if (issues.AlreadyShown > 0)
			{
				await output.WriteLineAsync($"  {Plural(issues.AlreadyShown, "distinct event")} not shown again: an earlier step showed the same.").ConfigureAwait(false);
			}
			if (issues.Truncated)
			{
				await output.WriteLineAsync($"  Only the first {issues.EventsRead} events were read; later ones are not counted (-save keeps the whole log).").ConfigureAwait(false);
			}
			if (issues.SavedPath is not null)
			{
				await output.WriteLineAsync($"  Full log saved: {issues.SavedPath}").ConfigureAwait(false);
			}
		}
	}

	/// <summary>The steps' errors and warnings as JSON: what <see cref="WriteTextAsync"/> prints, with URLs per event.</summary>
	public static JsonArray ToJson(Uri server, string jobId, IReadOnlyList<HordeStepIssues> steps)
	{
		JsonArray array = [];
		foreach (HordeStepIssues issues in steps)
		{
			HordeStep step = issues.Step;
			JsonArray shown = [];
			foreach (HordeLogIssue issue in issues.Shown)
			{
				JsonObject item = new()
				{
					["severity"] = issue.Event.Severity.ToString(),
					["line"] = issue.Event.LineIndex + 1,
					["lineCount"] = issue.Event.LineCount,
					["count"] = issue.Count,
					["lines"] = new JsonArray(issue.Lines.Select(line => (JsonNode)JsonValue.Create(line)).ToArray()),
				};
				if (issue.MoreLines > 0)
				{
					item["moreLines"] = issue.MoreLines;
				}
				if (issue.Context.Count > 0)
				{
					item["context"] = new JsonArray(issue.Context.Select(line => (JsonNode)new JsonObject { ["line"] = line.Index + 1, ["text"] = line.Text }).ToArray());
				}
				if (issue.Event.IssueId is int issueId)
				{
					item["issueId"] = issueId;
				}
				if (step.LogId is not null)
				{
					item["url"] = HordeCommandBase.LogUrl(server, step.LogId) + "?lineindex=" + issue.Event.LineIndex.ToString(CultureInfo.InvariantCulture);
				}
				shown.Add(item);
			}
			JsonObject result = new()
			{
				["id"] = step.Id,
				["name"] = step.Name,
				["state"] = step.State,
				["outcome"] = step.Outcome,
				["url"] = HordeCommandBase.StepUrl(server, jobId, step.Id),
				["log"] = step.LogId is null ? null : HordeCommandBase.LogUrl(server, step.LogId),
				["errors"] = issues.Errors,
				["warnings"] = issues.Warnings,
				["distinct"] = issues.Distinct,
				["eventsRead"] = issues.EventsRead,
				["truncated"] = issues.Truncated,
				["events"] = shown,
				["moreErrors"] = issues.MoreErrors,
				["moreWarnings"] = issues.MoreWarnings,
				["alreadyShown"] = issues.AlreadyShown,
				["saved"] = issues.SavedPath,
				["problem"] = issues.Problem,
			};
			array.Add(result);
		}
		return array;
	}

	static string Plural(int count, string noun) => count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");
}
