// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;

namespace AgentKit.Core;

/// <summary>A <c>uak mutate</c> journal left in the state folder, as <see cref="MutationFolders"/> sees it.</summary>
/// <param name="Name">The run's -name=.</param>
/// <param name="Directory">Its folder.</param>
/// <param name="Files">The files its journal lists; empty when the journal can't be read.</param>
public sealed record MutationFolder(string Name, string Directory, IReadOnlyList<string> Files);

/// <summary>
/// Where <c>uak mutate</c> keeps its journals, and which of them are left, for commands that must not take the tree while a
/// mutation may be in it but don't read the journals themselves (AgentKit.Locking's <c>MutationJournal</c> does): <c>uak vcs
/// shelve</c> and <c>uak horde preflight -shelve</c>, which would ship a mutant. A journal is written whole in
/// <see cref="GetPendingRoot"/> and renamed into <see cref="GetRoot"/>, and renamed back out before it is deleted, so a folder
/// in the root always holds a whole journal.
/// </summary>
public static class MutationFolders
{
	/// <summary>The file in a journal's folder that records the run.</summary>
	public const string RecordName = "journal.json";

	/// <summary>The extension of the copies of the files before the patch.</summary>
	public const string BackupExtension = ".orig";

	/// <summary>The extensions of files that load as code: what a mutant build can leave the mutation in.</summary>
	public static readonly IReadOnlySet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll", ".exe", ".so", ".dylib" };

	/// <summary>&lt;State&gt;/Mutations: one folder per run whose patch may be in the tree.</summary>
	public static string GetRoot(DirectoryInfo stateDirectory)
	{
		ArgumentNullException.ThrowIfNull(stateDirectory);
		return Path.Combine(stateDirectory.FullName, "Mutations");
	}

	/// <summary>
	/// &lt;State&gt;/MutationsPending: journals being written (before the patch, which changes nothing yet) or deleted (after the
	/// files are back). Nothing reads it: what is left there is never a mutation in the tree.
	/// </summary>
	public static string GetPendingRoot(DirectoryInfo stateDirectory)
	{
		ArgumentNullException.ThrowIfNull(stateDirectory);
		return Path.Combine(stateDirectory.FullName, "MutationsPending");
	}

	/// <summary>
	/// Whether a folder under <see cref="GetRoot"/> holds a journal: its record, a copy, or contents that can't be listed. A
	/// folder with none (a run killed before it copied anything, by a version that wrote in place) changed nothing.
	/// </summary>
	public static bool HoldsJournal(string folder)
	{
		try
		{
			return File.Exists(Path.Combine(folder, RecordName)) || Directory.EnumerateFiles(folder, "*" + BackupExtension).Any();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return true;
		}
	}

	/// <summary>
	/// Every journal left, live or stopped, whose files may hold a mutation, with the files it lists when it can be read. A
	/// journal whose record says its files are back (phase Restored: each holds its original bytes, or an edit made since
	/// with the mutation taken out) is left out: only the binaries may still hold its patch, which
	/// <see cref="DescribeBinariesIn"/> keeps off a shelf. One that can't be read, or doesn't say so as uak writes it, is in.
	/// </summary>
	public static List<MutationFolder> ListLeft(DirectoryInfo stateDirectory)
	{
		string root = GetRoot(stateDirectory);
		List<MutationFolder> left = [];
		string[] folders;
		try
		{
			folders = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [new MutationFolder("?", root, [])];
		}
		foreach (string folder in folders.Order(StringComparer.OrdinalIgnoreCase))
		{
			if (HoldsJournal(folder))
			{
				JournalView journal = ReadRecord(folder);
				if (!journal.FilesBack)
				{
					left.Add(new MutationFolder(Path.GetFileName(folder), folder, journal.Files));
				}
			}
		}
		return left;
	}

