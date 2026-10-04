// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EpicGames.Horde;
using EpicGames.Horde.Commits;
using EpicGames.Horde.Jobs;
using EpicGames.Horde.Jobs.Templates;
using EpicGames.Horde.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentKit.Horde;

/// <summary>A preflight to start: a shelved change, built by a stream's template.</summary>
/// <param name="StreamId">The Horde stream.</param>
/// <param name="TemplateId">The template, which must allow preflights.</param>
/// <param name="Change">The shelved Perforce change.</param>
/// <param name="AutoSubmit">Whether Horde submits the change when the preflight succeeds. Only with <c>-autosubmit</c>.</param>
/// <param name="Parameters">Template parameter values by Horde's parameter ids (<see cref="HordeBuildParameters.ToHorde"/>); the rest keep their defaults.</param>
public sealed record HordePreflightRequest(string StreamId, string TemplateId, int Change, bool AutoSubmit, IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>The Horde calls uak makes; <see cref="HordeApi"/> makes them through EpicGames.Horde, and tests use fakes.</summary>
public interface IHordeApi : IAsyncDisposable
{
	/// <summary>The server.</summary>
	Uri ServerUrl { get; }

	/// <summary>Whether a request will be authorized, without any prompt: a cached token (refreshed silently) or an anonymous server.</summary>
	Task<bool> IsLoggedInAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Signs in, opening the sign-in page in the browser when needed, and waits for it until <paramref name="cancellationToken"/>
	/// is cancelled (uak's -login-timeout): no HTTP time-out cuts it short.
	/// </summary>
	Task<bool> LoginAsync(CancellationToken cancellationToken);

	/// <summary>Whether the server accepts this client's credentials (an authenticated request that needs no prompt).</summary>
	Task<bool> CheckSignInAsync(CancellationToken cancellationToken);

	/// <summary>The access token requests use, without any prompt; null when there is none. Never printed or logged.</summary>
	Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);

	/// <summary>The streams the user can see, with their templates.</summary>
	Task<IReadOnlyList<HordeStream>> GetStreamsAsync(CancellationToken cancellationToken);

	/// <summary>The preflight jobs of a shelved change, newest first.</summary>
	Task<IReadOnlyList<HordeJobSummary>> FindPreflightsAsync(int change, CancellationToken cancellationToken);

	/// <summary>Starts a preflight and returns its job id. Sent once: a lost answer is not retried (see <see cref="HordeApi"/>).</summary>
	Task<string> CreatePreflightAsync(HordePreflightRequest request, CancellationToken cancellationToken);

	/// <summary>A job's state, or null when it hasn't changed since <paramref name="modifiedAfter"/> (a job's <see cref="HordeJob.UpdateTime"/>).</summary>
	Task<HordeJob?> GetJobAsync(string jobId, string? modifiedAfter, CancellationToken cancellationToken);

	/// <summary>A log's events (its errors and warnings, as Horde found them), from event <paramref name="index"/>, at most <paramref name="count"/>.</summary>
	Task<IReadOnlyList<HordeLogEvent>> GetLogEventsAsync(string logId, int index, int count, CancellationToken cancellationToken);

	/// <summary>A log's lines as text, from line <paramref name="index"/> (from 0), at most <paramref name="count"/>.</summary>
	Task<IReadOnlyList<string>> GetLogLinesAsync(string logId, int index, int count, CancellationToken cancellationToken);

	/// <summary>Saves a whole log as plain text to <paramref name="path"/> (replacing it), and returns its size in bytes.</summary>
	Task<long> SaveLogAsync(string logId, string path, CancellationToken cancellationToken);
}

/// <summary>The server refused a request because the user isn't logged in, or may not do it.</summary>
public sealed class HordeAuthException : Exception
{
	/// <summary>Creates the exception.</summary>
	public HordeAuthException(string message, bool notSignedIn = false) : base(message)
	{
		NotSignedIn = notSignedIn;
	}

	/// <summary>Whether the server answered 401 (the token isn't accepted), rather than 403 (not allowed).</summary>
	public bool NotSignedIn { get; }
}

