// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentKit.Core;

/// <summary>
/// `uak env`: every resolved path, how each was found, the bundled dotnet, and what the loaded <see cref="IUakEnvReporter"/>s
/// add (the version-control system, from AgentKit.Vcs). Runs without an engine, so it can show why none was found.
/// </summary>
public sealed class EnvCommand : IUakCommand
{
	/// <summary>The label AgentKit.Vcs reports under; "unknown" is shown when no reporter gives it.</summary>
	public const string VersionControlLabel = "Version control";

	/// <inheritdoc/>
	public string Name => "env";

	/// <inheritdoc/>
	public string Summary => "Shows the project, engine, state folder, tools and version control uak found, and how.";

	/// <inheritdoc/>
	public string Usage => """
		uak env [-json]

		-json  Print one JSON object (label to value) instead of aligned text.
		""";

	/// <inheritdoc/>
	public bool RequiresEngine => false;

	/// <summary>The reporters to ask; null for every one in <see cref="UakCommandCatalog.Current"/>.</summary>
	public IReadOnlyList<IUakEnvReporter>? Reporters { get; init; }

	/// <summary>Where the text goes; null for the console.</summary>
	public TextWriter? Output { get; init; }

	/// <inheritdoc/>
	public async Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments Arguments = new(arguments);
		bool Json = Arguments.GetFlag("json");
		Arguments.ThrowIfUnknown();
		Arguments.ThrowIfMorePositionalThan(0);

		List<KeyValuePair<string, string>> Rows = [.. Describe(context)];

		IReadOnlyList<IUakEnvReporter> Reporters = this.Reporters ?? UakCommandCatalog.Current?.CreateAll<IUakEnvReporter>() ?? [];
		foreach (IUakEnvReporter Reporter in Reporters)
		{
			try
			{
				Rows.AddRange(await Reporter.ReportAsync(context, cancellationToken).ConfigureAwait(false));
			}
			catch (Exception Error) when (Error is not OperationCanceledException)
			{
				context.Logger.LogDebug(Error, "{Reporter} failed", Reporter.GetType().FullName);
				Rows.Add(new(Reporter.GetType().Name, "error: " + Error.Message));
			}
		}
		if (!Rows.Any(Row => Row.Key.Equals(VersionControlLabel, StringComparison.OrdinalIgnoreCase)))
		{
			Rows.Add(new(VersionControlLabel, "unknown (no version-control provider is loaded)"));
		}

		TextWriter Writer = Output ?? Console.Out;
		if (Json)
		{
			// Keys stay unique: a repeated label becomes "Label (2)".
			Dictionary<string, string> Object = [];
			foreach ((string Key, string Value) in Rows)
			{
				string Unique = Key;
				for (int Suffix = 2; Object.ContainsKey(Unique); Suffix++)
				{
					Unique = $"{Key} ({Suffix})";
				}
				Object[Unique] = Value;
			}
			Writer.WriteLine(UakJson.Serialize(Object));
		}
		else
		{
			int Width = Rows.Max(Row => Row.Key.Length) + 1;
			foreach ((string Key, string Value) in Rows)
			{
				Writer.WriteLine((Key + ":").PadRight(Width + 1) + Value);
			}
		}
		return UakExitCodes.Success;
	}

	/// <summary>The rows Core itself knows: paths, how they were found, the engine's tools, and uak itself.</summary>
	public static IEnumerable<KeyValuePair<string, string>> Describe(UakContext context)
	{
		string How(string key) => context.Provenance.TryGetValue(key, out string? Value) ? Value : "unknown";

		yield return new("Project", context.ProjectFile?.FullName ?? "none");
		yield return new("Project from", How(UakContextResolver.ProjectKey));
		yield return new("Engine", context.EngineRoot?.FullName ?? "none");
		yield return new("Engine from", How(UakContextResolver.EngineKey));

		EngineLayout? Engine = context.Engine;
		if (Engine is not null)
		{
			yield return new("Engine version", Engine.ReadVersion()?.ToString() ?? "unknown (no readable Engine/Build/Build.version)");
			yield return new("Installed build", Engine.IsInstalledBuild ? "yes" : "no (source build)");
			yield return new("Bundled dotnet", Engine.DotNetExecutable ?? $"none (nothing under {Engine.DotNetRootDirectory} for {context.Platform.DotNetRid})");
			yield return new("UnrealBuildTool", Existing(Engine.UnrealBuildToolAssembly));
			yield return new("Build script", Existing(Engine.BuildScript));
			yield return new("Editor (cmd)", Existing(Engine.EditorCommandExecutable));
		}

		yield return new("State", context.StateDirectory.FullName);
		yield return new("State from", How(UakContextResolver.StateKey));
		yield return new("Platform", context.Platform.Name);
		if (context.RequestedVersionControl is not null)
		{
			yield return new("Requested VCS", $"{context.RequestedVersionControl} ({How(UakContextResolver.VcsKey)})");
		}
		(string FileName, IReadOnlyList<string> Prefix) = UakSelf.GetCommand();
		yield return new("uak", $"{UakHost.Version} for {BuiltFramework ?? "unknown .NET"} (running on {RuntimeInformation.FrameworkDescription}), built against engine {BuiltEngineVersion ?? "unknown"}, at {string.Join(' ', Prefix.Prepend(FileName))}");
	}

	/// <summary>
	/// The target framework uak was built for, such as "net10.0": the engine's own (DESIGN.md, "Build"). A project command
	/// assembly must target this or an older one to load.
	/// </summary>
	public static string? BuiltFramework
	{
		get
		{
			// ".NETCoreApp,Version=v10.0" is net10.0.
			string? Name = typeof(EnvCommand).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
			const string Marker = ",Version=v";
			int At = Name?.IndexOf(Marker, StringComparison.Ordinal) ?? -1;
			return Name is null || At < 0 ? Name : "net" + Name[(At + Marker.Length)..];
		}
	}

	/// <summary>The version of the engine uak was built against ("5.8.3"), from its Build.version at build time; null when unknown.</summary>
	public static string? BuiltEngineVersion => typeof(EnvCommand).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
		.FirstOrDefault(Attribute => Attribute.Key == "UakEngineVersion")?.Value is { Length: > 0 } Value ? Value : null;

	static string Existing(string path) => File.Exists(path) ? path : path + " (missing)";
}
