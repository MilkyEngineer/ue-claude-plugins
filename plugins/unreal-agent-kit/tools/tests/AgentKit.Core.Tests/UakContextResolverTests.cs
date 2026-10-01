// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core.Tests;

[TestClass]
public sealed class UakContextResolverTests
{
	static UakResolveOptions Options(TempTree tree, string currentDirectory, FakeEnvironment? environment = null, IEngineAssociationSource? source = null,
		string? project = null, string? engine = null, bool requireEngine = true, Func<string, bool>? isWritable = null, string? vcs = null)
	{
		FakeEnvironment Environment = environment ?? [];
		return new UakResolveOptions
		{
			ProjectArgument = project,
			EngineArgument = engine,
			VersionControlArgument = vcs,
			RequireEngine = requireEngine,
			CurrentDirectory = currentDirectory,
			GetEnvironmentVariable = Environment.Get,
			AssociationSource = source ?? new FakeAssociationSource(),
			UserHomeDirectory = tree.Dir("home"),
			IsWritable = isWritable,
		};
	}

	// ---- Project ----

	[TestMethod]
	public void ProjectArgument_File_IsUsed()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string Project = Tree.Project("Game/Game.uproject");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Project, engine: Engine));
		Assert.AreEqual(Project, Context.ProjectFile!.FullName);
		Assert.AreEqual("-project argument", Context.Provenance[UakContextResolver.ProjectKey]);
	}

	[TestMethod]
	public void ProjectArgument_Relative_ResolvesAgainstCurrentDirectory()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string Project = Tree.Project("Game/Game.uproject");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Path("Game"), project: "Game.uproject", engine: Engine));
		Assert.AreEqual(Project, Context.ProjectFile!.FullName);
	}

	[TestMethod]
	public void ProjectArgument_DirectoryWithOneProject_IsUsed()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string Project = Tree.Project("Game/Game.uproject");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Tree.Path("Game"), engine: Engine));
		Assert.AreEqual(Project, Context.ProjectFile!.FullName);
	}

	[TestMethod]
	public void ProjectArgument_DirectoryWithTwoProjects_IsAnErrorNamingThem()
	{
		using TempTree Tree = new();
		Tree.Project("Game/A.uproject");
		Tree.Project("Game/B.uproject");
		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Tree.Path("Game"), requireEngine: false)));
		StringAssert.Contains(Error.Message, "A.uproject");
		StringAssert.Contains(Error.Message, "B.uproject");
	}

	[TestMethod]
	public void ProjectArgument_DirectoryWithNoProject_IsAnError()
	{
		using TempTree Tree = new();
		Tree.Dir("Empty");
		Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Tree.Path("Empty"), requireEngine: false)));
	}

	[TestMethod]
	public void ProjectArgument_Missing_IsAnError()
	{
		using TempTree Tree = new();
		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, project: "Nope.uproject", requireEngine: false)));
		StringAssert.Contains(Error.Message, "does not exist");
	}

	[TestMethod]
	public void ProjectArgument_NotAProjectFile_IsAnError()
	{
		using TempTree Tree = new();
		string Text = Tree.File("notes.txt", "hi");
		Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Text, requireEngine: false)));
	}

	[TestMethod]
	public void ProjectVariable_IsUsed_WhenNoArgument()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string Project = Tree.Project("Game/Game.uproject");
		FakeEnvironment Environment = new() { [UakContextResolver.ProjectVariable] = Project };
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, Environment, engine: Engine));
		Assert.AreEqual(Project, Context.ProjectFile!.FullName);
		Assert.AreEqual("UAK_PROJECT environment variable", Context.Provenance[UakContextResolver.ProjectKey]);
	}

	[TestMethod]
	public void ProjectArgument_BeatsVariable()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string A = Tree.Project("A/A.uproject");
		string B = Tree.Project("B/B.uproject");
		FakeEnvironment Environment = new() { [UakContextResolver.ProjectVariable] = B };
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, Environment, project: A, engine: Engine));
		Assert.AreEqual(A, Context.ProjectFile!.FullName);
	}

	[TestMethod]
	public void Project_IsFoundByWalkingUp()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string Project = Tree.Project("Game/Game.uproject");
		string Deep = Tree.Dir("Game", "Source", "Game", "Private");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Deep, engine: Engine));
		Assert.AreEqual(Project, Context.ProjectFile!.FullName);
		StringAssert.StartsWith(Context.Provenance[UakContextResolver.ProjectKey], "found by walking up");
	}

	[TestMethod]
	public void Project_WalkingUpIntoTwoProjects_IsAnError()
	{
		using TempTree Tree = new();
		Tree.Project("Game/A.uproject");
		Tree.Project("Game/B.uproject");
		string Deep = Tree.Dir("Game", "Source");
		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Deep, requireEngine: false)));
		StringAssert.Contains(Error.Message, "more than one .uproject");
	}

	[TestMethod]
	public void Project_NoneFound_IsNullWithAReason()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Dir("Elsewhere"), engine: Engine));
		Assert.IsNull(Context.ProjectFile);
		StringAssert.StartsWith(Context.Provenance[UakContextResolver.ProjectKey], "no .uproject");
	}

	// ---- Engine ----

	[TestMethod]
	public void EngineArgument_Root_IsUsed()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), engine: Engine));
		Assert.AreEqual(Engine, Context.EngineRoot!.FullName);
		Assert.AreEqual("-engine argument", Context.Provenance[UakContextResolver.EngineKey]);
	}

	[TestMethod]
	public void EngineArgument_EngineFolder_GivesTheRoot()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), engine: Path.Combine(Engine, "Engine") + Path.DirectorySeparatorChar));
		Assert.AreEqual(Engine, Context.EngineRoot!.FullName);
	}

	[TestMethod]
	public void EngineArgument_NotAnEngine_IsAnError_EvenWhenNotRequired()
	{
		using TempTree Tree = new();
		string NotEngine = Tree.Dir("NotEngine");
		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, engine: NotEngine, requireEngine: false)));
		StringAssert.Contains(Error.Message, "-engine");
	}

	[TestMethod]
	public void EngineVariable_IsUsed_AndArgumentBeatsIt()
	{
		using TempTree Tree = new();
		string A = Tree.Engine("A");
		string B = Tree.Engine("B");
		FakeEnvironment Environment = new() { [UakContextResolver.EngineVariable] = B };
		UakContext FromVariable = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), Environment));
		Assert.AreEqual(B, FromVariable.EngineRoot!.FullName);
		Assert.AreEqual("UAK_ENGINE environment variable", FromVariable.Provenance[UakContextResolver.EngineKey]);

		UakContext FromArgument = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), Environment, engine: A));
		Assert.AreEqual(A, FromArgument.EngineRoot!.FullName);
	}

	[TestMethod]
	public void Engine_ContainingTheProject_IsUsed_BeforeTheAssociation()
	{
		using TempTree Tree = new();
		string Source = Tree.Engine("UESource");
		string Other = Tree.Engine("UEOther");
		string Project = Tree.Project("UESource/Games/Game/Game.uproject", "5.8");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, source: new FakeAssociationSource(("5.8", Other)), project: Project));
		Assert.AreEqual(Source, Context.EngineRoot!.FullName);
		Assert.AreEqual("the engine that contains the project", Context.Provenance[UakContextResolver.EngineKey]);
	}

	[TestMethod]
	public void Engine_FromAssociationSource()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("Installs/UE_5.8");
		string Project = Tree.Project("Game/Game.uproject", "5.8");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, source: new FakeAssociationSource(("5.8", Engine)), project: Project));
		Assert.AreEqual(Engine, Context.EngineRoot!.FullName);
		StringAssert.Contains(Context.Provenance[UakContextResolver.EngineKey], "EngineAssociation \"5.8\"");
		StringAssert.Contains(Context.Provenance[UakContextResolver.EngineKey], "fake:5.8");
	}

	[TestMethod]
	public void Engine_FromAssociationSource_SkipsEntriesThatAreNotEngines()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("Installs/Good");
		string Project = Tree.Project("Game/Game.uproject", "5.8");
		FakeAssociationSource Source = new(("5.8", Tree.Path("Installs", "Deleted")), ("5.8", Engine));
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, source: Source, project: Project));
		Assert.AreEqual(Engine, Context.EngineRoot!.FullName);
	}

	[TestMethod]
	public void Engine_AssociationNotFound_IsAnErrorWhenRequired()
	{
		using TempTree Tree = new();
		string Project = Tree.Project("Game/Game.uproject", "9.9");
		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Project)));
		StringAssert.Contains(Error.Message, "\"9.9\"");
		StringAssert.Contains(Error.Message, "the fake association list");
	}

	[TestMethod]
	public void Engine_AssociationNotFound_IsNullWhenNotRequired()
	{
		using TempTree Tree = new();
		string Project = Tree.Project("Game/Game.uproject", "9.9");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Project, requireEngine: false));
		Assert.IsNull(Context.EngineRoot);
		Assert.IsNull(Context.Engine);
		StringAssert.Contains(Context.Provenance[UakContextResolver.EngineKey], "\"9.9\"");
	}

	[TestMethod]
	public void Engine_OnlyInvalidAssociationEntries_NamesThemInTheError()
	{
		using TempTree Tree = new();
		string Project = Tree.Project("Game/Game.uproject", "5.8");
		FakeAssociationSource Source = new(("5.8", Tree.Path("Gone")));
		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, source: Source, project: Project)));
		StringAssert.Contains(Error.Message, Tree.Path("Gone"));
	}

	[TestMethod]
	public void Engine_PathLikeAssociation_IsRelativeToTheProject()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("Engines/Custom");
		string Project = Tree.Project("Game/Game.uproject", "../Engines/Custom");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Project));
		Assert.AreEqual(Engine, Context.EngineRoot!.FullName);
		StringAssert.Contains(Context.Provenance[UakContextResolver.EngineKey], "as a path");
	}

	[TestMethod]
	public void Engine_PathLikeAssociation_ToNothing_IsAnError()
	{
		using TempTree Tree = new();
		string Project = Tree.Project("Game/Game.uproject", "../Engines/Missing");
		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Project)));
		StringAssert.Contains(Error.Message, "is not an engine");
	}

	[TestMethod]
	public void Engine_EmptyAssociation_OutsideAnyEngine_IsAnError()
	{
		using TempTree Tree = new();
		string Project = Tree.Project("Game/Game.uproject", "");
		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Project)));
		StringAssert.Contains(Error.Message, "empty EngineAssociation");
	}

	[TestMethod]
	public void Engine_BadProjectJson_IsASetupError()
	{
		using TempTree Tree = new();
		string Project = Tree.File("Game/Game.uproject", "{ not json");
		Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Project)));
	}

	[TestMethod]
	public void Engine_NoProjectAndNoEngine_IsAnErrorOnlyWhenRequired()
	{
		using TempTree Tree = new();
		string Cwd = Tree.Dir("cwd");
		Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Cwd)));
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Cwd, requireEngine: false));
		Assert.IsNull(Context.ProjectFile);
		Assert.IsNull(Context.EngineRoot);
	}

	[TestMethod]
	[DataRow("5.8", false)]
	[DataRow("{A1B2C3D4-0000-0000-0000-000000000000}", false)]
	[DataRow("../UE", true)]
	[DataRow(@"..\UE", true)]
	[DataRow("./UE", true)]
	[DataRow(".", true)]
	public void IsPathLikeAssociation(string association, bool expected) =>
		Assert.AreEqual(expected, UakContextResolver.IsPathLikeAssociation(association));

	// ---- State directory ----

	[TestMethod]
	public void State_IsTheProjectsSavedFolder()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string Project = Tree.Project("Game/Game.uproject");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Project, engine: Engine));
		Assert.AreEqual(Tree.Path("Game", "Saved", "AgentKit"), Context.StateDirectory.FullName);
		// Resolving creates nothing: writers create the folder.
		Assert.IsFalse(Directory.Exists(Tree.Path("Game", "Saved")));
		Assert.AreEqual("the project's Saved folder", Context.Provenance[UakContextResolver.StateKey]);
	}

	[TestMethod]
	public void State_WithoutProject_IsTheEnginesSavedFolder_WhenWritable()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), engine: Engine));
		Assert.AreEqual(Path.Combine(Engine, "Engine", "Saved", "AgentKit"), Context.StateDirectory.FullName);
	}

	[TestMethod]
	public void State_UnwritableProject_FallsBackToTheEngine()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string Project = Tree.Project("Game/Game.uproject");
		string ProjectState = Tree.Path("Game", "Saved", "AgentKit");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Project, engine: Engine, isWritable: Path => Path != ProjectState));
		// The project's own folder in the engine's, never the engine's folder itself: another project may use that.
		Assert.AreEqual(Path.Combine(Engine, "Engine", "Saved", "AgentKit", "Projects", UakContextResolver.GetStateKey(Tree.Path("Game"))), Context.StateDirectory.FullName);
		StringAssert.Contains(Context.Provenance[UakContextResolver.StateKey], "the project's key");
	}

	[TestMethod]
	public void State_TwoProjectsOnOneEngine_NeverShareAFallbackFolder()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string First = Tree.Project("First/First.uproject");
		string Second = Tree.Project("Second/Second.uproject");
		string EngineState = Path.Combine(Engine, "Engine", "Saved", "AgentKit");
		bool NoProjectSaved(string Path) => !Path.Contains(System.IO.Path.Combine("First", "Saved"), StringComparison.OrdinalIgnoreCase)
			&& !Path.Contains(System.IO.Path.Combine("Second", "Saved"), StringComparison.OrdinalIgnoreCase);

		// The engine's Saved folder: one subfolder per project.
		string FirstInEngine = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: First, engine: Engine, isWritable: NoProjectSaved)).StateDirectory.FullName;
		string SecondInEngine = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Second, engine: Engine, isWritable: NoProjectSaved)).StateDirectory.FullName;
		Assert.AreNotEqual(FirstInEngine, SecondInEngine);
		StringAssert.StartsWith(FirstInEngine, Path.Combine(EngineState, "Projects"));
		StringAssert.StartsWith(SecondInEngine, Path.Combine(EngineState, "Projects"));

		// The user's folder: one per project, apart from the engine's own key.
		Func<string, bool> NothingInTheEngine = Path => NoProjectSaved(Path) && !Path.StartsWith(EngineState, StringComparison.OrdinalIgnoreCase);
		UakContext FirstInHome = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: First, engine: Engine, isWritable: NothingInTheEngine));
		UakContext SecondInHome = UakContextResolver.Resolve(Options(Tree, Tree.Root, project: Second, engine: Engine, isWritable: NothingInTheEngine));
		string UserState = Path.Combine(Tree.Path("home"), ".unreal-agent-kit", "State");
		Assert.AreEqual(Path.Combine(UserState, UakContextResolver.GetStateKey(Tree.Path("First"))), FirstInHome.StateDirectory.FullName);
		Assert.AreEqual(Path.Combine(UserState, UakContextResolver.GetStateKey(Tree.Path("Second"))), SecondInHome.StateDirectory.FullName);
		Assert.AreNotEqual(FirstInHome.StateDirectory.FullName, SecondInHome.StateDirectory.FullName);
		Assert.AreNotEqual(Path.Combine(UserState, UakContextResolver.GetEngineStateKey(Engine)), FirstInHome.StateDirectory.FullName);
		StringAssert.Contains(FirstInHome.Provenance[UakContextResolver.StateKey], "the project's key");
	}

	[TestMethod]
	public void State_UnwritableEngine_FallsBackToTheUsersFolder_KeyedByEngine()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string EngineState = Path.Combine(Engine, "Engine", "Saved", "AgentKit");
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), engine: Engine, isWritable: Path => Path != EngineState));
		string Expected = Path.Combine(Tree.Path("home"), ".unreal-agent-kit", "State", UakContextResolver.GetEngineStateKey(Engine));
		Assert.AreEqual(Expected, Context.StateDirectory.FullName);
		StringAssert.Contains(Context.Provenance[UakContextResolver.StateKey], "cannot write " + EngineState);
	}

	[TestMethod]
	public void State_NoProjectOrEngine_IsTheUsersNoneFolder()
	{
		using TempTree Tree = new();
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), requireEngine: false));
		Assert.AreEqual(Path.Combine(Tree.Path("home"), ".unreal-agent-kit", "State", "none"), Context.StateDirectory.FullName);
		Assert.IsFalse(Context.StateDirectory.Exists, "Resolving creates nothing: writers create the folder.");
	}

	[TestMethod]
	public void State_HomeVariable_OverridesTheUsersFolder()
	{
		using TempTree Tree = new();
		string Home = Tree.Dir("kit-home");
		FakeEnvironment Environment = new() { [UakContextResolver.HomeVariable] = Home };
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), Environment, requireEngine: false));
		Assert.AreEqual(Path.Combine(Home, "State", "none"), Context.StateDirectory.FullName);
		StringAssert.Contains(Context.Provenance[UakContextResolver.StateKey], "UAK_HOME");
	}

	[TestMethod]
	public void State_NothingWritable_IsASetupError()
	{
		using TempTree Tree = new();
		Assert.ThrowsExactly<UakSetupException>(() => UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), requireEngine: false, isWritable: _ => false)));
	}

	[TestMethod]
	public void EngineStateKey_IsStableShortAndPerEngine()
	{
		string A = Path.Combine(Path.GetTempPath(), "EngineA");
		string B = Path.Combine(Path.GetTempPath(), "EngineB");
		string KeyA = UakContextResolver.GetEngineStateKey(A);
		Assert.AreEqual(16, KeyA.Length);
		Assert.IsTrue(KeyA.All(Char.IsAsciiHexDigitLower));
		Assert.AreEqual(KeyA, UakContextResolver.GetEngineStateKey(A));
		Assert.AreEqual(KeyA, UakContextResolver.GetEngineStateKey(A + Path.DirectorySeparatorChar));
		Assert.AreNotEqual(KeyA, UakContextResolver.GetEngineStateKey(B));
	}

	[TestMethod]
	public void EngineStateKey_IgnoresCase_OnlyWhereAsked()
	{
		string Lower = Path.Combine(Path.GetTempPath(), "engine");
		string Upper = Path.Combine(Path.GetTempPath(), "ENGINE");
		Assert.AreEqual(UakContextResolver.GetEngineStateKey(Lower, caseInsensitivePaths: true), UakContextResolver.GetEngineStateKey(Upper, caseInsensitivePaths: true));
		Assert.AreNotEqual(UakContextResolver.GetEngineStateKey(Lower, caseInsensitivePaths: false), UakContextResolver.GetEngineStateKey(Upper, caseInsensitivePaths: false));
	}

	[TestMethod]
	public void EngineStateKey_KnownValue()
	{
		// Pinned so a change to the hashing, which would orphan every existing state folder, is deliberate.
		string Root = OperatingSystem.IsWindows() ? @"C:\Program Files\Epic Games\UE_5.8" : "/opt/UE_5.8";
		string Expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Root.ToLowerInvariant() : Root)))[..16];
		Assert.AreEqual(Expected, UakContextResolver.GetEngineStateKey(Root));
	}

	// ---- Version control and context ----

	[TestMethod]
	public void Vcs_ArgumentBeatsVariable_AndIsRecorded()
	{
		using TempTree Tree = new();
		FakeEnvironment Environment = new() { [UakContextResolver.VcsVariable] = "perforce" };
		UakContext FromVariable = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), Environment, requireEngine: false));
		Assert.AreEqual("perforce", FromVariable.RequestedVersionControl);
		Assert.AreEqual("UAK_VCS environment variable", FromVariable.Provenance[UakContextResolver.VcsKey]);

		UakContext FromArgument = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), Environment, requireEngine: false, vcs: "git"));
		Assert.AreEqual("git", FromArgument.RequestedVersionControl);
		Assert.AreEqual("-vcs argument", FromArgument.Provenance[UakContextResolver.VcsKey]);
	}

	[TestMethod]
	public void Vcs_NotRequested_IsNull()
	{
		using TempTree Tree = new();
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), requireEngine: false));
		Assert.IsNull(Context.RequestedVersionControl);
		Assert.IsFalse(Context.Provenance.ContainsKey(UakContextResolver.VcsKey));
	}

	[TestMethod]
	public void Context_RequireHelpers_ThrowSetupErrors()
	{
		using TempTree Tree = new();
		UakContext Context = UakContextResolver.Resolve(Options(Tree, Tree.Dir("cwd"), requireEngine: false));
		Assert.ThrowsExactly<UakSetupException>(() => Context.RequireEngine());
		Assert.ThrowsExactly<UakSetupException>(() => Context.RequireProject());
		Assert.AreEqual(Tree.Path("cwd"), Context.WorkingDirectory.FullName);
	}
}
