// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core;

/// <summary>
/// A `uak` subcommand. Implementations live in any AgentKit.* assembly the `uak` host references; the host finds them by
/// reflection, so adding a command never needs an edit to the host. Each implementation needs a public parameterless constructor.
/// </summary>
public interface IUakCommand
{
	/// <summary>Space-separated command path, lower case, e.g. "env", "lock run", "runs list".</summary>
	string Name { get; }

	/// <summary>One line for `uak help`.</summary>
	string Summary { get; }

	/// <summary>Usage text for `uak help &lt;name&gt;`: arguments and options, one per line.</summary>
	string Usage { get; }

	/// <summary>
	/// Whether the command needs a resolved engine. Commands that only read files under the project (for example `lock status`)
	/// return false and still run when no engine can be found.
	/// </summary>
	bool RequiresEngine => true;

	/// <summary>
	/// Whether `uak help` and partial-path listings leave the command out, for internal commands that uak starts itself
	/// (such as `runs _wrap`). A hidden command still runs, and `uak help &lt;name&gt;` still shows its usage.
	/// </summary>
	bool Hidden => false;

	/// <summary>Runs the command. Returns the process exit code: 0 success, 1 failure, 2 usage or setup error.</summary>
	Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
