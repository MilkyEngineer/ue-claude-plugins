// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Core.Tests;

[TestClass]
public sealed class ExecutableLocatorTests
{
	/// <summary>Writes a program that prints <paramref name="text"/> and touches <paramref name="marker"/> (a .cmd on Windows, a shell script elsewhere).</summary>
	internal static string FakeProgram(string directory, string name, string text, string? marker = null)
	{
		Directory.CreateDirectory(directory);
		if (OperatingSystem.IsWindows())
		{
			string File = Path.Combine(directory, name + ".cmd");
			System.IO.File.WriteAllText(File, "@echo off\r\n" + (marker is null ? "" : $"echo ran> \"{marker}\"\r\n") + $"echo {text}\r\n");
			return File;
		}
		string Script = Path.Combine(directory, name);
		System.IO.File.WriteAllText(Script, "#!/bin/sh\n" + (marker is null ? "" : $"echo ran > '{marker}'\n") + $"echo {text}\n");
		System.IO.File.SetUnixFileMode(Script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		return Script;
	}

	[TestMethod]
	[DataRow("git", true)]
	[DataRow("git.exe", true)]
	[DataRow("./git", false)]
	[DataRow("tools/git", false)]
	[DataRow("", false)]
	public void IsBareName(string name, bool expected)
	{
		Assert.AreEqual(expected, ExecutableLocator.IsBareName(name));
		if (OperatingSystem.IsWindows())
		{
			Assert.IsFalse(ExecutableLocator.IsBareName(@"C:\Tools\git.exe"));
			Assert.IsFalse(ExecutableLocator.IsBareName(@"tools\git"));
		}
	}

	[TestMethod]
	public void SearchesOnlyAbsolutePathEntries_InOrder()
	{
		using TempTree Tree = new();
		string First = Tree.Dir("first");
		string Second = Tree.Dir("second");
		string Expected = FakeProgram(Second, "uaktool", "second");
		FakeProgram(Tree.Dir("relative"), "uaktool", "relative");

		// Empty, "." and relative entries are skipped, even when they would hold the program.
		// Windows also allows a quoted entry.
		string PathValue = string.Join(Path.PathSeparator, "", ".", "relative", First, OperatingSystem.IsWindows() ? "\"" + Second + "\"" : Second);
		Assert.AreEqual(Expected, ExecutableLocator.FindOnPath("uaktool", PathValue, ".EXE;.CMD"), ignoreCase: OperatingSystem.IsWindows());

		string Later = FakeProgram(First, "uaktool", "first");
		Assert.AreEqual(Later, ExecutableLocator.FindOnPath("uaktool", PathValue, ".EXE;.CMD"), ignoreCase: OperatingSystem.IsWindows());

		Assert.IsNull(ExecutableLocator.FindOnPath("uaktool", string.Join(Path.PathSeparator, "", ".", "relative"), ".EXE;.CMD"));
		Assert.ThrowsExactly<ArgumentException>(() => ExecutableLocator.FindOnPath("dir/uaktool", PathValue));
	}

	[TestMethod]
	public void UsesPathExtOnWindows()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("PATHEXT is Windows-only.");
		}
		using TempTree Tree = new();
		string Directory = Tree.Dir("bin");
		string Cmd = FakeProgram(Directory, "uaktool", "cmd");
		Assert.AreEqual(Cmd, ExecutableLocator.FindOnPath("uaktool", Directory, ".EXE;.CMD"), ignoreCase: true);
		Assert.AreEqual(Cmd, ExecutableLocator.FindOnPath("uaktool.cmd", Directory, ".EXE;.CMD"), ignoreCase: true);
		Assert.IsNull(ExecutableLocator.FindOnPath("uaktool", Directory, ".EXE;.COM"), "an extension outside PATHEXT is not tried");
		Assert.AreEqual(Cmd, ExecutableLocator.FindOnPath("uaktool", Directory, ""), ignoreCase: true, "empty PATHEXT falls back to the default list");
	}

	[TestMethod]
	public void SkipsFilesThatAreNotExecutableOnUnix()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Execute permission is Unix-only.");
			return;
		}
		using TempTree Tree = new();
		string Directory = Tree.Dir("bin");
		string File = Tree.File(Path.Combine("bin", "uaktool"), "#!/bin/sh\n");
		System.IO.File.SetUnixFileMode(File, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		Assert.IsNull(ExecutableLocator.FindOnPath("uaktool", Directory));
	}

	[TestMethod]
	public void Resolve_LeavesPathsAlone_AndUsesTheChildsPath()
	{
		using TempTree Tree = new();
		string Directory = Tree.Dir("bin");
		string Expected = FakeProgram(Directory, "uaktool", "x");
		string Explicit = Tree.Path("somewhere", "program");
		Assert.AreSame(Explicit, ExecutableLocator.Resolve(Explicit));
		Dictionary<string, string?> Environment = new() { ["PATH"] = Directory, ["PATHEXT"] = ".CMD" };
		Assert.AreEqual(Expected, ExecutableLocator.Resolve("uaktool", Environment), ignoreCase: OperatingSystem.IsWindows());
		Assert.IsNull(ExecutableLocator.Resolve("uaktool", new Dictionary<string, string?> { ["PATH"] = null }));
	}
}

