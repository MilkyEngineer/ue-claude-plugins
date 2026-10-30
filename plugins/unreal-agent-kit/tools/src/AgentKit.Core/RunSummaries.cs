// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core;

/// <summary>One command's summary: the lines it printed at its end, kept so <c>uak runs show</c> can print them without the log.</summary>
public sealed class RunSummaryEntry
{
	/// <summary>The command: "build", "test" or "mutate".</summary>
	public string Command { get; set; } = "";

	/// <summary>The command's own name (its -name=, or "uak-build").</summary>
	public string Name { get; set; } = "";

	/// <summary>When the command started (UTC).</summary>
	public DateTime? Started { get; set; }

	/// <summary>When the command finished (UTC); null while it has recorded no result (<see cref="RunSummaries.Begin"/>).</summary>
	public DateTime? Finished { get; set; }

	/// <summary>
	/// The command's exit code; null while it has recorded no result: it still runs, or it was killed before it recorded one.
	/// Never read as 0.
	/// </summary>
	public int? ExitCode { get; set; }

	/// <summary>The summary lines, in the order printed; the verdict line last. Empty while it has recorded no result.</summary>
	public List<string> Lines { get; set; } = [];

	/// <summary>
	/// The command run that recorded it (<see cref="RunSummaryInvocation.Id"/>), so a command replaces only its own entry, never
	/// that of another command under the same name; null when it was recorded without one.
	/// </summary>
	public string? Invocation { get; set; }

	/// <summary>The ID of the process that recorded it, to tell whether a command with no result still runs; null when unknown.</summary>
	public int? Pid { get; set; }

	/// <summary>That process's start time (UTC), so a reused process ID is never taken for it; null when unknown.</summary>
	public DateTime? ProcessStart { get; set; }

	/// <summary>
	/// Whether the process that recorded it may still run: its ID runs, and both start times are known and match
	/// (<see cref="ProcessIdentity.IsSurelyAlive"/>), so a reused ID never keeps a stopped command's entry; false when unknown.
	/// </summary>
	public bool MayStillRun() => Pid is int Id && new ProcessIdentity(Id, ProcessStart).IsSurelyAlive();
}

/// <summary>One run of a command, as its summary entries name it: an ID of its own, and its process.</summary>
/// <param name="Id">Unique to this run of the command.</param>
/// <param name="Pid">Its process's ID.</param>
/// <param name="ProcessStart">Its process's start time (UTC), when it can be read.</param>
public sealed record RunSummaryInvocation(string Id, int Pid, DateTime? ProcessStart)
{
	/// <summary>A new run of a command in this process.</summary>
	public static RunSummaryInvocation New()
	{
		ProcessIdentity Self = ProcessIdentity.Current;
		return new RunSummaryInvocation(Guid.NewGuid().ToString("N"), Self.Pid, Self.StartTimeUtc);
	}

	/// <summary>Marks an entry as this run's, and returns it.</summary>
	public RunSummaryEntry Stamp(RunSummaryEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		entry.Invocation = Id;
		entry.Pid = Pid;
		entry.ProcessStart = ProcessStart;
		return entry;
	}
}

/// <summary>The summaries recorded under one name.</summary>
public sealed class RunSummaryFile
{
	/// <summary>The name: a detached run's, or a command's own.</summary>
	public string Name { get; set; } = "";

	/// <summary>The summaries, oldest first: one per command under a command's own name, every command of the run under a run's.</summary>
	public List<RunSummaryEntry> Entries { get; set; } = [];
}

/// <summary>
/// The summaries <c>uak build</c>, <c>uak test</c> and <c>uak mutate</c> record, in <c>&lt;State&gt;/Summaries/&lt;name&gt;.json</c>.
/// Each entry names the command run that recorded it (<see cref="RunSummaryInvocation"/>: an ID, and its process). As it
/// starts, a command drops the results recorded under its own name, and the entries of commands under it that no longer run,
/// and adds an entry with no result yet (<see cref="Begin"/>), so an earlier command's result under that name is never read
/// as this one's; at its end, or when it is refused or fails, it records its result in its own entry's place, never in
/// another's. Two commands that run under one name at once (the default names, uak-build, uak-test and uak-test-cooked, are
/// shared by every command started without -name=) so keep an entry each, and <c>uak runs show</c> lists both, saying which
/// still run. Inside a detached run (<see cref="RunNameVariable"/>, which the run's wrapper sets) a command also adds both to
/// the run's file, which the wrapper empties when the run starts, so a run that builds and then tests keeps both. Runs and
/// commands share the names: a command started directly with -name=X drops run X's results, and <c>uak runs start -name=X</c>
/// clears command X's, so the two are given different names.
/// </summary>
public static class RunSummaries
{
	/// <summary>Set by a detached run's wrapper for its command: the run's name.</summary>
	public const string RunNameVariable = "UAK_RUN_NAME";

