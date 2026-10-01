// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Net.Http;
using AgentKit.Core;
using AgentKit.Vcs;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Horde.Tests;

/// <summary>
/// <see cref="HordeAutoSubmitShelveGuard"/>: what <c>uak vcs shelve -c=</c> asks Horde before it changes a shelf, over a fake
/// Horde.
/// </summary>
[TestClass]
public sealed class HordeShelveGuardTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	static readonly UakContext s_context = new() { StateDirectory = new DirectoryInfo(Path.GetTempPath()), Logger = NullLogger.Instance };

	static readonly Uri s_server = new("https://horde.example.com/");

	/// <summary>A guard whose connector reaches <paramref name="api"/>, recording whether a sign-in page could open.</summary>
	static HordeAutoSubmitShelveGuard Guard(FakeHordeApi api, List<bool> prompts, Uri? server = null)
	{
		string home = Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"));
		HordeConnector connector = new()
		{
			Config = () => new HordeConfig(Path.Combine(home, "config.json")),
			HordeDefault = () => (server, "test default"),
			TokenCache = () => new HordeTokenCache(Path.Combine(home, "horde"), null),
			CreateApi = (_, prompt, _, _) =>
			{
				prompts.Add(prompt);
				return api;
			},
		};
		return new HordeAutoSubmitShelveGuard { Connector = connector };
	}

	[TestMethod]
	public async Task ARunningAutoSubmitPreflightRefusesTheShelve()
	{
		FakeHordeApi api = new();
		api.Preflights.Add([
			new HordeJobSummary("autojob", "project-main", "editor-preflight", "Running", "2026-01-01T01:00:00Z") { AutoSubmit = true },
			new HordeJobSummary("plainjob", "project-main", "editor-preflight", "Running", "2026-01-01T01:00:00Z"),
		]);
		List<bool> prompts = [];

		PerforceShelveGuardResult result = await Guard(api, prompts, s_server).CheckAsync(s_context, 12345, Token);

		Assert.AreEqual(PerforceShelveGuardVerdict.Refuse, result.Verdict);
		StringAssert.Contains(result.Message, "https://horde.example.com/job/autojob");
		StringAssert.Contains(result.Message, "Horde submits the change's CURRENT shelf");
		StringAssert.Contains(result.Message, "Nothing was shelved.");
		Assert.DoesNotContain("plainjob", result.Message!);
		Assert.IsFalse(prompts.Any(prompt => prompt), "the guard never opens a sign-in page");
	}

	[TestMethod]
	public async Task NoAutoSubmitPreflightOrNoServerIsClear()
	{
		FakeHordeApi api = new();
		api.Preflights.Add([
			new HordeJobSummary("plainjob", "project-main", "editor-preflight", "Running", "2026-01-01T01:00:00Z"),
			new HordeJobSummary("donejob", "project-main", "editor-preflight", "Complete", "2026-01-01T01:00:00Z") { AutoSubmit = true },
		]);
		List<bool> prompts = [];
		Assert.AreEqual(PerforceShelveGuardVerdict.Clear, (await Guard(api, prompts, s_server).CheckAsync(s_context, 12345, Token)).Verdict, "a finished auto-submit job submits nothing more");

		List<bool> none = [];
		Assert.AreEqual(PerforceShelveGuardVerdict.Clear, (await Guard(new FakeHordeApi(), none, server: null).CheckAsync(s_context, 12345, Token)).Verdict, "no Horde server: nothing to check");
		Assert.IsEmpty(none, "and Horde isn't contacted");
	}

	[TestMethod]
	public async Task WhenHordeCantBeAskedTheShelveGoesAheadWithAWarning()
	{
		List<bool> prompts = [];
		PerforceShelveGuardResult notSignedIn = await Guard(new FakeHordeApi { LoggedIn = false }, prompts, s_server).CheckAsync(s_context, 12345, Token);
		Assert.AreEqual(PerforceShelveGuardVerdict.Unknown, notSignedIn.Verdict);
		StringAssert.Contains(notSignedIn.Message, "not signed in to Horde at https://horde.example.com/");
		StringAssert.Contains(notSignedIn.Message, "Shelving anyway");
		CollectionAssert.AreEqual(new[] { false }, prompts, "never the sign-in page");

		FakeHordeApi unreachable = new() { PreflightsFailure = new HttpRequestException("no route to host") };
		PerforceShelveGuardResult down = await Guard(unreachable, [], s_server).CheckAsync(s_context, 12345, Token);
		Assert.AreEqual(PerforceShelveGuardVerdict.Unknown, down.Verdict);
		StringAssert.Contains(down.Message, "no route to host");
	}

	[TestMethod]
	public void TheKitFindsTheGuard()
	{
		// As the host finds it: every IPerforceShelveGuard in the kit's assemblies, which uak vcs shelve asks.
		UakCommandCatalog catalog = UakCommandCatalog.FromAssemblies([typeof(VcsShelveCommand).Assembly, typeof(HordeAutoSubmitShelveGuard).Assembly]);
		Assert.IsInstanceOfType<HordeAutoSubmitShelveGuard>(catalog.CreateAll<IPerforceShelveGuard>().Single());
		Assert.IsFalse(catalog.Commands.Any(command => command.Name == "horde _connect"), "the connector is not a command the host would list");
		Assert.IsEmpty(catalog.Problems);
	}
}
