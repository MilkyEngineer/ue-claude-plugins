// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>What a cooked test run builds, cooks, stages and runs (see <see cref="CookedTestCommandLine"/>).</summary>
public sealed record CookedTestRun
{
	/// <summary>The .uproject.</summary>
	public required string ProjectFile { get; init; }

	/// <summary>The test filter, as for the editor (several joined with '+'); check it with <see cref="CookedTestCommandLine.CheckFilter"/>.</summary>
	public required string Filter { get; init; }

	/// <summary>The platform the game is built for and runs on: the host.</summary>
	public required UnrealPlatform Platform { get; init; }

	/// <summary>The game's configuration (-clientconfig): Debug, DebugGame, Development or Test.</summary>
	public string Configuration { get; init; } = "Development";

	/// <summary>The maps to cook, the first of which the game opens (-map=A+B), or none for the project's defaults.</summary>
	public IReadOnlyList<string> Maps { get; init; } = [];

	/// <summary>Reuse the game's binaries: no -build.</summary>
	public bool SkipBuild { get; init; }

	/// <summary>Reuse the staged build: -skipcook -skipstage instead of -cook -stage.</summary>
	public bool SkipCook { get; init; }

	/// <summary>How long the game may run before UAT stops it (-RunTimeoutSeconds).</summary>
	public int TimeoutSeconds { get; init; } = CookedTestCommandLine.DefaultTimeoutSeconds;

	/// <summary>More game arguments (-addcmdline); check them with <see cref="CookedTestCommandLine.CheckPassThrough"/>.</summary>
	public IReadOnlyList<string> GameArguments { get; init; } = [];

	/// <summary>
	/// More cooker arguments, from <see cref="CookedTestCommandLine.ParseCookerOptions"/> and
	/// <see cref="CookedTestCommandLine.ResolveCookDirs"/>: the -COOKDIR= folders go through -cookdir=, the rest through
	/// -AdditionalCookerOptions. Not passed with <see cref="SkipCook"/>, as no cook runs.
	/// </summary>
	public IReadOnlyList<string> CookerOptions { get; init; } = [];

	/// <summary>UAT's log folder for this run (uebp_LogFolder), where it writes Client.log and its own Log.txt.</summary>
	public required string LogFolder { get; init; }
}

/// <summary>
/// Builds the UAT command that runs automation tests in a cooked game: BuildCookRun through the engine's RunUAT script,
/// with UAT's log folder set for the run, so the game's log is a known file.
/// </summary>
public static class CookedTestCommandLine
{
	/// <summary>The -RunTimeoutSeconds when none is given: half an hour.</summary>
	public const int DefaultTimeoutSeconds = 1800;

	/// <summary>
	/// The environment variable UAT takes its log folder from (CommandEnvironment.cs, EnvVarNames.LogFolder). An installed
	/// engine's UAT writes Client.log straight into it; a source engine's writes it to a temporary folder and copies it back
	/// when the run ends (RunProjectCommand.Automation.cs, GetLogFolderOutsideOfSandbox and CopyLogsBackToLogFolder).
	/// </summary>
	public const string LogFolderVariable = "uebp_LogFolder";

	/// <summary>
	/// The configurations a cooked test run can use. Shipping has no automation worker (WITH_AUTOMATION_WORKER is
	/// !UE_BUILD_SHIPPING in Build.h), so its game could never run a test.
	/// </summary>
	public static IReadOnlyList<string> Configurations { get; } = ["Debug", "DebugGame", "Development", "Test"];

	/// <summary>
	/// Game options a pass-through after "--" may not give: uak and UAT set -ExecCmds, -testexit and -abslog (a second one
	/// would be ignored, or would change which tests run, when the game quits, or which log uak reads), and -ReportExportPath
	/// makes a cooked game log an error, as it has no HTML report template.
	/// </summary>
	static readonly string[] ReservedOptions = ["ExecCmds", "testexit", "abslog", "ReportExportPath"];

