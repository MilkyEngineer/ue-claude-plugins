// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentKit.Core;

/// <summary>Where a project's editor is, and how that was decided.</summary>
/// <param name="CommandExecutable">The editor for command-line use (UnrealEditor-Cmd.exe or &lt;Target&gt;-Cmd.exe on Windows).</param>
/// <param name="Executable">The editor itself (UnrealEditor.exe or &lt;Target&gt;.exe on Windows).</param>
/// <param name="Target">The editor target the paths are for: the project's, or "UnrealEditor".</param>
/// <param name="How">How the paths were found, for `uak env` and error messages.</param>
public sealed record EditorLocation(string CommandExecutable, string Executable, string Target, string How);

/// <summary>
/// Finds the editor a project really runs. UBT names a target's binaries after its build environment
/// (UEBuildTarget.cs, the constructor and SetupBinaries):
/// <list type="bullet">
/// <item>Shared, the default for a modular editor: the engine's UnrealEditor, in Engine/Binaries/&lt;Platform&gt;.</item>
/// <item>Unique: the target's own name, &lt;Target&gt;-Cmd.exe, in the project's Binaries/&lt;Platform&gt; when the
/// .Target.cs is under the project (else the engine's).</item>
/// </list>
/// The environment can't be read from the .Target.cs text: a base class in another file can set it, as can
/// -UniqueBuildEnvironment, and UniqueIfNeeded is only settled by compiling the rules and comparing them with the base
/// target's (TargetRules.UpdateBuildEnvironmentIfNeeded). So the target receipt UBT writes when it builds the target comes
/// first: &lt;Binaries&gt;/&lt;Platform&gt;/&lt;Target&gt;.target, whose TargetBuildEnvironment, Launch and LaunchCmd are UBT's own answer.
/// Without one (the editor isn't built), a built &lt;Target&gt; editor is used if there is one, then the target file's
/// own BuildEnvironment, then the shared default.
/// </summary>
public static partial class EditorLocator
{
	/// <summary>The editor for <paramref name="projectFile"/> on <paramref name="engine"/>.</summary>
	/// <param name="engine">The engine.</param>
	/// <param name="projectFile">The .uproject, or null for no project (the engine's editor).</param>
	/// <param name="editorTarget">The editor target, or null to find it with <see cref="TargetResolver.ResolveEditorTarget"/>.</param>
	public static EditorLocation Locate(EngineLayout engine, string? projectFile, string? editorTarget = null)
	{
		const string SharedName = "UnrealEditor";
		EditorLocation Shared(string how) => new(engine.EditorCommandExecutable, engine.EditorExecutable, SharedName, how);

		if (projectFile is null)
		{
			return Shared("the engine's editor (no project)");
		}
		string ProjectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
		string Target;
		if (editorTarget is not null)
		{
			Target = editorTarget;
		}
		else
		{
			try
			{
				Target = TargetResolver.ResolveEditorTarget(projectFile);
			}
			catch (UakSetupException Error)
			{
				return Shared($"the engine's editor, because the project's editor target is unclear ({Error.Message})");
			}
		}
		if (Target.Equals(SharedName, StringComparison.OrdinalIgnoreCase))
		{
			return Shared("the engine's editor (the project has no Source, so no editor target of its own)");
		}

		string ProjectBinaries = Path.Combine(ProjectDirectory, "Binaries", engine.Platform.Name);
		foreach (string Binaries in new[] { ProjectBinaries, engine.BinariesDirectory })
		{
			string ReceiptFile = Path.Combine(Binaries, Target + ".target");
			if (ReadReceipt(ReceiptFile, engine, ProjectDirectory) is (string Launch, string LaunchCmd, string Environment))
			{
				if (File.Exists(LaunchCmd))
				{
					return new(LaunchCmd, Launch, Target, $"the {Target} receipt, {ReceiptFile} (TargetBuildEnvironment {Environment})");
				}
			}
		}

		foreach (string Binaries in new[] { ProjectBinaries, engine.BinariesDirectory })
		{
			string Command = engine.CommandExecutableIn(Binaries, Target);
			if (File.Exists(Command))
			{
				return new(Command, engine.ExecutableIn(Binaries, Target), Target, $"the built {Target} editor (no receipt; a unique build environment)");
			}
		}

		string TargetFile = Path.Combine(ProjectDirectory, "Source", Target + ".Target.cs");
		if (File.Exists(TargetFile) && UniqueEnvironmentPattern().IsMatch(TargetResolver.StripComments(File.ReadAllText(TargetFile))))
		{
			return new(engine.CommandExecutableIn(ProjectBinaries, Target), engine.ExecutableIn(ProjectBinaries, Target), Target,
				$"{Target}.Target.cs sets BuildEnvironment = TargetBuildEnvironment.Unique, and it isn't built yet");
		}
		return Shared($"the engine's editor: {Target} has no receipt and no built editor of its own, so a shared build environment is assumed (build {Target} to be sure)");
	}

	/// <summary>
	/// A target receipt's Launch, LaunchCmd (Launch where there is none, as on Linux and Mac) and TargetBuildEnvironment, with
	/// $(EngineDir) and $(ProjectDir) expanded. Null when the file is missing, unreadable, or not an editor's receipt.
	/// </summary>
	internal static (string Launch, string LaunchCmd, string Environment)? ReadReceipt(string receiptFile, EngineLayout engine, string projectDirectory)
	{
		try
		{
			using FileStream Stream = new(receiptFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using JsonDocument Document = JsonDocument.Parse(Stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
			JsonElement Root = Document.RootElement;
			if (GetString(Root, "TargetType") is string Type && !Type.Equals("Editor", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			string? Launch = GetString(Root, "Launch");
			if (Launch is null)
			{
				return null;
			}
			string? LaunchCmd = GetString(Root, "LaunchCmd") ?? Launch;
			string Environment = GetString(Root, "TargetBuildEnvironment") ?? "unknown";
			return (Expand(Launch, engine, projectDirectory), Expand(LaunchCmd, engine, projectDirectory), Environment);
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}

	/// <summary>Expands a receipt path's $(EngineDir) and $(ProjectDir) (TargetReceipt.cs writes paths under them) to a full path.</summary>
	static string Expand(string path, EngineLayout engine, string projectDirectory)
	{
		string Expanded = path
			.Replace("$(EngineDir)", engine.EngineDirectory, StringComparison.OrdinalIgnoreCase)
			.Replace("$(ProjectDir)", projectDirectory, StringComparison.OrdinalIgnoreCase)
			.Replace('/', Path.DirectorySeparatorChar)
			.Replace('\\', Path.DirectorySeparatorChar);
		return Path.GetFullPath(Expanded);
	}

	static string? GetString(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement Value) && Value.ValueKind == JsonValueKind.String ? Value.GetString() : null;

	[GeneratedRegex(@"\bBuildEnvironment\s*=\s*TargetBuildEnvironment\.Unique\b")]
	private static partial Regex UniqueEnvironmentPattern();
}
