// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>
/// The kit's per-project settings: <c>&lt;State&gt;/config.json</c>, in the project's own state folder (<c>uak env</c>'s
/// "State"), beside its lock queue and run records; the per-user settings are <c>&lt;UAK_HOME&gt;/config.json</c>. Written
/// whole and atomically, keeping keys uak doesn't know.
/// </summary>
public static class ProjectSettings
{
	/// <summary>The file's name in the state folder.</summary>
	public const string FileName = "config.json";

	/// <summary>The settings file of the context's project.</summary>
	public static string GetFile(UakContext context) => Path.Combine(context.StateDirectory.FullName, FileName);

	/// <summary><c>uak test -cooked</c>'s default -map= (maps joined with '+'), or null when none is saved.</summary>
	public static string? GetCookedMaps(UakContext context)
	{
		JsonObject? Root = Read(GetFile(context));
		return Root?["test"]?["cookedMaps"] is JsonValue Value && Value.TryGetValue(out string? Maps) && !string.IsNullOrEmpty(Maps) ? Maps : null;
	}

	/// <summary>Saves <c>uak test -cooked</c>'s default -map=, or forgets it (null).</summary>
	public static void SetCookedMaps(UakContext context, string? maps)
	{
		string File = GetFile(context);
		JsonObject Root = Read(File) ?? [];
		JsonObject Test = Root["test"] as JsonObject ?? [];
		if (maps is null)
		{
			Test.Remove("cookedMaps");
		}
		else
		{
			Test["cookedMaps"] = maps;
		}
		Root["test"] = Test;
		StateFiles.WriteJson(File, Root);
	}

	static JsonObject? Read(string file)
	{
		string? Text = StateFiles.ReadShared(file);
		if (string.IsNullOrWhiteSpace(Text))
		{
			return null;
		}
		try
		{
			return JsonNode.Parse(Text) as JsonObject ?? throw new UakSetupException($"{file} is not a JSON object: fix or delete it");
		}
		catch (JsonException Exception)
		{
			throw new UakSetupException($"{file} is not valid JSON ({Exception.Message}): fix or delete it", Exception);
		}
	}
}

/// <summary><c>uak test config</c>: shows or saves the per-project defaults of <c>uak test</c>.</summary>
public sealed class TestConfigCommand : IUakCommand
{
	/// <inheritdoc/>
	public string Name => "test config";

	/// <inheritdoc/>
	public string Summary => "Show or save this project's defaults for uak test (the maps a cooked run cooks).";

	/// <inheritdoc/>
	public string Usage =>
		"""
		uak test config [-cooked-maps=<map>[+<map>...]] [-reset]
		  Without options: prints the saved defaults and where they are kept.
		  -cooked-maps=<maps>   The maps `uak test -cooked` cooks when -map= isn't given, e.g.
		                        -cooked-maps=/Game/Maps/Test+/Engine/Maps/Entry (the first is the one the game opens).
		                        From Git Bash, set MSYS_NO_PATHCONV=1 so the shell leaves /Game/... alone.
		  -reset                Forget the saved defaults.
		The defaults are this project's, kept in <State>/config.json (the state folder `uak env` shows).
		""";

	/// <inheritdoc/>
	public bool RequiresEngine => false;

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return UnrealServices.GuardAsync(context, () =>
		{
			UakArguments Arguments = new(arguments);
			string? Maps = Arguments.GetString("cooked-maps");
			bool Reset = Arguments.GetFlag("reset");
			Arguments.ThrowIfUnknown();
			Arguments.ThrowIfMorePositionalThan(0);
			UnrealServices.RequireProject(context);
			if (Maps is not null && Reset)
			{
				throw new UakUsageException("give -cooked-maps= or -reset, not both");
			}
			if (Maps is not null)
			{
				ProjectSettings.SetCookedMaps(context, string.Join('+', CookedTestCommandLine.ParseMaps(Maps)));
			}
			else if (Reset)
			{
				ProjectSettings.SetCookedMaps(context, null);
			}
			string? Saved = ProjectSettings.GetCookedMaps(context);
			context.Logger.LogInformation("cooked-maps: {Maps}", Saved ?? "(none: a cooked run cooks the project's maps unless -map= is given)");
			context.Logger.LogInformation("Kept in {File}", ProjectSettings.GetFile(context));
			return Task.FromResult(UakExitCodes.Success);
		});
	}
}