	/// <summary>
	/// The paths a journal's record lists, for stamping a journal uak can't trust: what can be named gets its recompile, whatever
	/// else is wrong with the record. Empty when none can be read. Only full paths. It says nothing of what the record leaves
	/// out: to judge the run's files, use <see cref="ReadWholeList"/>.
	/// </summary>
	public static List<string> ReadListedFiles(string folder)
	{
		return [.. ReadRecord(folder).Files.Where(Path.IsPathFullyQualified)];
	}

	/// <summary>
	/// The files of a journal's run, or null when its record can't say: it can't be read, it names another run (a copy of one,
	/// say), or its list is empty or partial, with an entry that has no full path. The same test as for
	/// <see cref="ListLeft"/>'s files being back, so what a partial or misnamed list doesn't name is never taken to be nothing.
	/// </summary>
	public static List<string>? ReadWholeList(string folder)
	{
		JournalView journal = ReadRecord(folder);
		return journal.Trusted ? journal.Files : null;
	}

	/// <summary>Whether a file loads as code (<see cref="CodeExtensions"/>).</summary>
	public static bool IsCodeFile(string path) => CodeExtensions.Contains(Path.GetExtension(path));

	/// <summary>
	/// Whether a journal whose files are back is left: only then can <see cref="DescribeBinariesIn"/> refuse, so a command
	/// needs to read a changelist's files only then.
	/// </summary>
	public static bool HasBinariesPending(DirectoryInfo stateDirectory)
	{
		return FoldersWithFilesBack(stateDirectory).Any();
	}

