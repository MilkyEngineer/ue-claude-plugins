// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using AgentKit.Core;
using AgentKit.Locking;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>What <see cref="BuildEdits.StampAsync"/> did: the files it stamped, and the ones it couldn't.</summary>
/// <param name="Stamped">The files that changed while UBT ran, now marked for a recompile.</param>
/// <param name="NotMarked">The files that changed while UBT ran and could not be marked: nothing makes the next build recompile them.</param>
/// <param name="Problems">A line for each file of <paramref name="NotMarked"/>.</param>
internal sealed record BuildEditReport(IReadOnlyList<string> Stamped, IReadOnlyList<string> NotMarked, IReadOnlyList<string> Problems)
{
	/// <summary>A build no file changed during.</summary>
	public static BuildEditReport None { get; } = new([], [], []);

	/// <summary>What the marker's entries behind this report ask for (<see cref="RebuildNeededFile.DescribeWayOut"/>); null when it isn't the marker's.</summary>
	public string? WayOut { get; init; }

	/// <summary>
	/// For each file of <see cref="NotMarked"/> whose time could be read after its mark failed, the later of that time and the
	/// build's end (<see cref="RebuildNeededEntry.NotMarkedWritten"/>).
	/// </summary>
	public IReadOnlyDictionary<string, DateTime> Written { get; init; } = new Dictionary<string, DateTime>();

	/// <summary>Every file that changed while UBT ran, marked or not: the build's binaries may mix old and new text of each.</summary>
	public IReadOnlyList<string> Overlapped => [.. Stamped, .. NotMarked];

	/// <summary>
	/// This report and that of a <paramref name="later"/> build. For a file both name the later one wins: a stamp only counts
	/// against what was built before it, so a file the later build couldn't mark is unmarked, whatever an earlier stamp did.
	/// </summary>
	public BuildEditReport Plus(BuildEditReport later)
	{
		bool Superseded(string File) => later.Stamped.Contains(File, RebuildNeededFile.PathComparer) || later.NotMarked.Contains(File, RebuildNeededFile.PathComparer);
		List<(string File, string Problem)> Left = [.. NotMarked.Zip(Problems).Where(Pair => !Superseded(Pair.First))];
		Dictionary<string, DateTime> Times = new(RebuildNeededFile.PathComparer);
		foreach ((string File, DateTime Time) in Written.Where(Pair => Left.Exists(Kept => RebuildNeededFile.PathComparer.Equals(Kept.File, Pair.Key))).Concat(later.Written))
		{
			Times[File] = Time;
		}
		return new BuildEditReport([.. Stamped.Where(File => !Superseded(File)), .. later.Stamped], [.. Left.Select(Pair => Pair.File), .. later.NotMarked], [.. Left.Select(Pair => Pair.Problem), .. later.Problems])
		{
			WayOut = WayOut ?? later.WayOut,
			Written = Times,
		};
	}

	/// <summary>The warnings to print, for a build called <paramref name="what"/>: none when no file changed.</summary>
	public IEnumerable<string> Warnings(string what)
	{
		if (Stamped.Count > 0)
		{
			yield return $"{what}: {Stamped.Count} file{(Stamped.Count == 1 ? "" : "s")} changed while UBT ran, so what it wrote may hold the old text though it is newer than the edit:"
				+ $" marked so the next build recompiles what uses them, and this build's binaries may mix old and new until then: {BuildEdits.Name(Stamped)}: build again before you test."
				+ $" {BuildEdits.OwnWritesHint}";
		}
		foreach (string Problem in Problems)
		{
			yield return $"{what}: {Problem}";
		}
		if (Stamped.Count == 0 && NotMarked.Count > 0)
		{
			yield return $"{what}: {BuildEdits.OwnWritesHint}";
		}
	}

	/// <summary>
	/// Why a command doesn't go on after a build that overlapped these edits, and what clears it. Only a file with a recorded time
	/// (<see cref="Written"/>) is told to save: a save of one without shows nothing.
	/// </summary>
	public string DescribeRetryOverlap()
	{
		// Asked now: a file deleted then may be back, and then it is one to save.
		List<string> Gone = [.. NotMarked.Where(RebuildNeededFile.IsGone)];
		List<string> Unsaved = [.. NotMarked.Except(Gone, RebuildNeededFile.PathComparer).Where(Written.ContainsKey)];
		List<string> Timeless = [.. NotMarked.Except(Gone, RebuildNeededFile.PathComparer).Except(Unsaved, RebuildNeededFile.PathComparer)];
		string UnsavedNote = Unsaved.Count == 0 ? "" : $" ({BuildEdits.Name(Unsaved)} could not be marked for a recompile: save {(Unsaved.Count == 1 ? "it" : "them")} again first)";
		string TimelessNote = Timeless.Count == 0 ? "" : $" ({BuildEdits.Name(Timeless)} could not be marked for a recompile, and {(Timeless.Count == 1 ? "has" : "have")} no recorded time"
			+ $" to show a save by: a build whose mark takes clears {(Timeless.Count == 1 ? "it" : "them")}, else delete {RebuildNeededFile.FileName})";
		string GoneNote = Gone.Count == 0 ? "" : $" ({BuildEdits.Name(Gone)} {(Gone.Count == 1 ? "was" : "were")} deleted, so there is nothing to mark: {BuildEdits.DeletedNote})";
		string Why = $"source files changed while a build ran, so its binaries may mix old and new: {BuildEdits.Name(Overlapped)}{UnsavedNote}{TimelessNote}{GoneNote}: nothing was run. Build again before you test";
		return WayOut is null ? $"{Why}. {BuildEdits.OwnWritesHint}" : $"{Why}: {WayOut}";
	}
}