/// <summary>The current directory is process-wide, so these run alone.</summary>
[TestClass]
public sealed class ProcessRunnerSearchTests
{
	[TestMethod]
	[DoNotParallelize]
	public async Task BareName_IsNeverTakenFromTheCurrentDirectory()
	{
		using TempTree Tree = new();
		string Planted = Tree.Dir("repo");
		string Marker = Tree.Path("planted-ran.txt");
		ExecutableLocatorTests.FakeProgram(Planted, "git", "planted", Marker);
		string Empty = Tree.Dir("empty");
		string Bin = Tree.Dir("bin");
		ExecutableLocatorTests.FakeProgram(Bin, "git", "from-path");

		string Previous = Environment.CurrentDirectory;
		Environment.CurrentDirectory = Planted;
		try
		{
			// Not on PATH: the clear "not found" error, and the planted copy does not run.
			ProcessStartException Error = await Assert.ThrowsExactlyAsync<ProcessStartException>(() => ProcessRunner.Default.CaptureAsync(
				new ProcessInvocation("git", ["--version"], Environment: new Dictionary<string, string?> { ["PATH"] = Empty }), CancellationToken.None));
			StringAssert.Contains(Error.Message, "not found on PATH");

			// On PATH: that one runs.
			ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(
				new ProcessInvocation("git", ["--version"], Environment: new Dictionary<string, string?> { ["PATH"] = Bin }), CancellationToken.None);
			Assert.AreEqual("from-path", Result.StandardOutput.Trim(), Result.StandardError);
		}
		finally
		{
			Environment.CurrentDirectory = Previous;
		}
		Assert.IsFalse(File.Exists(Marker), "the git in the current directory ran");
	}

	[TestMethod]
	public async Task ConsoleEncoding_DecodesWhatCmdWrites()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.AreEqual(65001, ProcessRunner.ConsoleEncoding.CodePage);
			return;
		}
		const string Text = "caf\u00e9";
		if (ProcessRunner.ConsoleEncoding.GetString(ProcessRunner.ConsoleEncoding.GetBytes(Text)) != Text)
		{
			Assert.Inconclusive($"The OEM code page {ProcessRunner.ConsoleEncoding.CodePage} cannot show '\u00e9'.");
		}
		// cmd writes redirected output in its console's code page, which is the OEM one for a child of ProcessRunner.
		string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
		ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(new ProcessInvocation(Cmd, ["/d", "/c", "echo " + Text]),
			new ProcessCaptureOptions { OutputEncoding = ProcessRunner.ConsoleEncoding }, CancellationToken.None);
		Assert.AreEqual(Text, Result.StandardOutput.Trim());
	}
}