	/// <summary>&lt;State&gt;/Summaries.</summary>
	public static string GetDirectory(DirectoryInfo stateDirectory)
	{
		ArgumentNullException.ThrowIfNull(stateDirectory);
		return Path.Combine(stateDirectory.FullName, "Summaries");
	}

	/// <summary>Windows device names, which name no file in any folder (with or without an extension).</summary>
	static readonly HashSet<string> s_deviceNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
		"COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
		"LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
	};

	/// <summary>
	/// Whether a name can name a summary (and a command's log, and a mutation's journal folder): a plain file name that every
	/// platform keeps as it is, without white space, control characters or path separators, not ending in '.' (Windows drops a
	/// trailing dot, so "X." and "X" would share one file), not only dots, and no Windows device name (CON, NUL, COM1, ...,
	/// with or without an extension). <c>uak test</c>, <c>uak build</c> and <c>uak mutate</c> refuse any other -name=, and
	/// <c>uak runs show</c> takes any such name.
	/// </summary>
	public static bool IsValidName(string? name)
	{
		return !string.IsNullOrEmpty(name) && name.Trim('.').Length > 0 && !name.EndsWith('.') && !s_deviceNames.Contains(name.Split('.')[0])
			&& name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
			&& !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|');
	}

	/// <summary>Refuses (a usage error) a -name= that <see cref="IsValidName"/> doesn't take.</summary>
	public static void CheckName(string name)
	{
		if (!IsValidName(name))
		{
			throw new UakUsageException($"-name={name} must be a plain file name without spaces that doesn't end in '.' and isn't a device name"
				+ " such as NUL (it names the run's summary and log)");
		}
	}

	/// <summary>The file of a name, or null when the name can't be a file name (then nothing is recorded under it).</summary>
	public static string? GetFile(DirectoryInfo stateDirectory, string name)
	{
		if (!IsValidName(name))
		{
			return null;
		}
		return Path.Combine(GetDirectory(stateDirectory), name + ".json");
	}

	/// <summary>
	/// Records that a command has started under <paramref name="name"/>, as <paramref name="invocation"/> (a new one when null):
	/// the results recorded under that name are dropped, and so are the entries of commands under it that no longer run, and an
	/// entry with no result is added (to <paramref name="runName"/>'s file too, when that is another name), so <c>uak runs
	/// show</c> never prints an earlier command's result as this one's. Returns the failures, for a warning.
	/// </summary>
	public static List<string> Begin(DirectoryInfo stateDirectory, string command, string name, DateTime started, string? runName, RunSummaryInvocation? invocation = null)
	{
		RunSummaryEntry entry = (invocation ?? RunSummaryInvocation.New()).Stamp(new() { Command = command, Name = name, Started = started });
		return Store(stateDirectory, entry, runName, WriteMode.Start, WriteMode.Append);
	}

	/// <summary>
	/// Records a command's result under <see cref="RunSummaryEntry.Name"/>, and under <paramref name="runName"/> when that is
	/// another name, in place of the command's own entry (<see cref="IsOwn"/>); without one, it is added, after dropping the
	/// name's results and stopped commands as <see cref="Begin"/> does (in a run's file, only added). Never throws for a file
	/// it can't write: a summary is a convenience, and the command's result must not depend on it. Returns the failures, for
	/// a warning.
	/// </summary>
	public static List<string> Record(DirectoryInfo stateDirectory, RunSummaryEntry entry, string? runName)
	{
		return Store(stateDirectory, entry, runName, WriteMode.Finish, WriteMode.FinishOrAppend);
	}

	/// <summary>
	/// As <see cref="Record"/>, but only in place of the command's own entry with no result: for a command that is refused or
	/// fails after it may have recorded its summary, so a result recorded already stays. Returns the failures.
	/// </summary>
	public static List<string> RecordIfUnfinished(DirectoryInfo stateDirectory, RunSummaryEntry entry, string? runName)
	{
		return Store(stateDirectory, entry, runName, WriteMode.FinishOnly, WriteMode.FinishOnly);
	}

	/// <summary>
	/// Whether <paramref name="existing"/> is the entry of the command that records <paramref name="entry"/>: the same
	/// <see cref="RunSummaryEntry.Invocation"/>. An entry recorded without one is no command's own: it never takes another
	/// entry's place, so another command's entry with no result yet is never replaced by it.
	/// </summary>
	public static bool IsOwn(RunSummaryEntry existing, RunSummaryEntry entry)
	{
		ArgumentNullException.ThrowIfNull(existing);
		ArgumentNullException.ThrowIfNull(entry);
		return entry.Invocation is not null && entry.Invocation.Equals(existing.Invocation, StringComparison.Ordinal);
	}

	enum WriteMode
	{
		/// <summary>The name's results, and the entries of commands that no longer run, are dropped; the entry is added.</summary>
		Start,

		/// <summary>The entry is added at the end.</summary>
		Append,

		/// <summary>The entry replaces the command's own entry; without one, as <see cref="Start"/>.</summary>
		Finish,

		/// <summary>The entry replaces the command's own entry, or is added at the end.</summary>
		FinishOrAppend,

		/// <summary>The entry replaces the command's own entry with no result; without one, nothing changes.</summary>
		FinishOnly,
	}

	static List<string> Store(DirectoryInfo stateDirectory, RunSummaryEntry entry, string? runName, WriteMode own, WriteMode run)
	{
		ArgumentNullException.ThrowIfNull(entry);
		List<string> failures = [];
		bool inRun = !string.IsNullOrWhiteSpace(runName);
		// A run and its command may share a name: then the run's file keeps every command, so it is added to, not replaced.
		if (!inRun || !runName!.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
		{
			Write(stateDirectory, entry.Name, entry, own, failures);
		}
		if (inRun)
		{
			Write(stateDirectory, runName!, entry, run, failures);
		}
		return failures;
	}

	/// <summary>The summaries recorded under a name, or null when there are none.</summary>
	public static RunSummaryFile? Read(DirectoryInfo stateDirectory, string name)
	{
		string? file = GetFile(stateDirectory, name);
		return file is null ? null : StateFiles.ReadJson<RunSummaryFile>(file);
	}

	/// <summary>Forgets the summaries recorded under a name (a detached run's, as it starts).</summary>
	public static void Clear(DirectoryInfo stateDirectory, string name)
	{
		string? file = GetFile(stateDirectory, name);
		if (file is not null)
		{
			StateFiles.TryDelete(file);
		}
	}

	static void Write(DirectoryInfo stateDirectory, string name, RunSummaryEntry entry, WriteMode mode, List<string> failures)
	{
		string? file = GetFile(stateDirectory, name);
		if (file is null)
		{
			return;
		}
		try
		{
			// Two commands of one run may end together: each reads, adds and writes under the gate, so neither loses the other's.
			using FileStream gate = EnterGate(file + ".lock");
			RunSummaryFile summaries = StateFiles.ReadJson<RunSummaryFile>(file) ?? new RunSummaryFile();
			summaries.Name = name;
			int own = summaries.Entries.FindLastIndex(existing => IsOwn(existing, entry));
			if (own >= 0 && (mode is WriteMode.Finish or WriteMode.FinishOrAppend || (mode == WriteMode.FinishOnly && summaries.Entries[own].ExitCode is null)))
			{
				summaries.Entries[own] = entry;
			}
			else if (mode == WriteMode.FinishOnly)
			{
				return;
			}
			else if (mode is WriteMode.Start or WriteMode.Finish)
			{
				// Another command under this name that still runs keeps its entry; results, and commands gone without one, go.
				summaries.Entries = [.. summaries.Entries.Where(existing => existing.ExitCode is null && existing.Invocation is not null && !IsOwn(existing, entry)
					&& existing.MayStillRun()), entry];
			}
			else
			{
				summaries.Entries.Add(entry);
			}
			StateFiles.WriteJson(file, summaries);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			failures.Add($"{file}: {exception.Message}");
		}
	}

	/// <summary>Opens a gate file for this process alone, retrying for a few seconds while another writer has it.</summary>
	static FileStream EnterGate(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
		while (true)
		{
			try
			{
				return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException) when (DateTime.UtcNow < deadline)
			{
				Thread.Sleep(20);
			}
		}
	}
}
