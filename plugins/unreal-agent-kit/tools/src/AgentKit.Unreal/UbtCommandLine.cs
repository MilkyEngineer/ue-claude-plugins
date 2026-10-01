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
	/// <param name="extraArguments">More UBT arguments, e.g. -SingleFile=.</param>
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
}
