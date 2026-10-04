// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text.RegularExpressions;
using EpicGames.Perforce;

namespace AgentKit.Vcs;

/// <summary>A pending changelist, from <c>p4 change -o</c>.</summary>
/// <param name="Number">The changelist number.</param>
/// <param name="Client">The client (workspace) it belongs to.</param>
/// <param name="User">Its owner.</param>
/// <param name="Status">"pending", "submitted" or "shelved".</param>
/// <param name="Description">Its description, as the server holds it.</param>
/// <param name="Files">The depot paths of the files opened in it.</param>
public sealed record PerforceChange(int Number, string Client, string User, string Status, string Description, IReadOnlyList<string> Files);

/// <summary>What happened to one file of an edit, add or reopen, checked with <c>p4 fstat</c> afterwards.</summary>
/// <param name="Path">The local path, as given.</param>
/// <param name="Opened">Whether the file is now opened in the requested changelist.</param>
/// <param name="Action">Its open action (edit, add, delete...), when it is opened anywhere.</param>
/// <param name="Change">The changelist it is opened in ("default" or a number), when it is opened anywhere.</param>
/// <param name="Message">What p4 said about the file, when it said anything (such as "file(s) not on client").</param>
public sealed record PerforceFileResult(string Path, bool Opened, string? Action, string? Change, string? Message)
{
	/// <summary>Its depot path, when fstat gave one.</summary>
	public string? DepotFile { get; init; }

	/// <summary>For half of a move (move/add or move/delete), the depot path of the other half.</summary>
	public string? MovedFile { get; init; }
}

/// <summary>A file p4 shelved.</summary>
/// <param name="DepotFile">Its depot path.</param>
/// <param name="Action">Its open action (edit, add, delete...).</param>
public sealed record PerforceShelvedFile(string DepotFile, string Action);

/// <summary>A file on a changelist's shelf, from <c>p4 describe -S -s</c>.</summary>
/// <param name="DepotFile">Its depot path.</param>
/// <param name="Action">Its shelved action (edit, add, delete...).</param>
/// <param name="Digest">The shelved content's MD5, when p4 gives one.</param>
public sealed record PerforceShelfFile(string DepotFile, string Action, string? Digest);

/// <summary>What a shelve did.</summary>
/// <param name="Shelved">The files p4 shelved.</param>
/// <param name="Replaced">
/// Depot paths of files that were already shelved and changed on the shelf: their content (digest) or their action changed,
/// or a digest appeared or disappeared.
/// </param>
/// <param name="Removed">Depot paths of files the shelf held before and no longer holds (only with <see cref="ShelveMode.Replace"/>).</param>
/// <param name="Kept">
/// Depot paths of files the shelf still holds although p4 did not shelve them this time: shelved earlier and no longer opened
/// in the changelist. <see cref="ShelveMode.Update"/> (p4 shelve -f) keeps them, so a submit of the shelf would include them.
/// </param>
public sealed record PerforceShelveResult(IReadOnlyList<PerforceShelvedFile> Shelved, IReadOnlyList<string> Replaced, IReadOnlyList<string> Removed, IReadOnlyList<string> Kept)
{
	/// <summary>The shelf after shelving, as <c>p4 describe -S -s</c> read it.</summary>
	public IReadOnlyList<PerforceShelfFile> Shelf { get; init; } = [];
}

/// <summary>
/// What <see cref="PerforceVersionControl.UpdateDescriptionAsync"/> put back after p4 moved files while the description was
/// written (both empty when nothing moved).
/// </summary>
/// <param name="Restored">Files p4 moved out to the default changelist (reopened into the changelist meanwhile) that uak moved back in.</param>
/// <param name="Released">Files p4 pulled in from the default changelist (moved out of the changelist meanwhile) that uak moved back out.</param>
public sealed record PerforceDescriptionUpdate(IReadOnlyList<string> Restored, IReadOnlyList<string> Released);

/// <summary>How <c>uak vcs shelve -c=</c> and <c>uak horde preflight -shelve</c> update a changelist's shelf.</summary>
public enum ShelveMode
{
	/// <summary>
	/// <c>p4 shelve -f</c> (the commands' <c>-keep-unopened</c>): shelve every opened file, overwriting those already shelved;
	/// other shelved files stay, so a preflight builds them and a submit of the shelf includes them.
	/// </summary>
	Update,

	/// <summary>
	/// <c>p4 shelve -r</c> (the commands' default): the shelf becomes exactly the opened files; shelved files that are no longer
	/// opened are removed from it, which <see cref="PerforceVersionControl.ShelveAsync"/> refuses unless its caller allows it.
	/// </summary>
	Replace,
}

