// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Text.Json;

namespace AgentKit.Core.Tests;

/// <summary>
/// The plugin's SessionStart hook (hooks/check-uak.sh), run through a real `sh` when one is on PATH (Git Bash on Windows).
/// Every folder it reads is a temporary one: the plugin root, the project and UAK_HOME.
/// </summary>
[TestClass]
public sealed class HookTests
{
	const string Version = "9.8.7";

	public TestContext TestContext { get; set; } = null!;

	/// <summary>hooks/check-uak.sh in this source tree: the tests run from tools/bin/..., below the plugin folder.</summary>
	static string FindHook()
	{
		for (DirectoryInfo? Directory = new(AppContext.BaseDirectory); Directory is not null; Directory = Directory.Parent)
		{
			string Candidate = Path.Combine(Directory.FullName, "hooks", "check-uak.sh");
			if (File.Exists(Candidate))
			{
				return Candidate;
			}
		}
		throw new AssertFailedException("hooks/check-uak.sh not found above " + AppContext.BaseDirectory);
	}

	static string RequireShell()
	{
		string? Shell = ExecutableLocator.FindOnPath("sh") ?? (OperatingSystem.IsWindows() ? FindGitForWindowsShell() : null);
		if (Shell is null)
		{
			Assert.Inconclusive("No sh on PATH, and no Git for Windows sh found (Claude Code runs hooks through it): the hook can't be run here.");
		}
		return Shell!;
	}

	/// <summary>
	/// Git for Windows' sh, which Claude Code runs hooks through. PowerShell and cmd usually have only Git's cmd folder on PATH
	/// (cmd/git.exe), so sh is looked for beside it: bin/sh.exe and usr/bin/sh.exe under the Git folder.
	/// </summary>
	static string? FindGitForWindowsShell()
	{
		List<string> GitRoots = [];
		if (ExecutableLocator.FindOnPath("git") is string Git)
		{
			// <Git>/cmd/git.exe, <Git>/bin/git.exe or <Git>/mingw64/bin/git.exe.
			for (DirectoryInfo? Directory = new FileInfo(Git).Directory; Directory is not null; Directory = Directory.Parent)
			{
				GitRoots.Add(Directory.FullName);
			}
		}
		GitRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"));
		return GitRoots
			.SelectMany(Root => new[] { Path.Combine(Root, "bin", "sh.exe"), Path.Combine(Root, "usr", "bin", "sh.exe") })
			.FirstOrDefault(File.Exists);
	}

	/// <summary>A plugin root holding only .claude-plugin/plugin.json, as the real one is laid out (one field per line).</summary>
	static string PluginRoot(TempTree tree) => Path.GetDirectoryName(Path.GetDirectoryName(tree.File(Path.Combine("Plugin", ".claude-plugin", "plugin.json"),
		$$"""
		{
		  "name": "unreal-agent-kit",
		  "version": "{{Version}}",
		  "description": "test"
		}
		"""))!)!;

	async Task<(int ExitCode, string Output, string Errors)> RunHookAsync(string pluginRoot, string projectDirectory, string uakHome, string? uakEngine = null)
	{
		ProcessStartInfo Start = new(RequireShell())
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		Start.ArgumentList.Add(FindHook());
		Start.Environment["CLAUDE_PLUGIN_ROOT"] = pluginRoot;
		Start.Environment["CLAUDE_PROJECT_DIR"] = projectDirectory;
		Start.Environment["UAK_HOME"] = uakHome;
		// Never the machine's own UAK_ENGINE: the hook would find that engine instead of the test's.
		Start.Environment.Remove("UAK_ENGINE");
		if (uakEngine is not null)
		{
			Start.Environment["UAK_ENGINE"] = uakEngine;
		}
		using Process Hook = Process.Start(Start)!;
		Task<string> Output = Hook.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
		Task<string> Errors = Hook.StandardError.ReadToEndAsync(TestContext.CancellationToken);
		using CancellationTokenSource Timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
		Timeout.CancelAfter(TimeSpan.FromSeconds(30));
		await Hook.WaitForExitAsync(Timeout.Token);
		return (Hook.ExitCode, await Output, await Errors);
	}

