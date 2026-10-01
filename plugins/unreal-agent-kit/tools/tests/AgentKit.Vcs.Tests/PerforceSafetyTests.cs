// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using AgentKit.Core;
using EpicGames.Perforce;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Vcs.Tests;

/// <summary>File names p4 would misread, p4's time limit, and where p4 and git are found.</summary>
[TestClass]
public sealed class PerforceSafetyTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	/// <summary>A script standing in for a program (.cmd on Windows, an executable shell script elsewhere).</summary>
	internal static string FakeProgram(string directory, string name, string windowsBody, string unixBody)
	{
		System.IO.Directory.CreateDirectory(directory);
		if (OperatingSystem.IsWindows())
		{
			string file = Path.Combine(directory, name + ".cmd");
			File.WriteAllText(file, "@echo off\r\n" + windowsBody.Replace("\n", "\r\n", StringComparison.Ordinal));
			return file;
		}
		string script = Path.Combine(directory, name);
		File.WriteAllText(script, "#!/bin/sh\n" + unixBody);
		File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		return script;
	}

	[TestMethod]
	public void PathsAreEscapedForP4()
	{
		Assert.AreEqual("Foo%402x.png", PerforceVersionControl.EscapePath("Foo@2x.png"));
		Assert.AreEqual("%23notes.txt", PerforceVersionControl.EscapePath("#notes.txt"));
		Assert.AreEqual("100%25 %2A %40%23", PerforceVersionControl.EscapePath("100% * @#"));
		Assert.AreEqual("a%2540b", PerforceVersionControl.EscapePath("a%40b"), "% is escaped first, so an escape is never doubled up wrongly");
		Assert.AreEqual("a%40b", PerforceVersionControl.UnescapePath(PerforceVersionControl.EscapePath("a%40b")));
		Assert.AreEqual("100% * @#", PerforceVersionControl.UnescapePath("100%25 %2a %40%23"));

		string pattern = PerforceVersionControl.DirectoryPattern(Path.Combine("root@1", "Content"));
		Assert.AreEqual(Path.Combine("root%401", "Content", "..."), pattern, "the ... wildcard stays a wildcard");

		Assert.ThrowsExactly<VcsException>(() => PerforceVersionControl.EscapePath(Path.Combine("root", "a...b.txt")));
		Assert.ThrowsExactly<VcsException>(() => PerforceVersionControl.DirectoryPattern(Path.Combine("root", "...")));
	}

	[TestMethod]
	public async Task ChangedListsFilesWithSpecialCharacters()
	{
		using TempDirectory root = new();
		string foo = root.Write("Content/UI/Foo@2x.png");
		string notes = root.Write("#notes.txt");
		FakePerforceConnection connection = new FakePerforceConnection(root.Path).On("fstat", "fstat-special");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		VcsChangedCommand command = new();
		using StringWriter output = new();
		command.Output = output;
		int exitCode = await command.RunAsync(new VcsDetection(p4, "test"), VcsArguments.Parse([], allowPaths: false, allowPathFilter: true), Token);

		Assert.AreEqual(UakExitCodes.Success, exitCode);
		string text = output.ToString();
		StringAssert.Contains(text, "Added        " + foo);
		StringAssert.Contains(text, "Modified     " + notes);
		Assert.DoesNotContain("%40", text);
		Assert.DoesNotContain("%23", text);

		// A filter naming one of those files reaches p4 escaped.
		IReadOnlyList<VcsFileStatus> filtered = await p4.GetChangedFilesAsync(foo, Token);
		Assert.AreEqual(root.Combine("Content/UI/Foo%402x.png"), connection.Calls[^1].FileArguments.Single());
		Assert.IsTrue(filtered.Any(status => status.Path == foo));
	}

	[TestMethod]
	public async Task StatusEscapesFstatPaths_AndAddPreviewTakesTheRawNameWithForce()
	{
		using TempDirectory root = new();
		string foo = root.Write("Content/UI/Foo@2x.png");
		string notes = root.Write("#notes.txt");
		string percent = root.Write("Docs/100%.txt");
		string untracked = root.Write("Saved/a@b.log");
		FakePerforceConnection connection = new FakePerforceConnection(root.Path)
			.On("fstat", "fstat-special")
			.On("add", "add-n-ok");
		using PerforceVersionControl p4 = new(connection, root.Directory);

		IReadOnlyList<VcsFileStatus> statuses = await p4.GetFileStatusAsync([foo, notes, percent, untracked], Token);

		CollectionAssert.AreEqual(new[] { VcsFileState.Added, VcsFileState.Modified, VcsFileState.Unmodified, VcsFileState.Untracked }, statuses.Select(status => status.State).ToArray());
		CollectionAssert.AreEqual(
			new[] { root.Combine("Content/UI/Foo%402x.png"), root.Combine("%23notes.txt"), root.Combine("Docs/100%25.txt"), root.Combine("Saved/a%40b.log") },
			connection.Calls.Single(call => call.Command == "fstat").FileArguments.ToArray());
		PerforceCall add = connection.Calls.Single(call => call.Command == "add");
		CollectionAssert.Contains(add.Arguments.ToList(), "-n");
		CollectionAssert.Contains(add.Arguments.ToList(), "-f", "p4 add only takes @#%* names with -f, which escapes them itself");
		Assert.AreEqual(untracked, add.Arguments[^1]);
	}

	[TestMethod]
	public async Task EllipsisInAPathIsRejectedBeforeP4Runs()
	{
		using TempDirectory root = new();
		FakePerforceConnection connection = new(root.Path);
		using PerforceVersionControl p4 = new(connection, root.Directory);

		VcsException exception = await Assert.ThrowsExactlyAsync<VcsException>(() => p4.GetFileStatusAsync([root.Combine("Source/a...b.txt")], Token));
		StringAssert.Contains(exception.Message, "\"...\"");
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.IsIgnoredAsync(root.Combine("Source/a...b.txt"), Token));
		Assert.IsEmpty(connection.Calls);
	}

	[TestMethod]
	[DataRow(null, 15.0)]
	[DataRow("", 15.0)]
	[DataRow("2.5", 2.5)]
	[DataRow("0", 15.0)]
	[DataRow("-3", 15.0)]
	[DataRow("soon", 15.0)]
	public void TimeoutComesFromTheEnvironment(string? value, double seconds)
	{
		Assert.AreEqual(TimeSpan.FromSeconds(seconds), P4ProcessConnection.GetTimeout(name => name == P4ProcessConnection.TimeoutVariable ? value : null));
	}

	[TestMethod]
	public async Task SlowP4IsKilledAtTheTimeLimit()
	{
		using TempDirectory directory = new();
		string p4 = FakeProgram(directory.Path, "p4", "ping -n 60 127.0.0.1 >nul\n", "sleep 60\n");
		P4ProcessConnection connection = new(new PerforceSettings("localhost:1", "user") { ClientName = "user-ws" }, NullLogger.Instance, TimeSpan.FromSeconds(1), p4);
		using PerforceVersionControl vcs = new(connection, directory.Directory);

		Stopwatch timer = Stopwatch.StartNew();
		VcsException exception = await Assert.ThrowsExactlyAsync<VcsException>(() => vcs.GetCurrentRevisionAsync(Token));
		Assert.IsLessThan(TimeSpan.FromSeconds(20), timer.Elapsed);
		StringAssert.Contains(exception.Message, "did not finish within 1 s");
		StringAssert.Contains(exception.Message, P4ProcessConnection.TimeoutVariable);
	}

	[TestMethod]
	public void OnlyP4sIgnoredMessageMeansIgnored()
	{
		Assert.IsTrue(PerforceVersionControl.IsIgnoredMessage("/ws/Saved/Logs/Game.log - ignored file can't be added."));
		// "ignored" elsewhere in the line (here, in the path) is not p4 saying so.
		Assert.IsFalse(PerforceVersionControl.IsIgnoredMessage("/ws/Docs/ignored-notes.txt - opened for add"));
		Assert.IsFalse(PerforceVersionControl.IsIgnoredMessage("/ws/Ignored/Game.log - can't add existing file"));
		Assert.IsFalse(PerforceVersionControl.IsIgnoredMessage("/ws/Saved/Logs/Game.log - IGNORED"));
	}

	[TestMethod]
	[DataRow(null, 30.0)]
	[DataRow("", 30.0)]
	[DataRow("2.5", 2.5)]
	[DataRow("0", 30.0)]
	[DataRow("-3", 30.0)]
	[DataRow("soon", 30.0)]
	public void GitTimeoutComesFromTheEnvironment(string? value, double seconds)
	{
		Assert.AreEqual(TimeSpan.FromSeconds(seconds), GitVersionControl.GetTimeout(name => name == "UAK_GIT_TIMEOUT" ? value : null));
	}

	[TestMethod]
	public async Task P4ProcessConnection_PassesSettings_AndTurnsPlainTextIntoAVcsException()
	{
		using TempDirectory directory = new();
		string arguments = directory.Combine("arguments.txt");
		string p4 = FakeProgram(directory.Path, "p4",
			$"echo %* > \"{arguments}\"\necho Perforce client error: 1>&2\necho Connect to server failed; check $P4PORT. 1>&2\nexit /b 1\n",
			$"echo \"$@\" > '{arguments}'\necho 'Perforce client error:' 1>&2\necho 'Connect to server failed; check $P4PORT.' 1>&2\nexit 1\n");
		P4ProcessConnection connection = new(new PerforceSettings("localhost:1", "user") { ClientName = "user-ws" }, NullLogger.Instance, TimeSpan.FromSeconds(30), p4);
		using PerforceVersionControl vcs = new(connection, directory.Directory);

		VcsException exception = await Assert.ThrowsExactlyAsync<VcsException>(() => vcs.GetCurrentRevisionAsync(Token));
		StringAssert.Contains(exception.Message, "p4 changes failed");
		string received = File.ReadAllText(arguments);
		StringAssert.Contains(received, "-G");
		StringAssert.Contains(received, "-plocalhost:1");
		StringAssert.Contains(received, "-uuser");
		StringAssert.Contains(received, "-cuser-ws");
		StringAssert.Contains(received, "changes");
	}

	[TestMethod]
	public void P4ProcessConnection_ReportsAMissingP4AsAStartFailure()
	{
		P4ProcessConnection connection = new(new PerforceSettings("localhost:1", "user"), NullLogger.Instance, executable: null);
		if (ExecutableLocator.FindOnPath("p4") is not null)
		{
			Assert.AreEqual(ExecutableLocator.FindOnPath("p4"), connection.Executable);
			return;
		}
		Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(() => connection.Executable);
	}
}

