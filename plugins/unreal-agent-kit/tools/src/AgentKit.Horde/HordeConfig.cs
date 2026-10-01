// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Core;
using EpicGames.Horde;

namespace AgentKit.Horde;

/// <summary>
/// The kit's own settings file, <c>&lt;UAK_HOME or ~/.unreal-agent-kit&gt;/config.json</c>: shared by every kit version (it sits
/// beside the versioned install folders), so the Horde server is set once per user. Only the "horde" section is read or
/// written here; other sections are kept as they are.
/// </summary>
/// <example><code>{ "horde": { "server": "https://horde.example.com/" } }</code></example>
public sealed class HordeConfig
{
	/// <summary>The file's name in the kit folder.</summary>
	public const string FileName = "config.json";

	/// <summary>Creates the config for a file.</summary>
	public HordeConfig(string path)
	{
		Path = System.IO.Path.GetFullPath(path);
	}

	/// <summary>The config file.</summary>
	public string Path { get; }

	/// <summary>The config of this user: <c>config.json</c> in the kit folder (<c>UAK_HOME</c>, else <c>~/.unreal-agent-kit</c>).</summary>
	public static HordeConfig ForUser(UakResolveOptions? options = null)
		=> new(System.IO.Path.Combine(UakContextResolver.GetUserDirectory(options ?? new UakResolveOptions()), FileName));

	/// <summary>The stored Horde server, or null when there is none. Throws <see cref="UakUsageException"/> when the file can't be read.</summary>
	public Uri? ReadServer()
	{
		JsonObject root = ReadRoot();
		string? text = (root["horde"] as JsonObject)?["server"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		return TryParseServer(text, out Uri? server, out string? error) ? server : throw new UakUsageException($"{Path}: horde.server is not usable: {error}");
	}

	/// <summary>Stores the Horde server, keeping every other setting in the file. The write is atomic.</summary>
	public void WriteServer(Uri server) => WriteHordeValue("server", server.ToString());

	/// <summary>When <c>-wait</c> opens a job's page in the browser (horde.open); <see cref="HordeOpenMode.Never"/> when unset.</summary>
	public HordeOpenMode ReadOpen()
	{
		string? text = (ReadRoot()["horde"] as JsonObject)?["open"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(text))
		{
			return HordeOpenMode.Never;
		}
		return TryParseOpen(text, out HordeOpenMode mode) ? mode : throw new UakUsageException($"{Path}: horde.open must be never, created, finished or failed, not '{text}'.");
	}

	/// <summary>Stores when <c>-wait</c> opens a job's page, keeping every other setting.</summary>
	public void WriteOpen(HordeOpenMode mode) => WriteHordeValue("open", mode.ToString().ToLowerInvariant());

	/// <summary>Parses never, created, finished or failed (any case).</summary>
	public static bool TryParseOpen(string text, out HordeOpenMode mode)
		=> Enum.TryParse(text.Trim(), ignoreCase: true, out mode) && !int.TryParse(text, out _) && Enum.IsDefined(mode);

	void WriteHordeValue(string key, string value)
	{
		JsonObject root = ReadRoot();
		if (root["horde"] is not JsonObject horde)
		{
			horde = [];
			root["horde"] = horde;
		}
		horde[key] = value;
		StateFiles.WriteText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
	}

	JsonObject ReadRoot()
	{
		if (!File.Exists(Path))
		{
			return [];
		}
		try
		{
			string text = File.ReadAllText(Path);
			if (string.IsNullOrWhiteSpace(text))
			{
				return [];
			}
			return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
				?? throw new UakUsageException($"{Path} does not hold a JSON object. Fix or delete it.");
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
		{
			throw new UakUsageException($"Could not read {Path}: {exception.Message}. Fix or delete it.", exception);
		}
	}

	/// <summary>
	/// Parses a server URL: absolute http or https, with no query or fragment. The path keeps a trailing slash, so relative
	/// API paths (api/v1/...) resolve under it.
	/// </summary>
	public static bool TryParseServer(string text, out Uri? server, out string? error)
	{
		server = null;
		if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
		{
			error = $"'{text}' is not an http or https URL.";
			return false;
		}
		if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo))
		{
			error = $"'{text}' must be the server's address only, with no query, fragment or user name.";
			return false;
		}
		UriBuilder builder = new(parsed);
		if (!builder.Path.EndsWith('/'))
		{
			builder.Path += "/";
		}
		server = builder.Uri;
		error = null;
		return true;
	}
}

