// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Vcs.Tests;

/// <summary>The Perforce-only <c>uak vcs</c> commands (shelve, edit, add, reopen, change), over a fake connection.</summary>
[TestClass]
public sealed class VcsWriteCommandTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	static UakContext ContextFor(TempDirectory root) => new()
	{
		ProjectFile = new FileInfo(root.Write("Game.uproject", "{}\n")),
		StateDirectory = new DirectoryInfo(root.Combine("Saved/AgentKit")),
		Logger = NullLogger.Instance,
	};

	static async Task<(int ExitCode, string Output)> RunAsync(PerforceCommandBase command, TempDirectory root, FakePerforceConnection? connection, CancellationToken token, params string[] arguments)
	{
		(int exitCode, string output, _, _) = await RunCapturedAsync(command, root, connection, token, arguments);
		return (exitCode, output);
	}

	/// <summary>Runs a command and returns its exit code, standard output, standard error, and the logger's lines ("Level: message").</summary>
	static async Task<(int ExitCode, string Output, string Errors, List<string> Log)> RunCapturedAsync(PerforceCommandBase command, TempDirectory root, FakePerforceConnection? connection, CancellationToken token, params string[] arguments)
	{
		using StringWriter output = new();
		using StringWriter errors = new();
		command.Output = output;
		command.ErrorOutput = errors;
		if (connection is not null)
		{
			command.Detect = (_, _, _) => Task.FromResult(new VcsDetection(new PerforceVersionControl(connection, root.Directory), "test"));
		}
		CapturingLogger logger = new();
		UakContext context = new()
		{
			ProjectFile = new FileInfo(root.Write("Game.uproject", "{}\n")),
			StateDirectory = new DirectoryInfo(root.Combine("Saved/AgentKit")),
			Logger = logger,
		};
		int exitCode = await command.RunAsync(context, arguments, token);
		return (exitCode, output.ToString(), errors.ToString(), logger.Lines);
	}

	sealed class CapturingLogger : ILogger
	{
		public List<string> Lines { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(logLevel + ": " + formatter(state, exception));
	}

	/// <summary>A guard with a fixed answer that records the changes it was asked about.</summary>
	sealed class FakeGuard(PerforceShelveGuardResult answer) : IPerforceShelveGuard
	{
		public List<int> Asked { get; } = [];

		public Task<PerforceShelveGuardResult> CheckAsync(UakContext context, int change, CancellationToken cancellationToken)
		{
			Asked.Add(change);
			return Task.FromResult(answer);
		}
	}

	[TestMethod]
	public async Task AGuardRefusalShelvesNothingAndAnUnknownIsAWarning()
	{
		using TempDirectory root = new();
		FakeGuard refusing = new(new PerforceShelveGuardResult(PerforceShelveGuardVerdict.Refuse, "An auto-submit Horde preflight of change 12346 is running. Nothing was shelved."));
		FakePerforceConnection refused = new FakePerforceConnection(root.Path).On("change", "change-o-pending").On("shelve", "shelve").On("describe", "describe-s-pending");
		(int exitCode, _, _, List<string> log) = await RunCapturedAsync(new VcsShelveCommand { Guards = () => [refusing] }, root, refused, Token, "-c=12346");
		Assert.AreEqual(UakExitCodes.Failure, exitCode);
		CollectionAssert.AreEqual(new[] { 12346 }, refusing.Asked);
		Assert.IsFalse(refused.Calls.Any(call => call.Command == "shelve"), "nothing was shelved");
		Assert.IsTrue(log.Any(line => line.StartsWith("Error: An auto-submit Horde preflight of change 12346 is running", StringComparison.Ordinal)), string.Join("\n", log));

		FakeGuard unsure = new(new PerforceShelveGuardResult(PerforceShelveGuardVerdict.Unknown, "Could not check Horde."));
		FakePerforceConnection warned = new FakePerforceConnection(root.Path).On("change", "change-o-pending").On("shelve", "shelve").On("describe", "describe-s-pending");
		(int warnedExit, _, _, List<string> warnings) = await RunCapturedAsync(new VcsShelveCommand { Guards = () => [unsure] }, root, warned, Token, "-c=12346", "-replace");
		Assert.AreEqual(UakExitCodes.Success, warnedExit);
		Assert.IsTrue(warned.Calls.Any(call => call.Command == "shelve"));
		CollectionAssert.Contains(warnings, "Warning: Could not check Horde.");
	}

	[TestMethod]
	public async Task ANewChangelistIsNotCheckedByTheGuards()
	{
		using TempDirectory root = new();
		string game = root.Write("Source/Game/Game.cpp");
		string other = root.Write("Source/Game/Other.cpp");
		FakeGuard refusing = new(new PerforceShelveGuardResult(PerforceShelveGuardVerdict.Refuse, "no"));
		FakePerforceConnection connection = new FakePerforceConnection(root.Path)
			.OnEach("fstat", "fstat-ro-default", "fstat-ro-default", "fstat-ro-moved")
			.On("change", call => call.Arguments[0] == "-i" ? "change-created" : "change-o-new")
			.On("reopen", "reopen")
			.On("shelve", "shelve-new")
			.OnEach("describe", "describe-s-none", "describe-s-new");
		(int exitCode, string output) = await RunAsync(new VcsShelveCommand { Guards = () => [refusing] }, root, connection, Token, game, other);
		Assert.AreEqual(UakExitCodes.Success, exitCode, output);
		Assert.IsEmpty(refusing.Asked, "a changelist uak just created has no preflights");
	}

	[TestMethod]
	public async Task AFailureAfterTheChangelistWasCreatedNamesItAndItsFiles()
	{
		using TempDirectory root = new();
		string game = root.Write("Source/Game/Game.cpp");
		string other = root.Write("Source/Game/Other.cpp");
		// The files move into the new changelist, then p4 shelve fails.
		FakePerforceConnection connection = new FakePerforceConnection(root.Path)
			.OnEach("fstat", "fstat-ro-default", "fstat-ro-default", "fstat-ro-moved")
			.On("change", call => call.Arguments[0] == "-i" ? "change-created" : "change-o-new")
			.On("reopen", "reopen")
			.On("shelve", "shelve-no-files")
			.On("describe", "describe-s-none");

		(int exitCode, string output, string errors, List<string> log) = await RunCapturedAsync(new VcsShelveCommand(), root, connection, Token, game, other, "-json");

		Assert.AreEqual(UakExitCodes.Failure, exitCode);
		Assert.AreEqual("", output, "no JSON for a failure");
		Assert.AreEqual("Created change 12350.", errors.Trim(), "under -json the new changelist goes to standard error at once");
		string error = log.Single(line => line.StartsWith("Error: ", StringComparison.Ordinal));
		StringAssert.Contains(error, "No files to shelve");
		StringAssert.Contains(error, "Change 12350 was created and holds the files: //Game/main/Source/Game/Game.cpp, //Game/main/Source/Game/Other.cpp.");
	}

	[TestMethod]
	public async Task AReplaceThatLostAFileUnaskedExitsOneAndNamesIt()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("change", "change-o-pending").On("shelve", "shelve").OnEach("describe", "describe-s-pending", "describe-s-one");

		(int exitCode, _, _, List<string> log) = await RunCapturedAsync(new VcsShelveCommand(), root, connection, Token, "-c=12346", "-replace");

		Assert.AreEqual(UakExitCodes.Failure, exitCode);
		StringAssert.Contains(log.Single(line => line.StartsWith("Error: ", StringComparison.Ordinal)), "//Game/main/Config/Removed.ini");
	}

	[TestMethod]
	public async Task KeptFilesArePrintedAndInTheJson()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("change", "change-o-pending").On("shelve", "shelve").On("describe", "describe-s-shelved-only");

		(int exitCode, string output) = await RunAsync(new VcsShelveCommand(), root, connection, Token, "-c=12346");
		Assert.AreEqual(UakExitCodes.Success, exitCode, output);
		StringAssert.Contains(output, "kept on the shelf, but no longer opened in change 12346 (a submit of the shelf would include it): //Game/main/Source/Game/ShelvedOnly.cpp");

		(int jsonExit, string json) = await RunAsync(new VcsShelveCommand(), root, connection, Token, "-c=12346", "-json");
		Assert.AreEqual(UakExitCodes.Success, jsonExit);
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.AreEqual("//Game/main/Source/Game/ShelvedOnly.cpp", document.RootElement.GetProperty("kept")[0].GetString());
	}

	[TestMethod]
	public async Task ShelvingFilesCreatesAChangeMovesThemAndShelvesIt()
	{
		using TempDirectory root = new();
		string game = root.Write("Source/Game/Game.cpp");
		string other = root.Write("Source/Game/Other.cpp");
		FakePerforceConnection connection = new FakePerforceConnection(root.Path)
			.OnEach("fstat", "fstat-ro-default", "fstat-ro-default", "fstat-ro-moved")
			.On("change", call => call.Arguments[0] == "-i" ? "change-created" : "change-o-new")
			.On("reopen", "reopen")
			.On("shelve", "shelve-new")
			.OnEach("describe", "describe-s-none", "describe-s-new");

		(int exitCode, string output) = await RunAsync(new VcsShelveCommand(), root, connection, Token, game, other, "-description=Agent work");

		Assert.AreEqual(UakExitCodes.Success, exitCode, output);
		StringAssert.Contains(output, "Created change 12350.");
		StringAssert.Contains(output, "shelved edit         //Game/main/Source/Game/Game.cpp  (was in change default)");
		StringAssert.Contains(output, "Shelved 2 file(s) in change 12350 (p4 shelve -f).");
		CollectionAssert.AreEqual(new[] { "fstat", "change", "change", "fstat", "reopen", "fstat", "change", "describe", "shelve", "describe" }, connection.Calls.Select(call => call.Command).ToArray());
		CollectionAssert.AreEqual(new[] { "-f", "-c12350" }, connection.Calls.Single(call => call.Command == "shelve").Arguments.ToArray());
		Assert.IsFalse(connection.Calls.Any(call => call.Command is "revert" or "submit"));
	}

	[TestMethod]
	public async Task ShelvingFilesFromANumberedChangeNeedsForce()
	{
		using TempDirectory root = new();
		string game = root.Write("Source/Game/Game.cpp");
		string other = root.Write("Source/Game/Other.cpp");
		FakePerforceConnection refused = new FakePerforceConnection(root.Path).On("fstat", "fstat-ro-numbered");
		(int exitCode, _) = await RunAsync(new VcsShelveCommand(), root, refused, Token, game, other);
		Assert.AreEqual(UakExitCodes.Failure, exitCode);
		CollectionAssert.AreEqual(new[] { "fstat" }, refused.Calls.Select(call => call.Command).ToArray(), "nothing changed");

		FakePerforceConnection forced = new FakePerforceConnection(root.Path)
			.OnEach("fstat", "fstat-ro-numbered", "fstat-ro-numbered", "fstat-ro-moved")
			.On("change", call => call.Arguments[0] == "-i" ? "change-created" : "change-o-new")
			.On("reopen", "reopen")
			.On("shelve", "shelve-new")
			.OnEach("describe", "describe-s-none", "describe-s-new");
		(int forcedExit, string json) = await RunAsync(new VcsShelveCommand(), root, forced, Token, game, other, "-force", "-json");
		Assert.AreEqual(UakExitCodes.Success, forcedExit, json);
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement[] files = [.. document.RootElement.GetProperty("files").EnumerateArray()];
		Assert.AreEqual("12346", files.Single(file => file.GetProperty("depotFile").GetString()!.EndsWith("Game.cpp", StringComparison.Ordinal)).GetProperty("originalChange").GetString());
		Assert.AreEqual("default", files.Single(file => file.GetProperty("depotFile").GetString()!.EndsWith("Other.cpp", StringComparison.Ordinal)).GetProperty("originalChange").GetString());
	}

	[TestMethod]
	public async Task ShelvingHalfOfAMoveChangesNothing()
	{
		using TempDirectory root = new();
		string game = root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("fstat", "fstat-ro-half-move");
		(int exitCode, _) = await RunAsync(new VcsShelveCommand(), root, connection, Token, game);
		Assert.AreEqual(UakExitCodes.Failure, exitCode);
		CollectionAssert.AreEqual(new[] { "fstat" }, connection.Calls.Select(call => call.Command).ToArray(), "no changelist was created");
	}

	[TestMethod]
	public async Task ReplacingAShelfWithShelvedOnlyFilesNeedsDropUnopened()
	{
		using TempDirectory root = new();
		FakePerforceConnection refused = new FakePerforceConnection(root.Path).On("change", "change-o-pending").On("describe", "describe-s-shelved-only");
		(int exitCode, _) = await RunAsync(new VcsShelveCommand(), root, refused, Token, "-c=12346", "-replace");
		Assert.AreEqual(UakExitCodes.Failure, exitCode);
		Assert.IsFalse(refused.Calls.Any(call => call.Command == "shelve"));

		FakePerforceConnection dropping = new FakePerforceConnection(root.Path).On("change", "change-o-pending").On("shelve", "shelve").OnEach("describe", "describe-s-shelved-only", "describe-s-pending");
		(int dropped, string output) = await RunAsync(new VcsShelveCommand(), root, dropping, Token, "-c=12346", "-replace", "-drop-unopened");
		Assert.AreEqual(UakExitCodes.Success, dropped, output);
		StringAssert.Contains(output, "REMOVED from the shelf: //Game/main/Source/Game/ShelvedOnly.cpp");
		StringAssert.Contains(output, "1 file(s) removed from the shelf.");
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsShelveCommand(), root, dropping, Token, "-c=12346", "-drop-unopened"));
	}

	[TestMethod]
	public async Task ShelvingFilesThatAreNotOpenedChangesNothing()
	{
		using TempDirectory root = new();
		string game = root.Write("Source/Game/Game.cpp");
		string other = root.Write("Source/Game/Other.cpp");
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("fstat", "fstat-ro-one-not-opened");

		(int exitCode, _) = await RunAsync(new VcsShelveCommand(), root, connection, Token, game, other);

		Assert.AreEqual(UakExitCodes.Failure, exitCode);
		CollectionAssert.AreEqual(new[] { "fstat" }, connection.Calls.Select(call => call.Command).ToArray(), "no changelist was created");
	}

	[TestMethod]
	public async Task ShelvingAChangeUpdatesOrReplacesWithJson()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("change", "change-o-pending").On("shelve", "shelve").On("describe", "describe-s-pending");

		(int exitCode, string json) = await RunAsync(new VcsShelveCommand(), root, connection, Token, "-c=12346", "-replace", "-json");

		Assert.AreEqual(UakExitCodes.Success, exitCode);
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.AreEqual(12346, document.RootElement.GetProperty("change").GetInt32());
		Assert.AreEqual("replace", document.RootElement.GetProperty("mode").GetString());
		Assert.AreEqual(2, document.RootElement.GetProperty("files").GetArrayLength());
		Assert.AreEqual(0, document.RootElement.GetProperty("removed").GetArrayLength());
		CollectionAssert.AreEqual(new[] { "-r", "-c12346" }, connection.Calls.Single(call => call.Command == "shelve").Arguments.ToArray());
	}

	[TestMethod]
	public async Task ShelveArgumentsAreChecked()
	{
		using TempDirectory root = new();
		string file = root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = new(root.Path);

		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsShelveCommand(), root, connection, Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsShelveCommand(), root, connection, Token, "-c=12346", file));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsShelveCommand(), root, connection, Token, file, "-replace"));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsShelveCommand(), root, connection, Token, "-c=default"));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsShelveCommand(), root, connection, Token, "-c=-5"));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsShelveCommand(), root, connection, Token, "-c=12346", "-description=x"));
		Assert.IsEmpty(connection.Calls);
	}

	[TestMethod]
	public async Task GitWorkspacesAreNotSupported()
	{
		using TempGitRepository repo = new();
		using StringWriter output = new();
		VcsShelveCommand command = new() { Output = output };
		UakContext context = new()
		{
			ProjectFile = new FileInfo(repo.Write("Game/Game.uproject", "{}\n")),
			StateDirectory = new DirectoryInfo(repo.Combine("Game/Saved/AgentKit")),
			Logger = NullLogger.Instance,
		};

		UakUsageException exception = await Assert.ThrowsExactlyAsync<UakUsageException>(() => command.RunAsync(context, ["-c=1"], Token));
		StringAssert.Contains(exception.Message, "works with Perforce only");
		StringAssert.Contains(exception.Message, "uses Git");
	}

	[TestMethod]
	public async Task EditReportsEachFileAndFailsWhenOneIsNotOpened()
	{
		using TempDirectory root = new();
		string file = root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("change", "change-o-new").On("edit", "edit-other-change").On("fstat", "fstat-ro-other-change");

		(int exitCode, string output) = await RunAsync(new VcsEditCommand(), root, connection, Token, file, "-c=12350");

		Assert.AreEqual(UakExitCodes.Failure, exitCode);
		StringAssert.Contains(output, "FAILED  " + file);
		StringAssert.Contains(output, "use 'reopen'");
		StringAssert.Contains(output, "1 of 1 file(s) are not opened in change 12350.");
	}

	[TestMethod]
	public async Task EditIntoTheDefaultChangeSucceeds()
	{
		using TempDirectory root = new();
		string file = root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("edit", "edit").On("fstat", "fstat-ro-default");

		(int exitCode, string json) = await RunAsync(new VcsEditCommand(), root, connection, Token, file, "-json");

		Assert.AreEqual(UakExitCodes.Success, exitCode);
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.IsTrue(document.RootElement.GetProperty("succeeded").GetBoolean());
		Assert.AreEqual("default", document.RootElement.GetProperty("change").GetString());
		Assert.AreEqual(file, document.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
	}

	[TestMethod]
	public async Task ReopenNeedsATargetAndFiles()
	{
		using TempDirectory root = new();
		string file = root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = new(root.Path);

		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsReopenCommand(), root, connection, Token, file));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsReopenCommand(), root, connection, Token, "-c=12350"));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsReopenCommand(), root, connection, Token, file, "-c=abc"));
		Assert.IsEmpty(connection.Calls);

		(int exitCode, _) = await RunAsync(new VcsReopenCommand(), root, connection, Token, root.Combine("Source"), "-c=default");
		Assert.AreEqual(UakExitCodes.Failure, exitCode, "a directory without -folders");
		Assert.IsEmpty(connection.Calls);
	}

	[TestMethod]
	public async Task AddOfAMissingFileFails()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new(root.Path);

		(int exitCode, _) = await RunAsync(new VcsAddCommand(), root, connection, Token, root.Combine("Nope.txt"));

		Assert.AreEqual(UakExitCodes.Failure, exitCode);
		Assert.IsEmpty(connection.Calls);
	}

	[TestMethod]
	public async Task ChangeNewPrintsTheNumber()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("change", "change-created");

		(int exitCode, string output) = await RunAsync(new VcsChangeNewCommand(), root, connection, Token, "-description=Scratch");
		Assert.AreEqual(UakExitCodes.Success, exitCode);
		Assert.AreEqual("12350", output.Trim());

		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsChangeNewCommand(), root, connection, Token));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsChangeNewCommand(), root, connection, Token, "-description=x", "stray"));
	}

	[TestMethod]
	public async Task ChangeDescribeUpdatesOwnPendingChanges()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("change", call => call.Arguments[0] == "-o" ? "change-o-pending" : "change-updated").On("opened", "empty");

		(int exitCode, string output) = await RunAsync(new VcsChangeDescribeCommand(), root, connection, Token, "-c=12346", "-description=Better words");
		Assert.AreEqual(UakExitCodes.Success, exitCode);
		StringAssert.Contains(output, "Change 12346: description updated.");

		FakePerforceConnection theirs = new FakePerforceConnection(root.Path).On("change", "change-o-other-client").On("opened", "empty");
		(int refused, _) = await RunAsync(new VcsChangeDescribeCommand(), root, theirs, Token, "-c=12346", "-description=Mine");
		Assert.AreEqual(UakExitCodes.Failure, refused);
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => RunAsync(new VcsChangeDescribeCommand(), root, connection, Token, "-description=x"));
	}

	[TestMethod]
	public async Task ChangeDescribeListsFilesItMovedBack()
	{
		using TempDirectory root = new();
		int reads = 0;
		string[] inChange = ["opened-change-new-only", "opened-change-pending"];
		FakePerforceConnection connection = new FakePerforceConnection(root.Path)
			.On("change", call => call.Arguments[0] == "-o" ? "change-o-pending" : "change-updated-adding")
			.On("opened", call => call.Arguments[1] == "default" ? "opened-default-removed" : inChange[Math.Min(reads++, inChange.Length - 1)])
			.On("reopen", "empty");

		(int exitCode, string output) = await RunAsync(new VcsChangeDescribeCommand(), root, connection, Token, "-c=12346", "-description=Better words");
		Assert.AreEqual(UakExitCodes.Success, exitCode, output);
		StringAssert.Contains(output, "moved back to the default changelist (p4 had pulled it into change 12346 while the description changed): //Game/main/Config/Removed.ini");

		reads = 0;
		(int jsonExit, string json) = await RunAsync(new VcsChangeDescribeCommand(), root, connection, Token, "-c=12346", "-description=Better words", "-json");
		Assert.AreEqual(UakExitCodes.Success, jsonExit);
		using JsonDocument document = JsonDocument.Parse(json);
		Assert.AreEqual("//Game/main/Config/Removed.ini", document.RootElement.GetProperty("released")[0].GetString());
		Assert.AreEqual(0, document.RootElement.GetProperty("restored").GetArrayLength());
	}

	[TestMethod]
	public void CommandsAreNamedAndNeedNoEngine()
	{
		IUakCommand[] commands = [new VcsShelveCommand(), new VcsEditCommand(), new VcsAddCommand(), new VcsReopenCommand(), new VcsChangeNewCommand(), new VcsChangeDescribeCommand()];
		CollectionAssert.AreEqual(new[] { "vcs shelve", "vcs edit", "vcs add", "vcs reopen", "vcs change new", "vcs change describe" }, commands.Select(command => command.Name).ToArray());
		Assert.IsTrue(commands.All(command => !command.RequiresEngine));
		Assert.IsTrue(commands.All(command => command.Usage.Contains("Perforce only", StringComparison.Ordinal)));
	}
}
