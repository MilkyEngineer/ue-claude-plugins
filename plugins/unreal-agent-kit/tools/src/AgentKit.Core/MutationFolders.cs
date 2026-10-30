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

	/// <summary>Every journal left, live or stopped, with the files it lists when it can be read.</summary>
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
				left.Add(new MutationFolder(Path.GetFileName(folder), folder, ReadFiles(Path.Combine(folder, RecordName))));
			}
		}
		return left;
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
			+ " them and exits 0 once none is left. Nothing was changed.";
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

	/// <summary>The paths a journal lists, read without trusting the rest of it; empty when it can't be read.</summary>
	static List<string> ReadFiles(string record)
	{
		string? text = StateFiles.ReadShared(record);
		if (string.IsNullOrWhiteSpace(text))
		{
			return [];
		}
		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			if (!document.RootElement.TryGetProperty("Files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
			{
				return [];
			}
			return [.. files.EnumerateArray()
				.Select(file => file.ValueKind == JsonValueKind.Object && file.TryGetProperty("Path", out JsonElement path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null)
				.OfType<string>()];
		}
		catch (JsonException)
		{
			return [];
		}
	}
}
