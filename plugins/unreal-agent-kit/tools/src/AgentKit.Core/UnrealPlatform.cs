// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Runtime.InteropServices;

namespace AgentKit.Core;

/// <summary>
/// An Unreal host platform: UBT's name for it, how its executables and scripts are named, and where its batch files and
/// bundled .NET SDK live. Code outside Core never hard-codes ".exe", ".bat" or "Win64": it asks the platform
/// (DESIGN.md, "Unreal operations").
/// </summary>
public sealed class UnrealPlatform
{
	/// <summary>Windows. UBT calls both x64 and arm64 Windows hosts Win64.</summary>
	public static readonly UnrealPlatform Win64 = new("Win64", ".exe", ".bat", "", "win", isWindows: true);

	/// <summary>Linux on x64.</summary>
	public static readonly UnrealPlatform Linux = new("Linux", "", ".sh", "Linux", "linux", isWindows: false);

	/// <summary>Linux on arm64. Its batch files are Linux's.</summary>
	public static readonly UnrealPlatform LinuxArm64 = new("LinuxArm64", "", ".sh", "Linux", "linux", isWindows: false);

	/// <summary>macOS, x64 or Apple silicon.</summary>
	public static readonly UnrealPlatform Mac = new("Mac", "", ".sh", "Mac", "mac", isWindows: false);

	/// <summary>Every platform this type knows.</summary>
	public static IReadOnlyList<UnrealPlatform> All { get; } = [Win64, Linux, LinuxArm64, Mac];

	readonly string DotNetOsName;

	UnrealPlatform(string name, string executableSuffix, string scriptSuffix, string batchFilesFolder, string dotNetOsName, bool isWindows)
	{
		Name = name;
		ExecutableSuffix = executableSuffix;
		ScriptSuffix = scriptSuffix;
		BatchFilesFolder = batchFilesFolder;
		DotNetOsName = dotNetOsName;
		IsWindows = isWindows;
	}

	/// <summary>UBT's name for the platform, which is also its folder under Engine/Binaries.</summary>
	public string Name { get; }

	/// <summary>".exe" on Windows, empty elsewhere.</summary>
	public string ExecutableSuffix { get; }

	/// <summary>".bat" on Windows, ".sh" elsewhere.</summary>
	public string ScriptSuffix { get; }

	/// <summary>The platform's subfolder of Engine/Build/BatchFiles that holds Build.sh ("Linux", "Mac"); empty on Windows, whose scripts sit in BatchFiles itself.</summary>
	public string BatchFilesFolder { get; }

	/// <summary>Whether this is Windows: command lines are one string there, and scripts run through cmd.exe.</summary>
	public bool IsWindows { get; }

	/// <summary>The platform this process runs on.</summary>
	public static UnrealPlatform Host { get; } = FromOperatingSystem();

	/// <summary>
	/// The folder name of the engine's bundled .NET SDK for this platform and the machine's architecture, as the engine's
	/// GetDotnetPath.bat and SetupDotnet.sh choose it: win-x64, win-arm64, linux-x64, linux-arm64, mac-x64 or mac-arm64.
	/// </summary>
	public string DotNetRid => GetDotNetRid(RuntimeInformation.OSArchitecture);

	/// <summary>The bundled SDK folder name for this platform and the given architecture. See <see cref="DotNetRid"/>.</summary>
	public string GetDotNetRid(Architecture architecture)
	{
		if (ReferenceEquals(this, LinuxArm64))
		{
			return "linux-arm64";
		}
		return DotNetOsName + (architecture == Architecture.Arm64 ? "-arm64" : "-x64");
	}

	/// <summary>Appends <see cref="ExecutableSuffix"/> to a program name: "UnrealEditor-Cmd" becomes "UnrealEditor-Cmd.exe" on Windows.</summary>
	public string ExecutableName(string baseName) => baseName + ExecutableSuffix;

	/// <summary>Appends <see cref="ScriptSuffix"/> to a script name: "Build" becomes "Build.bat" on Windows and "Build.sh" elsewhere.</summary>
	public string ScriptName(string baseName) => baseName + ScriptSuffix;

	/// <summary>Finds a platform by UBT name (case-insensitive); "host" gives <see cref="Host"/>. Returns null for other names.</summary>
	public static UnrealPlatform? FromName(string name)
	{
		if (name.Equals("host", StringComparison.OrdinalIgnoreCase))
		{
			return Host;
		}
		return All.FirstOrDefault(Platform => Platform.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
	}

	/// <inheritdoc/>
	public override string ToString() => Name;

	static UnrealPlatform FromOperatingSystem()
	{
		if (OperatingSystem.IsWindows())
		{
			return Win64;
		}
		if (OperatingSystem.IsMacOS())
		{
			return Mac;
		}
		return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? LinuxArm64 : Linux;
	}
}