	/// <summary>
	/// Why <paramref name="doing"/> must not take <paramref name="files"/> (local or depot paths), or null: a journal whose
	/// files are back is left, so the binaries its mutant build wrote may hold its patch, and a project that versions its
	/// binaries would ship them. A file counts when it has the name of one the journal lists (a depot path says no more), or,
	/// when the journal doesn't know which it wrote, when it loads as code.
	/// <para>
	/// A .lib or .a counts in neither case, on purpose: the check builds the editor target, which is modular, so an import
	/// library holds no code, and a static library goes to Intermediate, off any shelf, and into a binary that does count
	/// (<c>TargetBinaries</c>, which lists no library for the same reason).
	/// </para>
	/// </summary>
	public static string? DescribeBinariesIn(DirectoryInfo stateDirectory, IEnumerable<string> files, string doing)
	{
		List<string> named = [.. files];
		List<string> found = [];
		foreach ((string folder, JournalView journal) in FoldersWithFilesBack(stateDirectory))
		{
			HashSet<string>? binaries = journal.Binaries?.Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
			List<string> held = [.. named.Where(file => binaries is null ? IsCodeFile(file) : binaries.Contains(Path.GetFileName(file)))];
			if (held.Count > 0)
			{
				found.Add($"{string.Join(", ", held)} (run '{Path.GetFileName(folder)}'{(binaries is null ? ", which doesn't know which binaries it wrote" : "")})");
			}
		}
		return found.Count == 0 ? null
			: $"Refused to {doing}: a uak mutate run's mutant build may have left its patch in these binaries: {string.Join("; ", found)}. A `uak build` that recompiles"
				+ " the files it put back clears its journal, as does `uak mutate -restore`; `uak mutate -status` lists it. Nothing was changed.";
	}

	/// <summary>Each journal left whose files are back, with what its record says.</summary>
	static IEnumerable<(string Folder, JournalView Journal)> FoldersWithFilesBack(DirectoryInfo stateDirectory)
	{
		string root = GetRoot(stateDirectory);
		string[] folders;
		try
		{
			folders = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			yield break;
		}
		foreach (string folder in folders.Order(StringComparer.OrdinalIgnoreCase))
		{
			JournalView journal = ReadRecord(folder);
			if (journal.FilesBack)
			{
				yield return (folder, journal);
			}
		}
	}

	/// <summary>
	/// Why <paramref name="doing"/> must not run now, or null: a <c>uak mutate</c> journal is left, so a run's patch may be in
	/// the tree (a live run's, or a stopped one's), and whatever this takes from the tree could hold it.
	/// </summary>
	public static string? DescribeLeft(DirectoryInfo stateDirectory, string doing)
	{
		List<MutationFolder> left = ListLeft(stateDirectory);
		if (left.Count == 0)
		{
			return null;
		}
		string runs = string.Join("; ", left.Select(folder => folder.Files.Count == 0
			? $"'{folder.Name}' ({folder.Directory})"
			: $"'{folder.Name}' ({string.Join(", ", folder.Files)})"));
		return $"Refused to {doing}: {(left.Count == 1 ? "a uak mutate journal is" : $"{left.Count} uak mutate journals are")} left, so the tree may hold"
			+ $" a mutation: {runs}. Wait until the mutation run ends, or finish a stopped one with `uak mutate -restore`; `uak mutate -status` lists"
			+ " them and exits 0 once no journal left has its patch in the tree. Nothing was changed.";
	}

	/// <summary>
	/// A warning for <paramref name="doing"/> when no project was found (<paramref name="projectFile"/> null), or null: the
	/// journals are in the project's state folder, so <see cref="DescribeLeft"/> can't see them from here. There is no list of
	/// projects to look through, so the guard warns instead of refusing.
	/// </summary>
	public static string? DescribeUnchecked(FileInfo? projectFile, string doing)
	{
		return projectFile is not null ? null
			: $"No Unreal project was found from here, so uak can't see whether a `uak mutate` run has its patch in the files to {doing} (its journal is in"
				+ " the project's state folder). Run this from the project's folder or give -project= (or UAK_PROJECT), or check that `uak mutate -status`"
				+ " exits 0 there first.";
	}

	/// <summary>What a journal's record says, read without trusting the rest of it.</summary>
	/// <param name="Files">The paths it lists (empty when it can't be read).</param>
	/// <param name="FilesBack">Whether it says its files are back; see <see cref="ReadRecord"/>.</param>
	/// <param name="Binaries">The binaries its mutant build wrote; null when it doesn't say.</param>
	/// <param name="Trusted">Whether <paramref name="Files"/> is the whole list of this folder's run; see <see cref="ReadRecord"/>.</param>
	sealed record JournalView(List<string> Files, bool FilesBack, List<string>? Binaries, bool Trusted = false);

	/// <summary>
	/// Reads the record in a journal's folder. Its list is trusted only when it is one uak wrote for this folder and
	/// MutationJournal's own check could trust: its name is the folder's, and it lists at least one file, each with a full
	/// path. Its files count as back only when it is trusted and its phase is "Restored". False when it can't be read. A
	/// record that names another run (a copy of one, say) says nothing of this folder's run.
	/// </summary>
	static JournalView ReadRecord(string folder)
	{
		string? text = StateFiles.ReadShared(Path.Combine(folder, RecordName));
		if (string.IsNullOrWhiteSpace(text))
		{
			return new JournalView([], false, null);
		}
		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
			{
				return new JournalView([], false, null);
			}
			List<string> paths = PathsIn(files);
			bool named = root.TryGetProperty("Name", out JsonElement name) && name.ValueKind == JsonValueKind.String
				&& string.Equals(name.GetString(), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase);
			bool trusted = named && paths.Count > 0 && paths.Count == files.GetArrayLength() && paths.All(Path.IsPathFullyQualified);
			bool back = trusted && root.TryGetProperty("Phase", out JsonElement phase) && phase.ValueKind == JsonValueKind.String && phase.GetString() == "Restored";
			List<string>? binaries = root.TryGetProperty("Binaries", out JsonElement written) && written.ValueKind == JsonValueKind.Array ? PathsIn(written) : null;
			return new JournalView(paths, back, binaries is not null && binaries.Count == written.GetArrayLength() ? binaries : null, trusted);
		}
		catch (JsonException)
		{
			return new JournalView([], false, null);
		}
	}

	/// <summary>The <c>Path</c> of each object in a JSON array that has one.</summary>
	static List<string> PathsIn(JsonElement entries)
	{
		return [.. entries.EnumerateArray()
			.Select(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("Path", out JsonElement path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null)
			.OfType<string>()];
	}
}
