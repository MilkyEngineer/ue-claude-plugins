// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Vcs.Tests;

/// <summary>The <c>uak vcs</c> commands against a real temporary git repository holding a project.</summary>
[TestClass]
public sealed class VcsCommandTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	static UakContext ContextFor(TempDirectory repo) => new()
	{
		ProjectFile = new FileInfo(repo.Write("Game/Game.uproject", "{}\n")),
		StateDirectory = new DirectoryInfo(repo.Combine("Game/Saved/AgentKit")),
		Logger = NullLogger.Instance,
	};

	static async Task<(int ExitCode, string Output)> RunAsync(VcsCommandBase command, UakContext context, CancellationToken token, params string[] arguments)
	{
		using StringWriter output = new();
		command.Output = output;
		int exitCode = await command.RunAsync(context, arguments, token);
		return (exitCode, output.ToString());
	}

	[TestMethod]
	public async Task RevisionPrintsTheHeadHash()
	{
		using TempGitRepository repo = new();
		UakContext context = ContextFor(repo);
		(int emptyExit, _) = await RunAsync(new VcsRevisionCommand(), context, Token, "-vcs=git");
		Assert.AreEqual(UakExitCodes.Failure, emptyExit, "no commits yet");

		string head = repo.Commit();
		(int exitCode, string output) = await RunAsync(new VcsRevisionCommand(), context, Token, "-vcs=git");

		Assert.AreEqual(UakExitCodes.Success, exitCode);
		Assert.AreEqual(head, output.Trim());
	}

	[TestMethod]
	public async Task ChangedPrintsTextOrJson()
	{
		using TempGitRepository repo = new();
		UakContext context = ContextFor(repo);
		repo.Commit();
		string added = repo.Write("Game/Source/New.cpp");

		(int exitCode, string text) = await RunAsync(new VcsChangedCommand(), context, Token, "-vcs=git");
		Assert.AreEqual(UakExitCodes.Success, exitCode);
		StringAssert.Contains(text, "Untracked");
		StringAssert.Contains(text, added);

		(_, string json) = await RunAsync(new VcsChangedCommand(), context, Token, "-vcs=git", "-json", "-path=" + repo.Combine("Game/Source"));
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement entry = document.RootElement.EnumerateArray().Single();
		Assert.AreEqual(added, entry.GetProperty("path").GetString());
		Assert.AreEqual("Untracked", entry.GetProperty("state").GetString());
	}

	[TestMethod]
	public async Task StatusDescribesOrListsFiles()
	{
		using TempGitRepository repo = new();
		UakContext context = ContextFor(repo);
		repo.Commit();

		(int exitCode, string summary) = await RunAsync(new VcsStatusCommand(), context, Token);
		Assert.AreEqual(UakExitCodes.Success, exitCode);
		StringAssert.StartsWith(summary, "Git at " + repo.Path);
		StringAssert.Contains(summary, "Detected by: .git at " + repo.Path);

		(_, string files) = await RunAsync(new VcsStatusCommand(), context, Token, repo.Combine("Game/Game.uproject"), repo.Combine("Game/Other.txt"));
		string[] lines = files.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		Assert.HasCount(2, lines);
		StringAssert.StartsWith(lines[0], "Unmodified");
		StringAssert.StartsWith(lines[1], "Unknown");

		(_, string json) = await RunAsync(new VcsStatusCommand(), context, Token, "-json");
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.AreEqual("Git", document.RootElement.GetProperty("kind").GetString());
		Assert.AreEqual(repo.Git("rev-parse", "HEAD"), document.RootElement.GetProperty("revision").GetString());
	}

	[TestMethod]
	public async Task BadArgumentsAreUsageErrors()
	{
		using TempGitRepository repo = new();
		UakContext context = ContextFor(repo);

		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new VcsRevisionCommand().RunAsync(context, ["-bogus"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new VcsChangedCommand().RunAsync(context, ["stray-path"], Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new VcsStatusCommand().RunAsync(context, ["-vcs=svn"], Token));
	}

	[TestMethod]
	public async Task TheContextsRequestedKindIsUsed()
	{
		using TempGitRepository repo = new();
		UakContext detected = ContextFor(repo);
		UakContext context = new()
		{
			ProjectFile = detected.ProjectFile,
			StateDirectory = detected.StateDirectory,
			Logger = detected.Logger,
			RequestedVersionControl = "none",
			Provenance = new Dictionary<string, string> { [UakContextResolver.VcsKey] = "UAK_VCS environment variable" },
		};

		(int exitCode, string output) = await RunAsync(new VcsStatusCommand(), context, Token);
		Assert.AreEqual(UakExitCodes.Success, exitCode);
		StringAssert.StartsWith(output, "None");
		StringAssert.Contains(output, "Detected by: UAK_VCS environment variable");

		(_, string overridden) = await RunAsync(new VcsStatusCommand(), context, Token, "-vcs=git");
		StringAssert.StartsWith(overridden, "Git at");
	}

	[TestMethod]
	public async Task EnvReporterGivesTheVersionControlLine()
	{
		using TempGitRepository repo = new();
		UakContext context = ContextFor(repo);
		repo.Commit();

		IReadOnlyList<KeyValuePair<string, string>> rows = await new VcsEnvReporter().ReportAsync(context, Token);

		Assert.HasCount(1, rows);
		Assert.AreEqual(EnvCommand.VersionControlLabel, rows[0].Key);
		StringAssert.StartsWith(rows[0].Value, "Git at " + repo.Path);
		StringAssert.Contains(rows[0].Value, "found by .git at");

		UakContext bad = new() { ProjectFile = context.ProjectFile, StateDirectory = repo.Directory, Logger = NullLogger.Instance, RequestedVersionControl = "svn" };
		StringAssert.StartsWith((await new VcsEnvReporter().ReportAsync(bad, Token))[0].Value, "error: Unknown version control 'svn'");
	}

	[TestMethod]
	public void CommandsDoNotNeedAnEngine()
	{
		IUakCommand[] commands = [new VcsStatusCommand(), new VcsChangedCommand(), new VcsRevisionCommand()];
		CollectionAssert.AreEqual(new[] { "vcs status", "vcs changed", "vcs revision" }, commands.Select(command => command.Name).ToArray());
		Assert.IsTrue(commands.All(command => !command.RequiresEngine));
	}
}