	/// <summary>The BuildCookRun option that carries the cooker arguments other than -COOKDIR=.</summary>
	public const string AdditionalCookerOptionsPrefix = "-AdditionalCookerOptions=";

	/// <summary>The BuildCookRun option that carries the -COOKDIR= folders, separated by '+' (BuildCookRun.Automation.cs).</summary>
	public const string CookDirListPrefix = "-cookdir=";

	/// <summary>
	/// The cooker switch that names a folder to cook. The cook commandlet takes a switch as the token after one '-', and reads
	/// COOKDIR= from its start, ignoring case (FCommandLine::Parse; CookCommandlet.cpp), so only this spelling counts.
	/// </summary>
	public const string CookDirPrefix = "-COOKDIR=";

	/// <summary>Whether a cooker argument is a -COOKDIR=, as the cooker reads one: one '-', ignoring case.</summary>
	public static bool IsCookDir(string option) => option.StartsWith(CookDirPrefix, StringComparison.OrdinalIgnoreCase);

	/// <summary>The cooker switches that name folders, which the cooker reads only as -&lt;name&gt;=: other spellings are refused.</summary>
	static readonly string[] FolderSwitches = ["COOKDIR", "NEVERCOOKDIR"];

	/// <summary>
	/// The characters a long package name can't hold (INVALID_LONGPACKAGE_CHARACTERS, NameTypes.h), and '+', which separates
	/// BuildCookRun's -cookdir= folders: a package path part holding one is refused.
	/// </summary>
	const string InvalidPackagePathCharacters = "\\:*?\"<>|' ,.&!~\n\r\t@#+";

	/// <summary>
	/// Cooker options -cooker-options= may not give: UAT starts the cook commandlet with -run=Cook, -TargetPlatform= and -abslog=
	/// (CommandletUtils.cs), and a second one would run another commandlet, cook for another platform, or move the log UAT reads.
	/// </summary>
	static readonly string[] ReservedCookerOptions = ["run", "TargetPlatform", "abslog"];

	/// <summary>Client.log in a run's log folder: the game's whole log, which uak judges.</summary>
	public static string ClientLogFile(string logFolder) => Path.Combine(logFolder, "Client.log");

	/// <summary>UAT's own log in a run's log folder.</summary>
	public static string UatLogFile(string logFolder) => Path.Combine(logFolder, "Log.txt");

	/// <summary>
	/// The BuildCookRun invocation: build (unless <see cref="CookedTestRun.SkipBuild"/>), cook and stage (or reuse the staged
	/// build), pak, then run the game with -nullrhi and "Automation RunTests &lt;filter&gt;" until the queue empties or the
	/// timeout ends it.
	/// </summary>
	/// <exception cref="UakSetupException">The engine has no RunUAT script.</exception>
	public static ProcessInvocation Build(EngineLayout paths, CookedTestRun run)
	{
		string Script = paths.RunUatScript;
		if (!File.Exists(Script))
		{
			throw new UakSetupException("RunUAT script not found: " + Script);
		}
		string Project = Path.GetFullPath(run.ProjectFile);
		List<string> Arguments =
		[
			"BuildCookRun",
			"-project=" + Project,
			"-platform=" + run.Platform.Name,
			"-clientconfig=" + run.Configuration,
			"-unattended",
			"-nop4",
			"-utf8output",
		];
		if (!run.SkipBuild)
		{
			Arguments.Add("-build");
		}
		// -skipstage still runs the stage step, which reuses the staged files (and paks them again with -pak).
		Arguments.AddRange(run.SkipCook ? ["-skipcook", "-skipstage", "-pak"] : ["-cook", "-stage", "-pak"]);
		if (run.Maps.Count > 0)
		{
			Arguments.Add("-map=" + string.Join('+', run.Maps));
		}
		if (!run.SkipCook)
		{
			// The -COOKDIR= folders go through BuildCookRun's own -cookdir=<folder>+<folder>, which gives the cook commandlet one
			// -CookDir="<folder>" each, quoted (CommandletUtils.cs), so a folder may hold a space, but not a '+'.
			string[] CookDirs = [.. run.CookerOptions.Where(IsCookDir).Select(Option => Option[CookDirPrefix.Length..])];
			if (CookDirs.Length > 0)
			{
				if (CookDirs.Any(Folder => Folder.Length == 0 || Folder.Contains('+', StringComparison.Ordinal)))
				{
					throw new ArgumentException("A -COOKDIR= folder is empty or holds a '+': " + string.Join(", ", CookDirs), nameof(run));
				}
				Arguments.Add(CookDirListPrefix + string.Join('+', CookDirs));
			}
			// The other switches go as one argument: RunUAT.bat forwards it quoted, UAT reads it whole and adds it to the cook
			// commandlet's command line as it stands (CookCommand.Automation.cs), where the cooker reads each switch on its own.
			string[] Others = [.. run.CookerOptions.Where(Option => !IsCookDir(Option))];
			if (Others.Length > 0)
			{
				Arguments.Add(AdditionalCookerOptionsPrefix + string.Join(' ', Others));
			}
		}
		Arguments.AddRange(
		[
			"-run",
			"-nullrhi",
			"-RunAutomationTest=" + run.Filter,
			"-RunTimeoutSeconds=" + run.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
		]);
		if (run.GameArguments.Count > 0)
		{
			Arguments.Add("-addcmdline=" + AddCmdLine(run.GameArguments));
		}
		return new ProcessInvocation(Script, Arguments, Path.GetDirectoryName(Project),
			new Dictionary<string, string?> { [LogFolderVariable] = Path.GetFullPath(run.LogFolder) });
	}

