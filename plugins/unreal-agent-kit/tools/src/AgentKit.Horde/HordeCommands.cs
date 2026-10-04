// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentKit.Core;
using AgentKit.Vcs;
using Microsoft.Extensions.Logging;

namespace AgentKit.Horde;

/// <summary>What a preflight needs from the Perforce workspace; tests fake it.</summary>
public interface IPreflightWorkspace : IDisposable
{
	/// <summary>The client's stream, then its parents up to the first stream that isn't virtual.</summary>
	Task<IReadOnlyList<PerforceStreamLink>> GetStreamChainAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Shelves every file opened in a pending change of this client: <see cref="ShelveMode.Replace"/> (<c>p4 shelve -r</c>)
	/// makes the shelf exactly the opened files, dropping shelved files that are not opened; <see cref="ShelveMode.Update"/>
	/// (<c>p4 shelve -f</c>) keeps them.
	/// </summary>
	Task<PerforceShelveResult> ShelveAsync(int change, ShelveMode mode, CancellationToken cancellationToken);

	/// <summary>When the change's files were last shelved, or null when it has no shelf or the time can't be placed.</summary>
	Task<DateTimeOffset?> GetShelveTimeAsync(int change, CancellationToken cancellationToken);

	/// <summary>The change's shelf (empty when nothing is shelved).</summary>
	Task<IReadOnlyList<PerforceShelfFile>> GetShelfAsync(int change, CancellationToken cancellationToken);

	/// <summary>The files on the shelf of a pending change of this client that are not opened in it.</summary>
	Task<IReadOnlyList<string>> GetShelvedNotOpenedAsync(int change, CancellationToken cancellationToken);
}

/// <summary>uak refuses to go on, to protect what Horde would submit (exit 5: an error, not a usage problem).</summary>
public sealed class HordeRefusedException : Exception
{
	/// <summary>Creates the exception with the message for the user.</summary>
	public HordeRefusedException(string message) : base(message)
	{
	}
}

/// <summary><see cref="IPreflightWorkspace"/> over the workspace's Perforce client.</summary>
public sealed class PerforcePreflightWorkspace : IPreflightWorkspace
{
	/// <summary>How many streams the chain may hold before uak gives up (a loop in the stream specs).</summary>
	public const int MaxChain = 16;

	readonly PerforceVersionControl _p4;

	/// <summary>Wraps a Perforce client; this object disposes it.</summary>
	public PerforcePreflightWorkspace(PerforceVersionControl p4)
	{
		_p4 = p4;
	}

	/// <summary>Finds the workspace's Perforce client. Throws <see cref="UakUsageException"/> for Git or no version control.</summary>
	public static async Task<IPreflightWorkspace> DetectAsync(UakContext context, CancellationToken cancellationToken)
	{
		VcsDetection detection;
		try
		{
			detection = await VersionControlDetector.DetectAsync(context, null, cancellationToken).ConfigureAwait(false);
		}
		catch (VcsException exception)
		{
			throw new UakUsageException(exception.Message, exception);
		}
		if (detection.VersionControl is PerforceVersionControl p4)
		{
			return new PerforcePreflightWorkspace(p4);
		}
		detection.VersionControl.Dispose();
		throw new UakUsageException($"Horde preflights need a Perforce workspace, and none was found ({detection.Provenance}). Pass -stream=<Horde stream id> to skip finding the stream.");
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<PerforceStreamLink>> GetStreamChainAsync(CancellationToken cancellationToken)
	{
		string? stream = await _p4.GetClientStreamAsync(cancellationToken).ConfigureAwait(false)
			?? throw new VcsException($"Client {_p4.ClientName} is not on a stream, so its Horde stream can't be found. Pass -stream=<Horde stream id>.");
		List<PerforceStreamLink> chain = [];
		while (stream is not null && chain.Count < MaxChain)
		{
			(string type, string? parent) = await _p4.GetStreamAsync(stream, cancellationToken).ConfigureAwait(false);
			PerforceStreamLink link = new(stream, type);
			chain.Add(link);
			stream = link.IsVirtual ? parent : null;
		}
		return chain;
	}

	/// <inheritdoc/>
	public Task<PerforceShelveResult> ShelveAsync(int change, ShelveMode mode, CancellationToken cancellationToken)
		=> _p4.ShelveAsync(change, mode, dropUnopened: mode == ShelveMode.Replace, cancellationToken);

	/// <inheritdoc/>
	public Task<DateTimeOffset?> GetShelveTimeAsync(int change, CancellationToken cancellationToken) => _p4.GetShelveTimeAsync(change, cancellationToken);

	/// <inheritdoc/>
	public Task<IReadOnlyList<PerforceShelfFile>> GetShelfAsync(int change, CancellationToken cancellationToken) => _p4.GetShelfAsync(change, cancellationToken);

	/// <inheritdoc/>
	public Task<IReadOnlyList<string>> GetShelvedNotOpenedAsync(int change, CancellationToken cancellationToken) => _p4.GetShelvedNotOpenedAsync(change, cancellationToken);

	/// <inheritdoc/>
	public void Dispose() => _p4.Dispose();
}

/// <summary>Shared plumbing for the <c>horde</c> commands: the server, the client, output and error handling.</summary>
public abstract class HordeCommandBase : IUakCommand
{
	static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() },
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>The message for exit code 4.</summary>
	public const string LoginMessage = "Not logged in to Horde at {0}, and -no-login forbids opening the sign-in page. Run the command without -no-login (it opens the Horde sign-in page in the user's browser and waits for it), or run uak horde login first.";

	/// <summary>The line printed before the browser opens.</summary>
	public const string OpeningMessage = "Opening the Horde sign-in page in your browser...";

	/// <summary>How long a sign-in may take before uak gives up, by default.</summary>
	public static readonly TimeSpan DefaultLoginTimeout = TimeSpan.FromSeconds(600);

	/// <inheritdoc/>
	public abstract string Name { get; }

	/// <inheritdoc/>
	public abstract string Summary { get; }

	/// <inheritdoc/>
	public abstract string Usage { get; }

	/// <inheritdoc/>
	public bool RequiresEngine => false;

	/// <summary>Where results go: standard output unless a test replaces it. Errors go to the context's logger.</summary>
	internal TextWriter Output { get; set; } = Console.Out;

	/// <summary>Where the job URL goes under -json, so it is seen at once without breaking the JSON on standard output.</summary>
	internal TextWriter ErrorOutput { get; set; } = Console.Error;

	/// <summary>
	/// Prints a job's URL as one line, at once: on standard output, or on standard error under -json. Callers print it as soon
	/// as they know the job, before any wait, so it is there whatever happens next (a failure, a time-out, Ctrl+C).
	/// </summary>
	internal async Task AnnounceJobAsync(Uri server, string jobId, bool json)
	{
		TextWriter writer = json ? ErrorOutput : Output;
		await writer.WriteLineAsync("Job URL: " + JobUrl(server, jobId)).ConfigureAwait(false);
		await writer.FlushAsync().ConfigureAwait(false);
	}

	/// <summary>The kit's config file; tests point it elsewhere.</summary>
	internal Func<HordeConfig> Config { get; set; } = () => HordeConfig.ForUser();

	/// <summary>Horde's own default server (environment, registry); tests replace it.</summary>
	internal Func<(Uri? Url, string Source)> HordeDefault { get; set; } = HordeServerResolver.GetHordeDefault;

	/// <summary>Creates the Horde client for a server: the flag allows the sign-in page; the token is a cached one, or null. Tests hand in fakes.</summary>
	internal Func<Uri, bool, string?, ILogger, IHordeApi> CreateApi { get; set; } = (server, prompt, token, logger) => new HordeApi(server, prompt, token, logger);

	/// <summary>uak's cache of Horde access tokens; tests point it elsewhere.</summary>
	internal Func<HordeTokenCache> TokenCache { get; set; } = () => HordeTokenCache.ForUser();

	/// <summary>The saved build settings; tests point them elsewhere.</summary>
	internal Func<HordeBuildSettingsStore> BuildSettings { get; set; } = () => HordeBuildSettingsStore.ForUser();

	/// <summary>uak's records of the preflights it started; tests point them elsewhere.</summary>
	internal Func<HordePreflightRecordStore> PreflightRecords { get; set; } = () => HordePreflightRecordStore.ForUser();

	/// <summary>The clock for saved settings' times; tests replace it.</summary>
	internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

	/// <summary>Finds the Perforce workspace; tests hand in fakes.</summary>
	internal Func<UakContext, CancellationToken, Task<IPreflightWorkspace>> Workspace { get; set; } = PerforcePreflightWorkspace.DetectAsync;

	/// <summary>Waits for jobs; tests use one with a fake clock.</summary>
	internal HordeJobWaiter Waiter { get; set; } = new();

	/// <summary>Opens a URL in the browser and returns the error, or null; tests record the URLs instead.</summary>
	internal Func<string, string?> OpenUrl { get; set; } = Browser.Open;

	/// <summary>Opens a page for <c>horde.open</c>; a failure is one warning, never a failed command.</summary>
	internal void OpenPage(UakContext context, string url)
	{
		string? error = OpenUrl(url);
		if (error is not null)
		{
			context.Logger.LogWarning("Could not open {Url} in the browser: {Error}", url, error);
		}
	}

	/// <summary>The open mode for a run: -no-open, else the config's horde.open.</summary>
	internal HordeOpenMode ResolveOpen(bool noOpen) => noOpen ? HordeOpenMode.Never : Config().ReadOpen();

