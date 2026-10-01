// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json.Nodes;
using AgentKit.Core;

namespace AgentKit.Horde.Tests;

/// <summary>Template parameters, saved build settings, and the token cache.</summary>
[TestClass]
public sealed class HordeBuildSettingsTests
{
	static readonly Uri s_server = new("https://horde.example.com/");

	static string NewRoot() => Path.Combine(Path.GetTempPath(), "uak-horde-tests", Guid.NewGuid().ToString("N"), "horde");

	[TestMethod]
	public void ParametersAreReadWithIdsForLists()
	{
		HordeTemplate template = Templates.Editor();
		Assert.AreEqual("Compiles the editor and runs tests.", template.Description);
		CollectionAssert.AreEqual(new[] { "run-tests", "target-platforms", "configuration", "extra-args" }, template.Parameters.Select(parameter => parameter.Id).ToArray());
		Assert.IsTrue(template.Parameters[0].BoolDefault);
		Assert.AreEqual(HordeListStyle.Multi, template.Parameters[1].Style);
		Assert.AreEqual(HordeListStyle.Single, template.Parameters[2].Style);
		Assert.AreEqual("^[^;]*$", template.Parameters[3].Validation);

		// Lists named alike, or like another parameter, get unique ids.
		JsonObject json = (JsonObject)JsonNode.Parse("""
			{ "parameters": [
			  { "type": "Bool", "id": "platforms", "label": "x" },
			  { "type": "List", "label": "Platforms!", "items": [ { "id": "a", "text": "A" } ] },
			  { "type": "List", "label": "Platforms", "style": 2, "items": [] },
			  { "type": "List", "label": "???", "items": [] },
			  { "type": "Unknown", "id": "skipped" } ] }
			""")!;
		IReadOnlyList<HordeTemplateParameter> parameters = HordeStreamParser.ParseParameters(json);
		CollectionAssert.AreEqual(new[] { "platforms", "platforms-2", "platforms-3", "list" }, parameters.Select(parameter => parameter.Id).ToArray());
		Assert.AreEqual(HordeListStyle.Tags, parameters[2].Style);
	}

	[TestMethod]
	[DataRow("run-tests", "No", true, "false")]
	[DataRow("run-tests", "1", true, "true")]
	[DataRow("run-tests", "maybe", false, null)]
	[DataRow("target-platforms", "PS5, win64", true, "win64,ps5")]
	[DataRow("target-platforms", "", true, "")]
	[DataRow("target-platforms", "switch", false, null)]
	[DataRow("configuration", "Test", true, "config-test")]
	[DataRow("configuration", "config-dev,config-test", false, null)]
	[DataRow("configuration", "", true, "")]
	[DataRow("extra-args", "-fast -quiet", true, "-fast -quiet")]
	[DataRow("extra-args", "a;b", false, null)]
	public void ValuesAreCheckedAndNormalized(string id, string value, bool valid, string? expected)
	{
		HordeTemplateParameter parameter = Templates.Editor().Parameters.Single(candidate => candidate.Id == id);
		Assert.AreEqual(valid, HordeBuildParameters.TryNormalize(parameter, value, out string normalized, out string? error), error);
		if (valid)
		{
			Assert.AreEqual(expected, normalized);
		}
		else
		{
			Assert.IsNotNull(error);
		}
	}

	[TestMethod]
	public void ValuesBecomeHordeParametersAndADescription()
	{
		HordeTemplate template = Templates.Editor();
		List<string> errors = [];
		Dictionary<string, string> values = HordeBuildParameters.Validate(template, [new("RUN-TESTS", "true"), new("target-platforms", "ps5"), new("nope", "1")], errors);
		Assert.HasCount(1, errors);
		StringAssert.Contains(errors[0], "Template editor-preflight has no parameter 'nope'");

		CollectionAssert.AreEquivalent(new Dictionary<string, string> { ["run-tests"] = "true", ["win64"] = "false", ["ps5"] = "true" }, HordeBuildParameters.ToHorde(template, values));
		Assert.AreEqual("Template: Editor Preflight (editor-preflight); non-default parameters: target-platforms=ps5", HordeBuildParameters.Describe(template, values), "run-tests=true is its default");
		Assert.AreEqual("Template: Editor Preflight (editor-preflight); parameters: template defaults", HordeBuildParameters.Describe(template, new Dictionary<string, string>()));
	}

