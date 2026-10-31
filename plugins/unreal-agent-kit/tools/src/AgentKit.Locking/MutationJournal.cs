// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Locking;

/// <summary>One file a mutation touches, as the journal records it.</summary>
public sealed class MutationFile
{
	/// <summary>The file's full path.</summary>
	public string Path { get; set; } = "";

	/// <summary>Whether it existed before the patch (false: the patch creates it).</summary>
	public bool Existed { get; set; }

	/// <summary>Whether it was read-only before (a Perforce file not opened): restored with the bytes.</summary>
	public bool ReadOnly { get; set; }

	/// <summary>The SHA-256 of its bytes before the patch; null when it didn't exist.</summary>
	public string? OriginalHash { get; set; }

	/// <summary>
	/// The SHA-256 of the bytes the patch is to give it, recorded before anything is written (<see cref="MutationJournal.SetPlanned"/>):
	/// a run stopped while it wrote the patch left the file either as it was, or with these bytes, or with part of them. Null
	/// when the patch deletes the file (<see cref="PlannedDeleted"/>), or when they aren't known (git applies the patch, and
	/// uak's own applier couldn't predict its bytes).
	/// </summary>
	public string? PlannedHash { get; set; }

	/// <summary>Whether the patch is to delete the file.</summary>
	public bool PlannedDeleted { get; set; }

	/// <summary>
	/// The SHA-256 of its bytes after the patch; null while the patch isn't applied, or when the patch deletes it. For a file
	/// another edit got into while git wrote the patch (<see cref="MutationJournal.MarkApplied"/>), the bytes the patch alone
	/// gives it (<see cref="PlannedHash"/>), so its bytes, the edit's and the mutation's, are never taken for the mutation's.
	/// </summary>
	public string? MutatedHash { get; set; }

	/// <summary>The copy of its bytes before the patch, in the journal's folder (<c>&lt;n&gt;.&lt;file name&gt;.orig</c>); null when it didn't exist.</summary>
	public string? Backup { get; set; }
}

/// <summary>Where a mutation run stands, as its journal records it.</summary>
public enum MutationPhase
{
	/// <summary>The files are backed up and the patch is being applied: they may be partly changed.</summary>
	Applying,

	/// <summary>The patch is applied: the files hold the mutation.</summary>
	Applied,

	/// <summary>The files are restored, but the rebuild after has not finished: the binaries may still hold the mutation.</summary>
	Restored,
}

/// <summary>How <see cref="MutationJournal.Restore"/> treats a file that holds neither its original bytes nor the mutation's.</summary>
public enum MutationRestoreMode
{
	/// <summary>Leaves it as it is, and reports it: an edit made since the patch was applied is never overwritten.</summary>
	Normal,

	/// <summary>Puts the original back over it, after saving its current bytes beside the journal.</summary>
	Force,

	/// <summary>
	/// Keeps the later edit in it (one the agent wants) as a check decides (<see cref="MutationAcceptCheck"/>: uak mutate merges
	/// the mutant, the file and the original three ways, and takes the mutation out only where it is sure the edit changed
	/// other lines and holds none of the mutated ones), and leaves it as a conflict where the check isn't; a file that still
	/// holds the mutation is put back. A file of a run that stopped while it wrote the patch can't be kept: it may hold part
	/// of the patch.
	/// </summary>
	Accept,
}

/// <summary>What <see cref="MutationRestoreMode.Accept"/> does with a file that holds neither its original bytes nor the mutation's.</summary>
public enum MutationAcceptAction
{
	/// <summary>Keeps it as it is: the mutation isn't in it.</summary>
	Keep,

	/// <summary>Writes <see cref="MutationAcceptCheck.Bytes"/>: the later edit, with the mutation taken out of it.</summary>
	Undo,

	/// <summary>Leaves it, as a conflict: the mutation may still be in it, and uak can't tell the edit from it.</summary>
	Refuse,
}

/// <summary>
/// The verdict on a file -accept would keep (an edit made over the mutant may still hold the mutation): what to do, the bytes
/// to write for <see cref="MutationAcceptAction.Undo"/>, and why, for the report.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="Bytes">For <see cref="MutationAcceptAction.Undo"/>, the file's new bytes; else null.</param>
/// <param name="Why">Why, in a few words.</param>
public sealed record MutationAcceptCheck(MutationAcceptAction Action, byte[]? Bytes, string Why);

/// <summary>The record <c>&lt;State&gt;/Mutations/&lt;name&gt;/journal.json</c> holds.</summary>
public sealed class MutationRecord
{
	public string Name { get; set; } = "";
	public string Patch { get; set; } = "";
	public int Pid { get; set; }
	public DateTime? ProcessStart { get; set; }
	public DateTime Started { get; set; }
	public MutationPhase Phase { get; set; }
	public List<MutationFile> Files { get; set; } = [];

	/// <summary>The folders that didn't exist before the patch created a file in them, deepest first: removed again, when empty.</summary>
	public List<string> CreatedDirectories { get; set; } = [];

	/// <summary>
	/// The copy of the patch in the journal's folder (<see cref="MutationJournal.PatchCopyName"/>), for -accept to tell
	/// whether the mutation is still in a file edited since; null when none was kept.
	/// </summary>
	public string? PatchCopy { get; set; }

	/// <summary>The SHA-256 of <see cref="PatchCopy"/>'s bytes.</summary>
	public string? PatchHash { get; set; }

	/// <summary>
	/// The UBT target the mutant was built as (the project's editor target), with <see cref="BuildPlatform"/> and
	/// <see cref="BuildConfiguration"/>: only a later <c>uak build</c> of the same shows what became of the mutant build's
	/// objects (<see cref="AwaitingRecompile"/>). Null when not recorded (a journal of 0.4.0, until a <c>-restore</c> records it).
	/// </summary>
	public string? BuildTarget { get; set; }

	/// <summary>The platform of <see cref="BuildTarget"/>'s build ("Win64").</summary>
	public string? BuildPlatform { get; set; }

	/// <summary>The configuration of <see cref="BuildTarget"/>'s build ("Development").</summary>
	public string? BuildConfiguration { get; set; }

	/// <summary>
	/// When the files, once back, were given a write time that is newer, by more than UBT's slop, than anything the mutant
	/// build wrote (<see cref="MutationJournal.StampForRecompile"/>), so the next build recompiles them and what includes
	/// them; null while they weren't. Only a journal in phase <see cref="MutationPhase.Restored"/> has one.
	/// </summary>
	public DateTime? RecompileStamp { get; set; }

	/// <summary>
	/// The patched files a build must still be seen recompiling after the stamp: the evidence that the stamp worked, without
	/// which a build that succeeds proves nothing (it may be up to date, with the mutant's objects). Empty when none is
	/// (<see cref="MutationJournal.HasRecompiled"/>): nothing a mutant build could compile was left, or every later build of
	/// <see cref="BuildTarget"/> together showed each one (a build that failed elsewhere may show some, and the next one the
	/// rest). A file the mutant build shows no compile of is left out: no binary can hold it, and no build may ever compile
	/// it. Null when not known yet, or a mutant build may have stopped part way, when the stamp makes it all of them.
	/// </summary>
	public List<string>? AwaitingRecompile { get; set; }

	/// <summary>
	/// The binaries the mutant build wrote (in the folders of its target's build products: changed or new since just before
	/// it), as they were once the files were back: while one of them isn't newer than the stamp, it may hold the mutation.
	/// Empty when it wrote none; null when unknown (no list of the target's binaries was found, the patch adds or deletes a
	/// file or changes what the build is made of, the mutant build linked a file not among them, or the run stopped before
	/// its files were back), when only a build that recompiles the files clears the journal.
	/// </summary>
	public List<MutationBinary>? Binaries { get; set; }
}

/// <summary>A binary the mutant build wrote, as it was once the files were back.</summary>
public sealed class MutationBinary
{
	/// <summary>The file's full path.</summary>
	public string Path { get; set; } = "";

	/// <summary>Its last write time (UTC).</summary>
	public DateTime LastWriteTimeUtc { get; set; }

