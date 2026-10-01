// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;

namespace AgentKit.Core.Tests;

[TestClass]
public sealed class CanonicalPathTests
{
	/// <summary>
	/// Creates a link to a directory: a junction on Windows (no privilege needed), a symbolic link elsewhere. Inconclusive
	/// when the system refuses.
	/// </summary>
	static void MakeLink(string link, string target)
	{
		if (!OperatingSystem.IsWindows())
		{
			Directory.CreateSymbolicLink(link, target);
			return;
		}
		ProcessStartInfo StartInfo = new(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		foreach (string Argument in new[] { "/d", "/c", "mklink", "/J", link, target })
		{
			StartInfo.ArgumentList.Add(Argument);
		}
		using Process Process = Process.Start(StartInfo)!;
		Process.StandardOutput.ReadToEnd();
		Process.StandardError.ReadToEnd();
		Process.WaitForExit();
		if (Process.ExitCode != 0 || !Directory.Exists(link))
		{
			Assert.Inconclusive("This system cannot create a directory junction.");
		}
	}

	[TestMethod]
	public void LinksResolveToTheirTarget()
	{
		using TempTree Tree = new();
		string Target = Tree.Dir("Real", "Project");
		string Link = Tree.Path("Link");
		MakeLink(Link, Target);
		try
		{
			Assert.AreEqual(CanonicalPath.Get(Target), CanonicalPath.Get(Link));
			Assert.AreEqual(CanonicalPath.Get(Path.Combine(Target, "Saved")), CanonicalPath.Get(Path.Combine(Link, "Saved")), "A missing child of a link resolves too.");
			Assert.IsTrue(CanonicalPath.AreSame(Link + Path.DirectorySeparatorChar, Target));
		}
		finally
		{
			Directory.Delete(Link, recursive: false);
		}
	}

	[TestMethod]
	public void IdentityIsTheSameThroughALinkAndDiffersBetweenFolders()
	{
		using TempTree Tree = new();
		string Target = Tree.Dir("Real", "Project");
		string Other = Tree.Dir("Real", "Other");
		string Link = Tree.Path("Link");
		MakeLink(Link, Target);
		try
		{
			string? Identity = CanonicalPath.TryGetIdentity(Target);
			Assert.IsNotNull(Identity, "The temporary folder's file system gives no file identity.");
			Assert.AreEqual(Identity, CanonicalPath.TryGetIdentity(Link));
			Assert.AreEqual(Identity, CanonicalPath.TryGetIdentity(Target.ToUpperInvariant() + Path.DirectorySeparatorChar));
			Assert.AreNotEqual(Identity, CanonicalPath.TryGetIdentity(Other));
			Assert.IsNull(CanonicalPath.TryGetIdentity(Path.Combine(Target, "Missing")));
			if (OperatingSystem.IsWindows())
			{
				StringAssert.Matches(Identity, new System.Text.RegularExpressions.Regex("^win:[0-9a-f]{16}:[0-9a-f]{32}$"));
			}
		}
		finally
		{
			Directory.Delete(Link, recursive: false);
		}
	}

	[TestMethod]
	public void IdentityIsTheSameThroughTheLoopbackShare()
	{
		using TempTree Tree = new();
		string Folder = Tree.Dir("Shared");
		string Share = LoopbackShare(Folder);
		// The readable path keeps the share's spelling; the identity sees through it.
		StringAssert.StartsWith(CanonicalPath.Get(Share), @"\\");
		Assert.AreEqual(CanonicalPath.TryGetIdentity(Folder), CanonicalPath.TryGetIdentity(Share));
	}

	/// <summary>
	/// A local folder through the loopback administrative share (<c>\\localhost\C$\...</c>). Inconclusive off Windows, or
	/// where the share cannot be reached (it needs an administrator's share and the Server service).
	/// </summary>
	internal static string LoopbackShare(string folder)
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Administrative shares are Windows only.");
		}
		string Full = Path.GetFullPath(folder);
		string Root = Path.GetPathRoot(Full)!;
		if (Root.Length < 2 || Root[1] != ':')
		{
			Assert.Inconclusive($"{Full} is not on a drive letter.");
		}
		string Share = $@"\\localhost\{char.ToUpperInvariant(Root[0])}$\{Full[Root.Length..]}";
		try
		{
			if (!Directory.Exists(Share))
			{
				Assert.Inconclusive($"The loopback share {Share} cannot be reached.");
			}
		}
		catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
		{
			Assert.Inconclusive($"The loopback share {Share} cannot be reached: {Error.Message}");
		}
		return Share;
	}

	[TestMethod]
	public void PathsAreFullWithoutTrailingSeparators()
	{
		using TempTree Tree = new();
		string Directory = Tree.Dir("A", "B");
		string Canonical = CanonicalPath.Get(Directory);
		Assert.AreEqual(Canonical, CanonicalPath.Get(Directory + Path.DirectorySeparatorChar));
		Assert.AreEqual(Canonical, CanonicalPath.Get(Path.Combine(Tree.Root, "A", ".", "C", "..", "B")));
		Assert.IsTrue(Path.IsPathFullyQualified(Canonical));
		// A path that does not exist keeps its missing part as given.
		Assert.AreEqual(Path.Combine(Canonical, "Not", "There"), CanonicalPath.Get(Path.Combine(Directory, "Not", "There")));
		string Root = Path.GetPathRoot(Canonical)!;
		Assert.AreEqual(CanonicalPath.Get(Root), Path.GetPathRoot(CanonicalPath.Get(Root)), "A root keeps its separator.");
	}

	[TestMethod]
	public void CaseFollowsTheDiskOnWindows()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Case-insensitive paths are the Windows default.");
			return;
		}
		using TempTree Tree = new();
		string Directory = Tree.Dir("MixedCase");
		Assert.AreEqual(CanonicalPath.Get(Directory), CanonicalPath.Get(Directory.ToUpperInvariant()));
		StringAssert.EndsWith(CanonicalPath.Get(Directory.ToUpperInvariant()), "MixedCase");
	}

	[TestMethod]
	[DataRow(@"\\?\C:\Projects\Game", @"C:\Projects\Game")]
	[DataRow(@"\\?\UNC\server\share\Game", @"\\server\share\Game")]
	[DataRow(@"\\?\unc\server\share", @"\\server\share")]
	[DataRow(@"C:\Plain", @"C:\Plain")]
	public void FinalPathPrefixesAreStripped(string finalPath, string expected)
	{
		Assert.AreEqual(expected, CanonicalPath.StripWindowsPrefix(finalPath));
	}

	[TestMethod]
	public void ResolverUsesCanonicalPathsAndCreatesNothing()
	{
		using TempTree Tree = new();
		string Engine = Tree.Engine("UE");
		string Project = Tree.Project("Game/Game.uproject");
		string Link = Tree.Path("GameLink");
		MakeLink(Link, Tree.Path("Game"));
		try
		{
			UakContext Direct = UakContextResolver.Resolve(new UakResolveOptions
			{
				ProjectArgument = Project,
				EngineArgument = Engine,
				CurrentDirectory = Tree.Root,
				GetEnvironmentVariable = _ => null,
				UserHomeDirectory = Tree.Dir("home"),
			});
			UakContext Linked = UakContextResolver.Resolve(new UakResolveOptions
			{
				ProjectArgument = Path.Combine(Link, "Game.uproject"),
				EngineArgument = Engine + Path.DirectorySeparatorChar,
				CurrentDirectory = Link,
				GetEnvironmentVariable = _ => null,
				UserHomeDirectory = Tree.Dir("home"),
			});
			Assert.AreEqual(Direct.ProjectFile!.FullName, Linked.ProjectFile!.FullName);
			Assert.AreEqual(CanonicalPath.Get(Project), Linked.ProjectFile.FullName);
			Assert.AreEqual(Direct.EngineRoot!.FullName, Linked.EngineRoot!.FullName);
			Assert.AreEqual(Direct.StateDirectory.FullName, Linked.StateDirectory.FullName);
			Assert.AreEqual(Path.Combine(CanonicalPath.Get(Tree.Path("Game")), "Saved", "AgentKit"), Linked.StateDirectory.FullName);
			Assert.IsFalse(Directory.Exists(Tree.Path("Game", "Saved")), "Resolving creates no state folder.");
		}
		finally
		{
			Directory.Delete(Link, recursive: false);
		}
	}
}
