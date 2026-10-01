// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace AgentKit.Horde;

/// <summary>
/// The Horde client a command works with, around the real one. When the server answers 401 to a client that uses a token
/// uak handed it (uak's cached token, or the token carried over by a rebuild), it signs in afresh (silently, else through the
/// sign-in page; deleting the cached token first when that was the one refused) and retries only the request that failed,
/// once: a preflight is never created or shelved twice, and a wait keeps its job and its deadline. It can also rebuild its
/// client (a fresh connection), which <see cref="HordeJobWaiter"/> does after repeated transient failures.
/// </summary>
public sealed class HordeApiSession : IHordeApi
{
	IHordeApi _inner;
	bool _fromCache;
	bool _rebuilt;
	readonly Func<TimeSpan?, CancellationToken, Task<IHordeApi?>> _signIn;
	readonly Func<string?, IHordeApi> _rebuild;
	readonly Action _forgetCachedToken;
	readonly ILogger _logger;

	/// <summary>Wraps a client.</summary>
	/// <param name="inner">The client to start with.</param>
	/// <param name="fromCache">Whether it uses uak's cached token, so a 401 means the cached token was refused.</param>
	/// <param name="signIn">
	/// Signs in afresh without the cache, waiting for an interactive sign-in at most the given time (null: the command's own
	/// -login-timeout); null (after saying why) when nobody signed in.
	/// </param>
	/// <param name="rebuild">Creates a client with a given token (null: Horde's own token handling, which refreshes itself).</param>
	/// <param name="forgetCachedToken">Deletes uak's cached token.</param>
	/// <param name="logger">For the one warning about a refused token.</param>
	public HordeApiSession(IHordeApi inner, bool fromCache, Func<TimeSpan?, CancellationToken, Task<IHordeApi?>> signIn, Func<string?, IHordeApi> rebuild, Action forgetCachedToken, ILogger logger)
	{
		_inner = inner;
		_fromCache = fromCache;
		_signIn = signIn;
		_rebuild = rebuild;
		_forgetCachedToken = forgetCachedToken;
		_logger = logger;
	}

	/// <summary>How many times the client was rebuilt; for tests and -verbose.</summary>
	public int Rebuilds { get; private set; }

	/// <summary>
	/// While set, how long a sign-in after a 401 may still take: <see cref="HordeJobWaiter"/> sets it to the time left before
	/// its -timeout, so a sign-in mid-wait never runs past the wait's deadline. Zero or less: no sign-in is tried.
	/// </summary>
	public Func<TimeSpan?>? SignInTimeLimit { get; set; }

	/// <inheritdoc/>
	public Uri ServerUrl => _inner.ServerUrl;

	/// <inheritdoc/>
	public Task<bool> IsLoggedInAsync(CancellationToken cancellationToken) => CallAsync(api => api.IsLoggedInAsync(cancellationToken), cancellationToken);

	/// <inheritdoc/>
	public Task<bool> LoginAsync(CancellationToken cancellationToken) => _inner.LoginAsync(cancellationToken);

	/// <inheritdoc/>
	public Task<bool> CheckSignInAsync(CancellationToken cancellationToken) => _inner.CheckSignInAsync(cancellationToken);

	/// <inheritdoc/>
	public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => _inner.GetAccessTokenAsync(cancellationToken);

	/// <inheritdoc/>
	public Task<IReadOnlyList<HordeStream>> GetStreamsAsync(CancellationToken cancellationToken) => CallAsync(api => api.GetStreamsAsync(cancellationToken), cancellationToken);

	/// <inheritdoc/>
	public Task<IReadOnlyList<HordeJobSummary>> FindPreflightsAsync(int change, CancellationToken cancellationToken) => CallAsync(api => api.FindPreflightsAsync(change, cancellationToken), cancellationToken);

	/// <inheritdoc/>
	/// <remarks>A 401 means the server refused the request before creating anything, so retrying it can't start a second job.</remarks>
	public Task<string> CreatePreflightAsync(HordePreflightRequest request, CancellationToken cancellationToken) => CallAsync(api => api.CreatePreflightAsync(request, cancellationToken), cancellationToken);

	/// <inheritdoc/>
	public Task<HordeJob?> GetJobAsync(string jobId, string? modifiedAfter, CancellationToken cancellationToken) => CallAsync(api => api.GetJobAsync(jobId, modifiedAfter, cancellationToken), cancellationToken);

	/// <summary>
	/// Replaces the client with a new one: a fresh connection after the old one kept failing. A client on uak's cached token
	/// keeps that token; any other gets Horde's own token handling again (which refreshes itself), never a copy of its current
	/// access token, which would expire. Either way, a 401 on the new client signs in again once.
	/// </summary>
	public async Task RebuildAsync(CancellationToken cancellationToken)
	{
		string? token = null;
		if (_fromCache)
		{
			try
			{
				token = await _inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is HttpRequestException or HordeAuthException or IOException or InvalidOperationException)
			{
				_logger.LogDebug("No token to carry over to a new Horde client: {Message}", exception.Message);
			}
			_fromCache = token is not null;
		}
		IHordeApi old = _inner;
		_inner = _rebuild(token);
		_rebuilt = true;
		Rebuilds++;
		_logger.LogDebug("Rebuilt the Horde client after repeated failures.");
		await old.DisposeAsync().ConfigureAwait(false);
	}

	async Task<T> CallAsync<T>(Func<IHordeApi, Task<T>> call, CancellationToken cancellationToken)
	{
		try
		{
			return await call(_inner).ConfigureAwait(false);
		}
		catch (HordeAuthException exception) when (exception.NotSignedIn && (_fromCache || _rebuilt))
		{
			// The server refused a token uak handed the client (uak's cached token, revoked or with changed signing keys; or the
			// sign-in a rebuilt client couldn't refresh): sign in again, and retry this one request.
			if (_fromCache)
			{
				_forgetCachedToken();
				_logger.LogWarning("Horde refused uak's saved sign-in, so it is deleted; signing in again.");
			}
			else
			{
				_logger.LogWarning("Horde refused the sign-in after uak reconnected; signing in again.");
			}
			_fromCache = false;
			_rebuilt = false;
			TimeSpan? limit = SignInTimeLimit?.Invoke();
			if (limit is { } left && left <= TimeSpan.Zero)
			{
				throw new HordeAuthException($"Horde at {ServerUrl} refused the sign-in, and the wait's -timeout passed before uak could sign in again.", notSignedIn: true);
			}
			IHordeApi? fresh = await _signIn(limit, cancellationToken).ConfigureAwait(false)
				?? throw new HordeAuthException($"Not signed in to Horde at {ServerUrl} after it refused the sign-in uak had.", notSignedIn: true);
			IHordeApi old = _inner;
			_inner = fresh;
			await old.DisposeAsync().ConfigureAwait(false);
			return await call(_inner).ConfigureAwait(false);
		}
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
