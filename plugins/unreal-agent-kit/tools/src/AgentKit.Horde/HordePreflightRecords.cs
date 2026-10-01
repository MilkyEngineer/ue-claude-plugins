// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Vcs;

namespace AgentKit.Horde;

/// <summary>One file of a shelf, as uak recorded it when it started a preflight.</summary>
public sealed class HordeRecordedShelfFile
{
	/// <summary>Its depot path.</summary>
	public string DepotFile { get; set; } = "";

	/// <summary>Its shelved action.</summary>
	public string Action { get; set; } = "";

	/// <summary>Its shelved content's MD5, when p4 gave one.</summary>
	public string? Digest { get; set; }
}

/// <summary>
/// What uak knows about a preflight it started: the request and the change's shelf when the job was created. A later
/// <c>uak horde preflight</c> reuses a running job only when this record exists and its shelf is the change's current shelf.
/// </summary>
public sealed class HordePreflightRecord
{
	/// <summary>The Horde job id.</summary>
	public string Job { get; set; } = "";

	/// <summary>The preflighted change.</summary>
	public int Change { get; set; }

	/// <summary>The Horde stream.</summary>
	public string Stream { get; set; } = "";

	/// <summary>The template.</summary>
	public string Template { get; set; } = "";

	/// <summary>Whether it was started with auto-submit.</summary>
	public bool AutoSubmit { get; set; }

	/// <summary>The parameters uak sent (Horde's ids).</summary>
	public Dictionary<string, string> Parameters { get; set; } = [];

	/// <summary>The change's shelf when uak created the job.</summary>
	public List<HordeRecordedShelfFile> Shelf { get; set; } = [];

	/// <summary>When uak created the job (UTC).</summary>
	public DateTime Created { get; set; }

	/// <summary>The recorded shelf as shelf files.</summary>
	public IReadOnlyList<PerforceShelfFile> ShelfFiles => Shelf.Select(file => new PerforceShelfFile(file.DepotFile, file.Action, file.Digest)).ToList();
}

/// <summary>
/// The preflights uak started, one file per job: <c>&lt;UAK_HOME&gt;/horde/&lt;server&gt;/preflights/&lt;job&gt;.json</c>, written
/// atomically. Records older than <see cref="MaximumAge"/> are deleted when a new one is saved.
/// </summary>
public sealed class HordePreflightRecordStore
{
	/// <summary>How long a record is kept.</summary>
	public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);

	/// <summary>Creates the store under <paramref name="root"/>, the kit's <c>horde</c> folder.</summary>
	public HordePreflightRecordStore(string root)
	{
		Root = Path.GetFullPath(root);
	}

	/// <summary>The kit's <c>horde</c> folder.</summary>
	public string Root { get; }

	/// <summary>This user's store.</summary>
	public static HordePreflightRecordStore ForUser() => new(HordeBuildSettingsStore.UserRoot());

	/// <summary>The folder of a server's records.</summary>
	public string FolderFor(Uri server) => Path.Combine(Root, HordeBuildSettingsStore.ServerFolder(server), "preflights");

	/// <summary>A job's record file, or null for an id that can't be a file name (then nothing is recorded or read).</summary>
	public string? PathFor(Uri server, string jobId)
		=> jobId.Length is > 0 and <= 64 && jobId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? Path.Combine(FolderFor(server), jobId + ".json") : null;

	/// <summary>A job's record, or null when uak has none (it didn't start the job here, or the record can't be read).</summary>
	public HordePreflightRecord? Read(Uri server, string jobId)
		=> PathFor(server, jobId) is string path ? StateFiles.ReadJson<HordePreflightRecord>(path) : null;

	/// <summary>Saves a record, and deletes records older than <see cref="MaximumAge"/>. Returns false for a job id that can't be a file name.</summary>
	public bool Save(Uri server, HordePreflightRecord record, DateTime utcNow)
	{
		string? path = PathFor(server, record.Job);
		if (path is null)
		{
			return false;
		}
		StateFiles.WriteJson(path, record);
		foreach (string old in SafeEnumerate(FolderFor(server)))
		{
			try
			{
				if (!old.Equals(path, StringComparison.OrdinalIgnoreCase) && utcNow - File.GetLastWriteTimeUtc(old) > MaximumAge)
				{
					StateFiles.TryDelete(old);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
		return true;
	}

	/// <summary>A record of a shelf, from the shelf files p4 described.</summary>
	public static List<HordeRecordedShelfFile> ToRecorded(IEnumerable<PerforceShelfFile> shelf)
		=> shelf.Select(file => new HordeRecordedShelfFile { DepotFile = file.DepotFile, Action = file.Action, Digest = file.Digest }).ToList();

	static IEnumerable<string> SafeEnumerate(string folder)
	{
		try
		{
			return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : [];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}
}
