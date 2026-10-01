// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Net.Http;
using System.Text.Json;
using AgentKit.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Horde.Tests;

/// <summary>The waiter and the <c>uak horde</c> commands, over a fake Horde and a fake clock.</summary>
[TestClass]
public sealed class HordeCommandTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	static readonly UakContext s_context = new() { StateDirectory = new DirectoryInfo(Path.GetTempPath()), Logger = NullLogger.Instance };

	static FakeHordeApi WithStreams(FakeHordeApi? api = null)
	{
		api ??= new();
		api.Streams.Add(new HordeStream("project-main", "//Project/Main", "project", "editor-preflight", [new("editor-preflight", "Editor Preflight", true, true)]));
		return api;
	}

	static readonly Uri s_server = new("https://horde.example.com/");

	/// <summary>
	/// Points a command at fakes. Its kit folder is <paramref name="home"/> (a new temporary one by default), where the
	/// stream project-main has saved build settings (editor-preflight, its defaults) unless <paramref name="savedBuild"/> is
	/// false. The token cache is off unless <paramref name="cache"/> is given.
	/// </summary>
	static T Setup<T>(T command, FakeHordeApi api, FakeWorkspace? workspace, StringWriter output, FakeClock? clock = null, string? configPath = null, List<string>? opened = null, StringWriter? errors = null, string? home = null, bool savedBuild = true, HordeTokenCache? cache = null) where T : HordeCommandBase
	{
		home ??= Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		HordeBuildSettingsStore store = new(Path.Combine(home, "horde"));
		if (savedBuild && store.Read(s_server, "project-main") is null)
		{
			store.Save(s_server, "project-main", new HordeTemplate("editor-preflight", "Editor Preflight", true, true), new Dictionary<string, string>(), DateTime.UtcNow);
		}
		command.BuildSettings = () => store;
		command.TokenCache = () => cache ?? new HordeTokenCache(Path.Combine(home, "horde"), null);
		command.Output = output;
		command.ErrorOutput = errors ?? new StringWriter();
		command.OpenUrl = url =>
		{
			opened?.Add(url);
			return null;
		};
		string path = configPath ?? Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"), "config.json");
		command.Config = () => new HordeConfig(path);
		command.HordeDefault = () => (new Uri("https://horde.example.com/"), "test default");
		command.CreateApi = (_, _, _, _) => api;
		command.Workspace = (_, _) => workspace is null ? throw new UakUsageException("no workspace in this test") : Task.FromResult<IPreflightWorkspace>(workspace);
		command.Waiter = (clock ?? new FakeClock()).Waiter();
		return command;
	}

	static JsonObjectJob Done(string outcome) => new(Jobs.Job("Complete", "t2", Jobs.Batch(0, Jobs.Step("s1", "Compile", 0, "Completed", outcome, "log1"), Jobs.Step("s2", "Test", 1, "Completed", "Success"))));

	sealed record JsonObjectJob(System.Text.Json.Nodes.JsonObject Json);

	[TestMethod]
	public async Task TheWaiterIsQuietAndBacksOff()
	{
		FakeHordeApi api = new();
		api.JobAnswers.AddRange([Jobs.Job("Waiting", "t1"), null, null, null, null, null, Jobs.Job("Running", "t2", Jobs.Batch(0, Jobs.Step("s", "A", 0, "Running", "Unspecified"))), null, Done("Success").Json]);
		FakeClock clock = new();
		using StringWriter output = new();

		(HordeJob? job, bool timedOut) = await clock.Waiter().WaitAsync(api, "job1", null, output, Token);

		Assert.IsFalse(timedOut);
		Assert.AreEqual(HordeJobResult.Success, job!.Result);
		Assert.AreEqual("Job job1: Running.", output.ToString().Trim(), "one line, on the state change");
		CollectionAssert.AreEqual(new[] { 15, 30, 45, 60, 60, 60, 15, 30 }, clock.Sleeps.Select(sleep => (int)sleep.TotalSeconds).ToArray());
		CollectionAssert.AreEqual(new string?[] { null, "t1", "t1", "t1", "t1", "t1", "t1", "t2", "t2" }, api.ModifiedAfter.ToArray(), "modifiedAfter, so unchanged polls are cheap");
	}

	[TestMethod]
	public async Task TheWaiterSurvivesTransientErrorsAndStopsAtTheTimeout()
	{
		FakeHordeApi api = new();
		api.JobAnswers.AddRange([Jobs.Job("Running", "t1"), new HttpRequestException("connection reset"), new HordeApiException(503, "busy"), null]);
		FakeClock clock = new();
		using StringWriter output = new();

		(HordeJob? job, bool timedOut) = await clock.Waiter().WaitAsync(api, "job1", TimeSpan.FromSeconds(200), output, Token);

		Assert.IsTrue(timedOut);
		Assert.AreEqual("job1", job!.Id);
		Assert.AreEqual(200, (int)clock.Sleeps.Sum(sleep => sleep.TotalSeconds), "never waits past the deadline");
		string[] lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		Assert.HasCount(2, lines);
		StringAssert.StartsWith(lines[0], "Horde can't be reached (connection reset)");
		Assert.AreEqual("Horde answers again.", lines[1]);
	}

	[TestMethod]
	public async Task TheWaiterStopsOnAuthAndHardErrors()
	{
		FakeHordeApi auth = new();
		auth.JobAnswers.Add(new HordeAuthException("401"));
		await Assert.ThrowsExactlyAsync<HordeAuthException>(() => new FakeClock().Waiter().WaitAsync(auth, "job1", null, TextWriter.Null, Token));

		FakeHordeApi missing = new();
		missing.JobAnswers.Add(new HordeApiException(404, "no such job"));
		await Assert.ThrowsExactlyAsync<HordeApiException>(() => new FakeClock().Waiter().WaitAsync(missing, "job1", null, TextWriter.Null, Token));
	}

	[TestMethod]
	[DataRow("Success", 0)]
	[DataRow("Failure", 1)]
	[DataRow("Warnings", 7)]
	public async Task JobWaitExitCodesFollowTheResult(string outcome, int exitCode)
	{
		FakeHordeApi api = new();
		api.JobAnswers.Add(Done(outcome).Json);
		using StringWriter output = new();

		int result = await Setup(new HordeJobCommand(), api, null, output).RunAsync(s_context, ["-id=job1", "-wait"], Token);

		Assert.AreEqual(exitCode, result);
		string text = output.ToString();
		StringAssert.StartsWith(text, "Job URL: https://horde.example.com/job/job1" + Environment.NewLine + $"Result: {outcome}: Preflight 12345");
		StringAssert.Contains(text, "https://horde.example.com/job/job1");
		if (outcome == "Success")
		{
			Assert.DoesNotContain("Compile", text, "passing steps are not listed");
		}
		else
		{
			StringAssert.Contains(text, $"{(outcome == "Failure" ? "FAILED  " : "WARNINGS")} Compile  https://horde.example.com/job/job1?step=s1  log: https://horde.example.com/log/log1");
			Assert.DoesNotContain(" Test ", text);
		}
	}

	[TestMethod]
	public async Task JobWithoutWaitReportsStillRunningAsThree()
	{
		FakeHordeApi api = new();
		api.JobAnswers.Add(Jobs.Job("Running", "t", Jobs.Batch(0, Jobs.Step("s", "Compile", 0, "Running", "Unspecified"))));
		using StringWriter output = new();

		Assert.AreEqual(HordeExitCodes.StillRunning, await Setup(new HordeJobCommand(), api, null, output).RunAsync(s_context, ["-id=job1"], Token));
		StringAssert.StartsWith(output.ToString(), "Still running (Running)");
	}

	[TestMethod]
	public async Task JobWaitTimeoutIsThreeAndJsonIsParseable()
	{
		FakeHordeApi api = new();
		api.JobAnswers.AddRange([Jobs.Job("Running", "t"), null]);
		using StringWriter output = new();

		int result = await Setup(new HordeJobCommand(), api, null, output).RunAsync(s_context, ["-id=job1", "-wait", "-timeout=60", "-json"], Token);

		Assert.AreEqual(HordeExitCodes.StillRunning, result);
		using JsonDocument document = JsonDocument.Parse(output.ToString());
		Assert.IsTrue(document.RootElement.GetProperty("timedOut").GetBoolean());
		Assert.AreEqual("Running", document.RootElement.GetProperty("result").GetString());
	}

	[TestMethod]
	public async Task WithoutATokenTheCommandSignsInUnlessNoLogin()
	{
		FakeHordeApi api = new() { LoggedIn = false };
		api.JobAnswers.Add(Done("Success").Json);
		List<bool> prompts = [];
		using StringWriter output = new();
		HordeJobCommand command = Setup(new HordeJobCommand(), api, null, output);
		command.CreateApi = (_, prompt, _, _) =>
		{
			prompts.Add(prompt);
			return api;
		};

		Assert.AreEqual(0, await command.RunAsync(s_context, ["-id=job1", "-wait"], Token));
		CollectionAssert.AreEqual(new[] { false, true }, prompts, "silent first, then the sign-in page");
		Assert.AreEqual(1, api.Logins);
		StringAssert.StartsWith(output.ToString(), HordeCommandBase.OpeningMessage + Environment.NewLine + "Signed in to https://horde.example.com/.");

		prompts.Clear();
		Assert.AreEqual(HordeExitCodes.NotLoggedIn, await command.RunAsync(s_context, ["-id=job1", "-wait", "-no-login"], Token));
		CollectionAssert.AreEqual(new[] { false }, prompts, "-no-login never opens the sign-in page");
		Assert.AreEqual(1, api.Logins);

		api.LoginResult = false;
		Assert.AreEqual(HordeExitCodes.NotLoggedIn, await command.RunAsync(s_context, ["-id=job1"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => command.RunAsync(s_context, ["-id=job1", "-no-login", "-login-timeout=5"], Token));

		FakeHordeApi expired = new();
		expired.JobAnswers.Add(new HordeAuthException("token expired", notSignedIn: true));
		Assert.AreEqual(HordeExitCodes.NotLoggedIn, await Setup(new HordeJobCommand(), expired, null, output).RunAsync(s_context, ["-id=job1", "-wait"], Token));
	}

	[TestMethod]
	public async Task AnUnfinishedSignInTimesOutAsFour()
	{
		FakeHordeApi api = new() { LoggedIn = false, LoginHangs = true };
		using StringWriter output = new();

		Assert.AreEqual(HordeExitCodes.NotLoggedIn, await Setup(new HordePreflightCommand(), WithStreams(api), new FakeWorkspace(), output).RunAsync(s_context, ["-c=12345", "-login-timeout=1"], Token));
		Assert.IsEmpty(api.Created, "nothing is started without a sign-in");
		StringAssert.StartsWith(output.ToString(), HordeCommandBase.OpeningMessage);
	}

	[TestMethod]
	public async Task OtherErrorsAreFive()
	{
		FakeHordeApi api = new();
		api.JobAnswers.Add(new HordeApiException(404, "no such job"));
		using StringWriter output = new();
		Assert.AreEqual(HordeExitCodes.Error, await Setup(new HordeJobCommand(), api, null, output).RunAsync(s_context, ["-id=job1"], Token));
	}

	[TestMethod]
	public async Task JobArgumentsAreChecked()
	{
		using StringWriter output = new();
		FakeHordeApi api = new();
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeJobCommand(), api, null, output).RunAsync(s_context, [], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeJobCommand(), api, null, output).RunAsync(s_context, ["-id=a/b"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeJobCommand(), api, null, output).RunAsync(s_context, ["-id=job1", "-timeout=5"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeJobCommand(), api, null, output).RunAsync(s_context, ["-id=job1", "-wait", "-timeout=0"], Token));
	}

	[TestMethod]
	public async Task PreflightFindsTheStreamAndTemplateAndNeverAutoSubmits()
	{
		FakeHordeApi api = WithStreams();
		FakeWorkspace workspace = new();
		using StringWriter output = new();

		int result = await Setup(new HordePreflightCommand(), api, workspace, output).RunAsync(s_context, ["-c=12345"], Token);

		Assert.AreEqual(HordeExitCodes.Success, result);
		HordePreflightRequest created = api.Created.Single();
		Assert.AreEqual(new HordePreflightRequest("project-main", "editor-preflight", 12345, false), created with { Parameters = null });
		Assert.IsEmpty(created.Parameters!, "saved with the template's defaults: no parameters sent");
		Assert.IsEmpty(workspace.Shelved, "no -shelve, no shelve");
		StringAssert.StartsWith(output.ToString(), "Template: Editor Preflight (editor-preflight); parameters: template defaults (saved)" + Environment.NewLine + "Job URL: https://horde.example.com/job/newjob" + Environment.NewLine, "the template line, then the URL, before anything else");
		StringAssert.Contains(output.ToString(), "template editor-preflight (Editor Preflight), job newjob");
		Assert.DoesNotContain("AUTO-SUBMIT", output.ToString());
		Assert.IsTrue(api.Disposed);
	}

	[TestMethod]
	public async Task AutoSubmitIsOptInAndSaidPlainly()
	{
		FakeHordeApi api = WithStreams();
		using StringWriter output = new();

		await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), output).RunAsync(s_context, ["-c=12345", "-autosubmit"], Token);

		Assert.IsTrue(api.Created.Single().AutoSubmit);
		StringAssert.Contains(output.ToString(), "AUTO-SUBMIT IS ON: if this preflight succeeds, Horde will edit change 12345's description and SUBMIT it.");
	}

	static HordeJobSummary RunningPreflight(string id, string created = "2026-01-01T01:00:00Z", bool autoSubmit = false, Dictionary<string, string>? parameters = null)
		=> new(id, "project-main", "editor-preflight", "Running", created) { AutoSubmit = autoSubmit, Parameters = parameters ?? [] };

	[TestMethod]
	public async Task OnlyTheSameRequestMadeAfterTheLastShelveIsReused()
	{
		async Task<(FakeHordeApi Api, string Output, int Exit)> Run(HordeJobSummary? running, FakeWorkspace? workspace = null, params string[] arguments)
		{
			FakeHordeApi api = WithStreams();
			if (running is not null)
			{
				api.Preflights.Add([running]);
			}
			using StringWriter output = new();
			int exit = await Setup(new HordePreflightCommand(), api, workspace ?? new FakeWorkspace(), output).RunAsync(s_context, ["-c=12345", .. arguments], Token);
			return (api, output.ToString(), exit);
		}

		// The same stream, template, parameters and auto-submit, created after the last shelve: reported, not started again.
		(FakeHordeApi same, string text, int exit) = await Run(RunningPreflight("oldjob"));
		Assert.AreEqual(0, exit);
		Assert.IsEmpty(same.Created);
		StringAssert.StartsWith(text, "reused: oldjob (still running");
		StringAssert.Contains(text, "(reused job)" + Environment.NewLine + "Job URL: https://horde.example.com/job/oldjob");
		StringAssert.Contains(text, "job oldjob (reused)");

		// Created before the change was last shelved: it builds an older shelf.
		Assert.HasCount(1, (await Run(RunningPreflight("stale", created: "2025-12-31T23:59:00Z"))).Api.Created);
		// No parameters in the answer, or no shelve time: uak can't tell, so it starts a new one.
		Assert.HasCount(1, (await Run(RunningPreflight("unknown") with { Parameters = null })).Api.Created);
		Assert.HasCount(1, (await Run(RunningPreflight("oldjob"), new FakeWorkspace { ShelveTime = null })).Api.Created);
		// -force always starts one.
		Assert.HasCount(1, (await Run(RunningPreflight("oldjob"), null, "-force")).Api.Created);

		// A running auto-submit preflight is not reused for a request without auto-submit, and the output says it is running.
		(FakeHordeApi submitting, string note, _) = await Run(RunningPreflight("autojob", autoSubmit: true));
		Assert.IsFalse(submitting.Created.Single().AutoSubmit);
		StringAssert.Contains(note, "an AUTO-SUBMIT preflight of change 12345 is running: https://horde.example.com/job/autojob");
		// With -autosubmit it is, and the output says auto-submit is on.
		(FakeHordeApi both, string on, _) = await Run(RunningPreflight("autojob", autoSubmit: true), null, "-autosubmit");
		Assert.IsEmpty(both.Created);
		StringAssert.Contains(on, "reused: autojob");
		StringAssert.Contains(on, "AUTO-SUBMIT IS ON");
	}

	[TestMethod]
	public async Task ReuseComparesEveryParameterValue()
	{
		FakeHordeApi api = WithTemplates();
		// The saved settings in Setup are editor-preflight with its defaults; Horde reports a job's values with "True"/"False".
		Dictionary<string, string> defaults = new() { ["run-tests"] = "True", ["win64"] = "True", ["ps5"] = "False", ["config-dev"] = "True", ["config-test"] = "False", ["extra-args"] = "" };
		api.Preflights.Add([RunningPreflight("samejob", parameters: defaults)]);
		using StringWriter output = new();
		Assert.AreEqual(0, await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), output).RunAsync(s_context, ["-c=12345"], Token));
		Assert.IsEmpty(api.Created);

		FakeHordeApi other = WithTemplates();
		other.Preflights.Add([RunningPreflight("otherjob", parameters: new(defaults) { ["ps5"] = "True" })]);
		Assert.AreEqual(0, await Setup(new HordePreflightCommand(), other, new FakeWorkspace(), output).RunAsync(s_context, ["-c=12345"], Token));
		Assert.HasCount(1, other.Created, "different parameters: a new preflight");

		FakeHordeApi overridden = WithTemplates();
		overridden.Preflights.Add([RunningPreflight("samejob", parameters: defaults)]);
		Assert.AreEqual(0, await Setup(new HordePreflightCommand(), overridden, new FakeWorkspace(), output).RunAsync(s_context, ["-c=12345", "-param:run-tests=false"], Token));
		Assert.HasCount(1, overridden.Created, "-param: changes the request");
	}

	[TestMethod]
	public async Task ShelveNeverReusesAndWontShelveUnderAnAutoSubmitPreflight()
	{
		FakeHordeApi api = WithStreams();
		api.Preflights.Add([RunningPreflight("oldjob")]);
		FakeWorkspace workspace = new();
		using StringWriter output = new();
		Assert.AreEqual(0, await Setup(new HordePreflightCommand(), api, workspace, output).RunAsync(s_context, ["-c=12345", "-shelve"], Token));
		Assert.HasCount(1, api.Created, "a new shelf gets a new preflight");
		CollectionAssert.AreEqual(new[] { 12345 }, workspace.Shelved);

		FakeHordeApi submitting = WithStreams();
		submitting.Preflights.Add([RunningPreflight("autojob", autoSubmit: true)]);
		FakeWorkspace untouched = new();
		UakUsageException refused = await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordePreflightCommand(), submitting, untouched, output).RunAsync(s_context, ["-c=12345", "-shelve"], Token));
		StringAssert.Contains(refused.Message, "auto-submit preflight of change 12345 is running");
		Assert.IsEmpty(untouched.Shelved, "nothing shelved");
		Assert.IsEmpty(submitting.Created);
	}

	[TestMethod]
	public async Task UnderJsonTheReuseNoticeGoesToStandardErrorAndTheJsonSaysWhatWasReported()
	{
		FakeHordeApi api = WithStreams();
		api.Preflights.Add([RunningPreflight("autojob", autoSubmit: true)]);
		using StringWriter output = new();
		using StringWriter errors = new();
		Assert.AreEqual(0, await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), output, errors: errors).RunAsync(s_context, ["-c=12345", "-autosubmit", "-json"], Token));

		StringAssert.StartsWith(errors.ToString(), "reused: autojob");
		StringAssert.Contains(errors.ToString(), "AUTO-SUBMIT IS ON");
		using JsonDocument document = JsonDocument.Parse(output.ToString());
		Assert.IsTrue(document.RootElement.GetProperty("reused").GetBoolean());
		Assert.IsTrue(document.RootElement.GetProperty("autoSubmit").GetBoolean());
		Assert.AreEqual("reused job", document.RootElement.GetProperty("buildSettings").GetString());
	}

	[TestMethod]
	public async Task A401MidWaitSignsInAgainAndKeepsWaitingOnTheSameJob()
	{
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		HordeTokenCache cache = new(Path.Combine(home, "horde"), new FakeProtector());
		string old = Tokens.Jwt(DateTime.UtcNow.AddHours(1), "old");
		Assert.IsTrue(cache.Save(s_server, old));
		FakeHordeApi api = WithStreams(new FakeHordeApi { LoggedIn = true, AccessToken = Tokens.Jwt(DateTime.UtcNow.AddHours(1), "fresh") });
		api.JobAnswers.AddRange([Jobs.Job("Running", "t1"), new HordeAuthException("401", notSignedIn: true), Done("Success").Json]);
		FakeWorkspace workspace = new();
		FakeClock clock = new();
		List<string?> tokens = [];
		using StringWriter output = new();
		HordePreflightCommand command = Setup(new HordePreflightCommand(), api, workspace, output, clock: clock, home: home, cache: cache);
		command.CreateApi = (_, _, token, _) =>
		{
			tokens.Add(token);
			return api;
		};

		Assert.AreEqual(0, await command.RunAsync(s_context, ["-c=12345", "-shelve", "-wait", "-timeout=600"], Token));

		Assert.HasCount(1, api.Created, "the preflight was started once");
		CollectionAssert.AreEqual(new[] { 12345 }, workspace.Shelved, "and shelved once");
		CollectionAssert.AreEqual(new[] { old, null }, tokens, "the saved token, then one fresh sign-in");
		Assert.AreEqual(api.AccessToken, cache.Read(s_server));
		Assert.IsLessThanOrEqualTo(600, (int)clock.Sleeps.Sum(sleep => sleep.TotalSeconds), "the original deadline");
		StringAssert.Contains(output.ToString(), "Result: Success");
	}

	[TestMethod]
	public async Task RepeatedTransientFailuresRebuildTheClient()
	{
		FakeHordeApi api = new();
		api.JobAnswers.AddRange([new HttpRequestException("reset"), new HttpRequestException("reset"), new HttpRequestException("reset"), new HttpRequestException("reset"), Done("Success").Json]);
		int clients = 0;
		using StringWriter output = new();
		HordeJobCommand command = Setup(new HordeJobCommand(), api, null, output);
		command.CreateApi = (_, _, _, _) =>
		{
			clients++;
			return api;
		};

		Assert.AreEqual(0, await command.RunAsync(s_context, ["-id=job1", "-wait"], Token));
		Assert.AreEqual(2, clients, "the first client, then one rebuilt after 4 failures in a row");
	}

	[TestMethod]
	public async Task ATimeoutBeforeAnyAnswerIsStillRunning()
	{
		FakeHordeApi api = new();
		api.JobAnswers.Add(new HttpRequestException("no route"));
		using StringWriter output = new();
		Assert.AreEqual(HordeExitCodes.StillRunning, await Setup(new HordeJobCommand(), api, null, output).RunAsync(s_context, ["-id=job1", "-wait", "-timeout=100"], Token));
		StringAssert.Contains(output.ToString(), "Still unknown: Horde could not be reached before -timeout passed");
	}

	[TestMethod]
	public async Task UnexpectedErrorsAndForbiddenAreFive()
	{
		FakeHordeApi html = new();
		html.JobAnswers.Add(new JsonException("'<' is an invalid start of a value."));
		using StringWriter output = new();
		Assert.AreEqual(HordeExitCodes.Error, await Setup(new HordeJobCommand(), html, null, output).RunAsync(s_context, ["-id=job1"], Token), "an answer that isn't JSON is an error, not a failed job");

		FakeHordeApi forbidden = new();
		forbidden.JobAnswers.Add(new HordeAuthException("Horde refused GET: not allowed (403)."));
		Assert.AreEqual(HordeExitCodes.Error, await Setup(new HordeJobCommand(), forbidden, null, output).RunAsync(s_context, ["-id=job1"], Token), "403 is not a sign-in problem");
	}

	[TestMethod]
	public async Task LoginChecksASavedTokenWithTheServer()
	{
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		HordeTokenCache cache = new(Path.Combine(home, "horde"), new FakeProtector());
		Assert.IsTrue(cache.Save(s_server, Tokens.Jwt(DateTime.UtcNow.AddHours(1), "saved")));
		FakeHordeApi api = new() { LoggedIn = true, AccessToken = Tokens.Jwt(DateTime.UtcNow.AddHours(1), "fresh") };

		using StringWriter accepted = new();
		Assert.AreEqual(0, await Setup(new HordeLoginCommand(), api, null, accepted, home: home, cache: cache).RunAsync(s_context, [], Token));
		StringAssert.Contains(accepted.ToString(), "checked with the server");

		api.TokenAccepted = false;
		using StringWriter refused = new();
		Assert.AreEqual(0, await Setup(new HordeLoginCommand(), api, null, refused, home: home, cache: cache).RunAsync(s_context, [], Token));
		Assert.DoesNotContain("checked with the server", refused.ToString());
		Assert.AreEqual(api.AccessToken, cache.Read(s_server), "the refused token was replaced by the fresh sign-in");
	}

	[TestMethod]
	[DoNotParallelize]
	public async Task AnEnvironmentTokenIsNeitherReplacedNorCached()
	{
		string? url = Environment.GetEnvironmentVariable("UE_HORDE_URL");
		string? token = Environment.GetEnvironmentVariable("UE_HORDE_TOKEN");
		try
		{
			Environment.SetEnvironmentVariable("UE_HORDE_URL", "https://horde.example.com");
			Environment.SetEnvironmentVariable("UE_HORDE_TOKEN", "ci-token");
			Assert.IsTrue(HordeApi.HasEnvironmentToken(s_server));
			Assert.IsFalse(HordeApi.HasEnvironmentToken(new Uri("https://other.example.com/")));

			string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
			HordeTokenCache cache = new(Path.Combine(home, "horde"), new FakeProtector());
			string saved = Tokens.Jwt(DateTime.UtcNow.AddHours(1), "saved");
			Assert.IsTrue(cache.Save(s_server, saved));
			FakeHordeApi api = new() { LoggedIn = true, AccessToken = Tokens.Jwt(DateTime.UtcNow.AddHours(2), "from-environment") };
			api.JobAnswers.Add(Done("Success").Json);
			List<string?> tokens = [];
			using StringWriter output = new();
			HordeJobCommand command = Setup(new HordeJobCommand(), api, null, output, home: home, cache: cache);
			command.CreateApi = (_, _, given, _) =>
			{
				tokens.Add(given);
				return api;
			};

			Assert.AreEqual(0, await command.RunAsync(s_context, ["-id=job1"], Token));
			CollectionAssert.AreEqual(new string?[] { null }, tokens, "uak's saved token is not used over the environment's");
			Assert.AreEqual(saved, cache.Read(s_server), "the environment's token is not saved");
		}
		finally
		{
			Environment.SetEnvironmentVariable("UE_HORDE_URL", url);
			Environment.SetEnvironmentVariable("UE_HORDE_TOKEN", token);
		}
	}


	[TestMethod]
	public async Task ALostCreateAnswerFindsTheJobInsteadOfRetrying()
	{
		FakeHordeApi api = WithStreams();
		api.CreateFailure = new TaskCanceledException("timed out");
		api.Preflights.AddRange([[], [], [new HordeJobSummary("madeanyway", "project-main", "editor-preflight", "Waiting", "x")]]);
		using StringWriter output = new();

		Assert.AreEqual(0, await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), output).RunAsync(s_context, ["-c=12345"], Token));
		Assert.HasCount(1, api.Created, "sent once");
		StringAssert.Contains(output.ToString(), "Job URL: https://horde.example.com/job/madeanyway");

		FakeHordeApi refused = WithStreams();
		refused.CreateFailure = new HordeApiException(400, "Change 12345 has no shelved files");
		Assert.AreEqual(HordeExitCodes.Error, await Setup(new HordePreflightCommand(), refused, new FakeWorkspace(), output).RunAsync(s_context, ["-c=12345"], Token));
	}

	[TestMethod]
	public async Task PreflightShelvesFirstAndWaits()
	{
		FakeHordeApi api = WithStreams();
		api.JobAnswers.Add(Done("Failure").Json);
		FakeWorkspace workspace = new();
		using StringWriter output = new();

		int result = await Setup(new HordePreflightCommand(), api, workspace, output).RunAsync(s_context, ["-c=12345", "-shelve", "-wait", "-stream=PROJECT-MAIN"], Token);

		Assert.AreEqual(HordeExitCodes.Failure, result);
		CollectionAssert.AreEqual(new[] { 12345 }, workspace.Shelved);
		StringAssert.Contains(output.ToString(), "Shelved 1 file(s) in change 12345.");
		StringAssert.Contains(output.ToString(), "Result: Failure");
	}

	[TestMethod]
	public async Task PreflightArgumentsAreChecked()
	{
		FakeHordeApi api = WithStreams();
		using StringWriter output = new();
		async Task Usage(params string[] arguments) => await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordePreflightCommand(), api, new FakeWorkspace(), output).RunAsync(s_context, arguments, Token));

		await Usage();
		await Usage("-c=abc");
		await Usage("-c=12345", "-timeout=60");
		await Usage("-c=12345", "-verbose");
		await Usage("-c=12345", "-stream=nope");
		await Usage("-c=12345", "-template=nope");
		await Usage("-c=12345", "stray");
		Assert.IsEmpty(api.Created);

		// No Perforce workspace and no -stream: say so.
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordePreflightCommand(), api, null, output).RunAsync(s_context, ["-c=12345"], Token));
	}

	[TestMethod]
	public async Task ConfigShowsAndStoresTheServer()
	{
		string path = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"), "config.json");
		using StringWriter shown = new();
		Assert.AreEqual(0, await Setup(new HordeConfigCommand(), new FakeHordeApi(), null, shown, configPath: path).RunAsync(s_context, [], Token));
		StringAssert.Contains(shown.ToString(), "Horde server: https://horde.example.com/ (from test default)");
		StringAssert.Contains(shown.ToString(), "uak horde config -server=");
		StringAssert.Contains(shown.ToString(), "Open the job's page (uak horde config -open=): never");

		using StringWriter stored = new();
		Assert.AreEqual(0, await Setup(new HordeConfigCommand(), new FakeHordeApi(), null, stored, configPath: path).RunAsync(s_context, ["-server=https://other.example.com"], Token));
		Assert.AreEqual(new Uri("https://other.example.com/"), new HordeConfig(path).ReadServer());

		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeConfigCommand(), new FakeHordeApi(), null, stored, configPath: path).RunAsync(s_context, ["-server=nope"], Token));

		HordeConfigCommand none = Setup(new HordeConfigCommand(), new FakeHordeApi(), null, stored, configPath: Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"), "config.json"));
		none.HordeDefault = () => (null, "none");
		using StringWriter missing = new();
		none.Output = missing;
		Assert.AreEqual(UakExitCodes.UsageError, await none.RunAsync(s_context, [], Token));
		StringAssert.Contains(missing.ToString(), "Ask the user");
	}

	[TestMethod]
	public async Task LoginPromptsOnlyWhenNeeded()
	{
		FakeHordeApi api = new() { LoggedIn = true };
		List<bool> prompts = [];
		using StringWriter output = new();
		HordeLoginCommand command = Setup(new HordeLoginCommand(), api, null, output);
		command.CreateApi = (_, prompt, _, _) =>
		{
			prompts.Add(prompt);
			return api;
		};

		Assert.AreEqual(0, await command.RunAsync(s_context, [], Token));
		StringAssert.StartsWith(output.ToString(), "Already logged in");
		CollectionAssert.AreEqual(new[] { false }, prompts);
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => command.RunAsync(s_context, ["-no-login"], Token));

		api.LoggedIn = false;
		Assert.AreEqual(0, await command.RunAsync(s_context, [], Token));
		CollectionAssert.AreEqual(new[] { false, false, true }, prompts);
		Assert.AreEqual(1, api.Logins);

		api.LoginResult = false;
		Assert.AreEqual(HordeExitCodes.NotLoggedIn, await command.RunAsync(s_context, [], Token));

		api.LoginHangs = true;
		Assert.AreEqual(HordeExitCodes.NotLoggedIn, await command.RunAsync(s_context, ["-login-timeout=1"], Token));
	}

	[TestMethod]
	public async Task StreamsMarksTheWorkspaceStream()
	{
		FakeHordeApi api = WithStreams();
		api.Streams.Add(new HordeStream("no-preflights", "//Project/Release", "project", null, [new("nightly", "Nightly", false, true)]));
		using StringWriter output = new();

		Assert.AreEqual(0, await Setup(new HordeStreamsCommand(), api, new FakeWorkspace(), output).RunAsync(s_context, [], Token));
		string text = output.ToString();
		StringAssert.Contains(text, "> project-main  //Project/Main");
		StringAssert.Contains(text, "* editor-preflight  Editor Preflight");
		Assert.DoesNotContain("no-preflights", text);

		using StringWriter all = new();
		await Setup(new HordeStreamsCommand(), api, null, all).RunAsync(s_context, ["-all"], Token);
		StringAssert.Contains(all.ToString(), "nightly  Nightly (no preflights)");
	}

	[TestMethod]
	public async Task UnderJsonTheUrlGoesToStandardErrorAtOnce()
	{
		FakeHordeApi api = WithStreams();
		api.JobAnswers.Add(Done("Success").Json);
		using StringWriter output = new();
		using StringWriter errors = new();

		Assert.AreEqual(0, await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), output, errors: errors).RunAsync(s_context, ["-c=12345", "-wait", "-json"], Token));

		Assert.AreEqual("Template: Editor Preflight (editor-preflight); parameters: template defaults (saved)" + Environment.NewLine + "Job URL: https://horde.example.com/job/newjob", errors.ToString().Trim());
		using JsonDocument document = JsonDocument.Parse(output.ToString());
		Assert.AreEqual("https://horde.example.com/job/job1", document.RootElement.GetProperty("url").GetString());
	}

	[TestMethod]
	[DataRow("never", "Failure", null)]
	[DataRow("created", "Success", "https://horde.example.com/job/job1")]
	[DataRow("finished", "Success", "https://horde.example.com/job/job1")]
	[DataRow("failed", "Success", null)]
	[DataRow("failed", "Failure", "https://horde.example.com/job/job1?step=s1")]
	public async Task TheOpenSettingOpensTheRightPage(string mode, string outcome, string? expected)
	{
		string path = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"), "config.json");
		using StringWriter configured = new();
		Assert.AreEqual(0, await Setup(new HordeConfigCommand(), new FakeHordeApi(), null, configured, configPath: path).RunAsync(s_context, ["-open=" + mode.ToUpperInvariant()], Token));
		Assert.AreEqual(mode, new HordeConfig(path).ReadOpen().ToString().ToLowerInvariant());

		FakeHordeApi api = new();
		api.JobAnswers.Add(Done(outcome).Json);
		List<string> opened = [];
		using StringWriter output = new();
		await Setup(new HordeJobCommand(), api, null, output, configPath: path, opened: opened).RunAsync(s_context, ["-id=job1", "-wait"], Token);
		CollectionAssert.AreEqual(expected is null ? Array.Empty<string>() : new[] { expected }, opened);

		opened.Clear();
		await Setup(new HordeJobCommand(), api, null, output, configPath: path, opened: opened).RunAsync(s_context, ["-id=job1", "-wait", "-no-open"], Token);
		Assert.IsEmpty(opened, "-no-open");

		await Setup(new HordeJobCommand(), api, null, output, configPath: path, opened: opened).RunAsync(s_context, ["-id=job1"], Token);
		Assert.IsEmpty(opened, "only with -wait");
	}

	[TestMethod]
	public async Task AFailedOpenIsOnlyAWarning()
	{
		string path = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"), "config.json");
		new HordeConfig(path).WriteOpen(HordeOpenMode.Finished);
		FakeHordeApi api = new();
		api.JobAnswers.Add(Done("Success").Json);
		using StringWriter output = new();
		HordeJobCommand command = Setup(new HordeJobCommand(), api, null, output, configPath: path);
		command.OpenUrl = _ => "no browser";

		Assert.AreEqual(0, await command.RunAsync(s_context, ["-id=job1", "-wait"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeConfigCommand(), api, null, output, configPath: path).RunAsync(s_context, ["-open=sometimes"], Token));
	}

	[TestMethod]
	public void CommandsAreNamedAndDocumentTheirExitCodes()
	{
		HordeCommandBase[] commands = [new HordeConfigCommand(), new HordeLoginCommand(), new HordeLogoutCommand(), new HordeStreamsCommand(), new HordeTemplatesCommand(), new HordePreflightCommand(), new HordeJobCommand()];
		CollectionAssert.AreEqual(new[] { "horde config", "horde login", "horde logout", "horde streams", "horde templates", "horde preflight", "horde job" }, commands.Select(command => command.Name).ToArray());
		StringAssert.Contains(new HordePreflightCommand().Usage, "6 build settings");
		Assert.IsTrue(commands.All(command => !command.RequiresEngine));
		StringAssert.Contains(new HordeJobCommand().Usage, "3 still running");
		StringAssert.Contains(new HordePreflightCommand().Usage, "SUBMITS");
	}

	static FakeHordeApi WithTemplates(FakeHordeApi? api = null)
	{
		api ??= new();
		api.Streams.AddRange(Templates.Streams());
		return api;
	}

	[TestMethod]
	public async Task WithoutSavedBuildSettingsPreflightStartsNothingAndExitsSix()
	{
		FakeHordeApi api = WithTemplates();
		using StringWriter output = new();
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));

		int result = await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345", "-wait"], Token);

		Assert.AreEqual(HordeExitCodes.BuildSettingsNeeded, result);
		Assert.IsEmpty(api.Created, "nothing is started");
		string text = output.ToString();
		StringAssert.StartsWith(text, "Build settings needed: No build settings are saved for stream project-main. No preflight was started.");
		StringAssert.Contains(text, "uak horde config -stream=project-main -template=<template id> -param:<parameter id>=<value> ...");
		Assert.DoesNotContain("Job URL", text);

		using StringWriter json = new();
		Assert.AreEqual(6, await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), json, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345", "-json"], Token));
		using JsonDocument document = JsonDocument.Parse(json.ToString());
		JsonElement root = document.RootElement;
		Assert.IsTrue(root.GetProperty("buildSettingsNeeded").GetBoolean());
		Assert.AreEqual(JsonValueKind.Null, root.GetProperty("saved").ValueKind);
		JsonElement[] templates = [.. root.GetProperty("templates").EnumerateArray()];
		CollectionAssert.AreEqual(new[] { "editor-preflight", "full-build" }, templates.Select(template => template.GetProperty("id").GetString()).ToArray(), "preflight templates you can run, the default first");
		Assert.IsTrue(templates[0].GetProperty("default").GetBoolean());
		JsonElement[] parameters = [.. templates[0].GetProperty("parameters").EnumerateArray()];
		CollectionAssert.AreEqual(new[] { "run-tests", "target-platforms", "configuration", "extra-args" }, parameters.Select(parameter => parameter.GetProperty("id").GetString()).ToArray());
		Assert.AreEqual("bool", parameters[0].GetProperty("type").GetString());
		Assert.IsTrue(parameters[0].GetProperty("default").GetBoolean());
		Assert.AreEqual("Runs the automation tests.", parameters[0].GetProperty("description").GetString());
		Assert.IsTrue(parameters[1].GetProperty("multiSelect").GetBoolean());
		Assert.AreEqual("win64", parameters[1].GetProperty("default")[0].GetString());
		Assert.AreEqual("Consoles", parameters[1].GetProperty("choices")[1].GetProperty("group").GetString());
		Assert.IsFalse(parameters[2].GetProperty("multiSelect").GetBoolean());
		Assert.AreEqual("text", parameters[3].GetProperty("type").GetString());
	}

	[TestMethod]
	public async Task SavedBuildSettingsAreCheckedSavedAndSent()
	{
		FakeHordeApi api = WithTemplates();
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		using StringWriter output = new();

		// A wrong value saves nothing.
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeConfigCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-template=editor-preflight", "-param:target-platforms=switch"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeConfigCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-template=nightly"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeConfigCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-template=locked"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeConfigCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-param:run-tests=false"], Token), "no saved template, no -template");
		Assert.IsNull(new HordeBuildSettingsStore(Path.Combine(home, "horde")).Read(s_server, "project-main"));

		// The workspace's stream, the template, and values by id or by text.
		Assert.AreEqual(0, await Setup(new HordeConfigCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-template=editor-preflight", "-param:run-tests=off", "-param:TARGET-PLATFORMS=Win64,ps5", "-param:configuration=Test"], Token));
		StringAssert.Contains(output.ToString(), "Saved build settings for stream project-main: Template: Editor Preflight (editor-preflight); non-default parameters: run-tests=false, target-platforms=win64,ps5, configuration=config-test");
		string file = Path.Combine(home, "horde", "horde.example.com", "project-main", "templates.json");
		JsonDocument saved = JsonDocument.Parse(File.ReadAllText(file));
		JsonElement values = saved.RootElement.GetProperty("templates").GetProperty("editor-preflight").GetProperty("parameters");
		Assert.AreEqual(JsonValueKind.False, values.GetProperty("run-tests").ValueKind, "bools are JSON bools");
		Assert.AreEqual(2, values.GetProperty("target-platforms").GetArrayLength(), "lists are JSON arrays");

		// A later -param: merges over the saved values.
		Assert.AreEqual(0, await Setup(new HordeConfigCommand(), api, null, output, home: home, savedBuild: false).RunAsync(s_context, ["-stream=project-main", "-param:extra-args=-fast"], Token));
		HordeSavedBuild build = new HordeBuildSettingsStore(Path.Combine(home, "horde")).Read(s_server, "project-main")!;
		Assert.AreEqual("editor-preflight", build.Template);
		Assert.HasCount(4, build.ParametersFor("editor-preflight"));

		// The preflight sends them by Horde's ids: a list as each choice on or off.
		using StringWriter run = new();
		Assert.AreEqual(0, await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), run, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345"], Token));
		HordePreflightRequest request = api.Created.Single();
		Assert.AreEqual("editor-preflight", request.TemplateId);
		CollectionAssert.AreEquivalent(
			new Dictionary<string, string> { ["run-tests"] = "false", ["win64"] = "true", ["ps5"] = "true", ["config-dev"] = "false", ["config-test"] = "true", ["extra-args"] = "-fast" },
			request.Parameters!.ToDictionary());
		StringAssert.StartsWith(run.ToString(), "Template: Editor Preflight (editor-preflight); non-default parameters: run-tests=false, target-platforms=win64,ps5, configuration=config-test, extra-args=\"-fast\" (saved)" + Environment.NewLine + "Job URL: ");
		using JsonDocument body = JsonDocument.Parse(HordeApi.SerializeCreateRequest(request));
		Assert.AreEqual("true", body.RootElement.GetProperty("parameters").GetProperty("ps5").GetString(), "EpicGames.Horde's CreateJobRequest carries them");

		// -template= and -param: change one run only; -use-template-defaults ignores the saved values.
		api.Created.Clear();
		await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), run, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345", "-force", "-param:run-tests=true"], Token);
		Assert.AreEqual("true", api.Created.Single().Parameters!["run-tests"]);
		Assert.AreEqual("false", new HordeBuildSettingsStore(Path.Combine(home, "horde")).Read(s_server, "project-main")!.ParametersFor("editor-preflight")["run-tests"]);
		api.Created.Clear();
		await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), run, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345", "-force", "-template=full-build"], Token);
		Assert.AreEqual("full-build", api.Created.Single().TemplateId);
		api.Created.Clear();
		await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), run, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345", "-force", "-use-template-defaults"], Token);
		Assert.IsNull(api.Created.Single().Parameters is { Count: > 0 } ? api.Created.Single().Parameters : null, "template defaults: no parameters sent");
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordePreflightCommand(), api, new FakeWorkspace(), run, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345", "-use-template-defaults", "-param:run-tests=true"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordePreflightCommand(), api, new FakeWorkspace(), run, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345", "-param:nope=1"], Token));
	}

	[TestMethod]
	public async Task StaleSavedSettingsAskAgain()
	{
		FakeHordeApi api = WithTemplates();
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		HordeBuildSettingsStore store = new(Path.Combine(home, "horde"));
		store.Save(s_server, "project-main", new HordeTemplate("retired", "Retired", true, true), new Dictionary<string, string>(), DateTime.UtcNow);
		using StringWriter output = new();

		Assert.AreEqual(6, await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345"], Token));
		StringAssert.Contains(output.ToString(), "The saved template 'retired' of stream project-main is no longer offered.");

		store.Save(s_server, "project-main", Templates.Editor(), new Dictionary<string, string> { ["removed-option"] = "true" }, DateTime.UtcNow);
		using StringWriter stale = new();
		Assert.AreEqual(6, await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), stale, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345"], Token));
		StringAssert.Contains(stale.ToString(), "no longer fit it");
		Assert.IsEmpty(api.Created);
	}

	[TestMethod]
	public async Task ConfigShowsAndResetsBuildSettings()
	{
		FakeHordeApi api = WithTemplates();
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		using StringWriter output = new();
		await Setup(new HordeConfigCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-template=editor-preflight", "-param:run-tests=false"], Token);

		using StringWriter shown = new();
		Assert.AreEqual(0, await Setup(new HordeConfigCommand(), api, null, shown, home: home, savedBuild: false).RunAsync(s_context, ["-show"], Token));
		StringAssert.Contains(shown.ToString(), "project-main: template editor-preflight; parameters: run-tests=false");

		using StringWriter json = new();
		await Setup(new HordeConfigCommand(), api, null, json, home: home, savedBuild: false).RunAsync(s_context, ["-json"], Token);
		using (JsonDocument document = JsonDocument.Parse(json.ToString()))
		{
			Assert.AreEqual("editor-preflight", document.RootElement.GetProperty("buildSettings")[0].GetProperty("template").GetString());
		}

		using StringWriter reset = new();
		Assert.AreEqual(0, await Setup(new HordeConfigCommand(), new FakeHordeApi(), null, reset, home: home, savedBuild: false).RunAsync(s_context, ["-stream=project-main", "-reset-build"], Token));
		StringAssert.Contains(reset.ToString(), "Forgot the build settings of stream project-main");
		Assert.AreEqual(6, await Setup(new HordePreflightCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-c=12345"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => Setup(new HordeConfigCommand(), api, null, output, home: home).RunAsync(s_context, ["-reset-build", "-template=x"], Token));
	}

	[TestMethod]
	public async Task TemplatesListsParametersAndMarksTheSavedValues()
	{
		FakeHordeApi api = WithTemplates();
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		using StringWriter output = new();
		await Setup(new HordeConfigCommand(), api, new FakeWorkspace(), output, home: home, savedBuild: false).RunAsync(s_context, ["-template=editor-preflight", "-param:target-platforms=ps5"], Token);

		using StringWriter text = new();
		Assert.AreEqual(0, await Setup(new HordeTemplatesCommand(), api, new FakeWorkspace(), text, home: home, savedBuild: false).RunAsync(s_context, [], Token));
		StringAssert.Contains(text.ToString(), "* editor-preflight  Editor Preflight  (stream default)  (saved)");
		StringAssert.Contains(text.ToString(), "target-platforms  list (several)  Target platforms  default: win64, current: ps5");
		StringAssert.Contains(text.ToString(), "choices: win64 (Win64), ps5 (PS5)");
		Assert.DoesNotContain("nightly", text.ToString());

		using StringWriter json = new();
		Assert.AreEqual(0, await Setup(new HordeTemplatesCommand(), api, null, json, home: home, savedBuild: false).RunAsync(s_context, ["-stream=project-main", "-json"], Token));
		using JsonDocument document = JsonDocument.Parse(json.ToString());
		Assert.AreEqual("editor-preflight", document.RootElement.GetProperty("saved").GetProperty("template").GetString());
		Assert.AreEqual("ps5", document.RootElement.GetProperty("templates")[0].GetProperty("parameters")[1].GetProperty("current")[0].GetString());
	}

	[TestMethod]
	public async Task ACachedTokenSkipsTheSignInAndANewOneIsSaved()
	{
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		HordeTokenCache cache = new(Path.Combine(home, "horde"), new FakeProtector());
		FakeHordeApi api = new() { LoggedIn = false, AccessToken = Tokens.Jwt(DateTime.UtcNow.AddHours(1), "first") };
		api.JobAnswers.Add(Done("Success").Json);
		List<(bool Prompt, string? Token)> created = [];
		using StringWriter output = new();
		HordeJobCommand Command()
		{
			HordeJobCommand command = Setup(new HordeJobCommand(), api, null, output, home: home, cache: cache);
			command.CreateApi = (_, prompt, token, _) =>
			{
				created.Add((prompt, token));
				return api;
			};
			return command;
		}

		Assert.AreEqual(0, await Command().RunAsync(s_context, ["-id=job1"], Token));
		CollectionAssert.AreEqual(new[] { (false, (string?)null), (true, (string?)null) }, created, "first run: silent, then the sign-in page");
		Assert.AreEqual(api.AccessToken, cache.Read(s_server), "the new token is saved");

		created.Clear();
		Assert.AreEqual(0, await Command().RunAsync(s_context, ["-id=job1"], Token));
		CollectionAssert.AreEqual(new[] { (false, api.AccessToken) }, created, "second run: the saved token, no sign-in page");
		Assert.AreEqual(1, api.Logins);
		Assert.DoesNotContain(api.AccessToken!, output.ToString(), "the token is never printed");

		using StringWriter loggedOut = new();
		Assert.AreEqual(0, await Setup(new HordeLogoutCommand(), api, null, loggedOut, home: home, cache: cache).RunAsync(s_context, [], Token));
		StringAssert.StartsWith(loggedOut.ToString(), "Deleted uak's saved Horde sign-in for https://horde.example.com/.");
		Assert.IsNull(cache.Read(s_server));
		Assert.IsFalse(File.Exists(cache.PathFor(s_server)));
	}

	[TestMethod]
	public async Task ARefusedCachedTokenIsDeletedAndTheRunRetriedOnce()
	{
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		HordeTokenCache cache = new(Path.Combine(home, "horde"), new FakeProtector());
		string revoked = Tokens.Jwt(DateTime.UtcNow.AddHours(1), "revoked");
		Assert.IsTrue(cache.Save(s_server, revoked));
		FakeHordeApi api = new() { LoggedIn = true, AccessToken = Tokens.Jwt(DateTime.UtcNow.AddHours(1), "fresh") };
		api.JobAnswers.AddRange([new HordeAuthException("401", notSignedIn: true), Done("Success").Json]);
		List<string?> tokens = [];
		using StringWriter output = new();
		HordeJobCommand command = Setup(new HordeJobCommand(), api, null, output, home: home, cache: cache);
		command.CreateApi = (_, _, token, _) =>
		{
			tokens.Add(token);
			return api;
		};

		Assert.AreEqual(0, await command.RunAsync(s_context, ["-id=job1"], Token));
		CollectionAssert.AreEqual(new[] { revoked, null }, tokens, "the saved token, then a fresh sign-in");
		Assert.AreEqual(api.AccessToken, cache.Read(s_server), "the refused token is replaced");

		// A second refusal is not retried again.
		FakeHordeApi refusing = new() { LoggedIn = true };
		refusing.JobAnswers.Add(new HordeAuthException("401", notSignedIn: true));
		Assert.IsTrue(cache.Save(s_server, revoked));
		Assert.AreEqual(HordeExitCodes.NotLoggedIn, await Setup(new HordeJobCommand(), refusing, null, output, home: home, cache: cache).RunAsync(s_context, ["-id=job1"], Token));
		Assert.IsNull(cache.Read(s_server), "the refused token is gone");
	}
}
