// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Vcs;
using Microsoft.Extensions.Logging;

namespace AgentKit.Horde;

/// <summary>
/// Refuses <c>uak vcs shelve -c=&lt;N&gt;</c> while an auto-submit preflight of N runs on Horde: when it succeeds, Horde
/// submits N's current shelf (its JobService passes the change itself as both the shelved and the original change), so a new
/// shelf would be submitted unbuilt. Best-effort: with no Horde server configured there is nothing to check; it never opens a
/// sign-in page; when Horde can't be asked (not signed in, unreachable, too slow), the answer is Unknown and uak warns.
/// </summary>
public sealed class HordeAutoSubmitShelveGuard : IPerforceShelveGuard
{
	/// <summary>How long the check may take before uak gives up on it (and warns).</summary>
	public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(60);

	/// <summary>Resolves the server and signs in, as the horde commands do; tests point it at fakes.</summary>
	internal HordeCommandBase Connector { get; set; } = new HordeConnector();

	/// <summary>How long the check may take.</summary>
	internal TimeSpan TimeLimit { get; set; } = DefaultTimeLimit;

	/// <inheritdoc/>
	public async Task<PerforceShelveGuardResult> CheckAsync(UakContext context, int change, CancellationToken cancellationToken)
	{
		HordeServer server;
		try
		{
			server = Connector.ResolveServer(null);
		}
		catch (UakUsageException exception) when (exception.Message == HordeServerResolver.NotConfiguredMessage)
		{
			context.Logger.LogDebug("No Horde server is configured, so no auto-submit preflight is checked before shelving change {Change}.", change);
			return PerforceShelveGuardResult.Clear;
		}
		catch (UakUsageException exception)
		{
			return Unknown(change, "the Horde server setting can't be read", exception.Message);
		}

		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(TimeLimit);
		// The connection's own messages (such as "not logged in") are details here: the guard's answer says what matters.
		UakContext quiet = new() { StateDirectory = context.StateDirectory, Logger = new ForwardingLoggerProvider(context.Logger).CreateLogger("guard"), WorkingDirectory = context.WorkingDirectory };
		try
		{
			await using IHordeApi? api = await Connector.ConnectAsync(quiet, server, new HordeCommandBase.LoginOptions(false, TimeSpan.Zero), TextWriter.Null, limit.Token).ConfigureAwait(false);
			if (api is null)
			{
				return Unknown(change, $"not signed in to Horde at {server.Url}", "run uak horde login");
			}
			List<HordeJobSummary> submitting = (await api.FindPreflightsAsync(change, limit.Token).ConfigureAwait(false)).Where(job => job.IsActive && job.AutoSubmit).ToList();
			if (submitting.Count == 0)
			{
				return PerforceShelveGuardResult.Clear;
			}
			return new PerforceShelveGuardResult(PerforceShelveGuardVerdict.Refuse,
				$"An auto-submit Horde preflight of change {change} is running ({string.Join(", ", submitting.Select(job => HordeCommandBase.JobUrl(server.Url, job.Id)))}). " +
				"When it succeeds, Horde submits the change's CURRENT shelf, so shelving now would change what it submits. Nothing was shelved. Wait for it to finish (uak horde job -id=<job> -wait), or ask the user.");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			return Unknown(change, $"Horde at {server.Url} could not be asked", limit.IsCancellationRequested ? $"no answer within {TimeLimit.TotalSeconds:0} s" : exception.Message);
		}
	}

	static PerforceShelveGuardResult Unknown(int change, string why, string detail)
		=> new(PerforceShelveGuardVerdict.Unknown, $"Could not check for a running auto-submit Horde preflight of change {change}: {why} ({detail}). Shelving anyway; if one is running, Horde will submit the new shelf when it succeeds.");
}

/// <summary>The horde commands' server resolution and sign-in, for code that isn't a command (<see cref="HordeAutoSubmitShelveGuard"/>). Not a command itself.</summary>
internal sealed class HordeConnector : HordeCommandBase
{
	public override string Name => "horde _connect";

	public override string Summary => "";

	public override string Usage => "";

	internal override Func<UakContext, CancellationToken, Task<int>> Parse(UakArguments arguments) => throw new NotSupportedException("HordeConnector is not a command.");
}