	[TestMethod]
	public void SavedSettingsRoundTripAndKeepUnknownKeys()
	{
		HordeBuildSettingsStore store = new(NewRoot());
		HordeTemplate editor = Templates.Editor();
		DateTime now = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		Assert.IsNull(store.Read(s_server, "project-main"));

		store.Save(s_server, "project-main", editor, new Dictionary<string, string> { ["run-tests"] = "false", ["target-platforms"] = "win64,ps5", ["extra-args"] = "-x" }, now);
		string path = store.PathFor(s_server, "project-main");
		StringAssert.EndsWith(path, Path.Combine("horde", "horde.example.com", "project-main", "templates.json"));
		JsonObject file = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
		file["note"] = "kept";
		((JsonObject)file["templates"]!["editor-preflight"]!)["mine"] = 1;
		File.WriteAllText(path, file.ToJsonString());

		store.Save(s_server, "project-main", new HordeTemplate("full-build", "Full Build", true, true), new Dictionary<string, string>(), now);
		HordeSavedBuild saved = store.Read(s_server, "project-main")!;
		Assert.AreEqual("full-build", saved.Template);
		Assert.AreEqual("2026-01-02T03:04:05Z", saved.Updated);
		Assert.AreEqual("win64,ps5", saved.ParametersFor("editor-preflight")["target-platforms"], "another template's values are kept");
		Assert.AreEqual("false", saved.ParametersFor("editor-preflight")["run-tests"]);
		JsonObject after = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
		Assert.AreEqual("kept", after["note"]!.GetValue<string>());
		Assert.AreEqual(1, after["templates"]!["editor-preflight"]!["mine"]!.GetValue<int>());
		Assert.HasCount(1, store.ReadAll(s_server));
		Assert.IsEmpty(store.ReadAll(new Uri("https://other.example.com/")));

		// Reset keeps a file with unknown keys, and deletes one without.
		Assert.IsTrue(store.Reset(s_server, "project-main"));
		Assert.IsNull(store.Read(s_server, "project-main")!.Template);
		Assert.AreEqual("kept", JsonNode.Parse(File.ReadAllText(path))!["note"]!.GetValue<string>());
		store.Save(s_server, "other", editor, new Dictionary<string, string>(), now);
		Assert.IsTrue(store.Reset(s_server, "other"));
		Assert.IsFalse(File.Exists(store.PathFor(s_server, "other")));
		Assert.IsFalse(store.Reset(s_server, "other"));
	}

