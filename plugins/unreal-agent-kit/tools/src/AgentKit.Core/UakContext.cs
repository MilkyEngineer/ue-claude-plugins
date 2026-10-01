// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace AgentKit.Core;

/// <summary>
/// Everything a command needs about where it runs. Created once per invocation by <see cref="UakContextResolver"/>
/// (see DESIGN.md, "Resolving the engine and project").
/// </summary>
public sealed class UakContext
{
	/// <summary>The .uproject file, or null when running against an engine without a project.</summary>
	public FileInfo? ProjectFile { get; init; }

	/// <summary>The project's directory, or null.</summary>
	public DirectoryInfo? ProjectDirectory => ProjectFile?.Directory;

	/// <summary>The engine's root (the directory that contains "Engine"), or null when <see cref="IUakCommand.RequiresEngine"/> is false and none was found.</summary>
	public DirectoryInfo? EngineRoot { get; init; }

	/// <summary>The engine's "Engine" directory, or null.</summary>
	public DirectoryInfo? EngineDirectory => EngineRoot is null ? null : new DirectoryInfo(Path.Combine(EngineRoot.FullName, "Engine"));

	/// <summary>
	/// Where the kit keeps its state (lock queue, run records): &lt;Project&gt;/Saved/AgentKit, else (if writable)
	/// &lt;Engine&gt;/Saved/AgentKit/Projects/&lt;project key&gt; with a project or &lt;Engine&gt;/Saved/AgentKit without one, else
	/// &lt;UAK_HOME or ~/.unreal-agent-kit&gt;/State/&lt;project key, engine key or "none"&gt;. One folder per project, so two
	/// projects never share a lock queue. The resolver has checked that it can write there, but it may not exist yet: writers
	/// create it (and readers cope with its absence).
	/// </summary>
	public required DirectoryInfo StateDirectory { get; init; }

	/// <summary>Logger for the command's output.</summary>
	public required ILogger Logger { get; init; }

	/// <summary>
	/// How each path was found (argument, environment variable, search, association), for `uak env`. Keys are
	/// <see cref="UakContextResolver.ProjectKey"/>, <see cref="UakContextResolver.EngineKey"/>, <see cref="UakContextResolver.StateKey"/>
	/// and, when one was requested, <see cref="UakContextResolver.VcsKey"/>.
	/// </summary>
	public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();

	/// <summary>The platform uak runs on. <see cref="Engine"/> builds tool paths for it.</summary>
	public UnrealPlatform Platform { get; init; } = UnrealPlatform.Host;

	/// <summary>The directory uak was started in.</summary>
	public DirectoryInfo WorkingDirectory { get; init; } = new(Environment.CurrentDirectory);

	/// <summary>The version-control system asked for with -vcs= or UAK_VCS (for example "git", "perforce", "none"), or null to detect it.</summary>
	public string? RequestedVersionControl { get; init; }

	/// <summary>The engine's tool and file paths for <see cref="Platform"/>, or null when there is no engine.</summary>
	public EngineLayout? Engine => EngineRoot is null ? null : new EngineLayout(EngineRoot.FullName, Platform);

	/// <summary>The engine's layout. Throws <see cref="UakSetupException"/> when there is no engine.</summary>
	public EngineLayout RequireEngine() =>
		Engine ?? throw new UakSetupException("This command needs an engine. Pass -engine=<engine root>, or set " + UakContextResolver.EngineVariable + ".");

	/// <summary>The project file. Throws <see cref="UakSetupException"/> when there is no project.</summary>
	public FileInfo RequireProject() =>
		ProjectFile ?? throw new UakSetupException("This command needs a project. Pass -project=<file.uproject>, set " + UakContextResolver.ProjectVariable + ", or run it inside the project.");
}