	/// <inheritdoc/>
	public async Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments parsed = new(arguments);
		Func<UakContext, CancellationToken, Task<int>> run = Parse(parsed);
		parsed.ThrowIfUnknown();
		parsed.ThrowIfMorePositionalThan(0);
		try
		{
			return await run(context, cancellationToken).ConfigureAwait(false);
		}
		catch (HordeAuthException exception) when (exception.NotSignedIn)
		{
			context.Logger.LogError("{Message}", exception.Message);
			context.Logger.LogError("{Message}", $"Horde at {_server?.ToString() ?? "the server"} refused the sign-in. Run the command again: it opens the sign-in page when it needs to.");
			return HordeExitCodes.NotLoggedIn;
		}
		catch (HordeAuthException exception)
		{
			// 403: signed in, but not allowed. Signing in again would not help.
			context.Logger.LogError("{Message}", exception.Message + " You are signed in but not allowed to do this; ask the Horde admin for access.");
			return HordeExitCodes.Error;
		}
		catch (HordeRefusedException exception)
		{
			// uak's own refusal to protect what Horde would submit: an error (5), not a usage problem (2).
			context.Logger.LogError("{Message}", exception.Message);
			return HordeExitCodes.Error;
		}
		catch (UakUsageException)
		{
			throw;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			// Anything else (an answer that isn't JSON, an I/O error, a bug) is an error, never a job result: exit 5.
			context.Logger.LogError("{Message}", $"{exception.GetType().Name}: {exception.Message}");
			context.Logger.LogDebug(exception, "Details");
			return HordeExitCodes.Error;
		}
	}

	/// <summary>Deletes uak's cached token of a server; a failure is only logged.</summary>
	internal void ForgetToken(UakContext context, Uri server)
	{
		try
		{
			TokenCache().Forget(server);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("Could not delete uak's saved Horde sign-in: {Message}", exception.Message);
		}
	}

	/// <summary>Reads the command's options and returns what to run.</summary>
	internal abstract Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments);

	/// <summary>The server for this run, from -server=, the config file or Horde's default.</summary>
	internal HordeServer ResolveServer(string? argument)
	{
		HordeServer server = HordeServerResolver.Resolve(argument, Config(), HordeDefault);
		_server = server.Url;
		return server;
	}

	Uri? _server;

	/// <summary>
	/// A client that is signed in: uak's cached access token while it has more than 5 minutes left (see
	/// <see cref="HordeTokenCache"/>); else silently (Horde's refresh token, without a prompt); when that fails, unless
	/// <paramref name="login"/> says -no-login, through the sign-in page in the user's browser, in this process, waiting up to
	/// -login-timeout. A new token is cached for the next commands. Returns null (after saying why) when it isn't signed in.
	/// </summary>
	internal async Task<IHordeApi?> ConnectAsync(UakContext context, HordeServer server, LoginOptions login, TextWriter log, CancellationToken cancellationToken)
	{
		HordeTokenCache cache = TokenCache();
		bool environmentToken = HordeApi.HasEnvironmentToken(server.Url);
		HordeApiSession Session(IHordeApi inner, bool fromCache) => new(inner, fromCache,
			(limit, token) => SignInFreshAsync(context, server, limit is { } left && left < login.Timeout ? login with { Timeout = left } : login, log, cache, environmentToken, token),
			token => CreateApi(server.Url, false, token, context.Logger),
			() => ForgetToken(context, server.Url),
			context.Logger);

		string? cached = environmentToken ? null : cache.Read(server.Url);
		if (cached is not null)
		{
			context.Logger.LogDebug("Using uak's saved Horde sign-in ({Path}).", cache.PathFor(server.Url));
			return Session(CreateApi(server.Url, false, cached, context.Logger), fromCache: true);
		}
		IHordeApi? api = await SignInFreshAsync(context, server, login, log, cache, environmentToken, cancellationToken).ConfigureAwait(false);
		return api is null ? null : Session(api, fromCache: false);
	}

	/// <summary>
	/// Signs in without uak's cache: silently (Horde's own token cache), else, unless -no-login, through the sign-in page. Saves
	/// the new token in uak's cache unless the environment supplies the token. Null (after saying why) when not signed in.
	/// </summary>
	async Task<IHordeApi?> SignInFreshAsync(UakContext context, HordeServer server, LoginOptions login, TextWriter log, HordeTokenCache cache, bool environmentToken, CancellationToken cancellationToken)
	{
		IHordeApi api = CreateApi(server.Url, false, null, context.Logger);
		bool loggedIn;
		try
		{
			loggedIn = await api.IsLoggedInAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await api.DisposeAsync().ConfigureAwait(false);
			throw;
		}
		if (loggedIn)
		{
			if (!environmentToken)
			{
				await SaveTokenAsync(context, cache, server, api, cancellationToken).ConfigureAwait(false);
			}
			return api;
		}
		await api.DisposeAsync().ConfigureAwait(false);
		if (!login.Allowed)
		{
			context.Logger.LogError("{Message}", string.Format(CultureInfo.InvariantCulture, LoginMessage, server.Url));
			return null;
		}
		IHordeApi interactive = CreateApi(server.Url, true, null, context.Logger);
		if (await SignInAsync(context, interactive, server, login.Timeout, log, cancellationToken).ConfigureAwait(false))
		{
			if (!environmentToken)
			{
				await SaveTokenAsync(context, cache, server, interactive, cancellationToken).ConfigureAwait(false);
			}
			return interactive;
		}
		await interactive.DisposeAsync().ConfigureAwait(false);
		return null;
	}

	/// <summary>Caches a client's access token for the next commands. Failures are only logged (at Debug); the token never is.</summary>
	internal static async Task SaveTokenAsync(UakContext context, HordeTokenCache cache, HordeServer server, IHordeApi api, CancellationToken cancellationToken)
	{
		if (!cache.Enabled)
		{
			return;
		}
		try
		{
			string? token = await api.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
			if (token is not null && cache.Save(server.Url, token))
			{
				context.Logger.LogDebug("Saved the Horde sign-in to {Path}.", cache.PathFor(server.Url));
			}
			else
			{
				context.Logger.LogDebug("The Horde sign-in was not saved: there is no token, or its expiry isn't known.");
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or HordeAuthException or HttpRequestException)
		{
			context.Logger.LogDebug("Could not save the Horde sign-in: {Message}", exception.Message);
		}
	}

	/// <summary>
	/// The Horde stream for a run: <paramref name="streamId"/> when given; else the one that builds the workspace's Perforce
	/// stream, walking up virtual streams (see <see cref="HordeSelection.FromChain"/>). Uses <paramref name="workspace"/>, or
	/// finds the workspace and disposes it after.
	/// </summary>
	internal async Task<HordeStream> FindStreamAsync(UakContext context, IReadOnlyList<HordeStream> streams, string? streamId, IPreflightWorkspace? workspace, CancellationToken cancellationToken)
	{
		if (streamId is not null)
		{
			return streams.FirstOrDefault(candidate => candidate.Id.Equals(streamId, StringComparison.OrdinalIgnoreCase))
				?? throw new UakUsageException($"No Horde stream '{streamId}' (or you can't see it). uak horde streams lists them.");
		}
		IPreflightWorkspace? owned = null;
		try
		{
			workspace ??= owned = await Workspace(context, cancellationToken).ConfigureAwait(false);
			IReadOnlyList<PerforceStreamLink> chain = await workspace.GetStreamChainAsync(cancellationToken).ConfigureAwait(false);
			(HordeStream stream, string perforceStream) = HordeSelection.FromChain(streams, chain);
			context.Logger.LogDebug("Horde stream {Stream} builds {PerforceStream}", stream.Id, perforceStream);
			return stream;
		}
		finally
		{
			owned?.Dispose();
		}
	}

	/// <summary>Writes a JSON object, indented.</summary>
	internal Task WriteNodeAsync(JsonNode node) => Output.WriteLineAsync(node.ToJsonString(s_jsonOptions));

	/// <summary>
	/// Opens the sign-in page and waits for it, up to <paramref name="timeout"/>. Prints one line before it and one result
	/// line; a failure or a time-out is also logged as an error.
	/// </summary>
	internal static async Task<bool> SignInAsync(UakContext context, IHordeApi api, HordeServer server, TimeSpan timeout, TextWriter log, CancellationToken cancellationToken)
	{
		await log.WriteLineAsync(OpeningMessage).ConfigureAwait(false);
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(timeout);
		string failure;
		try
		{
			if (await api.LoginAsync(limit.Token).ConfigureAwait(false))
			{
				await log.WriteLineAsync($"Signed in to {server.Url}.").ConfigureAwait(false);
				return true;
			}
			failure = $"The Horde sign-in at {server.Url} did not succeed. Ask the user to sign in, then run the command again.";
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			failure = $"Nobody finished the Horde sign-in at {server.Url} within {timeout.TotalSeconds:0} s. Ask the user to sign in when the page opens (-login-timeout= gives more time), then run the command again.";
		}
		context.Logger.LogError("{Message}", failure);
		return false;
	}

	/// <summary>Whether a command may open the sign-in page (not with -no-login), and for how long it waits (-login-timeout=).</summary>
	internal readonly record struct LoginOptions(bool Allowed, TimeSpan Timeout)
	{
		/// <summary>Parses -no-login and -login-timeout=&lt;seconds&gt; (default 600).</summary>
		public static LoginOptions Parse(UakArguments arguments, bool allowNoLogin = true)
		{
			bool noLogin = allowNoLogin && arguments.GetFlag("no-login");
			int? seconds = arguments.GetInt("login-timeout", 1, 86400);
			if (seconds is not null && noLogin)
			{
				throw new UakUsageException("-login-timeout and -no-login don't go together.");
			}
			return new LoginOptions(!noLogin, seconds is null ? DefaultLoginTimeout : TimeSpan.FromSeconds(seconds.Value));
		}
	}

	/// <summary>The usage lines of -login-timeout and -no-login.</summary>
	internal const string LoginUsage =
		"  -login-timeout=  when no cached token works, the command opens the Horde sign-in page in the user's browser and\n" +
		"                   waits this many seconds for the sign-in (default 600), then exits 4.\n" +
		"  -no-login        never open the sign-in page (CI, scripts): exit 4 when no cached token works.\n";

	/// <summary>Writes a value as indented JSON.</summary>
	internal Task WriteJsonAsync<T>(T value) => Output.WriteLineAsync(JsonSerializer.Serialize(value, s_jsonOptions));

	/// <summary>A shelve's files for JSON (depot path and action), or null without a shelve.</summary>
	internal static object? ShelvedFiles(PerforceShelveResult? shelved) => shelved?.Shelved.Select(file => new { depotFile = file.DepotFile, action = file.Action }).ToList();

	/// <summary>The dashboard page of a job.</summary>
	internal static string JobUrl(Uri server, string jobId) => new Uri(server, "job/" + Uri.EscapeDataString(jobId)).ToString();

	/// <summary>The dashboard page of a step.</summary>
	internal static string StepUrl(Uri server, string jobId, string stepId) => JobUrl(server, jobId) + "?step=" + Uri.EscapeDataString(stepId);

	/// <summary>The dashboard page of a log.</summary>
	internal static string LogUrl(Uri server, string logId) => new Uri(server, "log/" + Uri.EscapeDataString(logId)).ToString();

	/// <summary>Parses -timeout=&lt;seconds&gt;.</summary>
	internal static TimeSpan? ParseTimeout(UakArguments arguments)
	{
		int? seconds = arguments.GetInt("timeout", 1, int.MaxValue / 1000);
		return seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);
	}

	/// <summary>
	/// Waits for a job (when <paramref name="wait"/>) or reads it once, prints the summary (or JSON), and returns the exit code:
	/// 0 success, 1 failure or incomplete, 3 still running, 7 warnings.
	/// </summary>
	internal async Task<int> ReportJobAsync(UakContext context, IHordeApi api, string jobId, bool wait, TimeSpan? timeout, bool verbose, bool json, HordeOpenMode open, CancellationToken cancellationToken, bool announce = true, PerforceShelveResult? shelved = null, HordeLogOptions? issues = null)
	{
		HordeJob? job;
		bool timedOut;
		if (wait)
		{
			if (announce)
			{
				await AnnounceJobAsync(api.ServerUrl, jobId, json).ConfigureAwait(false);
			}
			if (open == HordeOpenMode.Created)
			{
				OpenPage(context, JobUrl(api.ServerUrl, jobId));
			}
			(job, timedOut) = await Waiter.WaitAsync(api, jobId, timeout, json ? TextWriter.Null : Output, cancellationToken).ConfigureAwait(false);
		}
		else
		{
			job = await api.GetJobAsync(jobId, null, cancellationToken).ConfigureAwait(false);
			timedOut = false;
		}
		if (job is null && timedOut)
		{
			// -timeout passed before any poll got an answer (Horde unreachable all along): the job's state is unknown, so wait again.
			if (json)
			{
				await WriteJsonAsync(new { id = jobId, url = JobUrl(api.ServerUrl, jobId), result = "Unknown", timedOut = true, shelved = ShelvedFiles(shelved), replaced = shelved?.Replaced, removed = shelved?.Removed, kept = shelved?.Kept }).ConfigureAwait(false);
			}
			else
			{
				await Output.WriteLineAsync($"Still unknown: Horde could not be reached before -timeout passed: {JobUrl(api.ServerUrl, jobId)}").ConfigureAwait(false);
				await Output.WriteLineAsync($"  Wait again with: uak horde job -id={jobId} -wait -timeout=<seconds>").ConfigureAwait(false);
			}
			return HordeExitCodes.StillRunning;
		}
		if (job is null)
		{
			throw new HordeApiException(404, $"Horde returned nothing for job {jobId}.");
		}

		HordeJobResult result = job.Result;
		List<HordeStep> latest = [.. job.LatestSteps];
		List<HordeStep> failed = latest.Where(step => step.Outcome.Equals("Failure", StringComparison.OrdinalIgnoreCase)).ToList();
		List<HordeStep> warned = latest.Where(step => step.Outcome.Equals("Warnings", StringComparison.OrdinalIgnoreCase)).ToList();
		List<HordeStep> skipped = latest.Where(step => step.State.Equals("Skipped", StringComparison.OrdinalIgnoreCase) || step.State.Equals("Aborted", StringComparison.OrdinalIgnoreCase)).ToList();
		Uri server = api.ServerUrl;
		int exitCode = timedOut ? HordeExitCodes.StillRunning : HordeExitCodes.For(result);
		if (wait && !timedOut && result != HordeJobResult.Running)
		{
			if (open == HordeOpenMode.Finished)
			{
				OpenPage(context, JobUrl(server, job.Id));
			}
			else if (open == HordeOpenMode.Failed && result is HordeJobResult.Failure or HordeJobResult.Incomplete)
			{
				OpenPage(context, failed.Count > 0 ? StepUrl(server, job.Id, failed[0].Id) : JobUrl(server, job.Id));
			}
		}

		// -issues: the failed steps' errors and warnings, then the other steps' warnings, from the logs' events.
		IReadOnlyList<HordeStepIssues>? stepIssues = issues is null || failed.Count + warned.Count == 0 ? null
			: await LogReader.ReadAsync(api, [.. failed, .. warned], issues, null, cancellationToken).ConfigureAwait(false);

		if (json)
		{
			object Step(HordeStep step) => new { name = step.Name, state = step.State, outcome = step.Outcome, url = StepUrl(server, job.Id, step.Id), log = step.LogId is null ? null : LogUrl(server, step.LogId) };
			await WriteJsonAsync(new
			{
				id = job.Id,
				name = job.Name,
				url = JobUrl(server, job.Id),
				streamId = job.StreamId,
				templateId = job.TemplateId,
				preflightChange = job.PreflightChange,
				autoSubmit = job.AutoSubmit,
				state = job.State,
				result = timedOut ? "Running" : result.ToString(),
				timedOut,
				failed = failed.Select(Step),
				warnings = warned.Select(Step),
				skipped = skipped.Count,
				batchErrors = job.BatchErrors,
				steps = verbose ? latest.Select(Step) : null,
				shelved = ShelvedFiles(shelved),
				replaced = shelved?.Replaced,
				removed = shelved?.Removed,
				kept = shelved?.Kept,
				issues = issues is null ? null : HordeLogReader.ToJson(server, job.Id, stepIssues ?? []),
			}).ConfigureAwait(false);
			return exitCode;
		}

		string headline = timedOut || result == HordeJobResult.Running
			? $"Still running ({job.State}): {job.Name}"
			: $"Result: {result}: {job.Name}";
		await Output.WriteLineAsync(headline).ConfigureAwait(false);
		await Output.WriteLineAsync("  " + JobUrl(server, job.Id)).ConfigureAwait(false);
		if (verbose)
		{
			foreach (HordeStep step in latest)
			{
				await Output.WriteLineAsync($"  {step.State,-9} {step.Outcome,-11} {step.Name}  {StepUrl(server, job.Id, step.Id)}").ConfigureAwait(false);
			}
		}
		foreach (HordeStep step in failed)
		{
			await Output.WriteLineAsync($"  FAILED   {step.Name}  {StepUrl(server, job.Id, step.Id)}" + (step.LogId is null ? "" : $"  log: {LogUrl(server, step.LogId)}")).ConfigureAwait(false);
		}
		foreach (HordeStep step in warned)
		{
			await Output.WriteLineAsync($"  WARNINGS {step.Name}  {StepUrl(server, job.Id, step.Id)}" + (step.LogId is null ? "" : $"  log: {LogUrl(server, step.LogId)}")).ConfigureAwait(false);
		}
		if (result == HordeJobResult.Incomplete)
		{
			string why = job.AbortedBy is not null ? $"aborted by {job.AbortedBy}" : $"{skipped.Count} step(s) skipped or aborted";
			await Output.WriteLineAsync($"  Did not complete: {why}.").ConfigureAwait(false);
		}
		foreach (HordeBatchError error in job.BatchErrors)
		{
			await Output.WriteLineAsync($"  Batch {error.GroupIdx} could not run: {error.Error}.").ConfigureAwait(false);
		}
		if (timedOut)
		{
			await Output.WriteLineAsync($"  -timeout passed; wait again with: uak horde job -id={job.Id} -wait -timeout=<seconds>").ConfigureAwait(false);
		}
		if (stepIssues is not null)
		{
			await HordeLogReader.WriteTextAsync(Output, server, stepIssues).ConfigureAwait(false);
			await Output.WriteLineAsync($"More: uak horde log -job={job.Id} [-step=<id or name>] [-max=<n>] [-context=<n>] [-save]").ConfigureAwait(false);
		}
		return exitCode;
	}

	/// <summary>Reads steps' log events; tests replace it to change its limits.</summary>
	internal HordeLogReader LogReader { get; set; } = new();

	/// <summary>The usage lines of -issues.</summary>
	internal const string IssuesUsage =
		"  -issues    also print the errors and warnings of the failed steps and those with warnings, from Horde's log\n" +
		"             events: de-duplicated, at most 10 distinct per step, errors with 2 lines of context before them\n" +
		"             (uak horde log shows more).\n";
}

