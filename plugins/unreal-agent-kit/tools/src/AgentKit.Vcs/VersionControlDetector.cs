// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using EpicGames.Perforce;
using Microsoft.Extensions.Logging;

namespace AgentKit.Vcs;

/// <summary>The version control found for a workspace, and how it was found (for <c>uak env</c>).</summary>
/// <param name="VersionControl">The implementation. Dispose it when done.</param>
/// <param name="Provenance">How it was chosen, for example "-vcs= argument", ".git directory at C:\Repo" or "P4CONFIG file C:\Ws\.p4config".</param>
public sealed record VcsDetection(IVersionControl VersionControl, string Provenance);

/// <summary>Knobs for <see cref="VersionControlDetector.DetectAsync(DirectoryInfo, VcsDetectionOptions, ILogger, CancellationToken)"/>, mainly for tests.</summary>
public sealed class VcsDetectionOptions
{
	/// <summary>The kind requested with <c>-vcs=</c> ("git", "perforce" or "p4", "none"); null or empty to fall back to <see cref="EnvironmentOverride"/>, then detection.</summary>
	public string? KindOverride { get; init; }

	/// <summary>How <see cref="KindOverride"/> was given, for the provenance; null means "-vcs= argument".</summary>
	public string? KindOverrideSource { get; init; }

	/// <summary>The value of the <c>UAK_VCS</c> environment variable; by default read from the process environment.</summary>
	public string? EnvironmentOverride { get; init; } = Environment.GetEnvironmentVariable(VersionControlDetector.EnvironmentVariable);

	/// <summary>The global Perforce environment; null uses EpicGames.Perforce's default (environment variables, <c>p4 set</c>, P4ENVIRO).</summary>
	public IPerforceEnvironment? PerforceEnvironment { get; init; }

	/// <summary>Creates Perforce connections; null creates a <see cref="P4ProcessConnection"/> that runs <c>p4</c> from PATH with <see cref="PerforceTimeout"/>.</summary>
	public Func<IPerforceSettings, ILogger, IPerforceConnection>? PerforceConnectionFactory { get; init; }

	/// <summary>
	/// How long each p4 command may take, including the <c>p4 info</c> check, before the server is treated as unreachable.
	/// By default <c>UAK_P4_TIMEOUT</c> seconds, else 15 s (<see cref="P4ProcessConnection.GetTimeout"/>).
	/// </summary>
	public TimeSpan PerforceTimeout { get; init; } = P4ProcessConnection.GetTimeout();

	/// <summary>The git program; "git" finds it on PATH.</summary>
	public string GitExecutable { get; init; } = "git";
}

/// <summary>
/// Finds the version control for a workspace (DESIGN.md, "Version control"), in this order:
/// 1. <c>-vcs=</c>, else the <c>UAK_VCS</c> environment variable;
/// 2. a <c>.git</c> directory or file at or above the start directory;
/// 3. a Perforce workspace: a P4CONFIG file at or above it, with <c>p4 info</c> giving the client's root;
/// 4. otherwise None.
/// Global Perforce settings alone (P4PORT or P4CLIENT from the environment or <c>p4 set</c>, with no P4CONFIG file in the
/// tree) do not make a workspace: many machines have them set for some other workspace, and asking the server would cost
/// every <c>uak env</c> a round trip, or a time-out. <c>-vcs=p4</c> still uses them.
/// </summary>
public static class VersionControlDetector
{
	/// <summary>The environment variable that overrides detection, with the same values as <c>-vcs=</c>.</summary>
	public const string EnvironmentVariable = "UAK_VCS";

