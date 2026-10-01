// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>What UBT builds: a target, a platform and a configuration, for an optional project.</summary>
/// <param name="Target">The target name, e.g. "MyGameEditor".</param>
/// <param name="Platform">The target platform.</param>
/// <param name="Configuration">The configuration, e.g. "Development".</param>
/// <param name="ProjectFile">The .uproject, or null for an engine target.</param>
/// <param name="MaxParallelActions">Caps UBT's parallel actions, or null for UBT's default.</param>
public sealed record UbtTarget(string Target, UnrealPlatform Platform, string Configuration, string? ProjectFile, int? MaxParallelActions);

/// <summary>Builds UnrealBuildTool command lines.</summary>
public static class UbtCommandLine
{
	/// <summary>
	/// A UBT build of the target. On an installed engine (or any engine whose UBT sources are absent) UBT runs directly on
	/// the engine's bundled dotnet, with the environment Build.bat would set. On a source build the platform's Build script
	/// runs instead, because it rebuilds UBT when its sources changed.
	/// Always passes -WaitMutex (UBT is one instance per engine: queue, never fail) and -NoHotReloadFromIDE (never patch a
	/// running editor's modules).
	/// </summary>
	/// <param name="paths">The engine's tool paths.</param>
	/// <param name="target">What to build.</param>
	/// <param name="extraArguments">More UBT arguments, e.g. -SingleFile=, or a pass-through checked by <see cref="CheckPassThrough"/>.</param>
	/// <exception cref="UakSetupException">UBT or the dotnet it needs is missing.</exception>
	public static ProcessInvocation Build(EngineLayout paths, UbtTarget target, IEnumerable<string>? extraArguments = null)
	{
		List<string> UbtArguments = [target.Target, target.Platform.Name, target.Configuration];
		if (target.ProjectFile is not null)
		{
			UbtArguments.Add("-Project=" + Path.GetFullPath(target.ProjectFile));
		}
		UbtArguments.Add("-WaitMutex");
		UbtArguments.Add("-NoHotReloadFromIDE");
		if (target.MaxParallelActions is int Max)
		{
			UbtArguments.Add("-MaxParallelActions=" + Max);
		}
		UbtArguments.AddRange(extraArguments ?? []);

		string EngineSource = Path.Combine(paths.EngineDirectory, "Source");
		bool HasUbtSources = File.Exists(Path.Combine(EngineSource, "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj"));
		if (paths.IsInstalledBuild || !HasUbtSources)
		{
			if (!File.Exists(paths.UnrealBuildToolAssembly))
			{
				throw new UakSetupException("UnrealBuildTool not found: " + paths.UnrealBuildToolAssembly);
			}
			// Throws UakSetupException when the engine ships no bundled SDK.
			return paths.GetUnrealBuildToolInvocation(UbtArguments, EngineSource);
		}
		if (!File.Exists(paths.BuildScript))
		{
			throw new UakSetupException("build script not found: " + paths.BuildScript);
		}
		return new ProcessInvocation(paths.BuildScript, UbtArguments, EngineSource);
	}

	/// <summary>
	/// The UBT arguments that compile the files listed in <paramref name="fileList"/> (written by <see cref="WriteFileList"/>)
	/// on their own: -FileList=, and -SingleFileBuildDependents with <paramref name="dependents"/>. UBT's TargetDescriptor reads
	/// each line of the list into the same list -SingleFile= fills (SpecificFilesToCompile), before -SingleFileBuildDependents
	/// expands it, so the build is the one a -SingleFile= per file would make. Unlike those, the command line stays the same
	/// length for any number of files: Build.bat runs through cmd.exe, whose command line stops at 8191 characters (any
	/// program's at 32767). UBT reads no @response file, so its own list file is the way. A listed path also never passes
	/// through cmd's parsing (% and ! expansion).
	/// </summary>
	/// <param name="fileList">The list file.</param>
	/// <param name="dependents">Also compile every file of the target that includes a listed header.</param>
	public static IReadOnlyList<string> SingleFileArguments(string fileList, bool dependents)
	{
		List<string> Arguments = ["-FileList=" + Path.GetFullPath(fileList)];
		if (dependents)
		{
			Arguments.Add("-SingleFileBuildDependents");
		}
		return Arguments;
	}

