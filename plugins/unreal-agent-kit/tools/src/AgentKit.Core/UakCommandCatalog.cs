// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AgentKit.Core;

/// <summary>
/// Every <see cref="IUakCommand"/> the host can run, found by reflection (DESIGN.md, "Command line"):
/// <list type="bullet">
/// <item>the kit's own AgentKit.*.dll next to uak, all loaded up front so none is missed because nothing referenced it yet;</item>
/// <item>command assemblies in the folders of <see cref="CommandPathsVariable"/> (separated by the platform's path separator;
/// absolute folders only: a relative one is skipped, with a warning in <see cref="Problems"/>);</item>
/// <item>command assemblies in &lt;Project&gt;/.uak/commands, only when the user opts in (<see cref="ProjectCommandsVariable"/>, or
/// the folder listed in <see cref="CommandPathsVariable"/>), because a repository must not run code just by being cloned.</item>
/// </list>
/// The last two are loaded lazily, by <see cref="LoadExternal"/>: kit commands, `uak help` and `uak env` never run their code.
/// A folder's command assemblies are the DLLs that reference AgentKit.Core; the other DLLs there are their dependencies and
/// load on demand. The first command with a name wins; a later one with the same name is reported in <see cref="Problems"/>.
/// </summary>
public sealed class UakCommandCatalog
{
	/// <summary>The environment variable listing extra command folders.</summary>
	public const string CommandPathsVariable = "UAK_COMMAND_PATHS";

	/// <summary>The project folder, relative to the project directory, whose assemblies add project-specific commands.</summary>
	public static readonly string ProjectCommandsFolder = Path.Combine(".uak", "commands");

	readonly List<IUakCommand> CommandList = [];
	readonly List<Assembly> AssemblyList = [];
	readonly List<string> ProblemList = [];
	readonly List<string> ExternalFolderList = [];

	/// <summary>The catalog the running host loaded, for code that needs the loaded assemblies (such as `uak env`). Null outside the host.</summary>
	public static UakCommandCatalog? Current { get; set; }

	/// <summary>Every command, sorted by name.</summary>
	public IReadOnlyList<IUakCommand> Commands => CommandList;

	/// <summary>The assemblies searched, in load order.</summary>
	public IReadOnlyList<Assembly> Assemblies => AssemblyList;

	/// <summary>Assemblies that failed to load, types that failed to construct, and duplicate command names. The host prints them as warnings.</summary>
	public IReadOnlyList<string> Problems => ProblemList;

	/// <summary>A catalog over the given assemblies only, for tests and for hosts that know their assemblies.</summary>
	public static UakCommandCatalog FromAssemblies(IEnumerable<Assembly> assemblies)
	{
		UakCommandCatalog Catalog = new();
		foreach (Assembly Assembly in assemblies)
		{
			Catalog.Add(Assembly);
		}
		Catalog.Finish();
		return Catalog;
	}

	/// <summary>
	/// Loads the kit's assemblies from <paramref name="baseDirectory"/> (AgentKit.*.dll, and the entry assembly), and records the
	/// command folders without loading them: <paramref name="extraDirectories"/>, the folders in <see cref="CommandPathsVariable"/>,
	/// and &lt;<paramref name="projectDirectory"/>&gt;/.uak/commands when it exists and the user opted in to it
	/// (<see cref="IsProjectCommandsOptIn"/>). <see cref="LoadExternal"/> loads them; until then no code from them runs.
	/// </summary>
	public static UakCommandCatalog Load(string baseDirectory, string? projectDirectory, Func<string, string?>? getEnvironmentVariable = null, IEnumerable<string>? extraDirectories = null)
	{
		getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
		UakCommandCatalog Catalog = new();

		Assembly? Entry = Assembly.GetEntryAssembly();
		if (Entry is not null)
		{
			Catalog.Add(Entry);
		}
		foreach (string File in SafeEnumerate(baseDirectory).Where(File => Path.GetFileName(File).StartsWith("AgentKit.", StringComparison.OrdinalIgnoreCase)).OrderBy(File => File, StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				// By name, so the host's deps.json resolves it into the default context like any referenced assembly.
				Catalog.Add(Assembly.Load(AssemblyName.GetAssemblyName(File)));
			}
			catch (Exception Error) when (Error is IOException or BadImageFormatException)
			{
				Catalog.ProblemList.Add($"Could not load {File}: {Error.Message}");
			}
		}

		List<string> Folders = [.. extraDirectories ?? []];
		string? Paths = getEnvironmentVariable(CommandPathsVariable);
		if (!string.IsNullOrWhiteSpace(Paths))
		{
			foreach (string Folder in Paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				// A relative folder would be found from the current directory: a cloned repository could then opt its own code in.
				if (Path.IsPathFullyQualified(Folder))
				{
					Folders.Add(Folder);
				}
				else
				{
					Catalog.ProblemList.Add($"{CommandPathsVariable}: skipped \"{Folder}\": only absolute folders are loaded.");
				}
			}
		}
		if (projectDirectory is not null)
		{
			string ProjectCommands = Path.GetFullPath(Path.Combine(projectDirectory, ProjectCommandsFolder));
			bool Listed = Folders.Any(Folder => SameFolder(Folder, ProjectCommands));
			if (Directory.Exists(ProjectCommands) && !Listed)
			{
				if (IsProjectCommandsOptIn(getEnvironmentVariable(ProjectCommandsVariable)))
				{
					Folders.Add(ProjectCommands);
				}
				else
				{
					Catalog.SkippedProjectCommands = ProjectCommands;
				}
			}
		}
		Catalog.ExternalFolderList.AddRange(Folders);

		Catalog.Finish();
		return Catalog;
	}