	/// <summary>
	/// The -addcmdline value for the game arguments: joined with spaces, and ending with one, because UAT appends
	/// -testexit= straight after it (RunProjectCommand.Automation.cs). An argument with white space is quoted with single
	/// quotes, which UAT turns into double quotes (ProjectParams.cs): -Key='a b' reaches the game as -Key="a b".
	/// </summary>
	internal static string AddCmdLine(IReadOnlyList<string> gameArguments)
	{
		StringBuilder Result = new();
		foreach (string Argument in gameArguments)
		{
			if (!Argument.Any(char.IsWhiteSpace))
			{
				Result.Append(Argument);
			}
			else
			{
				int Equals = Argument.IndexOf('=', StringComparison.Ordinal);
				bool KeyValue = Argument.StartsWith('-') && Equals > 1 && !Argument[..Equals].Any(char.IsWhiteSpace);
				Result.Append(KeyValue ? $"{Argument[..(Equals + 1)]}'{Argument[(Equals + 1)..]}'" : $"'{Argument}'");
			}
			Result.Append(' ');
		}
		return Result.ToString();
	}

	/// <summary>
	/// Checks the game arguments given after "--" and returns them unchanged. Rejected: options uak or UAT sets (see
	/// <see cref="ReservedOptions"/>), as -Name, --Name or /Name in any case; quotes, which -addcmdline can't carry (UAT turns
	/// ' into "); and % ! &amp; | &lt; &gt; ^ and control characters, because RunUAT.bat strips the quotes from each argument
	/// and expands it unquoted while it scans them, so cmd would run, redirect or expand them. The same rules hold on every
	/// platform.
	/// </summary>
	/// <exception cref="UakUsageException">An argument is one uak sets, or holds a character it can't pass.</exception>
	public static IReadOnlyList<string> CheckPassThrough(IReadOnlyList<string> arguments)
	{
		foreach (string Argument in arguments)
		{
			if (Argument.Length == 0 || Argument.Any(IsUnsafeArgumentCharacter))
			{
				throw new UakUsageException($"game argument '{Argument}' can't go through RunUAT's -addcmdline: no quotes, % ! & | < > ^ or control characters (white space is fine)");
			}
			if (Argument.StartsWith('-') || Argument.StartsWith('/'))
			{
				string Name = UbtCommandLine.OptionName(Argument);
				if (ReservedOptions.Any(Reserved => Reserved.Equals(Name, StringComparison.OrdinalIgnoreCase)))
				{
					throw new UakUsageException($"{Argument} can't go after -- in a cooked run: uak and UAT set -ExecCmds, -testexit and -abslog, and -ReportExportPath makes a cooked game log an error.");
				}
			}
		}
		return arguments;
	}

