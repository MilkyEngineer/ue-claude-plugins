// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;

namespace AgentKit.Core;

/// <summary>The engine version from Engine/Build/Build.version.</summary>
/// <param name="Major">MajorVersion, e.g. 5.</param>
/// <param name="Minor">MinorVersion, e.g. 8.</param>
/// <param name="Patch">PatchVersion.</param>
/// <param name="Changelist">Changelist, or 0.</param>
/// <param name="BranchName">BranchName, e.g. "++UE5+Release-5.8", or empty.</param>
public sealed record EngineVersion(int Major, int Minor, int Patch, int Changelist, string BranchName)
{
	/// <summary>"5.8.3", followed by the changelist and branch when known.</summary>
	public override string ToString()
	{
		string Text = $"{Major}.{Minor}.{Patch}";
		if (Changelist != 0)
		{
			Text += $"-{Changelist}";
		}
		if (BranchName.Length > 0)
		{
			Text += $"+{BranchName}";
		}
		return Text;
	}
}

/// <summary>
/// Where an engine keeps the files and tools the kit uses, for one host platform. Every path is built from the engine root and
/// the <see cref="UnrealPlatform"/>, so nothing else hard-codes a platform. The paths are computed, not checked: use
/// <see cref="File.Exists(string)"/> where it matters.
/// </summary>
public sealed class EngineLayout
{
	/// <summary>Creates the layout for an engine (its root, or its "Engine" folder) on a platform, by default the host.</summary>
	public EngineLayout(string engineRootOrEngineDirectory, UnrealPlatform? platform = null)
	{
		RootDirectory = EngineLocator.NormalizeEngineRoot(engineRootOrEngineDirectory) ?? Path.GetFullPath(engineRootOrEngineDirectory);
		EngineDirectory = Path.Combine(RootDirectory, "Engine");
		Platform = platform ?? UnrealPlatform.Host;
	}

	/// <summary>The platform the paths are for.</summary>
	public UnrealPlatform Platform { get; }

	/// <summary>The engine root: the directory that contains Engine/Build/Build.version.</summary>
	public string RootDirectory { get; }

	/// <summary>The engine's "Engine" directory.</summary>
	public string EngineDirectory { get; }

	/// <summary>Engine/Build/Build.version.</summary>
	public string BuildVersionFile => Path.Combine(EngineDirectory, "Build", "Build.version");

	/// <summary>Whether the engine is an installed build (Engine/Build/InstalledBuild.txt), such as a launcher install, whose tools are prebuilt.</summary>
	public bool IsInstalledBuild => File.Exists(Path.Combine(EngineDirectory, "Build", "InstalledBuild.txt"));

	/// <summary>Engine/Binaries/&lt;Platform&gt;.</summary>
	public string BinariesDirectory => Path.Combine(EngineDirectory, "Binaries", Platform.Name);

	/// <summary>Engine/Binaries/DotNET/UnrealBuildTool/UnrealBuildTool.dll, run with <see cref="DotNetExecutable"/>.</summary>
	public string UnrealBuildToolAssembly => Path.Combine(EngineDirectory, "Binaries", "DotNET", "UnrealBuildTool", "UnrealBuildTool.dll");

	/// <summary>Engine/Binaries/DotNET/AutomationTool/AutomationTool.dll, run with <see cref="DotNetExecutable"/>.</summary>
	public string AutomationToolAssembly => Path.Combine(EngineDirectory, "Binaries", "DotNET", "AutomationTool", "AutomationTool.dll");

	/// <summary>Engine/Build/BatchFiles, plus the platform's subfolder where it has one.</summary>
	public string BatchFilesDirectory => Platform.BatchFilesFolder.Length == 0
		? Path.Combine(EngineDirectory, "Build", "BatchFiles")
		: Path.Combine(EngineDirectory, "Build", "BatchFiles", Platform.BatchFilesFolder);

	/// <summary>The Build script: Engine/Build/BatchFiles/Build.bat on Windows, Engine/Build/BatchFiles/&lt;Linux|Mac&gt;/Build.sh elsewhere.</summary>
	public string BuildScript => Path.Combine(BatchFilesDirectory, Platform.ScriptName("Build"));

	/// <summary>The RunUAT script, Engine/Build/BatchFiles/RunUAT.bat or RunUAT.sh (the same folder on every platform).</summary>
	public string RunUatScript => Path.Combine(EngineDirectory, "Build", "BatchFiles", Platform.ScriptName("RunUAT"));

	/// <summary>
	/// The editor for command-line use, which logs to stdout and exits with the process:
	/// Engine/Binaries/Win64/UnrealEditor-Cmd.exe on Windows. Linux and Mac have no -Cmd build; there it is the editor itself
	/// (run it with -stdout), inside the app bundle on Mac. TODO(Linux, Mac): check on those platforms.
	/// </summary>
	/// <remarks>
	/// This is the engine's shared editor. A project whose editor target has a unique build environment runs its own:
	/// use <see cref="EditorLocator.Locate"/> for a project.
	/// </remarks>
	public string EditorCommandExecutable => CommandExecutableIn(BinariesDirectory, "UnrealEditor");

	/// <summary>The editor: UnrealEditor.exe, UnrealEditor, or UnrealEditor.app/Contents/MacOS/UnrealEditor on Mac.</summary>
	public string EditorExecutable => ExecutableIn(BinariesDirectory, "UnrealEditor");