/// <summary>
/// The span of a build in which an edit can leave something stale: from just before UBT runs until it ended. An edit before
/// UBT's first action is no safer than one after it. UBT may already have passed A.cpp as up to date on the old header when
/// P.cpp compiles with the new one, and UHT writes its <c>Timestamp</c> after it looked at the headers, so a header saved
/// between the two is older than that file and UHT's code stays stale. So what a pre-build step or a <c>.Build.cs</c> writes
/// counts as well, until the project names it (<see cref="ProjectSettings.GetBuildWrites"/>).
/// </summary>
internal sealed class BuildEditWindow
{
	readonly UakContext Context;
	readonly UnrealServices Services;
	DateTime? Closed;

	BuildEditWindow(UakContext context, UnrealServices services)
	{
		Context = context;
		Services = services;
		Opened = BuildEdits.Start(context, services);
	}

	/// <summary>When the window opened: just before UBT ran (<see cref="BuildEdits.Start"/>).</summary>
	public DateTime Opened { get; }

	/// <summary>Opens the window, just before UBT runs.</summary>
	public static BuildEditWindow Open(UakContext context, UnrealServices services) => new(context, services);

	/// <summary>Closes the window, as UBT ends (<see cref="BuildEdits.End"/>); once closed it stays as it is. Returns when.</summary>
	public DateTime Close()
	{
		Closed ??= BuildEdits.End(Context, Services);
		return Closed.Value;
	}
}

/// <summary>
/// Stale objects from an edit made while UBT ran. UBT reads a header or source, the agent saves a new version of it, and the
/// compile then writes an object from the old text, dated after the edit, so every later build takes it as up to date, and
/// units built before and after the edit disagree about a type; UHT's <c>Timestamp</c> can pass an edit the same way, even in
/// a build that compiled nothing. After each build, under the lock, this finds the project's
/// source files whose write time falls inside the build (<see cref="BuildEditWindow"/>) and gives them a write time past
/// everything that build wrote, the way <c>uak mutate</c>'s restore does (<see cref="MutationJournal.StampFiles"/>), so the
/// next build recompiles what uses them.
/// <para>
/// It only ever raises a write time, and never rewrites a file, as another agent may be saving it. So it can cost a recompile,
/// never a change to a file or to what <c>uak mutate</c> requires. Files of a mutation journal are left alone: their stamp is
/// the journal's. Nothing that refuses goes before that stamp ends, so a uak or a UBT killed or cancelled before it did leaves
/// any journal or marker standing, and only what it hadn't marked unmarked. It doesn't cover: a build uak didn't run (an
/// IDE's, or UAT's outside <c>uak test -cooked</c>); a <c>uak compile</c>, which gets no stamp and no warning when it overlaps an
/// edit; files outside the project's Source and Plugins folders (the engine, a plugin folder named in the .uproject); or a
/// change that keeps an old write time (a copy that keeps its date). And it can't mend the binaries of the build that
/// overlapped the edit: the warning says to build again.
/// </para>
/// </summary>
internal static class BuildEdits
{
	/// <summary>As <see cref="RebuildNeededFile.OwnWritesHint"/>.</summary>
	public const string OwnWritesHint = RebuildNeededFile.OwnWritesHint;

	/// <summary>What UBT does about a deleted file a compile read: it takes that action as out of date (ActionGraph.cs), so no mark is needed.</summary>
	public const string DeletedNote = "the next build recompiles what used it, and fails if a file still includes it";

	/// <summary>The project's folders that hold what a build compiles.</summary>
	public static readonly string[] SourceRoots = ["Source", "Plugins"];

	/// <summary>As <see cref="RebuildNeededFile.Name"/>.</summary>
	public static string Name(IReadOnlyCollection<string> files) => RebuildNeededFile.Name(files);

	/// <summary>
	/// The time a build starts, read just before UBT runs (<see cref="BuildEditWindow"/>): the
	/// earlier of this machine's clock and the file system's, so nothing edited from now on can look older. A file time needn't
	/// agree with the clock: NTFS takes a coarser clock, a tick behind, and a file server stamps what is saved there with its
	/// own. The probe goes in Intermediate: a temporary file in a source folder would change that folder's time, which UBT's
	/// makefile checks.
	/// </summary>
	public static DateTime Start(UakContext context, UnrealServices services)
	{
		DateTime Clock = DateTime.UtcNow;
		return Probe(context, services, "uak-build-start") is DateTime Time && Time < Clock ? Time : Clock;
	}

	/// <summary>
	/// The time a build ends: the later of the clock and the file system's (as <see cref="Start"/>), so an edit made before it can't
	/// look newer. A file dated past it is no edit of that build (a clock set wrong, a copy from elsewhere): without this bound
	/// it would count for every build.
	/// </summary>
	public static DateTime End(UakContext context, UnrealServices services)
	{
		DateTime Clock = DateTime.UtcNow;
		return Probe(context, services, "uak-build-end") is DateTime Time && Time > Clock ? Time : Clock;
	}

	/// <summary>The file system's time in the project's Intermediate folder; null with no project, or when it can't be told.</summary>
	static DateTime? Probe(UakContext context, UnrealServices services, string name)
	{
		string? Project = context.ProjectDirectory?.FullName;
		if (Project is null)
		{
			return null;
		}
		string Intermediate = Path.Combine(Project, "Intermediate");
		try
		{
			Directory.CreateDirectory(Intermediate);
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			// UBT will say so. Without a probe only the clock is left.
		}
		return services.ProbeFileTime(Path.Combine(Intermediate, name));
	}