	/// <summary>
	/// Checks a filter for a cooked run: the editor's rules (<see cref="EditorTestCommandLine.CheckFilter"/>), and none of
	/// % ! &amp; &lt; &gt;, nor ^ except as the first character of a '+' part (the engine's "match from the start"), because
	/// the filter passes through RunUAT.bat as the other arguments do (see <see cref="CheckPassThrough"/>).
	/// </summary>
	/// <exception cref="UakUsageException">The filter holds a character uak can't pass.</exception>
	public static void CheckFilter(string filter)
	{
		EditorTestCommandLine.CheckFilter(filter);
		bool PartStart = true;
		foreach (char C in filter)
		{
			if (C is '%' or '!' or '&' or '<' or '>' || (C == '^' && !PartStart))
			{
				throw new UakUsageException($"-filter='{filter}' can't go through RunUAT: no % ! & < >, and ^ only at the start of a '+' part");
			}
			PartStart = C == '+';
		}
	}

	/// <summary>The configuration named by -config= (any case), in UAT's spelling.</summary>
	/// <exception cref="UakUsageException">It is Shipping, or not a configuration.</exception>
	public static string ParseConfiguration(string name)
	{
		if (name.Equals("Shipping", StringComparison.OrdinalIgnoreCase))
		{
			throw new UakUsageException("-config=Shipping can't run tests: a Shipping game has no automation worker (Debug, DebugGame, Development or Test)");
		}
		return Configurations.FirstOrDefault(Configuration => Configuration.Equals(name, StringComparison.OrdinalIgnoreCase))
			?? throw new UakUsageException($"unknown -config={name} (Debug, DebugGame, Development or Test)");
	}

	/// <summary>The maps of -map=A+B: package names such as /Game/Maps/Main or /Engine/Maps/Entry.</summary>
	/// <exception cref="UakUsageException">A part is empty or holds a character other than letters, digits and _ - / .</exception>
	public static IReadOnlyList<string> ParseMaps(string? maps)
	{
		if (maps is null)
		{
			return [];
		}
		string[] Parts = maps.Split('+');
		foreach (string Part in Parts)
		{
			if (Part.Length == 0 || !Part.All(C => char.IsAsciiLetterOrDigit(C) || C is '_' or '-' or '/' or '.'))
			{
				// Git Bash (MSYS) turns an argument's "=/Game/..." into a Windows path before uak sees it.
				string Hint = maps.Contains(':', StringComparison.Ordinal) ? " (a shell turned it into a file path: from Git Bash, run with MSYS_NO_PATHCONV=1)" : "";
				throw new UakUsageException($"-map={maps} must be map package names joined with '+', e.g. /Game/Maps/Main+/Engine/Maps/Entry{Hint}");
			}
		}
		return Parts;
	}

