// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core.Tests;

[TestClass]
public sealed class EditorLocatorTests
{
	/// <summary>A source engine with a Game project inside it, an editor target GameEditor, and the shared editor built.</summary>
	sealed class Setup : IDisposable
	{
		public Setup(UnrealPlatform? platform = null, string editorTargetBody = "Type = TargetType.Editor;")
		{
			Root = Tree.Engine("UE");
			Layout = new EngineLayout(Root, platform ?? UnrealPlatform.Win64);
			ProjectFile = Tree.Project(System.IO.Path.Combine("UE", "Game", "Game.uproject"), "");
			ProjectDirectory = System.IO.Path.GetDirectoryName(ProjectFile)!;
			Tree.File(System.IO.Path.Combine("UE", "Game", "Source", "GameEditor.Target.cs"),
				$"public class GameEditorTarget : GameBaseTarget {{ public GameEditorTarget(TargetInfo Target) : base(Target) {{ {editorTargetBody} }} }}");
			Touch(Layout.EditorCommandExecutable);
		}

		public TempTree Tree { get; } = new();
		public string Root { get; }
		public EngineLayout Layout { get; }
		public string ProjectFile { get; }
		public string ProjectDirectory { get; }
		public string ProjectBinaries => System.IO.Path.Combine(ProjectDirectory, "Binaries", Layout.Platform.Name);

		public string Touch(string path)
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			File.WriteAllText(path, "");
			return path;
		}

		/// <summary>Writes a receipt as UBT does, with $(ProjectDir) or $(EngineDir) paths.</summary>
		public string Receipt(string binariesDirectory, string target, string environment, string launch, string? launchCmd, string type = "Editor")
		{
			string Cmd = launchCmd is null ? "" : $", \"LaunchCmd\": \"{launchCmd}\"";
			string ReceiptFile = Touch(System.IO.Path.Combine(binariesDirectory, target + ".target"));
			File.WriteAllText(ReceiptFile, $$"""{ "TargetName": "{{target}}", "TargetBuildEnvironment": "{{environment}}", "TargetType": "{{type}}", "Launch": "{{launch}}"{{Cmd}} }""");
			return ReceiptFile;
		}

