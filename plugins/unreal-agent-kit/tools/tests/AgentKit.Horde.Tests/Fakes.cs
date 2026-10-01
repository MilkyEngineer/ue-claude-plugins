// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentKit.Vcs;

namespace AgentKit.Horde.Tests;

/// <summary>Builds job responses in the shape <c>GET api/v1/jobs/{id}</c> returns.</summary>
internal static class Jobs
{
	public static JsonObject Step(string id, string name, int node, string state, string outcome, string? logId = null)
	{
		JsonObject step = new() { ["id"] = id, ["name"] = name, ["nodeIdx"] = node, ["state"] = state, ["outcome"] = outcome };
		if (logId is not null)
		{
			step["logId"] = logId;
		}
		return step;
	}

	public static JsonObject Batch(int group, params JsonObject[] steps) => new() { ["groupIdx"] = group, ["error"] = "None", ["steps"] = new JsonArray(steps) };

	public static JsonObject Job(string state, string updateTime, params JsonObject[] batches) => new()
	{
		["id"] = "job1",
		["name"] = "Preflight 12345",
		["streamId"] = "project-main",
		["templateId"] = "editor-preflight",
		["state"] = state,
		["updateTime"] = updateTime,
		["preflightChange"] = 12345,
		["autoSubmit"] = false,
		["batches"] = new JsonArray(batches),
	};

	public static HordeJob Parse(JsonObject job) => HordeJob.Parse(JsonNode.Parse(job.ToJsonString()))!;
}

/// <summary>A scripted Horde: answers from queues and records what it was asked.</summary>
internal sealed class FakeHordeApi : IHordeApi
{
	public Uri ServerUrl { get; init; } = new("https://horde.example.com/");

	public bool LoggedIn { get; set; } = true;

	public bool LoginResult { get; set; } = true;

	/// <summary>Whether the sign-in waits until it is cancelled, as when nobody signs in.</summary>
	public bool LoginHangs { get; set; }

	public List<HordeStream> Streams { get; } = [];

	/// <summary>Successive answers to FindPreflightsAsync; the last repeats.</summary>
	public List<List<HordeJobSummary>> Preflights { get; } = [];

	/// <summary>Successive answers (or exceptions) to GetJobAsync; the last repeats.</summary>
	public List<object?> JobAnswers { get; } = [];

	public Exception? CreateFailure { get; set; }

	public List<HordePreflightRequest> Created { get; } = [];

	public List<string?> ModifiedAfter { get; } = [];

	public int Logins { get; private set; }

	/// <summary>What GetAccessTokenAsync returns.</summary>
	public string? AccessToken { get; set; }

	/// <summary>What CheckSignInAsync returns: whether the server accepts the credentials.</summary>
	public bool TokenAccepted { get; set; } = true;

	public bool Disposed { get; private set; }

	int _preflightCalls;
	int _jobCalls;

	public Task<bool> IsLoggedInAsync(CancellationToken cancellationToken) => Task.FromResult(LoggedIn);

	public async Task<bool> LoginAsync(CancellationToken cancellationToken)
	{
		Logins++;
		if (LoginHangs)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}
		return LoginResult;
	}

	public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(AccessToken);

	public Task<bool> CheckSignInAsync(CancellationToken cancellationToken) => Task.FromResult(TokenAccepted);

	public Task<IReadOnlyList<HordeStream>> GetStreamsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HordeStream>>(Streams);

	public Task<IReadOnlyList<HordeJobSummary>> FindPreflightsAsync(int change, CancellationToken cancellationToken)
	{
		IReadOnlyList<HordeJobSummary> answer = Preflights.Count == 0 ? [] : Preflights[Math.Min(_preflightCalls, Preflights.Count - 1)];
		_preflightCalls++;
		return Task.FromResult(answer);
	}

	public Task<string> CreatePreflightAsync(HordePreflightRequest request, CancellationToken cancellationToken)
	{
		Created.Add(request);
		return CreateFailure is null ? Task.FromResult("newjob") : Task.FromException<string>(CreateFailure);
	}

	public Task<HordeJob?> GetJobAsync(string jobId, string? modifiedAfter, CancellationToken cancellationToken)
	{
		ModifiedAfter.Add(modifiedAfter);
		object? answer = JobAnswers[Math.Min(_jobCalls, JobAnswers.Count - 1)];
		_jobCalls++;
		return answer switch
		{
			Exception exception => Task.FromException<HordeJob?>(exception),
			JsonObject json => Task.FromResult<HordeJob?>(Jobs.Parse(json)),
			HordeJob job => Task.FromResult<HordeJob?>(job),
			_ => Task.FromResult<HordeJob?>(null),
		};
	}

	public ValueTask DisposeAsync()
	{
		Disposed = true;
		return ValueTask.CompletedTask;
	}
}