	/// <summary>
	/// The cooker arguments of -cooker-options= (or the saved default): switches separated by spaces, such as
	/// <c>-COOKDIR=/TestContent/Golden -COOKDIR=...</c>. Empty, or only spaces, is none. Each must be a switch (a '-' and a
	/// letter), and none may be -run, -TargetPlatform or -abslog, which UAT sets for the cook commandlet, or hold abslog= at all.
	/// Rejected as the game arguments after "--" are (<see cref="CheckPassThrough"/>): quotes, % ! &amp; | &lt; &gt; ^ and
	/// control characters, because the value goes through RunUAT.bat, which expands it unquoted while it scans its arguments;
	/// and white space other than a plain space, since spaces alone separate the arguments, so no argument can hold one.
	/// -COOKDIR= and -NEVERCOOKDIR= must be spelt so (one '-', a value), as the cooker ignores any other spelling, and a
	/// -COOKDIR= names one folder, without a '+' (<see cref="CookDirListPrefix"/>).
	/// </summary>
	/// <param name="value">The value as given.</param>
	/// <param name="source">What gave it, for the refusal: "-cooker-options", or the saved setting.</param>
	/// <exception cref="UakUsageException">The value holds a character uak can't pass, or an argument that isn't a switch, or one UAT sets.</exception>
	public static IReadOnlyList<string> ParseCookerOptions(string value, string source)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (value.Any(C => IsUnsafeArgumentCharacter(C) || (char.IsWhiteSpace(C) && C != ' ')))
		{
			throw new UakUsageException($"{source}='{value}' can't go through RunUAT's -AdditionalCookerOptions: no quotes, % ! & | < > ^, tabs or control characters;"
				+ " arguments are separated by spaces, so none can hold a space");
		}
		string[] Options = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		foreach (string Option in Options)
		{
			string Name = UbtCommandLine.OptionName(Option);
			if (!Option.StartsWith('-') || Name.Length == 0 || !char.IsLetter(Name[0]))
			{
				throw new UakUsageException($"{source}: '{Option}' isn't a cooker switch: each argument starts with '-' and a letter, e.g. -COOKDIR=<folder>");
			}
			// The commandlet reads -abslog= with FParse::Value, which finds it anywhere outside quotes, and UAT's own comes after
			// these: a switch that holds abslog= at all could move the log UAT reads.
			if (ReservedCookerOptions.Any(Reserved => Reserved.Equals(Name, StringComparison.OrdinalIgnoreCase))
				|| Option.Contains("abslog=", StringComparison.OrdinalIgnoreCase))
			{
				throw new UakUsageException($"{source}: {Option} can't be given: UAT starts the cook commandlet with -run=Cook, -TargetPlatform= and -abslog= itself"
					+ " (and the cooker would find an abslog= anywhere in a switch)");
			}
			string? Folder = FolderSwitches.FirstOrDefault(Switch => Switch.Equals(Name, StringComparison.OrdinalIgnoreCase));
			if (Folder is not null && !Option.StartsWith($"-{Folder}=", StringComparison.OrdinalIgnoreCase))
			{
				throw new UakUsageException($"{source}: the cooker ignores {Option}: it reads -{Folder}=<folder>, with one '-' and a value");
			}
			if (IsCookDir(Option) && (Option.Length == CookDirPrefix.Length || Option.Contains('+', StringComparison.Ordinal)))
			{
				throw new UakUsageException($"{source}: {Option} can't be passed: a -COOKDIR= names one folder (give one -COOKDIR= for each), and it goes through"
					+ " BuildCookRun's -cookdir=, which separates folders with '+'");
			}
		}
		return Options;
	}

	/// <summary>
	/// Warnings for -COOKDIR= switches that name no folder on this machine, after <see cref="ResolveCookDirs"/>. The cook
	/// commandlet reads -COOKDIR= as a folder on disk and cooks the .uasset files it finds under it (CookCommandlet.cpp,
	/// CookOnTheFlyServer.cpp), not the maps (.umap), which -map= names; the project's DirectoriesToAlwaysCook, unlike it, are
	/// package paths. It runs in the editor's binaries folder, so a relative path is warned about too. Warned about, never
	/// refused: the folder may be made before the cook.
	/// </summary>
	public static IReadOnlyList<string> CookDirWarnings(IReadOnlyList<string> cookerOptions)
	{
		List<string> Warnings = [];
		foreach (string Option in cookerOptions.Where(IsCookDir))
		{
			string Folder = Option[CookDirPrefix.Length..];
			if (!Path.IsPathFullyQualified(Folder))
			{
				Warn(Option, "isn't a full path");
			}
			else if (!Directory.Exists(Folder))
			{
				Warn(Option, "names no folder on this machine");
			}
		}
		return Warnings;

		void Warn(string option, string why) => Warnings.Add($"cooker option {option} {why}: the cooker reads -COOKDIR= as a folder on disk, given in full"
			+ " (it runs in the editor's binaries folder), so it cooks nothing from it");
	}

	/// <summary>
	/// Turns each -COOKDIR= package path into the folder on disk it names, as the cooker reads -COOKDIR= (see
	/// <see cref="CookDirWarnings"/>); every other argument is kept as given. /Game/&lt;rest&gt; is &lt;Project&gt;/Content/&lt;rest&gt;,
	/// /Engine/&lt;rest&gt; is the engine's Content/&lt;rest&gt;, and /&lt;Name&gt;/&lt;rest&gt; is the Content/&lt;rest&gt; of the plugin
	/// whose descriptor is &lt;Name&gt;.uplugin: in the project's Plugins folder, else in the engine's, each searched as the engine
	/// searches them (a folder that holds a .uplugin isn't searched further). Plugins the engine finds elsewhere (a project's
	/// AdditionalPluginDirectories, Platforms/&lt;platform&gt;/Plugins) aren't searched. Names match ignoring case on Windows,
	/// exactly elsewhere. On Windows a value that starts with one '/' is always a package path, as no full path does; elsewhere a
	/// full path starts with '/' too, so it is a package path only when its first part is Game, Engine or a plugin found, and is
	/// kept as given otherwise. -NEVERCOOKDIR= is kept as given: the cooker reads a package path there itself
	/// (GetNeverCookPackageNames, CookOnTheFlyServer.cpp).
	/// </summary>
	/// <param name="cookerOptions">The arguments, from <see cref="ParseCookerOptions"/>.</param>
	/// <param name="source">What gave them, for the refusal.</param>
	/// <param name="projectDirectory">The project's folder.</param>
	/// <param name="engineDirectory">The engine's Engine folder, or null when uak knows no engine.</param>
	/// <param name="windows">Whether the host is Windows.</param>
	/// <exception cref="UakUsageException">A package path names no folder, holds a character no package name can (or a '+'), or
	/// names a folder whose path can't go through RunUAT (a character <see cref="ParseCookerOptions"/> refuses, a '+', or white
	/// space other than a space), or a plugin name that two plugins in one Plugins folder share.</exception>
	public static IReadOnlyList<string> ResolveCookDirs(IReadOnlyList<string> cookerOptions, string source, string projectDirectory, string? engineDirectory, bool windows)
	{
		StringComparison Comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		List<string> Resolved = [];
		foreach (string Option in cookerOptions)
		{
			string Value = IsCookDir(Option) ? Option[CookDirPrefix.Length..] : "";
			if (!Value.StartsWith('/') || Value.StartsWith("//", StringComparison.Ordinal))
			{
				Resolved.Add(Option);
				continue;
			}
			string[] Parts = Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
			string? Content = Parts.Length == 0 ? null : FindContentFolder(Parts[0]);
			if (Content is null && !windows)
			{
				// A full path on this platform: kept, and warned about as any other folder.
				Resolved.Add(Option);
				continue;
			}
			if (Content is null)
			{
				string Plugins = Path.Combine(projectDirectory, "Plugins") + (engineDirectory is null ? "" : " or the engine's Plugins folder");
				string Why = Parts.Length == 0 ? "it names none" : $"no plugin there is named {Parts[0]}";
				throw new UakUsageException($"{source}: {Option} names no content folder: a -COOKDIR= that starts with '/' is a package path, /Game/<folder>,"
					+ $" /Engine/<folder> or /<Plugin>/<folder> for a plugin in {Plugins} (not one the engine finds elsewhere), and {Why}");
			}
			if (Parts.Any(Part => Part.Any(C => InvalidPackagePathCharacters.Contains(C, StringComparison.Ordinal) || char.IsWhiteSpace(C) || char.IsControl(C))))
			{
				throw new UakUsageException($"{source}: {Option} isn't a package path uak can resolve: its parts may hold no \\ : * ? \" < > | ' , . & ! ~ @ # +,"
					+ " white space or control characters");
			}
			string Folder = Path.Combine([Content, .. Parts[1..]]).Replace('\\', '/');
			string Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Content));
			string Full = Path.GetFullPath(Folder);
			if (!Full.Equals(Root, Comparison) && !Full.StartsWith(Root + Path.DirectorySeparatorChar, Comparison))
			{
				throw new UakUsageException($"{source}: {Option} is {Folder}, which isn't under {Root}");
			}
			if (!Directory.Exists(Folder))
			{
				throw new UakUsageException($"{source}: {Option} is {Folder}, which doesn't exist: the cooker would cook nothing from it");
			}
			// BuildCookRun quotes each folder for the cooker, so a space is fine; RunUAT.bat still expands the value while it scans it.
			if (Folder.Any(C => (char.IsWhiteSpace(C) && C != ' ') || IsUnsafeArgumentCharacter(C) || C == '+'))
			{
				throw new UakUsageException($"{source}: {Option} is {Folder}, which can't go through RunUAT to the cooker: no quotes, % ! & | < > ^, '+'"
					+ " (BuildCookRun's -cookdir= separates folders with it), tabs or control characters");
			}
			Resolved.Add(Option[..CookDirPrefix.Length] + Folder);
		}
		return Resolved;

		string? FindContentFolder(string name)
		{
			if (name.Equals("Game", Comparison))
			{
				return Path.Combine(projectDirectory, "Content");
			}
			if (name.Equals("Engine", Comparison))
			{
				return engineDirectory is null
					? throw new UakUsageException($"{source}: a -COOKDIR=/Engine/... names the engine's content, and uak knows no engine here")
					: Path.Combine(engineDirectory, "Content");
			}
			// The project's plugins first: the engine, too, takes a project plugin over an engine plugin of the same name.
			string?[] Roots = [Path.Combine(projectDirectory, "Plugins"), engineDirectory is null ? null : Path.Combine(engineDirectory, "Plugins")];
			foreach (string? Root in Roots)
			{
				List<string> Found = Root is null ? [] : FindPlugins(Root, name, Comparison);
				if (Found.Count > 1)
				{
					throw new UakUsageException($"{source}: /{name}/ can't be resolved: {Found.Count} plugins are named {name}: {string.Join(", ", Found)}");
				}
				if (Found.Count == 1)
				{
					return Path.Combine(Path.GetDirectoryName(Found[0])!, "Content");
				}
			}
			return null;
		}
	}

	/// <summary>
	/// The descriptors named &lt;name&gt;.uplugin under <paramref name="root"/>, found as the engine finds plugins: a folder that holds a
	/// .uplugin is a plugin, and isn't searched further. At most 16 folders deep, so a link that loops ends.
	/// </summary>
	internal static List<string> FindPlugins(string root, string name, StringComparison comparison)
	{
		List<string> Found = [];
		Stack<(string Folder, int Depth)> Folders = new([(root, 0)]);
		while (Folders.Count > 0)
		{
			(string Folder, int Depth) = Folders.Pop();
			try
			{
				string[] Descriptors = [.. Directory.EnumerateFiles(Folder, "*.uplugin").Where(File => Path.GetExtension(File).Equals(".uplugin", StringComparison.OrdinalIgnoreCase))];
				if (Descriptors.Length > 0)
				{
					Found.AddRange(Descriptors.Where(File => Path.GetFileNameWithoutExtension(File).Equals(name, comparison)));
					continue;
				}
				if (Depth < 16)
				{
					foreach (string Child in Directory.EnumerateDirectories(Folder))
					{
						Folders.Push((Child, Depth + 1));
					}
				}
			}
			catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
			{
				// A folder that is gone or can't be read holds no plugin uak can name.
			}
		}
		Found.Sort(StringComparer.Ordinal);
		return Found;
	}

	static bool IsUnsafeArgumentCharacter(char c) => c is '"' or '\'' or '%' or '!' or '&' or '|' or '<' or '>' or '^' || char.IsControl(c);
}