	/// <summary>
	/// The files mutation journals look after (<see cref="MutationJournal.GetManagedFiles"/>), which <see cref="Find"/> leaves
	/// out. A build takes these before it clears any journal, or its own clearing would turn a stamp into an edit.
	/// </summary>
	public static Dictionary<string, DateTime> GetManaged(UakContext context)
	{
		try
		{
			return MutationJournal.GetManagedFiles(context);
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			// Unread, a journal's file can only be stamped again, which costs a recompile and asks nothing of the journal.
			return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
		}
	}

	/// <summary>
	/// The source and header files and build descriptions under the project's Source and Plugins folders written at or after
	/// <paramref name="since"/> (and up to <paramref name="until"/>, when given), but for <paramref name="managed"/> ones and
	/// those the project says a build writes itself.
	/// </summary>
	public static List<string> Find(UakContext context, Dictionary<string, DateTime> managed, DateTime since, DateTime? until = null)
	{
		List<string> Found = [];
		string? Project = context.ProjectDirectory?.FullName;
		if (Project is null)
		{
			return Found;
		}
		HashSet<string> Visited = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
		foreach (string Root in SourceRoots.Select(Root => Path.Combine(Project, Root)))
		{
			Collect(Root, since, until ?? DateTime.MaxValue, managed, Found, Visited);
		}
		if (Found.Count > 0)
		{
			Found.RemoveAll(GetOwnWrites(context, Project).Contains);
		}
		return Found;
	}

	/// <summary>
	/// The files the project says its builds write themselves, by full path. Settings that can't be read, in any way, leave none,
	/// with a warning: that costs a recompile, where refusing or throwing would throw away a build that has run.
	/// </summary>
	static HashSet<string> GetOwnWrites(UakContext context, string project)
	{
		StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		try
		{
			return new HashSet<string>(ProjectSettings.GetBuildWrites(context).Select(File => Path.GetFullPath(Path.Combine(project, File))), Comparer);
		}
		catch (Exception Exception) when (Exception is not OutOfMemoryException)
		{
			context.Logger.LogWarning("Could not read which files a build writes itself, so they count as edits: {Message}", Exception.Message);
			return new HashSet<string>(Comparer);
		}
	}

	/// <summary>
	/// What a folder is: a junction or a symbolic link counts as its target, so a link back up the tree ends the walk there. A link
	/// is followed once, not skipped: a plugin junctioned into the project is a plugin the build compiles.
	/// </summary>
	static string RealPath(string directory)
	{
		DirectoryInfo Folder = new(directory);
		FileSystemInfo? Target = Folder.Attributes.HasFlag(FileAttributes.ReparsePoint) ? Folder.ResolveLinkTarget(returnFinalTarget: true) : null;
		return Path.TrimEndingDirectorySeparator((Target ?? Folder).FullName);
	}

