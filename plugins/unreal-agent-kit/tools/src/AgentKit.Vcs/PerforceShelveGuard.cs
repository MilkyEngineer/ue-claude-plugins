// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Vcs;

/// <summary>What a <see cref="IPerforceShelveGuard"/> found.</summary>
public enum PerforceShelveGuardVerdict
{
	/// <summary>Nothing stands in the way, or there is nothing to check (for example no Horde server is configured).</summary>
	Clear,

	/// <summary>Shelving now could change what something else is about to do with the shelf: refuse.</summary>
	Refuse,

	/// <summary>The guard could not check (a server can't be reached, nobody is signed in): shelve, with a warning.</summary>
	Unknown,
}

/// <summary>A guard's answer: the verdict, and the message to show for <see cref="PerforceShelveGuardVerdict.Refuse"/> and <see cref="PerforceShelveGuardVerdict.Unknown"/>.</summary>
/// <param name="Verdict">What the guard found.</param>
/// <param name="Message">Why, for the user; null when clear.</param>
public sealed record PerforceShelveGuardResult(PerforceShelveGuardVerdict Verdict, string? Message)
{
	/// <summary>Nothing to report.</summary>
	public static PerforceShelveGuardResult Clear { get; } = new(PerforceShelveGuardVerdict.Clear, null);
}

/// <summary>
/// Checked by <c>uak vcs shelve -c=</c> before it changes an existing changelist's shelf. Implementations live in any assembly
/// the host loads, found by reflection like <see cref="IUakCommand"/>, and need a public parameterless constructor. AgentKit.Horde
/// registers one that refuses while an auto-submit preflight of the changelist runs (Horde submits the changelist's current
/// shelf when it succeeds), so AgentKit.Vcs never references Horde. A guard is best-effort: it never prompts, and it answers
/// <see cref="PerforceShelveGuardVerdict.Unknown"/> rather than throw when it can't check.
/// </summary>
public interface IPerforceShelveGuard
{
	/// <summary>Whether shelving changelist <paramref name="change"/> again is safe now.</summary>
	Task<PerforceShelveGuardResult> CheckAsync(UakContext context, int change, CancellationToken cancellationToken);
}
