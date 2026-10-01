// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core;

/// <summary>
/// Adds lines to `uak env`. Implementations live in any assembly the host loads, found by reflection like
/// <see cref="IUakCommand"/>, and need a public parameterless constructor. AgentKit.Vcs reports the version-control system
/// this way, so Core never references it.
/// </summary>
public interface IUakEnvReporter
{
	/// <summary>
	/// Label and value pairs, in order, e.g. ("Version control", "Git at C:\Repo, HEAD 1a2b3c4d (main)"). A reporter that fails
	/// should report the failure as a value rather than throw; an exception is shown as that reporter's error.
	/// </summary>
	Task<IReadOnlyList<KeyValuePair<string, string>>> ReportAsync(UakContext context, CancellationToken cancellationToken);
}