/// <summary>When <c>uak horde preflight -wait</c> and <c>uak horde job -wait</c> open the job's page in the browser.</summary>
public enum HordeOpenMode
{
	/// <summary>Never (the default).</summary>
	Never,

	/// <summary>As soon as the job is created or found.</summary>
	Created,

	/// <summary>When the job ends.</summary>
	Finished,

	/// <summary>When the job fails or does not complete: the first failed step's page, else the job's.</summary>
	Failed,
}

/// <summary>Opens a URL in the user's default browser.</summary>
public static class Browser
{
	/// <summary>Opens <paramref name="url"/> (Windows: the shell; Mac: open; elsewhere: xdg-open). Returns the error, or null.</summary>
	public static string? Open(string url)
	{
		try
		{
			System.Diagnostics.ProcessStartInfo info = OperatingSystem.IsWindows()
				? new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }
				: new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, ArgumentList = { url } };
			using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(info);
			return null;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
		{
			return exception.Message;
		}
	}
}

/// <summary>A Horde server and where it came from.</summary>
/// <param name="Url">The server's base URL, ending in '/'.</param>
/// <param name="Source">How it was found, for messages: "-server=", the config file, UE_HORDE_URL, or Horde's own default.</param>
/// <param name="Stored">Whether it is the one in the kit's config file.</param>
public sealed record HordeServer(Uri Url, string Source, bool Stored);

/// <summary>Finds the Horde server for a command (DESIGN.md, "Horde").</summary>
public static class HordeServerResolver
{
	/// <summary>
	/// The message when no server is known. Agents can't guess a company's server, so it says to ask the user (through the
	/// lead), then store the answer.
	/// </summary>
	public const string NotConfiguredMessage =
		"No Horde server is configured. Ask the user (through the lead) for the Horde server's URL, then run: uak horde config -server=<url>";

	/// <summary>
	/// The server, from (in order) <paramref name="argument"/> (<c>-server=</c>), the kit's config file, then Horde's own default
	/// (<c>UE_HORDE_URL</c>, then on Windows the registry value Horde's tools write, elsewhere <c>~/.horde.json</c>).
	/// Throws <see cref="UakUsageException"/> with <see cref="NotConfiguredMessage"/> when there is none.
	/// </summary>
	public static HordeServer Resolve(string? argument, HordeConfig config, Func<(Uri? Url, string Source)>? hordeDefault = null)
	{
		if (argument is not null)
		{
			return HordeConfig.TryParseServer(argument, out Uri? server, out string? error)
				? new HordeServer(server!, "-server=", false)
				: throw new UakUsageException("-server: " + error);
		}
		Uri? stored = config.ReadServer();
		if (stored is not null)
		{
			return new HordeServer(stored, config.Path, true);
		}
		(Uri? fallback, string source) = (hordeDefault ?? GetHordeDefault)();
		if (fallback is not null && HordeConfig.TryParseServer(fallback.ToString(), out Uri? parsed, out _))
		{
			return new HordeServer(parsed!, source, false);
		}
		throw new UakUsageException(NotConfiguredMessage);
	}

	/// <summary>Horde's own default server: UE_HORDE_URL, then the registry (Windows) or ~/.horde.json, as Horde's tools find it.</summary>
	public static (Uri? Url, string Source) GetHordeDefault()
	{
		try
		{
			Uri? fromEnvironment = HordeOptions.GetServerUrlFromEnvironment();
			if (fromEnvironment is not null)
			{
				return (fromEnvironment, HordeHttpClient.HordeUrlEnvVarName + " environment variable");
			}
			Uri? fromDefault = HordeOptions.GetDefaultServerUrl();
			return (fromDefault, OperatingSystem.IsWindows() ? "Horde's default (registry, Software\\Epic Games\\Horde)" : "Horde's default (~/.horde.json)");
		}
		catch (Exception exception) when (exception is UriFormatException or IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
		{
			return (null, "Horde's default (unreadable: " + exception.Message + ")");
		}
	}
}