	static void Collect(string directory, DateTime since, DateTime until, Dictionary<string, DateTime> managed, List<string> found, HashSet<string> visited)
	{
		try
		{
			if (!visited.Add(RealPath(directory)))
			{
				return;
			}
			foreach (string File in Directory.EnumerateFiles(directory))
			{
				if (!MutateCommand.IsSource(File) && !MutateCommand.IsBuildDescription(File))
				{
					continue;
				}
				DateTime Written = System.IO.File.GetLastWriteTimeUtc(File);
				if (Written >= since && Written <= until && !(managed.TryGetValue(File, out DateTime Stamp) && Written <= Stamp))
				{
					found.Add(File);
				}
			}
			foreach (string Child in Directory.EnumerateDirectories(directory))
			{
				string Name = Path.GetFileName(Child);
				// A project's or plugin's Content holds assets, and any Python's headers; a Content folder in a module is compiled like the rest.
				bool Assets = Name.Equals("Content", StringComparison.OrdinalIgnoreCase) && Directory.EnumerateFiles(directory).Any(File => File.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase) || File.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase));
				if (!Assets && !SourceFiles.SkippedDirectories.Contains(Name, StringComparer.OrdinalIgnoreCase))
				{
					Collect(Child, since, until, managed, found, visited);
				}
			}
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			// A folder that doesn't exist (no Plugins), or went while this looked: nothing there to stamp.
		}
	}

	/// <summary>
	/// After a build, under its lock: marks every file <see cref="Find"/> finds inside <paramref name="window"/>, which it closes,
	/// for a recompile (<see cref="MarkAsync"/>).
	/// </summary>
	public static async Task<BuildEditReport> StampAsync(UakContext context, UnrealServices services, Dictionary<string, DateTime> managed, BuildEditWindow window)
	{
		List<string> Files = Find(context, managed, window.Opened, window.Close());
		return Files.Count == 0 ? BuildEditReport.None : await MarkAsync(context, services, Files).ConfigureAwait(false);
	}

	/// <summary>
	/// Marks <paramref name="files"/> for a recompile. It first waits out <see cref="UnrealServices.RecompileMargin"/>, as
	/// <c>uak mutate</c>'s restore does, so what the next build writes is newer than the stamp, which is then no longer in the
	/// future, by a clock that agrees with this machine's. A file server's clock that runs ahead is a stated limit: the stamp is
	/// taken from it, so it can be in the future here, and it can fall inside the next build's window, which opens by the earlier
	/// clock, and so count as an edit of that build: a false positive, which costs a warning and a recompile. Each time is read
	/// back, as <see cref="MutationJournal.StampFiles"/> does: a file it can't mark is reported, and stops nothing, with the later
	/// of its write time as this leaves it, read last, and the end of the build by the clock of the file system the file is on
	/// (<see cref="BuildEditReport.Written"/>): a probe in its folder once the marks are done, one for each folder, as
	/// <c>uak mutate</c>'s restore takes its reference, or this machine's end of the build (<see cref="End"/>) when no probe can be
	/// written there. So a time a mark moved and that didn't take is no save, nor is one set between the edit and that end, and a
	/// later time is a write after the build, which a save is, even on a file server whose clock runs behind this machine's. The
	/// probe changes the folder's time, which UBT's makefile checks: that costs the next build its makefile, and only when a mark
	/// failed. <paramref name="keptFromEarlier"/> says the files are kept from an earlier build
	/// (<see cref="RebuildNeeded.AfterBuildAsync"/>), so their lines don't say they changed during this one.
	/// </summary>
	public static async Task<BuildEditReport> MarkAsync(UakContext context, UnrealServices services, IReadOnlyList<string> files, bool keptFromEarlier = false)
	{
		DateTime Reference = End(context, services);
		DateTime Stamp = Reference + services.RecompileMargin;
		await services.Delay(services.RecompileMargin).ConfigureAwait(false);
		List<string> Stamped = [];
		List<string> NotMarked = [];
		List<string> Problems = [];
		Dictionary<string, DateTime> TimeLeft = new(RebuildNeededFile.PathComparer);
		foreach (string File in files)
		{
			string? Why = null;
			try
			{
				if (System.IO.File.GetLastWriteTimeUtc(File) < Stamp)
				{
					services.SetWriteTime(File, Stamp);
				}
				DateTime Written = System.IO.File.GetLastWriteTimeUtc(File);
				if (Written - Reference <= MutationJournal.UbtTimestampSlop)
				{
					Why = string.Create(CultureInfo.InvariantCulture,
						$"its write time is {UakJson.FormatTime(Written)}, not more than {MutationJournal.UbtTimestampSlop.TotalSeconds:0} s after {UakJson.FormatTime(Reference)}");
				}
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException)
			{
				// A path the system can't name (a marker edited by hand) is reported as gone, as IsGone says. Nothing here is cancelled:
				// an OperationCanceledException is .NET's ERROR_OPERATION_ABORTED, a query a file system or a share cancelled.
				Why = Exception.Message;
			}
			if (Why is null)
			{
				Stamped.Add(File);
			}
			else
			{
				NotMarked.Add(File);
				DateTime? Left = ReadWriteTime(File);
				if (Left is DateTime Time)
				{
					TimeLeft[File] = Time;
				}
				// Only a file with a recorded time can show a save: what clears one without is a build whose mark takes.
				string What = keptFromEarlier ? $"{File}, kept from an earlier build that overlapped its edit," : $"{File} changed while UBT ran, and";
				Problems.Add(RebuildNeededFile.IsGone(File)
					? $"{What} was deleted before it could be marked for a recompile, so there is nothing to mark: {DeletedNote}"
					: keptFromEarlier
						? $"{What} still could not be marked for a recompile ({Why}); if no build can clear it, delete {RebuildNeededFile.FileName}"
						: Left is null
							? $"{What} could not be marked for a recompile ({Why}), with no recorded time, so a save shows nothing: a build whose mark takes clears it; if none can, delete {RebuildNeededFile.FileName}"
							: $"{What} could not be marked for a recompile ({Why}): save it again before the next whole build");
			}
		}
		// The end of the build by the clock of the file system each file is on, read now: a save past it is a write after the build.
		Dictionary<string, DateTime> LeftAt = new(RebuildNeededFile.PathComparer);
		Dictionary<string, DateTime?> Probes = new(StringComparer.OrdinalIgnoreCase);
		DateTime? Clock = null;
		foreach ((string File, DateTime Time) in TimeLeft)
		{
			string Folder = Path.GetDirectoryName(File) ?? "";
			if (!Probes.TryGetValue(Folder, out DateTime? Probe))
			{
				Probe = ProbeBeside(services, File);
				Probes[Folder] = Probe;
			}
			DateTime Ended = Probe ?? (Clock ??= End(context, services));
			LeftAt[File] = Time > Ended ? Time : Ended;
		}
		return new BuildEditReport(Stamped, NotMarked, Problems) { Written = LeftAt };
	}

	/// <summary>The clock of the file system <paramref name="file"/> is on (<see cref="UnrealServices.ProbeFileTime"/>); null when it can't be told.</summary>
	static DateTime? ProbeBeside(UnrealServices services, string file)
	{
		try
		{
			return services.ProbeFileTime(file);
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException)
		{
			return null;
		}
	}

	/// <summary>A file's write time; null when it is gone, or that can't be told.</summary>
	static DateTime? ReadWriteTime(string file)
	{
		try
		{
			return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OperationCanceledException)
		{
			return null;
		}
	}

	/// <summary>
	/// What a build reports of <paramref name="stamped"/>. One that <paramref name="compiledNothing"/> reports only the files it
	/// couldn't mark. Its binaries mix nothing, so a stamped file needs no word: the stamp is for UHT, whose <c>Timestamp</c> UBT
	/// rewrites even then (ExternalExecution.UpdateTimestamps, UE 5.7; the target's <c>.uhttimestamps</c> in 5.8), so an edit can
	/// fall behind it. But a file it couldn't mark stays behind that <c>Timestamp</c>, and no later build picks it up.
	/// </summary>
	public static BuildEditReport Reported(BuildEditReport stamped, bool compiledNothing)
	{
		return compiledNothing ? new BuildEditReport([], stamped.NotMarked, stamped.Problems) { Written = stamped.Written } : stamped;
	}
}

/// <summary>Where what a build couldn't record in the marker went (<see cref="RebuildNeeded.KeepForEditor"/>, <see cref="RebuildNeeded.HoldWhere"/>).</summary>
internal enum OverlapKeptIn
{
	/// <summary>Nowhere: nothing refuses for it.</summary>
	Nowhere,

	/// <summary>The marker (<see cref="RebuildNeededFile"/>).</summary>
	Marker,

