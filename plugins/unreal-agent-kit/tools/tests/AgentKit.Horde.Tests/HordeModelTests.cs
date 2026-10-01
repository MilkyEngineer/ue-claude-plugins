// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Core;

namespace AgentKit.Horde.Tests;

/// <summary>The config file, server resolution, job results, stream and template choice, and the create request.</summary>
[TestClass]
public sealed class HordeModelTests
{
	sealed class TempFolder : IDisposable
	{
		public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N")[..12])).FullName;

		public string File(string name) => System.IO.Path.Combine(Path, name);

		public void Dispose()
		{
			try
			{
				Directory.Delete(Path, true);
			}
			catch (IOException)
			{
			}
		}
	}

	[TestMethod]
	public void ConfigStoresTheServerAndKeepsOtherSettings()
	{
		using TempFolder folder = new();
		HordeConfig config = new(folder.File("config.json"));
		Assert.IsNull(config.ReadServer(), "no file yet");

		File.WriteAllText(config.Path, "{ \"other\": { \"keep\": 1 }, // a comment\n }");
		config.WriteServer(new Uri("https://horde.example.com/"));

		Assert.AreEqual(new Uri("https://horde.example.com/"), config.ReadServer());
		JsonObject root = JsonNode.Parse(File.ReadAllText(config.Path))!.AsObject();
		Assert.AreEqual(1, root["other"]!["keep"]!.GetValue<int>());
		Assert.IsFalse(File.ReadAllBytes(config.Path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "no BOM");
	}

	[TestMethod]
	public void ABrokenConfigIsAUsageError()
	{
		using TempFolder folder = new();
		HordeConfig config = new(folder.File("config.json"));
		File.WriteAllText(config.Path, "not json");
		StringAssert.Contains(Assert.ThrowsExactly<UakUsageException>(() => config.ReadServer()).Message, "Fix or delete it");

		File.WriteAllText(config.Path, "{ \"horde\": { \"server\": \"ftp://nope\" } }");
		Assert.ThrowsExactly<UakUsageException>(() => config.ReadServer());
	}

	[TestMethod]
	public void ConfigLivesInTheKitFolder()
	{
		using TempFolder folder = new();
		HordeConfig config = HordeConfig.ForUser(new UakResolveOptions { GetEnvironmentVariable = name => name == "UAK_HOME" ? folder.Path : null });
		Assert.AreEqual(folder.File("config.json"), config.Path);
	}

	[TestMethod]
	[DataRow("https://horde.example.com", "https://horde.example.com/")]
	[DataRow("http://horde.example.com:13340/", "http://horde.example.com:13340/")]
	[DataRow(" https://horde.example.com/sub ", "https://horde.example.com/sub/")]
	public void ServersAreNormalised(string text, string expected)
	{
		Assert.IsTrue(HordeConfig.TryParseServer(text, out Uri? server, out _));
		Assert.AreEqual(expected, server!.ToString());
	}

	[TestMethod]
	[DataRow("horde.example.com")]
	[DataRow("ftp://horde.example.com/")]
	[DataRow("https://horde.example.com/?x=1")]
	[DataRow("https://user@horde.example.com/")]
	public void BadServersAreRefused(string text)
	{
		Assert.IsFalse(HordeConfig.TryParseServer(text, out _, out string? error));
		Assert.IsNotNull(error);
	}

	[TestMethod]
	public void TheServerComesFromTheArgumentThenTheConfigThenHordesDefault()
	{
		using TempFolder folder = new();
		HordeConfig config = new(folder.File("config.json"));
		(Uri?, string) hordeDefault = (new Uri("https://default.example.com"), "registry");

		HordeServer fromDefault = HordeServerResolver.Resolve(null, config, () => hordeDefault);
		Assert.AreEqual("https://default.example.com/", fromDefault.Url.ToString());
		Assert.IsFalse(fromDefault.Stored);

		config.WriteServer(new Uri("https://stored.example.com/"));
		HordeServer stored = HordeServerResolver.Resolve(null, config, () => hordeDefault);
		Assert.AreEqual("https://stored.example.com/", stored.Url.ToString());
		Assert.IsTrue(stored.Stored);

		Assert.AreEqual("https://given.example.com/", HordeServerResolver.Resolve("https://given.example.com", config, () => hordeDefault).Url.ToString());
		Assert.ThrowsExactly<UakUsageException>(() => HordeServerResolver.Resolve("nope", config, () => hordeDefault));
	}

	[TestMethod]
	public void NoServerSaysToAskTheUser()
	{
		using TempFolder folder = new();
		UakUsageException exception = Assert.ThrowsExactly<UakUsageException>(() => HordeServerResolver.Resolve(null, new HordeConfig(folder.File("config.json")), () => (null, "none")));
		Assert.AreEqual(HordeServerResolver.NotConfiguredMessage, exception.Message);
		StringAssert.Contains(exception.Message, "Ask the user");
		StringAssert.Contains(exception.Message, "uak horde config -server=<url>");
	}

	[TestMethod]
	public void AnUnchangedJobIsNull()
	{
		Assert.IsNull(HordeJob.Parse(JsonNode.Parse("{}")));
		Assert.IsNull(HordeJob.Parse(null));
	}

	[TestMethod]
	public void JobsAreRead()
	{
		HordeJob job = Jobs.Parse(Jobs.Job("Running", "2026-10-01T10:00:00Z", Jobs.Batch(1, Jobs.Step("s1", "Compile", 0, "Running", "Unspecified", "log1"))));
		Assert.AreEqual("job1", job.Id);
		Assert.AreEqual(12345, job.PreflightChange);
		Assert.AreEqual("2026-10-01T10:00:00Z", job.UpdateTime);
		Assert.AreEqual(new HordeStep("s1", "Compile", 1, 0, "Running", "Unspecified", "log1"), job.Steps.Single());
		Assert.AreEqual(HordeJobResult.Running, job.Result);
	}

	[TestMethod]
	public void ResultsFollowHordesRules()
	{
		static HordeJobResult Result(string state, params JsonObject[] steps) => Jobs.Parse(Jobs.Job(state, "t", Jobs.Batch(0, steps))).Result;

		Assert.AreEqual(HordeJobResult.Success, Result("Complete", Jobs.Step("a", "A", 0, "Completed", "Success"), Jobs.Step("b", "B", 1, "Completed", "Success")));
		Assert.AreEqual(HordeJobResult.Failure, Result("Complete", Jobs.Step("a", "A", 0, "Completed", "Failure"), Jobs.Step("b", "B", 1, "Completed", "Warnings")));
		Assert.AreEqual(HordeJobResult.Warnings, Result("Complete", Jobs.Step("a", "A", 0, "Completed", "Warnings"), Jobs.Step("b", "B", 1, "Skipped", "Unspecified")));
		Assert.AreEqual(HordeJobResult.Incomplete, Result("Complete", Jobs.Step("a", "A", 0, "Completed", "Success"), Jobs.Step("b", "B", 1, "Aborted", "Unspecified")));
		Assert.AreEqual(HordeJobResult.Running, Result("Running", Jobs.Step("a", "A", 0, "Completed", "Failure"), Jobs.Step("b", "B", 1, "Running", "Unspecified")));
		Assert.AreEqual(HordeJobResult.Running, Result("Waiting"), "no steps yet");
		Assert.AreEqual(HordeJobResult.Incomplete, Result("Complete"), "a finished job that ran nothing");
	}

	[TestMethod]
	public void ARetriedStepCountsOnlyItsLatestRun()
	{
		HordeJob job = Jobs.Parse(Jobs.Job("Complete", "t", Jobs.Batch(0, Jobs.Step("a1", "Cook", 0, "Completed", "Failure")), Jobs.Batch(0, Jobs.Step("a2", "Cook", 0, "Completed", "Success"))));
		Assert.HasCount(1, job.LatestSteps);
		Assert.AreEqual("a2", job.LatestSteps[0].Id);
		Assert.AreEqual(HordeJobResult.Success, job.Result);
	}

	[TestMethod]
	public void BatchErrorsLeaveAJobIncomplete()
	{
		JsonObject batch = Jobs.Batch(2, Jobs.Step("a", "A", 0, "Skipped", "Unspecified"));
		batch["error"] = "UnknownAgentType";
		HordeJob job = Jobs.Parse(Jobs.Job("Complete", "t", batch));
		Assert.AreEqual(new HordeBatchError(2, "UnknownAgentType"), job.BatchErrors.Single());
		Assert.AreEqual(HordeJobResult.Incomplete, job.Result);
	}

	[TestMethod]
	public void StreamsAndSummariesAreRead()
	{
		JsonNode streams = JsonNode.Parse("""
			[{ "id": "project-main", "name": "//Project/Main", "projectId": "project", "defaultPreflight": { "templateId": "editor-preflight" },
			   "templates": [ { "id": "editor-preflight", "name": "Editor Preflight", "allowPreflights": true, "canRun": true },
			                  { "id": "nightly", "name": "Nightly", "allowPreflights": false, "canRun": false } ] },
			 { "id": "project-dev", "name": "//Project/Dev", "defaultPreflightTemplate": "legacy" }]
			""")!;
		IReadOnlyList<HordeStream> parsed = HordeStreamParser.ParseList(streams);
		Assert.AreEqual("editor-preflight", parsed[0].DefaultPreflightTemplate);
		HordeTemplate nightly = parsed[0].Templates[1];
		Assert.AreEqual(("nightly", "Nightly", false, false), (nightly.Id, nightly.Name, nightly.AllowPreflights, nightly.CanRun));
		Assert.IsEmpty(nightly.Parameters);
		Assert.AreEqual("legacy", parsed[1].DefaultPreflightTemplate);

		IReadOnlyList<HordeJobSummary> jobs = HordeJobSummary.ParseList(JsonNode.Parse("""[{ "id": "j1", "streamId": "s", "templateId": "t", "state": "Running", "createTime": "x" }]"""));
		Assert.IsTrue(jobs.Single().IsActive);
	}

	static readonly HordeStream[] s_streams =
	[
		new("project-main", "//Project/Main", "project", "editor-preflight", [new("editor-preflight", "Editor Preflight", true, true), new("nightly", "Nightly", false, true)]),
		new("project-dev-tools", "//Project/Dev-Tools", "project", null, [new("a", "A", true, true), new("b", "B", true, true), new("c", "C", true, false)]),
		new("other", "//Other/Thing-VS", "other", null, [new("only", "Only", true, true)]),
	];

	[TestMethod]
	public void StreamsMatchAsTheDashboardDoes()
	{
		Assert.AreEqual("project-main", HordeSelection.Match(s_streams, "//project/main")!.Id, "by name, ignoring case");
		Assert.AreEqual("project-dev-tools", HordeSelection.Match([s_streams[1] with { Name = "renamed" }], "//Project/Dev/Tools")!.Id, "by id");
		Assert.AreEqual("other", HordeSelection.Match(s_streams, "//Other/Thing")!.Id, "by name plus -VS");
		Assert.IsNull(HordeSelection.Match(s_streams, "//Nope/Main"));
	}

	[TestMethod]
	public void TheChainIsWalkedThroughVirtualStreamsOnly()
	{
		(HordeStream stream, string p4) = HordeSelection.FromChain(s_streams, [new("//Project/main-virtual", "virtual"), new("//Project/Main", "mainline")]);
		Assert.AreEqual("project-main", stream.Id);
		Assert.AreEqual("//Project/Main", p4);

		// A development stream Horde doesn't build: its parent would not see the shelf, so no match.
		UakUsageException exception = Assert.ThrowsExactly<UakUsageException>(() => HordeSelection.FromChain(s_streams, [new("//Project/feature", "development"), new("//Project/Main", "mainline")]));
		StringAssert.Contains(exception.Message, "//Project/feature");
		Assert.DoesNotContain("//Project/Main", exception.Message);
		StringAssert.Contains(exception.Message, "-stream=");
	}

	[TestMethod]
	public void TemplatesAreChosenSafely()
	{
		Assert.AreEqual("editor-preflight", HordeSelection.ChooseTemplate(s_streams[0], null).Id, "the stream's default");
		Assert.AreEqual("editor-preflight", HordeSelection.ChooseTemplate(s_streams[0], "EDITOR PREFLIGHT").Id, "by name");
		StringAssert.Contains(Assert.ThrowsExactly<UakUsageException>(() => HordeSelection.ChooseTemplate(s_streams[0], "nightly")).Message, "does not allow preflights");
		StringAssert.Contains(Assert.ThrowsExactly<UakUsageException>(() => HordeSelection.ChooseTemplate(s_streams[0], "missing")).Message, "editor-preflight");
		StringAssert.Contains(Assert.ThrowsExactly<UakUsageException>(() => HordeSelection.ChooseTemplate(s_streams[1], null)).Message, "several");
		Assert.AreEqual("only", HordeSelection.ChooseTemplate(s_streams[2], null).Id, "the only one");
	}

	[TestMethod]
	public void TheCreateRequestIsAPreflightThatNeverAutoSubmitsByDefault()
	{
		JsonObject body = JsonNode.Parse(HordeApi.SerializeCreateRequest(new HordePreflightRequest("project-main", "editor-preflight", 12345, false)))!.AsObject();
		Assert.AreEqual("project-main", Get(body, "streamId"));
		Assert.AreEqual("editor-preflight", Get(body, "templateId"));
		Assert.AreEqual("12345", Get(body, "preflightCommitId"));
		Assert.AreEqual("false", Get(body, "autoSubmit"));
		Assert.AreEqual("false", Get(body, "updateIssues"));
		Assert.IsNull(Get(body, "commitId"), "the base is the server's choice (latest)");

		JsonObject submit = JsonNode.Parse(HordeApi.SerializeCreateRequest(new HordePreflightRequest("s", "t", 1, true)))!.AsObject();
		Assert.AreEqual("true", Get(submit, "autoSubmit"));
	}

	static string? Get(JsonObject body, string name)
	{
		JsonNode? node = body.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
		return node?.GetValueKind() switch
		{
			null or JsonValueKind.Null => null,
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => node.ToString(),
		};
	}

	[TestMethod]
	public void ErrorBodiesAreShortened()
	{
		Assert.AreEqual("Change 12345 has no shelved files", HordeApi.Brief("{\"message\":\"Change 12345 has no shelved files\",\"time\":\"x\"}"));
		Assert.AreEqual("a b", HordeApi.Brief("a\r\nb"));
		Assert.AreEqual(403, HordeApi.Brief(new string('x', 1000)).Length);
	}
}
