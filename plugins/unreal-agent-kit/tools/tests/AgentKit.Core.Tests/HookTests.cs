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
		string? Shell = ExecutableLocator.FindOnPath("sh");
		if (Shell is null)
		{
			Assert.Inconclusive("No sh on PATH (on Windows it comes with Git for Windows): the hook can't be run here.");
		}
		return Shell!;
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

	async Task<(int ExitCode, string Output, string Errors)> RunHookAsync(string pluginRoot, string projectDirectory, string uakHome)
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
		StringAssert.Contains(Context, "dotnet publish");
		StringAssert.Contains(Context, "tools/src/uak");
		StringAssert.Contains(Context, $"/{Version}/");

		// An older install means the plugin was updated.
		Directory.CreateDirectory(Path.Combine(Home, "0.0.1"));
		(_, Output, _) = await RunHookAsync(Root, Deep, Home);
		using JsonDocument Updated = JsonDocument.Parse(Output);
		StringAssert.Contains(Updated.RootElement.GetProperty("systemMessage").GetString()!, "found: 0.0.1");
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
