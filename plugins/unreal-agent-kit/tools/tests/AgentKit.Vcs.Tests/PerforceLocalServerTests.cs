// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using EpicGames.Perforce;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Vcs.Tests;

/// <summary>
/// The Perforce writes against a real, throwaway p4d: a fresh server in a temporary folder, reached over <c>rsh:</c> (no
/// network port), with its own client. Opt-in: set <c>UAK_TEST_P4D</c> to a p4d program (Perforce's DVCS install has one).
/// Nothing outside the temporary folder is touched.
/// </summary>
[TestClass]
public sealed class PerforceLocalServerTests
{
	public TestContext TestContext { get; set; } = null!;

	CancellationToken Token => TestContext.CancellationToken;

	const string User = "tester";
	const string Client = "tester-ws";

	sealed class LocalServer : IDisposable
	{
		readonly TempDirectory _folder = new();

		public LocalServer(string p4d)
		{
			// rsh: splits its command on spaces, so the server runs from a copy in the temporary folder.
			string server = _folder.Combine("p4d" + Path.GetExtension(p4d));
			File.Copy(p4d, server);
			Directory.CreateDirectory(_folder.Combine("root"));
			Port = $"rsh:{server} -r {_folder.Combine("root")} -L log -i";
			Root = Directory.CreateDirectory(_folder.Combine("ws")).FullName;
		}

		public string Port { get; }

		public string Root { get; }

		public string Write(string relativePath, string content = "content\n") => _folder.Write(Path.Combine("ws", relativePath), content);

		/// <summary>Runs p4 against this server and client, for setup and checks; throws on a non-zero exit.</summary>
		public string P4(string? input, params string[] arguments)
		{
			ProcessStartInfo info = new("p4") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = Root };
			foreach (string argument in new[] { "-p", Port, "-u", User, "-c", Client }.Concat(arguments))
			{
				info.ArgumentList.Add(argument);
			}
			// p4 takes relative paths from PWD when it is set (as shells set it), not from the process's directory.
			info.Environment["PWD"] = Root;
			info.Environment["P4CONFIG"] = "";
			info.Environment["P4ENVIRO"] = Path.Combine(Root, "..", "p4enviro");
			using Process process = Process.Start(info)!;
			process.StandardInput.Write(input ?? "");
			process.StandardInput.Close();
			string output = process.StandardOutput.ReadToEnd();
			string error = process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (process.ExitCode != 0)
			{
				throw new InvalidOperationException($"p4 {string.Join(' ', arguments)} failed: {error}{output}");
			}
			return output;
		}

		public PerforceVersionControl Connect() => new(new P4ProcessConnection(new PerforceSettings(Port, User) { ClientName = Client }, NullLogger.Instance, TimeSpan.FromSeconds(60)), new DirectoryInfo(Root));