/// <summary>A request to Horde failed with an HTTP error.</summary>
public sealed class HordeApiException : Exception
{
	/// <summary>Creates the exception.</summary>
	public HordeApiException(int statusCode, string message) : base(message)
	{
		StatusCode = statusCode;
	}

	/// <summary>The HTTP status.</summary>
	public int StatusCode { get; }

	/// <summary>Whether a later try may succeed: 408, 429 and 5xx.</summary>
	public bool IsTransient => StatusCode is 408 or 429 or >= 500;
}

/// <summary>
/// <see cref="IHordeApi"/> through EpicGames.Horde: its client (<c>AddHorde</c>), its OIDC login and token cache (shared with
/// Horde's other tools), and its authenticated HttpClient for reads. Stream and job responses are read into uak's own records:
/// the library's response classes take interfaces in their constructors and can't be deserialized.
/// </summary>
/// <remarks>
/// Job creation is not idempotent, and EpicGames.Horde's HTTP handler retries a request after a time-out or a 5xx, which could
/// start the same preflight twice. So the POST goes through a plain HttpClient with the access token, once; the preflight
/// command looks for an existing preflight before it, and again after a failure.
/// </remarks>
public sealed class HordeApi : IHordeApi
{
	readonly ServiceProvider _services;
	readonly IHordeClient _client;
	HordeHttpClient? _http;

	/// <summary>Creates the client.</summary>
	/// <param name="serverUrl">The server.</param>
	/// <param name="allowLoginPrompt">Whether signing in may open the sign-in page in the browser.</param>
	/// <param name="accessToken">A token to use instead of signing in (uak's token cache; HordeOptions.AccessToken), or null.</param>
	/// <param name="logger">Receives EpicGames.Horde's own messages (at Debug level, shown with uak -verbose); null drops them.</param>
	public HordeApi(Uri serverUrl, bool allowLoginPrompt, string? accessToken = null, ILogger? logger = null)
	{
		ServiceCollection services = new();
		if (logger is not null)
		{
			services.AddLogging(builder => builder.AddProvider(new ForwardingLoggerProvider(logger)).SetMinimumLevel(LogLevel.Trace));
		}
		services.AddHorde(options =>
		{
			options.ServerUrl = serverUrl;
			options.AllowAuthPrompt = allowLoginPrompt;
			options.AccessToken = accessToken;
		});
		_services = services.BuildServiceProvider();
		_client = _services.GetRequiredService<IHordeClient>();
		ServerUrl = serverUrl;
	}

	/// <inheritdoc/>
	public Uri ServerUrl { get; }

	HttpClient Http => (_http ??= _client.CreateHttpClient()).HttpClient;

	/// <inheritdoc/>
	public async Task<bool> IsLoggedInAsync(CancellationToken cancellationToken)
	{
		await _client.GetAccessTokenAsync(false, cancellationToken).ConfigureAwait(false);
		return _client.HasValidAccessToken();
	}

	/// <inheritdoc/>
	/// <remarks>
	/// IHordeClient.LoginAsync signs in inside an HTTP request, so HttpClient's default 100 s time-out ends a sign-in the user is
	/// still doing. Asking the auth state for a token interactively waits as long as <paramref name="cancellationToken"/> allows.
	/// </remarks>
	public async Task<bool> LoginAsync(CancellationToken cancellationToken)
	{
		await _client.GetAccessTokenAsync(true, cancellationToken).ConfigureAwait(false);
		return _client.HasValidAccessToken();
	}

	/// <inheritdoc/>
	public async Task<bool> CheckSignInAsync(CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await Http.GetAsync("api/v1/dashboard/challenge", cancellationToken).ConfigureAwait(false);
		return response.IsSuccessStatusCode;
	}