	/// <summary>
	/// An application's executable in a binaries folder, named as UBT names it on this platform: &lt;App&gt;.exe, &lt;App&gt;,
	/// or &lt;App&gt;.app/Contents/MacOS/&lt;App&gt; on Mac.
	/// </summary>
	public string ExecutableIn(string binariesDirectory, string appName) => ReferenceEquals(Platform, UnrealPlatform.Mac)
		? Path.Combine(binariesDirectory, appName + ".app", "Contents", "MacOS", appName)
		: Path.Combine(binariesDirectory, Platform.ExecutableName(appName));

	/// <summary>
	/// An application's command-line executable in a binaries folder: &lt;App&gt;-Cmd.exe on Windows; elsewhere there is no
	/// -Cmd build, so it is <see cref="ExecutableIn"/>.
	/// </summary>
	public string CommandExecutableIn(string binariesDirectory, string appName) => Platform.IsWindows
		? Path.Combine(binariesDirectory, Platform.ExecutableName(appName + "-Cmd"))
		: ExecutableIn(binariesDirectory, appName);

	/// <summary>Engine/Binaries/ThirdParty/DotNet: one folder per SDK version, each with one folder per <see cref="UnrealPlatform.DotNetRid"/>.</summary>
	public string DotNetRootDirectory => Path.Combine(EngineDirectory, "Binaries", "ThirdParty", "DotNet");

	/// <summary>
	/// The engine's bundled dotnet executable, Engine/Binaries/ThirdParty/DotNet/&lt;version&gt;/&lt;rid&gt;/dotnet(.exe), taking the
	/// highest version that has this platform's SDK. Null when the engine ships none.
	/// </summary>
	public string? DotNetExecutable
	{
		get
		{
			if (!Directory.Exists(DotNetRootDirectory))
			{
				return null;
			}
			string Rid = Platform.DotNetRid;
			return Directory.EnumerateDirectories(DotNetRootDirectory)
				.Select(VersionDirectory => (Version: ParseVersion(Path.GetFileName(VersionDirectory)), Path: Path.Combine(VersionDirectory, Rid, Platform.ExecutableName("dotnet"))))
				.Where(Candidate => File.Exists(Candidate.Path))
				.OrderByDescending(Candidate => Candidate.Version)
				.Select(Candidate => Candidate.Path)
				.FirstOrDefault();
		}
	}

	/// <summary>
	/// The environment the engine's own scripts set before running a .NET tool with the bundled SDK (GetDotnetPath.bat and
	/// SetupDotnet.sh): DOTNET_ROOT, DOTNET_MULTILEVEL_LOOKUP=0, DOTNET_ROLL_FORWARD=LatestMajor and the SDK first on PATH.
	/// Also turns off telemetry and the first-run banner. Empty when the engine ships no SDK.
	/// </summary>
	public IReadOnlyDictionary<string, string?> GetDotNetEnvironment()
	{
		Dictionary<string, string?> Environment = new(StringComparer.Ordinal);
		string? DotNet = DotNetExecutable;
		if (DotNet is null)
		{
			return Environment;
		}
		string DotNetDirectory = System.IO.Path.GetDirectoryName(DotNet)!;
		Environment["DOTNET_ROOT"] = DotNetDirectory;
		Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
		Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
		Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
		Environment["DOTNET_NOLOGO"] = "1";
		Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
		string? SearchPath = System.Environment.GetEnvironmentVariable("PATH");
		Environment["PATH"] = string.IsNullOrEmpty(SearchPath) ? DotNetDirectory : DotNetDirectory + System.IO.Path.PathSeparator + SearchPath;
		return Environment;
	}

	/// <summary>
	/// How to run UnrealBuildTool with the given arguments: the bundled dotnet with UnrealBuildTool.dll first, and
	/// <see cref="GetDotNetEnvironment"/>. Throws <see cref="UakSetupException"/> when the engine ships no bundled SDK.
	/// </summary>
	public ProcessInvocation GetUnrealBuildToolInvocation(IEnumerable<string> arguments, string? workingDirectory = null)
	{
		string DotNet = DotNetExecutable ?? throw new UakSetupException($"The engine at {RootDirectory} has no bundled .NET SDK under {DotNetRootDirectory}{Path.DirectorySeparatorChar}<version>{Path.DirectorySeparatorChar}{Platform.DotNetRid}.");
		return new ProcessInvocation(DotNet, [UnrealBuildToolAssembly, .. arguments], workingDirectory, GetDotNetEnvironment());
	}

	/// <summary>Reads <see cref="BuildVersionFile"/>. Returns null when it is missing or not valid JSON.</summary>
	public EngineVersion? ReadVersion()
	{
		try
		{
			using FileStream Stream = new(BuildVersionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using JsonDocument Document = JsonDocument.Parse(Stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
			JsonElement Root = Document.RootElement;
			return new EngineVersion(
				GetInt(Root, "MajorVersion"),
				GetInt(Root, "MinorVersion"),
				GetInt(Root, "PatchVersion"),
				GetInt(Root, "Changelist"),
				Root.TryGetProperty("BranchName", out JsonElement Branch) && Branch.ValueKind == JsonValueKind.String ? Branch.GetString() ?? "" : "");
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}

	static int GetInt(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement Value) && Value.ValueKind == JsonValueKind.Number && Value.TryGetInt32(out int Number) ? Number : 0;

	static Version ParseVersion(string text) => Version.TryParse(text, out Version? Parsed) ? Parsed : new Version(0, 0);
}