/// <summary>What UAT's output says about a cooked test run (the game's results are in Client.log).</summary>
public sealed class UatRunSummary
{
	/// <summary>UAT's exit code when UBT stopped with ConflictingInstance, which UAT prints as Error_SDKNotFound (both are 10).</summary>
	public const int ConflictingInstanceExitCode = 10;

	/// <summary>The step whose end means UAT runs no more UBT.</summary>
	public const string BuildCompleted = "BUILD COMMAND COMPLETED";

	/// <summary>Whether UAT's UBT said another UBT held its mutex ("A conflicting instance of ... is already running").</summary>
	public bool UbtConflict { get; set; }

	/// <summary>N of "Client exited with error code: N": reported, never judged, because UAT stops the game once the queue empties.</summary>
	public int? ClientExitCode { get; set; }

	/// <summary>N of "The run timed out after N seconds", or null.</summary>
	public int? TimedOutAfterSeconds { get; set; }

	/// <summary>Whether UAT's UBT said a target was up to date ("Target is up to date", "Targets are up to date").</summary>
	public bool UbtUpToDate { get; set; }

	/// <summary>Whether UAT's UBT said it runs actions: UAT may run one for each target, so "up to date" alone doesn't say that it compiled nothing.</summary>
	public bool UbtRanActions { get; set; }

