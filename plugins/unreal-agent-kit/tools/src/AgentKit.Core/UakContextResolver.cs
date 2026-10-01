// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Core;

/// <summary>What <see cref="UakContextResolver"/> works from. Every input has a seam, so tests never depend on this machine.</summary>
public sealed class UakResolveOptions
{
	/// <summary>The -project= value: a .uproject file, or a directory holding exactly one. Relative to <see cref="CurrentDirectory"/>.</summary>
	public string? ProjectArgument { get; init; }

	/// <summary>The -engine= value: an engine root or its Engine folder. Relative to <see cref="CurrentDirectory"/>.</summary>
	public string? EngineArgument { get; init; }

	/// <summary>The -vcs= value, passed through to <see cref="UakContext.RequestedVersionControl"/>.</summary>
	public string? VersionControlArgument { get; init; }

	/// <summary>Whether a missing engine is an error (<see cref="IUakCommand.RequiresEngine"/>). When false, the engine may be null.</summary>
	public bool RequireEngine { get; init; } = true;

	/// <summary>Where relative paths start and the search for a .uproject begins.</summary>
	public string CurrentDirectory { get; init; } = Environment.CurrentDirectory;

	/// <summary>Reads an environment variable (UAK_PROJECT, UAK_ENGINE, UAK_HOME, UAK_VCS).</summary>
	public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

	/// <summary>Where EngineAssociation values are looked up; null for the host's (<see cref="EngineAssociationSources.ForHost"/>).</summary>
	public IEngineAssociationSource? AssociationSource { get; init; }

	/// <summary>The user's home directory, under which the fallback state directory lives; null for the real one.</summary>
	public string? UserHomeDirectory { get; init; }

	/// <summary>
	/// Whether the kit may write in a directory; null for a real check, which creates nothing: it writes and deletes a probe
	/// file in the directory, or in its nearest existing ancestor when the directory does not exist yet.
	/// </summary>
	public Func<string, bool>? IsWritable { get; init; }

	/// <summary>The host platform; null for <see cref="UnrealPlatform.Host"/>.</summary>
	public UnrealPlatform? Platform { get; init; }

	/// <summary>The logger the context carries.</summary>
	public ILogger Logger { get; init; } = NullLogger.Instance;
}

/// <summary>
/// Builds a <see cref="UakContext"/>: finds the project, the engine and the state directory, and records how each was found
/// (DESIGN.md, "Resolving the engine and project").
/// </summary>
public static class UakContextResolver
{
	/// <summary>The environment variable naming the project.</summary>
	public const string ProjectVariable = "UAK_PROJECT";

	/// <summary>The environment variable naming the engine.</summary>
	public const string EngineVariable = "UAK_ENGINE";

	/// <summary>The environment variable naming the version-control system.</summary>
	public const string VcsVariable = "UAK_VCS";

	/// <summary>The environment variable that moves the user's kit folder (default ~/.unreal-agent-kit).</summary>
	public const string HomeVariable = "UAK_HOME";

	/// <summary>Provenance key for the project.</summary>
	public const string ProjectKey = "Project";

	/// <summary>Provenance key for the engine.</summary>
	public const string EngineKey = "Engine";

	/// <summary>Provenance key for the state directory.</summary>
	public const string StateKey = "State";

	/// <summary>Provenance key for the requested version-control system.</summary>
	public const string VcsKey = "Vcs";

	/// <summary>
	/// Resolves the whole context. Throws <see cref="UakSetupException"/> when a given path is wrong, the project is ambiguous,
	/// or <see cref="UakResolveOptions.RequireEngine"/> is set and no engine can be found.
	/// </summary>
	public static UakContext Resolve(UakResolveOptions options)
	{
		Dictionary<string, string> Provenance = new(StringComparer.Ordinal);

		(FileInfo? Project, string ProjectHow) = ResolveProject(options);
		Provenance[ProjectKey] = ProjectHow;

		(string? EngineRoot, string EngineHow) = ResolveEngine(options, Project);
		// Canonical, as the project is: the state directory and the editor lock's mutex name derive from it.
		EngineRoot = EngineRoot is null ? null : CanonicalPath.Get(EngineRoot);
		Provenance[EngineKey] = EngineHow;
		if (EngineRoot is null && options.RequireEngine)
		{
			throw new UakSetupException("No engine found: " + EngineHow + ". Pass -engine=<engine root>, or set " + EngineVariable + ".");
		}

		// Not created here: a read-only command (lock status, runs list) leaves no folder behind, and writers create it.
		(DirectoryInfo State, string StateHow) = ResolveStateDirectory(options, Project, EngineRoot);
		Provenance[StateKey] = StateHow;

		string? Vcs = options.VersionControlArgument;
		if (!string.IsNullOrWhiteSpace(Vcs))
		{
			Provenance[VcsKey] = "-vcs argument";
		}
		else
		{
			Vcs = NullIfBlank(options.GetEnvironmentVariable(VcsVariable));
			if (Vcs is not null)
			{
				Provenance[VcsKey] = VcsVariable + " environment variable";
			}
		}

		return new UakContext
		{
			ProjectFile = Project,
			EngineRoot = EngineRoot is null ? null : new DirectoryInfo(EngineRoot),
			StateDirectory = State,
			Logger = options.Logger,
			Provenance = Provenance,
			Platform = options.Platform ?? UnrealPlatform.Host,
			WorkingDirectory = new DirectoryInfo(Path.GetFullPath(options.CurrentDirectory)),
			RequestedVersionControl = Vcs,
		};
	}