/// <summary>The current directory is process-wide, so this runs alone.</summary>
[TestClass]
public sealed class ProgramSearchTests
{
	public TestContext TestContext { get; set; } = null!;

	[TestMethod]
	[DoNotParallelize]
	public async Task GitAndP4InTheCurrentDirectoryNeverRun()
	{
		using TempGitRepository repo = new();
		repo.Write("README.md");
		string head = repo.Commit();

		using TempDirectory planted = new();
		string marker = planted.Combine("planted-ran.txt");
		foreach (string name in new[] { "git", "p4" })
		{
			PerforceSafetyTests.FakeProgram(planted.Path, name, $"echo {name}>> \"{marker}\"\n", $"echo {name} >> '{marker}'\n");
		}

		string previous = Environment.CurrentDirectory;
		Environment.CurrentDirectory = planted.Path;
		try
		{
			// git: the real one from PATH answers.
			GitVersionControl git = new(repo.Directory);
			Assert.AreEqual(head, await git.GetCurrentRevisionAsync(TestContext.CancellationToken));

			// p4: the real one from PATH (if any) fails to connect, or there is none; either way the planted one stays idle.
			using P4ProcessConnection connection = new(new PerforceSettings("localhost:1", "user"), NullLogger.Instance, TimeSpan.FromSeconds(10));
			try
			{
				await connection.TryGetInfoAsync(InfoOptions.None, TestContext.CancellationToken);
			}
			catch (Exception exception) when (exception is PerforceException or System.ComponentModel.Win32Exception or TimeoutException)
			{
			}
		}
		finally
		{
			Environment.CurrentDirectory = previous;
		}
		Assert.IsFalse(File.Exists(marker), File.Exists(marker) ? "ran: " + File.ReadAllText(marker) : "");
	}

