// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentKit.Core.Tests;

[TestClass]
public sealed partial class ProcessInvocationTests
{
	[TestMethod]
	[DataRow("plain", "plain")]
	[DataRow("", "\"\"")]
	[DataRow("two words", "\"two words\"")]
	[DataRow("-Key=value", "-Key=value")]
	[DataRow("-ExecCmds=Automation RunTests Foo;Quit", "-ExecCmds=\"Automation RunTests Foo;Quit\"")]
	[DataRow(@"-Project=C:\My Projects\Game.uproject", @"-Project=""C:\My Projects\Game.uproject""")]
	[DataRow(@"C:\dir with space\", @"""C:\dir with space\\""")]
	[DataRow("say \"hi\"", "\"say \\\"hi\\\"\"")]
	[DataRow("-Key=a \"b\"", "\"-Key=a \\\"b\\\"\"")]
	public void QuoteForUnreal(string argument, string expected) =>
		Assert.AreEqual(expected, ProcessInvocation.QuoteForUnreal(argument));

	[TestMethod]
	[DataRow("plain")]
	[DataRow("")]
	[DataRow("two words")]
	[DataRow(@"C:\dir with space\")]
	[DataRow(@"C:\trailing\\")]
	[DataRow("say \"hi\"")]
	[DataRow(@"back\""slash")]
	[DataRow("-ExecCmds=Automation RunTests Foo;Quit")]
	[DataRow(@"-Project=C:\My Projects\")]
	[DataRow("tab\there")]
	public void QuotedArguments_RoundTripThroughCommandLineToArgvW(string argument)
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("CommandLineToArgvW is Windows-only.");
		}
		string CommandLine = "program.exe " + new ProcessInvocation("program.exe", [argument, "next"]).ToWindowsCommandLine();
		string[] Parsed = ParseWindows(CommandLine);
		// Unreal's -Key="value" form parses to -Key=value, which is the argument itself.
		CollectionAssert.AreEqual(new[] { "program.exe", argument, "next" }, Parsed);
	}

	[TestMethod]
	public void WithEnvironment_MergesAndOverrides()
	{
		ProcessInvocation Original = new("x", [], Environment: new Dictionary<string, string?> { ["A"] = "1", ["B"] = "2" });
		ProcessInvocation Merged = Original.WithEnvironment(new Dictionary<string, string?> { ["B"] = "3", ["C"] = null });
		Assert.AreEqual("1", Merged.Environment!["A"]);
		Assert.AreEqual("3", Merged.Environment!["B"]);
		Assert.IsTrue(Merged.Environment!.ContainsKey("C"));
		Assert.IsNull(Merged.Environment!["C"]);
		Assert.AreEqual("2", Original.Environment!["B"]);
	}

	[TestMethod]
	[DataRow("plain", "plain")]
	[DataRow("", "\"\"")]
	[DataRow("a&b", "\"a&b\"")]
	[DataRow("x|y", "\"x|y\"")]
	[DataRow("<>", "\"<>\"")]
	[DataRow("^", "\"^\"")]
	[DataRow("(a)", "\"(a)\"")]
	[DataRow("a,b;c", "\"a,b;c\"")]
	[DataRow("\"quoted\"", "\"\"\"quoted\"\"\"")]
	[DataRow("two words", "\"two words\"")]
	[DataRow("-Key=a&b", "-Key=\"a&b\"")]
	[DataRow("-Key=value", "-Key=value")]
	[DataRow(@"C:\dir with space\", @"""C:\dir with space\\""")]
	public void QuoteForCmd(string argument, string expected) =>
		Assert.AreEqual(expected, ProcessInvocation.QuoteForCmd(argument));

	[TestMethod]
	public void WindowsCommand_RunsScriptsThroughCmd()
	{
		(string File, string CommandLine) = ProcessRunner.GetWindowsCommand(new ProcessInvocation(@"C:\Program Files\UE\Build.bat", ["Target", "-Project=C:\\a b\\x.uproject"]));
		StringAssert.EndsWith(File, "cmd.exe", StringComparison.OrdinalIgnoreCase);
		Assert.AreEqual("/d /s /c \"\"C:\\Program Files\\UE\\Build.bat\" Target -Project=\"C:\\a b\\x.uproject\"\"", CommandLine);

		(string Exe, string ExeLine) = ProcessRunner.GetWindowsCommand(new ProcessInvocation("git", ["status", "two words"]));
		Assert.AreEqual("git", Exe);
		Assert.AreEqual("status \"two words\"", ExeLine);
	}

	static string[] ParseWindows(string commandLine)
	{
		IntPtr Argv = CommandLineToArgvW(commandLine, out int Count);
		try
		{
			string[] Result = new string[Count];
			for (int Index = 0; Index < Count; Index++)
			{
				Result[Index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(Argv, Index * IntPtr.Size))!;
			}
			return Result;
		}
		finally
		{
			LocalFree(Argv);
		}
	}

	[LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial IntPtr CommandLineToArgvW(string commandLine, out int count);

	[LibraryImport("kernel32.dll")]
	private static partial IntPtr LocalFree(IntPtr memory);
}

[TestClass]
public sealed class LineSplitterTests
{
	static List<string> Split(Encoding encoding, params byte[][] chunks)
	{
		List<string> Lines = [];
		LineSplitter Splitter = new(encoding, Lines.Add);
		foreach (byte[] Chunk in chunks)
		{
			Splitter.Write(Chunk, Chunk.Length);
		}
		Splitter.Flush();
		return Lines;
	}

	[TestMethod]
	public void SplitsOnLfCrLfAndLoneCr()
	{
		List<string> Lines = Split(Encoding.UTF8, Encoding.UTF8.GetBytes("a\nb\r\nc\rd"));
		CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, Lines);
	}

	[TestMethod]
	public void CrLfSplitAcrossReads_IsOneBreak()
	{
		List<string> Lines = Split(Encoding.UTF8, Encoding.UTF8.GetBytes("a\r"), Encoding.UTF8.GetBytes("\nb\n"));
		CollectionAssert.AreEqual(new[] { "a", "b" }, Lines);
	}

	[TestMethod]
	public void MultiByteCharacterSplitAcrossReads_Decodes()
	{
		byte[] Bytes = Encoding.UTF8.GetBytes("Grüße ✓\n");
		int Cut = Array.IndexOf(Bytes, (byte)0xC3) + 1;
		List<string> Lines = Split(Encoding.UTF8, Bytes[..Cut], Bytes[Cut..]);
		CollectionAssert.AreEqual(new[] { "Grüße ✓" }, Lines);
	}

	[TestMethod]
	public void EmptyLinesAreKept_AndNoTrailingEmptyLine()
	{
		List<string> Lines = Split(Encoding.UTF8, Encoding.UTF8.GetBytes("a\n\nb\n"));
		CollectionAssert.AreEqual(new[] { "a", "", "b" }, Lines);
	}
}

/// <summary>Runs real child processes: scripts written to a temporary folder (.bat on Windows, .sh elsewhere).</summary>
[TestClass]
public sealed class ProcessRunnerTests
{
	static string Script(TempTree tree, string name, string windows, string unix)
	{
		string File = tree.File(name + UnrealPlatform.Host.ScriptSuffix, OperatingSystem.IsWindows() ? "@echo off\r\n" + windows.Replace("\n", "\r\n", StringComparison.Ordinal) : "#!/bin/sh\n" + unix);
		if (!OperatingSystem.IsWindows())
		{
			System.IO.File.SetUnixFileMode(File, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		return File;
	}

	[TestMethod]
	public async Task Run_GivesLinesExitCodeLastLineAndOutputFile()
	{
		using TempTree Tree = new();
		string File = Script(Tree, "lines",
			"echo first\necho second 1>&2\necho last\nexit /b 3\n",
			"echo first\necho second 1>&2\necho last\nexit 3\n");
		string Output = Tree.Path("logs", "run.log");
		List<string> Lines = [];
		ProcessResult Result = await ProcessRunner.Default.RunAsync(new ProcessInvocation(File, []), new ProcessOutputOptions { OnLine = Lines.Add, OutputFile = Output }, CancellationToken.None);

		Assert.AreEqual(3, Result.ExitCode);
		Assert.AreEqual("last", Result.LastLine);
		CollectionAssert.AreEquivalent(new[] { "first", "second", "last" }, Lines.Select(Line => Line.Trim()).ToArray());
		byte[] Bytes = await System.IO.File.ReadAllBytesAsync(Output);
		Assert.IsFalse(Bytes.Length >= 3 && Bytes[0] == 0xEF && Bytes[1] == 0xBB, "The output file must not start with a BOM.");
		CollectionAssert.AreEquivalent(new[] { "first", "second", "last" }, Encoding.UTF8.GetString(Bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Line => Line.Trim()).ToArray());
	}

	[TestMethod]
	public async Task Capture_KeepsStdErrApart()
	{
		using TempTree Tree = new();
		string File = Script(Tree, "streams", "echo out\necho err 1>&2\n", "echo out\necho err 1>&2\n");
		ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(new ProcessInvocation(File, []), CancellationToken.None);
		Assert.AreEqual(0, Result.ExitCode);
		Assert.AreEqual("out", Result.StandardOutput.Trim());
		Assert.AreEqual("err", Result.StandardError.Trim());
	}

	[TestMethod]
	public async Task Capture_Raw_KeepsTheExactOutput()
	{
		using TempTree Tree = new();
		string File = Script(Tree, "raw", "echo one\necho two\n", "printf 'one\\ntwo\\r\\n'\n");
		ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(new ProcessInvocation(File, []), new ProcessCaptureOptions { Raw = true }, CancellationToken.None);
		Assert.AreEqual(OperatingSystem.IsWindows() ? "one\r\ntwo\r\n" : "one\ntwo\r\n", Result.StandardOutput);
	}

	[TestMethod]
	public async Task Capture_StandardInput_ReachesTheChild_ThenCloses()
	{
		using TempTree Tree = new();
		// sort (Windows) and cat (elsewhere) echo stdin and end only when it closes.
		ProcessInvocation Invocation = OperatingSystem.IsWindows()
			? new ProcessInvocation("sort", [])
			: new ProcessInvocation("cat", []);
		IProcessCapture Capture = ProcessRunner.Default;
		ProcessCapture Result = await Capture.CaptureAsync(Invocation, new ProcessCaptureOptions { Raw = true, StandardInput = "bravo\nalpha\n" }, CancellationToken.None);
		Assert.AreEqual(0, Result.ExitCode, Result.StandardError);
		string[] Lines = Result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
		CollectionAssert.AreEquivalent(new[] { "alpha", "bravo" }, Lines);
	}

	[TestMethod]
	public async Task BatchFile_GetsMetacharactersAsText_NotAsCommands()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("Batch files are Windows-only.");
		}
		using TempTree Tree = new();
		// Each argument is copied inside quotes (set "A=...") and echoed with delayed expansion, so the script itself never
		// runs a metacharacter; only a quoting failure in the runner can.
		string Lines = "setlocal enabledelayedexpansion\n";
		for (int Index = 1; Index <= 9; Index++)
		{
			Lines += $"set \"A{Index}=%~{Index}\"\n";
		}
		Lines += "echo [!A1!] [!A2!] [!A3!] [!A4!] [!A5!] [!A6!] [!A7!] [!A8!] [!A9!]\n";
		string File = Script(Tree, "echo args", Lines, "");
		ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(
			new ProcessInvocation(File, ["a&b", "x|y", "<>", "^", "\"quoted\"", "two words", "-Key=a&b", "plain"]), CancellationToken.None);
		Assert.AreEqual(0, Result.ExitCode, Result.StandardError);
		Assert.AreEqual("", Result.StandardError.Trim(), "A metacharacter ran as a command.");
		// %~1 strips only the outer quotes, so a quote inside an argument reaches a script doubled. cmd also splits %1..%9 at
		// '=', so -Key="a&b" arrives as %7=-Key and %8="a&b"; %* forwards it whole (as Build.bat does).
		Assert.AreEqual("[a&b] [x|y] [<>] [^] [\"\"quoted\"\"] [two words] [-Key] [a&b] [plain]", Result.StandardOutput.Trim());
	}

	[TestMethod]
	public async Task ArgumentsAndEnvironment_ReachTheChild()
	{
		using TempTree Tree = new();
		string File = Script(Tree, "args", "echo [%~1] [%~2] [%UAK_TEST_VALUE%]\n", "echo \"[$1] [$2] [$UAK_TEST_VALUE]\"\n");
		ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(
			new ProcessInvocation(File, ["two words", "plain"], Tree.Root, new Dictionary<string, string?> { ["UAK_TEST_VALUE"] = "set" }), CancellationToken.None);
		Assert.AreEqual("[two words] [plain] [set]", Result.StandardOutput.Trim());
	}

	[TestMethod]
	public async Task WorkingDirectory_IsUsed()
	{
		using TempTree Tree = new();
		string Work = Tree.Dir("work dir");
		string File = Script(Tree, "cwd", "cd\n", "pwd\n");
		ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(new ProcessInvocation(File, [], Work), CancellationToken.None);
		Assert.AreEqual(Path.GetFullPath(Work).TrimEnd(Path.DirectorySeparatorChar), Result.StandardOutput.Trim(), ignoreCase: OperatingSystem.IsWindows());
	}

	[TestMethod]
	public async Task Cancel_KillsTheChildAndThrows()
	{
		using TempTree Tree = new();
		string File = Script(Tree, "slow", "echo started\nping -n 60 127.0.0.1 >nul\necho finished\n", "echo started\nsleep 60\necho finished\n");
		using CancellationTokenSource Cancel = new();
		List<string> Lines = [];
		Stopwatch Timer = Stopwatch.StartNew();
		Task<ProcessResult> Run = ProcessRunner.Default.RunAsync(new ProcessInvocation(File, []), new ProcessOutputOptions
		{
			OnLine = Line =>
			{
				lock (Lines)
				{
					Lines.Add(Line);
				}
				if (Line.Trim() == "started")
				{
					Cancel.Cancel();
				}
			},
		}, Cancel.Token);
		await Assert.ThrowsAsync<OperationCanceledException>(() => Run);
		Assert.IsTrue(Timer.Elapsed < TimeSpan.FromSeconds(30), $"Cancelling took {Timer.Elapsed}.");
		lock (Lines)
		{
			Assert.IsFalse(Lines.Any(Line => Line.Trim() == "finished"));
		}
	}

	[TestMethod]
	public async Task MissingProgram_ThrowsProcessStartException()
	{
		using TempTree Tree = new();
		await Assert.ThrowsAsync<ProcessStartException>(() => ProcessRunner.Default.CaptureAsync(new ProcessInvocation(Tree.Path("no-such-program" + UnrealPlatform.Host.ExecutableSuffix), []), CancellationToken.None));
	}

	[TestMethod]
	public async Task InterfaceOverload_ReturnsTheExitCode()
	{
		using TempTree Tree = new();
		string File = Script(Tree, "code", "echo hi\nexit /b 7\n", "echo hi\nexit 7\n");
		IProcessRunner Runner = ProcessRunner.Default;
		List<string> Lines = [];
		Assert.AreEqual(7, await Runner.RunAsync(new ProcessInvocation(File, []), Lines.Add, CancellationToken.None));
		Assert.AreEqual("hi", Lines.Single().Trim());
	}

	[TestMethod]
	public void BuildEnvironment_InheritsAndApplies()
	{
		Assert.IsNull(ProcessRunner.BuildEnvironment(null));
		string Key = OperatingSystem.IsWindows() ? "Path" : "PATH";
		Dictionary<string, string> Environment = ProcessRunner.BuildEnvironment(new Dictionary<string, string?> { ["UAK_NEW"] = "1", [Key] = null })!;
		Assert.AreEqual("1", Environment["UAK_NEW"]);
		Assert.IsFalse(Environment.ContainsKey("PATH"));
		Assert.IsGreaterThan(1, Environment.Count);
	}
}