	/// <summary>The mutation journals, as the marker couldn't take it, until a build hands it over.</summary>
	Journals,

	/// <summary>Perhaps the mutation journals: they couldn't all be listed or written.</summary>
	Unknown,
}

/// <summary>
/// A rebuild that overlapped an edit may clear its journal, so nothing would stop the next <c>uak test</c>, <c>uak mutate</c> or
/// <c>uak lock run</c>, perhaps another agent's, from running on binaries that mix old and new. <c>&lt;State&gt;/RebuildNeeded.json</c>
/// (<see cref="RebuildNeededFile"/>) remembers that, for each target, and those three refuse while it stands. What leaves an
/// entry is decided in one place (<see cref="WhatStays"/>): a whole build of that target (no UBT arguments) that succeeds and
/// overlaps nothing takes what it can show out, and marks what is left of the files that couldn't be marked again. Unlike a
/// mutation journal the rule needs no proof of what was compiled, so a plain <c>uak build</c> clears it, or says what to save.
/// But it can't clear the entry of a target that no longer exists, so the refusal names the file to delete. All of it runs
/// under the editor lock.
/// </summary>
internal static class RebuildNeeded
{
	/// <summary>The file's name in the state folder.</summary>
	public const string FileName = RebuildNeededFile.FileName;

	/// <summary>What stands, for every target, with its way out. A file that can't be read refuses (<see cref="RebuildNeededFile.Read"/>).</summary>
	public static BuildEditReport Read(UakContext context)
	{
		List<RebuildNeededEntry> Entries = RebuildNeededFile.Read(context);
		BuildEditReport Standing = Entries.Aggregate(BuildEditReport.None, (Report, Entry) => Report.Plus(ReportOf(Entry)));
		return Entries.Count == 0 ? Standing : Standing with { WayOut = RebuildNeededFile.DescribeWayOut(context, Entries) };
	}

	static BuildEditReport ReportOf(RebuildNeededEntry entry) => new(entry.Stamped, entry.NotMarked, entry.Problems) { Written = entry.NotMarkedWritten };

	/// <summary>
	/// The entry that remembers <paramref name="report"/>, naming each file once (<see cref="RebuildNeededFile.MergeSpellings"/>):
	/// a report may come from a journal's entry, whose times are keyed by case.
	/// </summary>
	public static RebuildNeededEntry EntryOf(string target, string platform, string configuration, BuildEditReport report)
	{
		RebuildNeededEntry Entry = new()
		{
			Target = target,
			Platform = platform,
			Configuration = configuration,
			Stamped = [.. report.Stamped],
			NotMarked = [.. report.NotMarked],
			Problems = [.. report.Problems],
			NotMarkedWritten = new Dictionary<string, DateTime>(report.Written),
		};
		RebuildNeededFile.MergeSpellings(Entry);
		return Entry;
	}

	/// <summary>Whether an entry of this build stands.</summary>
	public static bool Stands(UakContext context, string target, string platform, string configuration)
	{
		return RebuildNeededFile.Load(context).Entries.Exists(Entry => Entry.Is(target, platform, configuration));
	}

	/// <summary>
	/// <b>The rule:</b> a file leaves the marker only on evidence: it was stamped before a whole, successful, non-overlapping build;
	/// or its own write time moved past the time recorded for it and before that build's window opened; or it no longer exists.
	/// Nothing else removes it, and that includes an entry being replaced.
	/// <para>
	/// This is the only code that lets a file of an entry go: whatever rewrites or removes an entry takes what stays from here, and
	/// a move to <see cref="RebuildNeededEntry.Stamped"/> needs a mark that takes. <paramref name="cleanBuildOpened"/> is when the
	/// window of such a build of the entry's target opened (<see cref="BuildEditWindow.Opened"/>), and null for any other build,
	/// which shows nothing of what it compiled. A saved file was compiled by that build: the recorded time is no earlier than the
	/// end of the build that overlapped it, by the clock of the file's own file system (<see cref="BuildEdits.MarkAsync"/>), so a later one is a write after that build, which
	/// a save is, and before the window it was there for this build to read. A file with no recorded time shows no save, and only
	/// leaves through a mark that takes or by being gone. A marker keeps times to the millisecond, so that is what is compared.
	/// Gone means the file system said so (<see cref="RebuildNeededFile.IsGone"/>): a file it can't reach stays, and so does one
	/// whose time can't be read. A file server's clock that a time can't be judged by is a stated limit, as for
	/// <see cref="BuildEdits.MarkAsync"/>.
	/// </para>
	/// </summary>
	public static BuildEditReport WhatStays(RebuildNeededEntry entry, DateTime? cleanBuildOpened)
	{
		static long Milliseconds(DateTime Time) => Time.Ticks / TimeSpan.TicksPerMillisecond;
		bool Saved(string File)
		{
			if (cleanBuildOpened is not DateTime Opened || !entry.NotMarkedWritten.TryGetValue(File, out DateTime Recorded))
			{
				return false;
			}
			DateTime Written;
			try
			{
				Written = System.IO.File.GetLastWriteTimeUtc(File);
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				return false;
			}
			return Milliseconds(Written) > Milliseconds(Recorded) && Written < Opened;
		}
		List<string> Stamped = cleanBuildOpened is null ? [.. entry.Stamped.Where(File => !RebuildNeededFile.IsGone(File))] : [];
		// Once per file, as the marker's paths are keys (RebuildNeededFile.MergeSpellings): an entry that wasn't loaded may name one twice.
		List<(string File, string Problem)> Left = [.. entry.NotMarked.Zip(entry.Problems).DistinctBy(Pair => Pair.First, RebuildNeededFile.PathComparer)
			.Where(Pair => !RebuildNeededFile.IsGone(Pair.First) && !Saved(Pair.First))];
		return new BuildEditReport(Stamped, [.. Left.Select(Pair => Pair.File)], [.. Left.Select(Pair => Pair.Problem)])
		{
			Written = Left.Where(Pair => entry.NotMarkedWritten.ContainsKey(Pair.File)).ToDictionary(Pair => Pair.File, Pair => entry.NotMarkedWritten[Pair.File], RebuildNeededFile.PathComparer),
		};
	}