/// <summary>A Perforce workspace for preflights, without p4.</summary>
internal sealed class FakeWorkspace : IPreflightWorkspace
{
	public List<PerforceStreamLink> Chain { get; } = [new("//Project/main-virtual", "virtual"), new("//Project/Main", "mainline")];

	public List<int> Shelved { get; } = [];

	/// <summary>When the change was last shelved; jobs in the tests are created an hour later.</summary>
	public DateTimeOffset? ShelveTime { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public Task<IReadOnlyList<PerforceStreamLink>> GetStreamChainAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PerforceStreamLink>>(Chain);

	public Task<PerforceShelveResult> ShelveAsync(int change, CancellationToken cancellationToken)
	{
		Shelved.Add(change);
		return Task.FromResult(new PerforceShelveResult([new PerforceShelvedFile("//Project/Main/Docs/Notes.md", "edit")], [], []));
	}

	public Task<DateTimeOffset?> GetShelveTimeAsync(int change, CancellationToken cancellationToken) => Task.FromResult(ShelveTime);

	public void Dispose()
	{
	}
}

/// <summary>A clock that moves only when the waiter "sleeps", recording each sleep.</summary>
internal sealed class FakeClock
{
	public DateTime Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

	public List<TimeSpan> Sleeps { get; } = [];

	public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
	{
		Sleeps.Add(delay);
		Now += delay;
		return Task.CompletedTask;
	}

	public HordeJobWaiter Waiter() => new() { Delay = Delay, UtcNow = () => Now };
}

/// <summary>A stand-in for DPAPI: readable, but bound to its entropy like the real one.</summary>
internal sealed class FakeProtector : IHordeTokenProtector
{
	static readonly byte[] s_magic = "FAKE"u8.ToArray();

	public byte[] Protect(byte[] data, byte[] entropy) => [.. s_magic, .. BitConverter.GetBytes(entropy.Length), .. entropy, .. data];

	public byte[] Unprotect(byte[] data, byte[] entropy)
	{
		if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual(s_magic))
		{
			throw new CryptographicException("not protected");
		}
		int length = BitConverter.ToInt32(data, 4);
		if (length != entropy.Length || !data.AsSpan(8, length).SequenceEqual(entropy))
		{
			throw new CryptographicException("wrong entropy");
		}
		return data[(8 + length)..];
	}
}

/// <summary>Builds unsigned JWTs with an expiry, as the token cache reads them.</summary>
internal static class Tokens
{
	public static string Jwt(DateTime expires, string subject = "user")
	{
		static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		return Part("{\"alg\":\"none\"}") + "." + Part($"{{\"sub\":\"{subject}\",\"exp\":{new DateTimeOffset(expires).ToUnixTimeSeconds()}}}") + ".sig";
	}
}

/// <summary>A template with one parameter of each kind, as the stream list returns it.</summary>
internal static class Templates
{
	public const string StreamListJson = """
		[{
		  "id": "project-main", "name": "//Project/Main", "projectId": "project",
		  "defaultPreflight": { "templateId": "editor-preflight" },
		  "templates": [
		    { "id": "editor-preflight", "name": "Editor Preflight", "description": "Compiles the editor and runs tests.", "allowPreflights": true, "canRun": true,
		      "parameters": [
		        { "type": "Bool", "id": "run-tests", "label": "Run tests", "default": true, "toolTip": "Runs the automation tests." },
		        { "type": "List", "label": "Target platforms", "style": "MultiList", "items": [
		            { "id": "win64", "text": "Win64", "default": true },
		            { "id": "ps5", "text": "PS5", "group": "Consoles", "default": false } ] },
		        { "type": "List", "label": "Configuration", "style": "List", "items": [
		            { "id": "config-dev", "text": "Development", "default": true },
		            { "id": "config-test", "text": "Test", "default": false } ] },
		        { "type": "Text", "id": "extra-args", "label": "Extra arguments", "default": "", "validation": "^[^;]*$", "validationError": "No semicolons" }
		      ] },
		    { "id": "full-build", "name": "Full Build", "allowPreflights": true, "canRun": true, "parameters": [] },
		    { "id": "nightly", "name": "Nightly", "allowPreflights": false, "canRun": true },
		    { "id": "locked", "name": "Locked", "allowPreflights": true, "canRun": false }
		  ]
		}]
		""";

	public static IReadOnlyList<HordeStream> Streams() => HordeStreamParser.ParseList(JsonNode.Parse(StreamListJson));

	public static HordeTemplate Editor() => Streams()[0].Templates[0];
}
