// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core.Tests;

[TestClass]
public sealed class EngineAssociationTests
{
	[TestMethod]
	public void RegistryBuilds_MatchesGuidWithOrWithoutBraces()
	{
		FakeRegistry Registry = new FakeRegistry()
			.Set(RegistryRoot.CurrentUser, RegistryBuildsSource.Key, "{8A1C3B5E-1111-2222-3333-444455556666}", @"D:\UE\Source")
			.Set(RegistryRoot.CurrentUser, RegistryBuildsSource.Key, "{00000000-0000-0000-0000-000000000000}", @"D:\UE\Other");
		RegistryBuildsSource Source = new(Registry);

		EngineAssociationMatch With = Source.Find("{8a1c3b5e-1111-2222-3333-444455556666}").Single();
		Assert.AreEqual(@"D:\UE\Source", With.EngineRoot);
		StringAssert.Contains(With.Source, @"HKCU\Software\Epic Games\Unreal Engine\Builds");

		Assert.AreEqual(@"D:\UE\Source", Source.Find("8A1C3B5E-1111-2222-3333-444455556666").Single().EngineRoot);
		Assert.IsFalse(Source.Find("5.8").Any());
	}

	[TestMethod]
	public void RegistryInstalledDirectory_ReadsTheVersionKey()
	{
		FakeRegistry Registry = new FakeRegistry()
			.Set(RegistryRoot.LocalMachine, RegistryInstalledDirectorySource.Key + @"\5.8", "InstalledDirectory", @"C:\Program Files\Epic Games\UE_5.8");
		RegistryInstalledDirectorySource Source = new(Registry);

		EngineAssociationMatch Match = Source.Find("5.8").Single();
		Assert.AreEqual(@"C:\Program Files\Epic Games\UE_5.8", Match.EngineRoot);
		StringAssert.Contains(Match.Source, @"HKLM\SOFTWARE\EpicGames\Unreal Engine\5.8 InstalledDirectory");
		Assert.IsFalse(Source.Find("5.7").Any());
		Assert.IsFalse(Source.Find(@"..\5.8").Any());
	}

	[TestMethod]
	public void LauncherInstalled_MatchesAppNameUEVersion()
	{
		using TempTree Tree = new();
		string List = Tree.File("LauncherInstalled.dat", """
			{
				"InstallationList": [
					{ "InstallLocation": "C:\\Epic\\FabPlugin", "AppName": "FabPlugin_5.8" },
					{ "InstallLocation": "C:\\Epic\\UE_5.7", "AppName": "UE_5.7" },
					{ "InstallLocation": "C:\\Epic\\UE_5.8", "AppName": "UE_5.8", "AppVersion": "5.8.3" }
				]
			}
			""");
		LauncherInstalledSource Source = new(List);
		EngineAssociationMatch Match = Source.Find("5.8").Single();
		Assert.AreEqual(@"C:\Epic\UE_5.8", Match.EngineRoot);
		StringAssert.Contains(Match.Source, "UE_5.8");
		Assert.IsFalse(Source.Find("5.9").Any());
	}

	[TestMethod]
	public void LauncherInstalled_MissingOrBadFile_ListsNothing()
	{
		using TempTree Tree = new();
		Assert.IsFalse(new LauncherInstalledSource(Tree.Path("missing.dat")).Find("5.8").Any());
		Assert.IsFalse(new LauncherInstalledSource(Tree.File("bad.dat", "{ nope")).Find("5.8").Any());
		Assert.IsFalse(new LauncherInstalledSource(Tree.File("array.dat", "[]")).Find("5.8").Any());
	}

	[TestMethod]
	public void InstallIni_MatchesReleasedAndSourceBuilds_InTheInstallationsSectionOnly()
	{
		using TempTree Tree = new();
		string Ini = Tree.File("Install.ini", """
			[Other]
			5.8=/wrong/place

			[Installations]
			; a comment
			UE_5.8=/home/me/UnrealEngine/UE_5.8
			{A1B2C3D4-0000-0000-0000-000000000000}="/home/me/src/UnrealEngine"
			""");
		InstallIniSource Source = new(Ini);

		EngineAssociationMatch Released = Source.Find("5.8").Single();
		Assert.AreEqual("/home/me/UnrealEngine/UE_5.8", Released.EngineRoot);
		StringAssert.Contains(Released.Source, "[Installations] UE_5.8");

		Assert.AreEqual("/home/me/src/UnrealEngine", Source.Find("{a1b2c3d4-0000-0000-0000-000000000000}").Single().EngineRoot);
		Assert.IsFalse(Source.Find("5.7").Any());
		Assert.IsFalse(new InstallIniSource(Tree.Path("none.ini")).Find("5.8").Any());
	}

