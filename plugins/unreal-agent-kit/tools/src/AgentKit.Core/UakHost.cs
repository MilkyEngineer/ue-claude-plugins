// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Reflection;
using Microsoft.Extensions.Logging;

namespace AgentKit.Core;

/// <summary>What the host runs with. Every part has a seam, so tests can run the host in-process.</summary>
public sealed class UakHostOptions
{
	/// <summary>Standard output.</summary>
	public TextWriter Out { get; init; } = Console.Out;

	/// <summary>Standard error.</summary>
	public TextWriter Error { get; init; } = Console.Error;

	/// <summary>Builds the command catalog once the project is known (its directory, or null); null for <see cref="UakCommandCatalog.Load"/> over uak's own folder.</summary>
	public Func<string?, UakCommandCatalog>? LoadCatalog { get; init; }

	/// <summary>Resolver settings other than the global options, which come from the command line (for tests: environment, association source, home).</summary>
	public UakResolveOptions ResolveOptions { get; init; } = new();

	/// <summary>Cancels the command (the host also cancels on Ctrl+C when <see cref="HandleCtrlC"/> is set).</summary>
	public CancellationToken CancellationToken { get; init; }

	/// <summary>Whether Ctrl+C cancels the command instead of killing uak outright. The second Ctrl+C still kills it.</summary>
	public bool HandleCtrlC { get; init; }
}

/// <summary>
/// The `uak` command line (DESIGN.md, "Command line"): `uak [-project=] [-engine=] [-vcs=] [-verbose] &lt;command...&gt; [arguments]`.
/// Global options are read only before the command path; everything after the command path reaches the command verbatim,
/// including "--" and what follows it. Exit codes are <see cref="UakExitCodes"/>.
/// </summary>
public static class UakHost
{
	/// <summary>The global options, as help shows them.</summary>
	public const string GlobalUsage = "uak [-project=<file.uproject|dir>] [-engine=<engine root>] [-vcs=<git|perforce|none>] [-verbose] <command> [arguments]";

	/// <summary>The global options read before the command path.</summary>
	public sealed record GlobalOptions(string? Project, string? Engine, string? Vcs, bool Verbose, bool Help);

	/// <summary>
	/// Splits the command line into the global options and the rest (the command path and its arguments). Throws
	/// <see cref="UakUsageException"/> for an unknown option before the command path.
	/// </summary>
	public static (GlobalOptions Options, IReadOnlyList<string> Remaining) ParseGlobalOptions(IReadOnlyList<string> arguments)
	{
		string? Project = null, Engine = null, Vcs = null;
		bool Verbose = false, Help = false;
		int Index = 0;
		for (; Index < arguments.Count && UakArguments.IsOption(arguments[Index]); Index++)
		{
			string Argument = arguments[Index];
			string Body = Argument.TrimStart('-');
			int Equals = Body.IndexOf('=', StringComparison.Ordinal);
			string Key = Equals < 0 ? Body : Body[..Equals];
			string? Value = Equals < 0 ? null : Body[(Equals + 1)..];
			switch (Key.ToLowerInvariant())
			{
				case "project":
					Project = RequireValue(Key, Value);
					break;
				case "engine":
					Engine = RequireValue(Key, Value);
					break;
				case "vcs":
					Vcs = RequireValue(Key, Value);
					break;
				case "verbose":
					Verbose = true;
					break;
				case "help":
				case "h":
					Help = true;
					break;
				default:
					throw new UakUsageException($"Unknown global option {Argument}. Global options are -project=, -engine=, -vcs= and -verbose, before the command.");
			}
		}
		return (new GlobalOptions(Project, Engine, Vcs, Verbose, Help), arguments.Skip(Index).ToList());
	}