	[TestMethod]
	public async Task NotInstalled_GivesValidJsonWithThePublishCommand()
	{
		using TempTree Tree = new();
		string Root = PluginRoot(Tree);
		Tree.Project("Game/Game.uproject");
		string Deep = Tree.Dir("Game", "Source", "Game");
		string Home = Tree.Dir("UakHome");
		(int ExitCode, string Output, string Errors) = await RunHookAsync(Root, Deep, Home);
		Assert.AreEqual(0, ExitCode, Errors);

		using JsonDocument Document = JsonDocument.Parse(Output);
		string Message = Document.RootElement.GetProperty("systemMessage").GetString()!;
		JsonElement Specific = Document.RootElement.GetProperty("hookSpecificOutput");
		Assert.AreEqual("SessionStart", Specific.GetProperty("hookEventName").GetString());
		string Context = Specific.GetProperty("additionalContext").GetString()!;
		StringAssert.Contains(Message, $"uak {Version} is not installed yet");
		// The exact engine or the generic placeholder, depending on the machine (a launcher UE_5.8 may be installed).
		StringAssert.Contains(Context, "publish \"");
		StringAssert.Contains(Context, "tools/src/uak");
		StringAssert.Contains(Context, "AskUserQuestion");
		StringAssert.Contains(Context, $"/{Version}/");

		// An older install means the plugin was updated.
		Directory.CreateDirectory(Path.Combine(Home, "0.0.1"));
		(_, Output, _) = await RunHookAsync(Root, Deep, Home);
		using JsonDocument Updated = JsonDocument.Parse(Output);
		StringAssert.Contains(Updated.RootElement.GetProperty("systemMessage").GetString()!, "found: 0.0.1");
	}

	/// <summary>The hook's additionalContext, after checking that it ran cleanly and asks the user before anything else.</summary>
	static string ContextOf((int ExitCode, string Output, string Errors) run)
	{
		Assert.AreEqual(0, run.ExitCode, run.Errors);
		using JsonDocument Document = JsonDocument.Parse(run.Output);
		string Context = Document.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
		StringAssert.Contains(Context, "Before you act on the user's first message, ask them with the AskUserQuestion tool");
		StringAssert.Contains(Context, "\"Not now\"");
		return Context;
	}

	/// <summary>Lays out an engine's bundled SDK: this host's dotnet in each given version, plus a newer SDK for another platform only.</summary>
	static void BundledDotNet(TempTree tree, string engineRoot, params string[] versions)
	{
		string Base = Path.Combine(engineRoot, "Engine", "Binaries", "ThirdParty", "DotNet");
		string Program = UnrealPlatform.Host.ExecutableName("dotnet");
		foreach (string SdkVersion in versions)
		{
			tree.File(Path.Combine(Base, SdkVersion, UnrealPlatform.Host.DotNetRid, Program));
		}
		tree.File(Path.Combine(Base, "99.0", "not-this-platform", Program));
	}

	static string ExpectedDotNet(string sdkVersion) => $"DotNet/{sdkVersion}/{UnrealPlatform.Host.DotNetRid}/{UnrealPlatform.Host.ExecutableName("dotnet")}";

	[TestMethod]
	public async Task AProjectInsideAnEngine_GetsThatEnginesNewestDotNet()
	{
		using TempTree Tree = new();
		string Root = PluginRoot(Tree);
		string Engine = Tree.Engine("UE");
		BundledDotNet(Tree, Engine, "8.0.300", "8.0.412");
		Tree.Project(Path.Combine("UE", "Game", "Game.uproject"), "");

		string Context = ContextOf(await RunHookAsync(Root, Tree.Dir("UE", "Game", "Source"), Tree.Dir("UakHome")));
		StringAssert.Contains(Context, ExpectedDotNet("8.0.412") + "\" publish");
		StringAssert.Contains(Context, "DOTNET_GENERATE_ASPNET_CERTIFICATE=false");
		// The command is sh: a VAR=value prefix and /c/-style paths that PowerShell and cmd can't run.
		StringAssert.Contains(Context, "with your Bash tool, never PowerShell or cmd");
		StringAssert.Contains(Context, "\"Publish now (Recommended)\"");
		Assert.DoesNotContain("<engine root>", Context);
		Assert.DoesNotContain("rm -rf", Context);
	}

	[TestMethod]
	public async Task APathAssociation_IsRelativeToTheProject()
	{
		using TempTree Tree = new();
		string Root = PluginRoot(Tree);
		string Engine = Tree.Engine(Path.Combine("Engines", "UE"));
		BundledDotNet(Tree, Engine, "8.0.412");
		Tree.Project(Path.Combine("Game", "Game.uproject"), "../Engines/UE");

		StringAssert.Contains(ContextOf(await RunHookAsync(Root, Tree.Path("Game"), Tree.Dir("UakHome"))), ExpectedDotNet("8.0.412"));
	}