	[TestMethod]
	public void Composite_SearchesInOrder()
	{
		FakeAssociationSource First = new(("5.8", "first"));
		FakeAssociationSource Second = new(("5.8", "second"), ("5.7", "only-second"));
		CompositeEngineAssociationSource Composite = new([First, Second]);
		CollectionAssert.AreEqual(new[] { "first", "second" }, Composite.Find("5.8").Select(Match => Match.EngineRoot).ToArray());
		Assert.AreEqual("only-second", Composite.Find("5.7").Single().EngineRoot);
		StringAssert.Contains(Composite.Description, ", then ");
	}

	[TestMethod]
	public void ForWindows_SearchesBuildsThenInstalledDirectoryThenLauncherList()
	{
		using TempTree Tree = new();
		string ProgramData = Tree.Dir("ProgramData");
		Tree.File(Path.Combine("ProgramData", "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat"),
			"""{ "InstallationList": [ { "InstallLocation": "from-launcher", "AppName": "UE_5.8" } ] }""");
		FakeRegistry Registry = new FakeRegistry()
			.Set(RegistryRoot.LocalMachine, RegistryInstalledDirectorySource.Key + @"\5.8", "InstalledDirectory", "from-hklm")
			.Set(RegistryRoot.CurrentUser, RegistryBuildsSource.Key, "5.8", "from-hkcu");

		IEngineAssociationSource Source = EngineAssociationSources.ForWindows(Registry, ProgramData);
		CollectionAssert.AreEqual(new[] { "from-hkcu", "from-hklm", "from-launcher" }, Source.Find("5.8").Select(Match => Match.EngineRoot).ToArray());
	}

	[TestMethod]
	public void ForUnix_SearchesInstallIniThenLauncherList()
	{
		using TempTree Tree = new();
		string Settings = Tree.Dir("Epic");
		Tree.File(Path.Combine("Epic", "UnrealEngine", "Install.ini"), "[Installations]\nUE_5.8=from-ini\n");
		Tree.File(Path.Combine("Epic", "UnrealEngineLauncher", "LauncherInstalled.dat"),
			"""{ "InstallationList": [ { "InstallLocation": "from-launcher", "AppName": "UE_5.8" } ] }""");
		CollectionAssert.AreEqual(new[] { "from-ini", "from-launcher" }, EngineAssociationSources.ForUnix(Settings).Find("5.8").Select(Match => Match.EngineRoot).ToArray());
	}

	[TestMethod]
	public void UnixSettingsDirectory_IsConfigOnLinuxAndApplicationSupportOnMac()
	{
		Assert.AreEqual(Path.Combine("home", ".config", "Epic"), EngineAssociationSources.GetUnixSettingsDirectory("home", isMac: false));
		Assert.AreEqual(Path.Combine("home", "Library", "Application Support", "Epic"), EngineAssociationSources.GetUnixSettingsDirectory("home", isMac: true));
	}

	[TestMethod]
	public void ResolverUsesTheWindowsSourcesEndToEnd()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("Epic Games/UE_5.8");
		string Project = Tree.Project("Game/Game.uproject", "5.8");
		FakeRegistry Registry = new FakeRegistry()
			.Set(RegistryRoot.LocalMachine, RegistryInstalledDirectorySource.Key + @"\5.8", "InstalledDirectory", Engine);
		UakContext Context = UakContextResolver.Resolve(new UakResolveOptions
		{
			ProjectArgument = Project,
			CurrentDirectory = Tree.Root,
			GetEnvironmentVariable = new FakeEnvironment().Get,
			AssociationSource = EngineAssociationSources.ForWindows(Registry, Tree.Dir("ProgramData")),
			UserHomeDirectory = Tree.Dir("home"),
		});
		Assert.AreEqual(Engine, Context.EngineRoot!.FullName);
		StringAssert.Contains(Context.Provenance[UakContextResolver.EngineKey], "InstalledDirectory");
	}
}