/// <summary><c>uak horde config</c>: shows or sets the Horde server, the -open setting, and each stream's saved build settings.</summary>
public sealed class HordeConfigCommand : HordeCommandBase
{
	/// <inheritdoc/>
	public override string Name => "horde config";

	/// <inheritdoc/>
	public override string Summary => "Show or set the Horde server, when -wait opens the job's page, and each stream's saved build settings.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak horde config [-server=<url>] [-open=never|created|finished|failed] [-show] [-json]\n" +
		"uak horde config [-stream=<id>] [-template=<id or name>] [-param:<id>=<value> ...] [-login-timeout=<s> | -no-login] [-json]\n" +
		"uak horde config [-stream=<id>] -reset-build [-json]\n" +
		"  Without options, or with -show: the server the horde commands use and where it comes from, the -open setting, and\n" +
		"  the build settings saved for that server (-stream= shows one stream's).\n" +
		"  -server=  store this server (an http or https URL) in <UAK_HOME or ~/.unreal-agent-kit>/config.json.\n" +
		"  -open=    when preflight -wait and job -wait open the job's page in the browser: never (the default), created\n" +
		"            (as soon as the job exists), finished (when it ends), or failed (the first failed step, when it fails or\n" +
		"            doesn't complete). -no-open skips it for one run.\n" +
		"  Build settings are what uak horde preflight builds with, saved per server and Horde stream in\n" +
		"  <UAK_HOME>/horde/<server>/<stream>/templates.json. Saving checks them against the server first.\n" +
		"  -stream=     the Horde stream id. Default: the workspace's, found as uak horde preflight finds it.\n" +
		"  -template=   the template preflights use (it must allow preflights). Default: the saved one.\n" +
		"  -param:<id>= a parameter value (repeatable), over the template's saved values: bool true|false, a list's choice ids\n" +
		"               comma-separated (at most one for a single-choice list), or text. uak horde templates lists the ids,\n" +
		"               choices and defaults.\n" +
		"  -reset-build forget the stream's saved build settings: the next preflight exits 6 and they are asked again.\n" +
		"  The other horde commands take the server from -server=, then config.json, then Horde's own default (UE_HORDE_URL,\n" +
		"  then the registry on Windows or ~/.horde.json elsewhere). When there is none they fail and say so: agents ask the\n" +
		"  user (through the lead) for the URL, then run uak horde config -server=<url>.\n" + LoginUsage;

	internal override Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		string? server = arguments.GetString("server");
		string? openText = arguments.GetString("open");
		string? streamId = arguments.GetString("stream");
		string? template = arguments.GetString("template");
		IReadOnlyList<KeyValuePair<string, string>> parameters = arguments.GetPrefixed("param:");
		bool reset = arguments.GetFlag("reset-build");
		bool show = arguments.GetFlag("show");
		bool json = arguments.GetFlag("json");
		LoginOptions login = LoginOptions.Parse(arguments);
		HordeOpenMode? openMode = null;
		if (openText is not null)
		{
			openMode = HordeConfig.TryParseOpen(openText, out HordeOpenMode parsed) ? parsed : throw new UakUsageException($"-open must be never, created, finished or failed, not '{openText}'.");
		}
		Uri? url = null;
		if (server is not null && !HordeConfig.TryParseServer(server, out url, out string? serverError))
		{
			throw new UakUsageException("-server: " + serverError);
		}
		bool setBuild = template is not null || parameters.Count > 0;
		if (reset && setBuild)
		{
			throw new UakUsageException("-reset-build and -template=/-param: don't go together.");
		}
		if (streamId is not null && !HordeBuildSettingsStore.IsId(streamId))
		{
			throw new UakUsageException($"'{streamId}' is not a Horde stream id.");
		}