	/// <summary>Its length in bytes.</summary>
	public long Length { get; set; }

	/// <summary>The binary as it is now; null when it doesn't exist (or can't be read).</summary>
	public static MutationBinary? Read(string path)
	{
		try
		{
			FileInfo file = new(path);
			return file.Exists ? new MutationBinary { Path = file.FullName, LastWriteTimeUtc = file.LastWriteTimeUtc, Length = file.Length } : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null;
		}
	}

	/// <summary>
	/// Whether <paramref name="other"/> is the same file as written then: the same length, and the same write time to the
	/// millisecond, as far as a journal keeps times (<see cref="UakJson.FormatTime"/>). A file rewritten within the same
	/// millisecond with the same length counts as the same: the safe way.
	/// </summary>
	public bool SameAs(MutationBinary? other) => other is not null && other.Length == Length && ToMilliseconds(other.LastWriteTimeUtc) == ToMilliseconds(LastWriteTimeUtc);

	static long ToMilliseconds(DateTime time) => (time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time).Ticks / TimeSpan.TicksPerMillisecond;
}

/// <summary>A folder under <c>&lt;State&gt;/Mutations</c> that holds a journal or copies uak can't read.</summary>
/// <param name="Directory">The folder.</param>
/// <param name="Name">Its name: the run's -name=.</param>
/// <param name="Problem">What is wrong with it.</param>
public sealed record UnreadableMutationJournal(string Directory, string Name, string Problem);

/// <summary>What <see cref="MutationJournal.Restore"/> did.</summary>
/// <param name="Conflicts">The files it left as they are, and why: the journal is not restored.</param>
/// <param name="Saved">What it saved or kept that is neither the original nor the mutation (-force, -accept).</param>
/// <param name="StoppedWhileApplying">
/// Whether a conflict is a file of a run that stopped while it wrote the patch: it may hold part of the patch, so -accept
/// can't keep it, and only -force puts it back.
/// </param>
/// <param name="BecameLink">
/// Whether a conflict is a file that became a link since the patch was applied (another hard link to it, or a symbolic link
/// in its place): uak never writes a link, in any mode, so it stays until the link goes.
/// </param>
public sealed record MutationRestore(List<string> Conflicts, List<string> Saved, bool StoppedWhileApplying = false, bool BecameLink = false);

/// <summary>
/// The journal of a mutation run (<c>uak mutate</c>), so a mutation is never left in the tree unnoticed: before the patch
/// is applied, each file it touches is copied to <c>&lt;State&gt;/Mutations/&lt;name&gt;/</c> and listed with its hash, flushed
/// to the disk, and so are the hashes the patch is to give each file. The run puts the files back from there (the reverse of
/// the patch, byte for byte) and deletes the journal once it has rebuilt. If uak is killed first, the journal stays:
/// <c>uak build</c>, <c>uak test</c>, <c>uak lock run</c> and <c>uak compile</c> then refuse to run on that tree, <c>uak vcs
/// shelve</c> and <c>uak horde preflight -shelve</c> refuse to ship it, and <c>uak mutate -restore</c> finishes the job. A file
/// is put back only while it still holds the mutation's bytes (or, for a run stopped while it wrote the patch, the bytes the
/// patch was to give it), unless asked (-force, which saves what it overwrites first; -accept, which keeps an edit made
/// since, merged with the original so the mutation's lines are as they were, and refuses a file where it can't be sure the
/// mutation is out). A file of the tree is written whole (<see cref="WriteFile"/>): a temporary file beside it, renamed
/// over it; a link is never written.
/// <para>
/// A journal is written whole in <see cref="MutationFolders.GetPendingRoot"/> and renamed into place, and renamed out again
/// before it is deleted, so a reader without the lock (<c>uak compile</c>) never sees half of one; one that can't be renamed
/// stays whole.
/// </para>
/// <para>
/// Every reader that holds the editor lock treats a journal as its run's own business only when that run is the holder of
/// the hold the reader joined (its ID and start time both read and matched). Any other journal is a stopped run's, since a
/// live run holds the lock; and a folder that holds a journal or copies uak can't read is refused too.
/// </para>
/// </summary>
public sealed class MutationJournal
{
	private const string RecordName = MutationFolders.RecordName;
	private const string BackupExtension = MutationFolders.BackupExtension;

	/// <summary>The name of the patch's copy in a journal's folder.</summary>
	public const string PatchCopyName = "patch.diff";

	/// <summary>How long a reader without the lock looks again at a folder it can't read before it refuses: a journal being saved.</summary>
	public static readonly TimeSpan DefaultSettleTime = TimeSpan.FromSeconds(1);

	/// <summary>
	/// How long clearing a journal (renaming its folder away) is retried while a reader has a file in it open: Windows renames
	/// no folder that holds an open file.
	/// </summary>
	public static readonly TimeSpan ClearRetryTime = TimeSpan.FromSeconds(2);

	private MutationJournal(string directory, MutationRecord record)
	{
		Directory = directory;
		Record = record;
	}

	/// <summary>The journal's folder.</summary>
	public string Directory { get; }

	/// <summary>What it records.</summary>
	public MutationRecord Record { get; }

	/// <summary>&lt;State&gt;/Mutations.</summary>
	public static string GetRoot(UakContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		return MutationFolders.GetRoot(context.StateDirectory);
	}

	/// <summary>&lt;State&gt;/DiscardedMutations: journals set aside by <c>uak mutate -restore -force</c> or <c>-accept</c>, with their copies.</summary>
	public static string GetDiscardedRoot(UakContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		return Path.Combine(context.StateDirectory.FullName, "DiscardedMutations");
	}

