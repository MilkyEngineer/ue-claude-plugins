// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;

namespace AgentKit.Locking;

/// <summary>One target's binaries that a build left mixed (<see cref="RebuildNeededFile"/>).</summary>
public sealed class RebuildNeededEntry
{
	/// <summary>The UBT target.</summary>
	public string Target { get; set; } = "";

	/// <summary>Its platform.</summary>
	public string Platform { get; set; } = "";

	/// <summary>Its configuration.</summary>
	public string Configuration { get; set; } = "";

	/// <summary>The files that changed while that build ran, marked for a recompile.</summary>
	public List<string> Stamped { get; set; } = [];

	/// <summary>The files that changed while that build ran and could not be marked: nothing makes the next build recompile them.</summary>
	public List<string> NotMarked { get; set; } = [];

	/// <summary>A line for each file of <see cref="NotMarked"/>.</summary>
	public List<string> Problems { get; set; } = [];

	/// <summary>
	/// For each file of <see cref="NotMarked"/>, the later of its write time, read after its mark last failed, and the end of the
	/// build that tried it: past that, someone saved it after that build. A file with none (a marker older than this field, or a
	/// time that couldn't be read) has nothing to compare with.
	/// </summary>
	public Dictionary<string, DateTime> NotMarkedWritten { get; set; } = [];

