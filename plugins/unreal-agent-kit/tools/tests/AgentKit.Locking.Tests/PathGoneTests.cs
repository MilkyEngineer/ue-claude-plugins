// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using AgentKit.Locking;

namespace AgentKit.Tests;

/// <summary>
/// When a missing file of the marker is gone (<see cref="RebuildNeededFile.IsGone(string)"/>), asked through its seam: a reparse
/// point that is no link (a cloud-synced folder's placeholder) and an error only a server gives can't be made in a test. What
/// the disk itself says of a link that can't be opened, or one seen through a share, is asked of the disk.
/// </summary>
[TestClass]
public sealed class PathGoneTests
{
	/// <summary>ERROR_BAD_PATHNAME as an HRESULT.</summary>
	const int ErrorBadPathname = unchecked((int)0x800700A1);

	/// <summary>ERROR_INVALID_NAME as an HRESULT.</summary>
	const int ErrorInvalidName = unchecked((int)0x8007007B);

	/// <summary>A tree with a folder <c>Linked</c> on the way to a missing file, and that file.</summary>
	static (string Folder, string Missing) Tree(TempState state)
	{
		string Folder = state.PathOf("Linked");
		Directory.CreateDirectory(Path.Combine(Folder, "Private"));
		return (Folder, Path.Combine(Folder, "Private", "Missing.cpp"));
	}

	/// <summary>
	/// The disk's answers, but <paramref name="folder"/> is a reparse point, whose link target and final target are as given:
	/// a null <paramref name="target"/> is no link; a throwing function is a folder that can't be opened to ask.
	/// </summary>
	static PathQuestions ReparsePoint(string folder, Func<string?> target, Func<bool> finalExists, Func<string, FileAttributes>? attributes = null)
	{
		attributes ??= File.GetAttributes;
		return new PathQuestions
		{
			GetAttributes = Path => Path == folder ? attributes(Path) | FileAttributes.ReparsePoint : attributes(Path),
			GetLinkTarget = Path => Path == folder ? target() : null,
			FinalTargetExists = Path => Path == folder ? finalExists() : throw new InvalidOperationException($"only a link is asked: {Path}"),
		};
	}

	[TestMethod]
	public void AReparsePointThatIsNoLinkIsAPlainFolder()
	{
		// A cloud-synced folder (OneDrive's Documents, say) is a reparse point with no link target: a file deleted there is gone (K8-F-1).
		using TempState State = new();
		(string Folder, string Missing) = Tree(State);

		Assert.IsTrue(RebuildNeededFile.IsGone(Missing, ReparsePoint(Folder, () => null, () => throw new InvalidOperationException("no link, nothing to resolve"))));
	}

	[TestMethod]
	public void ALinkWhoseTargetIsThereLetsAMissingFileGo()
	{
		using TempState State = new();
		(string Folder, string Missing) = Tree(State);

		Assert.IsTrue(RebuildNeededFile.IsGone(Missing, ReparsePoint(Folder, () => State.PathOf("Elsewhere"), () => true)));
	}

	[TestMethod]
	public void ALinkWhoseTargetIsntThereKeepsAMissingFile()
	{
		using TempState State = new();
		(string Folder, string Missing) = Tree(State);

		Assert.IsFalse(RebuildNeededFile.IsGone(Missing, ReparsePoint(Folder, () => State.PathOf("Elsewhere"), () => false)));
	}

	[TestMethod]
	[DataRow(true, false, DisplayName = "its link target")]
	[DataRow(false, false, DisplayName = "its final target")]
	[DataRow(false, true, DisplayName = "its final target, denied")]
	public void ALinkThatCantBeOpenedToAskKeepsAMissingFile(bool target, bool denied)
	{
		using TempState State = new();
		(string Folder, string Missing) = Tree(State);
		Exception Fails() => denied ? new UnauthorizedAccessException("denied") : new IOException("can't open it");

		PathQuestions Questions = target
			? ReparsePoint(Folder, () => throw Fails(), () => true)
			: ReparsePoint(Folder, () => State.PathOf("Elsewhere"), () => throw Fails());

		Assert.IsFalse(RebuildNeededFile.IsGone(Missing, Questions));
	}