/// <summary>Perforce writes: changelists, edit, add, reopen and shelve (DESIGN.md, "Version control"). Never revert or submit.</summary>
public sealed partial class PerforceVersionControl
{
	/// <summary>How long one <c>p4 shelve</c> may run: it sends file contents, so it gets far longer than a query.</summary>
	public static readonly TimeSpan ShelveTimeout = TimeSpan.FromMinutes(30);

	/// <summary>The value of <c>-c=</c> that names the default changelist.</summary>
	public const string DefaultChangeName = "default";

	/// <summary>Creates an empty pending changelist in this client, with the given description, and returns its number.</summary>
	public Task<int> CreateChangeAsync(string description, CancellationToken cancellationToken = default) => GuardAsync("change", async () =>
	{
		CheckDescription(description);
		// Change "new", this client, the description: p4 fills in the user. No Files field, so nothing moves into it.
		ChangeRecord record = new() { Client = ClientName, Description = description };
		PerforceResponse<ChangeRecord> response = await _connection.TryCreateChangeAsync(record, cancellationToken).ConfigureAwait(false);
		ThrowOnFailure(response.Error, "change");
		return response.Data.Number;
	});

	/// <summary>
	/// A pending changelist of this client. Throws <see cref="VcsException"/> when it doesn't exist, is not pending, or belongs
	/// to another client: no write ever touches someone else's changelist.
	/// </summary>
	public Task<PerforceChange> GetOwnPendingChangeAsync(int number, CancellationToken cancellationToken = default)
		=> GuardAsync("change -o", async () => ToChange(await GetOwnPendingChangeRecordAsync(number, cancellationToken).ConfigureAwait(false)));