	/// <summary>Whether this is the given build.</summary>
	public bool Is(string target, string platform, string configuration)
	{
		return Target.Equals(target, StringComparison.OrdinalIgnoreCase) && Platform.Equals(platform, StringComparison.OrdinalIgnoreCase)
			&& Configuration.Equals(configuration, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The build that clears this entry and the files it names, for a refusal. Only a file with a recorded time
	/// (<see cref="NotMarkedWritten"/>) is told to save first: a save of one without shows nothing.
	/// </summary>
	public string Describe()
	{
		// Asked now: a file deleted then may be back, and then it is one to save.
		List<string> gone = [.. NotMarked.Where(RebuildNeededFile.IsGone)];
		List<string> unsaved = [.. NotMarked.Except(gone, RebuildNeededFile.PathComparer).Where(NotMarkedWritten.ContainsKey)];
		List<string> timeless = [.. NotMarked.Except(gone, RebuildNeededFile.PathComparer).Except(unsaved, RebuildNeededFile.PathComparer)];
		List<string> parts = [];
		if (Stamped.Count > 0)
		{
			parts.Add(RebuildNeededFile.Name(Stamped));
		}
		if (unsaved.Count > 0)
		{
			parts.Add($"not marked, so save first: {RebuildNeededFile.Name(unsaved)}");
		}
		if (timeless.Count > 0)
		{
			parts.Add($"not marked, with no recorded time, so a save shows nothing: a build whose mark takes clears it, else delete the marker: {RebuildNeededFile.Name(timeless)}");
		}
		if (gone.Count > 0)
		{
			parts.Add($"deleted, so nothing to save: {RebuildNeededFile.Name(gone)}");
		}
		return $"`uak build -target={Target} -platform={Platform} -config={Configuration}` for {Target} {Platform} {Configuration} ({string.Join("; ", parts)})";
	}
}

/// <summary>What <see cref="RebuildNeededFile.Load"/> found. At most one of <see cref="Corrupt"/> and <see cref="ReadFailure"/> is set, and then <see cref="Entries"/> is empty.</summary>
public sealed class RebuildNeededContents
{
	/// <summary>What stands, for every target.</summary>
	public List<RebuildNeededEntry> Entries { get; init; } = [];

	/// <summary>Whether the file was read and holds no whole marker: bad JSON, or a null where an entry needs a value.</summary>
	public bool Corrupt { get; init; }

	/// <summary>Why the file couldn't be read (something holds it, say); null when it could. That says nothing of what it holds.</summary>
	public string? ReadFailure { get; init; }

	/// <summary>Whether <see cref="Entries"/> is all the file holds.</summary>
	public bool IsKnown => !Corrupt && ReadFailure is null;
}

/// <summary>
/// <c>&lt;State&gt;/RebuildNeeded.json</c>: for each target, what a build that overlapped an edit left mixed, which refuses later
/// commands as a mutation journal does. Read here, where <c>uak lock run</c> can reach it; what writes it is the build's
/// (<c>RebuildNeeded</c> in AgentKit.Unreal). All of it runs under the editor lock.
/// </summary>
public static class RebuildNeededFile
{
	/// <summary>The file's name in the state folder.</summary>
	public const string FileName = "RebuildNeeded.json";

	/// <summary>How many files a message names.</summary>
	public const int MaxNamed = 10;

	/// <summary>
	/// How the marker's paths compare, on every system: a path in another case is the same file, as on Windows. On a case-sensitive
	/// file system two files whose names differ only in case are one to the marker: a stated limit.
	/// </summary>
	public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

	/// <summary>The line a warning or a refusal ends with, for a project whose build writes a source file of its own.</summary>
	public const string OwnWritesHint = "A file the build writes itself is no edit: name it with `uak test config -build-writes=<path>`";

	/// <summary>ERROR_INVALID_NAME as an HRESULT: "The filename, directory name, or volume label syntax is incorrect."</summary>
	private const int ErrorInvalidName = unchecked((int)0x8007007B);

	/// <summary>ERROR_BAD_PATHNAME as an HRESULT: "The specified path is invalid."</summary>
	private const int ErrorBadPathname = unchecked((int)0x800700A1);

	/// <summary>The characters no file name holds, on this system.</summary>
	private static readonly char[] s_invalidFileNameChars = Path.GetInvalidFileNameChars();

	/// <summary>The file of the context's project.</summary>
	public static string GetFile(UakContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		return Path.Combine(context.StateDirectory.FullName, FileName);
	}

	/// <summary>Up to <see cref="MaxNamed"/> of the files, and how many more.</summary>
	public static string Name(IReadOnlyCollection<string> files)
	{
		ArgumentNullException.ThrowIfNull(files);
		string more = files.Count > MaxNamed ? $" and {files.Count - MaxNamed} more" : "";
		return string.Join(", ", files.Take(MaxNamed)) + more;
	}

	/// <summary>What stands, for every target: none when there is no file. A file that can't be read refuses: unread, it may hold a refusal.</summary>
	/// <exception cref="UakRefusalException">The file can't be read.</exception>
	public static List<RebuildNeededEntry> Read(UakContext context)
	{
		RebuildNeededContents contents = Load(context);
		if (contents.ReadFailure is not null)
		{
			throw new UakRefusalException($"{GetFile(context)} can't be read ({contents.ReadFailure}), so a build may have overlapped an edit: nothing was run."
				+ " No build clears that: try again once what holds it has let go; if nothing does, delete it");
		}
		if (contents.Corrupt)
		{
			throw new UakRefusalException($"{GetFile(context)} can't be read, so a build may have overlapped an edit: nothing was run. A whole `uak build` (no UBT arguments)"
				+ " of the target `uak test` uses, on its own, not inside `uak lock run`, that succeeds clears it; if none can, delete it");
		}
		return contents.Entries;
	}

	/// <summary>
	/// What the file holds, and whether it could tell. What writes it goes by this: an entry it can't read may stand all the same,
	/// so a file it didn't read whole is neither written over nor cleared on that account.
	/// </summary>
	public static RebuildNeededContents Load(UakContext context)
	{
		// Read, not asked whether it exists: File.Exists says no for a file it can't reach, and that would be no marker at all.
		string file = GetFile(context);
		string? text;
		try
		{
			text = StateFiles.ReadSharedIfThere(file);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new RebuildNeededContents { ReadFailure = exception.Message };
		}
		if (text is null)
		{
			return new RebuildNeededContents();
		}
		try
		{
			List<RebuildNeededEntry?> entries = UakJson.Deserialize<List<RebuildNeededEntry?>>(text);
			if (!entries.All(IsWhole))
			{
				return new RebuildNeededContents { Corrupt = true };
			}
			List<RebuildNeededEntry> whole = [.. entries.OfType<RebuildNeededEntry>()];
			whole.ForEach(entry => MergeSpellings(entry, path => Resolve(path, context.ProjectDirectory?.FullName)));
			return new RebuildNeededContents { Entries = whole };
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException)
		{
			return new RebuildNeededContents { Corrupt = true };
		}
	}

	/// <summary>
	/// <paramref name="path"/> as a full path, when it isn't fully qualified, as only a marker edited by hand has: from the
	/// project's folder (<paramref name="project"/>), not from the folder uak runs in, where it would name another file, or none.
	/// That is a relative path, one from the root (<c>\Source\Foo.cpp</c>, on the project's drive) and one from a drive's own
	/// folder (<c>C:Source\Foo.cpp</c>) alike, with the separators the build writes, so it matches the build's path for its file.
	/// An empty path, or one that can't be resolved (a NUL, say), stays as it is: it names no file anywhere. So does any path
	/// when the project isn't known.
	/// </summary>
	private static string Resolve(string path, string? project)
	{
		if (project is null || string.IsNullOrEmpty(path) || path.Contains('\0', StringComparison.Ordinal))
		{
			return path;
		}
		try
		{
			return Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path, project);
		}
		catch (ArgumentException)
		{
			return path;
		}
	}

	/// <summary>
	/// Makes <paramref name="entry"/> name each file once: its paths are keys under one comparer (<see cref="PathComparer"/>), so
	/// two spellings of one file, in case or, once <see cref="Load"/> resolved them, in form, are one. Else whatever pairs a file
	/// with its line or its time meets two keys for it, and throws, after a build UBT finished, on every build. Of
	/// <see cref="RebuildNeededEntry.NotMarked"/> the first keeps its line (<see cref="RebuildNeededEntry.Problems"/>), and a file
	/// with no line of its own, as only a marker edited by hand has, is given one, as what pairs them would drop it. Two keys of
	/// <see cref="RebuildNeededEntry.NotMarkedWritten"/> that name one file keep the later time: an earlier one would read a write
	/// made before it as a save.
	/// </summary>
	public static void MergeSpellings(RebuildNeededEntry entry) => MergeSpellings(entry, path => path);

	/// <summary>As <see cref="MergeSpellings(RebuildNeededEntry)"/>, each path made what <paramref name="resolve"/> gives first.</summary>
	private static void MergeSpellings(RebuildNeededEntry entry, Func<string, string> resolve)
	{
		ArgumentNullException.ThrowIfNull(entry);
		entry.Stamped = [.. entry.Stamped.Select(resolve).Distinct(PathComparer)];
		List<string> notMarked = [];
		List<string> problems = [];
		HashSet<string> named = new(PathComparer);
		for (int index = 0; index < entry.NotMarked.Count; index++)
		{
			string file = resolve(entry.NotMarked[index]);
			if (named.Add(file))
			{
				notMarked.Add(file);
				problems.Add(index < entry.Problems.Count ? entry.Problems[index] : $"{file} could not be marked for a recompile");
			}
		}
		entry.NotMarked = notMarked;
		entry.Problems = problems;
		Dictionary<string, DateTime> written = new(PathComparer);
		foreach ((string file, DateTime time) in entry.NotMarkedWritten)
		{
			string resolved = resolve(file);
			written[resolved] = written.TryGetValue(resolved, out DateTime other) && other > time ? other : time;
		}
		entry.NotMarkedWritten = written;
	}

	/// <summary>
	/// Whether <paramref name="file"/> is gone, which lets it leave the marker: only when the file system says it isn't found, and
	/// that answer can be believed (<see cref="NotFoundIsAnswer"/>). <see cref="File.Exists"/> says no for any error too (a share
	/// that didn't answer, a folder it may not list), and a file that can't be reached is kept. A path no file can have, as only a
	/// marker edited by hand has, is gone: it holds no file a build compiles. That is an empty path, or one with a NUL, or a file
	/// name with a character no file name may hold (<see cref="Path.GetInvalidFileNameChars"/>: on Windows <c>?</c>, <c>*</c>,
	/// <c>&lt;</c>, <c>&gt;</c>, <c>|</c>, <c>"</c> and <c>:</c> among them; elsewhere <c>?</c> and <c>*</c> are names a file may
	/// have), told before the disk is asked; or one Windows calls so: ERROR_INVALID_NAME (such a character in a folder's name, a
	/// name over 255 characters), or a path longer than any it takes (<see cref="PathTooLongException"/>). A share path with no
	/// share (<see cref="IsServerAlone"/>) is one too, told before the disk is asked. ERROR_BAD_PATHNAME, which such a path gives,
	/// is asked as "not found" is: a file system or a server that passes on an NT status meaning "path invalid" may give it for a
	/// path a file could have. ERROR_OPERATION_ABORTED, which .NET throws as a bare <see cref="OperationCanceledException"/>, is a
	/// file that can't be reached, here and on the walk: nothing is cancelled here.
	/// </summary>
	public static bool IsGone(string file) => IsGone(file, PathQuestions.Disk);

	/// <summary>As <see cref="IsGone(string)"/>, asking the file system through <paramref name="questions"/> (for tests).</summary>
	internal static bool IsGone(string file, PathQuestions questions)
	{
		if (string.IsNullOrEmpty(file) || file.Contains('\0', StringComparison.Ordinal) || Path.GetFileName(file).AsSpan().IndexOfAny(s_invalidFileNameChars) >= 0
			|| IsServerAlone(file))
		{
			return true;
		}
		try
		{
			_ = questions.GetAttributes(file);
			return false;
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException { HResult: ErrorBadPathname })
		{
			return NotFoundIsAnswer(file, questions);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException
			or IOException { HResult: ErrorInvalidName })
		{
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
		{
			return false;
		}
	}

	/// <summary>
	/// Whether <paramref name="file"/> names a server and no share, on Windows: <c>\\server</c>, <c>\\server\</c>,
	/// <c>\\?\UNC\server</c> or <c>\\.\UNC\server</c>, with either separator, as it is spelled or as Windows reads it
	/// (<see cref="Path.GetFullPath(string)"/>, which makes <c>\\server\.</c> and <c>\\server\..</c> <c>\\server\</c>; a
	/// <c>\\?\</c> path is taken as it is spelled, as Windows takes it). No file has such a path, as a file on a server is in one
	/// of its shares. Any other path, or one that can't be read so, is the disk's to answer.
	/// </summary>
	private static bool IsServerAlone(string file)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}
		string path = file.Replace('/', '\\');
		if (NamesServerAlone(path))
		{
			return true;
		}
		if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
		{
			return false;
		}
		try
		{
			return NamesServerAlone(Path.GetFullPath(path));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
		{
			return false;
		}
	}

	/// <summary>Whether <paramref name="path"/>, with <c>\</c> for its separators, is spelled as a server and no share (<see cref="IsServerAlone"/>).</summary>
	private static bool NamesServerAlone(string path)
	{
		string? server = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase) ? path[8..]
			: path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal) ? path[2..]
			: null;
		return server is not null && !server.TrimEnd('\\').Contains('\\', StringComparison.Ordinal);
	}

	/// <summary>
	/// Whether the file system's "not found" for <paramref name="file"/> can be believed: the root of its path (its drive, or its
	/// share) can be reached, and no folder on the way down to it that is there is a link whose target isn't
	/// (<see cref="LeadsNowhere"/>). A share that is disconnected, a drive that is gone, or a link to one, says not found of every
	/// file behind it, and a link is there whether what it leads to is or not, so behind any of them a file is kept. A reparse
	/// point that is no link (a cloud-synced folder's placeholder, deduplication, a projected file system) holds its files as a
	/// plain folder does, and so does a link whose target is there. False when that can't be told. A drive letter that another
	/// device took over reads as reachable: a stated limit.
	/// </summary>
	private static bool NotFoundIsAnswer(string file, PathQuestions questions)
	{
		try
		{
			string full = Path.GetFullPath(file);
			if (Path.GetPathRoot(full) is not { Length: > 0 } root || !Directory.Exists(root))
			{
				return false;
			}
			for (string? folder = Path.GetDirectoryName(full); folder is not null && folder.Length > root.Length; folder = Path.GetDirectoryName(folder))
			{
				FileAttributes attributes;
				try
				{
					attributes = questions.GetAttributes(folder);
				}
				catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
				{
					continue;
				}
				if (attributes.HasFlag(FileAttributes.ReparsePoint) && LeadsNowhere(folder, questions))
				{
					return false;
				}
			}
			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or OperationCanceledException)
		{
			return false;
		}
	}

	/// <summary>
	/// Whether the reparse point <paramref name="folder"/> is a link (a junction, a symbolic link, a mount point: one with a link
	/// target, <see cref="PathQuestions.GetLinkTarget"/>) whose final target can't be found: opened through the link, it isn't
	/// there, or it fails. True too when the link can't be opened to ask. Any other reparse point is no link.
	/// </summary>
	private static bool LeadsNowhere(string folder, PathQuestions questions)
	{
		try
		{
			return questions.GetLinkTarget(folder) is not null && !questions.FinalTargetExists(folder);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
		{
			return true;
		}
	}

	/// <summary>Whether JSON gave an entry everything it needs: a null in the file would otherwise throw in whatever reads it, and refuse with no way out.</summary>
	private static bool IsWhole(RebuildNeededEntry? entry)
	{
		return entry is { Target: not null, Platform: not null, Configuration: not null, Stamped: not null, NotMarked: not null, Problems: not null, NotMarkedWritten: not null };
	}

	/// <summary>
	/// The way out of a refusal: the <c>uak build</c> for each entry, with its target, platform, configuration and files, and the file
	/// to delete for what no build can clear: a target that no longer exists, and a file that can be neither marked nor saved.
	/// </summary>
	public static string DescribeWayOut(UakContext context, IReadOnlyCollection<RebuildNeededEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);
		string unmarked = entries.Any(entry => !entry.NotMarked.All(IsGone)) ? ", or a file that can't be marked can't be saved either" : "";
		return $"{string.Join("; ", entries.Select(entry => entry.Describe()))}, on its own, not inside `uak lock run`."
			+ $" If that target no longer exists{unmarked}, no build can clear this: delete {GetFile(context)}. {OwnWritesHint}";
	}

	/// <summary>For <c>uak lock run</c>: refuses while anything stands, for the build that clears it to be run first.</summary>
	/// <exception cref="UakRefusalException">Something stands, or the file can't be read.</exception>
	public static void ThrowIfStanding(UakContext context)
	{
		List<RebuildNeededEntry> entries = Read(context);
		if (entries.Count > 0)
		{
			throw new UakRefusalException($"source files changed while a build ran, so its binaries may mix old and new: nothing was run. Build again: {DescribeWayOut(context, entries)}");
		}
	}
}