	/// <summary>The steps UAT started and completed, in order ("BUILD COMMAND STARTED", ...).</summary>
	public List<string> Steps { get; } = [];
}

/// <summary>Reads UAT's console output line by line into a <see cref="UatRunSummary"/>.</summary>
public sealed partial class UatOutputParser
{
	/// <summary>What the lines so far say.</summary>
	public UatRunSummary Summary { get; } = new();

	/// <summary>Reads one line; returns the step it names ("COOK COMMAND STARTED"), or null.</summary>
	public string? AddLine(string line)
	{
		Match M;
		if ((M = StepPattern().Match(line)).Success)
		{
			string Step = M.Groups["step"].Value;
			Summary.Steps.Add(Step);
			return Step;
		}
		if ((M = ClientExitPattern().Match(line)).Success)
		{
			Summary.ClientExitCode = int.Parse(M.Groups["code"].Value, CultureInfo.InvariantCulture);
		}
		else if ((M = TimeoutPattern().Match(line)).Success)
		{
			Summary.TimedOutAfterSeconds = int.Parse(M.Groups["seconds"].Value, CultureInfo.InvariantCulture);
		}
		else if (UbtConflictPattern().IsMatch(line))
		{
			Summary.UbtConflict = true;
		}
		else if (UpToDatePattern().IsMatch(line))
		{
			Summary.UbtUpToDate = true;
		}
		else if (ActionsPattern().IsMatch(line))
		{
			Summary.UbtRanActions = true;
		}
		return null;
	}