	/// <summary>
	/// Replaces a pending changelist's description. The spec goes back exactly as <c>p4 change -o</c> gave it (its files, jobs
	/// and every other field), with only the description changed: a spec without its Files field would move its files out.
	/// The spec's Files field is what p4 makes the changelist hold, so files that changed changelist between the read and the
	/// write are moved: a file reopened into the changelist meanwhile goes out to the default changelist ("removing N
	/// file(s)"), and a file moved from it to the default changelist meanwhile comes back in ("adding N file(s)"; p4 refuses
	/// the whole spec when such a file went to another numbered changelist). uak lists the changelist's files just before the
	/// write and after it, puts back each file p4 moved (only files that were in the changelist, or in the spec), and checks
	/// that their number is the number p4 reported. When it isn't, uak can't tell which files p4 moved, so it moves nothing and
	/// fails, naming what it found.
	/// </summary>
	public Task<PerforceDescriptionUpdate> UpdateDescriptionAsync(int number, string description, CancellationToken cancellationToken = default) => GuardAsync("change -i", async () =>
	{
		CheckDescription(description);
		PerforceRawRecord spec = await GetOwnPendingChangeRecordAsync(number, cancellationToken).ConfigureAwait(false);
		HashSet<string> inSpec = new(spec.GetList("Files"), StringComparer.Ordinal);
		HashSet<string> before = await GetOpenedInChangeAsync(Number(number), cancellationToken).ConfigureAwait(false);
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "change", ["-i"], null, spec.SerializeWith("Description", description), cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "change -i", warningsFail: true);
		string? updated = responses.Where(response => response.Code == "info").Select(response => response.Message).FirstOrDefault(message => message.StartsWith("Change ", StringComparison.Ordinal));
		if (updated is null || !updated.Contains(" updated", StringComparison.Ordinal))
		{
			throw new VcsException($"p4 change -i did not update change {number} as expected: {updated ?? "no answer"}");
		}
		int removing = CountIn(updated, "removing");
		int adding = CountIn(updated, "adding");
		if (removing == 0 && adding == 0)
		{
			return new PerforceDescriptionUpdate([], []);
		}

		// The spec moved files: out to the default changelist (reopened into this one after uak read it), or in from it (moved
		// out after uak read it). Only files that were in the changelist just before the write, or in the spec, are candidates,
		// so unrelated files that reached the default changelist meanwhile are never touched.
		HashSet<string> after = await GetOpenedInChangeAsync(Number(number), cancellationToken).ConfigureAwait(false);
		HashSet<string> inDefault = await GetOpenedInChangeAsync(DefaultChangeName, cancellationToken).ConfigureAwait(false);
		List<string> removed = before.Where(file => !after.Contains(file) && !inSpec.Contains(file) && inDefault.Contains(file)).Order(StringComparer.Ordinal).ToList();
		List<string> added = after.Where(file => !before.Contains(file) && inSpec.Contains(file)).Order(StringComparer.Ordinal).ToList();
		if (removed.Count != removing || added.Count != adding)
		{
			throw new VcsException($"p4 moved files while change {number}'s description was updated ({updated}), and uak could not tell exactly which, so it moved none of them back. " +
				$"Files that left change {number} for the default changelist: {List(removed)}. Files that came into it from the default changelist: {List(added)}. " +
				$"Check change {number} and the default changelist, and reopen the files where they belong.");
		}
		if (removed.Count > 0)
		{
			await MoveBackAsync(removed, Number(number), $"p4 moved files out of change {number} while its description was updated, and these could not be reopened back into it (they are in the default changelist)", cancellationToken).ConfigureAwait(false);
		}
		if (added.Count > 0)
		{
			await MoveBackAsync(added, DefaultChangeName, $"p4 moved files from the default changelist into change {number} while its description was updated, and these could not be reopened back into the default changelist (they are in change {number})", cancellationToken).ConfigureAwait(false);
		}
		return new PerforceDescriptionUpdate(removed, added);
	});

	/// <summary>The count p4 reports before "file(s)" after a word ("removing 2 file(s)"); 0 when the word isn't there.</summary>
	static int CountIn(string message, string word)
	{
		Match match = Regex.Match(message, "\\b" + word + " (\\d+) file", RegexOptions.CultureInvariant);
		return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : 0;
	}

	static string List(IReadOnlyCollection<string> files) => files.Count == 0 ? "none found" : string.Join(", ", files);

	/// <summary>Reopens depot files into a changelist ("default" or a number) and checks they arrived; throws, naming those that didn't.</summary>
	async Task MoveBackAsync(List<string> depotFiles, string change, string failure, CancellationToken cancellationToken)
	{
		List<PerforceRawRecord> reopened = await PerforceRawRecord.RunAsync(_connection, "reopen", ["-c" + change], depotFiles, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(reopened, "reopen", warningsFail: false);
		HashSet<string> now = await GetOpenedInChangeAsync(change, cancellationToken).ConfigureAwait(false);
		List<string> stranded = depotFiles.Where(file => !now.Contains(file)).ToList();
		if (stranded.Count > 0)
		{
			throw new VcsException($"{failure}: {string.Join(", ", stranded)}");
		}
	}

	/// <summary>The depot paths of the files this client has opened in a changelist ("default" or a number), from <c>p4 opened -c</c>.</summary>
	async Task<HashSet<string>> GetOpenedInChangeAsync(string change, CancellationToken cancellationToken)
	{
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "opened", ["-c", change], null, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "opened", warningsFail: false);
		return responses.Where(response => response.IsStat).Select(response => response.Get("depotFile")).OfType<string>().ToHashSet(StringComparer.Ordinal);
	}

	/// <summary>
	/// Shelves every file opened in a pending changelist of this client, and returns what p4 shelved, which shelved files
	/// changed on the shelf, which the shelf lost, and which it kept although they are no longer opened. Refuses a changelist
	/// with nothing opened (with <see cref="ShelveMode.Replace"/> that would empty the shelf). With
	/// <see cref="ShelveMode.Replace"/>, a shelf that holds files not opened in the changelist (shelved-only work, which -r
	/// deletes) is refused unless <paramref name="dropUnopened"/>; and when the shelf lost any other file (one that left the
	/// changelist between uak's check and p4's shelve), the shelve has happened but this throws, naming it. When the shelf
	/// can't be read afterwards, this throws too, naming the files -r may have deleted. Never reverts.
	/// </summary>
	public Task<PerforceShelveResult> ShelveAsync(int number, ShelveMode mode, bool dropUnopened = false, CancellationToken cancellationToken = default) => GuardAsync("shelve", async () =>
	{
		PerforceChange change = ToChange(await GetOwnPendingChangeRecordAsync(number, cancellationToken).ConfigureAwait(false));
		if (change.Files.Count == 0)
		{
			throw new VcsException($"Change {number} has no opened files to shelve.");
		}
		IReadOnlyList<PerforceShelfFile> before = await GetShelfCoreAsync(number, cancellationToken).ConfigureAwait(false);
		HashSet<string> opened = new(change.Files, StringComparer.Ordinal);
		List<string> shelvedOnly = before.Select(file => file.DepotFile).Where(file => !opened.Contains(file)).ToList();
		if (mode == ShelveMode.Replace && !dropUnopened && shelvedOnly.Count > 0)
		{
			throw new VcsException($"Change {number}'s shelf holds files that are not opened in it, and p4 shelve -r would delete them from the shelf: " +
				string.Join(", ", shelvedOnly) + ". Nothing was shelved. Shelve with -keep-unopened (p4 shelve -f) to keep them.");
		}
		string flag = mode == ShelveMode.Replace ? "-r" : "-f";
		IPerforceConnection connection = _connection is P4ProcessConnection process && process.Timeout < ShelveTimeout ? process.WithTimeout(ShelveTimeout) : _connection;
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(connection, "shelve", [flag, "-c" + Number(number)], null, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "shelve", warningsFail: true);
		IReadOnlyList<PerforceShelvedFile> shelved = responses.Where(response => response.IsStat && response.Get("depotFile") is not null)
			.Select(response => new PerforceShelvedFile(response.Get("depotFile")!, response.Get("action") ?? string.Empty)).ToList();
		if (shelved.Count == 0)
		{
			throw new VcsException($"p4 shelve reported no shelved files for change {number}.");
		}

		IReadOnlyList<PerforceShelfFile> after;
		try
		{
			after = await GetShelfCoreAsync(number, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is VcsException or PerforceException or TimeoutException or System.ComponentModel.Win32Exception)
		{
			string deleted = mode != ShelveMode.Replace ? "" :
				$" p4 shelve -r deletes shelved files that are not opened in the changelist; when uak checked, these were shelved and not opened: {(shelvedOnly.Count == 0 ? "none" : string.Join(", ", shelvedOnly))} (a file that left the changelist since then is deleted too).";
			throw new VcsException($"p4 shelve {flag} shelved {shelved.Count} file(s) in change {number}, but uak could not read the shelf afterwards ({VcsPaths.OneLine(exception.Message)}), so it can't tell which shelved files changed or were removed.{deleted} Check the shelf with p4 describe -S {number}.", exception);
		}
		Dictionary<string, PerforceShelfFile> now = after.ToDictionary(file => file.DepotFile, StringComparer.Ordinal);
		List<string> removed = before.Where(file => !now.ContainsKey(file.DepotFile)).Select(file => file.DepotFile).ToList();
		List<string> replaced = before.Where(file => now.TryGetValue(file.DepotFile, out PerforceShelfFile? current) && !SameShelvedFile(file, current))
			.Select(file => file.DepotFile).ToList();
		HashSet<string> shelvedNow = new(shelved.Select(file => file.DepotFile), StringComparer.Ordinal);
		List<string> kept = after.Select(file => file.DepotFile).Where(file => !shelvedNow.Contains(file)).ToList();

		// -r deletes every shelved file that isn't opened: only those uak saw and was told to drop may go. A file that left the
		// changelist after uak read it was deleted from the shelf without anyone asking.
		HashSet<string> allowed = mode == ShelveMode.Replace && dropUnopened ? new(shelvedOnly, StringComparer.Ordinal) : new(StringComparer.Ordinal);
		List<string> unexpected = removed.Where(file => !allowed.Contains(file)).ToList();
		if (unexpected.Count > 0)
		{
			throw new VcsException($"p4 shelve {flag} shelved {shelved.Count} file(s) in change {number}, but it also deleted from the shelf files that uak was not told to drop: " +
				string.Join(", ", unexpected) + $". They left change {number} while uak was shelving (they may still be opened in another changelist). Their shelved content is gone from this shelf: shelve them again where they belong.");
		}
		return new PerforceShelveResult(shelved, replaced, removed, kept) { Shelf = after };
	});

	/// <summary>
	/// Whether a file holds the same thing on two reads of a shelf: the same action and the same digest (a digest that appears
	/// or disappears is a change).
	/// </summary>
	public static bool SameShelvedFile(PerforceShelfFile a, PerforceShelfFile b)
		=> a.DepotFile.Equals(b.DepotFile, StringComparison.Ordinal) && a.Action.Equals(b.Action, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Digest, b.Digest, StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether two reads of a shelf hold the same files, each with the same action and digest (see <see cref="SameShelvedFile"/>).</summary>
	public static bool SameShelf(IReadOnlyCollection<PerforceShelfFile> a, IReadOnlyCollection<PerforceShelfFile> b)
	{
		if (a.Count != b.Count)
		{
			return false;
		}
		Dictionary<string, PerforceShelfFile> byPath = new(StringComparer.Ordinal);
		foreach (PerforceShelfFile file in a)
		{
			byPath[file.DepotFile] = file;
		}
		return byPath.Count == a.Count && b.All(file => byPath.TryGetValue(file.DepotFile, out PerforceShelfFile? other) && SameShelvedFile(file, other));
	}

	/// <summary>
	/// The files on a pending changelist's shelf that are not opened in it (a later <c>p4 shelve -f</c> keeps them, and a submit
	/// of the shelf would include them). This client's changelists only.
	/// </summary>
	public Task<IReadOnlyList<string>> GetShelvedNotOpenedAsync(int number, CancellationToken cancellationToken = default) => GuardAsync("describe", async () =>
	{
		PerforceChange change = ToChange(await GetOwnPendingChangeRecordAsync(number, cancellationToken).ConfigureAwait(false));
		HashSet<string> opened = new(change.Files, StringComparer.Ordinal);
		IReadOnlyList<PerforceShelfFile> shelf = await GetShelfCoreAsync(number, cancellationToken).ConfigureAwait(false);
		return (IReadOnlyList<string>)shelf.Select(file => file.DepotFile).Where(file => !opened.Contains(file)).ToList();
	});

	/// <summary>The files on a changelist's shelf (<c>p4 describe -S -s</c>); empty when nothing is shelved.</summary>
	public Task<IReadOnlyList<PerforceShelfFile>> GetShelfAsync(int number, CancellationToken cancellationToken = default)
		=> GuardAsync("describe", () => GetShelfCoreAsync(number, cancellationToken));

	async Task<IReadOnlyList<PerforceShelfFile>> GetShelfCoreAsync(int number, CancellationToken cancellationToken)
	{
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "describe", ["-S", "-s", Number(number)], null, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "describe", warningsFail: true);
		List<PerforceShelfFile> files = [];
		foreach (PerforceRawRecord record in responses.Where(response => response.IsStat))
		{
			IReadOnlyList<string> depotFiles = record.GetList("depotFile");
			for (int index = 0; index < depotFiles.Count; index++)
			{
				string suffix = index.ToString(CultureInfo.InvariantCulture);
				files.Add(new PerforceShelfFile(depotFiles[index], record.Get("action" + suffix) ?? string.Empty, record.Get("digest" + suffix)));
			}
		}
		return files;
	}

	/// <summary>
	/// When a changelist's files were last shelved (<c>p4 change -o</c>'s shelveUpdate, in the server's time zone from
	/// <c>p4 info</c>'s tzoffset), or null when it has no shelf or the server's time zone isn't known (no tzoffset). Any
	/// client's changelist: this only reads. The time has 1 s resolution, and tzoffset is the server's offset now, so a
	/// daylight-saving change since the shelve moves it by the change; callers compare it with a margin, never alone.
	/// </summary>
	public Task<DateTimeOffset?> GetShelveTimeAsync(int number, CancellationToken cancellationToken = default) => GuardAsync("change -o", async () =>
	{
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "change", ["-o", Number(number)], null, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "change -o", warningsFail: true);
		string? text = responses.FirstOrDefault(response => response.IsStat)?.Get("shelveUpdate");
		if (string.IsNullOrEmpty(text) || !DateTime.TryParseExact(text, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
		{
			return (DateTimeOffset?)null;
		}
		PerforceRawRecord info = await GetServerInfoAsync(cancellationToken).ConfigureAwait(false);
		// Without the server's offset the time can't be placed: reading it as UTC could make it too early, which would make an
		// older preflight look newer than the shelf.
		if (!int.TryParse(info.Get("tzoffset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || Math.Abs(seconds) > 14 * 3600 || seconds % 60 != 0)
		{
			return null;
		}
		return new DateTimeOffset(local, TimeSpan.FromSeconds(seconds));
	});

	PerforceRawRecord? _serverInfo;

	/// <summary><c>p4 info</c>, read once per connection.</summary>
	async Task<PerforceRawRecord> GetServerInfoAsync(CancellationToken cancellationToken)
	{
		if (_serverInfo is null)
		{
			List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "info", [], null, null, cancellationToken).ConfigureAwait(false);
			ThrowOnErrors(responses, "info", warningsFail: true);
			_serverInfo = responses.FirstOrDefault(response => response.IsStat) ?? throw new VcsException("p4 info returned nothing.");
		}
		return _serverInfo;
	}

	/// <summary>
	/// The halves of moves among <paramref name="files"/> whose other half is not among them: p4 moves both halves of a move
	/// together, so naming one would also move a file nobody named.
	/// </summary>
	public static IReadOnlyList<PerforceFileResult> FindHalfMoves(IEnumerable<PerforceFileResult> files)
	{
		List<PerforceFileResult> list = files.ToList();
		HashSet<string> named = list.Select(file => file.DepotFile).OfType<string>().ToHashSet(StringComparer.Ordinal);
		return list.Where(file => file.MovedFile is not null && !named.Contains(file.MovedFile)).ToList();
	}

	/// <summary>Throws when <paramref name="files"/> hold half of a move without the other half (see <see cref="FindHalfMoves"/>).</summary>
	internal static void ThrowOnHalfMoves(IEnumerable<PerforceFileResult> files)
	{
		IReadOnlyList<PerforceFileResult> halves = FindHalfMoves(files);
		if (halves.Count > 0)
		{
			throw new VcsException("Half of a move was named without the other half, and p4 moves both together: " +
				string.Join("; ", halves.Select(file => $"{file.Path} ({file.Action}, moved with {file.MovedFile})")) + ". Name both files of each move. Nothing was changed.");
		}
	}

	/// <summary>
	/// Opens files for edit, in <paramref name="change"/> (a number, or null for the default changelist), then checks with fstat
	/// that each is opened there. A file already opened in another changelist stays where it is (p4 edit does not move it), and
	/// its result says so.
	/// </summary>
	/// <param name="paths">Local files, or with <paramref name="allowDirectories"/> directories (everything under them).</param>
	/// <param name="change">A pending changelist of this client, or null for the default changelist.</param>
	/// <param name="allowDirectories">Whether directories are allowed; otherwise a directory is rejected.</param>
	/// <param name="cancellationToken">Cancels the command.</param>
	public Task<IReadOnlyList<PerforceFileResult>> EditAsync(IReadOnlyList<string> paths, int? change, bool allowDirectories = false, CancellationToken cancellationToken = default) => GuardAsync("edit", async () =>
	{
		List<string> specs = ToFileSpecs(paths, allowDirectories);
		await CheckTargetChangeAsync(change, cancellationToken).ConfigureAwait(false);
		List<string> arguments = change is null ? [] : ["-c" + Number(change.Value)];
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "edit", arguments, specs, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "edit", warningsFail: false);
		return await CheckOpenedAsync(paths, specs, change, responses, cancellationToken).ConfigureAwait(false);
	});

	/// <summary>
	/// Opens new files for add, in <paramref name="change"/> (a number, or null for the default changelist), then checks with
	/// fstat that each is opened there. Ignored files (P4IGNORE) are not added: their result carries p4's message. Files that
	/// don't exist are refused before anything runs.
	/// </summary>
	public Task<IReadOnlyList<PerforceFileResult>> AddAsync(IReadOnlyList<string> paths, int? change, CancellationToken cancellationToken = default) => GuardAsync("add", async () =>
	{
		List<string> fullPaths = paths.Select(path => CheckInsideClient(path, allowDirectory: false)).ToList();
		if (fullPaths.Count == 0)
		{
			throw new VcsException("No files given.");
		}
		// p4 add opens a file that doesn't exist too, which would submit as an empty or broken file.
		List<string> missing = fullPaths.Where(path => !File.Exists(path)).ToList();
		if (missing.Count > 0)
		{
			throw new VcsException("No such file, so not added: " + string.Join(", ", missing));
		}
		await CheckTargetChangeAsync(change, cancellationToken).ConfigureAwait(false);
		// add takes names as they are on disk; with -f p4 escapes @ # % * itself (and refuses such names without it).
		List<string> arguments = change is null ? [] : ["-c" + Number(change.Value)];
		if (fullPaths.Any(NeedsEscaping))
		{
			arguments.Add("-f");
		}
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "add", arguments, fullPaths, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "add", warningsFail: false);
		return await CheckOpenedAsync(paths, fullPaths.Select(EscapePath).ToList(), change, responses, cancellationToken).ConfigureAwait(false);
	});

	/// <summary>
	/// Moves opened files into <paramref name="change"/> (a number, or null for the default changelist), then checks with fstat
	/// that each is opened there. Files only, unless <paramref name="allowDirectories"/>: a directory would also move every
	/// other file opened under it. Half of a move without its other half is refused before anything moves.
	/// </summary>
	public Task<IReadOnlyList<PerforceFileResult>> ReopenAsync(IReadOnlyList<string> paths, int? change, bool allowDirectories = false, CancellationToken cancellationToken = default) => GuardAsync("reopen", async () =>
	{
		List<string> specs = ToFileSpecs(paths, allowDirectories);
		await CheckTargetChangeAsync(change, cancellationToken).ConfigureAwait(false);
		ThrowOnHalfMoves((await GetOpenedCoreAsync(specs, cancellationToken).ConfigureAwait(false)).Values);
		List<string> arguments = [change is null ? "-c" + DefaultChangeName : "-c" + Number(change.Value)];
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "reopen", arguments, specs, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "reopen", warningsFail: false);
		return await CheckOpenedAsync(paths, specs, change, responses, cancellationToken).ConfigureAwait(false);
	});

	/// <summary>
	/// Which of the given files are opened in this client: their fstat records, keyed by local path. Files not opened (or not
	/// known to the server) are missing from the result.
	/// </summary>
	public Task<IReadOnlyDictionary<string, PerforceFileResult>> GetOpenedAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
		=> GuardAsync("fstat", () => GetOpenedCoreAsync(paths.Select(path => EscapePath(CheckInsideClient(path, allowDirectory: false))).ToList(), cancellationToken));

	/// <summary>The stream this client is on (<c>p4 info</c>'s clientStream), or null for a classic client.</summary>
	public Task<string?> GetClientStreamAsync(CancellationToken cancellationToken = default) => GuardAsync("info", async () =>
	{
		PerforceResponse<InfoRecord> response = await _connection.TryGetInfoAsync(InfoOptions.None, cancellationToken).ConfigureAwait(false);
		ThrowOnFailure(response.Error, "info");
		return string.IsNullOrEmpty(response.Data.ClientStream) ? null : response.Data.ClientStream;
	});

	/// <summary>A stream's type (mainline, development, virtual...) and parent (null for a mainline), from <c>p4 stream -o</c>.</summary>
	public Task<(string Type, string? Parent)> GetStreamAsync(string stream, CancellationToken cancellationToken = default) => GuardAsync("stream -o", async () =>
	{
		if (!stream.StartsWith("//", StringComparison.Ordinal) || stream.Contains("...", StringComparison.Ordinal) || stream.Any(char.IsWhiteSpace))
		{
			throw new VcsException($"'{stream}' is not a stream name (//depot/name).");
		}
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "stream", ["-o", stream], null, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "stream -o", warningsFail: true);
		PerforceRawRecord record = responses.FirstOrDefault(response => response.IsStat) ?? throw new VcsException($"p4 stream -o {stream} returned no spec.");
		string? parent = record.Get("Parent");
		return (record.Get("Type") ?? string.Empty, string.IsNullOrEmpty(parent) || parent == "none" ? null : parent);
	});

	async Task<PerforceRawRecord> GetOwnPendingChangeRecordAsync(int number, CancellationToken cancellationToken)
	{
		if (number <= 0)
		{
			throw new VcsException($"{number} is not a changelist number.");
		}
		List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "change", ["-o", Number(number)], null, null, cancellationToken).ConfigureAwait(false);
		ThrowOnErrors(responses, "change -o", warningsFail: true);
		PerforceRawRecord record = responses.FirstOrDefault(response => response.IsStat) ?? throw new VcsException($"p4 change -o {number} returned no spec.");
		PerforceChange change = ToChange(record);
		if (change.Number != number)
		{
			throw new VcsException($"p4 change -o {number} returned change {change.Number}.");
		}
		if (!change.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
		{
			throw new VcsException($"Change {number} is {change.Status}, not pending.");
		}
		if (ClientName is null || !await IsSameClientAsync(change.Client, ClientName, cancellationToken).ConfigureAwait(false))
		{
			throw new VcsException($"Change {number} belongs to client {change.Client}, not this workspace's client {ClientName ?? "(unset)"}: uak changes only this client's changelists.");
		}
		return record;
	}

	/// <summary>
	/// Whether two client names are the same client: equal, or equal ignoring case on a server that ignores case (p4 info's
	/// caseHandling). The server is asked only when the names differ in case alone.
	/// </summary>
	async Task<bool> IsSameClientAsync(string a, string b, CancellationToken cancellationToken)
	{
		if (a.Equals(b, StringComparison.Ordinal))
		{
			return true;
		}
		if (!a.Equals(b, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		PerforceRawRecord info = await GetServerInfoAsync(cancellationToken).ConfigureAwait(false);
		return string.Equals(info.Get("caseHandling"), "insensitive", StringComparison.OrdinalIgnoreCase);
	}

	static PerforceChange ToChange(PerforceRawRecord record)
	{
		_ = int.TryParse(record.Get("Change"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number);
		return new PerforceChange(number, record.Get("Client") ?? string.Empty, record.Get("User") ?? string.Empty, record.Get("Status") ?? string.Empty,
			(record.Get("Description") ?? string.Empty).TrimEnd('\n', '\r'), record.GetList("Files"));
	}

	/// <summary>Checks that a target changelist (null is the default changelist) is a pending changelist of this client.</summary>
	async Task CheckTargetChangeAsync(int? change, CancellationToken cancellationToken)
	{
		if (change is not null)
		{
			await GetOwnPendingChangeRecordAsync(change.Value, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>The p4 file arguments for local paths: escaped files, and, when allowed, "&lt;dir&gt;/..." for directories.</summary>
	List<string> ToFileSpecs(IReadOnlyList<string> paths, bool allowDirectories)
	{
		if (paths.Count == 0)
		{
			throw new VcsException("No files given.");
		}
		List<string> specs = [];
		foreach (string path in paths)
		{
			string fullPath = CheckInsideClient(path, allowDirectories);
			specs.Add(Directory.Exists(fullPath) ? DirectoryPattern(fullPath) : EscapePath(fullPath));
		}
		return specs;
	}

	/// <summary>A path as a full path inside the client's root; rejects wildcards, paths outside the root, and directories unless allowed.</summary>
	string CheckInsideClient(string path, bool allowDirectory)
	{
		if (path.IndexOfAny(['*', '?']) >= 0 || path.Contains("...", StringComparison.Ordinal))
		{
			throw new VcsException($"{path}: wildcards are not accepted; name the files.");
		}
		string fullPath = Path.GetFullPath(path);
		if (!VcsPaths.IsUnder(fullPath, RootDirectory.FullName))
		{
			throw new VcsException($"{fullPath} is outside the client's root {RootDirectory.FullName}.");
		}
		if (Directory.Exists(fullPath) && !allowDirectory)
		{
			throw new VcsException($"{fullPath} is a directory. Name the files, or pass -folders to take every file under it (for reopen, that includes every other file opened there).");
		}
		return fullPath;
	}

	/// <summary>After an edit, add or reopen: each path's state from fstat, with p4's message for files it didn't open.</summary>
	async Task<IReadOnlyList<PerforceFileResult>> CheckOpenedAsync(IReadOnlyList<string> paths, List<string> specs, int? change, List<PerforceRawRecord> responses, CancellationToken cancellationToken)
	{
		IReadOnlyDictionary<string, PerforceFileResult> opened = await GetOpenedCoreAsync(specs, cancellationToken).ConfigureAwait(false);
		string wanted = change is null ? DefaultChangeName : Number(change.Value);
		List<string> messages = responses.Where(response => !response.IsStat).Select(response => response.Message).Where(message => message.Length > 0).ToList();

		List<PerforceFileResult> results = [];
		foreach (string path in paths)
		{
			string fullPath = Path.GetFullPath(path);
			if (Directory.Exists(fullPath))
			{
				// Everything under the directory.
				foreach (PerforceFileResult file in opened.Values.Where(file => VcsPaths.IsUnder(file.Path, fullPath)).OrderBy(file => file.Path, VcsPaths.Comparer))
				{
					results.Add(file with { Opened = file.Change == wanted });
				}
				continue;
			}
			string? message = messages.FirstOrDefault(line => MentionsFile(line, fullPath));
			if (opened.TryGetValue(fullPath, out PerforceFileResult? state))
			{
				bool inTarget = state.Change == wanted;
				results.Add(state with { Path = fullPath, Opened = inTarget, Message = inTarget ? null : message ?? $"opened in change {state.Change}, not {wanted}" });
			}
			else
			{
				results.Add(new PerforceFileResult(fullPath, false, null, null, message ?? "not opened"));
			}
		}
		return results;
	}

	/// <summary>Whether a p4 message is about a file: it names the local path, or its file name (p4 names depot or relative paths).</summary>
	static bool MentionsFile(string message, string fullPath)
		=> message.Contains(fullPath, VcsPaths.Comparison) || message.Contains(fullPath.Replace('\\', '/'), VcsPaths.Comparison)
		|| Regex.IsMatch(message, "(^|[/\\\\])" + Regex.Escape(Path.GetFileName(fullPath)) + "([#@ ]|$)", OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);

	async Task<IReadOnlyDictionary<string, PerforceFileResult>> GetOpenedCoreAsync(List<string> specs, CancellationToken cancellationToken)
	{
		Dictionary<string, PerforceFileResult> opened = new(VcsPaths.Comparer);
		foreach (List<string> batch in VcsPaths.Batch(specs))
		{
			// -Ro: only files opened in this client. "file(s) not opened on this client" is a warning about the rest.
			List<PerforceRawRecord> responses = await PerforceRawRecord.RunAsync(_connection, "fstat", ["-Ro"], batch, null, cancellationToken).ConfigureAwait(false);
			ThrowOnErrors(responses, "fstat", warningsFail: false);
			foreach (PerforceRawRecord response in responses.Where(response => response.IsStat))
			{
				string? clientFile = response.Get("clientFile");
				string? action = response.Get("action");
				if (string.IsNullOrEmpty(clientFile) || clientFile.StartsWith("//", StringComparison.Ordinal) || string.IsNullOrEmpty(action))
				{
					continue;
				}
				string fullPath = Path.GetFullPath(LocalPathFromRecord(clientFile));
				opened[fullPath] = new PerforceFileResult(fullPath, true, action, response.Get("change") ?? DefaultChangeName, null)
				{
					DepotFile = response.Get("depotFile"),
					MovedFile = string.IsNullOrEmpty(response.Get("movedFile")) ? null : response.Get("movedFile"),
				};
			}
		}
		return opened;
	}

	static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

	static void CheckDescription(string description)
	{
		if (string.IsNullOrWhiteSpace(description))
		{
			throw new VcsException("A changelist needs a description.");
		}
	}

	/// <summary>Throws on failures (severity 3 and up), and on warnings too when <paramref name="warningsFail"/>.</summary>
	static void ThrowOnErrors(List<PerforceRawRecord> responses, string command, bool warningsFail)
	{
		PerforceRawRecord? failure = responses.FirstOrDefault(response => response.IsError && (response.Severity >= (int)PerforceSeverityCode.Failed || (warningsFail && response.Severity >= (int)PerforceSeverityCode.Warning)));
		if (failure is not null)
		{
			throw new VcsException($"p4 {command} failed: {failure.Message}");
		}
	}
}