		public void Dispose() => Tree.Dispose();
	}

	[TestMethod]
	public void AUniqueReceiptNamesTheProjectsOwnEditor()
	{
		using Setup Box = new();
		string Cmd = Box.Touch(Path.Combine(Box.ProjectBinaries, "GameEditor-Cmd.exe"));
		Box.Receipt(Box.ProjectBinaries, "GameEditor", "Unique", "$(ProjectDir)/Binaries/Win64/GameEditor.exe", "$(ProjectDir)/Binaries/Win64/GameEditor-Cmd.exe");

		EditorLocation Editor = EditorLocator.Locate(Box.Layout, Box.ProjectFile);
		Assert.AreEqual(Cmd, Editor.CommandExecutable);
		Assert.AreEqual(Path.Combine(Box.ProjectBinaries, "GameEditor.exe"), Editor.Executable);
		Assert.AreEqual("GameEditor", Editor.Target);
		Assert.Contains("receipt", Editor.How);
		Assert.Contains("Unique", Editor.How);
	}

	[TestMethod]
	public void ASharedReceiptNamesTheEnginesEditor()
	{
		using Setup Box = new();
		Box.Receipt(Box.ProjectBinaries, "GameEditor", "Shared", "$(EngineDir)/Binaries/Win64/UnrealEditor.exe", "$(EngineDir)/Binaries/Win64/UnrealEditor-Cmd.exe");

		EditorLocation Editor = EditorLocator.Locate(Box.Layout, Box.ProjectFile);
		Assert.AreEqual(Box.Layout.EditorCommandExecutable, Editor.CommandExecutable);
		Assert.Contains("Shared", Editor.How);
	}

	[TestMethod]
	public void AReceiptInTheEnginesBinariesCounts()
	{
		using Setup Box = new();
		string Cmd = Box.Touch(Path.Combine(Box.Layout.BinariesDirectory, "GameEditor-Cmd.exe"));
		Box.Receipt(Box.Layout.BinariesDirectory, "GameEditor", "Unique", "$(EngineDir)/Binaries/Win64/GameEditor.exe", "$(EngineDir)/Binaries/Win64/GameEditor-Cmd.exe");

		Assert.AreEqual(Cmd, EditorLocator.Locate(Box.Layout, Box.ProjectFile).CommandExecutable);
	}

	[TestMethod]
	public void AStaleReceiptOrAnotherTypesReceiptIsIgnored()
	{
		using Setup Box = new();
		// The receipt names an editor that isn't there: the shared default is used.
		Box.Receipt(Box.ProjectBinaries, "GameEditor", "Unique", "$(ProjectDir)/Binaries/Win64/GameEditor.exe", "$(ProjectDir)/Binaries/Win64/GameEditor-Cmd.exe");
		Assert.AreEqual(Box.Layout.EditorCommandExecutable, EditorLocator.Locate(Box.Layout, Box.ProjectFile).CommandExecutable);

		// A game target's receipt under the editor's name is not an editor's.
		Box.Touch(Path.Combine(Box.ProjectBinaries, "GameEditor-Cmd.exe"));
		Box.Receipt(Box.ProjectBinaries, "GameEditor", "Unique", "$(ProjectDir)/Binaries/Win64/Game.exe", null, type: "Game");
		EditorLocation Editor = EditorLocator.Locate(Box.Layout, Box.ProjectFile);
		Assert.AreEqual(Path.Combine(Box.ProjectBinaries, "GameEditor-Cmd.exe"), Editor.CommandExecutable);
		Assert.Contains("no receipt", Editor.How);
	}

	[TestMethod]
	public void ABuiltUniqueEditorWithoutAReceiptIsFound()
	{
		using Setup Box = new();
		string Cmd = Box.Touch(Path.Combine(Box.ProjectBinaries, "GameEditor-Cmd.exe"));

		EditorLocation Editor = EditorLocator.Locate(Box.Layout, Box.ProjectFile);
		Assert.AreEqual(Cmd, Editor.CommandExecutable);
		Assert.AreEqual(Path.Combine(Box.ProjectBinaries, "GameEditor.exe"), Editor.Executable);
	}

	[TestMethod]
	public void BeforeABuildTheTargetFilesOwnUniqueSettingIsTrusted()
	{
		using Setup Box = new(editorTargetBody: "Type = TargetType.Editor; BuildEnvironment = TargetBuildEnvironment.Unique;");
		EditorLocation Editor = EditorLocator.Locate(Box.Layout, Box.ProjectFile);
		Assert.AreEqual(Path.Combine(Box.ProjectBinaries, "GameEditor-Cmd.exe"), Editor.CommandExecutable);
		Assert.Contains("isn't built yet", Editor.How);

		// Commented out, it doesn't count.
		using Setup Commented = new(editorTargetBody: "Type = TargetType.Editor; // BuildEnvironment = TargetBuildEnvironment.Unique;");
		Assert.AreEqual(Commented.Layout.EditorCommandExecutable, EditorLocator.Locate(Commented.Layout, Commented.ProjectFile).CommandExecutable);
	}

	[TestMethod]
	public void WithNothingToGoOnTheSharedEditorIsAssumed()
	{
		// A base class in another file may set Unique: the text can't show it, so the answer says to build first.
		using Setup Box = new();
		EditorLocation Editor = EditorLocator.Locate(Box.Layout, Box.ProjectFile);
		Assert.AreEqual(Box.Layout.EditorCommandExecutable, Editor.CommandExecutable);
		Assert.AreEqual("UnrealEditor", Editor.Target);
		Assert.Contains("build GameEditor", Editor.How);
	}

	[TestMethod]
	public void NoProjectOrNoSourceUsesTheEnginesEditor()
	{
		using Setup Box = new();
		Assert.AreEqual(Box.Layout.EditorCommandExecutable, EditorLocator.Locate(Box.Layout, null).CommandExecutable);

		Directory.Delete(Path.Combine(Box.ProjectDirectory, "Source"), recursive: true);
		EditorLocation Editor = EditorLocator.Locate(Box.Layout, Box.ProjectFile);
		Assert.AreEqual(Box.Layout.EditorCommandExecutable, Editor.CommandExecutable);
		Assert.Contains("no Source", Editor.How);
	}

	[TestMethod]
	public void AnUnclearEditorTargetFallsBackToTheEnginesEditor()
	{
		using Setup Box = new();
		Box.Tree.File(Path.Combine("UE", "Game", "Source", "OtherEditor.Target.cs"), "Type = TargetType.Editor;");
		File.Delete(Path.Combine(Box.ProjectDirectory, "Source", "GameEditor.Target.cs"));
		Box.Tree.File(Path.Combine("UE", "Game", "Source", "ThirdEditor.Target.cs"), "Type = TargetType.Editor;");

		EditorLocation Editor = EditorLocator.Locate(Box.Layout, Box.ProjectFile);
		Assert.AreEqual(Box.Layout.EditorCommandExecutable, Editor.CommandExecutable);
		Assert.Contains("unclear", Editor.How);

		// A target given by the caller wins.
		string Cmd = Box.Touch(Path.Combine(Box.ProjectBinaries, "OtherEditor-Cmd.exe"));
		Assert.AreEqual(Cmd, EditorLocator.Locate(Box.Layout, Box.ProjectFile, "OtherEditor").CommandExecutable);
	}

	[TestMethod]
	public void LinuxAndMacReceiptsHaveNoLaunchCmd()
	{
		using Setup Linux = new(UnrealPlatform.Linux);
		string Editor = Linux.Touch(Path.Combine(Linux.ProjectBinaries, "GameEditor"));
		Linux.Receipt(Linux.ProjectBinaries, "GameEditor", "Unique", "$(ProjectDir)/Binaries/Linux/GameEditor", launchCmd: null);
		EditorLocation Found = EditorLocator.Locate(Linux.Layout, Linux.ProjectFile);
		Assert.AreEqual(Editor, Found.CommandExecutable);
		Assert.AreEqual(Editor, Found.Executable);

		using Setup Mac = new(UnrealPlatform.Mac);
		string MacEditor = Mac.Touch(Path.Combine(Mac.ProjectBinaries, "GameEditor.app", "Contents", "MacOS", "GameEditor"));
		Assert.AreEqual(MacEditor, EditorLocator.Locate(Mac.Layout, Mac.ProjectFile).CommandExecutable);
	}

	[TestMethod]
	public void NamesFollowThePlatform()
	{
		using TempTree Tree = new();
		string Root = Tree.Engine("UE");
		string Binaries = Tree.Path("Game", "Binaries");
		Assert.AreEqual(Path.Combine(Binaries, "GameEditor-Cmd.exe"), new EngineLayout(Root, UnrealPlatform.Win64).CommandExecutableIn(Binaries, "GameEditor"));
		Assert.AreEqual(Path.Combine(Binaries, "GameEditor.exe"), new EngineLayout(Root, UnrealPlatform.Win64).ExecutableIn(Binaries, "GameEditor"));
		Assert.AreEqual(Path.Combine(Binaries, "GameEditor"), new EngineLayout(Root, UnrealPlatform.Linux).CommandExecutableIn(Binaries, "GameEditor"));
		Assert.AreEqual(Path.Combine(Binaries, "GameEditor.app", "Contents", "MacOS", "GameEditor"), new EngineLayout(Root, UnrealPlatform.Mac).CommandExecutableIn(Binaries, "GameEditor"));
	}
}
