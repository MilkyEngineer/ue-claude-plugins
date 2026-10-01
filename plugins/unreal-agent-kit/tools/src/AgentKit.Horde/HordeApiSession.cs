// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace AgentKit.Horde;

/// <summary>
/// The Horde client a command works with, around the real one. When the client uses uak's cached token and the server answers
/// 401, it deletes that token, signs in afresh (silently, else through the sign-in page) and retries only the request that
/// failed, once: a preflight is never created or shelved twice, and a wait keeps its job and its deadline. It can also
/// rebuild its client (a fresh connection, same sign-in), which <see cref="HordeJobWaiter"/> does after repeated transient
/// failures.
/// </summary>
public sealed class HordeApiSession : IHordeApi
{
	IHordeApi _inner;
	bool _fromCache;
	readonly Func<CancellationToken, Task<IHordeApi?>> _signIn;
	readonly Func<string?, IHordeApi> _rebuild;
	readonly Action _forgetCachedToken;
	readonly ILogger _logger;

	/// <summary>Wraps a client.</summary>
	/// <param name="inner">The client to start with.</param>
	/// <param name="fromCache">Whether it uses uak's cached token, so a 401 means the cached token was refused.</param>
	/// <param name="signIn">Signs in afresh without the cache; null (after saying why) when nobody signed in.</param>
	/// <param name="rebuild">Creates a client with a given token (null: Horde's own token handling).</param>
	/// <param name="forgetCachedToken">Deletes uak's cached token.</param>
	/// <param name="logger">For the one warning about a refused cached token.</param>
	public HordeApiSession(IHordeApi inner, bool fromCache, Func<CancellationToken, Task<IHordeApi?>> signIn, Func<string?, IHordeApi> rebuild, Action forgetCachedToken, ILogger logger)
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

	/// <summary>Replaces the client with a new one carrying the same token: a fresh connection after the old one kept failing.</summary>
	public async Task RebuildAsync(CancellationToken cancellationToken)
	{
		string? token = null;
		try
		{
			token = await _inner.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is HttpRequestException or HordeAuthException or IOException or InvalidOperationException)
		{
			_logger.LogDebug("No token to carry over to a new Horde client: {Message}", exception.Message);
		}
		IHordeApi old = _inner;
		_inner = _rebuild(token);
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
		catch (HordeAuthException exception) when (exception.NotSignedIn && _fromCache)
		{
			// The server refused uak's cached token (revoked, or its signing keys changed): delete it, sign in again, and retry
			// this one request.
			_fromCache = false;
			_forgetCachedToken();
			_logger.LogWarning("Horde refused uak's saved sign-in, so it is deleted; signing in again.");
			IHordeApi? fresh = await _signIn(cancellationToken).ConfigureAwait(false)
				?? throw new HordeAuthException($"Not signed in to Horde at {ServerUrl} after uak's saved sign-in was refused.", notSignedIn: true);
			IHordeApi old = _inner;
			_inner = fresh;
			await old.DisposeAsync().ConfigureAwait(false);
			return await call(_inner).ConfigureAwait(false);
		}
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