		return async (context, cancellationToken) =>
		{
			HordeConfig config = Config();
			JsonObject changes = [];
			if (url is not null)
			{
				config.WriteServer(url);
				changes["server"] = url.ToString();
				if (!json)
				{
					await Output.WriteLineAsync($"Horde server: {url} (stored in {config.Path})").ConfigureAwait(false);
				}
			}
			if (openMode is not null)
			{
				config.WriteOpen(openMode.Value);
				changes["open"] = openMode.Value.ToString().ToLowerInvariant();
				if (!json)
				{
					await Output.WriteLineAsync($"Open the job's page: {openMode.Value.ToString().ToLowerInvariant()} (stored in {config.Path})").ConfigureAwait(false);
				}
			}
			if (setBuild || reset)
			{
				HordeServer found = ResolveServer(null);
				string? id = streamId;
				if (setBuild || id is null)
				{
					await using IHordeApi? api = await ConnectAsync(context, found, login, json ? TextWriter.Null : Output, cancellationToken).ConfigureAwait(false);
					if (api is null)
					{
						return HordeExitCodes.NotLoggedIn;
					}
					IReadOnlyList<HordeStream> streams = await api.GetStreamsAsync(cancellationToken).ConfigureAwait(false);
					HordeStream stream = await FindStreamAsync(context, streams, streamId, null, cancellationToken).ConfigureAwait(false);
					id = stream.Id;
					if (setBuild)
					{
						(HordeSavedBuild saved, HordeTemplate chosen, List<string> dropped) = SaveBuild(found, stream, template, parameters);
						changes["buildSettings"] = SavedToJson(saved);
						foreach (string warning in dropped)
						{
							context.Logger.LogWarning("{Message}", warning);
						}
						if (!json)
						{
							await Output.WriteLineAsync($"Saved build settings for stream {stream.Id}: {HordeBuildParameters.Describe(chosen, saved.ParametersFor(chosen.Id))}").ConfigureAwait(false);
							await Output.WriteLineAsync($"  ({saved.Path})").ConfigureAwait(false);
						}
					}
				}
				if (reset)
				{
					bool had = BuildSettings().Reset(found.Url, id!);
					changes["reset"] = new JsonObject { ["stream"] = id, ["hadSettings"] = had };
					if (!json)
					{
						await Output.WriteLineAsync(had ? $"Forgot the build settings of stream {id}: the next preflight asks for them (exit 6)." : $"No build settings were saved for stream {id}.").ConfigureAwait(false);
					}
				}
			}
			if (changes.Count > 0 && !show)
			{
				if (json)
				{
					changes["configFile"] = config.Path;
					await WriteNodeAsync(changes).ConfigureAwait(false);
				}
				return UakExitCodes.Success;
			}
			return await ShowAsync(config, streamId, json).ConfigureAwait(false);
		};
	}

	/// <summary>
	/// Saves a stream's template and parameters, after checking them against the server's template: the saved values of the
	/// template, then the new ones over them. Saved values that no longer fit the template, and that no new value replaces,
	/// are dropped, each with a warning (returned). Nothing is saved when a new value is wrong.
	/// </summary>
	(HordeSavedBuild Saved, HordeTemplate Template, List<string> Dropped) SaveBuild(HordeServer found, HordeStream stream, string? templateArgument, IReadOnlyList<KeyValuePair<string, string>> parameters)
	{
		HordeBuildSettingsStore store = BuildSettings();
		HordeSavedBuild? saved = store.Read(found.Url, stream.Id);
		string requested = templateArgument ?? saved?.Template
			?? throw new UakUsageException($"-template= is required: stream {stream.Id} has no saved template. uak horde templates lists them.");
		HordeTemplate template = HordeSelection.ChooseTemplate(stream, requested);
		if (!template.CanRun)
		{
			throw new UakUsageException($"You can't run template {template.Id} ({template.Name}) in stream {stream.Id}, so preflights can't use it.");
		}

		List<string> errors = [];
		Dictionary<string, string> values = HordeBuildParameters.Validate(template, parameters, errors);
		if (errors.Count > 0)
		{
			throw new UakUsageException(string.Join(" ", errors) + " Nothing was saved; uak horde templates lists the parameters.");
		}
		List<string> dropped = [];
		foreach ((string id, string value) in saved?.ParametersFor(template.Id) ?? new Dictionary<string, string>())
		{
			List<string> problems = [];
			Dictionary<string, string> kept = HordeBuildParameters.Validate(template, [new KeyValuePair<string, string>(id, value)], problems);
			if (problems.Count > 0)
			{
				if (!values.Keys.Any(key => key.Equals(id, StringComparison.OrdinalIgnoreCase)))
				{
					dropped.Add($"Dropped the saved value {id}={value}: {problems[0]}");
				}
				continue;
			}
			foreach ((string keptId, string keptValue) in kept)
			{
				values.TryAdd(keptId, keptValue);
			}
		}
		return (store.Save(found.Url, stream.Id, template, values, UtcNow()), template, dropped);
	}

	async Task<int> ShowAsync(HordeConfig config, string? streamId, bool json)
	{
		string open = config.ReadOpen().ToString().ToLowerInvariant();
		HordeServer found;
		try
		{
			found = ResolveServer(null);
		}
		catch (UakUsageException exception) when (exception.Message == HordeServerResolver.NotConfiguredMessage)
		{
			if (json)
			{
				await WriteJsonAsync(new { server = (string?)null, open, configFile = config.Path }).ConfigureAwait(false);
			}
			else
			{
				await Output.WriteLineAsync(exception.Message).ConfigureAwait(false);
			}
			return UakExitCodes.UsageError;
		}
		HordeBuildSettingsStore store = BuildSettings();
		List<HordeSavedBuild> builds = store.ReadAll(found.Url)
			.Where(build => build.Template is not null && (streamId is null || build.Stream.Equals(streamId, StringComparison.OrdinalIgnoreCase)))
			.ToList();
		string folder = Path.Combine(store.Root, HordeBuildSettingsStore.ServerFolder(found.Url));
		if (json)
		{
			JsonObject shown = new()
			{
				["server"] = found.Url.ToString(),
				["source"] = found.Source,
				["stored"] = found.Stored,
				["open"] = open,
				["configFile"] = config.Path,
				["buildSettingsFolder"] = folder,
				["buildSettings"] = new JsonArray(builds.Select(build => (JsonNode)SavedToJson(build)).ToArray()),
			};
			await WriteNodeAsync(shown).ConfigureAwait(false);
			return UakExitCodes.Success;
		}
		await Output.WriteLineAsync($"Horde server: {found.Url} (from {found.Source})").ConfigureAwait(false);
		await Output.WriteLineAsync($"Open the job's page (uak horde config -open=): {open}").ConfigureAwait(false);
		if (!found.Stored)
		{
			await Output.WriteLineAsync($"To keep it in the kit's config ({config.Path}): uak horde config -server={found.Url}").ConfigureAwait(false);
		}
		if (builds.Count == 0)
		{
			await Output.WriteLineAsync($"Build settings: none saved{(streamId is null ? "" : $" for stream {streamId}")} (a preflight asks for them on first use: exit 6).").ConfigureAwait(false);
		}
		else
		{
			await Output.WriteLineAsync($"Build settings ({folder}):").ConfigureAwait(false);
			foreach (HordeSavedBuild build in builds)
			{
				IReadOnlyDictionary<string, string> values = build.ParametersFor(build.Template!);
				string parameters = values.Count == 0 ? "template defaults" : string.Join(", ", values.Select(pair => $"{pair.Key}={pair.Value}"));
				await Output.WriteLineAsync($"  {build.Stream}: template {build.Template}; parameters: {parameters}{(build.Updated is null ? "" : $" (saved {build.Updated})")}").ConfigureAwait(false);
			}
		}
		return UakExitCodes.Success;
	}

	static JsonObject SavedToJson(HordeSavedBuild build)
	{
		JsonObject values = [];
		if (build.Template is not null)
		{
			foreach ((string id, string value) in build.ParametersFor(build.Template))
			{
				values[id] = value;
			}
		}
		return new JsonObject { ["stream"] = build.Stream, ["template"] = build.Template, ["parameters"] = values, ["updated"] = build.Updated, ["file"] = build.Path };
	}
}

/// <summary><c>uak horde login</c>: the interactive login the user runs once.</summary>
public sealed class HordeLoginCommand : HordeCommandBase
{
	/// <inheritdoc/>
	public override string Name => "horde login";

	/// <inheritdoc/>
	public override string Summary => "Sign in to Horde: opens the sign-in page in the user's browser unless a cached token still works.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak horde login [-login-timeout=<seconds>] [-server=<url>]\n" +
		"  When a cached token still works, it says so and opens nothing. Otherwise it prints one line, opens the Horde\n" +
		"  sign-in page in the user's browser, waits for the sign-in (default 600 s), and prints the result. The other horde\n" +
		"  commands do the same by themselves when they need to, so this is only for signing in ahead of time.\n" +
		"  It uses Horde's own login and token cache (shared with Horde's other tools), and saves the access token in uak's\n" +
		"  cache (Windows: encrypted for this user; uak horde logout deletes it). uak -verbose shows why a cached token was\n" +
		"  refused.\n" +
		"  Exit codes: 0 signed in, 4 the sign-in failed or didn't finish in time.";

	internal override Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		string? server = arguments.GetString("server");
		LoginOptions login = LoginOptions.Parse(arguments, allowNoLogin: false);
		return async (context, cancellationToken) =>
		{
			HordeServer found = ResolveServer(server);
			HordeTokenCache cache = TokenCache();
			bool environmentToken = HordeApi.HasEnvironmentToken(found.Url);
			string? cached = environmentToken ? null : cache.Read(found.Url);
			if (cached is not null)
			{
				await using IHordeApi saved = CreateApi(found.Url, false, cached, context.Logger);
				if (await saved.CheckSignInAsync(cancellationToken).ConfigureAwait(false))
				{
					await Output.WriteLineAsync($"Already logged in to {found.Url} (uak's saved sign-in, checked with the server).").ConfigureAwait(false);
					return UakExitCodes.Success;
				}
				ForgetToken(context, found.Url);
				context.Logger.LogWarning("Horde refused uak's saved sign-in, so it is deleted.");
			}
			await using (IHordeApi silent = CreateApi(found.Url, false, null, context.Logger))
			{
				if (await silent.IsLoggedInAsync(cancellationToken).ConfigureAwait(false))
				{
					if (!environmentToken)
					{
						await SaveTokenAsync(context, cache, found, silent, cancellationToken).ConfigureAwait(false);
					}
					await Output.WriteLineAsync($"Already logged in to {found.Url}.").ConfigureAwait(false);
					return UakExitCodes.Success;
				}
			}
			await using IHordeApi api = CreateApi(found.Url, true, null, context.Logger);
			if (!await SignInAsync(context, api, found, login.Timeout, Output, cancellationToken).ConfigureAwait(false))
			{
				return HordeExitCodes.NotLoggedIn;
			}
			if (!environmentToken)
			{
				await SaveTokenAsync(context, cache, found, api, cancellationToken).ConfigureAwait(false);
			}
			return UakExitCodes.Success;
		};
	}
}

/// <summary><c>uak horde streams</c>: the Horde streams and their preflight templates.</summary>
public sealed class HordeStreamsCommand : HordeCommandBase
{
	/// <inheritdoc/>
	public override string Name => "horde streams";

	/// <inheritdoc/>
	public override string Summary => "List the Horde streams you can see and their preflight templates, marking the workspace's stream.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak horde streams [-stream=<id>] [-all] [-login-timeout=<seconds> | -no-login] [-server=<url>] [-json]\n" +
		"  Lists streams with at least one template that allows preflights (-all: every stream), each with those templates;\n" +
		"  the stream's default preflight template is marked with *. -stream= shows one stream. In a Perforce workspace on a\n" +
		"  stream, the Horde stream uak horde preflight would pick is marked with >.\n" + LoginUsage +
		"  Exit codes: 0 listed, 4 not signed in, 5 another error.";