	/// <summary>Runs uak with the given command line and returns the exit code.</summary>
	public static async Task<int> RunAsync(IReadOnlyList<string> arguments, UakHostOptions? options = null)
	{
		options ??= new UakHostOptions();
		TextWriter Out = options.Out;
		TextWriter Err = options.Error;

		GlobalOptions Globals;
		IReadOnlyList<string> Rest;
		try
		{
			(Globals, Rest) = ParseGlobalOptions(arguments);
		}
		catch (UakUsageException Error)
		{
			Err.WriteLine("error: " + Error.Message);
			return UakExitCodes.UsageError;
		}

		ILogger Logger = new UakConsoleLogger(Out, Err, Globals.Verbose ? LogLevel.Debug : LogLevel.Information);
		UakResolveOptions Resolve = WithGlobals(options.ResolveOptions, Globals, Logger);

		// The project decides which project commands exist, so find it before the command.
		string? ProjectDirectory;
		try
		{
			ProjectDirectory = UakContextResolver.ResolveProject(Resolve).Project?.DirectoryName;
		}
		catch (UakSetupException Error)
		{
			bool IsHelp = Globals.Help || Rest.Count == 0 || Rest[0].Equals("help", StringComparison.OrdinalIgnoreCase);
			if (!IsHelp)
			{
				Err.WriteLine("error: " + Error.Message);
				return UakExitCodes.UsageError;
			}
			ProjectDirectory = null;
		}

		UakCommandCatalog Catalog = options.LoadCatalog is not null
			? options.LoadCatalog(ProjectDirectory)
			: UakCommandCatalog.Load(AppContext.BaseDirectory, ProjectDirectory, Resolve.GetEnvironmentVariable);
		UakCommandCatalog.Current = Catalog;
		int ProblemsReported = 0;
		void ReportProblems()
		{
			for (; ProblemsReported < Catalog.Problems.Count; ProblemsReported++)
			{
				Logger.LogWarning("{Problem}", Catalog.Problems[ProblemsReported]);
			}
		}
		ReportProblems();

		bool HelpCommand = Rest.Count > 0 && Rest[0].Equals("help", StringComparison.OrdinalIgnoreCase);
		if (Globals.Help || HelpCommand)
		{
			List<string> Words = (HelpCommand ? Rest.Skip(1) : Rest).ToList();
			bool All = Words.RemoveAll(Word => Word.Equals("-all", StringComparison.OrdinalIgnoreCase)) > 0;
			// Commands outside the kit run code when loaded: only for -all, or to show one the kit does not have.
			if (All || (Words.Count > 0 && Catalog.Match(Words).Command is null && Catalog.WithPrefix(Words).Count == 0))
			{
				Catalog.LoadExternal();
				ReportProblems();
			}
			WriteSkippedProjectCommands(Err, Catalog);
			return WriteHelp(Out, Err, Catalog, Words);
		}
		if (Rest.Count == 0)
		{
			WriteSkippedProjectCommands(Err, Catalog);
			WriteOverview(Err, Catalog);
			return UakExitCodes.UsageError;
		}

		// Kit commands resolve without loading anything else; other command folders load only for a name the kit lacks.
		(IUakCommand? Command, int WordCount) = Catalog.Match(Rest);
		if (Command is null && Catalog.LoadExternal())
		{
			ReportProblems();
			(Command, WordCount) = Catalog.Match(Rest);
		}
		if (Command is null)
		{
			return WriteUnknown(Err, Catalog, Rest);
		}
		if (Command is EnvCommand)
		{
			WriteSkippedProjectCommands(Err, Catalog);
		}
		List<string> CommandArguments = Rest.Skip(WordCount).ToList();
		if (CommandArguments.Count > 0 && IsHelpFlag(CommandArguments[0]))
		{
			WriteCommandHelp(Out, Command);
			return UakExitCodes.Success;
		}

		UakContext Context;
		try
		{
			Context = UakContextResolver.Resolve(new UakResolveOptions
			{
				ProjectArgument = Resolve.ProjectArgument,
				EngineArgument = Resolve.EngineArgument,
				VersionControlArgument = Resolve.VersionControlArgument,
				RequireEngine = Command.RequiresEngine,
				CurrentDirectory = Resolve.CurrentDirectory,
				GetEnvironmentVariable = Resolve.GetEnvironmentVariable,
				AssociationSource = Resolve.AssociationSource,
				UserHomeDirectory = Resolve.UserHomeDirectory,
				IsWritable = Resolve.IsWritable,
				Platform = Resolve.Platform,
				Logger = Logger,
			});
		}
		catch (UakSetupException Error)
		{
			Err.WriteLine("error: " + Error.Message);
			return UakExitCodes.UsageError;
		}

		using CancellationTokenSource Cancel = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
		ConsoleCancelEventHandler? OnCancel = null;
		if (options.HandleCtrlC)
		{
			OnCancel = (_, Args) =>
			{
				if (!Cancel.IsCancellationRequested)
				{
					// The first Ctrl+C cancels the command, which stops what it started; a second one ends uak at once.
					Args.Cancel = true;
					Cancel.Cancel();
				}
			};
			Console.CancelKeyPress += OnCancel;
		}
		try
		{
			return await Command.RunAsync(Context, CommandArguments, Cancel.Token).ConfigureAwait(false);
		}
		catch (UakUsageException Error)
		{
			Err.WriteLine("error: " + Error.Message);
			Err.WriteLine($"Run 'uak help {Command.Name}' for its usage.");
			return UakExitCodes.UsageError;
		}
		catch (OperationCanceledException) when (Cancel.IsCancellationRequested)
		{
			Err.WriteLine("error: cancelled.");
			return UakExitCodes.Failure;
		}
		catch (Exception Error)
		{
			Err.WriteLine("error: " + Error.Message);
			if (Globals.Verbose)
			{
				Err.WriteLine(Error.ToString());
			}
			return UakExitCodes.Failure;
		}
		finally
		{
			if (OnCancel is not null)
			{
				Console.CancelKeyPress -= OnCancel;
			}
		}
	}

