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

	static bool IsUnsafeArgumentCharacter(char c) => c is '"' or '\'' or '%' or '!' or '&' or '|' or '<' or '>' or '^' || char.IsControl(c);
}

/// <summary>What UAT's output says about a cooked test run (the game's results are in Client.log).</summary>
public sealed class UatRunSummary
{
	/// <summary>N of "Client exited with error code: N": reported, never judged, because UAT stops the game once the queue empties.</summary>
	public int? ClientExitCode { get; set; }

	/// <summary>N of "The run timed out after N seconds", or null.</summary>
	public int? TimedOutAfterSeconds { get; set; }

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
		return null;
	}

	[GeneratedRegex(@"^\s*\*{10} (?<step>[A-Z][A-Z ]* COMMAND (?:STARTED|COMPLETED)) \*{10}\s*$")]
	private static partial Regex StepPattern();

	[GeneratedRegex(@"^\s*Client exited with error code: (?<code>-?\d+)")]
	private static partial Regex ClientExitPattern();

	[GeneratedRegex(@"^\s*The run timed out after (?<seconds>\d+) seconds")]
	private static partial Regex TimeoutPattern();
}
