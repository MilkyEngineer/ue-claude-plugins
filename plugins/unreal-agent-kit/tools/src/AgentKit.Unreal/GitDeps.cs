// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>A file a gitdeps manifest lists: its path from the engine root (forward slashes), the SHA-1 of its content (which names its blob), and whether it is executable.</summary>
internal sealed record GitDepsFile(string Name, string Hash, bool IsExecutable, string Manifest);

/// <summary>A blob: a file's content, <see cref="Size"/> bytes at <see cref="PackOffset"/> in the uncompressed pack <see cref="PackHash"/>.</summary>
internal sealed record GitDepsBlob(string Hash, long Size, string PackHash, long PackOffset)
{
	/// <summary>The SHA-1 of no bytes: the only hash a zero-size blob can have.</summary>
	public const string EmptyHash = "da39a3ee5e6b4b0d3255bfef95601890afd80709";
}

/// <summary>
/// A pack: a gzip stream of blobs laid end to end, served at <see cref="Url"/>. <see cref="Hash"/> is the SHA-1 of the
/// uncompressed stream; <see cref="CompressedSize"/> is what a download reads.
/// </summary>
internal sealed record GitDepsPack(string Hash, long Size, long CompressedSize, string RemotePath, string BaseUrl)
{
	public string Url => $"{BaseUrl.TrimEnd('/')}/{RemotePath}/{Hash}";
}

/// <summary>What is on disk for a manifest file.</summary>
internal enum GitDepsState
{
	/// <summary>Present with the manifest's hash.</summary>
	Present,

	/// <summary>
	/// Tracked, with the hash .uedependencies expected but not the manifest's: the engine was updated since GitDependencies
	/// wrote it, and GitDependencies would replace it without asking.
	/// </summary>
	Outdated,

	/// <summary>Tracked with a hash other than the one .uedependencies expected, or untracked with a hash other than the manifest's: a local change.</summary>
	Modified,

	/// <summary>Not on disk.</summary>
	Missing,
}

/// <summary>A manifest file and what is on disk for it; <see cref="Timestamp"/> is the write time (UTC ticks) seen before its hash was taken, 0 when missing.</summary>
internal sealed record GitDepsEntry(GitDepsFile File, GitDepsState State, bool Tracked, long Timestamp = 0);

/// <summary>A content hash that is not the one the manifest names, or a pack too short for its blobs. Not retried: the source is wrong.</summary>
internal sealed class GitDepsCorruptException(string message) : Exception(message);

/// <summary>Reading a pack's source (the network stream, or a cached pack) failed or stalled. Another try may not repeat it.</summary>
internal sealed class GitDepsSourceException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>Writing a staged file failed. Not retried: the next try would write to the same disk.</summary>
internal sealed class GitDepsWriteException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>
/// The engine's dependency manifests, read as Engine/Source/Programs/GitDependencies reads them (Program.cs, AddManifests and
/// AddPluginManifests): every &lt;root&gt;/&lt;folder&gt;/Build/*.gitdeps.xml, and each plugin's Build/*.gitdeps.xml under
/// &lt;root&gt;/&lt;folder&gt;/Plugins, its names prefixed with the plugin's folder. A later manifest's entry replaces an earlier one's.
/// </summary>
internal sealed class GitDepsManifests
{
	/// <summary>GitDependencies' default when a manifest names no BaseUrl (DependencyManifest.cs).</summary>
	public const string DefaultBaseUrl = "http://cdn.unrealengine.com/dependencies";

	readonly string RootPrefix;

	GitDepsManifests(string root)
	{
		string Full = Path.GetFullPath(root);
		RootPrefix = Path.EndsInDirectorySeparator(Full) ? Full : Full + Path.DirectorySeparatorChar;
	}