	/// <summary>
	/// Writes a list file for UBT's -FileList= (<see cref="SingleFileArguments"/>): one full path per line, in UTF-8 without a
	/// byte order mark. UBT skips blank lines and keeps a rooted path as it is (FileReference.Combine from the engine root).
	/// </summary>
	/// <param name="path">The list file to write. Its folder must exist.</param>
	/// <param name="files">The files, as full paths.</param>
	/// <exception cref="UakSetupException">A path is not rooted, or holds a line break (UBT would read it as two files).</exception>
	public static void WriteFileList(string path, IEnumerable<string> files)
	{
		List<string> Lines = [];
		foreach (string Entry in files)
		{
			if (!Path.IsPathRooted(Entry) || Entry.Contains('\n', StringComparison.Ordinal) || Entry.Contains('\r', StringComparison.Ordinal))
			{
				throw new UakSetupException($"can't give '{Entry}' to UBT in a file list: it needs a full path without line breaks");
			}
			Lines.Add(Entry);
		}
		File.WriteAllLines(path, Lines, new System.Text.UTF8Encoding(false));
	}

	/// <summary>
	/// UBT options uak sets itself, or that would break what uak promises, so a pass-through after "--" may not give them:
	/// -Project= and -Target=/-TargetList= (what is built: use -project= and -target=), -Mode= (it would no longer be a build),
	/// -WaitMutex and -NoMutex (UBT's one-instance mutex: uak always waits on it), -NoHotReloadFromIDE, -ForceHotReload and
	/// -LiveCoding (never patch a running editor), and -MaxParallelActions= (use uak's option, which also reads
	/// UAK_MAX_PARALLEL_ACTIONS). UBT reads options case-insensitively (CommandLineArguments), and so does this check.
	/// </summary>
	static readonly string[] ReservedOptions =
		["Project", "Target", "TargetList", "Mode", "WaitMutex", "NoMutex", "NoHotReloadFromIDE", "ForceHotReload", "LiveCoding", "MaxParallelActions"];

	/// <summary>
	/// Checks the UBT arguments given after "--" and returns them unchanged. Each must be an option (-Name or -Name=Value):
	/// UBT reads a bare word as another target, platform or configuration. Options uak sets itself, or that would break the
	/// lock and mutex guarantees, are rejected (see <see cref="ReservedOptions"/>), as are <paramref name="alsoReserved"/>.
	/// </summary>
	/// <param name="arguments">The arguments after "--".</param>
	/// <param name="alsoReserved">More option names the command sets itself, e.g. "SingleFile" for `uak compile`.</param>
	/// <exception cref="UakUsageException">An argument is not an option, or is one uak sets.</exception>
	public static IReadOnlyList<string> CheckPassThrough(IReadOnlyList<string> arguments, params string[] alsoReserved)
	{
		foreach (string Argument in arguments)
		{
			if (!Argument.StartsWith('-') || Argument.TrimStart('-').Length == 0)
			{
				throw new UakUsageException($"'{Argument}' after -- is not a UBT option: UBT would read it as a target, platform or configuration. Give options only (-Name or -Name=Value).");
			}
			string Name = OptionName(Argument);
			if (ReservedOptions.Concat(alsoReserved).Any(Reserved => Reserved.Equals(Name, StringComparison.OrdinalIgnoreCase)))
			{
				throw new UakUsageException($"{Argument} can't go after --: uak sets it, or it would break the lock or UBT's mutex. Use uak's own options instead (uak help).");
			}
		}
		return arguments;
	}

	/// <summary>An option's name: without its leading '-' characters and from '=' on.</summary>
	internal static string OptionName(string argument)
	{
		string Body = argument.TrimStart('-', '/');
		int Equals = Body.IndexOf('=', StringComparison.Ordinal);
		return Equals < 0 ? Body : Body[..Equals];
	}
}
