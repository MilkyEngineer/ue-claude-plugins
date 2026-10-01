// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>Builds the editor command line that runs automation tests headless and quits.</summary>
public static class EditorTestCommandLine
{
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
		Build(EditorLocator.Locate(paths, projectFile).CommandExecutable, projectFile, filter, gpu, logFile, reportDirectory);

	/// <summary>The same, with the editor's command-line executable given.</summary>
	/// <param name="editorCommandExecutable">The editor for command-line use (<see cref="EditorLocation.CommandExecutable"/>).</param>
	/// <param name="projectFile">The .uproject.</param>
	/// <param name="filter">The test filter (a path prefix; several joined with '+').</param>
	/// <param name="gpu">Render with a real RHI off screen instead of -nullrhi, for tests that draw.</param>
	/// <param name="logFile">The editor's log file (-abslog).</param>
	/// <param name="reportDirectory">Where the test report goes (-ReportExportPath).</param>
	/// <exception cref="UakUsageException">The filter holds characters the editor's command would split on.</exception>
	public static ProcessInvocation Build(string editorCommandExecutable, string projectFile, string filter, bool gpu, string logFile, string reportDirectory)
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
			gpu ? "-RenderOffscreen" : "-nullrhi",
			"-nosplash",
			"-nopause",
			"-NoSound",
			"-abslog=" + Path.GetFullPath(logFile),
			"-ReportExportPath=" + Path.GetFullPath(reportDirectory),
			"-testexit=Automation Test Queue Empty",
		];
		return new ProcessInvocation(editorCommandExecutable, Arguments, Path.GetDirectoryName(Path.GetFullPath(projectFile)));
	}

	/// <summary>
	/// Characters that would break "-ExecCmds=Automation RunTests &lt;filter&gt;;Quit": the engine splits -ExecCmds on ','
	/// outside single quotes and turns single quotes into double quotes (ParseExecCommands.cpp); the automation command
	/// splits on ';'; double quotes and white space end or split the argument. '|', '`' and control characters have no place
	/// in a test path, so they are rejected too.
	/// </summary>
	static bool IsUnsafeFilterCharacter(char c) => c is ';' or ',' or '|' or '"' or '\'' or '`' || char.IsWhiteSpace(c) || char.IsControl(c);
}