	/// <summary>
	/// Finds the project only: -project=, then UAK_PROJECT, then the nearest .uproject walking up from the current directory.
	/// Returns null (with the reason) when there is none. The host uses this before it knows the command, to find the
	/// project's own commands. The project's directory is canonical (<see cref="CanonicalPath"/>): a project reached through a
	/// junction, a link or a mapped drive resolves to the same path, so it shares one state directory and one editor lock.
	/// </summary>
	public static (FileInfo? Project, string How) ResolveProject(UakResolveOptions options)
	{
		(FileInfo? Project, string How) = ResolveProjectAsGiven(options);
		return (Project is null ? null : new FileInfo(Path.Combine(CanonicalPath.Get(Project.DirectoryName!), Project.Name)), How);
	}

	static (FileInfo? Project, string How) ResolveProjectAsGiven(UakResolveOptions options)
	{
		if (!string.IsNullOrWhiteSpace(options.ProjectArgument))
		{
			return (ProjectFromPath(options.ProjectArgument, options.CurrentDirectory, "-project"), "-project argument");
		}
		string? FromEnvironment = NullIfBlank(options.GetEnvironmentVariable(ProjectVariable));
		if (FromEnvironment is not null)
		{
			return (ProjectFromPath(FromEnvironment, options.CurrentDirectory, ProjectVariable), ProjectVariable + " environment variable");
		}

		string Start = Path.GetFullPath(options.CurrentDirectory);
		for (DirectoryInfo? Directory = new(Start); Directory is not null; Directory = Directory.Parent)
		{
			FileInfo[] Found = FindProjectFiles(Directory);
			if (Found.Length > 1)
			{
				throw new UakSetupException($"{Directory.FullName} holds more than one .uproject ({string.Join(", ", Found.Select(File => File.Name))}). Pass -project=<file>.");
			}
			if (Found.Length == 1)
			{
				return (Found[0], "found by walking up from " + Start);
			}
		}
		return (null, "no .uproject in " + Start + " or above it, and neither -project nor " + ProjectVariable + " was given");
	}

