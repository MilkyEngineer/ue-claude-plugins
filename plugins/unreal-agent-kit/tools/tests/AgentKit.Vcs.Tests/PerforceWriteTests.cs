// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using EpicGames.Core;
using EpicGames.Perforce;

namespace AgentKit.Vcs.Tests;

/// <summary>
/// Perforce writes (changelists, shelve, edit, add, reopen) over recorded server output. The write fixtures follow what a real
/// p4d 2025.1 answered to the same commands (info.ztag is from a 2024.2 server), and go through EpicGames.Perforce's -G parser.
/// </summary>
[TestClass]
public sealed class PerforceWriteTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	/// <summary>Decodes -G input (a spec sent to change -i) into its fields, in order.</summary>
	static List<KeyValuePair<string, string>> DecodeSpec(byte[] input)
	{
		List<KeyValuePair<Utf8String, PerforceValue>> rows = [];
		int position = 0;
		Assert.IsTrue(PerforceOutput.ParseRecord(input, ref position, rows));
		Assert.AreEqual(input.Length, position, "one record");
		return rows.Select(row => KeyValuePair.Create(row.Key.ToString(), row.Value.ToString())).ToList();
	}

	static FakePerforceConnection Fake(TempDirectory root) => new(root.Path);

	[TestMethod]
	public async Task CreateChangeSendsANewSpecWithoutFiles()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = Fake(root).On("change", "change-created");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		Assert.AreEqual(12350, await p4.CreateChangeAsync("Scratch work", Token));

		PerforceCall call = connection.Calls.Single();
		CollectionAssert.AreEqual(new[] { "-i" }, call.Arguments.ToArray());
		List<KeyValuePair<string, string>> spec = DecodeSpec(call.Input!);
		Assert.AreEqual("new", spec.Single(field => field.Key == "Change").Value);
		Assert.AreEqual("user-ws", spec.Single(field => field.Key == "Client").Value);
		Assert.AreEqual("Scratch work", spec.Single(field => field.Key == "Description").Value);
		Assert.IsFalse(spec.Any(field => field.Key.StartsWith("Files", StringComparison.Ordinal)), "nothing moves into a new changelist");
	}

	[TestMethod]
	public async Task CreateChangeNeedsADescription()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = Fake(root);
		using PerforceVersionControl p4 = new(connection, root.Directory);

		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.CreateChangeAsync("  ", Token));
		Assert.IsEmpty(connection.Calls);
	}

	[TestMethod]
	public async Task OnlyThisClientsPendingChangesAreAccepted()
	{
		using TempDirectory root = new();
		using PerforceVersionControl other = new(Fake(root).On("change", "change-o-other-client"), root.Directory);
		VcsException notOurs = await Assert.ThrowsExactlyAsync<VcsException>(() => other.GetOwnPendingChangeAsync(12346, Token));
		StringAssert.Contains(notOurs.Message, "belongs to client someone-else-ws");

		using PerforceVersionControl submitted = new(Fake(root).On("change", "change-o-submitted"), root.Directory);
		StringAssert.Contains((await Assert.ThrowsExactlyAsync<VcsException>(() => submitted.GetOwnPendingChangeAsync(12340, Token))).Message, "submitted, not pending");

		using PerforceVersionControl unknown = new(Fake(root).On("change", "change-o-unknown"), root.Directory);
		StringAssert.Contains((await Assert.ThrowsExactlyAsync<VcsException>(() => unknown.GetOwnPendingChangeAsync(99, Token))).Message, "Change 99 unknown");

		using PerforceVersionControl mismatch = new(Fake(root).On("change", "change-o-pending"), root.Directory);
		StringAssert.Contains((await Assert.ThrowsExactlyAsync<VcsException>(() => mismatch.GetOwnPendingChangeAsync(555, Token))).Message, "returned change 12346");

		using PerforceVersionControl ours = new(Fake(root).On("change", "change-o-pending"), root.Directory);
		PerforceChange change = await ours.GetOwnPendingChangeAsync(12346, Token);
		Assert.AreEqual("Fix the spawn order.\n\tSecond line.", change.Description);
		CollectionAssert.AreEqual(new[] { "//Game/main/Source/Game/New.cpp", "//Game/main/Config/Removed.ini" }, change.Files.ToArray());
	}

	[TestMethod]
	public async Task UpdateDescriptionSendsTheWholeSpecBack()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = Fake(root).On("change", call => call.Arguments[0] == "-o" ? "change-o-pending" : "change-updated").On("opened", "empty");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		Assert.IsEmpty(await p4.UpdateDescriptionAsync(12346, "Fix the spawn order, take two.", Token));

		CollectionAssert.AreEqual(new[] { "opened", "change", "change" }, connection.Calls.Select(call => call.Command).ToArray());
		CollectionAssert.AreEqual(new[] { "-o", "12346" }, connection.Calls[1].Arguments.ToArray());
		CollectionAssert.AreEqual(new[] { "-i" }, connection.Calls[2].Arguments.ToArray());
		List<KeyValuePair<string, string>> sent = DecodeSpec(connection.Calls[2].Input!);
		// Every field of change -o but "code", in order, with only the description replaced: dropping Files would move the files out.
		CollectionAssert.AreEqual(
			new[] { "Change", "Date", "Client", "User", "Status", "Description", "Jobs0", "Files0", "Files1", "Type", "extraTag0", "extraTagType0", "shelveUpdate" },
			sent.Select(field => field.Key).ToArray());
		Assert.AreEqual("Fix the spawn order, take two.", sent.Single(field => field.Key == "Description").Value);
		Assert.AreEqual("//Game/main/Config/Removed.ini", sent.Single(field => field.Key == "Files1").Value);
		Assert.AreEqual("job000123", sent.Single(field => field.Key == "Jobs0").Value);
	}

	[TestMethod]
	public async Task UpdateDescriptionMovesBackFilesAConcurrentReopenLost()
	{
		using TempDirectory root = new();
		int defaultReads = 0;
		FakePerforceConnection connection = Fake(root)
			.On("change", call => call.Arguments[0] == "-o" ? "change-o-pending" : "change-updated-removing")
			.On("opened", call => call.Arguments[1] == "default" ? (defaultReads++ == 0 ? "empty" : "opened-default-late") : "opened-change-late")
			.On("reopen", "reopen-late");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		IReadOnlyList<string> restored = await p4.UpdateDescriptionAsync(12346, "New text", Token);

		CollectionAssert.AreEqual(new[] { "//Game/main/Source/Game/Late.cpp" }, restored.ToArray(), "the file reopened in between went back into the change");
		PerforceCall reopen = connection.Calls.Single(call => call.Command == "reopen");
		CollectionAssert.AreEqual(new[] { "-c12346" }, reopen.Arguments.ToArray());
		CollectionAssert.AreEqual(new[] { "//Game/main/Source/Game/Late.cpp" }, reopen.FileArguments.ToArray());

		// When nothing new shows up in the default changelist, uak can't tell which files moved: a failure that says so.
		FakePerforceConnection unknown = Fake(root)
			.On("change", call => call.Arguments[0] == "-o" ? "change-o-pending" : "change-updated-removing")
			.On("opened", "empty");
		using PerforceVersionControl lost = new(unknown, root.Directory);
		StringAssert.Contains((await Assert.ThrowsExactlyAsync<VcsException>(() => lost.UpdateDescriptionAsync(12346, "New text", Token))).Message, "could not tell which");
		Assert.IsFalse(unknown.Calls.Any(call => call.Command == "reopen"));
	}

	[TestMethod]
	public async Task UpdateDescriptionRefusesAnotherClientsChange()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = Fake(root).On("change", "change-o-other-client").On("opened", "empty");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.UpdateDescriptionAsync(12346, "Mine now", Token));
		Assert.IsFalse(connection.Calls.Any(call => call.Command == "change" && call.Arguments[0] == "-i"), "nothing was sent");
	}

	[TestMethod]
	public async Task ShelveUpdatesOrReplacesTheShelf()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = Fake(root).On("change", "change-o-pending").On("shelve", "shelve")
			.OnEach("describe", "describe-s-pending", "describe-s-pending-changed", "describe-s-pending-changed");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		PerforceShelveResult shelved = await p4.ShelveAsync(12346, ShelveMode.Update, cancellationToken: Token);
		CollectionAssert.AreEqual(
			new[] { new PerforceShelvedFile("//Game/main/Config/Removed.ini", "delete"), new PerforceShelvedFile("//Game/main/Source/Game/New.cpp", "add") },
			shelved.Shelved.ToArray());
		CollectionAssert.AreEqual(new[] { "//Game/main/Source/Game/New.cpp" }, shelved.Replaced.ToArray(), "its shelved content changed");
		Assert.IsEmpty(shelved.Removed);
		CollectionAssert.AreEqual(new[] { "-f", "-c12346" }, connection.Calls.Single(call => call.Command == "shelve").Arguments.ToArray());
		CollectionAssert.AreEqual(new[] { "-S", "-s", "12346" }, connection.Calls.First(call => call.Command == "describe").Arguments.ToArray());

		await p4.ShelveAsync(12346, ShelveMode.Replace, cancellationToken: Token);
		CollectionAssert.AreEqual(new[] { "-r", "-c12346" }, connection.Calls.Last(call => call.Command == "shelve").Arguments.ToArray());
		Assert.IsFalse(connection.Calls.Any(call => call.Command is "revert" or "submit"));
	}

	[TestMethod]
	public async Task ReplaceRefusesToDropShelvedFilesThatAreNotOpened()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = Fake(root).On("change", "change-o-pending").On("shelve", "shelve").On("describe", "describe-s-shelved-only");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		VcsException refused = await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ShelveAsync(12346, ShelveMode.Replace, cancellationToken: Token));
		StringAssert.Contains(refused.Message, "//Game/main/Source/Game/ShelvedOnly.cpp");
		StringAssert.Contains(refused.Message, "-drop-unopened");
		Assert.IsFalse(connection.Calls.Any(call => call.Command == "shelve"), "nothing was shelved");

		// -f keeps them, so it goes ahead.
		await p4.ShelveAsync(12346, ShelveMode.Update, cancellationToken: Token);

		// Asked to drop them: it goes ahead, and says what the shelf lost.
		FakePerforceConnection dropping = Fake(root).On("change", "change-o-pending").On("shelve", "shelve").OnEach("describe", "describe-s-shelved-only", "describe-s-pending");
		using PerforceVersionControl dropper = new(dropping, root.Directory);
		PerforceShelveResult result = await dropper.ShelveAsync(12346, ShelveMode.Replace, dropUnopened: true, Token);
		CollectionAssert.AreEqual(new[] { "//Game/main/Source/Game/ShelvedOnly.cpp" }, result.Removed.ToArray());
		CollectionAssert.AreEqual(new[] { "-r", "-c12346" }, dropping.Calls.Single(call => call.Command == "shelve").Arguments.ToArray());
	}

	[TestMethod]
	public async Task ReopenRefusesHalfOfAMove()
	{
		using TempDirectory root = new();
		string game = root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = Fake(root).On("change", "change-o-new").On("fstat", "fstat-ro-half-move").On("reopen", "reopen");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		VcsException refused = await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ReopenAsync([game], 12350, cancellationToken: Token));
		StringAssert.Contains(refused.Message, "//Game/main/Source/Game/OldGame.cpp");
		Assert.IsFalse(connection.Calls.Any(call => call.Command == "reopen"), "nothing moved");
	}

	[TestMethod]
	public async Task ClientNamesDifferingInCaseMatchOnlyOnACaseInsensitiveServer()
	{
		using TempDirectory root = new();
		using PerforceVersionControl insensitive = new(Fake(root).On("change", "change-o-pending-other-case").On("info", "info"), root.Directory);
		Assert.AreEqual(12346, (await insensitive.GetOwnPendingChangeAsync(12346, Token)).Number);

		using PerforceVersionControl sensitive = new(Fake(root).On("change", "change-o-pending-other-case").On("info", "info-case-sensitive"), root.Directory);
		StringAssert.Contains((await Assert.ThrowsExactlyAsync<VcsException>(() => sensitive.GetOwnPendingChangeAsync(12346, Token))).Message, "belongs to client User-WS");

		// Equal names never ask the server.
		FakePerforceConnection same = Fake(root).On("change", "change-o-pending");
		using PerforceVersionControl exact = new(same, root.Directory);
		await exact.GetOwnPendingChangeAsync(12346, Token);
		Assert.IsFalse(same.Calls.Any(call => call.Command == "info"));
	}

	[TestMethod]
	public async Task ShelveTimeIsReadInTheServersTimeZone()
	{
		using TempDirectory root = new();
		using PerforceVersionControl p4 = new(Fake(root).On("change", "change-o-pending").On("info", "info"), root.Directory);
		Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 9, 25, 0, TimeSpan.FromHours(10)), await p4.GetShelveTimeAsync(12346, Token));

		using PerforceVersionControl none = new(Fake(root).On("change", "change-o-new"), root.Directory);
		Assert.IsNull(await none.GetShelveTimeAsync(12350, Token));
	}

	[TestMethod]
	public async Task ShelveRefusesAChangeWithNothingOpened()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = Fake(root).On("change", "change-o-empty");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		VcsException exception = await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ShelveAsync(12350, ShelveMode.Replace, cancellationToken: Token));
		StringAssert.Contains(exception.Message, "no opened files");
		Assert.IsFalse(connection.Calls.Any(call => call.Command == "shelve"), "-r with nothing opened would empty the shelf");
	}

	[TestMethod]
	public async Task ShelveErrorsAreFailures()
	{
		using TempDirectory root = new();
		using PerforceVersionControl already = new(Fake(root).On("change", "change-o-pending").On("shelve", "shelve-already").On("describe", "describe-s-none"), root.Directory);
		StringAssert.Contains((await Assert.ThrowsExactlyAsync<VcsException>(() => already.ShelveAsync(12346, ShelveMode.Update, cancellationToken: Token))).Message, "already shelved");

		using PerforceVersionControl none = new(Fake(root).On("change", "change-o-pending").On("shelve", "shelve-no-files").On("describe", "describe-s-none"), root.Directory);
		StringAssert.Contains((await Assert.ThrowsExactlyAsync<VcsException>(() => none.ShelveAsync(12346, ShelveMode.Update, cancellationToken: Token))).Message, "No files to shelve");

		using PerforceVersionControl other = new(Fake(root).On("change", "change-o-other-client"), root.Directory);
		await Assert.ThrowsExactlyAsync<VcsException>(() => other.ShelveAsync(12346, ShelveMode.Update, cancellationToken: Token));
	}

	[TestMethod]
	public async Task EditChecksEachFileAfterwards()
	{
		using TempDirectory root = new();
		string file = root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = Fake(root).On("edit", "edit").On("fstat", "fstat-ro-default");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		IReadOnlyList<PerforceFileResult> results = await p4.EditAsync([file], null, cancellationToken: Token);

		Assert.AreEqual(new PerforceFileResult(file, true, "edit", "default", null) { DepotFile = "//Game/main/Source/Game/Game.cpp" }, results.Single());
		Assert.IsEmpty(connection.Calls[0].Arguments, "the default changelist: no -c");
		CollectionAssert.AreEqual(new[] { file }, connection.Calls[0].FileArguments.ToArray());
		CollectionAssert.AreEqual(new[] { "-Ro" }, connection.Calls[1].Arguments.ToArray());
	}

	[TestMethod]
	public async Task EditReportsAFileOpenedInAnotherChange()
	{
		using TempDirectory root = new();
		string file = root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = Fake(root).On("change", "change-o-new").On("edit", "edit-other-change").On("fstat", "fstat-ro-other-change");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		PerforceFileResult result = (await p4.EditAsync([file], 12350, cancellationToken: Token)).Single();

		Assert.IsFalse(result.Opened);
		Assert.AreEqual("12346", result.Change);
		StringAssert.Contains(result.Message, "can't change from change 12346 - use 'reopen'");
		CollectionAssert.AreEqual(new[] { "-c12350" }, connection.Calls.Single(call => call.Command == "edit").Arguments.ToArray());
	}

	[TestMethod]
	public async Task ReopenMovesNamedFilesAndChecksThem()
	{
		using TempDirectory root = new();
		string game = root.Write("Source/Game/Game.cpp");
		string other = root.Write("Source/Game/Other.cpp");
		FakePerforceConnection connection = Fake(root).On("change", "change-o-new").On("reopen", "reopen").On("fstat", "fstat-ro-moved");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		IReadOnlyList<PerforceFileResult> results = await p4.ReopenAsync([game, other], 12350, cancellationToken: Token);

		Assert.IsTrue(results.All(result => result.Opened && result.Change == "12350"));
		PerforceCall reopen = connection.Calls.Single(call => call.Command == "reopen");
		CollectionAssert.AreEqual(new[] { "-c12350" }, reopen.Arguments.ToArray());
		CollectionAssert.AreEqual(new[] { game, other }, reopen.FileArguments.ToArray());

		await p4.ReopenAsync([game], null, cancellationToken: Token);
		CollectionAssert.AreEqual(new[] { "-cdefault" }, connection.Calls.Last(call => call.Command == "reopen").Arguments.ToArray());
	}

	[TestMethod]
	public async Task ReopenRefusesDirectoriesAndWildcardsUnlessAllowed()
	{
		using TempDirectory root = new();
		root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = Fake(root).On("change", "change-o-new").On("reopen", "reopen").On("fstat", "fstat-ro-moved");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		StringAssert.Contains((await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ReopenAsync([root.Combine("Source")], 12350, cancellationToken: Token))).Message, "-folders");
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ReopenAsync([root.Combine("Source") + "/...", ], 12350, allowDirectories: true, cancellationToken: Token));
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ReopenAsync([root.Combine("Source/*.cpp")], 12350, cancellationToken: Token));
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ReopenAsync([Path.GetTempPath()], 12350, allowDirectories: true, cancellationToken: Token));
		Assert.IsEmpty(connection.Calls, "refused before asking the server");

		IReadOnlyList<PerforceFileResult> results = await p4.ReopenAsync([root.Combine("Source")], 12350, allowDirectories: true, cancellationToken: Token);
		CollectionAssert.AreEqual(new[] { Path.Combine(root.Combine("Source"), "...") }, connection.Calls.Single(call => call.Command == "reopen").FileArguments.ToArray());
		Assert.HasCount(2, results, "every file opened under the directory is reported");
	}

	[TestMethod]
	public async Task ReopenIntoAnotherClientsChangeIsRefused()
	{
		using TempDirectory root = new();
		string file = root.Write("Source/Game/Game.cpp");
		FakePerforceConnection connection = Fake(root).On("change", "change-o-other-client");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ReopenAsync([file], 12346, cancellationToken: Token));
		Assert.IsFalse(connection.Calls.Any(call => call.Command == "reopen"));
	}

	[TestMethod]
	public async Task AddRefusesMissingFilesAndPassesSpecialNamesWithF()
	{
		using TempDirectory root = new();
		string art = root.Write("Source/Game/Art@2x.png");
		FakePerforceConnection connection = Fake(root).On("change", "change-o-pending").On("add", "add-art").On("fstat", "fstat-ro-added");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		StringAssert.Contains((await Assert.ThrowsExactlyAsync<VcsException>(() => p4.AddAsync([root.Combine("Source/Game/Missing.cpp")], null, Token))).Message, "No such file");
		Assert.IsEmpty(connection.Calls);

		PerforceFileResult result = (await p4.AddAsync([art], 12346, Token)).Single();

		Assert.IsTrue(result.Opened);
		Assert.AreEqual(art, result.Path);
		PerforceCall add = connection.Calls.Single(call => call.Command == "add");
		CollectionAssert.AreEqual(new[] { "-c12346", "-f" }, add.Arguments.ToArray());
		CollectionAssert.AreEqual(new[] { art }, add.FileArguments.ToArray(), "add takes the raw name");
		CollectionAssert.AreEqual(new[] { root.Combine("Source/Game/Art%402x.png") }, connection.Calls.Single(call => call.Command == "fstat").FileArguments.ToArray());
	}

	[TestMethod]
	public async Task AddOfAnIgnoredFileIsNotOpened()
	{
		using TempDirectory root = new();
		string log = root.Write("Saved/Logs/Game.log");
		FakePerforceConnection connection = Fake(root).On("add", "add-n-ignored").On("fstat", "fstat-none-opened");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		PerforceFileResult result = (await p4.AddAsync([log], null, Token)).Single();

		Assert.IsFalse(result.Opened);
		StringAssert.Contains(result.Message, "ignored file can't be added");
	}

	[TestMethod]
	public async Task GetOpenedListsOnlyOpenedFiles()
	{
		using TempDirectory root = new();
		string game = root.Write("Source/Game/Game.cpp");
		string other = root.Write("Source/Game/Other.cpp");
		using PerforceVersionControl p4 = new(Fake(root).On("fstat", "fstat-ro-one-not-opened"), root.Directory);

		IReadOnlyDictionary<string, PerforceFileResult> opened = await p4.GetOpenedAsync([game, other], Token);

		Assert.HasCount(1, opened);
		Assert.AreEqual("default", opened[game].Change);
	}

	[TestMethod]
	public async Task StreamsAreReadFromInfoAndStreamSpecs()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = Fake(root).On("info", "info-stream").On("stream", call => call.Arguments[1] switch
		{
			"//Game/main-virtual" => "stream-virtual",
			"//Game/dev" => "stream-dev",
			_ => "stream-main",
		});
		using PerforceVersionControl p4 = new(connection, root.Directory);

		Assert.AreEqual("//Game/main-virtual", await p4.GetClientStreamAsync(Token));
		Assert.AreEqual(("virtual", (string?)"//Game/dev"), await p4.GetStreamAsync("//Game/main-virtual", Token));
		Assert.AreEqual(("development", (string?)"//Game/main"), await p4.GetStreamAsync("//Game/dev", Token));
		Assert.AreEqual(("mainline", (string?)null), await p4.GetStreamAsync("//Game/main", Token));
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.GetStreamAsync("//Game/...", Token));
	}

	[TestMethod]
	public async Task PlainTextAnswersAreFailures()
	{
		using TempDirectory root = new();
		using PerforceVersionControl p4 = new(Fake(root).On("change", "text:Perforce password (P4PASSWD) invalid or unset.\n"), root.Directory);

		VcsException exception = await Assert.ThrowsExactlyAsync<VcsException>(() => p4.GetOwnPendingChangeAsync(12346, Token));
		StringAssert.Contains(exception.Message, "P4PASSWD");
	}

	[TestMethod]
	public void RawRecordsReplaceOneFieldAndDropTheCode()
	{
		PerforceRawRecord record = PerforceRawRecord.FromFields(("code", "stat"), ("Change", "5"), ("Description", "old"), ("Files0", "//a"));
		List<KeyValuePair<string, string>> sent = DecodeSpec(record.SerializeWith("Description", "new"));
		CollectionAssert.AreEqual(new[] { "Change=5", "Description=new", "Files0=//a" }, sent.Select(field => $"{field.Key}={field.Value}").ToArray());

		List<KeyValuePair<string, string>> added = DecodeSpec(PerforceRawRecord.FromFields(("Change", "5")).SerializeWith("Description", "text"));
		Assert.AreEqual("Description", added[^1].Key);
		CollectionAssert.AreEqual(new[] { "//a" }, record.GetList("Files").ToArray());
	}
}
