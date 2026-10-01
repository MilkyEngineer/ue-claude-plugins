// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>How the test editor renders.</summary>
public enum EditorRendering
{
	/// <summary>No RHI (-nullrhi), with the command-line editor: the default, for tests that don't draw.</summary>
	NullRhi,

	/// <summary>A real RHI off screen (-RenderOffscreen), with the command-line editor, for tests that draw.</summary>
	Offscreen,

	/// <summary>
	/// A real RHI in a window (-windowed -ResX= -ResY=), with the editor itself (<see cref="EditorLocation.Executable"/>), for
	/// tests that need a real viewport and Slate windows.
	/// </summary>
	Windowed,
}

/// <summary>Builds the editor command line that runs automation tests and quits.</summary>
public static class EditorTestCommandLine
{
	/// <summary>The window size of a <see cref="EditorRendering.Windowed"/> run when none is given.</summary>
	public const int DefaultResX = 1600, DefaultResY = 900;

	/// <summary>
	/// Editor options uak sets itself, so a pass-through after "--" may not give them: -ExecCmds, -testexit, -abslog and
	/// -ReportExportPath would change which tests run, when the editor quits, or where uak reads the results (the engine reads
	/// the first -ExecCmds= and -abslog= it finds, so a second one would be silently ignored); -nullrhi, -RenderOffscreen,
	/// -windowed, -ResX and -ResY are uak's -gpu, -windowed, -resx= and -resy=.
	/// </summary>
	static readonly string[] ReservedOptions = ["ExecCmds", "testexit", "abslog", "ReportExportPath", "nullrhi", "RenderOffscreen", "windowed", "ResX", "ResY"];

	/// <summary>
	/// The editor run for "Automation RunTests &lt;filter&gt;". It quits when the test queue empties, writes its full log to
	/// <paramref name="logFile"/>, and exports the JSON report to <paramref name="reportDirectory"/>.
	/// </summary>
	/// <param name="paths">The engine's tool paths. The editor is the project's, from <see cref="EditorLocator.Locate"/>.</param>
	/// <param name="projectFile">The .uproject.</param>
	/// <param name="filter">The test filter (a path prefix; several joined with '+').</param>
	/// <param name="gpu">Render with a real RHI off screen instead of -nullrhi, for tests that draw.</param>
	/// <param name="logFile">The editor's log file (-abslog).</param>
	/// <param name="reportDirectory">Where the test report goes (-ReportExportPath).</param>
	/// <exception cref="UakUsageException">The filter holds characters the editor's command would split on.</exception>
	public static ProcessInvocation Build(EngineLayout paths, string projectFile, string filter, bool gpu, string logFile, string reportDirectory) =>
		Build(EditorLocator.Locate(paths, projectFile), projectFile, filter, gpu ? EditorRendering.Offscreen : EditorRendering.NullRhi, logFile, reportDirectory);

	/// <summary>The same, with the editor given, any rendering, and more editor arguments.</summary>
	/// <param name="editor">The project's editor (<see cref="EditorLocator.Locate"/>): its command-line executable, or the editor itself for a windowed run.</param>
	/// <param name="projectFile">The .uproject.</param>
	/// <param name="filter">The test filter (a path prefix; several joined with '+').</param>
	/// <param name="rendering">How the editor renders.</param>
	/// <param name="logFile">The editor's log file (-abslog).</param>
	/// <param name="reportDirectory">Where the test report goes (-ReportExportPath).</param>
	/// <param name="extraArguments">More editor arguments, last; check a pass-through with <see cref="CheckPassThrough"/> first.</param>
	/// <param name="resX">The window width of a windowed run.</param>
	/// <param name="resY">The window height of a windowed run.</param>
	/// <exception cref="UakUsageException">The filter holds characters the editor's command would split on.</exception>
	public static ProcessInvocation Build(EditorLocation editor, string projectFile, string filter, EditorRendering rendering, string logFile, string reportDirectory,
		IEnumerable<string>? extraArguments = null, int resX = DefaultResX, int resY = DefaultResY)
	{
		if (filter.Length == 0 || filter.Any(IsUnsafeFilterCharacter))
		{
			throw new UakUsageException($"-filter='{filter}' must be a test path prefix without spaces, quotes, ',', ';', '|' or '`'");
		}
		List<string> Arguments =
		[
			Path.GetFullPath(projectFile),
			$"-ExecCmds=Automation RunTests {filter};Quit",
			"-unattended",
		];
		Arguments.AddRange(rendering switch
		{
			EditorRendering.Offscreen => ["-RenderOffscreen"],
			EditorRendering.Windowed => ["-windowed", $"-ResX={resX}", $"-ResY={resY}"],
			_ => ["-nullrhi"],
		});
		Arguments.AddRange(
		[
			"-nosplash",
			"-nopause",
			"-NoSound",
			"-abslog=" + Path.GetFullPath(logFile),
			"-ReportExportPath=" + Path.GetFullPath(reportDirectory),
			"-testexit=Automation Test Queue Empty",
		]);
		Arguments.AddRange(extraArguments ?? []);
		// The editor itself has a window; the -Cmd one (the same on Linux and Mac) is for command-line runs.
		string Executable = rendering == EditorRendering.Windowed ? editor.Executable : editor.CommandExecutable;
		return new ProcessInvocation(Executable, Arguments, Path.GetDirectoryName(Path.GetFullPath(projectFile)));
	}

	/// <summary>
	/// Checks the editor arguments given after "--" and returns them unchanged. Options uak sets itself are rejected (see
	/// <see cref="ReservedOptions"/>), whether given as -Name, --Name or /Name, in any case. Anything else passes as given.
	/// </summary>
	/// <param name="arguments">The arguments after "--".</param>
	/// <exception cref="UakUsageException">An argument is one uak sets.</exception>
	public static IReadOnlyList<string> CheckPassThrough(IReadOnlyList<string> arguments)
	{
		foreach (string Argument in arguments.Where(Argument => Argument.StartsWith('-') || Argument.StartsWith('/')))
		{
			string Name = UbtCommandLine.OptionName(Argument);
			if (ReservedOptions.Any(Reserved => Reserved.Equals(Name, StringComparison.OrdinalIgnoreCase)))
			{
				throw new UakUsageException($"{Argument} can't go after --: uak sets it (-ExecCmds, -testexit, -abslog, -ReportExportPath), or it is uak's -gpu, -windowed, -resx= or -resy=.");
			}
		}
		return arguments;
	}

	/// <summary>
	/// Characters that would break "-ExecCmds=Automation RunTests &lt;filter&gt;;Quit": the engine splits -ExecCmds on ','
	/// outside single quotes and turns single quotes into double quotes (ParseExecCommands.cpp); the automation command
	/// splits on ';'; double quotes and white space end or split the argument. '|', '`' and control characters have no place
	/// in a test path, so they are rejected too.
	/// </summary>
	static bool IsUnsafeFilterCharacter(char c) => c is ';' or ',' or '|' or '"' or '\'' or '`' || char.IsWhiteSpace(c) || char.IsControl(c);
}