	[TestMethod]
	public async Task UakEngine_WinsOverTheAssociation()
	{
		using TempTree Tree = new();
		string Root = PluginRoot(Tree);
		string Engine = Tree.Engine("Chosen");
		BundledDotNet(Tree, Engine, "10.0");
		Tree.Project(Path.Combine("Game", "Game.uproject"), "{00000000-0000-0000-0000-000000000000}");

		StringAssert.Contains(ContextOf(await RunHookAsync(Root, Tree.Path("Game"), Tree.Dir("UakHome"), uakEngine: Path.Combine(Engine, "Engine"))), ExpectedDotNet("10.0"));
	}

	[TestMethod]
	public async Task AnEngineItCantFind_StillAsks_WithTheGenericCommand()
	{
		using TempTree Tree = new();
		string Root = PluginRoot(Tree);
		Tree.Project(Path.Combine("Game", "Game.uproject"), "{00000000-0000-0000-0000-000000000000}");

		string Context = ContextOf(await RunHookAsync(Root, Tree.Path("Game"), Tree.Dir("UakHome")));
		StringAssert.Contains(Context, "<engine root>/Engine/Binaries/ThirdParty/DotNet");
		StringAssert.Contains(Context, "find the project's engine first");
	}

	[TestMethod]
	public async Task OlderVersions_AreUninstalledAfterThePublish_ButNeverState()
	{
		using TempTree Tree = new();
		string Root = PluginRoot(Tree);
		Tree.Project(Path.Combine("Game", "Game.uproject"));
		string Home = Tree.Dir("UakHome");
		Tree.Dir("UakHome", "0.0.1");
		Tree.Dir("UakHome", "0.1.0");
		Tree.Dir("UakHome", "State");

		string Context = ContextOf(await RunHookAsync(Root, Tree.Path("Game"), Home));
		StringAssert.Contains(Context, "\"Publish now and remove the old versions (Recommended)\"");
		StringAssert.Contains(Context, "After a successful publish, uninstall the older versions (0.0.1 0.1.0) with your Bash tool: rm -rf \"");
		StringAssert.Contains(Context, "/0.0.1\" \"");
		StringAssert.EndsWith(Context.Split("rm -rf")[1].Split(". If")[0], "/0.1.0\"");
		Assert.DoesNotContain("/State", Context);
		Assert.DoesNotContain($"/{Version}\" ", Context.Split("rm -rf")[1].Split(". If")[0]);
	}

	[TestMethod]
	public async Task Installed_IsSilent()
	{
		using TempTree Tree = new();
		string Root = PluginRoot(Tree);
		Tree.Project("Game/Game.uproject");
		string Home = Tree.Dir("UakHome");
		Tree.File(Path.Combine("UakHome", Version, OperatingSystem.IsWindows() ? "uak.exe" : "uak"));
		(int ExitCode, string Output, string Errors) = await RunHookAsync(Root, Tree.Path("Game"), Home);
		Assert.AreEqual(0, ExitCode, Errors);
		Assert.AreEqual("", Output);
	}

	[TestMethod]
	public async Task OutsideAnUnrealProject_IsSilent()
	{
		using TempTree Tree = new();
		string Root = PluginRoot(Tree);
		string Elsewhere = Tree.Dir("NotAProject", "Deeper");
		for (DirectoryInfo? Directory = new(Elsewhere); Directory is not null; Directory = Directory.Parent)
		{
			bool HasProject;
			try
			{
				HasProject = Directory.EnumerateFiles("*.uproject").Any();
			}
			catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
			{
				HasProject = false;
			}
			if (HasProject)
			{
				Assert.Inconclusive($"{Directory.FullName} holds a .uproject, so the temporary folder is inside an Unreal project.");
			}
		}
		Stopwatch Timer = Stopwatch.StartNew();
		(int ExitCode, string Output, string Errors) = await RunHookAsync(Root, Elsewhere, Tree.Dir("UakHome"));
		Assert.AreEqual(0, ExitCode, Errors);
		Assert.AreEqual("", Output);
		// The walk up stops before the filesystem root, which Git Bash would glob as a network path for seconds.
		Assert.IsLessThan(TimeSpan.FromSeconds(10), Timer.Elapsed);
	}
}