	/// <summary>The environment variable that opts in to &lt;Project&gt;/.uak/commands: "1" or "true".</summary>
	public const string ProjectCommandsVariable = "UAK_PROJECT_COMMANDS";

	/// <summary>Whether a <see cref="ProjectCommandsVariable"/> value opts in: "1" or "true" (case-insensitive).</summary>
	public static bool IsProjectCommandsOptIn(string? value) =>
		value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// The command folders outside the kit (<see cref="CommandPathsVariable"/>, and the project's when opted in), which
	/// <see cref="LoadExternal"/> loads. Loading runs their code (each command's constructor), so the host loads them only
	/// for a command the kit does not have, or for `uak help -all`.
	/// </summary>
	public IReadOnlyList<string> ExternalFolders => ExternalFolderList;

	/// <summary>Whether <see cref="LoadExternal"/> has run.</summary>
	public bool ExternalLoaded { get; private set; }

	/// <summary>
	/// &lt;Project&gt;/.uak/commands when it exists but is not loaded, because neither <see cref="ProjectCommandsVariable"/> nor
	/// <see cref="CommandPathsVariable"/> opted in to it; null otherwise. The host mentions it in `uak help` and `uak env`.
	/// </summary>
	public string? SkippedProjectCommands { get; private set; }

	/// <summary>
	/// Loads the command assemblies in <see cref="ExternalFolders"/>, once. Their commands join the catalog, after the kit's
	/// (a kit command keeps its name). Returns whether this call loaded anything new.
	/// </summary>
	public bool LoadExternal()
	{
		if (ExternalLoaded)
		{
			return false;
		}
		ExternalLoaded = true;
		foreach (string Folder in ExternalFolders)
		{
			LoadFolder(Folder);
		}
		Finish();
		return ExternalFolders.Count > 0;
	}