	public Dictionary<string, GitDepsFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<string, GitDepsBlob> Blobs { get; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<string, GitDepsPack> Packs { get; } = new(StringComparer.OrdinalIgnoreCase);
	public List<string> ManifestFiles { get; } = [];

	/// <summary>
	/// Reads every manifest under the root. Throws <see cref="UakSetupException"/> for one that can't be read, or that names a
	/// file outside the root.
	/// </summary>
	public static GitDepsManifests Read(string root)
	{
		GitDepsManifests Manifests = new(root);
		foreach (string BaseFolder in Directory.EnumerateDirectories(root))
		{
			Manifests.AddManifests(Path.Combine(BaseFolder, "Build"), "");
			Manifests.AddPluginManifests(Path.Combine(BaseFolder, "Plugins"), Path.GetFileName(BaseFolder) + "/Plugins");
		}
		return Manifests;
	}

	void AddPluginManifests(string pluginsFolder, string extractPrefix)
	{
		if (!Directory.Exists(pluginsFolder))
		{
			return;
		}
		if (Directory.EnumerateFiles(pluginsFolder, "*.uplugin").Any())
		{
			AddManifests(Path.Combine(pluginsFolder, "Build"), extractPrefix + "/");
			return;
		}
		foreach (string Subfolder in Directory.EnumerateDirectories(pluginsFolder))
		{
			string Name = Path.GetFileName(Subfolder);
			if (!Name.StartsWith('.'))
			{
				AddPluginManifests(Subfolder, extractPrefix + "/" + Name);
			}
		}
	}

	void AddManifests(string buildFolder, string extractPrefix)
	{
		if (!Directory.Exists(buildFolder))
		{
			return;
		}
		foreach (string ManifestFile in Directory.EnumerateFiles(buildFolder, "*.gitdeps.xml"))
		{
			// GitDependencies skips dotfiles, which a Mac writes on non-unix partitions.
			if (!Path.GetFileName(ManifestFile).StartsWith('.'))
			{
				ReadManifest(ManifestFile, extractPrefix);
			}
		}
	}

	void ReadManifest(string fileName, string extractPrefix)
	{
		try
		{
			// GitDependencies never reads the manifest's IgnoreProxy, so uak doesn't either.
			string BaseUrl = DefaultBaseUrl;
			using XmlReader Reader = XmlReader.Create(fileName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true });
			while (Reader.Read())
			{
				if (Reader.NodeType != XmlNodeType.Element)
				{
					continue;
				}
				if (Reader.Depth == 0 && Reader.LocalName == "DependencyManifest")
				{
					BaseUrl = Reader.GetAttribute("BaseUrl") ?? DefaultBaseUrl;
				}
				else if (Reader.Depth == 2 && Reader.LocalName == "File")
				{
					string Name = extractPrefix + Required(Reader, "Name");
					CheckName(fileName, Name);
					Files[Name] = new GitDepsFile(Name, Required(Reader, "Hash"), Reader.GetAttribute("IsExecutable") is string Executable && XmlConvert.ToBoolean(Executable), fileName);
				}
				else if (Reader.Depth == 2 && Reader.LocalName == "Blob")
				{
					string Hash = Required(Reader, "Hash");
					Blobs[Hash] = new GitDepsBlob(Hash, XmlConvert.ToInt64(Required(Reader, "Size")), Required(Reader, "PackHash"), XmlConvert.ToInt64(Required(Reader, "PackOffset")));
				}
				else if (Reader.Depth == 2 && Reader.LocalName == "Pack")
				{
					string Hash = Required(Reader, "Hash");
					Packs[Hash] = new GitDepsPack(Hash, XmlConvert.ToInt64(Required(Reader, "Size")), XmlConvert.ToInt64(Required(Reader, "CompressedSize")), Required(Reader, "RemotePath"), BaseUrl);
				}
			}
		}
		catch (Exception Error) when (Error is XmlException or FormatException or OverflowException or InvalidDataException or IOException)
		{
			throw new UakSetupException($"{fileName} is not a readable gitdeps manifest: {Error.Message}", Error);
		}
		ManifestFiles.Add(fileName);
	}