	[TestMethod]
	public void ServerFoldersAndStreamIdsAreSafe()
	{
		Assert.AreEqual("horde.example.com", HordeBuildSettingsStore.ServerFolder(new Uri("https://Horde.Example.com/")));
		Assert.AreEqual("horde.example.com_8080_ci_horde", HordeBuildSettingsStore.ServerFolder(new Uri("http://horde.example.com:8080/ci/horde/")));
		HordeBuildSettingsStore store = new(NewRoot());
		Assert.ThrowsExactly<UakUsageException>(() => store.PathFor(s_server, "../escape"));
		Assert.ThrowsExactly<UakUsageException>(() => store.PathFor(s_server, "a/b"));

		string path = store.PathFor(s_server, "broken");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "{ not json");
		Assert.ThrowsExactly<UakUsageException>(() => store.Read(s_server, "broken"));
	}

	[TestMethod]
	public void TheResolverAsksWhenNothingFits()
	{
		HordeStream stream = Templates.Streams()[0];
		Assert.IsNull(HordeBuildResolver.Resolve(stream, null, null, [], false, out string? needed));
		StringAssert.Contains(needed, "No build settings are saved for stream project-main.");

		HordeBuildChoice defaults = HordeBuildResolver.Resolve(stream, null, null, [], true, out _)!;
		Assert.AreEqual("editor-preflight", defaults.Template.Id);
		Assert.IsEmpty(defaults.Parameters);

		HordeBuildChoice onlyParameters = HordeBuildResolver.Resolve(stream, null, null, [new("run-tests", "false")], false, out _)!;
		Assert.AreEqual("editor-preflight", onlyParameters.Template.Id, "-param: alone uses the stream's default template");
		Assert.AreEqual("command line", onlyParameters.Source);

		Assert.ThrowsExactly<UakUsageException>(() => HordeBuildResolver.Resolve(stream, null, "nightly", [], false, out _));
		Assert.ThrowsExactly<UakUsageException>(() => HordeBuildResolver.Resolve(stream, null, null, [new("run-tests", "maybe")], false, out _));
	}

	[TestMethod]
	public void TheTokenCacheRoundTripsAndIgnoresWhatItCantUse()
	{
		string root = NewRoot();
		HordeTokenCache cache = new(root, new FakeProtector());
		string token = Tokens.Jwt(DateTime.UtcNow.AddHours(1));
		Assert.IsNull(cache.Read(s_server));
		Assert.IsTrue(cache.Save(s_server, token));
		Assert.AreEqual(token, cache.Read(s_server));
		StringAssert.EndsWith(cache.PathFor(s_server), Path.Combine("horde", "horde.example.com", "token.bin"));
		StringAssert.StartsWith(File.ReadAllText(cache.PathFor(s_server)), "FAKE", "written through the protector");

		// Another server's entropy can't read it.
		string other = cache.PathFor(new Uri("https://other.example.com/"));
		Directory.CreateDirectory(Path.GetDirectoryName(other)!);
		File.Copy(cache.PathFor(s_server), other);
		Assert.IsNull(cache.Read(new Uri("https://other.example.com/")));

		// Too close to its expiry (5 minutes or less left), or expired: not used.
		cache.UtcNow = () => DateTime.UtcNow.AddMinutes(56);
		Assert.IsNull(cache.Read(s_server));
		cache.UtcNow = () => DateTime.UtcNow;

		// A corrupt file is ignored, then replaced by the next save.
		File.WriteAllBytes(cache.PathFor(s_server), [1, 2, 3]);
		Assert.IsNull(cache.Read(s_server));
		Assert.IsTrue(cache.Save(s_server, token));
		Assert.AreEqual(token, cache.Read(s_server));

		// No expiry, no cache; a disabled cache saves nothing.
		Assert.IsFalse(cache.Save(s_server, "opaque-token"));
		Assert.IsFalse(cache.Save(s_server, Tokens.Jwt(DateTime.UtcNow.AddMinutes(-1))));
		Assert.IsNull(HordeTokenCache.GetExpiry("a.b"));
		HordeTokenCache disabled = new(root, null);
		Assert.IsFalse(disabled.Enabled);
		Assert.IsFalse(disabled.Save(s_server, token));
		Assert.IsNull(disabled.Read(s_server));

		Assert.IsTrue(cache.Forget(s_server));
		Assert.IsFalse(cache.Forget(s_server));
	}

	[TestMethod]
	public void DpapiRoundTripsForThisUser()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("DPAPI is Windows only.");
		}
		DpapiTokenProtector protector = new();
		byte[] secret = "a token"u8.ToArray();
		byte[] entropy = "uak-horde-token\nhttps://horde.example.com/"u8.ToArray();
		byte[] sealedBytes = protector.Protect(secret, entropy);
		CollectionAssert.AreNotEqual(secret, sealedBytes);
		CollectionAssert.AreEqual(secret, protector.Unprotect(sealedBytes, entropy));
		Assert.ThrowsExactly<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(sealedBytes, "other"u8.ToArray()));

		HordeTokenCache cache = HordeTokenCache.ForUser(new UakResolveOptions());
		Assert.IsTrue(cache.Enabled);
	}
}