	/// <summary>
	/// Remembers what a build of a target overlapped, if anything, beside what stays of its entry (<see cref="WhatStays"/>): a build
	/// that overlapped an edit shows nothing, whether it failed or not. Returns whether it is on disk: one that can't be written is
	/// warned about, and whatever else refuses must stay. So is a file that wasn't read whole, as an entry in it may stand all the same.
	/// </summary>
	public static bool Keep(UakContext context, string target, string platform, string configuration, BuildEditReport edits)
	{
		if (edits.Overlapped.Count == 0)
		{
			return true;
		}
		try
		{
			RebuildNeededContents Marker = RebuildNeededFile.Load(context);
			if (!Marker.IsKnown)
			{
				throw new IOException($"{RebuildNeededFile.GetFile(context)} can't be read: {Marker.ReadFailure ?? "it holds no marker"}");
			}
			List<RebuildNeededEntry> Entries = Marker.Entries;
			RebuildNeededEntry? Last = Entries.Find(Entry => Entry.Is(target, platform, configuration));
			BuildEditReport Report = Last is null ? edits : WhatStays(Last, null).Plus(edits);
			Entries.RemoveAll(Entry => Entry.Is(target, platform, configuration));
			Entries.Add(EntryOf(target, platform, configuration, Report));
			StateFiles.WriteJson(RebuildNeededFile.GetFile(context), Entries);
			return true;
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("Could not record that a build overlapped an edit, so the next `uak test` won't know: {Message}", Exception.Message);
			return false;
		}
	}

	/// <summary>
	/// For <c>uak test -cooked</c>: unless told to skip it, BuildCookRun also builds the project's editor target for this machine in
	/// Development (BuildProjectCommand.Automation.cs), the binaries <c>uak test</c> runs, so a file its build couldn't mark is
	/// kept under that target's entry (<see cref="Keep"/>), as no later build picks it up. That is no cooked entry: a whole
	/// <c>uak build</c> of the editor target clears it by the rule. A stamped file isn't kept: it recompiles at the next build, and
	/// the run's note tells it. Returns where the file went, the marker or else the mutation journals (<see cref="HoldWhere"/>), and
	/// the entry it went as; nowhere and no entry when there was nothing to keep, or the target can't be told, with a warning.
	/// </summary>
	public static (OverlapKeptIn Where, RebuildNeededEntry? Entry) KeepForEditor(UakContext context, UnrealServices services, BuildEditReport edits)
	{
		if (edits.NotMarked.Count == 0)
		{
			return (OverlapKeptIn.Nowhere, null);
		}
		string Target;
		try
		{
			Target = TargetResolver.ResolveEditorTarget(UnrealServices.RequireProject(context));
		}
		catch (Exception Exception) when (Exception is UakSetupException or UakUsageException or IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("Could not tell the project's editor target, so nothing records that {Files} could not be marked: {Message}", BuildEdits.Name(edits.NotMarked), Exception.Message);
			return (OverlapKeptIn.Nowhere, null);
		}
		BuildEditReport Unmarked = new([], edits.NotMarked, edits.Problems) { Written = edits.Written };
		RebuildNeededEntry Entry = EntryOf(Target, services.HostPlatform.Name, "Development", Unmarked);
		return (Keep(context, Target, services.HostPlatform.Name, "Development", Unmarked) ? OverlapKeptIn.Marker : HoldWhere(context, Entry), Entry);
	}