	[TestMethod]
	public void AFolderOnTheWayWhoseAttributesCantBeReadKeepsAMissingFile()
	{
		using TempState State = new();
		(string Folder, string Missing) = Tree(State);
		PathQuestions Questions = new() { GetAttributes = Path => Path == Folder ? throw new IOException("the share didn't answer") : File.GetAttributes(Path) };

		Assert.IsFalse(RebuildNeededFile.IsGone(Missing, Questions));
	}

	[TestMethod]
	public void AnOperationAbortedIsNoAnswerAndKeepsAMissingFile()
	{
		// .NET throws a bare OperationCanceledException for ERROR_OPERATION_ABORTED, a query a file system or a share cancelled:
		// nothing cancelled the walk, so the file is one that can't be reached, and it stays (K10-F-2).
		using TempState State = new();
		(string Folder, string Missing) = Tree(State);
		static OperationCanceledException Aborted() => new("The I/O operation has been aborted");
		FileAttributes AbortedAt(string Path, string At) => Path == At ? throw Aborted() : File.GetAttributes(Path);

		Assert.IsFalse(RebuildNeededFile.IsGone(Missing, new PathQuestions { GetAttributes = Path => AbortedAt(Path, Missing) }), "the file asked");
		Assert.IsFalse(RebuildNeededFile.IsGone(Missing, new PathQuestions { GetAttributes = Path => AbortedAt(Path, Folder) }), "a folder on the way asked");
		Assert.IsFalse(RebuildNeededFile.IsGone(Missing, ReparsePoint(Folder, () => throw Aborted(), () => true)), "a link's target read");
		Assert.IsFalse(RebuildNeededFile.IsGone(Missing, ReparsePoint(Folder, () => State.PathOf("Elsewhere"), () => throw Aborted())), "a link opened");
	}

	[TestMethod]
	public void ABadPathnameIsAskedAsNotFoundIs()
	{
		// ERROR_BAD_PATHNAME may come from a server or a file system for a path a file could have: it is believed only where
		// "not found" would be (K8, the 161 review).
		using TempState State = new();
		(string Folder, string Missing) = Tree(State);
		FileAttributes BadPath(string Path) => Path == Missing ? throw new IOException("The specified path is invalid.", ErrorBadPathname) : File.GetAttributes(Path);

		Assert.IsTrue(RebuildNeededFile.IsGone(Missing, new PathQuestions { GetAttributes = BadPath }), "under plain folders");
		Assert.IsFalse(RebuildNeededFile.IsGone(Missing, ReparsePoint(Folder, () => State.PathOf("Elsewhere"), () => false, BadPath)), "under a link whose target is gone");
	}

	[TestMethod]
	public void AnInvalidNameIsGoneWithoutTheWalk()
	{
		using TempState State = new();
		(string Folder, string Missing) = Tree(State);
		FileAttributes InvalidName(string Path) => Path == Missing ? throw new IOException("The filename, directory name, or volume label syntax is incorrect.", ErrorInvalidName) : File.GetAttributes(Path);

		Assert.IsTrue(RebuildNeededFile.IsGone(Missing, ReparsePoint(Folder, () => State.PathOf("Elsewhere"), () => false, InvalidName)));
	}

	[TestMethod]
	public void AFileThatIsThereIsNotGoneAndNoLinkIsAsked()
	{
		using TempState State = new();
		(string Folder, string Missing) = Tree(State);
		File.WriteAllText(Missing, "// there\n");

		Assert.IsFalse(RebuildNeededFile.IsGone(Missing, ReparsePoint(Folder, () => throw new InvalidOperationException("not asked"), () => false)));
	}

	[TestMethod]
	public void AServerWithNoShareIsGoneBeforeTheDiskIsAsked()
	{
		// No file has such a path, and no root can be reached for it, so no build could clear it (K9-F-4). Any other path,
		// a share's own, is the disk's to answer.
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("a share path is Windows'");
			return;
		}
		List<string> Asked = [];
		PathQuestions Questions = new()
		{
			GetAttributes = Path =>
			{
				Asked.Add(Path);
				throw new FileNotFoundException("not found", Path);
			},
		};

