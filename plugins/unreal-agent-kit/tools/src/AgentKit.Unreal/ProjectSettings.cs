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
	public static string? GetCookedMaps(UakContext context) => GetTestString(context, "cookedMaps");

	/// <summary>Saves <c>uak test -cooked</c>'s default -map=, or forgets it (null).</summary>
	public static void SetCookedMaps(UakContext context, string? maps) => SetTestString(context, "cookedMaps", maps);

	/// <summary>
	/// <c>uak test -cooked</c>'s default -cooker-options= (cooker arguments joined with spaces, as saved: check them with
	/// <see cref="CookedTestCommandLine.ParseCookerOptions"/>), or null when none is saved.
	/// </summary>
	public static string? GetCookedCookerOptions(UakContext context) => GetTestString(context, "cookedCookerOptions");

	/// <summary>Saves <c>uak test -cooked</c>'s default -cooker-options=, or forgets it (null).</summary>
	public static void SetCookedCookerOptions(UakContext context, string? options) => SetTestString(context, "cookedCookerOptions", options);

	static string? GetTestString(UakContext context, string key)
	{
		JsonObject? Root = Read(GetFile(context));
		return (Root?["test"] as JsonObject)?[key] is JsonValue Value && Value.TryGetValue(out string? Text) && !string.IsNullOrEmpty(Text) ? Text : null;
	}

	static void SetTestString(UakContext context, string key, string? value)
	{
		string File = GetFile(context);
		JsonObject Root = Read(File) ?? [];
		JsonObject Test = Root["test"] as JsonObject ?? [];
		if (value is null)
		{
			Test.Remove(key);
		}
		else
		{
			Test[key] = value;
		}
		Root["test"] = Test;
		StateFiles.WriteJson(File, Root);
	}

	/// <summary>
	/// The source files the project's builds write themselves, as paths from the project folder with '/' (<see cref="ParseBuildWrites"/>),
	/// which aren't edits of a build. Empty when none is saved.
	/// </summary>
	public static IReadOnlyList<string> GetBuildWrites(UakContext context)
	{
		JsonObject? Root = Read(GetFile(context));
		return (Root?["build"] as JsonObject)?["writes"] is JsonArray Saved ? [.. Saved.OfType<JsonValue>().Select(Value => Value.TryGetValue(out string? Relative) ? Relative : "").Where(Relative => Relative.Length > 0)] : [];
	}

	/// <summary>Saves the files of <see cref="GetBuildWrites"/>, or forgets them (null).</summary>
	public static void SetBuildWrites(UakContext context, IReadOnlyList<string>? paths)
	{
		string File = GetFile(context);
		JsonObject Root = Read(File) ?? [];
		JsonObject Build = Root["build"] as JsonObject ?? [];
		if (paths is null)
		{
			Build.Remove("writes");
		}
		else
		{
			JsonArray Writes = [];
			foreach (string Relative in paths)
			{
				Writes.Add(JsonValue.Create(Relative));
			}
			Build["writes"] = Writes;
		}
		Root["build"] = Build;
		StateFiles.WriteJson(File, Root);
	}

	/// <summary>Reads -build-writes=: exact project-relative paths joined with '+', refused if one has a wildcard, is absolute, or isn't a plain path inside the project.</summary>
	public static List<string> ParseBuildWrites(string value)
	{
		List<string> Paths = [];
		foreach (string Given in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			string Relative = Given.Replace('\\', '/');
			if (Relative.IndexOfAny(['*', '?']) >= 0)
			{
				throw new UakUsageException($"-build-writes: {Given} has a wildcard: name each file exactly");
			}
			if (Path.IsPathRooted(Given) || Relative.Contains(':', StringComparison.Ordinal) || Relative.Split('/').Any(Part => Part is "" or "." or ".."))
			{
				throw new UakUsageException($"-build-writes: {Given} isn't a path from the project folder");
			}
			Paths.Add(Relative);
		}
		return Paths.Count > 0 ? Paths : throw new UakUsageException("-build-writes= names no file");
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
	public string Summary => "Show or save this project's settings for uak test and its builds (the maps a cooked run cooks and its cooker options, the files a build writes itself).";

	/// <inheritdoc/>
	public string Usage =>
		"""
		uak test config [-cooked-maps=<map>[+<map>...]] [-cooked-cooker-options=<args>] [-build-writes=<path>[+<path>...]] [-reset]
		  Without options: prints the saved settings and where they are kept.
		  -cooked-maps=<maps>   The maps `uak test -cooked` cooks when -map= isn't given, e.g.
		                        -cooked-maps=/Game/Maps/Test+/Engine/Maps/Entry (the first is the one the game opens).
		                        From Git Bash, set MSYS_NO_PATHCONV=1 so the shell leaves /Game/... alone.
		  -cooked-cooker-options=<args>
		                        The cooker arguments `uak test -cooked` passes when -cooker-options= isn't given, checked
		                        as -cooker-options= is (see `uak help test`), e.g.
		                        "-cooked-cooker-options=-COOKDIR=/TestContent/Golden -COOKDIR=/Game/Tests".
		                        Kept as typed, so it holds on every machine: each run resolves its -COOKDIR= package
		                        paths again (a -skipcook run, which uses none, checks none), and this command shows what
		                        they resolve to here. An empty -cooked-cooker-options= forgets them.
		  -build-writes=<paths> The source files a build writes itself (a pre-build step's output), which aren't edits:
		                        no warning, no recompile mark, no EditedDuringBuild, no `uak mutate` exit 4 and no
		                        retried rebuild for them. Exact paths from the project folder, no wildcards, e.g.
		                        -build-writes=Source/Game/Private/Version.gen.h. Replaces any saved list. Empty by default.
		                        Warns about a file that doesn't exist, or lies outside Source and Plugins, the only
		                        folders uak looks in, and saves it all the same. '+' separates paths, so a path with a
		                        '+' in it can't be named.
		  -reset                Forget the saved settings.
		The settings are this project's, kept in <State>/config.json (the state folder `uak env` shows).
		""";

	/// <inheritdoc/>
	public bool RequiresEngine => false;

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return UnrealServices.GuardAsync(context, () =>
		{
			UakArguments Arguments = new(arguments, DotSplitRule.Options);
			string? Maps = Arguments.GetString("cooked-maps");
			string? CookerOptions = Arguments.GetStringOrEmpty("cooked-cooker-options");
			string? Writes = Arguments.GetString("build-writes");
			bool Reset = Arguments.GetFlag("reset");
			Arguments.ThrowIfUnknown();
			Arguments.ThrowIfMorePositionalThan(0);
			UnrealServices.RequireProject(context);
			if ((Maps is not null || CookerOptions is not null || Writes is not null) && Reset)
			{
				throw new UakUsageException("give -cooked-maps=, -cooked-cooker-options= or -build-writes=, or -reset, not both");
			}
			// All of it checked first: a value that's refused saves nothing.
			string? ParsedMaps = Maps is null ? null : string.Join('+', CookedTestCommandLine.ParseMaps(Maps));
			IReadOnlyList<string>? ParsedCookerOptions = CookerOptions is null ? null : CookedTestCommandLine.ParseCookerOptions(CookerOptions, "-cooked-cooker-options");
			List<string>? ParsedWrites = Writes is null ? null : ProjectSettings.ParseBuildWrites(Writes);
			if (ParsedMaps is not null)
			{
				ProjectSettings.SetCookedMaps(context, ParsedMaps);
			}
			if (ParsedCookerOptions is not null)
			{
				// An empty value forgets them: a cooked run then passes none unless -cooker-options= is given.
				// Kept as typed, package paths and all, so the setting is the same on every machine: each run resolves them.
				ProjectSettings.SetCookedCookerOptions(context, ParsedCookerOptions.Count == 0 ? null : string.Join(' ', ParsedCookerOptions));
			}
			if (ParsedWrites is not null)
			{
				ProjectSettings.SetBuildWrites(context, ParsedWrites);
				WarnAboutBuildWrites(context, ParsedWrites);
			}
			if (Reset)
			{
				ProjectSettings.SetCookedMaps(context, null);
				ProjectSettings.SetCookedCookerOptions(context, null);
				ProjectSettings.SetBuildWrites(context, null);
			}
			string? Saved = ProjectSettings.GetCookedMaps(context);
			string? SavedCookerOptions = ProjectSettings.GetCookedCookerOptions(context);
			IReadOnlyList<string> SavedWrites = ProjectSettings.GetBuildWrites(context);
			context.Logger.LogInformation("cooked-maps: {Maps}", Saved ?? "(none: a cooked run cooks the project's maps unless -map= is given)");
			context.Logger.LogInformation("cooked-cooker-options: {Options}", SavedCookerOptions ?? "(none: a cooked run passes the cooker no more arguments unless -cooker-options= is given)");
			if (SavedCookerOptions is not null)
			{
				ShowResolvedCookerOptions(context, SavedCookerOptions);
			}
			context.Logger.LogInformation("build-writes: {Writes}", SavedWrites.Count > 0 ? string.Join('+', SavedWrites) : "(none: every source file written during a build counts as an edit)");
			context.Logger.LogInformation("Kept in {File}", ProjectSettings.GetFile(context));
			return Task.FromResult(UakExitCodes.Success);
		});
	}

	/// <summary>
	/// What the saved cooker options resolve to here and now (their -COOKDIR= package paths), or why a cooked run would refuse them,
	/// and the warnings a run would print. Never a refusal: the folders may be made, or the plugin added, before the run.
	/// </summary>
	static void ShowResolvedCookerOptions(UakContext context, string saved)
	{
		try
		{
			IReadOnlyList<string> Given = CookedTestCommandLine.ParseCookerOptions(saved, "cooked-cooker-options");
			IReadOnlyList<string> Resolved = CookedTestCommandLine.ResolveCookDirs(Given, "cooked-cooker-options", context.ProjectDirectory!.FullName,
				context.EngineDirectory?.FullName, context.Platform.IsWindows);
			if (!Resolved.SequenceEqual(Given))
			{
				context.Logger.LogInformation("  resolves here to: {Options}", string.Join(' ', Resolved));
			}
			foreach (string Warning in CookedTestCommandLine.CookDirWarnings(Resolved))
			{
				context.Logger.LogWarning("{Warning}", Warning);
			}
		}
		catch (UakUsageException Refusal)
		{
			context.Logger.LogWarning("  a cooked run here would refuse them: {Why}", Refusal.Message);
		}
	}

	/// <summary>Warns about what can't be meant as it stands. Saved all the same: a build may yet write a file that isn't there.</summary>
	static void WarnAboutBuildWrites(UakContext context, List<string> paths)
	{
		string? Project = context.ProjectDirectory?.FullName;
		foreach (string Relative in paths)
		{
			if (Project is not null && !File.Exists(Path.Combine(Project, Relative)))
			{
				context.Logger.LogWarning("-build-writes: {File} doesn't exist: check the path", Relative);
			}
			if (!BuildEdits.SourceRoots.Contains(Relative.Split('/')[0], StringComparer.OrdinalIgnoreCase))
			{
				context.Logger.LogWarning("-build-writes: {File} isn't under Source or Plugins, the only folders uak looks in, so naming it changes nothing", Relative);
			}
		}
	}
}
