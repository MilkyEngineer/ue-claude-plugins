// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Unreal;

/// <summary>Takes the editor lock around editor and build runs. A seam for tests.</summary>
public interface IEditorLockProvider
{
	/// <summary>Waits in the lock's queue and returns the held lock; disposing it releases the lock.</summary>
	/// <param name="context">The invocation's context (the lock is per project, else per engine).</param>
	/// <param name="name">What the holder is doing, shown by `uak lock status`.</param>
	/// <param name="cancellationToken">Cancels the wait.</param>
	Task<IAsyncDisposable> AcquireAsync(UakContext context, string name, CancellationToken cancellationToken);

	/// <summary>
	/// As <see cref="AcquireAsync(UakContext, string, CancellationToken)"/>; with <paramref name="quiet"/>, takes a quiet hold
	/// (<see cref="QuietRuns"/>) and, once it holds, waits until no <c>uak compile</c> runs.
	/// </summary>
	Task<IAsyncDisposable> AcquireAsync(UakContext context, string name, bool quiet, CancellationToken cancellationToken) =>
		AcquireAsync(context, name, cancellationToken);

	/// <summary>How many others wait in line for the lock (a quiet run refuses to start behind them).</summary>
	int CountWaiting(UakContext context) => 0;
}

/// <summary>
/// The default <see cref="IEditorLockProvider"/>: <see cref="EditorLock"/>. Inside a run the name is prefixed with
/// UAK_LOCK_NAME, and the priority comes from UAK_LOCK_PRIORITY (<see cref="EditorLock.ResolveName"/>, <see cref="EditorLock.ResolvePriority"/>).
/// </summary>
public sealed class EditorLockProvider : IEditorLockProvider
{
	/// <inheritdoc/>
	public async Task<IAsyncDisposable> AcquireAsync(UakContext context, string name, CancellationToken cancellationToken)
	{
		return await EditorLock.AcquireAsync(context, name, priority: null, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<IAsyncDisposable> AcquireAsync(UakContext context, string name, bool quiet, CancellationToken cancellationToken)
	{
		if (!quiet)
		{
			return await AcquireAsync(context, name, cancellationToken).ConfigureAwait(false);
		}
		EditorLockHold Hold = await EditorLock.AcquireAsync(context, name, priority: null, new EditorLockOptions { Quiet = true }, cancellationToken).ConfigureAwait(false);
		try
		{
			await QuietRuns.WaitForCompilesAsync(context, QuietRuns.DefaultPollInterval, QuietRuns.DefaultCompileWaitTimeout, cancellationToken).ConfigureAwait(false);
			return Hold;
		}
		catch
		{
			await Hold.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	/// <inheritdoc/>
	public int CountWaiting(UakContext context) => QuietRuns.GetWaiting(context).Count;
}