	/// <summary>
	/// The factory for <c>uak env</c> and the <c>vcs</c> commands: detects from the project directory, else the engine root,
	/// else the working directory. The kind comes from <paramref name="kindOverride"/>, else <see cref="UakContext.RequestedVersionControl"/>
	/// (the global <c>-vcs=</c> or <c>UAK_VCS</c>, already resolved by Core), else detection.
	/// </summary>
	/// <param name="context">The resolved context.</param>
	/// <param name="kindOverride">A command's own <c>-vcs=</c> value, or null.</param>
	/// <param name="cancellationToken">Cancels detection.</param>
	/// <exception cref="VcsException">The kind is unknown, or a forced kind has no workspace here.</exception>
	public static Task<VcsDetection> DetectAsync(UakContext context, string? kindOverride = null, CancellationToken cancellationToken = default)
	{
		DirectoryInfo start = context.ProjectDirectory ?? context.EngineRoot ?? context.WorkingDirectory;
		VcsDetectionOptions options = !string.IsNullOrWhiteSpace(kindOverride)
			? new VcsDetectionOptions { KindOverride = kindOverride, EnvironmentOverride = null }
			: new VcsDetectionOptions
			{
				KindOverride = context.RequestedVersionControl,
				KindOverrideSource = context.Provenance.TryGetValue(UakContextResolver.VcsKey, out string? how) ? how : null,
				EnvironmentOverride = null,
			};
		return DetectAsync(start, options, context.Logger, cancellationToken);
	}

	/// <summary>Detects the version control for a directory.</summary>
	/// <param name="startDirectory">Where to look from, usually the project directory.</param>
	/// <param name="options">Overrides and test hooks.</param>
	/// <param name="logger">Receives diagnostics, such as a failed <c>p4 info</c>.</param>
	/// <param name="cancellationToken">Cancels detection.</param>
	/// <exception cref="VcsException">The override names an unknown kind, or a forced kind has no workspace here.</exception>
	public static async Task<VcsDetection> DetectAsync(DirectoryInfo startDirectory, VcsDetectionOptions options, ILogger logger, CancellationToken cancellationToken = default)
	{
		startDirectory = new DirectoryInfo(Path.GetFullPath(startDirectory.FullName));

		string? forcedSource = null;
		string? forced = null;
		if (!string.IsNullOrWhiteSpace(options.KindOverride))
		{
			(forced, forcedSource) = (options.KindOverride, options.KindOverrideSource ?? "-vcs= argument");
		}
		else if (!string.IsNullOrWhiteSpace(options.EnvironmentOverride))
		{
			(forced, forcedSource) = (options.EnvironmentOverride, $"{EnvironmentVariable} environment variable");
		}

		if (forced is not null)
		{
			VcsKind kind = ParseKind(forced) ?? throw new VcsException($"Unknown version control '{forced}' from the {forcedSource}. Use git, perforce (or p4), or none.");
			switch (kind)
			{
				case VcsKind.None:
					return new VcsDetection(new NoVersionControl(), forcedSource!);
				case VcsKind.Git:
				{
					DirectoryInfo root = FindGitRoot(startDirectory) ?? throw new VcsException($"The {forcedSource} asks for Git, but there is no .git at or above {startDirectory.FullName}.");
					return new VcsDetection(new GitVersionControl(root, options.GitExecutable), $"{forcedSource}, .git at {root.FullName}");
				}
				default:
				{
					VcsDetection? perforce = await DetectPerforceAsync(startDirectory, options, logger, forced: true, cancellationToken).ConfigureAwait(false);
					return perforce is null ? throw new VcsException($"The {forcedSource} asks for Perforce, but no workspace was found for {startDirectory.FullName}.") : perforce with { Provenance = $"{forcedSource}, {perforce.Provenance}" };
				}
			}
		}

		DirectoryInfo? gitRoot = FindGitRoot(startDirectory);
		if (gitRoot is not null)
		{
			return new VcsDetection(new GitVersionControl(gitRoot, options.GitExecutable), $".git at {gitRoot.FullName}");
		}

		PerforceWorkspaceEnvironment environment = new(startDirectory, options.PerforceEnvironment);
		if (environment.ConfigFile is null)
		{
			string reason = environment.HasConnectionSettings
				? "no .git or P4CONFIG file found (global P4PORT/P4CLIENT settings alone are not used; -vcs=p4 uses them)"
				: "no .git or Perforce workspace found";
			return new VcsDetection(new NoVersionControl(), reason);
		}
		VcsDetection? detected = await DetectPerforceAsync(startDirectory, options, logger, forced: false, cancellationToken).ConfigureAwait(false);
		return detected ?? new VcsDetection(new NoVersionControl(), "no .git or Perforce workspace found");
	}

	/// <summary>Parses a <c>-vcs=</c> value (case-insensitive): git, perforce or p4, none. Null when unknown.</summary>
	public static VcsKind? ParseKind(string value) => value.Trim().ToLowerInvariant() switch
	{
		"git" => VcsKind.Git,
		"perforce" or "p4" => VcsKind.Perforce,
		"none" => VcsKind.None,
		_ => null,
	};