	/// <summary>
	/// Whether EpicGames.Horde takes this server's token from the environment (UE_HORDE_TOKEN, when UE_HORDE_URL names the same
	/// host). uak then neither uses nor saves its own cached token, so the environment's token always wins.
	/// </summary>
	public static bool HasEnvironmentToken(Uri server)
	{
		string? url = Environment.GetEnvironmentVariable(HordeHttpClient.HordeUrlEnvVarName);
		string? token = Environment.GetEnvironmentVariable(HordeHttpClient.HordeTokenEnvVarName);
		return !string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(token) && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && parsed.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase);
	}

	/// <inheritdoc/>
	public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => _client.GetAccessTokenAsync(false, cancellationToken);

	/// <inheritdoc/>
	public async Task<IReadOnlyList<HordeStream>> GetStreamsAsync(CancellationToken cancellationToken)
		=> HordeStreamParser.ParseList(await GetJsonAsync("api/v1/streams?filter=id,name,projectId,defaultPreflight,defaultPreflightTemplate,templates", cancellationToken).ConfigureAwait(false));

	/// <inheritdoc/>
	public async Task<IReadOnlyList<HordeJobSummary>> FindPreflightsAsync(int change, CancellationToken cancellationToken)
		=> HordeJobSummary.ParseList(await GetJsonAsync($"api/v1/jobs?preflightChange={change.ToString(CultureInfo.InvariantCulture)}&count=50&filter=id,streamId,templateId,state,createTime,autoSubmit,parameters,additionalArguments,targets", cancellationToken).ConfigureAwait(false));

	/// <inheritdoc/>
	public async Task<HordeJob?> GetJobAsync(string jobId, string? modifiedAfter, CancellationToken cancellationToken)
	{
		string path = "api/v1/jobs/" + Uri.EscapeDataString(jobId) + (modifiedAfter is null ? "" : "?modifiedAfter=" + Uri.EscapeDataString(modifiedAfter));
		return HordeJob.Parse(await GetJsonAsync(path, cancellationToken).ConfigureAwait(false));
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<HordeLogEvent>> GetLogEventsAsync(string logId, int index, int count, CancellationToken cancellationToken)
		=> HordeLogParser.ParseEvents(await GetJsonAsync($"api/v1/logs/{Uri.EscapeDataString(logId)}/events?index={index.ToString(CultureInfo.InvariantCulture)}&count={count.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false));

	/// <inheritdoc/>
	public async Task<IReadOnlyList<string>> GetLogLinesAsync(string logId, int index, int count, CancellationToken cancellationToken)
		=> HordeLogParser.ParseLines(await GetJsonAsync($"api/v1/logs/{Uri.EscapeDataString(logId)}/lines?index={index.ToString(CultureInfo.InvariantCulture)}&count={count.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false));

	/// <inheritdoc/>
	/// <remarks>Streams the log (<c>api/v1/logs/{id}/data?format=Text</c>) to a temporary file beside the target, then moves it into place.</remarks>
	public async Task<long> SaveLogAsync(string logId, string path, CancellationToken cancellationToken)
	{
		string what = $"GET api/v1/logs/{Uri.EscapeDataString(logId)}/data";
		using HttpResponseMessage response = await Http.GetAsync(what[4..] + "?format=Text", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			await ReadAsync(response, what, cancellationToken).ConfigureAwait(false);
		}
		string full = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		string temporary = full + ".tmp";
		try
		{
			await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
			await using (FileStream target = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
			}
			File.Move(temporary, full, overwrite: true);
		}
		catch
		{
			File.Delete(temporary);
			throw;
		}
		return new FileInfo(full).Length;
	}

	/// <inheritdoc/>
	public async Task<string> CreatePreflightAsync(HordePreflightRequest request, CancellationToken cancellationToken)
	{
		string body = SerializeCreateRequest(request);
		string? token = await _client.GetAccessTokenAsync(false, cancellationToken).ConfigureAwait(false);
		using HttpClient http = new() { BaseAddress = ServerUrl, Timeout = TimeSpan.FromMinutes(2) };
		using HttpRequestMessage message = new(HttpMethod.Post, "api/v1/jobs") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		if (!string.IsNullOrEmpty(token))
		{
			message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}
		using HttpResponseMessage response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
		JsonNode? answer = await ReadAsync(response, "POST api/v1/jobs", cancellationToken).ConfigureAwait(false);
		string? id = (answer as JsonObject) is { } created ? Json.String(created, "id") : null;
		return string.IsNullOrEmpty(id) ? throw new HordeApiException((int)response.StatusCode, "Horde created no job id: " + answer?.ToJsonString()) : id;
	}

	/// <summary>
	/// The body of <c>POST api/v1/jobs</c>: EpicGames.Horde's own CreateJobRequest, serialized with its own JSON settings. Never
	/// auto-submits unless asked, and asks Horde not to update issues (UpdateIssues = false; a template can still force it).
	/// </summary>
	public static string SerializeCreateRequest(HordePreflightRequest request)
	{
		CreateJobRequest create = new(new StreamId(request.StreamId), new TemplateId(request.TemplateId))
		{
			PreflightCommitId = CommitId.FromPerforceChange(request.Change),
			AutoSubmit = request.AutoSubmit,
			UpdateIssues = false,
			Parameters = request.Parameters is { Count: > 0 } values ? values.ToDictionary(pair => new ParameterId(pair.Key), pair => pair.Value) : null,
		};
		JsonSerializerOptions options = new();
		HordeHttpClient.ConfigureJsonSerializer(options);
		return JsonSerializer.Serialize(create, options);
	}

	async Task<JsonNode?> GetJsonAsync(string path, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await Http.GetAsync(path, cancellationToken).ConfigureAwait(false);
		return await ReadAsync(response, "GET " + path, cancellationToken).ConfigureAwait(false);
	}

	static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
	{
		string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		if (response.StatusCode is HttpStatusCode.Unauthorized)
		{
			throw new HordeAuthException($"Horde refused {what}: not signed in ({(int)response.StatusCode}).", notSignedIn: true);
		}
		if (response.StatusCode is HttpStatusCode.Forbidden)
		{
			throw new HordeAuthException($"Horde refused {what}: not allowed ({(int)response.StatusCode}). {Brief(text)}");
		}
		if (!response.IsSuccessStatusCode)
		{
			throw new HordeApiException((int)response.StatusCode, $"Horde answered {what} with {(int)response.StatusCode} {response.ReasonPhrase}: {Brief(text)}");
		}
		try
		{
			return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
		}
		catch (JsonException exception)
		{
			throw new HordeApiException((int)response.StatusCode, $"Horde answered {what} with something that isn't JSON: {Brief(text)} ({exception.Message})");
		}
	}

	/// <summary>A response body for a message: Horde's "message" field when it has one, else the start of the text, on one line.</summary>
	internal static string Brief(string text)
	{
		try
		{
			if (JsonNode.Parse(text) is JsonObject error && Json.String(error, "message") is { Length: > 0 } message)
			{
				text = message;
			}
		}
		catch (JsonException)
		{
		}
		text = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
		return text.Length > 400 ? text[..400] + "..." : text;
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync()
	{
		_http?.Dispose();
		await _services.DisposeAsync().ConfigureAwait(false);
	}
}

/// <summary>
/// Sends the library's log messages, at every level, to uak's logger at Debug level, so they show only with
/// <c>-verbose</c> and never mix with a command's own output.
/// </summary>
internal sealed class ForwardingLoggerProvider(ILogger target) : ILoggerProvider
{
	public ILogger CreateLogger(string categoryName) => new Forwarder(target);

	public void Dispose()
	{
	}

	sealed class Forwarder(ILogger target) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => target.IsEnabled(Lower(logLevel));

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			LogLevel level = Lower(logLevel);
			if (target.IsEnabled(level))
			{
				string message = "horde: " + formatter(state, exception) + (exception is null ? "" : " (" + exception.Message + ")");
				target.Log(level, eventId, message, null, (text, _) => text);
			}
		}

		static LogLevel Lower(LogLevel level) => level switch
		{
			_ => LogLevel.Debug,
		};
	}
}