	/// <summary>
	/// Reads the .uproject's EngineAssociation: a version ("5.8"), a registered build's GUID, a path, or empty for the engine that
	/// contains the project. Throws <see cref="UakSetupException"/> when the file is not valid JSON.
	/// </summary>
	public static string ReadEngineAssociation(FileInfo projectFile)
	{
		try
		{
			using FileStream Stream = new(projectFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using JsonDocument Document = JsonDocument.Parse(Stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
			return Document.RootElement.ValueKind == JsonValueKind.Object
				&& Document.RootElement.TryGetProperty("EngineAssociation", out JsonElement Value)
				&& Value.ValueKind == JsonValueKind.String
				? (Value.GetString() ?? "").Trim()
				: "";
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new UakSetupException($"Cannot read {projectFile.FullName}: {Error.Message}", Error);
		}
	}

	/// <summary>Whether an EngineAssociation is a path rather than an identifier: it holds a separator, is rooted, or starts with '.'.</summary>
	public static bool IsPathLikeAssociation(string association) =>
		association.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(association) || association.StartsWith('.');

	static (string? Root, string How) ResolveEngine(UakResolveOptions options, FileInfo? project)
	{
		if (!string.IsNullOrWhiteSpace(options.EngineArgument))
		{
			return (EngineFromPath(options.EngineArgument, options.CurrentDirectory, "-engine"), "-engine argument");
		}
		string? FromEnvironment = NullIfBlank(options.GetEnvironmentVariable(EngineVariable));
		if (FromEnvironment is not null)
		{
			return (EngineFromPath(FromEnvironment, options.CurrentDirectory, EngineVariable), EngineVariable + " environment variable");
		}
		if (project is null)
		{
			return (null, "there is no project to take it from, and neither -engine nor " + EngineVariable + " was given");
		}

		string? Containing = EngineLocator.FindContainingEngineRoot(project.DirectoryName!);
		if (Containing is not null)
		{
			return (Containing, "the engine that contains the project");
		}

		string Association = ReadEngineAssociation(project);
		if (Association.Length == 0)
		{
			return (null, $"{project.Name} has an empty EngineAssociation and no engine contains it");
		}
		if (IsPathLikeAssociation(Association))
		{
			string Candidate = Path.GetFullPath(Path.Combine(project.DirectoryName!, Association));
			string? Root = EngineLocator.NormalizeEngineRoot(Candidate);
			return Root is not null
				? (Root, $"EngineAssociation \"{Association}\" in {project.Name}, as a path")
				: (null, $"EngineAssociation \"{Association}\" in {project.Name} is a path, and {Candidate} is not an engine");
		}

		IEngineAssociationSource Source = options.AssociationSource ?? EngineAssociationSources.ForHost();
		List<string> Skipped = [];
		foreach (EngineAssociationMatch Match in Source.Find(Association))
		{
			string? Root = EngineLocator.NormalizeEngineRoot(Match.EngineRoot);
			if (Root is not null)
			{
				return (Root, $"EngineAssociation \"{Association}\" in {project.Name}, from {Match.Source}");
			}
			Skipped.Add($"{Match.EngineRoot} (from {Match.Source}) is not an engine");
		}
		string Reason = $"EngineAssociation \"{Association}\" in {project.Name} is not in {Source.Description}";
		return (null, Skipped.Count == 0 ? Reason : Reason + " as a valid engine: " + string.Join("; ", Skipped));
	}

	static (DirectoryInfo State, string How) ResolveStateDirectory(UakResolveOptions options, FileInfo? project, string? engineRoot)
	{
		Func<string, bool> IsWritable = options.IsWritable ?? CanWrite;
		List<string> Refused = [];
		if (project is not null)
		{
			string Candidate = Path.Combine(project.DirectoryName!, "Saved", "AgentKit");
			if (IsWritable(Candidate))
			{
				return (new DirectoryInfo(Candidate), "the project's Saved folder");
			}
			Refused.Add(Candidate);
		}
		if (engineRoot is not null)
		{
			// A project's state never shares the engine's folder with another project's: each gets its own, by its key.
			string Candidate = project is null
				? Path.Combine(engineRoot, "Engine", "Saved", "AgentKit")
				: Path.Combine(engineRoot, "Engine", "Saved", "AgentKit", "Projects", GetStateKey(project.DirectoryName!));
			if (IsWritable(Candidate))
			{
				return (new DirectoryInfo(Candidate), project is null ? "the engine's Saved folder (no project)" : "the engine's Saved folder, for the project's key " + GetStateKey(project.DirectoryName!));
			}
			Refused.Add(Candidate);
		}

		string UserDirectory = GetUserStateDirectory(options, project?.DirectoryName, engineRoot);
		if (!IsWritable(UserDirectory))
		{
			throw new UakSetupException("Cannot write a state directory: tried " + string.Join(", ", Refused.Append(UserDirectory)) + ".");
		}
		string Home = NullIfBlank(options.GetEnvironmentVariable(HomeVariable)) is null ? "the user's kit folder" : "the " + HomeVariable + " folder";
		string For = project is not null ? "the project's key " + GetStateKey(project.DirectoryName!)
			: engineRoot is not null ? "the engine's key " + GetEngineStateKey(engineRoot)
			: "no engine";
		return (new DirectoryInfo(UserDirectory), $"{Home}, for {For}" + (Refused.Count == 0 ? " (no project or engine to hold it)" : " (cannot write " + string.Join(" or ", Refused) + ")"));
	}

	/// <summary>The user's kit folder: UAK_HOME if set (relative to the current directory), else ~/.unreal-agent-kit.</summary>
	public static string GetUserDirectory(UakResolveOptions options)
	{
		string? Home = NullIfBlank(options.GetEnvironmentVariable(HomeVariable));
		if (Home is not null)
		{
			return Path.GetFullPath(Path.Combine(options.CurrentDirectory, Home));
		}
		string User = options.UserHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return Path.Combine(User, ".unreal-agent-kit");
	}

	/// <summary>
	/// The fallback state directory when neither the project nor the engine can hold it, keyed by what the editor lock covers:
	/// &lt;user kit folder&gt;/State/&lt;project key&gt; with a project, else State/&lt;engine key&gt;, else State/none. Each project
	/// (and each engine without one) gets its own folder, so two locks never share one queue or one holder record.
	/// </summary>
	/// <param name="options">Gives the user's kit folder.</param>
	/// <param name="projectDirectory">The (canonical) project directory, or null for none.</param>
	/// <param name="engineRoot">The (canonical) engine root, or null for none.</param>
	public static string GetUserStateDirectory(UakResolveOptions options, string? projectDirectory, string? engineRoot) =>
		Path.Combine(GetUserDirectory(options), "State",
			projectDirectory is not null ? GetStateKey(projectDirectory) : engineRoot is not null ? GetEngineStateKey(engineRoot) : "none");

	/// <summary>A short, stable key for an engine root (<see cref="GetStateKey"/>).</summary>
	public static string GetEngineStateKey(string engineRoot, bool? caseInsensitivePaths = null) => GetStateKey(engineRoot, caseInsensitivePaths);

	/// <summary>
	/// A short, stable key for a directory (a project or an engine root): the first 16 hex digits of the SHA-256 of its full
	/// path with trailing separators removed, lower-cased where paths are case-insensitive (Windows by default). Pass the
	/// canonical path (<see cref="CanonicalPath.Get"/>), as the resolver does.
	/// </summary>
	public static string GetStateKey(string directory, bool? caseInsensitivePaths = null)
	{
		string Normal = EngineLocator.TrimSeparators(Path.GetFullPath(directory));
		if (caseInsensitivePaths ?? OperatingSystem.IsWindows())
		{
			Normal = Normal.ToLowerInvariant();
		}
		byte[] Hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normal));
		return Convert.ToHexStringLower(Hash)[..16];
	}