	internal override Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		string? server = arguments.GetString("server");
		string? only = arguments.GetString("stream");
		bool all = arguments.GetFlag("all");
		bool json = arguments.GetFlag("json");
		LoginOptions login = LoginOptions.Parse(arguments);
		return async (context, cancellationToken) =>
		{
			HordeServer found = ResolveServer(server);
			await using IHordeApi? api = await ConnectAsync(context, found, login, json ? TextWriter.Null : Output, cancellationToken).ConfigureAwait(false);
			if (api is null)
			{
				return HordeExitCodes.NotLoggedIn;
			}
			IReadOnlyList<HordeStream> streams = await api.GetStreamsAsync(cancellationToken).ConfigureAwait(false);
			string? workspaceStream = null;
			try
			{
				using IPreflightWorkspace workspace = await Workspace(context, cancellationToken).ConfigureAwait(false);
				workspaceStream = HordeSelection.FromChain(streams, await workspace.GetStreamChainAsync(cancellationToken).ConfigureAwait(false)).Stream.Id;
			}
			catch (Exception exception) when (exception is UakUsageException or VcsException)
			{
				context.Logger.LogDebug("No workspace stream: {Message}", exception.Message);
			}

			List<HordeStream> shown = streams
				.Where(stream => only is null ? all || stream.Templates.Any(template => template.AllowPreflights) : stream.Id.Equals(only, StringComparison.OrdinalIgnoreCase))
				.OrderBy(stream => stream.Name, StringComparer.OrdinalIgnoreCase).ToList();
			if (only is not null && shown.Count == 0)
			{
				throw new UakUsageException($"No Horde stream '{only}' (or you can't see it).");
			}
			if (json)
			{
				await WriteJsonAsync(shown.Select(stream => new
				{
					id = stream.Id,
					name = stream.Name,
					projectId = stream.ProjectId,
					workspace = stream.Id == workspaceStream,
					defaultPreflightTemplate = stream.DefaultPreflightTemplate,
					templates = stream.Templates.Where(template => all || only is not null || template.AllowPreflights),
				})).ConfigureAwait(false);
				return UakExitCodes.Success;
			}
			foreach (HordeStream stream in shown)
			{
				await Output.WriteLineAsync($"{(stream.Id == workspaceStream ? ">" : " ")} {stream.Id}  {stream.Name}").ConfigureAwait(false);
				foreach (HordeTemplate template in stream.Templates.Where(template => all || only is not null || template.AllowPreflights))
				{
					string mark = template.Id.Equals(stream.DefaultPreflightTemplate, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
					string notes = (template.AllowPreflights ? "" : " (no preflights)") + (template.CanRun ? "" : " (you can't run it)");
					await Output.WriteLineAsync($"    {mark} {template.Id}  {template.Name}{notes}").ConfigureAwait(false);
				}
			}
			return UakExitCodes.Success;
		};
	}
}

/// <summary><c>uak horde preflight</c>: starts a Horde preflight of a shelved change.</summary>
public sealed class HordePreflightCommand : HordeCommandBase
{
	/// <inheritdoc/>
	public override string Name => "horde preflight";

	/// <inheritdoc/>
	public override string Summary => "Start a Horde preflight of a shelved Perforce change, and optionally wait for its result.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak horde preflight -c=<shelved change> [-template=<id or name>] [-param:<id>=<value> ...] [-use-template-defaults]\n" +
		"                    [-stream=<id>] [-shelve [-keep-unopened]] [-force] [-autosubmit [-allow-shelved-only]]\n" +
		"                    [-wait [-timeout=<seconds>] [-verbose] [-issues] [-no-open]]\n" +
		"                    [-login-timeout=<seconds> | -no-login] [-server=<url>] [-json]\n" +
		"  Builds with the stream's saved build settings (template and parameters, see uak horde config). With none saved,\n" +
		"  and no -template= or -param:, it starts nothing and exits 6, printing the templates and their parameters (as\n" +
		"  uak horde templates -json) and the uak horde config command that saves the user's answers.\n" +
		"  Prints \"Job URL: <server>/job/<id>\" as soon as the job is created (or found), before any waiting; under -json, on\n" +
		"  standard error, and in the JSON too. An agent sends that URL to the lead at once, then again with the result.\n" +
		"  -c=          the change; its files must be shelved (see -shelve) and inside the stream.\n" +
		"  -stream=     the Horde stream id. Default: found from the workspace's Perforce stream, walking up virtual streams.\n" +
		"  -template=   the template (id or name) for this run. Default: the saved one (with only -param:, the stream's\n" +
		"               default preflight template).\n" +
		"  -param:<id>= a parameter for this run, over the saved ones (repeatable): bool true|false, list choice ids\n" +
		"               (comma-separated), text. uak horde templates lists them.\n" +
		"  -use-template-defaults  skip the saved settings and the exit-6 check (CI): the template (-template=, else the\n" +
		"               stream's default) with its default parameters.\n" +
		"  -shelve      first shelve the change's opened files, replacing its shelf (p4 shelve -r; nothing is reverted), then\n" +
		"               start a new preflight. The shelf becomes exactly the opened files: shelved files that are no longer\n" +
		"               opened in the change (reverted, or moved out) are deleted from it, so the preflight doesn't build them;\n" +
		"               each is listed (\"removed from the shelf\"), and so are shelved files whose content changed. Refused\n" +
		"               when nothing is opened in the change, and while an auto-submit preflight of the change is running\n" +
		"               (Horde submits the change's CURRENT shelf when it succeeds), checked before shelving and again just\n" +
		"               before. Agents use it only on changelists they created.\n" +
		"  -keep-unopened  with -shelve: p4 shelve -f instead (the behaviour before 0.3.3): shelved files that are no longer\n" +
		"               opened STAY on the shelf, listed as kept; the preflight builds them, and a submit would include them.\n" +
		"  -force       start a new preflight even when an equal one is still running. By default (without -shelve) uak\n" +
		"               reports a still-running preflight of this change instead (\"reused: <job>\") only when uak started it\n" +
		"               (it keeps a record of each), with the same stream, template, parameters and auto-submit setting and\n" +
		"               no other arguments or targets, at least 5 s after the change was last shelved, and the change's shelf\n" +
		"               is still the one uak recorded then.\n" +
		"  -autosubmit  ONLY when the user asked for it: if the preflight succeeds, Horde edits the change's description and\n" +
		"               SUBMITS the change's shelf. Off by default; uak never turns it on by itself. Refused while another\n" +
		"               auto-submit preflight of the change is running (unless it is reused). With -shelve -keep-unopened,\n" +
		"               also refused when the shelf holds files that are not opened in the change (Horde would submit them\n" +
		"               too); plain -shelve deletes those files from the shelf instead.\n" +
		"  -allow-shelved-only  with -shelve -keep-unopened -autosubmit: go ahead although the shelf holds files not opened\n" +
		"               in the change, and submit them too.\n" +
		"  -wait        wait quietly for the result, then print a short summary (failing steps with their URLs).\n" +
		"  -timeout=    with -wait: stop waiting after this many seconds (exit 3); wait again with uak horde job -wait.\n" +
		"  -verbose     with -wait: every step in the summary.\n" +
		"  -issues      with -wait: also the errors and warnings of failed steps and those with warnings (as uak horde\n" +
		"               job -issues).\n" +
		"  -no-open     with -wait: don't open the job's page in the browser this time (see uak horde config -open=).\n" + LoginUsage +
		"  Exit codes: 0 started (with -wait: succeeded), 1 failed or did not complete, 2 usage error, 3 still running after\n" +
		"  -timeout, 4 not signed in (the sign-in failed or timed out, or -no-login), 5 another error (including 403, not\n" +
		"  allowed, and uak's refusals above: an auto-submit preflight of the change is running, or the shelf holds files that\n" +
		"  aren't opened), 6 build settings needed (nothing started), 7 succeeded with warnings.";

	internal override Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		string changeText = arguments.GetRequiredString("c");
		if (!int.TryParse(changeText, NumberStyles.None, CultureInfo.InvariantCulture, out int change) || change <= 0)
		{
			throw new UakUsageException($"-c must be a changelist number, not '{changeText}'.");
		}
		string? template = arguments.GetString("template");
		IReadOnlyList<KeyValuePair<string, string>> parameters = arguments.GetPrefixed("param:");
		bool useTemplateDefaults = arguments.GetFlag("use-template-defaults");
		if (useTemplateDefaults && parameters.Count > 0)
		{
			throw new UakUsageException("-use-template-defaults and -param: don't go together.");
		}
		string? streamId = arguments.GetString("stream");
		bool shelve = arguments.GetFlag("shelve");
		bool force = arguments.GetFlag("force");
		bool autoSubmit = arguments.GetFlag("autosubmit");
		bool wait = arguments.GetFlag("wait");
		TimeSpan? timeout = ParseTimeout(arguments);
		bool verbose = arguments.GetFlag("verbose");
		bool issues = arguments.GetFlag("issues");
		string? server = arguments.GetString("server");
		bool json = arguments.GetFlag("json");
		if (!wait && (timeout is not null || verbose || issues))
		{
			throw new UakUsageException("-timeout, -verbose and -issues go with -wait.");
		}
		LoginOptions login = LoginOptions.Parse(arguments);
		bool noOpen = arguments.GetFlag("no-open");
		bool keepUnopened = arguments.GetFlag("keep-unopened");
		if (keepUnopened && !shelve)
		{
			throw new UakUsageException("-keep-unopened goes with -shelve.");
		}
		bool allowShelvedOnly = arguments.GetFlag("allow-shelved-only");
		if (allowShelvedOnly && !(shelve && autoSubmit))
		{
			throw new UakUsageException("-allow-shelved-only goes with -shelve -keep-unopened -autosubmit.");
		}
		// Since 0.3.3, -shelve replaces the shelf and so deletes the files -allow-shelved-only would submit: a script that asks
		// for them to be submitted must say it wants them kept, rather than see them go.
		if (allowShelvedOnly && !keepUnopened)
		{
			throw new UakUsageException("-allow-shelved-only needs -keep-unopened: -shelve now replaces the shelf (p4 shelve -r), which deletes shelved files that are not opened in the change. " +
				"Add -keep-unopened to keep them and submit them too, or drop -allow-shelved-only to let them go.");
		}
		ShelveMode shelveMode = keepUnopened ? ShelveMode.Update : ShelveMode.Replace;