	/// <summary>
	/// Refuses a name that would be written outside the engine root: rooted, holding a '..' segment, or with a full path not
	/// under the root. GitDependencies doesn't check (it passes names to Path.Combine), so a bad plugin manifest could write
	/// anywhere.
	/// </summary>
	void CheckName(string manifest, string name)
	{
		string? Problem = null;
		if (name.Length == 0)
		{
			Problem = "is empty";
		}
		else if (Path.IsPathRooted(name) || name[0] is '/' or '\\')
		{
			Problem = "is rooted";
		}
		else if (name.Split('/', '\\').Contains(".."))
		{
			Problem = "holds a '..' segment";
		}
		else if (!Path.GetFullPath(Path.Combine(RootPrefix, name)).StartsWith(RootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
		{
			Problem = "has a full path outside the engine root";
		}
		if (Problem is not null)
		{
			throw new UakSetupException($"{manifest} lists the file '{name}', which {Problem}. uak refuses a manifest that could write outside the engine root; fix or remove that manifest.");
		}
	}

	static string Required(XmlReader reader, string attribute) =>
		reader.GetAttribute(attribute) ?? throw new InvalidDataException($"a {reader.LocalName} element has no {attribute} attribute");

	/// <summary>
	/// What is on disk for a file. A tracked file whose write time is the one the working manifest saved keeps the saved
	/// hash, as GitDependencies does (Program.cs:463-470); any other file is hashed. A tracked file with the hash
	/// .uedependencies expected, but not the manifest's, is outdated: GitDependencies replaces it without asking, as it only
	/// counts a file tampered with when its hash is not its ExpectedHash (Program.cs:565-569).
	/// </summary>
	public static GitDepsEntry Inspect(string root, GitDepsFile file, GitDepsWorking working)
	{
		working.Files.TryGetValue(file.Name, out GitDepsWorkingFile? WorkingFile);
		bool Tracked = WorkingFile is not null;
		FileInfo Info = new(Path.Combine(root, file.Name));
		if (!Info.Exists)
		{
			return new GitDepsEntry(file, GitDepsState.Missing, Tracked);
		}
		long Timestamp = Info.LastWriteTimeUtc.Ticks;
		string Hash = WorkingFile is { Hash: not null } && WorkingFile.Timestamp == Timestamp ? WorkingFile.Hash : HashFile(Info.FullName);
		GitDepsState State = SameHash(Hash, file.Hash) ? GitDepsState.Present
			: WorkingFile?.ExpectedHash is string Expected && SameHash(Hash, Expected) ? GitDepsState.Outdated
			: GitDepsState.Modified;
		return new GitDepsEntry(file, State, Tracked, Timestamp);
	}

	static bool SameHash(string first, string second) => first.Equals(second, StringComparison.OrdinalIgnoreCase);

	/// <summary>The SHA-1 of a file, lower-case hex, as GitDependencies writes hashes.</summary>
	public static string HashFile(string path)
	{
		using FileStream Stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		return Convert.ToHexString(SHA1.HashData(Stream)).ToLowerInvariant();
	}
}

/// <summary>
/// GitDependencies' working manifest (.uedependencies), declared as its WorkingManifest.cs declares it, so XmlSerializer
/// reads and writes the same XML. Public only because XmlSerializer needs public types.
/// </summary>
[XmlRoot("WorkingManifest")]
public sealed class GitDepsWorkingManifest
{
	/// <summary>Every file GitDependencies tracks.</summary>
	[XmlArrayItem("File")]
	public List<GitDepsWorkingFile> Files { get; } = [];
}

/// <summary>A file in GitDependencies' working manifest: the hash it saw at <see cref="Timestamp"/> (UTC ticks), and the hash it wanted.</summary>
public sealed class GitDepsWorkingFile
{
	/// <summary>The file's path from the engine root, as the gitdeps manifest names it.</summary>
	[XmlAttribute]
	public string? Name { get; set; }

	/// <summary>The SHA-1 GitDependencies saw on disk at <see cref="Timestamp"/>.</summary>
	[XmlAttribute]
	public string? Hash { get; set; }

	/// <summary>The SHA-1 the gitdeps manifest named when GitDependencies wrote the entry.</summary>
	[XmlAttribute]
	public string? ExpectedHash { get; set; }

	/// <summary>The file's last write time (UTC ticks) when <see cref="Hash"/> was taken.</summary>
	[XmlAttribute]
	public long Timestamp { get; set; }
}

/// <summary>The working manifest at an engine root, as read for status and fetch.</summary>
internal sealed class GitDepsWorking
{
	/// <summary>GitDependencies' working manifest at the root (Program.cs, ManifestFilename).</summary>
	public const string FileName = ".uedependencies";

	/// <summary>The name older engines used (Program.cs, LegacyManifestFilename); uak reads it but never writes it.</summary>
	public const string LegacyFileName = ".ue4dependencies";

	static readonly XmlSerializer Serializer = new(typeof(GitDepsWorkingManifest));

	/// <summary>The file read, or null when there is none.</summary>
	public string? Path { get; private init; }

	/// <summary>Whether <see cref="Path"/> is the legacy .ue4dependencies.</summary>
	public bool IsLegacy { get; private init; }

	/// <summary>Why <see cref="Path"/> couldn't be read, or null. Every file then counts as untracked.</summary>
	public string? Error { get; private init; }

	/// <summary>The tracked files by name, compared case-sensitively as GitDependencies does (Program.cs:457).</summary>
	public IReadOnlyDictionary<string, GitDepsWorkingFile> Files { get; private init; } = new Dictionary<string, GitDepsWorkingFile>(StringComparer.Ordinal);

	/// <summary>
	/// Reads the working manifest GitDependencies would (Program.cs:403-413): .uedependencies, else the legacy
	/// .ue4dependencies. An unreadable one is reported in <see cref="Error"/>, as GitDependencies then starts afresh.
	/// </summary>
	public static GitDepsWorking Read(string root)
	{
		string Main = System.IO.Path.Combine(root, FileName);
		string Legacy = System.IO.Path.Combine(root, LegacyFileName);
		string? Chosen = File.Exists(Main) ? Main : File.Exists(Legacy) ? Legacy : null;
		if (Chosen is null)
		{
			return new GitDepsWorking();
		}
		try
		{
			Dictionary<string, GitDepsWorkingFile> Files = new(StringComparer.Ordinal);
			foreach (GitDepsWorkingFile Each in ReadManifest(Chosen).Files)
			{
				if (Each.Name is not null)
				{
					Files.TryAdd(Each.Name, Each);
				}
			}
			return new GitDepsWorking { Path = Chosen, IsLegacy = Chosen == Legacy, Files = Files };
		}
		catch (Exception Error) when (IsManifestError(Error))
		{
			return new GitDepsWorking { Path = Chosen, IsLegacy = Chosen == Legacy, Error = Error.InnerException?.Message is string Inner ? $"{Error.Message} {Inner}" : Error.Message };
		}
	}

	/// <summary>Whether an exception is one <see cref="ReadManifest"/> or <see cref="WriteManifest"/> throws for a file it can't read or write.</summary>
	public static bool IsManifestError(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException or XmlException;

	/// <summary>
	/// Reads a working manifest as GitDependencies does (ReadXmlObject, Program.cs:1451-1468), refusing a DTD. The file is
	/// opened sharing read, write and delete, so a reader never makes a writer's delete of it fail, uak's or GitDependencies'.
	/// </summary>
	public static GitDepsWorkingManifest ReadManifest(string path)
	{
		using FileStream Stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using StreamReader Text = new(Stream);
		using XmlReader Reader = XmlReader.Create(Text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
		return (GitDepsWorkingManifest?)Serializer.Deserialize(Reader) ?? new GitDepsWorkingManifest();
	}

	/// <summary>How many times <see cref="WriteManifest"/> tries to delete the old manifest, <see cref="DeleteRetryWait"/> apart: a reader's handle is the likely cause of a failure.</summary>
	const int DeleteTries = 5;

	static readonly TimeSpan DeleteRetryWait = TimeSpan.FromMilliseconds(100);

	/// <summary>
	/// Writes a working manifest as GitDependencies does (WriteWorkingManifest and WriteXmlObject, Program.cs:1470-1511): the
	/// XML to &lt;path&gt;.tmp through XmlSerializer (UTF-8 without a BOM, indented), the tmp file marked hidden, the old file
	/// deleted, the tmp file moved into place. A tmp file already there is stale (GitDependencies only uses one when the
	/// manifest itself is missing, Program.cs:412-416), so it is deleted first. If a step fails while the old manifest is
	/// still there, the tmp file is deleted before the failure is thrown: beside the manifest GitDependencies never uses it,
	/// and its own next write can fail on a hidden file. When that delete fails too, a <see cref="GitDepsLeftoverException"/>
	/// names it. If this fails after the old manifest's delete, the tmp file holds the new manifest, and GitDependencies'
	/// next run moves it into place.
	/// </summary>
	/// <param name="path">The manifest.</param>
	/// <param name="manifest">What to write.</param>
	/// <param name="deleteFile">Deletes the old manifest and, on a failure, the tmp file: <see cref="File.Delete"/> unless a test fails it.</param>
	public static void WriteManifest(string path, GitDepsWorkingManifest manifest, Action<string>? deleteFile = null)
	{
		Action<string> Delete = deleteFile ?? File.Delete;
		string Temporary = path + ".tmp";
		if (File.Exists(Temporary))
		{
			File.SetAttributes(Temporary, FileAttributes.Normal);
			File.Delete(Temporary);
		}
		try
		{
			using (StreamWriter Text = new(Temporary))
			using (XmlWriter Writer = XmlWriter.Create(Text, new XmlWriterSettings { Indent = true }))
			{
				Serializer.Serialize(Writer, manifest);
			}
			File.SetAttributes(Temporary, File.GetAttributes(Temporary) | FileAttributes.Hidden);
			DeleteWithRetries(path, Delete);
		}
		catch (Exception Error) when (File.Exists(path))
		{
			try
			{
				if (File.Exists(Temporary))
				{
					File.SetAttributes(Temporary, FileAttributes.Normal);
					Delete(Temporary);
				}
			}
			catch (Exception Cleanup) when (Cleanup is IOException or UnauthorizedAccessException)
			{
				throw new GitDepsLeftoverException(Temporary, Cleanup.Message, Error);
			}
			throw;
		}
		File.Move(Temporary, path);
	}

	static void DeleteWithRetries(string path, Action<string> delete)
	{
		for (int Try = 1; ; Try++)
		{
			try
			{
				delete(path);
				return;
			}
			catch (Exception Error) when (Try < DeleteTries && Error is IOException or UnauthorizedAccessException)
			{
				Thread.Sleep(DeleteRetryWait);
			}
		}
	}
}

/// <summary>
/// Writing .uedependencies failed (<see cref="Exception.InnerException"/>), and its .tmp file, which GitDependencies never
/// uses beside the manifest and may fail to write over, could not be deleted either.
/// </summary>
internal sealed class GitDepsLeftoverException(string path, string reason, Exception innerException) : IOException($"{path} could not be deleted ({reason})", innerException)
{
	/// <summary>The .tmp file left behind.</summary>
	public string Path { get; } = path;

	/// <summary>Why it could not be deleted.</summary>
	public string Reason { get; } = reason;
}

/// <summary>
/// The lock that lets one <c>uak gitdeps fetch</c> write to an engine at a time: a named system mutex, named as the editor
/// lock's is (<c>EditorLock.GetMutexName</c>): from the engine root folder's identity, else its canonical path
/// (<see cref="CanonicalPath"/>), so every spelling of the folder (a junction, a link, a subst drive, a short name, another
/// case) gets one lock, in the <c>Global\</c> namespace on every OS, so it covers every session (on Unix, an unprefixed
/// name is per session, and each shell has its own).
/// A mutex belongs to the thread that took it, and a fetch's awaits change thread, so a thread of its own takes it, holds it
/// and releases it. A mutex a killed fetch left (abandoned) counts as taken by the new holder.
/// </summary>
internal sealed class GitDepsFetchLock : IDisposable
{
	readonly ManualResetEventSlim ReleaseRequested;
	readonly Thread Holder;

	GitDepsFetchLock(ManualResetEventSlim releaseRequested, Thread holder, bool wasAbandoned)
	{
		ReleaseRequested = releaseRequested;
		Holder = holder;
		WasAbandoned = wasAbandoned;
	}

	/// <summary>Whether the last holder ended without releasing it: a fetch was killed.</summary>
	public bool WasAbandoned { get; }

	/// <summary>The base of the mutex's name, made unique per engine by <see cref="NameFor"/>.</summary>
	public const string MutexBaseName = "UnrealAgentKit_GitDepsFetch";

	/// <summary>
	/// The mutex's name for an engine root: <see cref="MutexBaseName"/> made unique for the root folder's identity, else its
	/// canonical path, through <c>GlobalSingleInstanceMutex.GetUniqueMutexForPath</c> (<c>Global\</c>, the key upper-cased
	/// and hashed), as <c>EditorLock.GetScope</c> and <c>EditorLock.GetMutexName</c> key the editor lock.
	/// </summary>
	public static string NameFor(string root)
	{
		string Canonical = CanonicalPath.Get(root);
		return UnrealBuildBase.GlobalSingleInstanceMutex.GetUniqueMutexForPath(MutexBaseName, CanonicalPath.TryGetIdentity(Canonical) ?? Canonical);
	}

	/// <summary>
	/// Takes the lock without waiting: null when another holder has it. On Windows, a mutex another user's fetch holds can't
	/// be opened, which counts as held too. Throws <see cref="UakSetupException"/> when the mutex can't be made.
	/// </summary>
	public static GitDepsFetchLock? TryTake(string root)
	{
		string Name = NameFor(root);
		ManualResetEventSlim Release = new();
		TaskCompletionSource<(bool Taken, bool Abandoned)> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread Holder = new(() =>
		{
			Mutex Lock;
			bool Taken;
			bool Abandoned = false;
			try
			{
				Lock = new Mutex(false, Name);
			}
			catch (UnauthorizedAccessException)
			{
				Result.SetResult((false, false));
				return;
			}
			catch (Exception Error)
			{
				Result.SetException(new UakSetupException($"the fetch lock {Name} could not be made: {Error.Message}", Error));
				return;
			}
			using (Lock)
			{
				try
				{
					Taken = Lock.WaitOne(0);
				}
				catch (AbandonedMutexException)
				{
					Taken = true;
					Abandoned = true;
				}
				Result.SetResult((Taken, Abandoned));
				if (Taken)
				{
					Release.Wait();
					Lock.ReleaseMutex();
				}
			}
		})
		{
			IsBackground = true,
			Name = "uak gitdeps fetch lock",
		};
		Holder.Start();
		(bool Taken, bool Abandoned) = Result.Task.GetAwaiter().GetResult();
		if (!Taken)
		{
			Holder.Join();
			Release.Dispose();
			return null;
		}
		return new GitDepsFetchLock(Release, Holder, Abandoned);
	}

	int Disposed;

	/// <summary>Releases the lock, on its own thread, and waits for that. A second call does nothing.</summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref Disposed, 1) != 0)
		{
			return;
		}
		ReleaseRequested.Set();
		Holder.Join();
		ReleaseRequested.Dispose();
	}
}

/// <summary>
/// A path or glob naming manifest files, from the engine root. Forward or back slashes; case-insensitive. '*' matches within
/// one folder, '**' across folders, '?' one character. Without a wildcard it names a file, or a folder and everything under it.
/// An absolute path must be under the root.
/// </summary>
internal sealed class GitDepsPattern
{
	readonly Regex? Glob;
	readonly string? Exact;

	GitDepsPattern(string text, Regex? glob, string? exact)
	{
		Text = text;
		Glob = glob;
		Exact = exact;
	}

	/// <summary>The pattern as given.</summary>
	public string Text { get; }

	/// <summary>Parses a pattern; a usage error for an empty one or an absolute path outside the root.</summary>
	public static GitDepsPattern Parse(string text, string root)
	{
		string Relative = text;
		if (Path.IsPathFullyQualified(text))
		{
			Relative = Path.GetRelativePath(root, text);
			if (Relative == ".." || Relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathFullyQualified(Relative))
			{
				throw new UakUsageException($"'{text}' is not under the engine root {root}.");
			}
		}
		Relative = Relative.Replace('\\', '/');
		while (Relative.StartsWith("./", StringComparison.Ordinal))
		{
			Relative = Relative[2..];
		}
		Relative = Relative.Trim('/');
		if (Relative.Length == 0 || Relative == ".")
		{
			throw new UakUsageException($"'{text}' names no file: give a path or glob from the engine root, such as 'Engine/Binaries/ThirdParty/**/*.dll'.");
		}
		if (Relative.IndexOfAny(['*', '?']) < 0)
		{
			return new GitDepsPattern(text, null, Relative);
		}
		StringBuilder Expression = new("^");
		for (int Index = 0; Index < Relative.Length; Index++)
		{
			char Character = Relative[Index];
			if (Character == '*' && Index + 1 < Relative.Length && Relative[Index + 1] == '*')
			{
				// "**/" also matches no folder at all; a bare "**" matches anything.
				bool FolderForm = Index + 2 < Relative.Length && Relative[Index + 2] == '/';
				Expression.Append(FolderForm ? "(?:.*/)?" : ".*");
				Index += FolderForm ? 2 : 1;
			}
			else if (Character == '*')
			{
				Expression.Append("[^/]*");
			}
			else if (Character == '?')
			{
				Expression.Append("[^/]");
			}
			else
			{
				Expression.Append(Regex.Escape(Character.ToString()));
			}
		}
		Expression.Append('$');
		return new GitDepsPattern(text, new Regex(Expression.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), null);
	}

	/// <summary>Whether the pattern names a manifest file.</summary>
	public bool Matches(string name) => Glob is not null
		? Glob.IsMatch(name)
		: name.Equals(Exact, StringComparison.OrdinalIgnoreCase) || name.StartsWith(Exact + "/", StringComparison.OrdinalIgnoreCase);

	/// <summary>The files the patterns name, sorted; a usage error naming each pattern that matches none.</summary>
	public static IReadOnlyList<GitDepsFile> Select(GitDepsManifests manifests, IReadOnlyList<GitDepsPattern> patterns, string root)
	{
		SortedDictionary<string, GitDepsFile> Selected = new(StringComparer.OrdinalIgnoreCase);
		List<string> Unmatched = [];
		foreach (GitDepsPattern Pattern in patterns)
		{
			bool Any = false;
			foreach (GitDepsFile File in manifests.Files.Values)
			{
				if (Pattern.Matches(File.Name))
				{
					Selected[File.Name] = File;
					Any = true;
				}
			}
			if (!Any)
			{
				Unmatched.Add(Pattern.Text);
			}
		}
		if (Unmatched.Count > 0)
		{
			throw new UakUsageException($"{string.Join(", ", Unmatched.Select(Text => "'" + Text + "'"))} {(Unmatched.Count == 1 ? "matches" : "match")} no file in the {manifests.ManifestFiles.Count} gitdeps manifest(s) under {root}.");
		}
		return [.. Selected.Values];
	}
}

/// <summary>Reads blobs out of a pack stream.</summary>
internal static class GitDepsPackReader
{
	/// <summary>
	/// Reads the gzip pack stream and writes each blob, sorted by offset then size, to the file <paramref name="outputFor"/>
	/// names, checking its SHA-1 against the blob's hash. Zero-size blobs need no pack bytes, so they are skipped here (the
	/// caller writes them); a real overlap is refused. Stops after the last blob, so the rest of the pack is not read. Throws
	/// <see cref="GitDepsCorruptException"/> for a wrong hash, an overlap or a pack that ends early, and
	/// <see cref="GitDepsWriteException"/>, naming the path, when a file can't be written. Errors reading
	/// <paramref name="compressed"/> pass through as it throws them. The caller deletes what was written.
	/// </summary>
	public static async Task ExtractAsync(Stream compressed, IReadOnlyList<GitDepsBlob> blobs, Func<GitDepsBlob, string> outputFor, CancellationToken cancellationToken)
	{
		using GZipStream Pack = new(compressed, CompressionMode.Decompress, leaveOpen: true);
		byte[] Buffer = new byte[81920];
		long Position = 0;
		foreach (GitDepsBlob Blob in blobs.Where(Blob => Blob.Size > 0).OrderBy(Blob => Blob.PackOffset).ThenBy(Blob => Blob.Size))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (Blob.PackOffset < Position)
			{
				throw new GitDepsCorruptException($"blob {Blob.Hash} at offset {Blob.PackOffset} of pack {Blob.PackHash} overlaps the blob before it");
			}
			// Skip to the blob.
			while (Position < Blob.PackOffset)
			{
				int Read = await Pack.ReadAsync(Buffer.AsMemory(0, (int)Math.Min(Buffer.Length, Blob.PackOffset - Position)), cancellationToken).ConfigureAwait(false);
				if (Read == 0)
				{
					throw new GitDepsCorruptException($"pack {Blob.PackHash} ended at byte {Position}, before blob {Blob.Hash} at {Blob.PackOffset}");
				}
				Position += Read;
			}
			string Output = outputFor(Blob);
			using IncrementalHash Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
			FileStream Stream = Writing(Output, () => new FileStream(Output, FileMode.CreateNew, FileAccess.Write, FileShare.None));
			await using (Stream.ConfigureAwait(false))
			{
				long Left = Blob.Size;
				while (Left > 0)
				{
					int Read = await Pack.ReadAsync(Buffer.AsMemory(0, (int)Math.Min(Buffer.Length, Left)), cancellationToken).ConfigureAwait(false);
					if (Read == 0)
					{
						throw new GitDepsCorruptException($"pack {Blob.PackHash} ended at byte {Position}, inside blob {Blob.Hash} ({Blob.Size} bytes at {Blob.PackOffset})");
					}
					Hasher.AppendData(Buffer, 0, Read);
					try
					{
						await Stream.WriteAsync(Buffer.AsMemory(0, Read), cancellationToken).ConfigureAwait(false);
					}
					catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
					{
						throw new GitDepsWriteException($"writing {Output} failed: {Error.Message}", Error);
					}
					Left -= Read;
					Position += Read;
				}
				try
				{
					await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
				{
					throw new GitDepsWriteException($"writing {Output} failed: {Error.Message}", Error);
				}
			}
			string Hash = Convert.ToHexString(Hasher.GetHashAndReset()).ToLowerInvariant();
			if (!Hash.Equals(Blob.Hash, StringComparison.OrdinalIgnoreCase))
			{
				throw new GitDepsCorruptException($"blob {Blob.Hash} from pack {Blob.PackHash} has SHA-1 {Hash}");
			}
		}
	}

	/// <summary>Runs a file-system step for a staged file, turning its failure into a <see cref="GitDepsWriteException"/> naming the path.</summary>
	internal static T Writing<T>(string path, Func<T> step)
	{
		try
		{
			return step();
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			throw new GitDepsWriteException($"writing {path} failed: {Error.Message}", Error);
		}
	}
}

/// <summary>
/// A pack's source stream: every read must return within an idle time, and all of them before a deadline. A failure,
/// stall or deadline becomes a <see cref="GitDepsSourceException"/>, which a fetch tries again; the caller's own
/// cancellation passes through unchanged.
/// </summary>
internal sealed class GitDepsSourceStream(Stream inner, string source, TimeSpan idleTimeout, TimeSpan transferTimeout, CancellationToken deadline) : Stream
{
	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		using CancellationTokenSource Idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline);
		Idle.CancelAfter(idleTimeout);
		try
		{
			return await inner.ReadAsync(buffer, Idle.Token).ConfigureAwait(false);
		}
		catch (Exception Error) when (!cancellationToken.IsCancellationRequested)
		{
			string Why = Error is not OperationCanceledException ? Error.Message
				: deadline.IsCancellationRequested ? $"the transfer took longer than {transferTimeout.TotalSeconds:0.###} s, GitDependencies' limit for it"
				: $"no data for {idleTimeout.TotalSeconds:0.###} s";
			throw new GitDepsSourceException($"reading {source} failed: {Why}", Error);
		}
	}

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

	public override void Flush()
	{
	}

	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Settings GitDependencies reads that a fetch honours: the proxy, the download cache and the transfer time limit.</summary>
internal sealed class GitDepsSettings
{
	/// <summary>The longest a read of a pack may wait for data: HttpClient's default timeout, GitDependencies' floor (Program.cs:1278).</summary>
	public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(100);

	/// <summary>The proxy, without its user info, for every URL; null to use .NET's default.</summary>
	public Uri? Proxy { get; init; }

	/// <summary>The proxy's credentials, from its URL or UE_GITDEPS_ARGS' -proxy-user= and -proxy-password=; never printed.</summary>
	public NetworkCredential? ProxyCredentials { get; init; }

	/// <summary>Where <see cref="Proxy"/> came from, for the output.</summary>
	public string ProxyFrom { get; init; } = "";

	/// <summary>The GitDependencies download cache to read packs from, or null.</summary>
	public string? CacheDirectory { get; init; }

	/// <summary>Where <see cref="CacheDirectory"/> came from, for the output.</summary>
	public string CacheFrom { get; init; } = "none";

	/// <summary>GitDependencies' -http-timeout-multiplier= (UE_GITDEPS_ARGS), default 1.</summary>
	public double HttpTimeoutMultiplier { get; init; } = 1.0;

	/// <summary>GitDependencies' -threads= (UE_GITDEPS_ARGS), default max(4, a quarter of the processors); it scales the time limit.</summary>
	public int Threads { get; init; } = DefaultThreads;

	static int DefaultThreads => Math.Max(4, (int)(Environment.ProcessorCount * .25));

	/// <summary>The proxy for the output: scheme, host and port, never credentials.</summary>
	public string ProxyDescription => Proxy is null
		? ".NET default (no -proxy=, and none in UE_GITDEPS_ARGS, HTTP_PROXY or http_proxy)"
		: $"{Proxy.Scheme}://{Proxy.Host}:{Proxy.Port}{(ProxyCredentials is null ? "" : " (with credentials)")} (from {ProxyFrom})";

	/// <summary>
	/// GitDependencies' time limit for a download (CreateHttpClient, Program.cs:1265-1280): HttpClient's 100 s, or the time
	/// the largest pack takes at 2 Mbit/s times -http-timeout-multiplier= times -threads= (Program.cs:111), whichever is
	/// longer. uak reads one pack at a time, so this is generous.
	/// </summary>
	public TimeSpan TransferTimeout(long largestCompressedSize) =>
		TimeSpan.FromSeconds(Math.Min(Math.Max(DefaultIdleTimeout.TotalSeconds, largestCompressedSize / 250000.0 * HttpTimeoutMultiplier * Threads), int.MaxValue / 1000.0));

	/// <summary>
	/// Resolves them as GitDependencies does (Program.cs, Main): an option given wins, then the same option in
	/// UE_GITDEPS_ARGS. The proxy then falls back to HTTP_PROXY, then http_proxy, for every URL (Program.cs:161-185); the
	/// cache to UE_GITDEPS, then .git/ue-gitdeps (or a not yet migrated .git/ue4-gitdeps) in the nearest folder at or above
	/// the root that has a .git folder (Program.cs:116-147). -no-cache turns the cache off.
	/// </summary>
	public static GitDepsSettings Resolve(string root, string? proxy, string? cache, bool noCache, Func<string, string?> environment)
	{
		List<string> Defaults = SplitArguments(environment("UE_GITDEPS_ARGS"));
		string? Default(string prefix) => Defaults.FirstOrDefault(Argument => Argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

		(string? ProxyText, string ProxyFrom) = proxy is not null ? (proxy, "-proxy=") : (Default("-proxy="), "UE_GITDEPS_ARGS");
		if (string.IsNullOrEmpty(ProxyText))
		{
			(ProxyText, ProxyFrom) = environment("HTTP_PROXY") is { Length: > 0 } Upper ? (Upper, "HTTP_PROXY") : (environment("http_proxy"), "http_proxy");
		}
		Uri? Proxy = null;
		NetworkCredential? Credentials = null;
		if (!string.IsNullOrEmpty(ProxyText))
		{
			if (!Uri.TryCreate(ProxyText.Contains("://", StringComparison.Ordinal) ? ProxyText : "http://" + ProxyText, UriKind.Absolute, out Uri? Parsed) || string.IsNullOrEmpty(Parsed.Host))
			{
				// The value is never echoed: a password with an unencoded '/', '?' or '#' is what makes it fail to parse.
				throw new UakUsageException($"the proxy from {ProxyFrom} is not a URL; check its value there (not shown, as it may hold a password).");
			}
			// As GitDependencies does: user:password in the URL, else -proxy-user= and -proxy-password= (MakeCredentialsFromUri).
			string UserInfo = Parsed.UserInfo;
			int Colon = UserInfo.IndexOf(':', StringComparison.Ordinal);
			string User = Colon >= 0 ? Uri.UnescapeDataString(UserInfo[..Colon]) : Uri.UnescapeDataString(UserInfo);
			string Password = Colon >= 0 ? Uri.UnescapeDataString(UserInfo[(Colon + 1)..]) : "";
			User = User.Length > 0 ? User : Default("-proxy-user=") ?? "";
			Password = Password.Length > 0 ? Password : Default("-proxy-password=") ?? "";
			if (User.Length > 0 && Password.Length > 0)
			{
				Credentials = new NetworkCredential(User, Password);
			}
			Proxy = new UriBuilder(Parsed) { UserName = "", Password = "" }.Uri;
		}

		string? CacheDirectory = null;
		string CacheFrom = "none (-no-cache)";
		if (!noCache)
		{
			// As GitDependencies: UE_GITDEPS_ARGS' -cache= replaces UE_GITDEPS even when empty, and an empty value falls back to
			// the .git lookup (Program.cs:119-141, ParseParameter at :301-318). uak's own -cache= wins over both.
			string? FromArguments = Default("-cache=");
			(CacheDirectory, CacheFrom) = cache is not null ? (cache, "-cache=")
				: FromArguments is { Length: > 0 } ? (FromArguments, "UE_GITDEPS_ARGS")
				: FromArguments is not null ? WithReason(FindGitCache(root), "UE_GITDEPS_ARGS has an empty -cache=")
				: environment("UE_GITDEPS") is { Length: > 0 } FromVariable ? (FromVariable, "UE_GITDEPS")
				: FindGitCache(root);
			if (CacheDirectory is not null)
			{
				CacheDirectory = Path.GetFullPath(CacheDirectory);
			}
		}

		NumberFormatInfo UserFormat = UserNumberFormat();
		double Multiplier = Defaults.Select(Argument => ParseFloat(Argument, "-http-timeout-multiplier=", UserFormat)).FirstOrDefault(Value => Value is not null) ?? 1.0;
		int Threads = Defaults.Select(Argument => ParseInt(Argument, "-threads=")).FirstOrDefault(Value => Value is not null) ?? DefaultThreads;
		return new GitDepsSettings
		{
			Proxy = Proxy,
			ProxyCredentials = Credentials,
			ProxyFrom = ProxyFrom,
			CacheDirectory = CacheDirectory,
			CacheFrom = CacheFrom,
			HttpTimeoutMultiplier = Multiplier,
			Threads = Threads,
		};
	}

	static (string?, string) WithReason((string? Folder, string From) found, string reason) => (found.Folder, $"{found.From} ({reason})");

	/// <summary>
	/// Parses a number as GitDependencies' ParseFloatParameter does (Program.cs:340-352): float.TryParse's default styles
	/// (Float and AllowThousands) in the user's culture, so a comma-decimal locale reads "1,5" as 1.5.
	/// </summary>
	internal static float? ParseFloat(string argument, string prefix, NumberFormatInfo format) =>
		argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && float.TryParse(argument[prefix.Length..], NumberStyles.Float | NumberStyles.AllowThousands, format, out float Value) ? Value : null;

	/// <summary>
	/// The user's decimal and group separators, which GitDependencies' current culture parses with. uak runs with invariant
	/// globalization, so on Windows they are read where .NET's user overrides come from (HKCU\Control Panel\International);
	/// elsewhere, and when they can't be read, the invariant culture's.
	/// </summary>
	static NumberFormatInfo UserNumberFormat()
	{
		NumberFormatInfo Format = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
		if (!OperatingSystem.IsWindows())
		{
			return Format;
		}
		try
		{
			using Microsoft.Win32.RegistryKey? International = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
			if (International?.GetValue("sDecimal") is string { Length: > 0 } Decimal)
			{
				Format.NumberDecimalSeparator = Decimal;
			}
			if (International?.GetValue("sThousand") is string { Length: > 0 } Group)
			{
				Format.NumberGroupSeparator = Group;
			}
		}
		catch (Exception Error) when (Error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
		{
		}
		return Format;
	}

	static int? ParseInt(string argument, string prefix) =>
		argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(argument[prefix.Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int Value) ? Value : null;

	static (string?, string) FindGitCache(string root)
	{
		for (DirectoryInfo? Folder = new(root); Folder is not null; Folder = Folder.Parent)
		{
			string Git = Path.Combine(Folder.FullName, ".git");
			if (Directory.Exists(Git))
			{
				// GitDependencies moves .git/ue4-gitdeps to .git/ue-gitdeps; uak never writes the cache, so it reads the old name.
				string Current = Path.Combine(Git, "ue-gitdeps");
				string Old = Path.Combine(Git, "ue4-gitdeps");
				return !Directory.Exists(Current) && Directory.Exists(Old) ? (Old, ".git/ue4-gitdeps, not yet migrated") : (Current, ".git/ue-gitdeps");
			}
		}
		return (null, "none (no .git folder at or above the root)");
	}

	/// <summary>The cached copy of a pack (GitDependencies' &lt;cache&gt;/&lt;first two hash characters&gt;/&lt;hash&gt;, the compressed pack), or null.</summary>
	public string? CachedPack(GitDepsPack pack)
	{
		if (CacheDirectory is null)
		{
			return null;
		}
		string Cached = Path.Combine(CacheDirectory, pack.Hash[..Math.Min(2, pack.Hash.Length)], pack.Hash);
		return File.Exists(Cached) ? Cached : null;
	}

	/// <summary>Splits UE_GITDEPS_ARGS as GitDependencies does (whitespace, double quotes), turning "--x" into "-x".</summary>
	internal static List<string> SplitArguments(string? text)
	{
		List<string> Arguments = [];
		if (string.IsNullOrEmpty(text))
		{
			return Arguments;
		}
		for (int Index = 0; Index < text.Length; Index++)
		{
			if (char.IsWhiteSpace(text[Index]))
			{
				continue;
			}
			StringBuilder Argument = new();
			for (bool InQuotes = false; Index < text.Length; Index++)
			{
				if (!InQuotes && char.IsWhiteSpace(text[Index]))
				{
					break;
				}
				if (text[Index] == '"')
				{
					InQuotes ^= true;
				}
				else
				{
					Argument.Append(text[Index]);
				}
			}
			string Value = Argument.ToString();
			Arguments.Add(Value.StartsWith("--", StringComparison.Ordinal) ? Value[1..] : Value);
		}
		return Arguments;
	}
}

/// <summary>Strips credentials from text uak prints.</summary>
internal static class GitDepsRedact
{
	/// <summary>
	/// A URL without its user info (user:password@): everything from after "://" (or the start) to the last '@' in the whole
	/// string is cut, so a password holding an unencoded '/', '?' or '#', which makes the URL fail to parse, is cut too. A URL
	/// with an '@' in its path loses more than its user info, which is safe.
	/// </summary>
	public static string Url(string url)
	{
		int Start = url.IndexOf("://", StringComparison.Ordinal);
		Start = Start < 0 ? 0 : Start + 3;
		int At = url.LastIndexOf('@');
		return At < Start ? url : url[..Start] + url[(At + 1)..];
	}
}