		public void Dispose() => _folder.Dispose();
	}

	static LocalServer? StartServer()
	{
		string? p4d = Environment.GetEnvironmentVariable("UAK_TEST_P4D");
		if (string.IsNullOrEmpty(p4d) || !File.Exists(p4d))
		{
			Assert.Inconclusive("Set UAK_TEST_P4D to a p4d program to run this test against a throwaway local server.");
			return null;
		}
		LocalServer server = new(p4d);
		string root = server.Root.Replace('\\', '/');
		server.P4($"Client: {Client}\nOwner: {User}\nRoot: {root}\nOptions: allwrite noclobber nocompress unlocked nomodtime normdir\nView:\n\t//depot/... //{Client}/...\n", "client", "-i");
		server.Write("Source/A.cpp", "one\n");
		server.Write("Source/B.cpp", "two\n");
		server.Write("Docs/C.md", "three\n");
		server.P4(null, "add", "Source/A.cpp", "Source/B.cpp", "Docs/C.md");
		server.P4(null, "submit", "-d", "base");
		return server;
	}

	[TestMethod]
	public async Task WritesAgainstARealServer()
	{
		using LocalServer? server = StartServer();
		if (server is null)
		{
			return;
		}
		using PerforceVersionControl p4 = server.Connect();
		string a = Path.Combine(server.Root, "Source", "A.cpp");
		string b = Path.Combine(server.Root, "Source", "B.cpp");

		// edit into the default changelist, then a new changelist, then reopen into it.
		Assert.IsTrue((await p4.EditAsync([a, b], null, cancellationToken: Token)).All(result => result.Opened && result.Change == "default"));
		int change = await p4.CreateChangeAsync("Agent work", Token);
		Assert.IsTrue((await p4.ReopenAsync([a, b], change, cancellationToken: Token)).All(result => result.Opened && result.Change == change.ToString(System.Globalization.CultureInfo.InvariantCulture)));

		// shelve, then move B out: B is shelved but not opened in the change, and -replace (p4 shelve -r) would delete it from
		// the shelf, so it is refused until asked; then the shelf is exactly A, and the result says B went.
		Assert.HasCount(2, (await p4.ShelveAsync(change, ShelveMode.Update, cancellationToken: Token)).Shelved);
		await p4.ReopenAsync([b], null, cancellationToken: Token);
		VcsException shelvedOnly = await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ShelveAsync(change, ShelveMode.Replace, cancellationToken: Token));
		StringAssert.Contains(shelvedOnly.Message, "//depot/Source/B.cpp");
		StringAssert.Contains(server.P4(null, "describe", "-S", "-s", change.ToString(System.Globalization.CultureInfo.InvariantCulture)), "//depot/Source/B.cpp#1 edit");
		File.WriteAllText(a, "one, changed\n");
		CollectionAssert.AreEqual(new[] { "//depot/Source/B.cpp" }, (await p4.GetShelvedNotOpenedAsync(change, Token)).ToArray());
		PerforceShelveResult update = await p4.ShelveAsync(change, ShelveMode.Update, cancellationToken: Token);
		CollectionAssert.AreEqual(new[] { "//depot/Source/A.cpp" }, update.Replaced.ToArray(), "A's shelved content changed");
		CollectionAssert.AreEqual(new[] { "//depot/Source/B.cpp" }, update.Kept.ToArray(), "p4 shelve -f keeps B, which is no longer opened");
		Assert.HasCount(2, update.Shelf);

		// When it was shelved, in the server's time zone: now, give or take the server's clock and the 1 s resolution.
		DateTimeOffset? shelvedAt = await p4.GetShelveTimeAsync(change, Token);
		Assert.IsNotNull(shelvedAt);
		Assert.IsLessThan(120.0, Math.Abs((shelvedAt.Value - DateTimeOffset.Now).TotalSeconds), $"shelveUpdate read as {shelvedAt:o}");
		PerforceShelveResult replaced = await p4.ShelveAsync(change, ShelveMode.Replace, dropUnopened: true, Token);
		Assert.HasCount(1, replaced.Shelved);
		CollectionAssert.AreEqual(new[] { "//depot/Source/B.cpp" }, replaced.Removed.ToArray());
		StringAssert.Contains(server.P4(null, "describe", "-S", "-s", change.ToString(System.Globalization.CultureInfo.InvariantCulture)), "//depot/Source/A.cpp#1 edit");
		Assert.DoesNotContain("B.cpp", server.P4(null, "describe", "-S", "-s", change.ToString(System.Globalization.CultureInfo.InvariantCulture)));

		// The description changes, the opened files stay.
		PerforceDescriptionUpdate described = await p4.UpdateDescriptionAsync(change, "Agent work, renamed", Token);
		Assert.IsEmpty(described.Restored);
		Assert.IsEmpty(described.Released);
		PerforceChange renamed = await p4.GetOwnPendingChangeAsync(change, Token);
		Assert.AreEqual("Agent work, renamed", renamed.Description);
		CollectionAssert.AreEqual(new[] { "//depot/Source/A.cpp" }, renamed.Files.ToArray());

		// add, and an edit that p4 refuses because the file is in another changelist.
		string added = server.Write("Docs/New.md", "new\n");
		Assert.IsTrue((await p4.AddAsync([added], change, Token)).Single().Opened);
		PerforceFileResult refused = (await p4.EditAsync([b], change, cancellationToken: Token)).Single();
		Assert.IsFalse(refused.Opened);
		StringAssert.Contains(refused.Message, "reopen");

		// A file that isn't in the depot can't be edited.
		string loose = server.Write("Docs/Loose.md", "x\n");
		PerforceFileResult notOnClient = (await p4.EditAsync([loose], null, cancellationToken: Token)).Single();
		Assert.IsFalse(notOnClient.Opened);

		// A move: naming one half for a reopen is refused (p4 would move both).
		string c = Path.Combine(server.Root, "Docs", "C.md");
		string d = Path.Combine(server.Root, "Docs", "D.md");
		server.P4(null, "edit", "Docs/C.md");
		server.P4(null, "move", "Docs/C.md", "Docs/D.md");
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.ReopenAsync([d], change, cancellationToken: Token));
		Assert.IsTrue((await p4.ReopenAsync([c, d], change, cancellationToken: Token)).All(result => result.Opened));

		// Nothing was submitted: change 1 is still the only submitted change.
		StringAssert.StartsWith(server.P4(null, "changes", "-s", "submitted").Trim(), "Change 1 ");
		await Assert.ThrowsExactlyAsync<VcsException>(() => p4.UpdateDescriptionAsync(1, "Rewrite history", Token));
	}

	/// <summary>Runs <c>uak vcs shelve</c> against the server, with no shelve guards; returns its exit code and output.</summary>
	async Task<(int ExitCode, string Output)> ShelveCommandAsync(LocalServer server, params string[] arguments)
	{
		using StringWriter output = new();
		VcsShelveCommand command = new()
		{
			Output = output,
			ErrorOutput = output,
			Guards = () => [],
			Detect = (_, _, _) => Task.FromResult(new VcsDetection(server.Connect(), "test")),
		};
		AgentKit.Core.UakContext context = new()
		{
			ProjectFile = new FileInfo(server.Write("Game.uproject", "{}\n")),
			StateDirectory = new DirectoryInfo(Path.Combine(server.Root, "Saved", "AgentKit")),
			Logger = NullLogger.Instance,
		};
		int exitCode = await command.RunAsync(context, arguments, Token);
		return (exitCode, output.ToString());
	}

	[TestMethod]
	public async Task TheShelveCommandReplacesTheShelfUnlessKeepUnopened()
	{
		using LocalServer? server = StartServer();
		if (server is null)
		{
			return;
		}
		string a = Path.Combine(server.Root, "Source", "A.cpp");
		string b = Path.Combine(server.Root, "Source", "B.cpp");
		server.P4(null, "edit", "Source/A.cpp", "Source/B.cpp");

		// Files into a new changelist: shelved with -r, which works on a changelist with no shelf yet.
		(int created, string createdOutput) = await ShelveCommandAsync(server, a, b);
		Assert.AreEqual(0, created, createdOutput);
		string number = System.Text.RegularExpressions.Regex.Match(createdOutput, "Created change (\\d+)\\.").Groups[1].Value;
		StringAssert.Contains(createdOutput, $"Shelved 2 file(s) in change {number} (p4 shelve -r");
		string Shelf() => server.P4(null, "describe", "-S", "-s", number);
		StringAssert.Contains(Shelf(), "//depot/Source/B.cpp#1 edit");

		// B leaves the change: the default shelve deletes it from the shelf, and says so.
		server.P4(null, "reopen", "-c", "default", "Source/B.cpp");
		(int replaced, string replacedOutput) = await ShelveCommandAsync(server, "-c=" + number);
		Assert.AreEqual(0, replaced, replacedOutput);
		StringAssert.Contains(replacedOutput, "REMOVED from the shelf: //depot/Source/B.cpp");
		Assert.DoesNotContain("B.cpp", Shelf());
		StringAssert.Contains(Shelf(), "//depot/Source/A.cpp#1 edit");

		// Shelve B again, move it out again: -keep-unopened (p4 shelve -f) keeps it, and says so.
		server.P4(null, "reopen", "-c", number, "Source/B.cpp");
		Assert.AreEqual(0, (await ShelveCommandAsync(server, "-c=" + number)).ExitCode);
		server.P4(null, "reopen", "-c", "default", "Source/B.cpp");
		(int kept, string keptOutput) = await ShelveCommandAsync(server, "-c=" + number, "-keep-unopened");
		Assert.AreEqual(0, kept, keptOutput);
		StringAssert.Contains(keptOutput, "kept on the shelf, but no longer opened in change " + number + " (a submit of the shelf would include it): //depot/Source/B.cpp");
		StringAssert.Contains(Shelf(), "//depot/Source/B.cpp#1 edit");

		// Nothing opened in the change: refused in both modes, and the shelf keeps both files.
		server.P4(null, "reopen", "-c", "default", "Source/A.cpp");
		Assert.AreEqual(1, (await ShelveCommandAsync(server, "-c=" + number)).ExitCode);
		Assert.AreEqual(1, (await ShelveCommandAsync(server, "-c=" + number, "-keep-unopened")).ExitCode);
		StringAssert.Contains(Shelf(), "//depot/Source/A.cpp#1 edit");
		StringAssert.Contains(Shelf(), "//depot/Source/B.cpp#1 edit");
	}
}
