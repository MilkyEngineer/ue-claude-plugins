// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace AgentKit.Core;

/// <summary>An engine found for a .uproject's EngineAssociation.</summary>
/// <param name="EngineRoot">The engine root as the source lists it (not yet checked).</param>
/// <param name="Source">Where it was found, for provenance, e.g. "HKLM\SOFTWARE\EpicGames\Unreal Engine\5.8 InstalledDirectory".</param>
public sealed record EngineAssociationMatch(string EngineRoot, string Source);

/// <summary>
/// Somewhere that maps an EngineAssociation (a version such as "5.8", or a GUID for a registered source build) to an engine
/// root: the registry, the launcher's install list, or Install.ini. A seam so tests never read this machine's settings.
/// </summary>
public interface IEngineAssociationSource
{
	/// <summary>A short description for messages, e.g. "the launcher's LauncherInstalled.dat".</summary>
	string Description { get; }

	/// <summary>
	/// Every engine root this source lists for the association, in the source's order. Empty when there is none or the
	/// source cannot be read. The resolver skips entries that are not engines.
	/// </summary>
	IEnumerable<EngineAssociationMatch> Find(string association);
}

/// <summary>The two registry roots the association lookup reads.</summary>
public enum RegistryRoot
{
	/// <summary>HKEY_CURRENT_USER.</summary>
	CurrentUser,

	/// <summary>HKEY_LOCAL_MACHINE.</summary>
	LocalMachine,
}

/// <summary>Reads string values from the Windows registry. A seam so tests can use a fake registry.</summary>
public interface IRegistryReader
{
	/// <summary>The names of the values under the key, or empty when the key does not exist.</summary>
	IReadOnlyList<string> GetValueNames(RegistryRoot root, string key);

	/// <summary>A string value, or null when the key or value does not exist or is not a string.</summary>
	string? GetString(RegistryRoot root, string key, string valueName);
}