	/// <summary>Every journal left in the state folder, by name, and every folder whose journal or copies can't be read.</summary>
	public static (List<MutationJournal> Journals, List<UnreadableMutationJournal> Unreadable) List(UakContext context)
	{
		string root = GetRoot(context);
		List<MutationJournal> journals = [];
		List<UnreadableMutationJournal> unreadable = [];
		if (!System.IO.Directory.Exists(root))
		{
			return (journals, unreadable);
		}
		foreach (string folder in System.IO.Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
		{
			string name = Path.GetFileName(folder);
			string? problem = Read(folder, out MutationRecord? record);
			if (problem is not null)
			{
				unreadable.Add(new UnreadableMutationJournal(folder, name, problem));
			}
			else if (record is not null)
			{
				journals.Add(new MutationJournal(folder, record));
			}
		}
		return (journals, unreadable);
	}

	/// <summary>
	/// Reads a journal's folder: null with the record (or with none, for a folder that holds neither a journal nor copies:
	/// a run killed before it backed anything up, which changed nothing), or why it can't be trusted.
	/// </summary>
	private static string? Read(string folder, out MutationRecord? record)
	{
		record = null;
		string file = Path.Combine(folder, RecordName);
		bool hasCopies;
		try
		{
			hasCopies = System.IO.Directory.EnumerateFiles(folder, "*" + BackupExtension).Any();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return $"its folder can't be listed: {exception.Message}";
		}
		if (!File.Exists(file))
		{
			return hasCopies ? $"it holds copies (*{BackupExtension}) but no {RecordName}" : null;
		}
		string? text = StateFiles.ReadShared(file);
		if (text is null)
		{
			return $"{RecordName} can't be opened";
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			return $"{RecordName} is empty";
		}
		try
		{
			record = UakJson.Deserialize<MutationRecord>(text);
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException)
		{
			return $"{RecordName} doesn't parse: {exception.Message}";
		}
		string? invalid = Validate(folder, record);
		if (invalid is not null)
		{
			record = null;
			return $"{RecordName} {invalid}";
		}
		return null;
	}

	/// <summary>Why a parsed record can't be trusted, or null.</summary>
	private static string? Validate(string folder, MutationRecord? record)
	{
		if (record is null)
		{
			return "is null";
		}
		if (!record.Name.Equals(Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase))
		{
			return $"names the run '{record.Name}', not its folder's name";
		}
		if (!Enum.IsDefined(record.Phase))
		{
			return $"has an unknown phase ({(int)record.Phase})";
		}
		if (record.Files is null || record.Files.Count == 0)
		{
			return "lists no files";
		}
		foreach (MutationFile entry in record.Files)
		{
			if (entry is null || string.IsNullOrEmpty(entry.Path) || !Path.IsPathFullyQualified(entry.Path))
			{
				return "lists a file without a full path";
			}
			if (entry.Existed != (entry.Backup is not null) || entry.Existed != (entry.OriginalHash is not null))
			{
				return $"lists {entry.Path} with a copy and hash that don't fit whether it existed";
			}
			// A Restored journal's files are back, and its copies are never read again: one whose copies are gone (a clear that
			// stopped part way) is still trusted, so a -restore can rebuild and clear it.
			if (entry.Backup is not null && (entry.Backup != Path.GetFileName(entry.Backup) || !entry.Backup.EndsWith(BackupExtension, StringComparison.Ordinal)
				|| (record.Phase != MutationPhase.Restored && !File.Exists(Path.Combine(folder, entry.Backup)))))
			{
				return $"names a copy of {entry.Path} ({entry.Backup}) that isn't in its folder";
			}
		}
		if (record.Binaries is not null && record.Binaries.Any(binary => binary is null || string.IsNullOrEmpty(binary.Path) || !Path.IsPathFullyQualified(binary.Path)))
		{
			return "lists a binary without a full path";
		}
		return null;
	}

	/// <summary>
	/// Whether a journal is the run of <paramref name="holder"/>, the holder of the hold this process joined, and that holder
	/// surely still runs: the same ID, both start times known and matching, and the process with that ID started then. An
	/// unknown start time never matches, so a stopped run's journal is never taken for a live one.
	/// </summary>
	public static bool IsRunOf(MutationRecord record, LockHolderInfo? holder)
	{
		ArgumentNullException.ThrowIfNull(record);
		return holder is not null && record.Pid == holder.Pid && record.ProcessStart is not null && holder.ProcessStart is not null
			&& ProcessIdentity.StartTimesMatch(record.ProcessStart, holder.ProcessStart) && new ProcessIdentity(record.Pid, record.ProcessStart).IsSurelyAlive();
	}

	/// <summary>
	/// For a command that holds the editor lock: refuses (a usage error) while a mutation journal is left, or a folder under
	/// <c>Mutations</c> can't be read, unless the journal is the run of the holder whose hold this process joined
	/// (<see cref="IsRunOf"/>). With <paramref name="building"/>, a journal whose files are restored is allowed: a build is
	/// what takes the mutation out of the binaries. A journal whose files are back and stamped for a recompile, and none of
	/// whose binaries may still hold the mutation (<see cref="StillHeldBinaries"/>), is spent: it is cleared
	/// (<see cref="ClearFolder"/>), and allowed.
	/// </summary>
	public static void ThrowIfUnfinished(UakContext context, bool building = false)
	{
		ThrowIfUnfinished(context, EditorLock.GetInheritedHolder(context), building);
	}

	/// <summary>As <see cref="ThrowIfUnfinished(UakContext, bool)"/>, with the inherited hold's holder given (for tests).</summary>
	public static void ThrowIfUnfinished(UakContext context, LockHolderInfo? inheritedHolder, bool building)
	{
		(List<MutationJournal> journals, List<UnreadableMutationJournal> unreadable) = List(context);
		if (unreadable.Count > 0)
		{
			throw new UakUsageException(DescribeUnreadable(unreadable[0]));
		}
		foreach (MutationJournal journal in journals)
		{
			if (IsRunOf(journal.Record, inheritedHolder))
			{
				continue;
			}
			if (journal.Record.Phase == MutationPhase.Restored && building)
			{
				continue;
			}
			if (journal.IsSpent())
			{
				// Under the lock, so no run of its own is mid-way: its files are back and no binary may hold its patch.
				TryClear(context, journal, "no binary the mutant build wrote is left as it wrote it");
				continue;
			}
			throw new UakUsageException(journal.DescribeStopped());
		}
	}

	/// <summary>
	/// Whether the journal's files are back (phase <see cref="MutationPhase.Restored"/>), stamped for a recompile, and none of
	/// the binaries its mutant build wrote may still hold the mutation (its list known, <see cref="StillHeldBinaries"/>):
	/// neither the tree nor the binaries can hold the patch, and the next build recompiles what the mutant build compiled.
	/// </summary>
	public bool IsSpent()
	{
		return Record.Phase == MutationPhase.Restored && Record.RecompileStamp is not null && StillHeldBinaries() is { Count: 0 };
	}

	/// <summary>
	/// The binaries the mutant build wrote that may still hold the mutation: those that exist and aren't newer than the stamp
	/// (<see cref="MutationRecord.RecompileStamp"/>); null when the journal doesn't know which it wrote
	/// (<see cref="MutationRecord.Binaries"/>), when any binary may. Only the write time is read, so any write after the stamp
	/// counts as taking the mutation out, as a link then does (UBT makes it from recompiled files), though a copy, a sync or a
	/// touch after the stamp would count too; a write time that merely changed to one before the stamp doesn't. A binary
	/// deleted holds none.
	/// </summary>
	public List<MutationBinary>? StillHeldBinaries()
	{
		return Record.Binaries is null ? null : [.. Record.Binaries.Where(binary => MutationBinary.Read(binary.Path) is MutationBinary now
			&& !(Record.RecompileStamp is DateTime stamp && now.LastWriteTimeUtc > stamp))];
	}

	/// <summary>
	/// Whether the files are stamped and builds since showed each patched file recompiled (<see cref="MutationRecord.AwaitingRecompile"/>):
	/// the objects of the mutant build are gone, so a build that succeeds has relinked them.
	/// </summary>
	public bool HasRecompiled() => Record.RecompileStamp is not null && Record.AwaitingRecompile is { Count: 0 };

	/// <summary>The patched files no build has been seen recompiling since the stamp, for a message.</summary>
	public string DescribeAwaitingRecompile() => Record.AwaitingRecompile is { Count: > 0 } awaiting ? string.Join(", ", awaiting) : DescribeFiles();

	/// <summary>
	/// After a build of <see cref="MutationRecord.BuildTarget"/> ran past the stamp: takes the files <paramref name="recompiled"/>
	/// says it compiled out of <see cref="MutationRecord.AwaitingRecompile"/>. It tells a file by its name in the build's log,
	/// so uak's own check (MutateCommand) gives it. With <paramref name="save"/> false the journal isn't saved: the caller
	/// saves it only if it stays (<see cref="SetAwaitingRecompile"/>).
	/// </summary>
	public void NoteRecompiled(Func<MutationFile, bool> recompiled, bool save = true)
	{
		ArgumentNullException.ThrowIfNull(recompiled);
		if (Record.RecompileStamp is null || Record.AwaitingRecompile is not { Count: > 0 } awaiting)
		{
			return;
		}
		if (awaiting.RemoveAll(path => Record.Files.Find(file => file.Path == path) is not MutationFile entry || recompiled(entry)) > 0 && save)
		{
			Save();
		}
	}

	/// <summary>Records which patched files the mutant build may have compiled (<see cref="MutationRecord.AwaitingRecompile"/>).</summary>
	public void SetAwaitingRecompile(List<string>? files)
	{
		Record.AwaitingRecompile = files;
		Save();
	}

	/// <summary>Records the binaries the mutant build wrote, as they are now that the files are back (<see cref="MutationRecord.Binaries"/>).</summary>
	public void SetBinaries(List<MutationBinary>? binaries)
	{
		Record.Binaries = binaries;
		Save();
	}

	/// <summary>Records the build the mutant was built with (<see cref="MutationRecord.BuildTarget"/>); saved with the next change.</summary>
	public void SetBuild(string target, string platform, string configuration)
	{
		Record.BuildTarget = target;
		Record.BuildPlatform = platform;
		Record.BuildConfiguration = configuration;
	}

	/// <summary>
	/// UBT's slop: a direct prerequisite of an action counts as newer than its output only when it is more than this newer
	/// (ActionGraph.cs, "allow a 1 second slop for network copies"; in Actions/ since UE 5.8, System/ before). A file from a
	/// dependency list, such as a header, or a source in a unity file, counts as soon as it is newer.
	/// </summary>
	public static readonly TimeSpan UbtTimestampSlop = TimeSpan.FromSeconds(1);

	/// <summary>
	/// Once the files are back: gives each of them that exists (but those in <paramref name="untouched"/>, which this process
	/// never wrote) the write time <paramref name="stamp"/> (<see cref="StampFiles"/>), so UBT counts each of them, and so
	/// every object that includes them, as newer than what the mutant build compiled, and recompiles them: written back
	/// within a second of a mutant object, a file would count as up to date, and the binaries keep the mutant. Records the
	/// stamp (<see cref="MutationRecord.RecompileStamp"/>) once every file took it, and that a build must now show each
	/// recompiled (<see cref="MutationRecord.AwaitingRecompile"/>, all the files when it isn't known which the mutant build
	/// compiled).
	/// </summary>
	/// <exception cref="IOException">A file's time can't be set, or didn't take: nothing is recorded, and the next restore tries again.</exception>
	public void StampForRecompile(DateTime stamp, DateTime reference, IReadOnlySet<string>? untouched = null, Action<string, DateTime>? setTime = null)
	{
		StampFiles(Record.Files.Select(file => file.Path).Where(path => untouched is null || !untouched.Contains(path)), stamp, reference, setTime);
		Record.RecompileStamp = stamp;
		Record.AwaitingRecompile ??= [.. Record.Files.Select(file => file.Path)];
		Save();
	}

	/// <summary>
	/// Gives each of <paramref name="paths"/> that exists the write time <paramref name="stamp"/>, and checks that it took:
	/// each must now be more than <see cref="UbtTimestampSlop"/> newer than <paramref name="reference"/>, a time no earlier
	/// than anything the mutant build wrote. Only the write time changes: a file's bytes and attributes (the read-only flag
	/// among them) stay. Where the time can't be set (a share or an ACL that allows writes and no change of attributes), the
	/// file's own bytes are written whole instead (<see cref="WriteFile"/>), which gives it the clock's time, past the stamp
	/// once the caller has waited; that needs no such right, unless the file is read-only, hidden or system, when it fails
	/// too. A probe a run killed before its stamp left beside a file (<see cref="ProbeFileTime"/>) is deleted on the way:
	/// the journal is past the restore that deletes such files. <paramref name="setTime"/> sets a time (for tests).
	/// </summary>
	/// <exception cref="IOException">A file's time can't be set, or didn't take.</exception>
	public static void StampFiles(IEnumerable<string> paths, DateTime stamp, DateTime reference, Action<string, DateTime>? setTime = null)
	{
		ArgumentNullException.ThrowIfNull(paths);
		setTime ??= File.SetLastWriteTimeUtc;
		List<string> failed = [];
		foreach (string path in paths)
		{
			DeleteLeftoverTemporaryFiles(path);
			if (!File.Exists(path))
			{
				continue;
			}
			try
			{
				try
				{
					setTime(path, stamp);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					WriteFile(path, File.ReadAllBytes(path));
				}
				DateTime written = File.GetLastWriteTimeUtc(path);
				if (written - reference <= UbtTimestampSlop)
				{
					failed.Add($"{path} (its write time is {UakJson.FormatTime(written)}, not more than {UbtTimestampSlop.TotalSeconds:0} s after {UakJson.FormatTime(reference)})");
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				failed.Add($"{path} ({exception.Message})");
			}
		}
		if (failed.Count > 0)
		{
			throw new IOException($"could not mark {string.Join(", ", failed)} for a recompile");
		}
	}

	/// <summary>
	/// The time of the file system a file is on: a temporary file written beside it, its write time read back, and deleted
	/// (named as <see cref="WriteFile"/>'s are, so a run killed here leaves one the next stamp deletes, <see cref="StampFiles"/>). A
	/// file server's clock may run ahead of this machine's, and it stamps what the build writes there. Null when it can't be told.
	/// </summary>
	public static DateTime? ProbeFileTime(string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		if (!System.IO.Directory.Exists(Path.GetDirectoryName(path)))
		{
			return null;
		}
		string probe = $"{path}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.WriteAllBytes(probe, []);
			return File.GetLastWriteTimeUtc(probe);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
		finally
		{
			StateFiles.TryDelete(probe);
		}
	}

	/// <summary>
	/// Clears a journal whose run is over: deletes its folder (<see cref="DeleteFolder"/>), or sets it aside
	/// (<see cref="SetAside"/>) when it holds bytes saved beside it (<c>&lt;n&gt;.&lt;time&gt;.current</c>, by -force or
	/// -accept), so nothing it saved is lost. Returns where it was set aside, or null when deleted.
	/// </summary>
	/// <exception cref="IOException">It can't be moved: it stays whole, as it was.</exception>
	public static string? ClearFolder(UakContext context, string folder, bool setAside = false)
	{
		ArgumentNullException.ThrowIfNull(context);
		if (!setAside && !System.IO.Directory.EnumerateFiles(folder, "*.current").Any())
		{
			DeleteFolder(folder);
			return null;
		}
		return SetAside(context, folder);
	}

	/// <summary>Clears a spent journal (<see cref="ClearFolder"/>), saying so; one that can't be cleared stays, with a warning.</summary>
	private static bool TryClear(UakContext context, MutationJournal journal, string why)
	{
		try
		{
			string? aside = ClearFolder(context, journal.Directory);
			context.Logger.LogInformation("Cleared the mutation journal '{Name}' ({Why}){Aside}.", journal.Record.Name, why, aside is null ? "" : $"; set aside in {aside}");
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("The mutation journal '{Name}' is spent ({Why}) but could not be cleared ({Message}): the next command under the lock tries again.",
				journal.Record.Name, why, exception.Message);
			return false;
		}
	}

	/// <summary>
	/// After a build, under the lock it held, for every stopped run's journal whose files are back and stamped for a recompile
	/// and whose mutant was built as this build's target, platform and configuration (a build given other UBT arguments, which
	/// may build part of it, doesn't count: don't call this for one): notes the files it recompiled (<see cref="NoteRecompiled"/>, by
	/// <paramref name="recompiled"/>; a build that failed elsewhere counts for those), and, when it
	/// <paramref name="succeeded"/> and every file has been seen recompiled (<see cref="HasRecompiled"/>), clears the journal:
	/// that build relinked what the mutant build linked. A build that succeeded without that evidence may be up to date with
	/// the mutant's objects, so the journal stays, with a warning. Any journal that is spent (<see cref="IsSpent"/>) goes too.
	/// A journal not stamped (of 0.4.0, or one whose stamp didn't take) needs <c>uak mutate -restore</c>, which stamps first.
	/// A build never waits for <see cref="StillHeldBinaries"/>: a binary the mutant build wrote may be one an unrelated change
	/// made it link, which no later build relinks, so no journal could ever clear. Returns the names cleared.
	/// </summary>
	public static List<string> ClearAfterBuild(UakContext context, string target, string platform, string configuration, bool succeeded, Func<MutationFile, bool> recompiled)
	{
		ArgumentNullException.ThrowIfNull(context);
		LockHolderInfo? holder = EditorLock.GetInheritedHolder(context);
		(List<MutationJournal> journals, _) = List(context);
		List<string> cleared = [];
		foreach (MutationJournal journal in journals)
		{
			MutationRecord record = journal.Record;
			if (IsRunOf(record, holder) || record.Phase != MutationPhase.Restored || record.RecompileStamp is null)
			{
				continue;
			}
			bool sameBuild = target.Equals(record.BuildTarget, StringComparison.OrdinalIgnoreCase)
				&& platform.Equals(record.BuildPlatform, StringComparison.OrdinalIgnoreCase) && configuration.Equals(record.BuildConfiguration, StringComparison.OrdinalIgnoreCase);
			if (sameBuild)
			{
				journal.NoteRecompiled(recompiled);
			}
			bool rebuilt = sameBuild && succeeded && journal.HasRecompiled();
			if (rebuilt || journal.IsSpent())
			{
				if (TryClear(context, journal, rebuilt ? $"this build of {target} recompiled what its mutant build compiled" : "no binary the mutant build wrote is left as it wrote it"))
				{
					cleared.Add(record.Name);
				}
			}
			else if (sameBuild && succeeded)
			{
				context.Logger.LogWarning("The mutation journal '{Name}' stays: this build shows no compile of {Files}, so {Which} may still hold its patch. `uak mutate -restore -name={Name}`"
					+ " marks them again and rebuilds; if no build compiles them, `-restore -name={Name} -force` sets the journal aside.", record.Name, journal.DescribeAwaitingRecompile(),
					journal.DescribeHeld(), record.Name, record.Name);
			}
		}
		return cleared;
	}

	/// <summary>
	/// For <c>uak compile</c>, which takes no lock: refuses (a usage error) while a stopped run's journal whose files aren't
	/// back is left (one stopped after it put its files back, before its rebuild, is allowed: the compile reads the files), or
	/// a folder can't be read, naming the files; returns a warning for each run that surely still runs, whose patch is in the
	/// tree while it builds and tests. A folder that can't be read is looked at again for up to <see cref="DefaultSettleTime"/>
	/// first: a live run may be saving its journal.
	/// </summary>
	public static List<string> CheckForCompile(UakContext context)
	{
		return CheckForCompile(context, DefaultSettleTime);
	}

	/// <summary>
	/// As <see cref="List"/>, for a reader without the lock: while a folder can't be read, looks again every 50 ms for up to
	/// <paramref name="settle"/> (a live run saving its journal), and returns what it last saw.
	/// </summary>
	public static (List<MutationJournal> Journals, List<UnreadableMutationJournal> Unreadable) ListSettled(UakContext context, TimeSpan settle)
	{
		DateTime deadline = DateTime.UtcNow + settle;
		(List<MutationJournal> journals, List<UnreadableMutationJournal> unreadable) = List(context);
		while (unreadable.Count > 0 && DateTime.UtcNow < deadline)
		{
			Thread.Sleep(50);
			(journals, unreadable) = List(context);
		}
		return (journals, unreadable);
	}

	/// <summary>As <see cref="CheckForCompile(UakContext)"/>, with how long to look again at a folder that can't be read (for tests).</summary>
	public static List<string> CheckForCompile(UakContext context, TimeSpan settle)
	{
		(List<MutationJournal> journals, List<UnreadableMutationJournal> unreadable) = ListSettled(context, settle);
		if (unreadable.Count > 0)
		{
			throw new UakUsageException(DescribeUnreadable(unreadable[0]));
		}
		List<string> warnings = [];
		foreach (MutationJournal journal in journals)
		{
			if (new ProcessIdentity(journal.Record.Pid, journal.Record.ProcessStart).IsSurelyAlive())
			{
				warnings.Add($"the mutation run '{journal.Record.Name}' (PID {journal.Record.Pid}) has its patch in {journal.DescribeFiles()} while it builds and tests:"
					+ " this compile sees the mutant in those files");
			}
			else if (journal.Record.Phase != MutationPhase.Restored)
			{
				throw new UakUsageException(journal.DescribeStopped());
			}
		}
		return warnings;
	}

	/// <summary>Why a command refuses to run while this journal, of a stopped run, is left.</summary>
	public string DescribeStopped()
	{
		string run = $"run `uak mutate -restore -name={Record.Name}` on its own (not inside `uak lock run`: it takes the editor lock itself)";
		if (Record.Phase == MutationPhase.Restored && Record.RecompileStamp is not null)
		{
			string build = Record.BuildTarget is null ? "" : $" of {Record.BuildTarget} ({Record.BuildPlatform} {Record.BuildConfiguration})";
			string clears = HasRecompiled() ? $"the next `uak build`{build} that succeeds clears this (once the tree builds again), as does {run}"
				: $"a `uak build`{build} that shows a compile of {DescribeAwaitingRecompile()} and succeeds clears this, as does {run}, which marks them again; if no build"
					+ $" compiles them, `uak mutate -restore -name={Record.Name} -force` sets the journal aside";
			return $"the mutation run '{Record.Name}' put its files back (the tree holds no mutation) and marked them so the next build recompiles them, but"
				+ $" no build since has recompiled them and succeeded, so {DescribeHeld()} may still hold its patch ({Record.Patch}): {clears}";
		}
		if (Record.Phase == MutationPhase.Restored)
		{
			return $"the mutation run '{Record.Name}' put its files back, but its journal was not cleared (it stopped before its rebuild, or a reader"
				+ $" held a file of the journal open when it cleared it), so the binaries may still hold its patch ({Record.Patch}): {run}, which rebuilds and clears it";
		}
		return $"the mutation run '{Record.Name}' stopped with its patch ({Record.Patch}) still in {DescribeFiles()}:"
			+ $" {run} to put them back (the originals are in {Directory})";
	}

	/// <summary>Why a command refuses to run while this folder is left.</summary>
	public static string DescribeUnreadable(UnreadableMutationJournal folder)
	{
		ArgumentNullException.ThrowIfNull(folder);
		return $"the mutation journal in {folder.Directory} can't be trusted ({folder.Problem}), so uak can't tell whether a patch is still in the tree:"
			+ $" check the files that mutation run touched by hand (each *{BackupExtension} file there is the original of the file it is named after,"
			+ $" <n>.<file name>{BackupExtension}). Give each file you put back a new write time (save it in an editor: a copy keeps the old time of its"
			+ $" {BackupExtension}, older than what the mutant build compiled, so a build keeps the mutant). Then run `uak mutate -restore -name={folder.Name} -accept`"
			+ " on its own (it takes the editor lock itself), which keeps the tree as it is, gives the files it can still name a new write time, rebuilds, and sets"
			+ " the folder aside once the rebuild compiled something (it refuses a rebuild that was up to date)";
	}

	private string DescribeFiles() => string.Join(", ", Record.Files.Select(file => file.Path));

	/// <summary>The binaries that may still hold the mutation (<see cref="StillHeldBinaries"/>), for a message.</summary>
	public string DescribeHeld()
	{
		List<MutationBinary>? held = StillHeldBinaries();
		return held is null ? "the binaries its mutant build wrote (uak doesn't know which)" : string.Join(", ", held.Select(binary => binary.Path));
	}

	/// <summary>
	/// Starts a journal: copies each file's bytes and records them (phase <see cref="MutationPhase.Applying"/>), and a copy of
	/// the patch (<paramref name="patchBytes"/>, for -accept), all flushed to the disk, before anything is changed. The journal
	/// is written in <see cref="MutationFolders.GetPendingRoot"/> and renamed into place whole. Returns the journal; the bytes
	/// it copied are each file's <see cref="ReadOriginal"/>.
	/// </summary>
	/// <exception cref="UakUsageException">
	/// The name can't name a folder as it is, a journal or copies of that name are left from an earlier run, a folder of that
	/// name that holds neither can't be deleted, or a file can't be read or copied (then nothing is left behind: the tree is
	/// untouched).
	/// </exception>
	public static MutationJournal Begin(UakContext context, string name, string patch, IReadOnlyList<string> files, byte[]? patchBytes = null)
	{
		ArgumentNullException.ThrowIfNull(files);
		// The folder must keep the name exactly: Windows would drop a trailing '.', and the journal would no longer match its folder.
		RunSummaries.CheckName(name);
		string root = GetRoot(context);
		string folder = Path.Combine(root, name);
		if (System.IO.Directory.Exists(folder))
		{
			if (MutationFolders.HoldsJournal(folder))
			{
				throw new UakUsageException($"a mutation journal named '{name}' is left in {folder}: run `uak mutate -restore -name={name}` first");
			}
			try
			{
				// Left by a run killed before it copied anything: it changed nothing.
				System.IO.Directory.Delete(folder, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				throw new UakUsageException($"{folder}, left by an earlier run with no journal in it, can't be deleted ({exception.Message}): nothing was changed;"
					+ " delete it, or give another -name=", exception);
			}
		}
		string pending = Path.Combine(MutationFolders.GetPendingRoot(context.StateDirectory), $"{name}.{Environment.ProcessId}.{Guid.NewGuid():N}");
		ProcessIdentity self = ProcessIdentity.Current;
		MutationRecord record = new()
		{
			Name = name,
			Patch = patch,
			Pid = self.Pid,
			ProcessStart = self.StartTimeUtc,
			Started = DateTime.UtcNow,
			Phase = MutationPhase.Applying,
		};
		try
		{
			try
			{
				System.IO.Directory.CreateDirectory(pending);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				throw new UakUsageException($"the mutation journal's folder {pending} could not be made ({exception.Message}): nothing was changed", exception);
			}
			for (int index = 0; index < files.Count; index++)
			{
				string path = files[index];
				MutationFile entry = new() { Path = path, Existed = File.Exists(path) };
				if (entry.Existed)
				{
					try
					{
						byte[] bytes = File.ReadAllBytes(path);
						entry.ReadOnly = File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
						entry.OriginalHash = Hash(bytes);
						entry.Backup = $"{index.ToString(CultureInfo.InvariantCulture)}.{Path.GetFileName(path)}{BackupExtension}";
						StateFiles.WriteBytes(Path.Combine(pending, entry.Backup), bytes, durable: true);
					}
					catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
					{
						throw new UakUsageException($"{path} could not be copied before the patch ({exception.Message}): nothing was changed", exception);
					}
				}
				else
				{
					AddCreatedDirectories(record.CreatedDirectories, Path.GetDirectoryName(path));
				}
				record.Files.Add(entry);
			}
			if (patchBytes is not null)
			{
				try
				{
					StateFiles.WriteBytes(Path.Combine(pending, PatchCopyName), patchBytes, durable: true);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					throw new UakUsageException($"the patch could not be copied to the journal ({exception.Message}): nothing was changed", exception);
				}
				record.PatchCopy = PatchCopyName;
				record.PatchHash = Hash(patchBytes);
			}
			try
			{
				new MutationJournal(pending, record).Save();
				System.IO.Directory.CreateDirectory(root);
				System.IO.Directory.Move(pending, folder);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				throw new UakUsageException($"the mutation journal could not be put in {folder} ({exception.Message}): nothing was changed", exception);
			}
		}
		catch
		{
			// Nothing was written to the tree: the copies go.
			TryDeleteDirectory(pending);
			throw;
		}
		return new MutationJournal(folder, record);
	}

	/// <summary>Adds the folders above a new file that don't exist yet, deepest first.</summary>
	private static void AddCreatedDirectories(List<string> created, string? directory)
	{
		List<string> missing = [];
		for (string? current = directory; !string.IsNullOrEmpty(current) && !System.IO.Directory.Exists(current); current = Path.GetDirectoryName(current))
		{
			missing.Add(current);
		}
		foreach (string folder in missing)
		{
			if (!created.Contains(folder, StringComparer.OrdinalIgnoreCase))
			{
				created.Add(folder);
			}
		}
		created.Sort((a, b) => b.Length.CompareTo(a.Length));
	}

	/// <summary>A file's bytes before the patch, from its copy (null when it didn't exist), checked against the recorded hash.</summary>
	/// <exception cref="IOException">The copy no longer matches its hash.</exception>
	public byte[]? ReadOriginal(MutationFile entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		if (!entry.Existed)
		{
			return null;
		}
		string backup = Path.Combine(Directory, entry.Backup!);
		byte[] bytes = File.ReadAllBytes(backup);
		if (Hash(bytes) != entry.OriginalHash)
		{
			throw new IOException($"the copy {backup} of {entry.Path} doesn't match its recorded hash");
		}
		return bytes;
	}

	/// <summary>The bytes of the patch's copy, checked against the recorded hash.</summary>
	/// <exception cref="IOException">No copy was kept, or it can't be read, or it no longer matches its hash.</exception>
	public byte[] ReadPatchCopy()
	{
		if (Record.PatchCopy is null || Record.PatchHash is null || Record.PatchCopy != Path.GetFileName(Record.PatchCopy))
		{
			throw new IOException("the journal kept no copy of the patch");
		}
		string copy = Path.Combine(Directory, Record.PatchCopy);
		byte[] bytes = File.ReadAllBytes(copy);
		if (Hash(bytes) != Record.PatchHash)
		{
			throw new IOException($"the patch's copy {copy} doesn't match its recorded hash");
		}
		return bytes;
	}

	/// <summary>
	/// Saves a file's bytes beside the journal, flushed, as <c>&lt;n&gt;.&lt;UTC time&gt;.current</c>, never over bytes saved
	/// earlier (a second -force keeps the first one's); returns where.
	/// </summary>
	public string SaveCurrent(int index, byte[] bytes)
	{
		ArgumentNullException.ThrowIfNull(bytes);
		string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
		string prefix = $"{index.ToString(CultureInfo.InvariantCulture)}.{stamp}";
		string path = Path.Combine(Directory, prefix + ".current");
		for (int count = 2; File.Exists(path); count++)
		{
			path = Path.Combine(Directory, $"{prefix}-{count.ToString(CultureInfo.InvariantCulture)}.current");
		}
		StateFiles.WriteBytes(path, bytes, durable: true);
		return path;
	}

	/// <summary>
	/// Records, flushed to the disk before anything is written, the bytes the patch is to give each file: a byte array (its
	/// hash is kept), null to delete it; a file missing from <paramref name="planned"/> has bytes nobody predicted (git applies
	/// the patch). A run stopped while it wrote the patch can then be told apart from a later edit.
	/// </summary>
	public void SetPlanned(IReadOnlyDictionary<string, byte[]?> planned)
	{
		ArgumentNullException.ThrowIfNull(planned);
		foreach (MutationFile entry in Record.Files)
		{
			entry.PlannedHash = null;
			entry.PlannedDeleted = false;
			if (planned.TryGetValue(entry.Path, out byte[]? bytes))
			{
				entry.PlannedHash = bytes is null ? null : Hash(bytes);
				entry.PlannedDeleted = bytes is null;
			}
		}
		Save();
	}

	/// <summary>
	/// Refuses (a usage error) when a file no longer holds the bytes it was copied with (or a file the patch creates now
	/// exists): someone changed it after the copy was taken, and the patch would overwrite their change. Called just before
	/// the patch is applied.
	/// </summary>
	public void ThrowIfChangedSinceBegin()
	{
		foreach (MutationFile entry in Record.Files)
		{
			ThrowIfChangedSinceBegin(entry);
		}
	}

	/// <summary>As <see cref="ThrowIfChangedSinceBegin()"/>, for one file: called just before it is written.</summary>
	public static void ThrowIfChangedSinceBegin(MutationFile entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		string? current = File.Exists(entry.Path) ? Hash(File.ReadAllBytes(entry.Path)) : null;
		if (current != entry.OriginalHash)
		{
			throw new MutationFileChangedException(entry.Path);
		}
	}

	/// <summary>
	/// Records the patched files' hashes (phase <see cref="MutationPhase.Applied"/>), once the whole patch is written. Returns
	/// the files the patch left as they were (each mutated hash equal to its original). A write that failed or stopped part
	/// way leaves the journal in <see cref="MutationPhase.Applying"/>: then only the planned bytes count as the mutation's.
	/// The files in <paramref name="editedMeanwhile"/>, which another edit got into while git wrote the patch, are recorded
	/// with their planned hash instead, so their bytes are never put back without -accept or -force.
	/// </summary>
	public List<string> MarkApplied(IReadOnlySet<string>? editedMeanwhile = null)
	{
		List<string> unchanged = [];
		foreach (MutationFile entry in Record.Files)
		{
			entry.MutatedHash = editedMeanwhile is not null && editedMeanwhile.Contains(entry.Path) ? entry.PlannedHash
				: File.Exists(entry.Path) ? Hash(File.ReadAllBytes(entry.Path)) : null;
			if (entry.MutatedHash == entry.OriginalHash)
			{
				unchanged.Add(entry.Path);
			}
		}
		Record.Phase = MutationPhase.Applied;
		Save();
		return unchanged;
	}

	/// <summary>Whether a file holds bytes this run gave it: the patch as applied, or (before that is recorded) as planned.</summary>
	private bool HoldsMutation(MutationFile entry, bool exists, string? currentHash)
	{
		bool planned = entry.PlannedDeleted ? !exists : entry.PlannedHash is not null && currentHash == entry.PlannedHash;
		return planned || (Record.Phase == MutationPhase.Applied && currentHash == entry.MutatedHash);
	}

	/// <summary>
	/// Puts every file back as it was. A journal already <see cref="MutationPhase.Restored"/> has every file back, and its
	/// files are not checked again: an edit made since is someone's work. Otherwise a file that already is as it was is left
	/// alone, and one that holds the mutation's bytes (or, in phase <see cref="MutationPhase.Applying"/>, the bytes the patch
	/// was to give it) is put back (or deleted, or recreated), from a copy whose hash is checked first, in every mode. A file
	/// that holds neither is a conflict and is left as it is in <see cref="MutationRestoreMode.Normal"/>;
	/// <see cref="MutationRestoreMode.Force"/> saves its bytes beside the journal (<see cref="SaveCurrent"/>) and puts the
	/// original back; <see cref="MutationRestoreMode.Accept"/> asks <paramref name="checkAccept"/> (given the file's index, its
	/// entry and its bytes) whether the mutation is still in it: it keeps the file, writes the bytes the check gives (the later
	/// edit with the mutation taken out, after saving what it held), or leaves it as a conflict (as it does without a check);
	/// in phase Applying, where it may hold part of the patch, it is a conflict. A copy that doesn't match its hash is never
	/// written, in any mode, nor is a file that became a link since (<see cref="DescribeLink"/>): it is a conflict
	/// (<see cref="MutationRestore.BecameLink"/>) until the link goes. Each file is written whole (<see cref="WriteFile"/>), once what it held is saved (when it is
	/// saved) and only while it still holds the bytes it was judged by: one changed meanwhile is a conflict, never overwritten.
	/// The files in <paramref name="untouched"/> (which this process knows it never wrote) are left alone. With no conflicts,
	/// the folders the patch created are removed when empty, and the phase becomes Restored. <paramref name="beforeWrite"/> is
	/// called with a file's path just before it is looked at again and written (for tests: an edit that lands meanwhile).
	/// </summary>
	public MutationRestore Restore(MutationRestoreMode mode = MutationRestoreMode.Normal, IReadOnlySet<string>? untouched = null,
		Func<int, MutationFile, byte[]?, MutationAcceptCheck>? checkAccept = null, Action<string>? beforeWrite = null)
	{
		List<string> conflicts = [];
		List<string> saved = [];
		bool stoppedWhileApplying = false;
		bool becameLink = false;
		if (Record.Phase == MutationPhase.Restored)
		{
			return new MutationRestore(conflicts, saved);
		}
		for (int index = 0; index < Record.Files.Count; index++)
		{
			MutationFile entry = Record.Files[index];
			if (untouched is not null && untouched.Contains(entry.Path))
			{
				continue;
			}
			// A temporary file a killed write of this tree left beside it (WriteFile) is no one's.
			DeleteLeftoverTemporaryFiles(entry.Path);
			byte[]? current = File.Exists(entry.Path) ? File.ReadAllBytes(entry.Path) : null;
			string? currentHash = current is null ? null : Hash(current);
			if (currentHash == entry.OriginalHash)
			{
				SetReadOnly(entry);
				continue;
			}
			bool mutated = HoldsMutation(entry, current is not null, currentHash);
			string original = entry.Backup is null ? "" : $" (its original is {Path.Combine(Directory, entry.Backup)})";
			string changedAgain = $"{entry.Path}: changed again while it was being restored{original}: nothing was written to it; run -restore again";
			if (DescribeLink(entry.Path) is string link)
			{
				// Made a link since the patch was applied (uak mutate refuses one before): a whole write would part or replace it.
				conflicts.Add($"{entry.Path}: it became {link} after the patch was applied, and uak never writes a link{original}: remove the other link (or"
					+ " put a plain file back in its place), then run -restore again");
				becameLink = true;
				continue;
			}
			if (!mutated && Record.Phase == MutationPhase.Applying && mode != MutationRestoreMode.Force)
			{
				conflicts.Add($"{entry.Path}: the run stopped while it wrote the patch, so this file may hold part of it{original}: look at it;"
					+ " -force puts the original back, saving these bytes beside the journal");
				stoppedWhileApplying = true;
				continue;
			}
			if (!mutated && mode == MutationRestoreMode.Accept)
			{
				// An edit made over the mutant may have kept the mutation: the check says what of it can be kept.
				MutationAcceptCheck check = checkAccept?.Invoke(index, entry, current)
					?? new MutationAcceptCheck(MutationAcceptAction.Refuse, null, "uak can't check whether the mutation is still in it");
				if (check.Action == MutationAcceptAction.Keep)
				{
					saved.Add($"kept {entry.Path} as it is ({(current is null ? "deleted" : "a later edit")}: {check.Why})");
				}
				else if (check.Action == MutationAcceptAction.Undo && check.Bytes is not null && current is not null)
				{
					// Saved first, so what it held is never lost; written only while it still holds it.
					string before = SaveCurrent(index, current);
					beforeWrite?.Invoke(entry.Path);
					if (!Holds(entry.Path, currentHash))
					{
						conflicts.Add($"{changedAgain} (what it held before is saved in {before})");
						continue;
					}
					WriteFile(entry.Path, check.Bytes);
					saved.Add($"kept the later edit in {entry.Path} and took the mutation out of it ({check.Why}); what it held is saved in {before}");
				}
				else
				{
					conflicts.Add($"{entry.Path}: -accept can't keep it: {check.Why}{original}. Make the edit again by hand on the original, which"
						+ " -force puts back, saving these bytes beside the journal");
				}
				continue;
			}
			if (!mutated && mode != MutationRestoreMode.Force)
			{
				conflicts.Add($"{entry.Path}: changed since the mutation was applied{original}");
				continue;
			}
			byte[]? bytes;
			try
			{
				bytes = ReadOriginal(entry);
			}
			catch (IOException exception)
			{
				conflicts.Add($"{entry.Path}: not put back: {exception.Message}");
				continue;
			}
			if (!mutated && current is not null)
			{
				string keep = SaveCurrent(index, current);
				saved.Add($"saved the bytes -force overwrote in {entry.Path} to {keep}");
			}
			beforeWrite?.Invoke(entry.Path);
			if (!Holds(entry.Path, currentHash))
			{
				conflicts.Add(changedAgain);
				continue;
			}
			if (bytes is not null)
			{
				WriteFile(entry.Path, bytes);
				SetReadOnly(entry);
			}
			else if (current is not null)
			{
				File.SetAttributes(entry.Path, FileAttributes.Normal);
				File.Delete(entry.Path);
			}
		}
		if (conflicts.Count == 0)
		{
			RemoveCreatedDirectories();
			Record.Phase = MutationPhase.Restored;
			Save();
		}
		return new MutationRestore(conflicts, saved, stoppedWhileApplying, becameLink);
	}

	/// <summary>
	/// Writes a file of the tree whole: to a temporary file beside it (<c>&lt;file&gt;.&lt;pid&gt;.&lt;guid&gt;.tmp</c>), flushed to
	/// the disk, then renamed over it (<see cref="StateFiles.WriteBytes(string, byte[], bool)"/>), so a crash leaves it with its
	/// old bytes or the new ones, never part. Its attributes, the read-only flag among them, are kept (a new file gets the
	/// default ones). Creates its folder. A link is never written (<see cref="DescribeLink"/>): it throws.
	/// </summary>
	/// <exception cref="IOException">The file is a symbolic link or has other hard links, or the write failed.</exception>
	public static void WriteFile(string path, byte[] bytes)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(bytes);
		string? link = DescribeLink(path);
		if (link is not null)
		{
			throw new IOException($"{path} is {link}, so uak doesn't write it");
		}
		FileAttributes? attributes = File.Exists(path) ? File.GetAttributes(path) : null;
		bool hard = attributes is FileAttributes held && (held & HardAttributes) != 0;
		if (hard)
		{
			// Windows replaces no read-only file, and may refuse a hidden or system one. Only then: it takes the right to change
			// attributes, which a share may not give, and StampFiles comes here because it doesn't.
			File.SetAttributes(path, FileAttributes.Normal);
		}
		try
		{
			StateFiles.WriteBytes(path, bytes, durable: true);
		}
		finally
		{
			if (attributes is FileAttributes kept && File.Exists(path) && File.GetAttributes(path) != kept)
			{
				try
				{
					File.SetAttributes(path, kept);
				}
				catch (Exception exception) when (!hard && exception is IOException or UnauthorizedAccessException)
				{
					// An attribute such as "not indexed", which the bytes just written don't depend on.
				}
			}
		}
	}

	/// <summary>The attributes that keep <see cref="WriteFile"/> from replacing a file, or that must not be lost when it does.</summary>
	private const FileAttributes HardAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

	/// <summary>
	/// Why <see cref="WriteFile"/> can't write a file of the tree, or null: it is a symbolic link (or a junction), which a new
	/// file renamed over it would replace, so the tree would lose the link; or one of several hard links to one file, which it
	/// would part (the other names keeping the old bytes), clearing the read-only flag they share on the way. uak mutate
	/// refuses such a file before it writes anything.
	/// </summary>
	public static string? DescribeLink(string path)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		string? target;
		try
		{
			target = new FileInfo(path).LinkTarget;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			target = null;
		}
		if (target is not null)
		{
			return $"a symbolic link (to {target}): a new file renamed over it would replace the link";
		}
		int? links = File.Exists(path) ? CanonicalPath.TryGetLinkCount(path) : null;
		return links > 1 ? $"one of {links} hard links to one file: a new file renamed over it would part them" : null;
	}

	/// <summary>Deletes the temporary files a killed <see cref="WriteFile"/> left beside a file (<c>&lt;file&gt;.&lt;pid&gt;.&lt;guid&gt;.tmp</c>).</summary>
	private static void DeleteLeftoverTemporaryFiles(string path)
	{
		string? folder = Path.GetDirectoryName(path);
		string name = Path.GetFileName(path);
		if (folder is null || !System.IO.Directory.Exists(folder))
		{
			return;
		}
		try
		{
			foreach (string temporary in System.IO.Directory.EnumerateFiles(folder, name + ".*.tmp"))
			{
				string found = Path.GetFileName(temporary);
				if (found.Length < name.Length + 1 + ".tmp".Length || !found.StartsWith(name + ".", StringComparison.Ordinal)
					|| !found.EndsWith(".tmp", StringComparison.Ordinal))
				{
					continue;
				}
				string[] parts = found[(name.Length + 1)..^".tmp".Length].Split('.');
				if (parts.Length == 2 && parts[0].Length > 0 && parts[0].All(char.IsAsciiDigit) && parts[1].Length == 32 && parts[1].All(char.IsAsciiHexDigitLower))
				{
					StateFiles.TryDelete(temporary);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// What can't be listed stays: it is no part of the tree's sources.
		}
	}

	/// <summary>Removes the folders the patch created, deepest first, while they are empty.</summary>
	private void RemoveCreatedDirectories()
	{
		foreach (string folder in Record.CreatedDirectories)
		{
			try
			{
				if (System.IO.Directory.Exists(folder) && !System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
				{
					System.IO.Directory.Delete(folder);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Something was put in it meanwhile, or it is in use: it stays.
			}
		}
	}

	/// <summary>Deletes the journal and its copies: the run is over (<see cref="DeleteFolder"/>).</summary>
	public void Delete()
	{
		DeleteFolder(Directory);
	}

	/// <summary>
	/// Deletes a journal's folder: first renamed out of <c>Mutations</c> into <c>MutationsPending</c>, which nothing reads
	/// (retried for <see cref="ClearRetryTime"/> while a reader has a file in it open), so no reader sees part of it, then
	/// deleted there as far as it can be. It is never deleted in place: a delete that stopped part way would leave a journal
	/// without its copies, which nobody could trust.
	/// </summary>
	/// <exception cref="IOException">It can't be renamed: it stays whole, as it was.</exception>
	public static void DeleteFolder(string folder)
	{
		ArgumentException.ThrowIfNullOrEmpty(folder);
		string state = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(folder))!)!;
		string away = Path.Combine(MutationFolders.GetPendingRoot(new DirectoryInfo(state)), $"{Path.GetFileName(folder)}.deleted.{Guid.NewGuid():N}");
		System.IO.Directory.CreateDirectory(Path.GetDirectoryName(away)!);
		MoveFolder(folder, away);
		TryDeleteDirectory(away);
	}

	/// <summary>Renames a folder, retried for <see cref="ClearRetryTime"/> while a reader has a file in it open; throws when it still can't.</summary>
	private static void MoveFolder(string folder, string target)
	{
		DateTime deadline = DateTime.UtcNow + ClearRetryTime;
		while (true)
		{
			try
			{
				System.IO.Directory.Move(folder, target);
				return;
			}
			catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && DateTime.UtcNow < deadline)
			{
				Thread.Sleep(50);
			}
		}
	}

	/// <summary>Deletes what is left in <c>MutationsPending</c> (journals a killed run was writing or deleting), as far as it can.</summary>
	public static void CleanPending(UakContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		string pending = MutationFolders.GetPendingRoot(context.StateDirectory);
		if (!System.IO.Directory.Exists(pending))
		{
			return;
		}
		foreach (string folder in System.IO.Directory.GetDirectories(pending))
		{
			TryDeleteDirectory(folder);
		}
	}

	private static void TryDeleteDirectory(string folder)
	{
		try
		{
			System.IO.Directory.Delete(folder, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Nothing reads it: it stays until the next clean-up.
		}
	}

	/// <summary>
	/// Moves a journal's folder, with its copies and any bytes -force saved, to <see cref="GetDiscardedRoot"/> as
	/// <c>&lt;name&gt;-&lt;UTC time&gt;</c> (retried as <see cref="DeleteFolder"/> is), and returns where: the journal is cleared,
	/// and nothing it held is lost.
	/// </summary>
	/// <exception cref="IOException">It can't be moved: it stays whole, as it was.</exception>
	public static string SetAside(UakContext context, string directory)
	{
		string target = Path.Combine(GetDiscardedRoot(context), $"{Path.GetFileName(directory)}-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}");
		System.IO.Directory.CreateDirectory(GetDiscardedRoot(context));
		MoveFolder(directory, target);
		return target;
	}

	/// <summary>Whether a file still holds the bytes with this hash (null: it doesn't exist).</summary>
	private static bool Holds(string path, string? hash)
	{
		return (File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null) == hash;
	}

	private void Save()
	{
		StateFiles.WriteJson(Path.Combine(Directory, RecordName), Record, durable: true);
	}

	private static void SetReadOnly(MutationFile entry)
	{
		if (entry.ReadOnly && File.Exists(entry.Path))
		{
			File.SetAttributes(entry.Path, File.GetAttributes(entry.Path) | FileAttributes.ReadOnly);
		}
	}

	/// <summary>Clears a file's read-only flag (a Perforce file that isn't opened), so the patch can write it.</summary>
	public static void MakeWritable(string path)
	{
		if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
		{
			File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
		}
	}

	/// <summary>The lowercase hex SHA-256 of some bytes.</summary>
	public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>A file changed after the journal copied it, before the patch was written to it: the patch is not applied to it.</summary>
public sealed class MutationFileChangedException : UakUsageException
{
	/// <summary>Creates the exception for <paramref name="path"/>.</summary>
	public MutationFileChangedException(string path)
		: base($"{path} changed while the patch was being prepared or applied: it was not written")
	{
		Path = path;
	}

	/// <summary>The file that changed.</summary>
	public string Path { get; }
}