		return async (context, cancellationToken) =>
		{
			HordeServer found = ResolveServer(server);
			HordeOpenMode open = wait ? ResolveOpen(noOpen) : HordeOpenMode.Never;
			IPreflightWorkspace? workspace = shelve || streamId is null ? await Workspace(context, cancellationToken).ConfigureAwait(false) : null;
			bool workspaceTried = workspace is not null;

			// The workspace when a read needs it and the command didn't so far; null when there is none (then nothing is reused).
			async Task<IPreflightWorkspace?> WorkspaceOrNullAsync()
			{
				if (workspace is null && !workspaceTried)
				{
					workspaceTried = true;
					try
					{
						workspace = await Workspace(context, cancellationToken).ConfigureAwait(false);
					}
					catch (Exception exception) when (exception is UakUsageException or VcsException)
					{
						context.Logger.LogDebug("No Perforce workspace, so uak can't read change {Change}'s shelf: {Message}", change, exception.Message);
					}
				}
				return workspace;
			}

			try
			{
				TextWriter log = json ? TextWriter.Null : Output;
				TextWriter notices = json ? ErrorOutput : Output;
				await using IHordeApi? api = await ConnectAsync(context, found, login, log, cancellationToken).ConfigureAwait(false);
				if (api is null)
				{
					return HordeExitCodes.NotLoggedIn;
				}

				IReadOnlyList<HordeStream> streams = await api.GetStreamsAsync(cancellationToken).ConfigureAwait(false);
				HordeStream stream = await FindStreamAsync(context, streams, streamId, workspace, cancellationToken).ConfigureAwait(false);

				// The build settings: saved, or from the command line; with neither, the user must choose (exit 6).
				HordeSavedBuild? saved = useTemplateDefaults ? null : BuildSettings().Read(found.Url, stream.Id);
				HordeBuildChoice? build = HordeBuildResolver.Resolve(stream, saved, template, parameters, useTemplateDefaults, out string? needed);
				if (build is null)
				{
					return await ReportSettingsNeededAsync(found.Url, stream, saved, needed!, json).ConfigureAwait(false);
				}
				HordeTemplate chosen = build.Template;
				IReadOnlyDictionary<string, string> sent = HordeBuildParameters.ToHorde(chosen, build.Parameters);

				// With -shelve -keep-unopened -autosubmit, shelved files that aren't opened in the change would be submitted with
				// it: p4 shelve -f keeps them on the shelf, and Horde submits the whole shelf. (Plain -shelve, p4 shelve -r, deletes
				// them from the shelf; a file that p4 keeps anyway is caught after shelving.)
				if (shelve && shelveMode == ShelveMode.Update && autoSubmit && !allowShelvedOnly)
				{
					IReadOnlyList<string> shelvedOnly = await workspace!.GetShelvedNotOpenedAsync(change, cancellationToken).ConfigureAwait(false);
					if (shelvedOnly.Count > 0)
					{
						throw new HordeRefusedException(ShelvedOnlyRefusal(change, shelvedOnly, shelved: false, shelveMode));
					}
				}

				// The change's preflights, read before anything is shelved. Horde auto-submits the change's CURRENT shelf when an
				// auto-submit preflight succeeds, whatever shelf that preflight built.
				List<HordeJobSummary> active = (await api.FindPreflightsAsync(change, cancellationToken).ConfigureAwait(false)).Where(job => job.IsActive).ToList();
				List<HordeJobSummary> submitting = active.Where(job => job.AutoSubmit).ToList();
				if (shelve && submitting.Count > 0)
				{
					throw new HordeRefusedException(ShelveRefusal(found.Url, change, submitting));
				}

				// When the change was last shelved and what its shelf holds: to reuse a running preflight, and to tell whether a
				// running auto-submit preflight built this shelf. Unknown (null) means no reuse, and a warning.
				IReadOnlyDictionary<string, string> expected = HordeBuildParameters.Effective(chosen, sent);
				DateTimeOffset? shelvedAt = null;
				IReadOnlyList<PerforceShelfFile>? shelf = null;
				if (!shelve && (!force || submitting.Count > 0) && await WorkspaceOrNullAsync().ConfigureAwait(false) is IPreflightWorkspace reader)
				{
					shelvedAt = await ReadOrNullAsync(context, change, () => reader.GetShelveTimeAsync(change, cancellationToken)).ConfigureAwait(false);
					shelf = await ReadOrNullAsync(context, change, () => reader.GetShelfAsync(change, cancellationToken)).ConfigureAwait(false);
				}
				HordePreflightRecordStore records = PreflightRecords();
				HordeJobSummary? reused = shelve || force ? null
					: active.FirstOrDefault(job => IsReusable(job, records.Read(found.Url, job.Id), change, stream.Id, chosen.Id, expected, autoSubmit, shelvedAt, shelf));

				// Two auto-submit preflights of one change: each would submit the change's current shelf when it succeeds.
				List<HordeJobSummary> otherSubmitting = submitting.Where(job => job.Id != reused?.Id).ToList();
				if (autoSubmit && otherSubmitting.Count > 0)
				{
					throw new HordeRefusedException($"Another auto-submit preflight of change {change} is running ({string.Join(", ", otherSubmitting.Select(job => JobUrl(found.Url, job.Id)))}), " +
						"and uak doesn't reuse it (another request, or uak can't tell that it built the change's current shelf). A second one would also submit the change. Nothing was started. Wait for it to finish, or ask the user.");
				}
				foreach (HordeJobSummary job in otherSubmitting)
				{
					await notices.WriteLineAsync(BuildsCurrentShelf(job, records.Read(found.Url, job.Id), change, shelvedAt, shelf)
						? $"Note: an AUTO-SUBMIT preflight of change {change} is running: {JobUrl(found.Url, job.Id)}. This starts a separate preflight, without auto-submit."
						: $"WARNING: an AUTO-SUBMIT preflight of change {change} is running ({JobUrl(found.Url, job.Id)}), and it was created before the change was last shelved, or uak can't tell. " +
						"When it succeeds, Horde submits the change's CURRENT shelf, which that preflight may not have built. Tell the user (through the lead). This starts a separate preflight, without auto-submit.").ConfigureAwait(false);
				}

				PerforceShelveResult? shelved = null;
				if (shelve)
				{
					// Again just before shelving: an auto-submit preflight started since the first look would submit the new shelf.
					List<HordeJobSummary> late = (await api.FindPreflightsAsync(change, cancellationToken).ConfigureAwait(false)).Where(job => job.IsActive && job.AutoSubmit).ToList();
					if (late.Count > 0)
					{
						throw new HordeRefusedException(ShelveRefusal(found.Url, change, late));
					}
					shelved = await workspace!.ShelveAsync(change, shelveMode, cancellationToken).ConfigureAwait(false);
					string how = shelveMode == ShelveMode.Replace ? "p4 shelve -r: the shelf is now exactly the opened files" : "p4 shelve -f";
					await notices.WriteLineAsync($"Shelved {shelved.Shelved.Count} file(s) in change {change} ({how}).").ConfigureAwait(false);
					foreach (string file in shelved.Replaced)
					{
						await notices.WriteLineAsync($"  replaced on the shelf (its shelved content or action changed): {file}").ConfigureAwait(false);
					}
					foreach (string file in shelved.Removed)
					{
						await notices.WriteLineAsync($"  removed from the shelf (no longer opened in change {change}): {file}").ConfigureAwait(false);
					}
					foreach (string file in shelved.Kept)
					{
						await notices.WriteLineAsync($"  kept on the shelf, but not opened in change {change} (the preflight builds it, and a submit would include it): {file}").ConfigureAwait(false);
					}
					shelf = shelved.Shelf;
					if (autoSubmit && !allowShelvedOnly && shelved.Kept.Count > 0)
					{
						throw new HordeRefusedException(ShelvedOnlyRefusal(change, shelved.Kept, shelved: true, shelveMode));
					}
				}

				string jobId;
				bool jobAutoSubmit;
				if (reused is not null)
				{
					jobId = reused.Id;
					jobAutoSubmit = reused.AutoSubmit;
					await notices.WriteLineAsync($"reused: {jobId} (still running; uak started it with the same stream, template, parameters and auto-submit setting, and change {change}'s shelf hasn't changed since; -force starts another)").ConfigureAwait(false);
				}
				else
				{
					// What the job will build, for uak's record of it: only a recorded job is ever reused.
					if (shelf is null && await WorkspaceOrNullAsync().ConfigureAwait(false) is IPreflightWorkspace shelfReader)
					{
						shelf = await ReadOrNullAsync(context, change, () => shelfReader.GetShelfAsync(change, cancellationToken)).ConfigureAwait(false);
					}
					HordePreflightRequest request = new(stream.Id, chosen.Id, change, autoSubmit, sent);
					jobId = await CreateAsync(api, request, expected, cancellationToken).ConfigureAwait(false);
					jobAutoSubmit = autoSubmit;
					Record(context, records, found.Url, jobId, request, shelf);
				}

				// The job reported here was started with exactly this template and these parameters (a reused one matched them).
				await notices.WriteLineAsync(HordeBuildParameters.Describe(chosen, build.Parameters) + $" ({(reused is null ? build.Source : "reused job")})").ConfigureAwait(false);
				await AnnounceJobAsync(found.Url, jobId, json).ConfigureAwait(false);
				if (jobAutoSubmit)
				{
					await notices.WriteLineAsync($"AUTO-SUBMIT IS ON: if this preflight succeeds, Horde will edit change {change}'s description and SUBMIT it.").ConfigureAwait(false);
				}
				if (!json)
				{
					await Output.WriteLineAsync($"Preflight of change {change}: stream {stream.Id}, template {chosen.Id} ({chosen.Name}), job {jobId}{(reused is null ? "" : " (reused)")}").ConfigureAwait(false);
				}
				if (!wait)
				{
					if (json)
					{
						await WriteJsonAsync(new
						{
							id = jobId,
							url = JobUrl(found.Url, jobId),
							change,
							streamId = stream.Id,
							templateId = chosen.Id,
							parameters = build.Parameters,
							buildSettings = reused is null ? build.Source : "reused job",
							autoSubmit = jobAutoSubmit,
							reused = reused is not null,
							shelved = ShelvedFiles(shelved),
							replaced = shelved?.Replaced,
							removed = shelved?.Removed,
							kept = shelved?.Kept,
						}).ConfigureAwait(false);
					}
					return HordeExitCodes.Success;
				}
				return await ReportJobAsync(context, api, jobId, true, timeout, verbose, json, open, cancellationToken, announce: false, shelved, issues ? new HordeLogOptions() : null).ConfigureAwait(false);
			}
			finally
			{
				workspace?.Dispose();
			}
		};
	}

	/// <summary>
	/// How long after the change's last shelve a preflight must have been created to count as building that shelf: the shelve
	/// time has 1 s resolution, and the Perforce and Horde servers' clocks may differ a little.
	/// </summary>
	public static readonly TimeSpan ShelveTimeMargin = TimeSpan.FromSeconds(5);

	/// <summary>A read of the change's shelf state, or null (logged) when it fails: uak then can't tell, so nothing is reused.</summary>
	static async Task<T?> ReadOrNullAsync<T>(UakContext context, int change, Func<Task<T>> read)
	{
		try
		{
			return await read().ConfigureAwait(false);
		}
		catch (VcsException exception)
		{
			context.Logger.LogDebug("Can't read change {Change}'s shelf, so no running preflight is reused: {Message}", change, exception.Message);
			return default;
		}
	}

	/// <summary>The refusal to shelve under running auto-submit preflights.</summary>
	static string ShelveRefusal(Uri server, int change, IEnumerable<HordeJobSummary> submitting)
		=> $"An auto-submit preflight of change {change} is running ({string.Join(", ", submitting.Select(job => JobUrl(server, job.Id)))}). When it succeeds, Horde submits the change's " +
			"CURRENT shelf, so the change is not shelved again: that would change what Horde submits. Nothing was shelved or started. Wait for it to finish, or ask the user.";

	/// <summary>The refusal to auto-submit a shelf that holds files not opened in the change.</summary>
	static string ShelvedOnlyRefusal(int change, IReadOnlyList<string> files, bool shelved, ShelveMode mode)
		=> $"Change {change}'s shelf holds files that are not opened in it, and an auto-submit would submit them too: {string.Join(", ", files)}. " +
			(shelved ? "The change was shelved; no preflight was started. " : "Nothing was shelved or started. ") +
			(mode == ShelveMode.Update ? "Drop -keep-unopened to delete them from the shelf when shelving (only if that work isn't needed), or pass -allow-shelved-only when the user wants them submitted."
				: "p4 shelve -r should have deleted them; check the shelf with p4 describe -S, and ask the user.");

	/// <summary>Whether a running preflight is the same request: stream, template, auto-submit, every parameter value, and no other arguments or targets.</summary>
	internal static bool IsSameRequest(HordeJobSummary job, string streamId, string templateId, IReadOnlyDictionary<string, string> expected, bool autoSubmit)
	{
		if (!job.StreamId.Equals(streamId, StringComparison.OrdinalIgnoreCase) || !job.TemplateId.Equals(templateId, StringComparison.OrdinalIgnoreCase) || job.AutoSubmit != autoSubmit || job.Parameters is null)
		{
			return false;
		}
		// uak never sends these; a job that has them was started some other way, with a command line uak can't compare.
		if (job.AdditionalArguments is { Count: > 0 } || job.Targets is { Count: > 0 })
		{
			return false;
		}
		foreach ((string id, string value) in expected)
		{
			if (!job.Parameters.TryGetValue(id, out string? actual) || !HordeBuildParameters.SameValue(actual, value))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// Whether a running preflight builds the change's current shelf, as far as uak can tell: it was created at least
	/// <see cref="ShelveTimeMargin"/> after the change was last shelved, and, when uak started it, the shelf uak recorded then
	/// is the current shelf. False when any of that is unknown.
	/// </summary>
	internal static bool BuildsCurrentShelf(HordeJobSummary job, HordePreflightRecord? record, int change, DateTimeOffset? shelvedAt, IReadOnlyList<PerforceShelfFile>? shelf)
	{
		if (shelvedAt is null || job.Created is not DateTimeOffset created || created < shelvedAt.Value + ShelveTimeMargin)
		{
			return false;
		}
		return record is null || (record.Change == change && shelf is { Count: > 0 } && PerforceVersionControl.SameShelf(record.ShelfFiles, shelf));
	}

	/// <summary>
	/// Whether a running preflight may be reported instead of starting another: uak started it (it has a record), it is the same
	/// request, and it builds the change's current shelf (<see cref="BuildsCurrentShelf"/>).
	/// </summary>
	internal static bool IsReusable(HordeJobSummary job, HordePreflightRecord? record, int change, string streamId, string templateId, IReadOnlyDictionary<string, string> expected, bool autoSubmit, DateTimeOffset? shelvedAt, IReadOnlyList<PerforceShelfFile>? shelf)
		=> record is not null && record.Job.Equals(job.Id, StringComparison.Ordinal) && IsSameRequest(job, streamId, templateId, expected, autoSubmit) && BuildsCurrentShelf(job, record, change, shelvedAt, shelf);

	/// <summary>Records a preflight uak started, with the shelf it builds. Without a shelf nothing is recorded (it is never reused); a failure is only logged.</summary>
	void Record(UakContext context, HordePreflightRecordStore records, Uri server, string jobId, HordePreflightRequest request, IReadOnlyList<PerforceShelfFile>? shelf)
	{
		if (shelf is not { Count: > 0 })
		{
			return;
		}
		try
		{
			records.Save(server, new HordePreflightRecord
			{
				Job = jobId,
				Change = request.Change,
				Stream = request.StreamId,
				Template = request.TemplateId,
				AutoSubmit = request.AutoSubmit,
				Parameters = request.Parameters?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? [],
				Shelf = HordePreflightRecordStore.ToRecorded(shelf),
				Created = UtcNow(),
			}, UtcNow());
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogDebug("Could not record preflight {Job}: {Message}", jobId, exception.Message);
		}
	}

	/// <summary>
	/// Exit 6: no usable build settings. Prints why, the templates and their parameters (as uak horde templates -json), and
	/// the command that saves the user's answers. Starts nothing.
	/// </summary>
	async Task<int> ReportSettingsNeededAsync(Uri server, HordeStream stream, HordeSavedBuild? saved, string reason, bool json)
	{
		JsonObject described = HordeBuildParameters.DescribeTemplates(server, stream, saved);
		described["buildSettingsNeeded"] = true;
		described["reason"] = reason;
		if (!json)
		{
			await Output.WriteLineAsync($"Build settings needed: {reason} No preflight was started.").ConfigureAwait(false);
			await Output.WriteLineAsync("The lead asks the user which template and parameters to use (the templates and parameters follow, as").ConfigureAwait(false);
			await Output.WriteLineAsync($"uak horde templates -stream={stream.Id} -json prints them), saves the answers with:").ConfigureAwait(false);
			await Output.WriteLineAsync("  " + HordeBuildParameters.SaveCommand(stream.Id)).ConfigureAwait(false);
			await Output.WriteLineAsync("and runs the preflight again. A worker sends this output to the lead instead of asking the user.").ConfigureAwait(false);
		}
		await WriteNodeAsync(described).ConfigureAwait(false);
		return HordeExitCodes.BuildSettingsNeeded;
	}

	/// <summary>
	/// Starts the preflight. When the request fails without a clear answer (the connection dropped, a time-out, a 5xx), the job
	/// may still have been created, so uak looks for a new job of exactly this request (<see cref="IsSameRequest"/>, with
	/// <paramref name="expected"/>, the parameter values the job reports) before reporting the failure; it never sends the
	/// request twice.
	/// </summary>
	static async Task<string> CreateAsync(IHordeApi api, HordePreflightRequest request, IReadOnlyDictionary<string, string> expected, CancellationToken cancellationToken)
	{
		HashSet<string> before = [.. (await api.FindPreflightsAsync(request.Change, cancellationToken).ConfigureAwait(false)).Select(job => job.Id)];
		try
		{
			return await api.CreatePreflightAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (HordeJobWaiter.IsTransient(exception, cancellationToken))
		{
			HordeJobSummary? created = (await api.FindPreflightsAsync(request.Change, cancellationToken).ConfigureAwait(false))
				.FirstOrDefault(job => !before.Contains(job.Id) && IsSameRequest(job, request.StreamId, request.TemplateId, expected, request.AutoSubmit));
			if (created is not null)
			{
				return created.Id;
			}
			throw;
		}
	}
}

/// <summary><c>uak horde job</c>: a job's state, or a quiet wait for its result.</summary>
public sealed class HordeJobCommand : HordeCommandBase
{
	/// <inheritdoc/>
	public override string Name => "horde job";

	/// <inheritdoc/>
	public override string Summary => "Report a Horde job's result, or wait quietly for it (-wait), with exit codes for agents.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak horde job -id=<job> [-wait [-timeout=<seconds>] [-no-open]] [-verbose] [-issues]\n" +
		"              [-login-timeout=<seconds> | -no-login] [-server=<url>] [-json]\n" +
		"  Prints the result, the job's URL, and only the failing steps (and those with warnings) with their URLs. With -wait,\n" +
		"  \"Job URL: <url>\" comes first, before the wait.\n" +
		"  -wait      wait until the job finishes: quiet (one line when it starts running), polling every 15 s and backing\n" +
		"             off to 60 s while nothing changes. Run it in a background shell; it costs nothing while it waits.\n" +
		"  -timeout=  stop waiting after this many seconds (exit 3): keep it under the shell's limit, then wait again.\n" +
		"  -verbose   every step in the summary.\n" + IssuesUsage +
		"  -no-open   with -wait: don't open the job's page in the browser this time (see uak horde config -open=).\n" + LoginUsage +
		"  Exit codes: 0 succeeded, 1 failed or did not complete, 2 usage error, 3 still running (after -timeout, or without\n" +
		"  -wait; or Horde couldn't be reached before -timeout), 4 not signed in (the sign-in failed or timed out, or\n" +
		"  -no-login), 5 another error (no such job, 403 not allowed...), 7 succeeded with warnings.";

	internal override Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		string jobId = arguments.GetRequiredString("id");
		if (jobId.Any(character => !char.IsLetterOrDigit(character) && character != '-' && character != '_'))
		{
			throw new UakUsageException($"'{jobId}' is not a Horde job id.");
		}
		bool wait = arguments.GetFlag("wait");
		TimeSpan? timeout = ParseTimeout(arguments);
		bool verbose = arguments.GetFlag("verbose");
		bool issues = arguments.GetFlag("issues");
		string? server = arguments.GetString("server");
		bool json = arguments.GetFlag("json");
		if (timeout is not null && !wait)
		{
			throw new UakUsageException("-timeout goes with -wait.");
		}
		LoginOptions login = LoginOptions.Parse(arguments);
		bool noOpen = arguments.GetFlag("no-open");
		return async (context, cancellationToken) =>
		{
			HordeServer found = ResolveServer(server);
			HordeOpenMode open = wait ? ResolveOpen(noOpen) : HordeOpenMode.Never;
			await using IHordeApi? api = await ConnectAsync(context, found, login, json ? TextWriter.Null : Output, cancellationToken).ConfigureAwait(false);
			return api is null ? HordeExitCodes.NotLoggedIn : await ReportJobAsync(context, api, jobId, wait, timeout, verbose, json, open, cancellationToken, issues: issues ? new HordeLogOptions() : null).ConfigureAwait(false);
		};
	}
}

/// <summary><c>uak horde log</c>: a job's errors and warnings from its steps' log events, short enough for an agent to read.</summary>
public sealed class HordeLogCommand : HordeCommandBase
{
	/// <inheritdoc/>
	public override string Name => "horde log";

	/// <inheritdoc/>
	public override string Summary => "Print a Horde job's errors and warnings (from its steps' log events), de-duplicated and capped, without reading whole logs.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak horde log -job=<job> [-step=<id or name>] [-errors | -warnings] [-max=<n>] [-context=<n>] [-save]\n" +
		"              [-login-timeout=<seconds> | -no-login] [-server=<url>] [-json]\n" +
		"  Reads the events Horde found in the steps' logs (each has a severity: error or warning), not the log's text, and\n" +
		"  prints per step one line (outcome, counts, the log's URL), then each distinct event: its line number, how often it\n" +
		"  occurred, its lines (\"N>\") and the log lines before it (\"N|\"). Errors come first. An event an earlier step\n" +
		"  already showed is only counted. Without -step: the failed steps, then those with warnings.\n" +
		"  -step=     a step id, or a step name (exact, else every step whose name contains it; case is ignored).\n" +
		"  -errors    only errors.  -warnings  only warnings.  (Default: both.)\n" +
		"  -max=      distinct events shown per step (default 10).\n" +
		"  -context=  log lines shown before each event (default 2 before errors, none before warnings).\n" +
		"  -save      also save each step's whole log as text, and print where: <state folder>/Logs/horde/<job>/.\n" +
		"             Read or search that file instead of asking again.\n" + LoginUsage +
		"  Exit codes, as uak horde job (the job's result, not the log's): 0 succeeded, 1 failed or did not complete, 2 usage\n" +
		"  error, 3 still running, 4 not signed in, 5 another error (no such job or step, 403 not allowed...), 7 succeeded\n" +
		"  with warnings.";

	internal override Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		string jobId = arguments.GetRequiredString("job");
		if (jobId.Any(character => !char.IsLetterOrDigit(character) && character != '-' && character != '_'))
		{
			throw new UakUsageException($"'{jobId}' is not a Horde job id.");
		}
		string? stepArgument = arguments.GetString("step");
		if (stepArgument is not null && string.IsNullOrWhiteSpace(stepArgument))
		{
			throw new UakUsageException("-step= needs a step id or name.");
		}
		bool errors = arguments.GetFlag("errors");
		bool warnings = arguments.GetFlag("warnings");
		if (errors && warnings)
		{
			throw new UakUsageException("-errors and -warnings don't go together: leave both out for both.");
		}
		int max = arguments.GetInt("max", 1, 1000) ?? HordeLogOptions.DefaultMax;
		int? context = arguments.GetInt("context", 0, 50);
		bool save = arguments.GetFlag("save");
		string? server = arguments.GetString("server");
		bool json = arguments.GetFlag("json");
		LoginOptions login = LoginOptions.Parse(arguments);
		HordeLogOptions options = new(!warnings, !errors, max, context ?? HordeLogOptions.DefaultErrorContext, context ?? 0);

		return async (uakContext, cancellationToken) =>
		{
			HordeServer found = ResolveServer(server);
			await using IHordeApi? api = await ConnectAsync(uakContext, found, login, json ? TextWriter.Null : Output, cancellationToken).ConfigureAwait(false);
			if (api is null)
			{
				return HordeExitCodes.NotLoggedIn;
			}
			HordeJob job = await api.GetJobAsync(jobId, null, cancellationToken).ConfigureAwait(false)
				?? throw new HordeApiException(404, $"Horde returned nothing for job {jobId}.");
			IReadOnlyList<HordeStep> latest = job.LatestSteps;
			List<HordeStep> steps;
			if (stepArgument is not null)
			{
				steps = FindSteps(latest, stepArgument);
				if (steps.Count == 0)
				{
					uakContext.Logger.LogError("{Message}", $"Job {job.Id} has no step '{stepArgument}'. Its steps: {string.Join(", ", latest.Select(step => $"{step.Name} ({step.Id})"))}.");
					return HordeExitCodes.Error;
				}
			}
			else
			{
				bool Is(HordeStep step, string outcome) => step.Outcome.Equals(outcome, StringComparison.OrdinalIgnoreCase);
				steps = [.. latest.Where(step => Is(step, "Failure")), .. options.Warnings ? latest.Where(step => Is(step, "Warnings")) : []];
			}

			string LogPath(HordeStep step) => Path.Combine(uakContext.StateDirectory.FullName, "Logs", "horde", job.Id, $"{FileName(step.Name)}-{step.Id}.log");
			IReadOnlyList<HordeStepIssues> issues = await LogReader.ReadAsync(api, steps, options, save ? LogPath : null, cancellationToken).ConfigureAwait(false);
			HordeJobResult result = job.Result;
			int exitCode = HordeExitCodes.For(result);
			Uri url = api.ServerUrl;
			if (json)
			{
				await WriteNodeAsync(new JsonObject
				{
					["id"] = job.Id,
					["name"] = job.Name,
					["url"] = JobUrl(url, job.Id),
					["state"] = job.State,
					["result"] = result.ToString(),
					["steps"] = HordeLogReader.ToJson(url, job.Id, issues),
				}).ConfigureAwait(false);
				return exitCode;
			}
			await Output.WriteLineAsync(result == HordeJobResult.Running ? $"Still running ({job.State}): {job.Name}" : $"Result: {result}: {job.Name}").ConfigureAwait(false);
			await Output.WriteLineAsync("  " + JobUrl(url, job.Id)).ConfigureAwait(false);
			if (steps.Count == 0)
			{
				string what = options.Warnings ? "failed or has warnings" : "failed";
				await Output.WriteLineAsync($"No step {what}" + (result == HordeJobResult.Running ? " so far." : ".")).ConfigureAwait(false);
				return exitCode;
			}
			await HordeLogReader.WriteTextAsync(Output, url, issues).ConfigureAwait(false);
			return exitCode;
		};
	}

	/// <summary>The steps a -step= names: the step with that id; else those with that name; else those whose name contains it.</summary>
	internal static List<HordeStep> FindSteps(IReadOnlyList<HordeStep> steps, string text)
	{
		List<HordeStep> byId = steps.Where(step => step.Id.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
		if (byId.Count > 0)
		{
			return byId;
		}
		List<HordeStep> byName = steps.Where(step => step.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
		return byName.Count > 0 ? byName : steps.Where(step => step.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
	}

	/// <summary>A step name as a file name: letters, digits, '-', '_' and '.', anything else as '_', at most 80 characters.</summary>
	internal static string FileName(string name)
	{
		string clean = new([.. name.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_')]);
		clean = clean.Trim('.', '_');
		return clean.Length == 0 ? "step" : clean.Length > 80 ? clean[..80] : clean;
	}
}

/// <summary><c>uak horde templates</c>: a stream's preflight templates, their parameters, and the saved build settings.</summary>
public sealed class HordeTemplatesCommand : HordeCommandBase
{
	/// <inheritdoc/>
	public override string Name => "horde templates";

	/// <inheritdoc/>
	public override string Summary => "List a stream's preflight templates with their parameters (types, choices, defaults) and the saved build settings.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak horde templates [-stream=<id>] [-login-timeout=<seconds> | -no-login] [-server=<url>] [-json]\n" +
		"  For the workspace's Horde stream (found as uak horde preflight finds it) or -stream=: each template that allows\n" +
		"  preflights and that you can run, the stream's default first, with its parameters: id, type (bool, list or text),\n" +
		"  label, description, default, a list's choices and whether it takes several (multiSelect), and the saved value\n" +
		"  (current). Then the saved build settings and the uak horde config command that saves new ones. A list's id is\n" +
		"  uak's, made from its label (Horde's lists have none; their choices do); its value is the chosen choice ids.\n" +
		"  The lead uses -json to ask the user (AskUserQuestion), then saves the answers with uak horde config.\n" + LoginUsage +
		"  Exit codes: 0 listed, 4 not signed in, 5 another error.";

	internal override Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		string? server = arguments.GetString("server");
		string? streamId = arguments.GetString("stream");
		bool json = arguments.GetFlag("json");
		LoginOptions login = LoginOptions.Parse(arguments);
		return async (context, cancellationToken) =>
		{
			HordeServer found = ResolveServer(server);
			await using IHordeApi? api = await ConnectAsync(context, found, login, json ? TextWriter.Null : Output, cancellationToken).ConfigureAwait(false);
			if (api is null)
			{
				return HordeExitCodes.NotLoggedIn;
			}
			IReadOnlyList<HordeStream> streams = await api.GetStreamsAsync(cancellationToken).ConfigureAwait(false);
			HordeStream stream = await FindStreamAsync(context, streams, streamId, null, cancellationToken).ConfigureAwait(false);
			HordeSavedBuild? saved = BuildSettings().Read(found.Url, stream.Id);
			if (json)
			{
				await WriteNodeAsync(HordeBuildParameters.DescribeTemplates(found.Url, stream, saved)).ConfigureAwait(false);
				return UakExitCodes.Success;
			}

			await Output.WriteLineAsync($"Stream {stream.Id} ({stream.Name}) on {found.Url}").ConfigureAwait(false);
			await Output.WriteLineAsync(saved?.Template is null
				? "Saved build settings: none (uak horde preflight exits 6 until some are saved)."
				: $"Saved build settings: template {saved.Template}{(saved.Updated is null ? "" : $", saved {saved.Updated}")}").ConfigureAwait(false);
			IReadOnlyList<HordeTemplate> templates = HordeBuildParameters.PreflightTemplates(stream);
			if (templates.Count == 0)
			{
				await Output.WriteLineAsync("  No template here allows preflights and can be run by you.").ConfigureAwait(false);
			}
			foreach (HordeTemplate template in templates)
			{
				bool isDefault = template.Id.Equals(stream.DefaultPreflightTemplate, StringComparison.OrdinalIgnoreCase);
				bool isSaved = template.Id.Equals(saved?.Template, StringComparison.OrdinalIgnoreCase);
				IReadOnlyDictionary<string, string> current = saved?.ParametersFor(template.Id) ?? new Dictionary<string, string>();
				await Output.WriteLineAsync($"{(isDefault ? "*" : " ")} {template.Id}  {template.Name}{(isDefault ? "  (stream default)" : "")}{(isSaved ? "  (saved)" : "")}").ConfigureAwait(false);
				if (template.Description is not null)
				{
					await Output.WriteLineAsync("    " + template.Description.ReplaceLineEndings(" ")).ConfigureAwait(false);
				}
				foreach (HordeTemplateParameter parameter in template.Parameters)
				{
					string kind = parameter.Kind switch
					{
						HordeParameterKind.List => parameter.Style == HordeListStyle.Single ? "list (one)" : "list (several)",
						_ => parameter.Kind.ToString().ToLowerInvariant(),
					};
					string shownDefault = parameter.Kind == HordeParameterKind.Text ? $"\"{parameter.TextDefault}\"" : HordeBuildParameters.DefaultValue(parameter);
					string shownCurrent = current.TryGetValue(parameter.Id, out string? value) ? $", current: {(parameter.Kind == HordeParameterKind.Text ? $"\"{value}\"" : value)}" : "";
					await Output.WriteLineAsync($"    {parameter.Id}  {kind}  {parameter.Label}  default: {shownDefault}{shownCurrent}{(parameter.Description is null ? "" : "  - " + parameter.Description.ReplaceLineEndings(" "))}").ConfigureAwait(false);
					if (parameter.Kind == HordeParameterKind.List)
					{
						await Output.WriteLineAsync("        choices: " + string.Join(", ", parameter.Choices.Select(choice => choice.Text == choice.Id ? choice.Id : $"{choice.Id} ({choice.Text})"))).ConfigureAwait(false);
					}
				}
			}
			await Output.WriteLineAsync("Save: " + HordeBuildParameters.SaveCommand(stream.Id)).ConfigureAwait(false);
			return UakExitCodes.Success;
		};
	}
}

/// <summary><c>uak horde logout</c>: deletes uak's cached Horde access token.</summary>
public sealed class HordeLogoutCommand : HordeCommandBase
{
	/// <inheritdoc/>
	public override string Name => "horde logout";

	/// <inheritdoc/>
	public override string Summary => "Delete uak's saved Horde sign-in (its cached access token) for the server.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak horde logout [-server=<url>]\n" +
		"  Deletes <UAK_HOME>/horde/<server>/token.bin, uak's cached access token. The next horde command signs in again:\n" +
		"  silently when Horde's own refresh token still works, else through the sign-in page. Horde's own token store\n" +
		"  (shared with Horde's other tools) is left alone.\n" +
		"  Exit codes: 0 deleted, or nothing was saved; 5 the file couldn't be deleted.";

	internal override Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		string? server = arguments.GetString("server");
		return async (context, _) =>
		{
			HordeServer found = ResolveServer(server);
			bool had;
			try
			{
				had = TokenCache().Forget(found.Url);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				context.Logger.LogError("Could not delete uak's saved Horde sign-in: {Message}", exception.Message);
				return HordeExitCodes.Error;
			}
			await Output.WriteLineAsync(had ? $"Deleted uak's saved Horde sign-in for {found.Url}." : $"uak has no saved Horde sign-in for {found.Url}.").ConfigureAwait(false);
			return UakExitCodes.Success;
		};
	}
}
