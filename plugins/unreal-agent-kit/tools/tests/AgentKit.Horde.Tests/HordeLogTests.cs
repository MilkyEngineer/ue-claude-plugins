// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Core;
using EpicGames.Horde.Logs;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Horde.Tests;

/// <summary><c>uak horde log</c>, <c>-issues</c>, and the log reader, over a fake Horde with synthetic logs.</summary>
[TestClass]
public sealed class HordeLogTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	static readonly UakContext s_context = new() { StateDirectory = new DirectoryInfo(Path.GetTempPath()), Logger = NullLogger.Instance };

	static HordeLogEvent Error(int line, params string[] lines) => new(LogEventSeverity.Error, line, lines.Length, lines);

	static HordeLogEvent Warning(int line, params string[] lines) => new(LogEventSeverity.Warning, line, lines.Length, lines);

	static List<string> Lines(int count) => Enumerable.Range(0, count).Select(index => $"line {index}").ToList();

	static HordeStep Step(string id, string name, string outcome, string? logId) => new(id, name, 0, 0, "Completed", outcome, logId);

	/// <summary>
	/// A failed job: Compile Editor failed (log1: one error, one warning), Cook has warnings (log2: the same warning, and
	/// another), Test succeeded (log3, which has a warning that must not be read).
	/// </summary>
	static FakeHordeApi FailedJob()
	{
		FakeHordeApi api = new();
		api.JobAnswers.Add(Jobs.Job("Complete", "t", Jobs.Batch(0,
			Jobs.Step("s1", "Compile Editor", 0, "Completed", "Failure", "log1"),
			Jobs.Step("s2", "Cook", 1, "Completed", "Warnings", "log2"),
			Jobs.Step("s3", "Test", 2, "Completed", "Success", "log3"))));
		api.LogEvents["log1"] = [Warning(5, "LogTemp: Warning: shared warning"), Error(20, "Source/Module/A.cpp(12): error C2065: 'x': undeclared identifier", "  while compiling A.cpp")];
		api.LogEvents["log2"] = [Warning(7, "[2026.01.01-00.00.00:000][  3]LogTemp: Warning: shared warning"), Warning(9, "LogCook: Warning: another warning")];
		api.LogEvents["log3"] = [Warning(1, "LogTemp: Warning: from a passing step")];
		api.LogLines["log1"] = Lines(40);
		api.LogLines["log2"] = Lines(20);
		return api;
	}

	[TestMethod]
	public void EventsAndLinesAreParsed()
	{
		JsonNode events = JsonNode.Parse("""
			[
				{ "logId": "log1", "severity": "Error", "lineIndex": 10, "lineCount": 2, "issueId": 42, "lines": [
					{ "time": "2026-01-01T00:00:00Z", "level": "Error", "message": "Source/A.cpp(12): error C2065: 'x': undeclared identifier", "format": "{file}({line}): error", "properties": {} },
					{ "level": "Error", "message": "  while compiling A.cpp" } ] },
				{ "severity": 2, "lineIndex": 3, "lineCount": 1, "lines": [ "a plain text warning" ] },
				{ "severity": "Information", "lineIndex": 1, "lines": [ { "format": "only a format" } ] },
				"not an event"
			]
			""")!;

		IReadOnlyList<HordeLogEvent> parsed = HordeLogParser.ParseEvents(events);

		Assert.HasCount(3, parsed);
		Assert.AreEqual(LogEventSeverity.Error, parsed[0].Severity);
		Assert.AreEqual(10, parsed[0].LineIndex);
		Assert.AreEqual(2, parsed[0].LineCount);
		Assert.AreEqual(42, parsed[0].IssueId);
		CollectionAssert.AreEqual(new[] { "Source/A.cpp(12): error C2065: 'x': undeclared identifier", "  while compiling A.cpp" }, parsed[0].Lines.ToArray());
		Assert.AreEqual(LogEventSeverity.Warning, parsed[1].Severity, "a number is a severity too");
		Assert.AreEqual("a plain text warning", parsed[1].Lines.Single());
		Assert.AreEqual("only a format", parsed[2].Lines.Single());
		Assert.IsEmpty(HordeLogParser.ParseEvents(JsonNode.Parse("{}")));

		CollectionAssert.AreEqual(new[] { "a", "b" }, HordeLogParser.ParseLines(JsonNode.Parse("""{ "index": 0, "count": 2, "lines": [ { "message": "a" }, "b" ] }""")).ToArray());
		Assert.IsEmpty(HordeLogParser.ParseLines(null));
		Assert.AreEqual(LogEventSeverity.Unspecified, HordeLogParser.ParseSeverity("bogus"));
		Assert.AreEqual(LogEventSeverity.Unspecified, HordeLogParser.ParseSeverity("7"));
		Assert.AreEqual(LogEventSeverity.Error, HordeLogParser.ParseSeverity("error"));
	}

	[TestMethod]
	public void CleanDropsTheUnrealPrefixAndCaps()
	{
		Assert.AreEqual("LogCook: Warning: x", HordeLogParser.Clean("[2026.01.01-00.00.00:000][ 12]LogCook: Warning: x  \r\n"));
		Assert.AreEqual("[not a prefix] x", HordeLogParser.Clean("[not a prefix] x"));
		Assert.AreEqual("aaaa...", HordeLogParser.Clean(new string('a', 10), 4));
		Assert.AreEqual("a b", HordeLogParser.Clean("a\nb"));
	}

	[TestMethod]
	public async Task TheReaderDeduplicatesAndPutsErrorsFirstWithContext()
	{
		FakeHordeApi api = new();
		api.LogEvents["log1"] =
		[
			Warning(5, "LogA: Warning: w1"),
			Error(20, "e1"),
			Warning(30, "[2026.01.01-00.00.00:000][  1]LogA: Warning: w1"),
			Error(40, "e2"),
			new HordeLogEvent(LogEventSeverity.Information, 50, 1, ["info"]),
		];
		api.LogLines["log1"] = Lines(60);

		HordeStepIssues issues = (await new HordeLogReader().ReadAsync(api, [Step("s1", "Compile", "Failure", "log1")], new HordeLogOptions(), null, Token)).Single();

		Assert.AreEqual(2, issues.Errors);
		Assert.AreEqual(2, issues.Warnings);
		Assert.AreEqual(3, issues.Distinct, "the two w1 differ only in Unreal's time prefix");
		Assert.AreEqual(5, issues.EventsRead);
		CollectionAssert.AreEqual(new[] { "e1", "e2", "LogA: Warning: w1" }, issues.Shown.Select(issue => issue.Lines[0]).ToArray());
		Assert.AreEqual(2, issues.Shown[2].Count);
		Assert.AreEqual(5, issues.Shown[2].Event.LineIndex, "the first occurrence");
		CollectionAssert.AreEqual(new[] { (18, "line 18"), (19, "line 19") }, issues.Shown[0].Context.ToArray());
		Assert.IsEmpty(issues.Shown[2].Context, "no context before warnings by default");
		Assert.HasCount(2, api.LineRequests, "context is read only for what is shown");
		Assert.IsNull(issues.Problem);
	}

	[TestMethod]
	public async Task TheReaderCapsAndCountsWhatItLeavesOut()
	{
		FakeHordeApi api = new();
		api.LogEvents["log1"] = [Warning(1, "w1"), Warning(2, "w2"), Error(3, "e1"), Warning(4, "w3"), Error(5, "e2"), Error(6, "e3")];

		HordeStepIssues issues = (await new HordeLogReader().ReadAsync(api, [Step("s1", "Compile", "Failure", "log1")], new HordeLogOptions(Max: 2, ErrorContext: 0), null, Token)).Single();

		CollectionAssert.AreEqual(new[] { "e1", "e2" }, issues.Shown.Select(issue => issue.Lines[0]).ToArray());
		Assert.AreEqual(1, issues.MoreErrors);
		Assert.AreEqual(3, issues.MoreWarnings);
		Assert.IsEmpty(api.LineRequests, "no context asked for");

		HordeStepIssues errorsOnly = (await new HordeLogReader().ReadAsync(api, [Step("s1", "Compile", "Failure", "log1")], new HordeLogOptions(Warnings: false, ErrorContext: 0), null, Token)).Single();
		Assert.AreEqual(3, errorsOnly.Distinct);
		Assert.AreEqual(0, errorsOnly.MoreWarnings);
		Assert.AreEqual(3, errorsOnly.Warnings, "the counts are of every event read");
	}

	[TestMethod]
	public async Task TheReaderCapsLongEvents()
	{
		FakeHordeApi api = new();
		api.LogEvents["log1"] = [Error(0, [.. Enumerable.Range(0, 15).Select(index => $"part {index}")])];

		HordeLogIssue issue = (await new HordeLogReader().ReadAsync(api, [Step("s1", "Compile", "Failure", "log1")], new HordeLogOptions(), null, Token)).Single().Shown.Single();

		Assert.HasCount(12, issue.Lines);
		Assert.AreEqual(3, issue.MoreLines);
		Assert.IsEmpty(api.LineRequests, "an event on line 1 has nothing before it");
	}

	[TestMethod]
	[DataRow(5, false)]
	[DataRow(7, true)]
	public async Task TheReaderPagesAndStopsAtItsLimit(int events, bool truncated)
	{
		FakeHordeApi api = new();
		api.LogEvents["log1"] = [.. Enumerable.Range(0, events).Select(index => Warning(index, $"w{index}"))];

		HordeStepIssues issues = (await new HordeLogReader { PageSize = 2, MaxEvents = 5 }.ReadAsync(api, [Step("s1", "Cook", "Warnings", "log1")], new HordeLogOptions(), null, Token)).Single();

		Assert.AreEqual(5, issues.EventsRead);
		Assert.AreEqual(truncated, issues.Truncated);
		CollectionAssert.AreEqual(new[] { (0, 2), (2, 2), (4, 1), (5, 1) }, api.EventRequests.Select(request => (request.Index, request.Count)).ToArray());
	}

	[TestMethod]
	public async Task AnEventAnEarlierStepShowedIsOnlyCounted()
	{
		FakeHordeApi api = FailedJob();

		IReadOnlyList<HordeStepIssues> issues = await new HordeLogReader().ReadAsync(api, [Step("s1", "Compile Editor", "Failure", "log1"), Step("s2", "Cook", "Warnings", "log2")], new HordeLogOptions(), null, Token);

		Assert.AreEqual(1, issues[1].AlreadyShown);
		Assert.AreEqual("LogCook: Warning: another warning", issues[1].Shown.Single().Lines[0]);
		Assert.AreEqual(2, issues[1].Distinct);
	}

	[TestMethod]
	public async Task UnreadableLogsAndContextAreNotesButARefusedSignInIsNot()
	{
		FakeHordeApi api = FailedJob();
		api.LogFailures["log2"] = new HordeApiException(404, "no such log");
		api.LinesFailure = new HordeApiException(200, "Horde answered with something that isn't JSON");

		IReadOnlyList<HordeStepIssues> issues = await new HordeLogReader().ReadAsync(api, [Step("s1", "Compile Editor", "Failure", "log1"), Step("s2", "Cook", "Warnings", "log2"), Step("s4", "Later", "Unspecified", null)], new HordeLogOptions(), null, Token);

		Assert.IsEmpty(issues[0].Shown[0].Context, "context that can't be read is left out");
		Assert.IsNull(issues[0].Problem);
		StringAssert.Contains(issues[1].Problem, "could not be read: no such log");
		Assert.AreEqual("it has no log yet", issues[2].Problem);

		api.LogFailures["log1"] = new HordeAuthException("not signed in", notSignedIn: true);
		await Assert.ThrowsExactlyAsync<HordeAuthException>(() => new HordeLogReader().ReadAsync(api, [Step("s1", "Compile Editor", "Failure", "log1")], new HordeLogOptions(), null, Token));
	}

	[TestMethod]
	public async Task LogReportsTheFailedStepsThenThoseWithWarnings()
	{
		FakeHordeApi api = FailedJob();
		using StringWriter output = new();

		int result = await HordeCommandTests.Setup(new HordeLogCommand(), api, null, output).RunAsync(s_context, ["-job=job1"], Token);

		Assert.AreEqual(HordeExitCodes.Failure, result, "the job's result, as uak horde job");
		string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		CollectionAssert.AreEqual(new[]
		{
			"Result: Failure: Preflight 12345",
			"  https://horde.example.com/job/job1",
			"Step Compile Editor: Failure, 1 error, 1 warning (2 distinct)  log: https://horde.example.com/log/log1",
			"  error, line 21:",
			"    19| line 18",
			"    20| line 19",
			"    21> Source/Module/A.cpp(12): error C2065: 'x': undeclared identifier",
			"    22>   while compiling A.cpp",
			"  warning, line 6: LogTemp: Warning: shared warning",
			"Step Cook: Warnings, 0 errors, 2 warnings (2 distinct)  log: https://horde.example.com/log/log2",
			"  warning, line 10: LogCook: Warning: another warning",
			"  1 distinct event not shown again: an earlier step showed the same.",
		}, lines);
		Assert.IsFalse(api.EventRequests.Any(request => request.LogId == "log3"), "a step that passed is not read");
	}

	[TestMethod]
	public async Task LogTakesAStepFiltersAndPrintsJson()
	{
		FakeHordeApi api = FailedJob();
		using StringWriter output = new();

		int result = await HordeCommandTests.Setup(new HordeLogCommand(), api, null, output).RunAsync(s_context, ["-job=job1", "-step=COOK", "-warnings", "-max=1", "-json"], Token);

		Assert.AreEqual(HordeExitCodes.Failure, result);
		using JsonDocument document = JsonDocument.Parse(output.ToString());
		JsonElement root = document.RootElement;
		Assert.AreEqual("Failure", root.GetProperty("result").GetString());
		JsonElement step = root.GetProperty("steps").EnumerateArray().Single();
		Assert.AreEqual("Cook", step.GetProperty("name").GetString());
		JsonElement shown = step.GetProperty("events").EnumerateArray().Single();
		Assert.AreEqual("Warning", shown.GetProperty("severity").GetString());
		Assert.AreEqual(8, shown.GetProperty("line").GetInt32());
		Assert.AreEqual("LogTemp: Warning: shared warning", shown.GetProperty("lines")[0].GetString());
		Assert.AreEqual("https://horde.example.com/log/log2?lineindex=7", shown.GetProperty("url").GetString());
		Assert.AreEqual(1, step.GetProperty("moreWarnings").GetInt32());

		// A name part matches, an id matches exactly; -errors leaves the warnings out.
		using StringWriter errors = new();
		Assert.AreEqual(HordeExitCodes.Failure, await HordeCommandTests.Setup(new HordeLogCommand(), FailedJob(), null, errors).RunAsync(s_context, ["-job=job1", "-step=s1", "-errors"], Token));
		Assert.DoesNotContain("warning, line", errors.ToString());
		StringAssert.Contains(errors.ToString(), "error, line 21:");

		using StringWriter none = new();
		Assert.AreEqual(HordeExitCodes.Error, await HordeCommandTests.Setup(new HordeLogCommand(), FailedJob(), null, none).RunAsync(s_context, ["-job=job1", "-step=Package"], Token));
	}

	[TestMethod]
	public async Task LogSaveKeepsTheWholeLogInTheStateFolder()
	{
		string state = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		UakContext context = new() { StateDirectory = new DirectoryInfo(state), Logger = NullLogger.Instance };
		FakeHordeApi api = FailedJob();
		using StringWriter output = new();

		Assert.AreEqual(HordeExitCodes.Failure, await HordeCommandTests.Setup(new HordeLogCommand(), api, null, output).RunAsync(context, ["-job=job1", "-step=Compile Editor", "-save"], Token));

		string saved = Path.Combine(state, "Logs", "horde", "job1", "Compile_Editor-s1.log");
		StringAssert.Contains(output.ToString(), "  Full log saved: " + saved);
		Assert.AreEqual(string.Join('\n', Lines(40)), File.ReadAllText(saved));
		Directory.Delete(state, recursive: true);
	}

	[TestMethod]
	public async Task LogOfARunningJobWithNothingWrongIsThree()
	{
		FakeHordeApi api = new();
		api.JobAnswers.Add(Jobs.Job("Running", "t", Jobs.Batch(0, Jobs.Step("s1", "Compile", 0, "Running", "Unspecified", "log1"))));
		using StringWriter output = new();

		Assert.AreEqual(HordeExitCodes.StillRunning, await HordeCommandTests.Setup(new HordeLogCommand(), api, null, output).RunAsync(s_context, ["-job=job1"], Token));
		StringAssert.Contains(output.ToString(), "No step failed or has warnings so far.");
		Assert.IsEmpty(api.EventRequests);
	}

	[TestMethod]
	public async Task LogArgumentsAreChecked()
	{
		FakeHordeApi api = FailedJob();
		using StringWriter output = new();
		foreach (string[] arguments in new[]
		{
			Array.Empty<string>(),
			["-job=a/b"],
			["-job=job1", "-errors", "-warnings"],
			["-job=job1", "-max=0"],
			["-job=job1", "-context=51"],
			["-job=job1", "-step="],
		})
		{
			await Assert.ThrowsExactlyAsync<UakUsageException>(() => HordeCommandTests.Setup(new HordeLogCommand(), api, null, output).RunAsync(s_context, arguments, Token), string.Join(" ", arguments));
		}
		StringAssert.Contains(new HordeLogCommand().Usage, "3 still running");
	}

	[TestMethod]
	public async Task JobIssuesAddsTheStepsEvents()
	{
		using StringWriter output = new();
		Assert.AreEqual(HordeExitCodes.Failure, await HordeCommandTests.Setup(new HordeJobCommand(), FailedJob(), null, output).RunAsync(s_context, ["-id=job1", "-issues"], Token));
		string text = output.ToString();
		StringAssert.StartsWith(text, "Result: Failure: Preflight 12345");
		StringAssert.Contains(text, "  FAILED   Compile Editor");
		StringAssert.Contains(text, "Step Compile Editor: Failure, 1 error, 1 warning (2 distinct)");
		StringAssert.Contains(text, "    21> Source/Module/A.cpp(12): error C2065: 'x': undeclared identifier");
		StringAssert.Contains(text, "More: uak horde log -job=job1");

		using StringWriter json = new();
		Assert.AreEqual(HordeExitCodes.Failure, await HordeCommandTests.Setup(new HordeJobCommand(), FailedJob(), null, json).RunAsync(s_context, ["-id=job1", "-issues", "-json"], Token));
		using JsonDocument document = JsonDocument.Parse(json.ToString());
		Assert.AreEqual(2, document.RootElement.GetProperty("issues").GetArrayLength());

		// Without -issues nothing is read; a job that passed has nothing to read.
		FakeHordeApi plain = FailedJob();
		Assert.AreEqual(HordeExitCodes.Failure, await HordeCommandTests.Setup(new HordeJobCommand(), plain, null, new StringWriter()).RunAsync(s_context, ["-id=job1"], Token));
		Assert.IsEmpty(plain.EventRequests);
		FakeHordeApi passed = new();
		passed.JobAnswers.Add(Jobs.Job("Complete", "t", Jobs.Batch(0, Jobs.Step("s1", "Compile", 0, "Completed", "Success", "log1"))));
		using StringWriter quiet = new();
		Assert.AreEqual(HordeExitCodes.Success, await HordeCommandTests.Setup(new HordeJobCommand(), passed, null, quiet).RunAsync(s_context, ["-id=job1", "-issues"], Token));
		Assert.IsEmpty(passed.EventRequests);
		Assert.DoesNotContain("More:", quiet.ToString());

		await Assert.ThrowsExactlyAsync<UakUsageException>(() => HordeCommandTests.Setup(new HordePreflightCommand(), FailedJob(), new FakeWorkspace(), new StringWriter()).RunAsync(s_context, ["-c=12345", "-issues"], Token));
	}

	[TestMethod]
	public async Task TheSessionRetriesALogReadAfterARefusedSavedToken()
	{
		FakeHordeApi refusing = new();
		refusing.LogFailures["log1"] = new HordeAuthException("not signed in", notSignedIn: true);
		FakeHordeApi fresh = new();
		fresh.LogEvents["log1"] = [Error(3, "e1")];
		bool forgot = false;
		await using HordeApiSession session = new(refusing, fromCache: true, (_, _) => Task.FromResult<IHordeApi?>(fresh), _ => fresh, () => forgot = true, NullLogger.Instance);

		IReadOnlyList<HordeLogEvent> events = await session.GetLogEventsAsync("log1", 0, 10, Token);

		Assert.AreEqual("e1", events.Single().Lines[0]);
		Assert.IsTrue(forgot, "the refused token is deleted");
		Assert.IsTrue(refusing.Disposed);
	}
}