/// <summary>
/// What <see cref="RebuildNeededFile.IsGone(string)"/> asks the file system of a path: a seam, so a test can give what it can't
/// make, such as a reparse point that is no link (a cloud-synced folder needs its provider) or an error only a server gives.
/// Each throws as the file system's own call does.
/// </summary>
internal sealed class PathQuestions
{
	/// <summary>The file system's own answers.</summary>
	public static PathQuestions Disk { get; } = new();

	/// <summary>A path's attributes (<see cref="File.GetAttributes(string)"/>).</summary>
	public Func<string, FileAttributes> GetAttributes { get; init; } = File.GetAttributes;

	/// <summary>
	/// Where a folder links to; null for a folder that is no link (<see cref="FileSystemInfo.ResolveLinkTarget(bool)"/>, not
	/// <see cref="FileSystemInfo.LinkTarget"/>, which says null of a link it can't open). Throws when it can't be opened to ask.
	/// </summary>
	public Func<string, string?> GetLinkTarget { get; init; } = folder => new DirectoryInfo(folder).ResolveLinkTarget(returnFinalTarget: false)?.FullName;

	/// <summary>Whether a link's final target, through any links on the way, is there (<see cref="FinalTargetIsThere"/>).</summary>
	public Func<string, bool> FinalTargetExists { get; init; } = FinalTargetIsThere;

	/// <summary>
	/// Whether <paramref name="folder"/> can be opened through its links, by listing it: not found says its final target isn't
	/// there; any other error throws. That asks what a file behind it is asked through, so it holds where reading the target's
	/// path wouldn't: a link seen through a share, or one that leads to a share, whose final path .NET reads as relative.
	/// </summary>
	private static bool FinalTargetIsThere(string folder)
	{
		try
		{
			using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
			_ = entries.MoveNext();
			return true;
		}
		catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
		{
			return false;
		}
	}
}
