// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Unreal;

/// <summary>
/// The binaries a build of a target can write, for <c>uak mutate</c> to tell which ones its mutant build wrote: the files
/// that load as code (.dll, .exe, .so, .dylib, and any file the receipt lists as an executable or a library) in each folder
/// that holds one of the target's build products, as its receipt lists them (<c>Binaries/&lt;Platform&gt;/&lt;Target&gt;.target</c>,
/// which UBT writes at the end of each build that succeeds), in the project's own <c>Binaries/&lt;Platform&gt;</c>, and in
/// each project plugin's. The receipt may be older than the build it is asked about: a plugin that build linked for the
/// first time isn't in it, so the list is read again after the build (<see cref="Union"/>), and checked against what the
/// build says it linked (<see cref="FindUnwritten"/>).
/// </summary>
/// <param name="Folders">The folders, full paths.</param>
/// <param name="Listed">The code files the receipt lists by name (an executable without an extension among them).</param>
internal sealed record TargetBinaries(IReadOnlyList<string> Folders, IReadOnlySet<string> Listed)
{
	/// <summary>The receipt's build product types that load as code.</summary>
	static readonly HashSet<string> CodeTypes = new(StringComparer.OrdinalIgnoreCase) { "Executable", "DynamicLibrary" };

	/// <summary>The files a link action can write that no process loads: import libraries and archives, which what they go into shows.</summary>
	static readonly HashSet<string> LibraryExtensions = new(StringComparer.OrdinalIgnoreCase) { ".lib", ".a", ".exp", ".tbd" };

	/// <summary>How deep under <c>Plugins</c> a plugin is looked for.</summary>
	const int MaxPluginDepth = 8;

	/// <summary>How paths compare on this machine's file system (Windows and Mac ignore case).</summary>
	static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

	/// <summary>
	/// The receipt of a project's target (UE's TargetReceipt.GetDefaultPath: <c>&lt;Target&gt;.target</c> for Development,
	/// else <c>&lt;Target&gt;-&lt;Platform&gt;-&lt;Configuration&gt;.target</c>; a platform that puts its architecture in file names
	/// isn't looked for, so it reads as no receipt).
	/// </summary>
	public static string ReceiptPath(string projectDirectory, string target, UnrealPlatform platform, string configuration)
	{
		string Name = configuration.Equals("Development", StringComparison.OrdinalIgnoreCase) ? $"{target}.target" : $"{target}-{platform.Name}-{configuration}.target";
		return Path.Combine(projectDirectory, "Binaries", platform.Name, Name);
	}