	[TestMethod]
	[DoNotParallelize]
	public async Task SlowGitOnThePathIsKilledAtTheTimeLimit()
	{
		using TempDirectory work = new();
		using TempDirectory bin = new();
		string started = bin.Combine("slow-git-started.txt");
		// A "git" that hangs (as one waiting on a network share or a credential helper would).
		PerforceSafetyTests.FakeProgram(bin.Path, "git", $"echo started> \"{started}\"\nping -n 60 127.0.0.1 >nul\n", $"echo started > '{started}'\nsleep 60\n");
		string? path = Environment.GetEnvironmentVariable("PATH");
		Environment.SetEnvironmentVariable("PATH", bin.Path + Path.PathSeparator + path);
		Stopwatch timer;
		VcsException exception;
		try
		{
			GitVersionControl git = new(work.Directory, timeout: TimeSpan.FromSeconds(1));
			timer = Stopwatch.StartNew();
			exception = await Assert.ThrowsExactlyAsync<VcsException>(() => git.GetCurrentRevisionAsync(TestContext.CancellationToken));
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", path);
		}
		Assert.IsTrue(File.Exists(started), "The fake git on PATH ran.");
		Assert.IsLessThan(TimeSpan.FromSeconds(20), timer.Elapsed);
		StringAssert.Contains(exception.Message, "git rev-parse did not finish within 1 s");
		StringAssert.Contains(exception.Message, GitVersionControl.TimeoutVariable);
	}
}