	/// <summary>The nearest directory at or above <paramref name="startDirectory"/> holding a <c>.git</c> directory or file (worktrees and submodules use a file).</summary>
	public static DirectoryInfo? FindGitRoot(DirectoryInfo startDirectory)
	{
		for (DirectoryInfo? current = startDirectory; current is not null; current = current.Parent)
		{
			string dotGit = Path.Combine(current.FullName, ".git");
			if (Directory.Exists(dotGit) || File.Exists(dotGit))
			{
				return current;
			}
		}
		return null;
	}

	static async Task<VcsDetection?> DetectPerforceAsync(DirectoryInfo startDirectory, VcsDetectionOptions options, ILogger logger, bool forced, CancellationToken cancellationToken)
	{
		PerforceWorkspaceEnvironment environment = new(startDirectory, options.PerforceEnvironment);

		// Without a P4CONFIG file only a forced kind asks the server (see the class comment).
		if (environment.ConfigFile is null && !forced)
		{
			return null;
		}

		PerforceSettings settings = new(environment);
		Func<IPerforceSettings, ILogger, IPerforceConnection> factory = options.PerforceConnectionFactory ?? ((s, l) => new P4ProcessConnection(s, l, options.PerforceTimeout));
		IPerforceConnection connection = factory(settings, logger);

		// Ask the server for the client's root. A P4CONFIG file is enough on its own, so an unreachable server still counts
		// as Perforce (rooted at the config file); a bare P4PORT/P4CLIENT only counts when the client's root holds the start directory.
		InfoRecord? info = null;
		string? infoFailure = null;
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(options.PerforceTimeout);
			PerforceResponse<InfoRecord> response = await connection.TryGetInfoAsync(InfoOptions.None, timeout.Token).ConfigureAwait(false);
			if (response.Succeeded)
			{
				info = response.Data;
			}
			else
			{
				infoFailure = response.Error is null ? null : VcsPaths.OneLine(response.Error.Data);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			infoFailure = $"p4 info timed out after {options.PerforceTimeout.TotalSeconds:0.#} s";
		}
		catch (Exception exception) when (exception is PerforceException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException or TimeoutException or VcsException)
		{
			infoFailure = VcsPaths.OneLine(exception.Message);
		}
		if (infoFailure is not null)
		{
			logger.LogDebug("Perforce detection: p4 info failed: {Failure}", infoFailure);
		}

		string? clientRoot = IsRealClient(info) ? info!.ClientRoot : null;
		if (clientRoot is not null && VcsPaths.IsUnder(startDirectory.FullName, clientRoot))
		{
			string how = environment.ConfigFile is not null ? $"P4CONFIG file {environment.ConfigFile.FullName}" : "p4 info";
			return new VcsDetection(new PerforceVersionControl(connection, new DirectoryInfo(clientRoot)), $"{how}, client {info!.ClientName} rooted at {clientRoot}");
		}

		if (environment.ConfigFile is not null)
		{
			string reason = clientRoot is not null ? $"client root {clientRoot} does not contain it" : infoFailure ?? "p4 info named no client";
			return new VcsDetection(new PerforceVersionControl(connection, environment.ConfigFile.Directory!) { UnreachableReason = infoFailure }, $"P4CONFIG file {environment.ConfigFile.FullName} ({reason}; root assumed at the config file)");
		}

		if (forced)
		{
			string reason = clientRoot is not null ? $"client root {clientRoot} does not contain it" : infoFailure ?? "p4 info named no client";
			return new VcsDetection(new PerforceVersionControl(connection, startDirectory) { UnreachableReason = infoFailure }, $"p4 settings ({reason}; root assumed at {startDirectory.FullName})");
		}

		connection.Dispose();
		return null;
	}

	/// <summary>p4 info reports "*unknown*" as the client when the settings name none, or one that does not exist.</summary>
	static bool IsRealClient(InfoRecord? info)
		=> info is not null && !string.IsNullOrEmpty(info.ClientRoot) && !string.IsNullOrEmpty(info.ClientName) && info.ClientName != "*unknown*";
}