	/// <summary>The kit's version, from uak's informational version (or AgentKit.Core's).</summary>
	public static string Version
	{
		get
		{
			Assembly Source = Assembly.GetEntryAssembly() ?? typeof(UakHost).Assembly;
			string? Informational = Source.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			return Informational?.Split('+')[0] ?? Source.GetName().Version?.ToString() ?? "unknown";
		}
	}

	static UakResolveOptions WithGlobals(UakResolveOptions baseOptions, GlobalOptions globals, ILogger logger) => new()
	{
		ProjectArgument = globals.Project ?? baseOptions.ProjectArgument,
		EngineArgument = globals.Engine ?? baseOptions.EngineArgument,
		VersionControlArgument = globals.Vcs ?? baseOptions.VersionControlArgument,
		RequireEngine = baseOptions.RequireEngine,
		CurrentDirectory = baseOptions.CurrentDirectory,
		GetEnvironmentVariable = baseOptions.GetEnvironmentVariable,
		AssociationSource = baseOptions.AssociationSource,
		UserHomeDirectory = baseOptions.UserHomeDirectory,
		IsWritable = baseOptions.IsWritable,
		Platform = baseOptions.Platform,
		Logger = logger,
	};

	static string RequireValue(string key, string? value) =>
		string.IsNullOrWhiteSpace(value) ? throw new UakUsageException($"-{key} needs a value (-{key}=...).") : value;

	static bool IsHelpFlag(string argument) =>
		argument.Equals("-help", StringComparison.OrdinalIgnoreCase) || argument.Equals("--help", StringComparison.OrdinalIgnoreCase)
		|| argument.Equals("-h", StringComparison.OrdinalIgnoreCase) || argument.Equals("-?", StringComparison.Ordinal);

	static int WriteHelp(TextWriter output, TextWriter error, UakCommandCatalog catalog, IReadOnlyList<string> words)
	{
		if (words.Count == 0)
		{
			WriteOverview(output, catalog);
			return UakExitCodes.Success;
		}
		(IUakCommand? Command, int WordCount) = catalog.Match(words);
		if (Command is not null && WordCount == words.Count)
		{
			WriteCommandHelp(output, Command);
			return UakExitCodes.Success;
		}
		IReadOnlyList<IUakCommand> Group = Visible(catalog.WithPrefix(words));
		if (Group.Count > 0)
		{
			WriteCommandList(output, Group);
			return UakExitCodes.Success;
		}
		error.WriteLine($"error: there is no command \"{string.Join(' ', words)}\". Run 'uak help' for the list.");
		return UakExitCodes.UsageError;
	}