		// As Windows reads it too: it makes \\server\. and \\server\.. \\server\ (K10-F-1).
		foreach (string Server in new[] { @"\\nosuchserver-k9", @"\\nosuchserver-k9\", @"\\?\UNC\nosuchserver-k9", @"\\?\UNC\nosuchserver-k9\", @"\\.\UNC\nosuchserver-k9",
			"//nosuchserver-k9/", @"\\nosuchserver-k10\.", @"\\nosuchserver-k10\..", "//nosuchserver-k10/..",@"\\localhost\." })
		{
			Assert.IsTrue(RebuildNeededFile.IsGone(Server, Questions), Server);
		}
		Assert.IsEmpty(Asked, string.Join(", ", Asked));

		Assert.IsFalse(RebuildNeededFile.IsGone(@"\\localhost\NoSuchShare-k9\Missing.cpp", Questions), "a share that can't be reached");
		Assert.IsFalse(RebuildNeededFile.IsGone(@"\\?\UNC\localhost\NoSuchShare-k9", Questions), "a share's own folder");
		Assert.HasCount(2, Asked);
	}

	// ---- On the disk: a link that can't be opened, and one seen through a share (K9-F-1, K9-F-3) ----

	/// <summary>The current user, as icacls takes a SID.</summary>
	[SupportedOSPlatform("windows")]
	static string Me => "*" + WindowsIdentity.GetCurrent().User!.Value;

	/// <summary>Runs icacls with <paramref name="arguments"/>, and fails the test when it fails.</summary>
	static void Icacls(params string[] arguments)
	{
		ProcessStartInfo Start = new("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (string Argument in arguments)
		{
			Start.ArgumentList.Add(Argument);
		}
		using Process Run = Process.Start(Start)!;
		string Said = Run.StandardOutput.ReadToEnd() + Run.StandardError.ReadToEnd();
		Run.WaitForExit();
		Assert.AreEqual(0, Run.ExitCode, $"icacls {string.Join(' ', arguments)}: {Said}");
	}

	/// <summary>A junction <c>Linked</c> in <paramref name="state"/> to a folder <c>Elsewhere</c>, which holds a file; inconclusive where none can be made.</summary>
	static (string Link, string Target) LiveJunction(TempState state, string name = "Linked", string target = "Elsewhere")
	{
		string Link = state.PathOf(name);
		string Target = state.PathOf(target);
		Directory.CreateDirectory(Target);
		File.WriteAllText(Path.Combine(Target, "There.cpp"), "// behind the link\n");
		if (!DirectoryLinks.TryCreate(Link, Target))
		{
			Assert.Inconclusive("This system cannot create a directory junction.");
		}
		return (Link, Target);
	}

	[TestMethod]
	[SupportedOSPlatform("windows")]
	public void ADeadLinkThatCantBeOpenedKeepsAMissingFile()
	{
		// .NET's LinkTarget says null of a link it can't open, as of a folder that is no link, so a missing file behind a dead link
		// went with no evidence; what reads the target throws there, and the file stays (K9-F-1).
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("a junction and its ACL are Windows'");
			return;
		}
		using TempState State = new();
		(string Link, string Target) = LiveJunction(State);
		try
		{
			Directory.Delete(Target, recursive: true);
			string Missing = Path.Combine(Link, "Missing.cpp");
			Assert.IsFalse(RebuildNeededFile.IsGone(Missing), "behind a dead link");

			Icacls(Link, "/L", "/deny", Me + ":(F)");
			try
			{
				try
				{
					_ = new DirectoryInfo(Link).ResolveLinkTarget(returnFinalTarget: false);
					Assert.Inconclusive("the deny didn't take: the link opens");
				}
				catch (UnauthorizedAccessException)
				{
				}
				Assert.IsTrue(File.GetAttributes(Link).HasFlag(FileAttributes.ReparsePoint), "its attributes are read all the same");

				Assert.IsFalse(RebuildNeededFile.IsGone(Missing), "behind a dead link that can't be opened to ask");
			}
			finally
			{
				Icacls(Link, "/L", "/remove:d", Me);
			}
		}
		finally
		{
			DirectoryLinks.Remove(Link);
		}
	}

	[TestMethod]
	[SupportedOSPlatform("windows")]
	public void ALinkCycleKeepsAMissingFile()
	{
		// Two junctions that lead to each other can't be listed through: Windows says the name can't be resolved (0x80070781), an
		// error and not "not found", so the file stays, whether the file itself is asked or reads as not found (K10-F-4).
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("a junction is Windows'");
			return;
		}
		using TempState State = new();
		(string Link, string Target) = LiveJunction(State);
		try
		{
			Directory.Delete(Target, recursive: true);
			if (!DirectoryLinks.TryCreate(Target, Link))
			{
				Assert.Inconclusive("This system cannot create a junction back to the first.");
				return;
			}
			Assert.ThrowsExactly<IOException>(() => Directory.EnumerateFileSystemEntries(Link).GetEnumerator().MoveNext(), "a cycle is an error, not \"not found\"");
			string Missing = Path.Combine(Link, "Missing.cpp");
			PathQuestions NotFound = new() { GetAttributes = Path => Path == Missing ? throw new FileNotFoundException("not found", Path) : File.GetAttributes(Path) };

			Assert.IsFalse(RebuildNeededFile.IsGone(Missing), "behind a link cycle");
			Assert.IsFalse(RebuildNeededFile.IsGone(Missing, NotFound), "behind a link cycle, read as not found");
		}
		finally
		{
			DirectoryLinks.Remove(Target);
			DirectoryLinks.Remove(Link);
		}
	}

	[TestMethod]
	[SupportedOSPlatform("windows")]
	public void ALiveLinkWhoseTargetCantBeListedKeepsAMissingFile()
	{
		// Its target is there, but opening it through the link fails: an error, not "not found", so the file stays (K9-F-1).
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("a junction and its ACL are Windows'");
			return;
		}
		using TempState State = new();
		(string Link, string Target) = LiveJunction(State);
		try
		{
			string Missing = Path.Combine(Link, "Missing.cpp");
			Assert.IsTrue(RebuildNeededFile.IsGone(Missing), "behind a live link");

			Icacls(Target, "/deny", Me + ":(RD)");
			try
			{
				Assert.IsTrue(new DirectoryInfo(Link).ResolveLinkTarget(returnFinalTarget: true) is { Exists: true }, "its target is there");
				Assert.ThrowsExactly<FileNotFoundException>(() => File.GetAttributes(Missing), "and the file behind it reads as not found");

				Assert.IsFalse(RebuildNeededFile.IsGone(Missing), "behind a live link whose target can't be listed");
			}
			finally
			{
				Icacls(Target, "/remove:d", Me);
			}
		}
		finally
		{
			DirectoryLinks.Remove(Link);
		}
	}

	[TestMethod]
	[SupportedOSPlatform("windows")]
	public void ALinkSeenThroughAShareIsOpenedThroughTheShare()
	{
		// .NET gives a link's final path through a share as a relative one (UNC\server\...), which isn't there, so a file deleted
		// behind a live link never left; the link is opened through the share instead (K9-F-3). Inconclusive where the loopback
		// administrative share can't be reached without elevation.
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("administrative shares are Windows'");
			return;
		}
		using TempState State = new();
		(string Link, string _) = LiveJunction(State);
		(string Dead, string Gone) = LiveJunction(State, "Dead", "Gone");
		try
		{
			Directory.Delete(Gone, recursive: true);
			string Full = Path.GetFullPath(State.Root);
			string Root = Path.GetPathRoot(Full)!;
			if (Root.Length < 2 || Root[1] != ':')
			{
				Assert.Inconclusive($"{Full} is not on a drive letter.");
				return;
			}
			string Share = $@"\\localhost\{char.ToUpperInvariant(Root[0])}$\{Full[Root.Length..]}";
			try
			{
				if (!File.Exists(Path.Combine(Share, "Linked", "There.cpp")))
				{
					Assert.Inconclusive($"The loopback share {Share} cannot be reached.");
					return;
				}
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
			{
				Assert.Inconclusive($"The loopback share {Share} cannot be reached: {Exception.Message}");
				return;
			}

			Assert.IsTrue(RebuildNeededFile.IsGone(Path.Combine(Share, "Linked", "Missing.cpp")), "behind a live link, through the share");
			Assert.IsFalse(RebuildNeededFile.IsGone(Path.Combine(Share, "Dead", "Missing.cpp")), "behind a dead link, through the share");
			Assert.IsFalse(RebuildNeededFile.IsGone(Path.Combine(Share, "Linked", "There.cpp")), "there, through the share");
		}
		finally
		{
			DirectoryLinks.Remove(Dead);
			DirectoryLinks.Remove(Link);
		}
	}
}