	static FileInfo ProjectFromPath(string path, string currentDirectory, string from)
	{
		string Full = Path.GetFullPath(Path.Combine(currentDirectory, path.Trim().Trim('"')));
		if (File.Exists(Full))
		{
			if (!Full.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase))
			{
				throw new UakSetupException($"{from}: {Full} is not a .uproject file.");
			}
			return new FileInfo(Full);
		}
		if (Directory.Exists(Full))
		{
			FileInfo[] Found = FindProjectFiles(new DirectoryInfo(Full));
			return Found.Length switch
			{
				1 => Found[0],
				0 => throw new UakSetupException($"{from}: {Full} holds no .uproject."),
				_ => throw new UakSetupException($"{from}: {Full} holds more than one .uproject ({string.Join(", ", Found.Select(File => File.Name))}). Name the file."),
			};
		}
		throw new UakSetupException($"{from}: {Full} does not exist.");
	}

	static string EngineFromPath(string path, string currentDirectory, string from)
	{
		string Full = Path.GetFullPath(Path.Combine(currentDirectory, path.Trim().Trim('"')));
		return EngineLocator.NormalizeEngineRoot(Full)
			?? throw new UakSetupException($"{from}: {Full} is not an engine: there is no Engine/Build/Build.version in it, and it is not an Engine folder.");
	}

	static FileInfo[] FindProjectFiles(DirectoryInfo directory)
	{
		try
		{
			return directory.GetFiles("*.uproject", SearchOption.TopDirectoryOnly)
				.Where(File => File.Extension.Equals(".uproject", StringComparison.OrdinalIgnoreCase))
				.OrderBy(File => File.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	/// <summary>
	/// Writes and deletes a probe file in the directory, or, when it does not exist yet, in its nearest existing ancestor (where
	/// it would be created). Creates nothing. False when no ancestor exists or the probe cannot be written.
	/// </summary>
	static bool CanWrite(string directory)
	{
		try
		{
			string? Existing = Path.GetFullPath(directory);
			while (Existing is not null && !Directory.Exists(Existing))
			{
				Existing = Path.GetDirectoryName(Existing);
			}
			if (Existing is null)
			{
				return false;
			}
			string Probe = Path.Combine(Existing, ".uak-write-probe-" + Guid.NewGuid().ToString("N"));
			using (new FileStream(Probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
			{
			}
			return true;
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return false;
		}
	}

	static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