	static int WriteUnknown(TextWriter error, UakCommandCatalog catalog, IReadOnlyList<string> words)
	{
		// "uak lock" names a group: list its commands.
		int Longest = 0;
		IReadOnlyList<IUakCommand> Group = [];
		for (int Count = 1; Count <= words.Count && !UakArguments.IsOption(words[Count - 1]); Count++)
		{
			IReadOnlyList<IUakCommand> Candidates = Visible(catalog.WithPrefix(words.Take(Count).ToList()));
			if (Candidates.Count > 0)
			{
				Longest = Count;
				Group = Candidates;
			}
		}
		if (Longest > 0)
		{
			error.WriteLine($"error: \"{string.Join(' ', words.Take(Longest))}\" needs a subcommand:");
			WriteCommandList(error, Group);
		}
		else
		{
			error.WriteLine($"error: unknown command \"{words[0]}\". Run 'uak help' for the list.");
		}
		return UakExitCodes.UsageError;
	}

	static void WriteOverview(TextWriter writer, UakCommandCatalog catalog)
	{
		writer.WriteLine($"uak {Version}: UnrealAgentKit's command line.");
		writer.WriteLine();
		writer.WriteLine("Usage: " + GlobalUsage);
		writer.WriteLine();
		writer.WriteLine("Commands:");
		WriteCommandList(writer, catalog.Commands, includeHelp: true);
		if (!catalog.ExternalLoaded && catalog.ExternalFolders.Count > 0)
		{
			writer.WriteLine($"  (Commands from {string.Join(", ", catalog.ExternalFolders)} are not loaded here: 'uak help -all' lists them.)");
		}
		writer.WriteLine();
		writer.WriteLine("The project is -project=, else UAK_PROJECT, else the nearest .uproject above the current directory.");
		writer.WriteLine("The engine is -engine=, else UAK_ENGINE, else the project's EngineAssociation, as Unreal reads it: a version,");
		writer.WriteLine("  a registered build's GUID or a path names the engine (one that names none is an error); an empty one means the");
		writer.WriteLine("  engine whose folder holds the project.");
		writer.WriteLine("Exit codes: 0 success, 1 failure, 2 usage or setup error (3: 'uak runs wait' timed out). 'uak help <command>' shows a command's usage.");
	}

	/// <summary>One line on stderr when the project has commands that are not loaded because the user has not opted in.</summary>
	static void WriteSkippedProjectCommands(TextWriter error, UakCommandCatalog catalog)
	{
		if (catalog.SkippedProjectCommands is not null)
		{
			error.WriteLine($"note: {catalog.SkippedProjectCommands} holds project commands, which uak does not load unless {UakCommandCatalog.ProjectCommandsVariable}=1 (or the folder is in {UakCommandCatalog.CommandPathsVariable}).");
		}
	}

	/// <summary>The commands listings show: hidden ones (<see cref="IUakCommand.Hidden"/>) still run, and `uak help &lt;name&gt;` still shows them.</summary>
	static List<IUakCommand> Visible(IEnumerable<IUakCommand> commands) => commands.Where(Command => !Command.Hidden).ToList();

	static void WriteCommandList(TextWriter writer, IReadOnlyList<IUakCommand> commands, bool includeHelp = false)
	{
		List<(string Name, string Summary)> Rows = Visible(commands).Select(Command => (Command.Name, Command.Summary)).ToList();
		if (includeHelp)
		{
			Rows.Add(("help [-all] [command]", "Lists the commands (-all: with those from other command folders), or shows one command's usage."));
		}
		int Width = Rows.Count == 0 ? 0 : Rows.Max(Row => Row.Name.Length);
		foreach ((string Name, string Summary) in Rows.OrderBy(Row => Row.Name, StringComparer.OrdinalIgnoreCase))
		{
			writer.WriteLine("  " + Name.PadRight(Width) + "  " + Summary);
		}
	}

	static void WriteCommandHelp(TextWriter writer, IUakCommand command)
	{
		writer.WriteLine($"uak {command.Name}: {command.Summary}");
		writer.WriteLine();
		foreach (string Line in command.Usage.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
		{
			writer.WriteLine(Line.Length == 0 ? "" : "  " + Line);
		}
		if (!command.RequiresEngine)
		{
			writer.WriteLine();
			writer.WriteLine("It runs without an engine.");
		}
	}
}