	/// <summary>
	/// The target's binaries, from its receipt and the project's plugins; null when there is no receipt, or it can't be read
	/// or lists no build product, or the plugins can't be listed: then which binaries a build writes isn't known.
	/// </summary>
	public static TargetBinaries? Find(string projectDirectory, string engineDirectory, string target, UnrealPlatform platform, string configuration)
	{
		string Receipt = ReceiptPath(projectDirectory, target, platform, configuration);
		string? Text = StateFiles.ReadShared(Receipt);
		if (string.IsNullOrWhiteSpace(Text))
		{
			return null;
		}
		List<string> Folders = [Path.GetFullPath(Path.Combine(projectDirectory, "Binaries", platform.Name))];
		HashSet<string> Listed = new(PathComparer);
		try
		{
			using JsonDocument Document = JsonDocument.Parse(Text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
			if (Document.RootElement.ValueKind != JsonValueKind.Object || !Document.RootElement.TryGetProperty("BuildProducts", out JsonElement Products)
				|| Products.ValueKind != JsonValueKind.Array)
			{
				return null;
			}
			foreach (JsonElement Product in Products.EnumerateArray())
			{
				if (Product.ValueKind != JsonValueKind.Object || !Product.TryGetProperty("Path", out JsonElement PathElement) || PathElement.ValueKind != JsonValueKind.String)
				{
					return null;
				}
				string Full = Path.GetFullPath(PathElement.GetString()!
					.Replace("$(EngineDir)", engineDirectory, StringComparison.OrdinalIgnoreCase)
					.Replace("$(ProjectDir)", projectDirectory, StringComparison.OrdinalIgnoreCase));
				string Folder = Path.GetDirectoryName(Full)!;
				if (!Folders.Contains(Folder, PathComparer))
				{
					Folders.Add(Folder);
				}
				if (Product.TryGetProperty("Type", out JsonElement TypeElement) && TypeElement.ValueKind == JsonValueKind.String && CodeTypes.Contains(TypeElement.GetString()!))
				{
					Listed.Add(Full);
				}
			}
			if (Products.GetArrayLength() == 0)
			{
				return null;
			}
		}
		catch (Exception Exception) when (Exception is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
		return AddPluginFolders(projectDirectory, platform, Folders) ? new TargetBinaries(Folders, Listed) : null;
	}

	/// <summary>
	/// Adds each <c>Plugins/**/Binaries/&lt;Platform&gt;</c> under the project. Below a folder holding a .uplugin or a
	/// .ubtignore UBT looks for no plugin (EnumeratePlugins.cs), so the walk doesn't either: it stays out of a plugin's
	/// sources and content. Returns false when a folder can't be listed.
	/// </summary>
	static bool AddPluginFolders(string projectDirectory, UnrealPlatform platform, List<string> folders)
	{
		string Root = Path.Combine(projectDirectory, "Plugins");
		if (!Directory.Exists(Root))
		{
			return true;
		}
		Stack<(string Folder, int Depth)> Pending = new([(Root, 0)]);
		try
		{
			while (Pending.Count > 0)
			{
				(string Folder, int Depth) = Pending.Pop();
				string Binaries = Path.GetFullPath(Path.Combine(Folder, "Binaries", platform.Name));
				if (Directory.Exists(Binaries) && !folders.Contains(Binaries, PathComparer))
				{
					folders.Add(Binaries);
				}
				string[] Files = Directory.GetFiles(Folder);
				bool Stop = Files.Any(File => Path.GetExtension(File).Equals(".uplugin", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(File).Equals(".ubtignore", StringComparison.Ordinal));
				if (!Stop && Depth < MaxPluginDepth)
				{
					foreach (string Child in Directory.GetDirectories(Folder).Where(Child => !Path.GetFileName(Child).Equals("Binaries", StringComparison.OrdinalIgnoreCase)))
					{
						Pending.Push((Child, Depth + 1));
					}
				}
			}
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
		return true;
	}

	/// <summary>These binaries with the folders and listed files of <paramref name="other"/>; null, unknown, when it is.</summary>
	public TargetBinaries? Union(TargetBinaries? other)
	{
		if (other is null)
		{
			return null;
		}
		List<string> Folders = [.. this.Folders];
		Folders.AddRange(other.Folders.Where(Folder => !Folders.Contains(Folder, PathComparer)).ToList());
		HashSet<string> Listed = new(this.Listed, PathComparer);
		Listed.UnionWith(other.Listed);
		return new TargetBinaries(Folders, Listed);
	}

	/// <summary>The code files in the folders as they are now; null when a folder that exists can't be listed (then what a build wrote isn't known).</summary>
	public List<MutationBinary>? Snapshot()
	{
		List<MutationBinary> Result = [];
		foreach (string Folder in Folders)
		{
			if (!Directory.Exists(Folder))
			{
				continue;
			}
			try
			{
				foreach (FileInfo Entry in new DirectoryInfo(Folder).EnumerateFiles())
				{
					if (MutationFolders.IsCodeFile(Entry.Name) || Listed.Contains(Entry.FullName))
					{
						Result.Add(new MutationBinary { Path = Entry.FullName, LastWriteTimeUtc = Entry.LastWriteTimeUtc, Length = Entry.Length });
					}
				}
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
			{
				return null;
			}
		}
		return Result;
	}

	/// <summary>The binaries in <paramref name="now"/> that aren't in <paramref name="before"/> as they were there: written (or made) since.</summary>
	public static List<MutationBinary> WrittenSince(IReadOnlyList<MutationBinary> before, IReadOnlyList<MutationBinary> now)
	{
		Dictionary<string, MutationBinary> Before = new(PathComparer);
		foreach (MutationBinary Binary in before)
		{
			Before[Binary.Path] = Binary;
		}
		return [.. now.Where(Binary => !(Before.TryGetValue(Binary.Path, out MutationBinary? Was) && Was.SameAs(Binary)))];
	}

	/// <summary>
	/// The files a build says it linked (<see cref="BuildOutcome.Linked"/>, by name) that aren't among <paramref name="written"/>,
	/// the libraries no process loads aside: a binary outside the folders looked at, so the list can't be the whole of what
	/// the build wrote. One whose link failed is among them too: it may have been left part written.
	/// </summary>
	public static List<string> FindUnwritten(IReadOnlyList<string> linked, IReadOnlyList<MutationBinary> written)
	{
		HashSet<string> Written = new(written.Select(Binary => Path.GetFileName(Binary.Path)), PathComparer);
		return [.. linked.Where(Item => !LibraryExtensions.Contains(Path.GetExtension(Item)) && !Written.Contains(Path.GetFileName(Item)))];
	}
}