	/// <summary>
	/// For a whole build that succeeded and overlapped nothing, whose window opened at <paramref name="opened"/>. What stays of
	/// its target's entry (<see cref="WhatStays"/>) is a file that couldn't be marked and nothing shows was compiled: each mark is tried
	/// again, and one that takes moves the file to <see cref="RebuildNeededEntry.Stamped"/>, so the next build compiles it and
	/// clears the entry. With nothing left it clears. A file that can't be read is left, with a warning; one that holds no marker
	/// goes only with a build of the target <c>uak test</c> uses (<see cref="ClearCorrupt"/>). Returns the warnings to print.
	/// </summary>
	public static async Task<List<string>> AfterBuildAsync(UakContext context, UnrealServices services, string target, string platform, string configuration, DateTime opened)
	{
		string MarkerFile = RebuildNeededFile.GetFile(context);
		RebuildNeededContents Marker = RebuildNeededFile.Load(context);
		if (Marker.ReadFailure is string Failure)
		{
			return [$"build: could not read {MarkerFile}, so it stays and nothing in it is cleared, and `uak test`, `uak mutate` and `uak lock run` go on refusing: {Failure}"];
		}
		if (Marker.Corrupt)
		{
			return ClearCorrupt(context, services, target, platform, configuration);
		}
		RebuildNeededEntry? Entry = Marker.Entries.Find(Entry => Entry.Is(target, platform, configuration));
		if (Entry is null)
		{
			return [];
		}
		BuildEditReport Stays = WhatStays(Entry, opened);
		if (Stays.Overlapped.Count == 0)
		{
			Marker.Entries.Remove(Entry);
			try
			{
				Store(context, Marker.Entries);
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
			{
				context.Logger.LogWarning("Could not clear {File}, so `uak test` and `uak mutate` still refuse: {Message}", MarkerFile, Exception.Message);
			}
			return [];
		}
		BuildEditReport Marked = await BuildEdits.MarkAsync(context, services, Stays.NotMarked, keptFromEarlier: true).ConfigureAwait(false);
		Entry.Stamped = [.. Stays.Stamped.Except(Marked.Stamped, RebuildNeededFile.PathComparer), .. Marked.Stamped];
		Entry.NotMarked = [.. Marked.NotMarked];
		Entry.Problems = [.. Marked.Problems];
		// A file that had no time gets none now: what shows a save of it is a time recorded when the build that overlapped it ended.
		Entry.NotMarkedWritten = Marked.Written.Where(Pair => Stays.Written.ContainsKey(Pair.Key)).ToDictionary(Pair => Pair.Key, Pair => Pair.Value, RebuildNeededFile.PathComparer);
		List<string> Warnings = [.. Marked.Problems.Select(Problem => "build: " + Problem)];
		try
		{
			Store(context, Marker.Entries);
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			Warnings.Add($"build: could not record what was marked in {RebuildNeededFile.FileName}: {Exception.Message}");
		}
		// Only a file with a recorded time can show a save; one without leaves through a mark that takes, or by hand.
		List<string> Saveable = [.. Marked.NotMarked.Where(Entry.NotMarkedWritten.ContainsKey)];
		List<string> Timeless = [.. Marked.NotMarked.Except(Saveable, RebuildNeededFile.PathComparer)];
		List<string> Steps = [];
		if (Saveable.Count > 0)
		{
			Steps.Add($"save {BuildEdits.Name(Saveable)}, then run a whole build, which clears {(Saveable.Count == 1 ? "it" : "them")} whether or not the mark takes;"
				+ $" if {(Saveable.Count == 1 ? "it" : "they")} can't be saved, delete {MarkerFile}");
		}
		if (Timeless.Count > 0)
		{
			Steps.Add($"{BuildEdits.Name(Timeless)} {(Timeless.Count == 1 ? "has" : "have")} no recorded time to show a save by, so saving clears nothing:"
				+ $" a build whose mark takes clears {(Timeless.Count == 1 ? "it" : "them")}; if none can, delete {MarkerFile}");
		}
		string Next = Marked.NotMarked.Count == 0
			? $"marked {BuildEdits.Name(Marked.Stamped)} for a recompile: build again, which compiles {(Marked.Stamped.Count == 1 ? "it" : "them")} and clears the refusal"
			: string.Join("; ", Steps);
		// Nothing changed during this build: what stays is an earlier build's, and a compile line here can't show its edit was compiled.
		string It = Stays.NotMarked.Count == 1 ? "it" : "them";
		Warnings.Add($"build: {BuildEdits.Name(Stays.NotMarked)} {(Stays.NotMarked.Count == 1 ? "is" : "are")} kept from an earlier build that overlapped {(Stays.NotMarked.Count == 1 ? "its edit" : "their edits")}"
			+ $" and could not mark {It}. No build can prove it compiled the edit to a file it couldn't mark, so a compile of {It} in this build's log doesn't clear {It}, and its binaries"
			+ $" may still lack the edit: `uak test`, `uak mutate` and `uak lock run` go on refusing: {Next}");
		return Warnings;
	}

	/// <summary>
	/// What a build of a target says of the marker once it is done with it: the infos and the warnings to print. A file this build
	/// couldn't mark (<paramref name="kept"/>) that the marker now keeps refuses until a whole build clears it; an entry an earlier
	/// build left that this one cleared is told, as is one that a build given UBT arguments (not <paramref name="whole"/>) left
	/// standing, as such a build clears nothing. Nothing when the marker can't be read: what read it said so.
	/// </summary>
	public static (List<string> Infos, List<string> Warnings) DescribeEntry(UakContext context, string target, string platform, string configuration, bool stoodBefore,
		bool whole, IReadOnlyList<string> kept)
	{
		List<string> Infos = [];
		List<string> Warnings = [];
		RebuildNeededContents Marker = RebuildNeededFile.Load(context);
		if (!Marker.IsKnown)
		{
			return (Infos, Warnings);
		}
		string Build = $"{target} {platform} {configuration}";
		bool StandsNow = Marker.Entries.Exists(Entry => Entry.Is(target, platform, configuration));
		string Refuse = $"`uak test`, `uak mutate` and `uak lock run` refuse until a whole `uak build` of {Build} (no UBT arguments) clears it";
		if (kept.Count > 0 && StandsNow)
		{
			Warnings.Add($"build: {BuildEdits.Name(kept)} could not be marked, so {RebuildNeededFile.FileName} keeps {(kept.Count == 1 ? "it" : "them")} for {Build}: {Refuse}");
		}
		else if (StandsNow && stoodBefore && !whole)
		{
			Warnings.Add($"build: {RebuildNeededFile.FileName} still holds what an earlier build left for {Build}, and a build given UBT arguments clears nothing: {Refuse}");
		}
		if (!StandsNow && stoodBefore)
		{
			List<string> Others = [.. Marker.Entries.Select(Entry => $"{Entry.Target} {Entry.Platform} {Entry.Configuration}")];
			Infos.Add($"build: cleared what an earlier build left for {Build} in {RebuildNeededFile.FileName}"
				+ (Others.Count == 0 ? "" : $"; what stands for {string.Join(", ", Others)} still refuses"));
		}
		return (Infos, Warnings);
	}

	/// <summary>
	/// Clears a file that holds no marker, for a build of the target <c>uak test</c> uses: its editor target, on this machine, in
	/// Development, the binaries the refusal is there for. Nothing shows what the file held, so any other build leaves it. In a
	/// project whose editor target can't be told (none, or several) no build clears it, and the warning says to delete it. Returns
	/// the warnings to print.
	/// </summary>
	static List<string> ClearCorrupt(UakContext context, UnrealServices services, string target, string platform, string configuration)
	{
		string MarkerFile = RebuildNeededFile.GetFile(context);
		string? Project = context.ProjectFile?.FullName;
		string? EditorTarget;
		try
		{
			EditorTarget = Project is null ? null : TargetResolver.ResolveEditorTarget(Project);
		}
		catch (Exception Exception) when (Exception is UakSetupException or IOException or UnauthorizedAccessException)
		{
			// Its "give -target=" is for a command that resolves the target: no build's -target= clears this.
			string Why = Exception.Message.Replace("; give -target=", "", StringComparison.Ordinal);
			return [$"build: {MarkerFile} can't be read, and the project's editor target can't be told ({Why}), so no build clears it, and `uak test`, `uak mutate` and"
				+ $" `uak lock run` go on refusing: delete {MarkerFile} once no build that overlapped an edit is left unbuilt"];
		}
		if (EditorTarget is null || !target.Equals(EditorTarget, StringComparison.OrdinalIgnoreCase)
			|| !platform.Equals(services.HostPlatform.Name, StringComparison.OrdinalIgnoreCase) || !configuration.Equals("Development", StringComparison.OrdinalIgnoreCase))
		{
			return [$"build: {MarkerFile} can't be read, so `uak test`, `uak mutate` and `uak lock run` go on refusing: only a whole build of the target `uak test` uses clears it"];
		}
		try
		{
			Store(context, []);
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("Could not clear {File}, so `uak test` and `uak mutate` still refuse: {Message}", MarkerFile, Exception.Message);
		}
		return [];
	}

	/// <summary>Writes the entries, or deletes the file with none left. Only what <see cref="WhatStays"/> left comes here.</summary>
	static void Store(UakContext context, List<RebuildNeededEntry> entries)
	{
		string MarkerFile = RebuildNeededFile.GetFile(context);
		if (entries.Count > 0)
		{
			StateFiles.WriteJson(MarkerFile, entries);
		}
		else
		{
			System.IO.File.Delete(MarkerFile);
		}
	}

	/// <summary>
	/// Records in every stopped run's journal what <paramref name="entry"/> says and the marker couldn't take, so that journal stands
	/// (<see cref="MutationRecord.UnkeptOverlap"/>) for <see cref="Keep"/>'s sake until a build can hand it over. Returns whether a
	/// journal may hold it (<see cref="HoldWhere"/>): one that couldn't be listed or written may stand all the same.
	/// </summary>
	public static bool HoldInJournals(UakContext context, RebuildNeededEntry entry) => HoldWhere(context, entry) != OverlapKeptIn.Nowhere;

	/// <summary>
	/// As <see cref="HoldInJournals"/>, saying where it went: <see cref="OverlapKeptIn.Journals"/>; <see cref="OverlapKeptIn.Nowhere"/>
	/// when no journal stood to hold it, which is a stated limit, with a warning; or <see cref="OverlapKeptIn.Unknown"/> when the
	/// journals couldn't be listed or written, as they may stand all the same, with its own warning.
	/// </summary>
	public static OverlapKeptIn HoldWhere(UakContext context, RebuildNeededEntry entry)
	{
		try
		{
			if (MutationJournal.RecordUnkeptOverlap(context, entry) > 0)
			{
				return OverlapKeptIn.Journals;
			}
			context.Logger.LogWarning("No mutation journal stands to hold it either, so it is recorded nowhere and nothing will refuse for it: {Files}",
				BuildEdits.Name([.. entry.Stamped, .. entry.NotMarked]));
			return OverlapKeptIn.Nowhere;
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("Could not record that in a mutation journal either, so nothing will refuse once it is spent: {Message}", Exception.Message);
			return OverlapKeptIn.Unknown;
		}
	}

	/// <summary>Records <paramref name="entry"/> in <paramref name="journal"/>, the run's own (<see cref="HoldInJournals"/>).</summary>
	public static void HoldInJournal(UakContext context, MutationJournal journal, RebuildNeededEntry entry)
	{
		try
		{
			journal.AddUnkeptOverlap(entry);
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("Could not record that in the mutation journal '{Name}' either, so nothing will refuse once it is spent: {Message}", journal.Record.Name, Exception.Message);
		}
	}

	/// <summary>
	/// Hands what <paramref name="journal"/> holds to the marker (<see cref="Keep"/>), then lets it go, so a kill between the two
	/// leaves both. Returns whether the journal holds nothing now. What it hands over goes by the rule from there.
	/// </summary>
	public static bool KeepWhatItHolds(UakContext context, MutationJournal journal)
	{
		if (journal.Record.UnkeptOverlap is not List<RebuildNeededEntry> Held)
		{
			return true;
		}
		if (!Held.All(Entry => Keep(context, Entry.Target, Entry.Platform, Entry.Configuration, ReportOf(Entry))))
		{
			return false;
		}
		try
		{
			journal.ClearUnkeptOverlap();
			return true;
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("Could not clear what the mutation journal '{Name}' held, so it stands: {Message}", journal.Record.Name, Exception.Message);
			return false;
		}
	}

	/// <summary>As <see cref="KeepWhatItHolds"/>, for every journal. Returns whether none holds anything now; journals that can't be listed hold nothing it can tell.</summary>
	public static bool KeepWhatJournalsHold(UakContext context)
	{
		try
		{
			return MutationJournal.List(context).Journals.Aggregate(true, (All, Journal) => KeepWhatItHolds(context, Journal) && All);
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("Could not look for what mutation journals hold: {Message}", Exception.Message);
			return true;
		}
	}
}