	[GeneratedRegex(@"^\s*\*{10} (?<step>[A-Z][A-Z ]* COMMAND (?:STARTED|COMPLETED)) \*{10}\s*$")]
	private static partial Regex StepPattern();

	[GeneratedRegex(@"^\s*Client exited with error code: (?<code>-?\d+)")]
	private static partial Regex ClientExitPattern();

	[GeneratedRegex(@"^\s*The run timed out after (?<seconds>\d+) seconds")]
	private static partial Regex TimeoutPattern();

	// GlobalSingleInstanceMutex.AcquireNowOrThrow; the mutex is named after UBT (UnrealBuildTool.cs), and the line may carry a log prefix.
	[GeneratedRegex(@"A conflicting instance of \S*UnrealBuildTool\S* is already running")]
	private static partial Regex UbtConflictPattern();

	// BuildMode.cs: "Target is up to date", and for one run of several targets "Targets are up to date". The line may carry a prefix.
	[GeneratedRegex(@"\bTargets? (?:is|are) up to date\b")]
	private static partial Regex UpToDatePattern();

	// ActionGraph.ExecuteActionsAsync prints the first for every executor ("Using Unreal Build Accelerator local executor to run
	// 7 action(s)"; UE 5.7 and 5.8). ImmediateActionQueue prints the second under the parallel executor, and older engines the
	// third; UBA prints only the first, so it carries the rule.
	[GeneratedRegex(@"executor to run \d+ action\(s\)|Building \d+ action\(s\) started|Building \d+ actions? with")]
	private static partial Regex ActionsPattern();
}