	static bool SameFolder(string a, string b)
	{
		try
		{
			return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
				OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		}
		catch (Exception Error) when (Error is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	/// <summary>
	/// The command named by the longest run of leading <paramref name="words"/> (case-insensitive), and how many words its name
	/// took. Null when none matches.
	/// </summary>
	public (IUakCommand? Command, int WordCount) Match(IReadOnlyList<string> words)
	{
		IUakCommand? Best = null;
		int BestCount = 0;
		foreach (IUakCommand Command in CommandList)
		{
			string[] Name = SplitName(Command.Name);
			if (Name.Length > BestCount && Name.Length <= words.Count && Name.Select((Word, Index) => Word.Equals(words[Index], StringComparison.OrdinalIgnoreCase)).All(Same => Same))
			{
				Best = Command;
				BestCount = Name.Length;
			}
		}
		return (Best, BestCount);
	}

	/// <summary>The commands whose names start with the given words, such as every "lock ..." command for ["lock"].</summary>
	public IReadOnlyList<IUakCommand> WithPrefix(IReadOnlyList<string> words) => CommandList
		.Where(Command =>
		{
			string[] Name = SplitName(Command.Name);
			return Name.Length > words.Count && words.Select((Word, Index) => Word.Equals(Name[Index], StringComparison.OrdinalIgnoreCase)).All(Same => Same);
		})
		.ToList();

	/// <summary>
	/// Creates one instance of every public, concrete type in the catalog's assemblies that implements <typeparamref name="T"/> and
	/// has a public parameterless constructor, such as every <see cref="IUakEnvReporter"/>.
	/// </summary>
	public IReadOnlyList<T> CreateAll<T>() where T : class
	{
		List<T> Instances = [];
		foreach (Type Type in AssemblyList.SelectMany(GetLoadableTypes).Where(IsCreatable<T>))
		{
			try
			{
				Instances.Add((T)Activator.CreateInstance(Type)!);
			}
			catch (Exception Error) when (Error is TargetInvocationException or MissingMethodException or TypeLoadException)
			{
				ProblemList.Add($"Could not create {Type.FullName}: {(Error.InnerException ?? Error).Message}");
			}
		}
		return Instances;
	}

	/// <summary>A command name as lower-case words.</summary>
	public static string[] SplitName(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	void LoadFolder(string folder)
	{
		if (!Directory.Exists(folder))
		{
			ProblemList.Add($"The command folder {folder} does not exist.");
			return;
		}
		foreach (string File in SafeEnumerate(folder).OrderBy(File => File, StringComparer.OrdinalIgnoreCase))
		{
			if (!ReferencesCore(File))
			{
				continue;
			}
			// An assembly of that name is already loaded: a copy of one of ours (skipped, it must not load twice), or this same
			// file loaded by an earlier catalog in this process (searched again).
			string Name = Path.GetFileNameWithoutExtension(File);
			Assembly? Loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(Assembly => string.Equals(Assembly.GetName().Name, Name, StringComparison.OrdinalIgnoreCase));
			if (Loaded is not null)
			{
				if (!Loaded.IsDynamic && string.Equals(Path.GetFullPath(Loaded.Location), Path.GetFullPath(File), StringComparison.OrdinalIgnoreCase))
				{
					Add(Loaded);
				}
				continue;
			}
			try
			{
				// LoadFrom resolves the assembly's own dependencies from its folder.
				Add(Assembly.LoadFrom(File));
			}
			catch (Exception Error) when (Error is IOException or BadImageFormatException)
			{
				ProblemList.Add($"Could not load {File}: {Error.Message}");
			}
		}
	}

	/// <summary>Whether the DLL references AgentKit.Core, read from its metadata without loading it.</summary>
	static bool ReferencesCore(string file)
	{
		try
		{
			using FileStream Stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using PEReader Reader = new(Stream);
			if (!Reader.HasMetadata)
			{
				return false;
			}
			MetadataReader Metadata = Reader.GetMetadataReader();
			string CoreName = typeof(IUakCommand).Assembly.GetName().Name!;
			return Metadata.AssemblyReferences.Any(Handle => Metadata.GetString(Metadata.GetAssemblyReference(Handle).Name).Equals(CoreName, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception Error) when (Error is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidOperationException)
		{
			return false;
		}
	}

	void Add(Assembly assembly)
	{
		if (AssemblyList.Contains(assembly))
		{
			return;
		}
		AssemblyList.Add(assembly);
		foreach (Type Type in GetLoadableTypes(assembly).Where(IsCreatable<IUakCommand>))
		{
			IUakCommand Command;
			try
			{
				Command = (IUakCommand)Activator.CreateInstance(Type)!;
			}
			catch (Exception Error) when (Error is TargetInvocationException or MissingMethodException or TypeLoadException)
			{
				ProblemList.Add($"Could not create the command {Type.FullName}: {(Error.InnerException ?? Error).Message}");
				continue;
			}
			string Name = string.Join(' ', SplitName(Command.Name));
			IUakCommand? Existing = CommandList.FirstOrDefault(Other => string.Join(' ', SplitName(Other.Name)).Equals(Name, StringComparison.OrdinalIgnoreCase));
			if (Name.Length == 0)
			{
				ProblemList.Add($"The command {Type.FullName} has no name.");
			}
			else if (Existing is not null)
			{
				ProblemList.Add($"The command \"{Name}\" in {Type.Assembly.GetName().Name} is ignored: {Existing.GetType().Assembly.GetName().Name} already has it.");
			}
			else
			{
				CommandList.Add(Command);
			}
		}
	}

	void Finish() => CommandList.Sort((A, B) => string.Compare(A.Name, B.Name, StringComparison.OrdinalIgnoreCase));

	static bool IsCreatable<T>(Type type) =>
		type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && (type.IsPublic || type.IsNestedPublic) && typeof(T).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null;

	static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException Error)
		{
			return Error.Types.OfType<Type>();
		}
	}

	static IEnumerable<string> SafeEnumerate(string directory)
	{
		try
		{
			return Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}
}