/// <summary>The real Windows registry, reading the 64-bit view and then the 32-bit one.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryReader : IRegistryReader
{
	/// <inheritdoc/>
	public IReadOnlyList<string> GetValueNames(RegistryRoot root, string key)
	{
		foreach (RegistryView View in Views)
		{
			using RegistryKey? Key = Open(root, View, key);
			if (Key is not null)
			{
				return Key.GetValueNames();
			}
		}
		return [];
	}

	/// <inheritdoc/>
	public string? GetString(RegistryRoot root, string key, string valueName)
	{
		foreach (RegistryView View in Views)
		{
			using RegistryKey? Key = Open(root, View, key);
			if (Key?.GetValue(valueName) is string Value)
			{
				return Value;
			}
		}
		return null;
	}

	static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

	static RegistryKey? Open(RegistryRoot root, RegistryView view, string key)
	{
		try
		{
			using RegistryKey Base = RegistryKey.OpenBaseKey(root == RegistryRoot.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);
			return Base.OpenSubKey(key, writable: false);
		}
		catch (Exception Error) when (Error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
		{
			return null;
		}
	}
}

/// <summary>
/// Source builds registered by UnrealVersionSelector: HKCU\Software\Epic Games\Unreal Engine\Builds, one value per
/// identifier (usually a GUID in braces) holding the engine root.
/// </summary>
public sealed class RegistryBuildsSource(IRegistryReader registry) : IEngineAssociationSource
{
	/// <summary>The key, under HKEY_CURRENT_USER.</summary>
	public const string Key = @"Software\Epic Games\Unreal Engine\Builds";

	/// <inheritdoc/>
	public string Description => @"HKCU\" + Key;

	/// <inheritdoc/>
	public IEnumerable<EngineAssociationMatch> Find(string association)
	{
		string Wanted = StripBraces(association);
		foreach (string Name in registry.GetValueNames(RegistryRoot.CurrentUser, Key))
		{
			if (StripBraces(Name).Equals(Wanted, StringComparison.OrdinalIgnoreCase))
			{
				string? Root = registry.GetString(RegistryRoot.CurrentUser, Key, Name);
				if (!string.IsNullOrWhiteSpace(Root))
				{
					yield return new EngineAssociationMatch(Root, $@"HKCU\{Key} ""{Name}""");
				}
			}
		}
	}

	static string StripBraces(string text) => text.Trim().TrimStart('{').TrimEnd('}');
}

/// <summary>Launcher installs: HKLM\SOFTWARE\EpicGames\Unreal Engine\&lt;version&gt;, value InstalledDirectory.</summary>
public sealed class RegistryInstalledDirectorySource(IRegistryReader registry) : IEngineAssociationSource
{
	/// <summary>The parent key, under HKEY_LOCAL_MACHINE.</summary>
	public const string Key = @"SOFTWARE\EpicGames\Unreal Engine";

	/// <inheritdoc/>
	public string Description => @"HKLM\" + Key + @"\<version> InstalledDirectory";

	/// <inheritdoc/>
	public IEnumerable<EngineAssociationMatch> Find(string association)
	{
		if (association.IndexOfAny(['\\', '/']) >= 0)
		{
			yield break;
		}
		string SubKey = Key + @"\" + association;
		string? Root = registry.GetString(RegistryRoot.LocalMachine, SubKey, "InstalledDirectory");
		if (!string.IsNullOrWhiteSpace(Root))
		{
			yield return new EngineAssociationMatch(Root, $@"HKLM\{SubKey} InstalledDirectory");
		}
	}
}

/// <summary>
/// The Epic Games Launcher's install list, LauncherInstalled.dat (JSON). An engine is listed with AppName "UE_&lt;version&gt;"
/// and its root in InstallLocation.
/// </summary>
public sealed class LauncherInstalledSource(string listFile) : IEngineAssociationSource
{
	/// <summary>The LauncherInstalled.dat this source reads.</summary>
	public string ListFile { get; } = listFile;

	/// <inheritdoc/>
	public string Description => ListFile;

	/// <inheritdoc/>
	public IEnumerable<EngineAssociationMatch> Find(string association)
	{
		List<EngineAssociationMatch> Matches = [];
		try
		{
			using FileStream Stream = new(ListFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using JsonDocument Document = JsonDocument.Parse(Stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
			if (Document.RootElement.ValueKind == JsonValueKind.Object
				&& Document.RootElement.TryGetProperty("InstallationList", out JsonElement List)
				&& List.ValueKind == JsonValueKind.Array)
			{
				string AppName = "UE_" + association;
				foreach (JsonElement Entry in List.EnumerateArray())
				{
					if (Entry.ValueKind == JsonValueKind.Object
						&& GetString(Entry, "AppName") is string Name
						&& Name.Equals(AppName, StringComparison.OrdinalIgnoreCase)
						&& GetString(Entry, "InstallLocation") is string Location
						&& Location.Length > 0)
					{
						Matches.Add(new EngineAssociationMatch(Location, $"{ListFile} (AppName {Name})"));
					}
				}
			}
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException)
		{
			// A missing or unreadable list lists nothing.
		}
		return Matches;
	}

	static string? GetString(JsonElement entry, string name) =>
		entry.TryGetProperty(name, out JsonElement Value) && Value.ValueKind == JsonValueKind.String ? Value.GetString() : null;
}

/// <summary>
/// Install.ini on Linux and Mac (&lt;settings&gt;/Epic/UnrealEngine/Install.ini), section [Installations]: one
/// "&lt;identifier&gt;=&lt;engine root&gt;" line per engine. Released builds are listed as "UE_&lt;version&gt;", source builds by GUID.
/// </summary>
public sealed class InstallIniSource(string iniFile) : IEngineAssociationSource
{
	/// <summary>The Install.ini this source reads.</summary>
	public string IniFile { get; } = iniFile;

	/// <inheritdoc/>
	public string Description => IniFile + " [Installations]";

	/// <inheritdoc/>
	public IEnumerable<EngineAssociationMatch> Find(string association)
	{
		List<EngineAssociationMatch> Matches = [];
		string[] Lines;
		try
		{
			Lines = File.ReadAllLines(IniFile);
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			return Matches;
		}

		string Wanted = StripBraces(association);
		bool InSection = false;
		foreach (string RawLine in Lines)
		{
			string Line = RawLine.Trim();
			if (Line.Length == 0 || Line[0] == ';' || Line[0] == '#')
			{
				continue;
			}
			if (Line[0] == '[')
			{
				InSection = Line.Equals("[Installations]", StringComparison.OrdinalIgnoreCase);
				continue;
			}
			int Equals = Line.IndexOf('=', StringComparison.Ordinal);
			if (!InSection || Equals <= 0)
			{
				continue;
			}
			string Key = Line[..Equals].Trim();
			string Value = Line[(Equals + 1)..].Trim().Trim('"');
			string Id = StripBraces(Key);
			if (Value.Length > 0 && (Id.Equals(Wanted, StringComparison.OrdinalIgnoreCase) || Id.Equals("UE_" + Wanted, StringComparison.OrdinalIgnoreCase)))
			{
				Matches.Add(new EngineAssociationMatch(Value, $"{IniFile} [Installations] {Key}"));
			}
		}
		return Matches;
	}

	static string StripBraces(string text) => text.Trim().TrimStart('{').TrimEnd('}');
}

/// <summary>Several sources, searched in order.</summary>
public sealed class CompositeEngineAssociationSource(IReadOnlyList<IEngineAssociationSource> sources) : IEngineAssociationSource
{
	/// <summary>The sources, in search order.</summary>
	public IReadOnlyList<IEngineAssociationSource> Sources { get; } = sources;

	/// <inheritdoc/>
	public string Description => string.Join(", then ", Sources.Select(Source => Source.Description));

	/// <inheritdoc/>
	public IEnumerable<EngineAssociationMatch> Find(string association) => Sources.SelectMany(Source => Source.Find(association));
}

/// <summary>The association sources Unreal itself uses on each platform (DesktopPlatform's EnumerateEngineInstallations).</summary>
public static class EngineAssociationSources
{
	/// <summary>
	/// The sources for the host: on Windows the registry's registered builds, then the launcher's registry keys, then
	/// %ProgramData%\Epic\UnrealEngineLauncher\LauncherInstalled.dat; on Linux and Mac, Install.ini and then the launcher list
	/// in the user's Epic settings folder.
	/// </summary>
	public static IEngineAssociationSource ForHost()
	{
		if (OperatingSystem.IsWindows())
		{
			return ForWindows(new WindowsRegistryReader(), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
		}
		string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return ForUnix(GetUnixSettingsDirectory(Home, OperatingSystem.IsMacOS()));
	}

	/// <summary>The Windows sources, over a registry and the ProgramData folder.</summary>
	public static IEngineAssociationSource ForWindows(IRegistryReader registry, string programDataDirectory) => new CompositeEngineAssociationSource(
	[
		new RegistryBuildsSource(registry),
		new RegistryInstalledDirectorySource(registry),
		new LauncherInstalledSource(Path.Combine(programDataDirectory, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat")),
	]);

	/// <summary>The Linux and Mac sources, over the user's Epic settings folder (see <see cref="GetUnixSettingsDirectory"/>).</summary>
	public static IEngineAssociationSource ForUnix(string epicSettingsDirectory) => new CompositeEngineAssociationSource(
	[
		new InstallIniSource(Path.Combine(epicSettingsDirectory, "UnrealEngine", "Install.ini")),
		new LauncherInstalledSource(Path.Combine(epicSettingsDirectory, "UnrealEngineLauncher", "LauncherInstalled.dat")),
	]);

	/// <summary>
	/// Unreal's ApplicationSettingsDir on Linux and Mac: ~/.config/Epic on Linux, ~/Library/Application Support/Epic on Mac.
	/// TODO(Linux, Mac): check against real installs.
	/// </summary>
	public static string GetUnixSettingsDirectory(string homeDirectory, bool isMac) => isMac
		? Path.Combine(homeDirectory, "Library", "Application Support", "Epic")
		: Path.Combine(homeDirectory, ".config", "Epic");
}
