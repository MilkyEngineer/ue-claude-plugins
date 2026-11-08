// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal.Tests;

[TestClass]
public sealed class GitDepsTests
{
	const string IosPng = "Engine/Source/Programs/AutomationTool/IOS/Resources/GreenCheck.png";

	// ---- uak gitdeps fetch ----

	[TestMethod]
	public async Task FetchExtractsOnlyTheNamedBlobsAtTheirOffsets()
	{
		using GitDepsEngine Engine = new();
		byte[] First = GitDepsEngine.Random(100, 1);
		// Larger than the reader's buffer, so the blob spans reads.
		byte[] Second = GitDepsEngine.Random(200_000, 2);
		byte[] Third = GitDepsEngine.Random(10, 3);
		string Pack = Engine.AddPack(("Engine/Content/First.bin", First, false), (IosPng, Second, false), ("Engine/Content/Third.bin", Third, false));
		Engine.WriteManifest();

		int Exit = await Engine.FetchAsync(IosPng, "Engine/Content/Third.bin");

		Assert.AreEqual(0, Exit, Engine.Logger.Text);
		CollectionAssert.AreEqual(Second, File.ReadAllBytes(Engine.PathOf(IosPng)));
		CollectionAssert.AreEqual(Third, File.ReadAllBytes(Engine.PathOf("Engine/Content/Third.bin")));
		Assert.IsFalse(File.Exists(Engine.PathOf("Engine/Content/First.bin")));
		CollectionAssert.AreEqual(new[] { Engine.UrlOf(Pack) }, Engine.Server.Requests);
		Assert.IsEmpty(Engine.TemporaryFiles());
		Assert.Contains($"fetched {IosPng} (200000 bytes, SHA-1 {GitDepsEngine.Sha1(Second)} verified)", Engine.Logger.Text);
		Assert.Contains("Engine root: " + Engine.Root + " (-root=)", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task ASkipLongerThanTheBufferReachesTheBlob()
	{
		using GitDepsEngine Engine = new();
		byte[] Wanted = GitDepsEngine.Random(300, 60);
		// The unwanted blob before it is longer than the reader's buffer, so the skip takes several reads.
		Engine.AddPack(("Engine/Content/Skipped.bin", GitDepsEngine.Random(200_000, 61), false), ("Engine/Content/Wanted.bin", Wanted, false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/Wanted.bin"), Engine.Logger.Text);

		CollectionAssert.AreEqual(Wanted, File.ReadAllBytes(Engine.PathOf("Engine/Content/Wanted.bin")));
		Assert.IsFalse(File.Exists(Engine.PathOf("Engine/Content/Skipped.bin")));
	}

	[TestMethod]
	public async Task ThePackIsReadOnlyUpToTheLastNeededBlob()
	{
		using GitDepsEngine Engine = new();
		byte[] Wanted = GitDepsEngine.Random(1000, 62);
		// Random bytes don't compress: the tail is about 3 MB on the wire too.
		Engine.AddPack(("Engine/Content/Wanted.bin", Wanted, false), ("Engine/Content/Tail.bin", GitDepsEngine.Random(3_000_000, 63), false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/Wanted.bin"), Engine.Logger.Text);

		CollectionAssert.AreEqual(Wanted, File.ReadAllBytes(Engine.PathOf("Engine/Content/Wanted.bin")));
		Assert.IsLessThan(1_000_000, Engine.Server.BytesServed, "the reader went on past the last blob it needed");
	}

	[TestMethod]
	public async Task FetchDownloadsOnlyThePacksThatHoldTheBlobs()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack(("Engine/Binaries/A.dll", GitDepsEngine.Random(64, 1), false));
		string Second = Engine.AddPack(("Engine/Binaries/B.dll", GitDepsEngine.Random(64, 2), false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Binaries/B.dll"), Engine.Logger.Text);

		CollectionAssert.AreEqual(new[] { Engine.UrlOf(Second) }, Engine.Server.Requests);
		Assert.IsFalse(File.Exists(Engine.PathOf("Engine/Binaries/A.dll")));
	}

	[TestMethod]
	public async Task FilesThatShareABlobAreAllWritten()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(300, 4);
		Engine.AddPack(("Engine/A/Same.bin", Content, false), ("Engine/B/Same.bin", Content, false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/**/Same.bin"), Engine.Logger.Text);

		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf("Engine/A/Same.bin")));
		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf("Engine/B/Same.bin")));
		Assert.IsEmpty(Engine.TemporaryFiles());
	}

	[TestMethod]
	public async Task AHashMismatchWritesNothing()
	{
		using GitDepsEngine Engine = new();
		byte[] Good = GitDepsEngine.Random(500, 5);
		byte[] Other = GitDepsEngine.Random(500, 6);
		Engine.AddPack(("Engine/Content/Fine.bin", GitDepsEngine.Random(40, 7), false), (IosPng, Good, false));
		// The server's pack holds other bytes where the blob should be.
		Engine.CorruptBlob(Good, Other);
		Engine.WriteManifest();

		int Exit = await Engine.FetchAsync("Engine/Content/Fine.bin", IosPng);

		Assert.AreEqual(1, Exit, Engine.Logger.Text);
		Assert.IsFalse(File.Exists(Engine.PathOf(IosPng)));
		// The file whose blob was fine is not written either: nothing is renamed until every blob is checked.
		Assert.IsFalse(File.Exists(Engine.PathOf("Engine/Content/Fine.bin")));
		Assert.IsEmpty(Engine.TemporaryFiles());
		// The folders it made for them are gone again; a wrong hash is not retried.
		Assert.IsFalse(Directory.Exists(Path.Combine(Engine.Root, "Engine", "Source")));
		Assert.HasCount(1, Engine.Server.Requests);
		Assert.Contains($"has SHA-1 {GitDepsEngine.Sha1(Other)}", Engine.Logger.Text);
		Assert.Contains("nothing written", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task AMissingPackFailsWithoutRetryingAndWritesNothing()
	{
		using GitDepsEngine Engine = new();
		string Pack = Engine.AddPack((IosPng, GitDepsEngine.Random(50, 8), false));
		Engine.WriteManifest();
		Engine.Server.Content.Remove(Engine.UrlOf(Pack));

		Assert.AreEqual(1, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		Assert.HasCount(1, Engine.Server.Requests);
		Assert.IsEmpty(Engine.Delays);
		Assert.IsFalse(File.Exists(Engine.PathOf(IosPng)));
		Assert.Contains("404", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task AServerErrorIsRetriedAfterABackoff()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(50, 9);
		Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		Engine.Server.Faults.Enqueue(new FakeFault(FakeFaultKind.ServiceUnavailable));

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		Assert.HasCount(2, Engine.Server.Requests);
		CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, Engine.Delays);
		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf(IosPng)));
	}

	[TestMethod]
	public async Task AConnectionBrokenMidStreamIsDiscardedAndRetried()
	{
		using GitDepsEngine Engine = new();
		byte[] First = GitDepsEngine.Random(2000, 64);
		byte[] Second = GitDepsEngine.Random(150_000, 65);
		Engine.AddPack(("Engine/Content/First.bin", First, false), ("Engine/Content/Second.bin", Second, false));
		Engine.WriteManifest();
		// The first blob is written before the stream breaks; the retry must start it again from a clean slate.
		Engine.Server.Faults.Enqueue(new FakeFault(FakeFaultKind.Break, 40_000));

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content"), Engine.Logger.Text);

		Assert.HasCount(2, Engine.Server.Requests);
		CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, Engine.Delays);
		CollectionAssert.AreEqual(First, File.ReadAllBytes(Engine.PathOf("Engine/Content/First.bin")));
		CollectionAssert.AreEqual(Second, File.ReadAllBytes(Engine.PathOf("Engine/Content/Second.bin")));
		Assert.IsEmpty(Engine.TemporaryFiles());
		Assert.Contains("trying again in 1 s (try 2 of 3)", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task RunningOutOfTriesFailsAndWritesNothing()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack(("Engine/Content/A.bin", GitDepsEngine.Random(100_000, 66), false));
		Engine.WriteManifest();
		for (int Index = 0; Index < 3; Index++)
		{
			Engine.Server.Faults.Enqueue(new FakeFault(FakeFaultKind.Break, 30_000));
		}

		Assert.AreEqual(1, await Engine.FetchAsync("Engine/Content/A.bin"), Engine.Logger.Text);

		Assert.HasCount(3, Engine.Server.Requests);
		CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, Engine.Delays);
		Assert.IsFalse(File.Exists(Engine.PathOf("Engine/Content/A.bin")));
		Assert.IsEmpty(Engine.TemporaryFiles());
		Assert.Contains("failed 3 times", Engine.Logger.Text);
		Assert.Contains("nothing written", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task AStalledReadTimesOutAndIsRetried()
	{
		using GitDepsEngine Engine = new() { IdleTimeout = TimeSpan.FromMilliseconds(200) };
		byte[] Content = GitDepsEngine.Random(50_000, 67);
		Engine.AddPack(("Engine/Content/A.bin", Content, false));
		Engine.WriteManifest();
		Engine.Server.Faults.Enqueue(new FakeFault(FakeFaultKind.Stall, 10_000));

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/A.bin"), Engine.Logger.Text);

		Assert.HasCount(2, Engine.Server.Requests);
		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf("Engine/Content/A.bin")));
		Assert.Contains("no data for 0.2 s", Engine.Logger.Text);
	}

	[TestMethod]
	public void TheTransferTimeLimitIsGitDependencies()
	{
		string Root = Path.Combine(Path.GetTempPath(), "uak-gitdeps-root");
		Dictionary<string, string> Environment = new() { ["UE_GITDEPS_ARGS"] = "--http-timeout-multiplier=2 -threads=3" };
		GitDepsSettings Settings = GitDepsSettings.Resolve(Root, null, null, true, Name => Environment.GetValueOrDefault(Name));

		// The time at 2 Mbit/s (250,000 bytes a second), times the multiplier and the threads, at least 100 s.
		Assert.AreEqual(TimeSpan.FromSeconds(24_000), Settings.TransferTimeout(1_000_000_000));
		Assert.AreEqual(TimeSpan.FromSeconds(100), Settings.TransferTimeout(1000));
		Assert.AreEqual(Math.Max(4, (int)(System.Environment.ProcessorCount * .25)), GitDepsSettings.Resolve(Root, null, null, true, _ => null).Threads);
	}

	[TestMethod]
	public async Task AWriteFailureFailsAtOnceNamingThePath()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((IosPng, GitDepsEngine.Random(50, 68), false));
		Engine.WriteManifest();
		// A file where the target's folder should be: the folder can't be created.
		Sandbox.Write(Path.Combine(Engine.Root, "Engine", "Source"), "a file, not a folder");

		Assert.AreEqual(1, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		Assert.HasCount(1, Engine.Server.Requests);
		Assert.IsEmpty(Engine.Delays);
		Assert.Contains("writing " + Path.GetDirectoryName(Engine.PathOf(IosPng)), Engine.Logger.Text);
		Assert.DoesNotContain("trying again", Engine.Logger.Text);
		Assert.IsEmpty(Engine.TemporaryFiles());
	}

	[TestMethod]
	public async Task ARenameFailureKeepsTheFilesAlreadyRenamed()
	{
		using GitDepsEngine Engine = new();
		byte[] First = GitDepsEngine.Random(30, 69);
		Engine.AddPack(("Engine/Content/A.bin", First, false), ("Engine/Content/B.bin", GitDepsEngine.Random(30, 70), false), ("Engine/Content/C.bin", GitDepsEngine.Random(30, 71), false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest();
		// A folder where B.bin goes: B counts as missing, and its rename fails.
		Directory.CreateDirectory(Engine.PathOf("Engine/Content/B.bin"));

		Assert.AreEqual(1, await Engine.FetchAsync("Engine/Content"), Engine.Logger.Text);

		CollectionAssert.AreEqual(First, File.ReadAllBytes(Engine.PathOf("Engine/Content/A.bin")));
		Assert.IsFalse(File.Exists(Engine.PathOf("Engine/Content/C.bin")));
		Assert.IsEmpty(Engine.TemporaryFiles());
		Assert.Contains("renaming Engine/Content/B.bin into place failed", Engine.Logger.Text);
		Assert.Contains("1 of 3 file(s) were renamed into place before it failed, each verified, and stay: Engine/Content/A.bin", Engine.Logger.Text);
		// The file renamed into place is recorded, as it is the manifest's.
		Assert.AreEqual(GitDepsEngine.Sha1(First), Engine.WorkingEntry("Engine/Content/A.bin")?.ExpectedHash);
		Assert.IsNull(Engine.WorkingEntry("Engine/Content/C.bin"));
	}

	[TestMethod]
	public async Task ACancelledFetchLeavesNoTemporaryFiles()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack(("Engine/Content/A.bin", GitDepsEngine.Random(2000, 72), false), ("Engine/Content/B.bin", GitDepsEngine.Random(300_000, 73), false));
		Engine.WriteManifest();
		using CancellationTokenSource Cancel = new();
		Engine.Server.OnRead = Served =>
		{
			if (Served > 50_000)
			{
				Cancel.Cancel();
			}
		};

		await Assert.ThrowsAsync<OperationCanceledException>(() => Engine.FetchAsync(Cancel.Token, "Engine/Content"));

		Assert.IsEmpty(Engine.TemporaryFiles());
		Assert.IsFalse(File.Exists(Engine.PathOf("Engine/Content/A.bin")));
		Assert.IsFalse(Directory.Exists(Path.Combine(Engine.Root, "Engine", "Content")));
	}

	[TestMethod]
	public async Task APatternThatMatchesNothingIsRefused()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((IosPng, GitDepsEngine.Random(50, 10), false));
		Engine.WriteManifest();

		Assert.AreEqual(2, await Engine.FetchAsync(IosPng, "Engine/**/*.nothing"));

		Assert.IsEmpty(Engine.Server.Requests);
		Assert.IsFalse(File.Exists(Engine.PathOf(IosPng)));
		Assert.Contains("'Engine/**/*.nothing' matches no file", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task AModifiedFileIsRefusedWithoutForce()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(80, 11);
		Engine.AddPack((IosPng, Content, false), ("Engine/Content/Other.bin", GitDepsEngine.Random(20, 12), false));
		Engine.WriteManifest();
		Sandbox.Write(Engine.PathOf(IosPng), "local change");

		Assert.AreEqual(2, await Engine.FetchAsync(IosPng, "Engine/Content/Other.bin"));
		Assert.AreEqual("local change", File.ReadAllText(Engine.PathOf(IosPng)));
		// The refusal comes before any download, so the other file isn't fetched either.
		Assert.IsEmpty(Engine.Server.Requests);
		Assert.IsFalse(File.Exists(Engine.PathOf("Engine/Content/Other.bin")));
		Assert.Contains("-force", Engine.Logger.Text);

		File.SetAttributes(Engine.PathOf(IosPng), FileAttributes.ReadOnly);
		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-force"), Engine.Logger.Text);
		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf(IosPng)));
		Assert.Contains("replaced the modified file", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task ATrackedFileWithAHashItDidNotExpectIsModified()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(80, 74);
		Engine.AddPack(("Engine/Content/A.bin", Content, false));
		Engine.WriteManifest();
		Sandbox.Write(Engine.PathOf("Engine/Content/A.bin"), "local change");
		string Old = GitDepsEngine.Sha1(GitDepsEngine.Random(80, 75));
		Engine.WriteWorkingManifest(("Engine/Content/A.bin", Old, Old, 1));

		Assert.AreEqual(2, await Engine.FetchAsync("Engine/Content/A.bin"));

		Assert.AreEqual("local change", File.ReadAllText(Engine.PathOf("Engine/Content/A.bin")));
		Assert.Contains("nor the one .uedependencies expected", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task AnOutdatedFileIsShownAndReplacedWithoutForce()
	{
		using GitDepsEngine Engine = new();
		byte[] Old = GitDepsEngine.Random(80, 76);
		byte[] New = GitDepsEngine.Random(80, 77);
		Engine.AddPack(("Engine/Content/A.bin", New, false));
		Engine.WriteManifest();
		string Target = Engine.PathOf("Engine/Content/A.bin");
		File.WriteAllBytes(Sandbox.Write(Target, ""), Old);
		// GitDependencies wrote the old engine's file and saved its hash at its write time; then the engine changed.
		Engine.WriteWorkingManifest(("Engine/Content/A.bin", GitDepsEngine.Sha1(Old), GitDepsEngine.Sha1(Old), File.GetLastWriteTimeUtc(Target).Ticks), ("Engine/Content/Other.bin", "11", "11", 5));
		StringWriter Output = new();

		Assert.AreEqual(0, await new GitDepsStatusCommand(Engine.Services(Output)).RunAsync(Engine.Context(), ["Engine/Content", "-root=" + Engine.Root, "-json"], CancellationToken.None));
		Assert.Contains("\"State\": \"Outdated\"", Output.ToString());

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/A.bin"), Engine.Logger.Text);

		CollectionAssert.AreEqual(New, File.ReadAllBytes(Target));
		Assert.Contains("replaced the outdated file", Engine.Logger.Text);
		// Its entry is replaced where it was, with the new hash and write time; the other is kept.
		GitDepsWorkingFile[] Entries = [.. GitDepsWorking.ReadManifest(Engine.WorkingPath).Files];
		Assert.HasCount(2, Entries);
		Assert.AreEqual("Engine/Content/A.bin", Entries[0].Name);
		Assert.AreEqual(GitDepsEngine.Sha1(New), Entries[0].Hash);
		string? Recorded = Entries[0].ExpectedHash;
		Assert.AreEqual(GitDepsEngine.Sha1(New), Recorded);
		Assert.AreEqual(File.GetLastWriteTimeUtc(Target).Ticks, Entries[0].Timestamp);
		Assert.AreEqual("11", Entries[1].Hash);
	}

	[TestMethod]
	public async Task APresentFileIsSkipped()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(80, 13);
		Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		File.WriteAllBytes(Sandbox.Write(Engine.PathOf(IosPng), ""), Content);

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		Assert.IsEmpty(Engine.Server.Requests);
		Assert.Contains("already present: " + IosPng, Engine.Logger.Text);
	}

	[TestMethod]
	public async Task ADryRunWritesNothingAndShowsThePackBytes()
	{
		using GitDepsEngine Engine = new();
		string Pack = Engine.AddPack((IosPng, GitDepsEngine.Random(400, 14), false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest(("Engine/Content/Other.bin", "11", "11", 5));
		byte[] Working = File.ReadAllBytes(Engine.WorkingPath);

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-dry-run"), Engine.Logger.Text);

		Assert.IsEmpty(Engine.Server.Requests);
		Assert.IsFalse(File.Exists(Engine.PathOf(IosPng)));
		Assert.IsFalse(Directory.Exists(Path.Combine(Engine.Root, "Engine", "Source")));
		CollectionAssert.AreEqual(Working, File.ReadAllBytes(Engine.WorkingPath));
		Assert.Contains($"pack {Pack}: {Engine.CompressedSizeOf(Pack)} bytes compressed, 1 blob(s) needed, from {Engine.UrlOf(Pack)}", Engine.Logger.Text);
		Assert.Contains("would fetch " + IosPng, Engine.Logger.Text);
		Assert.Contains("dry run, nothing written (.uedependencies included)", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task AFetchOverTheSizeCapIsRefusedBeforeAnyFileIsListed()
	{
		using GitDepsEngine Engine = new();
		// Random bytes don't compress: the pack is over 1 MiB.
		Engine.AddPack((IosPng, GitDepsEngine.Random(1_200_000, 15), false));
		Engine.WriteManifest();

		Assert.AreEqual(2, await Engine.FetchAsync(IosPng, "-max-mb=1"));
		Assert.IsEmpty(Engine.Server.Requests);
		Assert.IsFalse(File.Exists(Engine.PathOf(IosPng)));
		Assert.Contains("-max-mb=1", Engine.Logger.Text);
		Assert.Contains("an upper bound", Engine.Logger.Text);
		Assert.DoesNotContain("to fetch " + IosPng, Engine.Logger.Text);

		// A dry run warns instead.
		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-max-mb=1", "-dry-run"));
		Assert.IsEmpty(Engine.Server.Requests);
	}

	[TestMethod]
	public async Task ACachedPackIsReadWithoutADownload()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(70, 18);
		string Pack = Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		string Cache = Path.Combine(Engine.Root, "cache");
		Sandbox.Write(Path.Combine(Cache, Pack[..2], Pack), "");
		File.WriteAllBytes(Path.Combine(Cache, Pack[..2], Pack), Engine.Server.Content[Engine.UrlOf(Pack)]);

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-cache=" + Cache), Engine.Logger.Text);

		Assert.IsEmpty(Engine.Server.Requests);
		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf(IosPng)));
		Assert.Contains("read from the cache", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task ABadCachedPackFallsBackToADownload()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(70, 19);
		string Pack = Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		string Cache = Path.Combine(Engine.Root, "cache");
		Sandbox.Write(Path.Combine(Cache, Pack[..2], Pack), "not a gzip stream");

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-cache=" + Cache), Engine.Logger.Text);

		Assert.HasCount(1, Engine.Server.Requests);
		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf(IosPng)));
		Assert.IsEmpty(Engine.TemporaryFiles());
	}

	[TestMethod]
	public async Task ADownloadAfterABadCachedPackCountsAgainstTheCap()
	{
		using GitDepsEngine Engine = new();
		string Pack = Engine.AddPack((IosPng, GitDepsEngine.Random(1_200_000, 78), false));
		Engine.WriteManifest();
		string Cache = Path.Combine(Engine.Root, "cache");
		Sandbox.Write(Path.Combine(Cache, Pack[..2], Pack), "not a gzip stream");

		// Cached, so the plan downloads nothing; the fallback would download over 1 MiB.
		Assert.AreEqual(2, await Engine.FetchAsync(IosPng, "-cache=" + Cache, "-max-mb=1"));

		Assert.IsEmpty(Engine.Server.Requests);
		Assert.IsFalse(File.Exists(Engine.PathOf(IosPng)));
		Assert.IsEmpty(Engine.TemporaryFiles());
		Assert.Contains("more than -max-mb=1", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task CacheAndNoCacheTogetherIsAUsageError()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((IosPng, GitDepsEngine.Random(70, 79), false));
		Engine.WriteManifest();

		Assert.AreEqual(2, await Engine.FetchAsync(IosPng, "-cache=" + Path.Combine(Engine.Root, "cache"), "-no-cache"));
		Assert.Contains("don't go together", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task ACacheFolderThatDoesNotExistIsShown()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((IosPng, GitDepsEngine.Random(70, 80), false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-cache=" + Path.Combine(Engine.Root, "no-cache-here"), "-dry-run"), Engine.Logger.Text);
		Assert.Contains("no-cache-here (-cache=), which does not exist", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task PluginManifestsArePrefixedWithThePluginFolder()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(30, 20);
		Engine.AddPack(("Binaries/Win64/Thing.dll", Content, false));
		Sandbox.Write(Path.Combine(Engine.Root, "Engine", "Plugins", "Group", "Thing", "Thing.uplugin"), "{}");
		Engine.WriteManifest(Path.Combine("Engine", "Plugins", "Group", "Thing", "Build", "Thing.gitdeps.xml"));

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Plugins/Group/Thing/Binaries/Win64/Thing.dll"), Engine.Logger.Text);

		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf("Engine/Plugins/Group/Thing/Binaries/Win64/Thing.dll")));
	}

	[TestMethod]
	public async Task TheRootDefaultsToUaksEngine()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((IosPng, GitDepsEngine.Random(30, 21), false));
		Engine.WriteManifest();
		UakContext Context = new()
		{
			EngineRoot = new DirectoryInfo(Engine.Root),
			StateDirectory = new DirectoryInfo(Path.Combine(Engine.Root, "state")),
			Logger = Engine.Logger,
			Provenance = new Dictionary<string, string> { [UakContextResolver.EngineKey] = "UAK_ENGINE environment variable" },
		};

		Assert.AreEqual(0, await new GitDepsFetchCommand(Engine.Services()).RunAsync(Context, [IosPng, "-dry-run"], CancellationToken.None), Engine.Logger.Text);

		Assert.Contains($"Engine root: {Engine.Root} (uak's engine: UAK_ENGINE environment variable)", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task NoRootAndNoEngineIsASetupError()
	{
		ListLogger Logger = new();
		UakContext Context = new() { StateDirectory = new DirectoryInfo(Path.GetTempPath()), Logger = Logger };

		Assert.AreEqual(2, await new GitDepsStatusCommand().RunAsync(Context, [IosPng], CancellationToken.None));
		Assert.Contains("-root=", Logger.Text);
	}

	[TestMethod]
	[OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
	public async Task AnExecutableFileGetsItsExecuteBits()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack(("Engine/Binaries/Linux/tool", GitDepsEngine.Random(30, 22), true), ("Engine/Binaries/Linux/data", GitDepsEngine.Random(30, 23), false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Binaries/Linux"), Engine.Logger.Text);

		if (!OperatingSystem.IsWindows())
		{
			Assert.IsTrue(File.GetUnixFileMode(Engine.PathOf("Engine/Binaries/Linux/tool")).HasFlag(UnixFileMode.UserExecute));
			Assert.IsFalse(File.GetUnixFileMode(Engine.PathOf("Engine/Binaries/Linux/data")).HasFlag(UnixFileMode.UserExecute));
		}
	}

	// ---- zero-size and overlapping blobs ----

	[TestMethod]
	public async Task AZeroSizeBlobAtARealBlobsOffsetIsWrittenWithoutThePack()
	{
		using GitDepsEngine Engine = new();
		byte[] Real = GitDepsEngine.Random(3340, 81);
		// Both blobs sit at offset 0, as GitDependencies' manifests have it; the real one sorts first by name.
		string Pack = Engine.AddPack(("Engine/Content/Empty.txt", [], false), ("Engine/Content/A/Checked.png", Real, false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content"), Engine.Logger.Text);

		CollectionAssert.AreEqual(Real, File.ReadAllBytes(Engine.PathOf("Engine/Content/A/Checked.png")));
		Assert.AreEqual(0, new FileInfo(Engine.PathOf("Engine/Content/Empty.txt")).Length);
		CollectionAssert.AreEqual(new[] { Engine.UrlOf(Pack) }, Engine.Server.Requests);
	}

	[TestMethod]
	public async Task AZeroSizeBlobAloneNeedsNoDownload()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack(("Engine/Content/Empty.txt", [], false), ("Engine/Content/Other.bin", GitDepsEngine.Random(100, 82), false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/Empty.txt"), Engine.Logger.Text);

		Assert.IsEmpty(Engine.Server.Requests);
		Assert.AreEqual(0, new FileInfo(Engine.PathOf("Engine/Content/Empty.txt")).Length);
		Assert.Contains("0 bytes to download", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task AZeroSizeBlobWithAnotherHashIsABadManifest()
	{
		using GitDepsEngine Engine = new();
		string Pack = Engine.AddPack(("Engine/Content/Other.bin", GitDepsEngine.Random(100, 83), false));
		Engine.AddRawEntry("Engine/Content/Empty.txt", new string('1', 40), 0, Pack, 0);
		Engine.WriteManifest();

		Assert.AreEqual(2, await Engine.FetchAsync("Engine/Content/Empty.txt"));

		Assert.IsEmpty(Engine.Server.Requests);
		Assert.Contains("has size 0", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task RealOverlappingBlobsStillFail()
	{
		using GitDepsEngine Engine = new();
		byte[] Whole = GitDepsEngine.Random(100, 84);
		string Pack = Engine.AddPack(("Engine/Content/Whole.bin", Whole, false));
		Engine.AddRawEntry("Engine/Content/Half.bin", GitDepsEngine.Sha1(Whole[50..]), 50, Pack, 50);
		Engine.WriteManifest();

		Assert.AreEqual(1, await Engine.FetchAsync("Engine/Content"), Engine.Logger.Text);

		Assert.Contains("overlaps the blob before it", Engine.Logger.Text);
		Assert.IsEmpty(Engine.TemporaryFiles());
		Assert.IsFalse(File.Exists(Engine.PathOf("Engine/Content/Whole.bin")));
	}

	// ---- the working manifest ----

	[TestMethod]
	public void TheWorkingManifestRoundTripsByteForByte()
	{
		using GitDepsEngine Engine = new();
		Engine.WriteWorkingManifest(("Engine/Binaries/A.dll", "9f5c6f674e4a6866df01278f56ff0f3c8d803f00", "9f5c6f674e4a6866df01278f56ff0f3c8d803f00", 638835021652608959),
			("Engine/Binaries/Not yet.dll", null, "c0d2196d1c0e6d5edfdd020a52ec07407986f8ea", 0), (".tgitconfig", "8fa70533aa3bc6aee606776237b8d1b3ed16d315", "8fa70533aa3bc6aee606776237b8d1b3ed16d315", 1),
			// The real file has one name that needs escaping.
			("Engine/Source/ThirdParty/Licenses/Triton Inference Server & Client.license", "1d0e5b1f3f9a4bd0b8a1c0f2e7d6c5b4a3928170", "1d0e5b1f3f9a4bd0b8a1c0f2e7d6c5b4a3928170", 638835021652608960));
		byte[] Before = File.ReadAllBytes(Engine.WorkingPath);
		Assert.Contains("Triton Inference Server &amp; Client.license", File.ReadAllText(Engine.WorkingPath));
		string Copy = Path.Combine(Engine.Root, "copy");
		File.WriteAllBytes(Copy, []);

		GitDepsWorking.WriteManifest(Copy, GitDepsWorking.ReadManifest(Engine.WorkingPath));

		CollectionAssert.AreEqual(Before, File.ReadAllBytes(Copy));
		Assert.IsFalse(File.Exists(Copy + ".tmp"));
		if (OperatingSystem.IsWindows())
		{
			Assert.IsTrue(File.GetAttributes(Copy).HasFlag(FileAttributes.Hidden));
		}
	}

	[TestMethod]
	public async Task FetchedFilesAreRecordedInTheWorkingManifest()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(60, 16);
		Engine.AddPack((IosPng, Content, false), ("Engine/Content/Tracked.bin", GitDepsEngine.Random(60, 17), false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest(("Engine/Content/Tracked.bin", "00", "00", 1), ("Engine/Content/Pending.bin", null, "22", 0));
		string[] Before = File.ReadAllText(Engine.WorkingPath).Split(Environment.NewLine);
		// A stale tmp file from an interrupted GitDependencies run is replaced.
		File.WriteAllText(Engine.WorkingPath + ".tmp", "stale");

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		string[] After = File.ReadAllText(Engine.WorkingPath).Split(Environment.NewLine);
		// Every other line is as it was; the new entry goes before </Files>.
		CollectionAssert.AreEqual(Before[..^2], After[..^3]);
		Assert.AreEqual($"    <File Name=\"{IosPng}\" Hash=\"{GitDepsEngine.Sha1(Content)}\" ExpectedHash=\"{GitDepsEngine.Sha1(Content)}\" Timestamp=\"{File.GetLastWriteTimeUtc(Engine.PathOf(IosPng)).Ticks}\" />", After[^3]);
		CollectionAssert.AreEqual(Before[^2..], After[^2..]);
		Assert.IsFalse(File.Exists(Engine.WorkingPath + ".tmp"));
		Assert.Contains("Recorded 1 fetched file(s) in " + Engine.WorkingPath, Engine.Logger.Text);

		// GitDependencies' rule, and uak's: the saved hash at the saved write time counts, so the file is present and tracked.
		Assert.AreEqual(0, await new GitDepsStatusCommand(Engine.Services()).RunAsync(Engine.Context(), [IosPng, "-root=" + Engine.Root], CancellationToken.None));
		Assert.Contains("present   tracked    " + IosPng, Engine.Logger.Text);
	}

	[TestMethod]
	public async Task WithoutAWorkingManifestTheFilesStayUntracked()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((IosPng, GitDepsEngine.Random(60, 85), false), ("Engine/Content/B.bin", GitDepsEngine.Random(60, 86), false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		Assert.IsFalse(File.Exists(Engine.WorkingPath));
		Assert.Contains("No .uedependencies at the root (GitDependencies has not run here): the 1 fetched file(s) stay untracked", Engine.Logger.Text);

		// Only the legacy name: uak doesn't write it.
		string Legacy = Path.Combine(Engine.Root, ".ue4dependencies");
		File.WriteAllText(Legacy, "<?xml version=\"1.0\" encoding=\"utf-8\"?><WorkingManifest><Files /></WorkingManifest>");
		byte[] LegacyBefore = File.ReadAllBytes(Legacy);
		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/B.bin"), Engine.Logger.Text);
		CollectionAssert.AreEqual(LegacyBefore, File.ReadAllBytes(Legacy));
		Assert.IsFalse(File.Exists(Engine.WorkingPath));
		Assert.Contains("Only the legacy .ue4dependencies is at the root", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task AnUnreadableWorkingManifestLeavesTheFilesUntrackedWithOneWarning()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(60, 87);
		Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		File.WriteAllText(Engine.WorkingPath, "this is not XML");

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf(IosPng)));
		Assert.AreEqual("this is not XML", File.ReadAllText(Engine.WorkingPath));
		List<string> Warnings = [.. Engine.Logger.Entries.Where(Entry => Entry.Level == LogLevel.Warning).Select(Entry => Entry.Message)];
		Assert.HasCount(1, Warnings);
		Assert.Contains(".uedependencies", Warnings[0]);
		Assert.Contains("untracked", Warnings[0]);
	}

	[TestMethod]
	public async Task AFetchRefusesWhileGitDependenciesRuns()
	{
		using GitDepsEngine Engine = new() { GitDependenciesRunning = true };
		Engine.AddPack((IosPng, GitDepsEngine.Random(60, 88), false));
		Engine.WriteManifest();

		Assert.AreEqual(2, await Engine.FetchAsync(IosPng));

		Assert.IsEmpty(Engine.Server.Requests);
		Assert.IsFalse(File.Exists(Engine.PathOf(IosPng)));
		Assert.Contains("a process named GitDependencies is running", Engine.Logger.Text);
	}

	[TestMethod]
	[OSCondition(OperatingSystems.Windows)]
	public async Task AReadOnlyWorkingManifestLeavesNoTmpFileAndOneWarning()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(60, 95);
		Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest(("Engine/Content/Other.bin", "11", "11", 5));
		byte[] Working = File.ReadAllBytes(Engine.WorkingPath);
		// Windows refuses to delete a read-only file.
		File.SetAttributes(Engine.WorkingPath, FileAttributes.ReadOnly);

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		AssertUntrackedWithOneWarning(Engine, Content, Working);
	}

	[TestMethod]
	[OSCondition(OperatingSystems.Windows)]
	public async Task AWorkingManifestHeldOpenWithoutDeleteSharingLeavesNoTmpFileAndOneWarning()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(60, 96);
		Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest(("Engine/Content/Other.bin", "11", "11", 5));
		byte[] Working = File.ReadAllBytes(Engine.WorkingPath);

		// A reader that doesn't share delete, as uak's own status did: the old manifest can't be deleted while it is open.
		using (new FileStream(Engine.WorkingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);
		}

		AssertUntrackedWithOneWarning(Engine, Content, Working);
	}

	static void AssertUntrackedWithOneWarning(GitDepsEngine engine, byte[] content, byte[] working)
	{
		CollectionAssert.AreEqual(content, File.ReadAllBytes(engine.PathOf(IosPng)));
		CollectionAssert.AreEqual(working, File.ReadAllBytes(engine.WorkingPath));
		Assert.IsFalse(File.Exists(engine.WorkingPath + ".tmp"), "a hidden .uedependencies.tmp was left beside .uedependencies");
		List<string> Warnings = [.. engine.Logger.Entries.Where(Entry => Entry.Level == LogLevel.Warning).Select(Entry => Entry.Message)];
		Assert.HasCount(1, Warnings, engine.Logger.Text);
		Assert.Contains(".uedependencies could not be read or written", Warnings[0]);
		Assert.Contains("the 1 fetched file(s) stay, untracked", Warnings[0]);
		Assert.DoesNotContain(".tmp", Warnings[0]);
	}

	[TestMethod]
	public async Task ATmpFileThatCannotBeDeletedIsNamedInTheWarning()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(60, 109);
		Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest(("Engine/Content/Other.bin", "11", "11", 5));
		byte[] Working = File.ReadAllBytes(Engine.WorkingPath);
		// Neither the old manifest nor, after that failure, the tmp file can be deleted.
		Engine.DeleteFile = Target => throw new IOException("the test refuses to delete " + Path.GetFileName(Target));

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf(IosPng)));
		CollectionAssert.AreEqual(Working, File.ReadAllBytes(Engine.WorkingPath));
		Assert.IsTrue(File.Exists(Engine.WorkingPath + ".tmp"));
		List<string> Warnings = [.. Engine.Logger.Entries.Where(Entry => Entry.Level == LogLevel.Warning).Select(Entry => Entry.Message)];
		Assert.HasCount(1, Warnings, Engine.Logger.Text);
		Assert.Contains("(the test refuses to delete .uedependencies)", Warnings[0]);
		Assert.Contains("the 1 fetched file(s) stay, untracked", Warnings[0]);
		Assert.Contains(Engine.WorkingPath + ".tmp was left behind and could not be deleted (the test refuses to delete .uedependencies.tmp): delete it before running Setup", Warnings[0]);
	}

	[TestMethod]
	public void WritingTheManifestThrowsALeftoverErrorNamingATmpFileItCannotDelete()
	{
		using GitDepsEngine Engine = new();
		Engine.WriteWorkingManifest(("Engine/Content/A.bin", "11", "11", 5));
		byte[] Working = File.ReadAllBytes(Engine.WorkingPath);
		IOException Refusal = new("refused");

		GitDepsLeftoverException Error = Assert.Throws<GitDepsLeftoverException>(() => GitDepsWorking.WriteManifest(Engine.WorkingPath, new GitDepsWorkingManifest(), _ => throw Refusal));

		Assert.AreEqual(Engine.WorkingPath + ".tmp", Error.Path);
		Assert.AreEqual("refused", Error.Reason);
		Assert.AreSame(Refusal, Error.InnerException);
		CollectionAssert.AreEqual(Working, File.ReadAllBytes(Engine.WorkingPath));
	}

	[TestMethod]
	[OSCondition(OperatingSystems.Windows)]
	public void ReadingTheWorkingManifestLetsAWriterDeleteIt()
	{
		using GitDepsEngine Engine = new();
		Engine.WriteWorkingManifest(("Engine/Content/A.bin", "11", "11", 5));

		// A handle that holds delete access, as a writer's delete does: the reader must share delete to open the file at all.
		using (new FileStream(Engine.WorkingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
		{
			Assert.HasCount(1, GitDepsWorking.ReadManifest(Engine.WorkingPath).Files);
		}
	}

	[TestMethod]
	public async Task GitDependenciesStartingDuringTheFetchLeavesTheFilesUntracked()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(60, 97);
		Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest(("Engine/Content/Other.bin", "11", "11", 5));
		byte[] Working = File.ReadAllBytes(Engine.WorkingPath);
		// Not running when the fetch starts; running by the time it records.
		Engine.Server.OnRead = _ => Engine.GitDependenciesRunning = true;

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf(IosPng)));
		CollectionAssert.AreEqual(Working, File.ReadAllBytes(Engine.WorkingPath));
		Assert.IsNull(Engine.WorkingEntry(IosPng));
		List<string> Warnings = [.. Engine.Logger.Entries.Where(Entry => Entry.Level == LogLevel.Warning).Select(Entry => Entry.Message)];
		Assert.HasCount(1, Warnings, Engine.Logger.Text);
		Assert.Contains("A process named GitDependencies is running", Warnings[0]);
		Assert.Contains("untracked", Warnings[0]);
	}

	[TestMethod]
	public async Task WithOnlyAPendingTmpFileTheFilesStayUntrackedForSetupToFinish()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((IosPng, GitDepsEngine.Random(60, 98), false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest(("Engine/Content/Other.bin", "11", "11", 5));
		File.Move(Engine.WorkingPath, Engine.WorkingPath + ".tmp");
		byte[] Pending = File.ReadAllBytes(Engine.WorkingPath + ".tmp");

		Assert.AreEqual(0, await new GitDepsStatusCommand(Engine.Services()).RunAsync(Engine.Context(), [IosPng, "-root=" + Engine.Root], CancellationToken.None));
		Assert.Contains(".uedependencies.tmp is there: it holds an update to the manifest that was not moved into place, which Setup recovers", Engine.Logger.Text);
		Engine.Logger.Entries.Clear();

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		Assert.Contains(".uedependencies.tmp is there: it holds an update to the manifest that was not moved into place, which Setup recovers; the 1 fetched file(s) stay untracked", Engine.Logger.Text);
		Assert.DoesNotContain("GitDependencies was interrupted", Engine.Logger.Text);
		Assert.DoesNotContain("has not run here", Engine.Logger.Text);
		Assert.IsFalse(File.Exists(Engine.WorkingPath));
		CollectionAssert.AreEqual(Pending, File.ReadAllBytes(Engine.WorkingPath + ".tmp"));
	}

	[TestMethod]
	public async Task FilesAlreadyPresentButUntrackedAreRecorded()
	{
		using GitDepsEngine Engine = new();
		byte[] Present = GitDepsEngine.Random(60, 99);
		byte[] Fetched = GitDepsEngine.Random(60, 100);
		byte[] Second = GitDepsEngine.Random(60, 101);
		Engine.AddPack(("Engine/Content/Present.bin", Present, false), ("Engine/Content/Fetched.bin", Fetched, false), ("Engine/Content/Second.bin", Second, false),
			("Engine/Content/Modified.bin", GitDepsEngine.Random(60, 102), false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest(("Engine/Content/Other.bin", "11", "11", 5));
		string PresentPath = Engine.PathOf("Engine/Content/Present.bin");
		File.WriteAllBytes(Sandbox.Write(PresentPath, ""), Present);
		Sandbox.Write(Engine.PathOf("Engine/Content/Modified.bin"), "local change");

		// A dry run only says so.
		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/Present.bin", "-dry-run"), Engine.Logger.Text);
		Assert.Contains("would record 1 file(s) already present", Engine.Logger.Text);
		Assert.IsNull(Engine.WorkingEntry("Engine/Content/Present.bin"));

		// Nothing to fetch: the file is recorded as GitDependencies records an untracked file with the manifest's hash.
		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/Present.bin"), Engine.Logger.Text);
		Assert.Contains("Recorded 1 file(s) already present in " + Engine.WorkingPath, Engine.Logger.Text);
		GitDepsWorkingFile? Entry = Engine.WorkingEntry("Engine/Content/Present.bin");
		Assert.IsNotNull(Entry);
		Assert.AreEqual(GitDepsEngine.Sha1(Present), Entry.Hash);
		// A local, as the analyzer takes a property named Expected... for the expected argument.
		string? RecordedHash = Entry.ExpectedHash;
		Assert.AreEqual(GitDepsEngine.Sha1(Present), RecordedHash);
		Assert.AreEqual(File.GetLastWriteTimeUtc(PresentPath).Ticks, Entry.Timestamp);
		Assert.AreEqual("11", Engine.WorkingEntry("Engine/Content/Other.bin")?.Hash);

		// With a file to fetch, both are recorded in one write, and counted apart.
		File.WriteAllBytes(Sandbox.Write(Engine.PathOf("Engine/Content/Second.bin"), ""), Second);
		Engine.Logger.Entries.Clear();
		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/Second.bin", "Engine/Content/Fetched.bin"), Engine.Logger.Text);
		Assert.Contains("Recorded 1 fetched file(s) in", Engine.Logger.Text);
		Assert.Contains("Recorded 1 file(s) already present in", Engine.Logger.Text);
		Assert.AreEqual(GitDepsEngine.Sha1(Fetched), Engine.WorkingEntry("Engine/Content/Fetched.bin")?.ExpectedHash);
		Assert.AreEqual(GitDepsEngine.Sha1(Second), Engine.WorkingEntry("Engine/Content/Second.bin")?.ExpectedHash);

		// An untracked file with another hash is modified: refused, and not recorded.
		Assert.AreEqual(2, await Engine.FetchAsync("Engine/Content/Modified.bin"));
		Assert.IsNull(Engine.WorkingEntry("Engine/Content/Modified.bin"));
		Assert.AreEqual("local change", File.ReadAllText(Engine.PathOf("Engine/Content/Modified.bin")));

		// With no .uedependencies, nothing is recorded, and the output says so.
		File.Delete(Engine.WorkingPath);
		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/Fetched.bin"), Engine.Logger.Text);
		Assert.Contains("(GitDependencies has not run here): the 1 file(s) already present stay untracked", Engine.Logger.Text);
		Assert.IsFalse(File.Exists(Engine.WorkingPath));
	}

	// ---- one fetch per engine ----

	[TestMethod]
	public async Task AFetchRefusesWhileAnotherFetchHoldsTheEngine()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(60, 103);
		Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();

		using (GitDepsFetchLock? Held = GitDepsFetchLock.TryTake(Engine.Root))
		{
			Assert.IsNotNull(Held);
			Assert.AreEqual(2, await Engine.FetchAsync(IosPng));
			Assert.IsEmpty(Engine.Server.Requests);
			Assert.IsFalse(File.Exists(Engine.PathOf(IosPng)));
			Assert.Contains("another uak gitdeps fetch is running on this engine", Engine.Logger.Text);

			// A dry run writes nothing, so it needs no lock.
			Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-dry-run"), Engine.Logger.Text);
		}

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);
		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf(IosPng)));
		// The fetch released it.
		using GitDepsFetchLock? After = GitDepsFetchLock.TryTake(Engine.Root);
		Assert.IsNotNull(After);
	}

	[TestMethod]
	public void TheFetchLockIsNamedAfterTheFullRoot()
	{
		using GitDepsEngine Engine = new();
		string Name = GitDepsFetchLock.NameFor(Engine.Root);

		// Machine-wide on every OS: on Unix an unprefixed name is per session, and each shell has its own.
		StringAssert.StartsWith(Name, "Global\\" + GitDepsFetchLock.MutexBaseName + "_");
		Assert.AreEqual(Name, GitDepsFetchLock.NameFor(Engine.Root + Path.DirectorySeparatorChar));
		Assert.AreEqual(Name, GitDepsFetchLock.NameFor(Path.Combine(Engine.Root, "Engine", "..")));
		Assert.AreNotEqual(Name, GitDepsFetchLock.NameFor(Path.Combine(Engine.Root, "Engine")));
		if (OperatingSystem.IsWindows())
		{
			Assert.AreEqual(Name, GitDepsFetchLock.NameFor(Engine.Root.ToUpperInvariant()));
		}
		Assert.DoesNotContain(Engine.Root, Name);

		// Another spelling of the folder, through a link to it, is the same engine.
		string Link = Path.Combine(Path.GetDirectoryName(Engine.Root)!, Path.GetFileName(Engine.Root) + "-link");
		MakeLink(Link, Engine.Root);
		try
		{
			Assert.AreEqual(Name, GitDepsFetchLock.NameFor(Link));
		}
		finally
		{
			Directory.Delete(Link);
		}
	}

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
		System.Diagnostics.ProcessStartInfo StartInfo = new(System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
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
		using System.Diagnostics.Process Process = System.Diagnostics.Process.Start(StartInfo)!;
		Process.StandardOutput.ReadToEnd();
		Process.StandardError.ReadToEnd();
		Process.WaitForExit();
		if (Process.ExitCode != 0 || !Directory.Exists(link))
		{
			Assert.Inconclusive("This system cannot create a directory junction.");
		}
	}

	[TestMethod]
	public void ReleasingTheFetchLockTwiceDoesNothing()
	{
		using GitDepsEngine Engine = new();
		GitDepsFetchLock? Lock = GitDepsFetchLock.TryTake(Engine.Root);
		Assert.IsNotNull(Lock);

		Lock.Dispose();
		Lock.Dispose();

		using GitDepsFetchLock? After = GitDepsFetchLock.TryTake(Engine.Root);
		Assert.IsNotNull(After);
	}

	[TestMethod]
	public async Task AFetchLockAKilledFetchLeftIsTaken()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((IosPng, GitDepsEngine.Random(60, 104), false));
		Engine.WriteManifest();
		// A thread that takes the mutex and ends without releasing it abandons it, as a killed process does. Its handle stays
		// open, so the mutex lives on, abandoned.
		Mutex? Abandoned = null;
		Thread Taker = new(() =>
		{
			Abandoned = new Mutex(false, GitDepsFetchLock.NameFor(Engine.Root));
			Abandoned.WaitOne();
		});
		Taker.Start();
		Taker.Join();
		try
		{
			Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

			Assert.Contains("ended without releasing its lock (it was killed); this fetch holds it now", Engine.Logger.Text);
			Assert.IsTrue(File.Exists(Engine.PathOf(IosPng)));
			using GitDepsFetchLock? After = GitDepsFetchLock.TryTake(Engine.Root);
			Assert.IsNotNull(After);
			Assert.IsFalse(After.WasAbandoned);
		}
		finally
		{
			Abandoned?.Dispose();
		}
	}

	[TestMethod]
	public async Task TemporaryFilesAKilledFetchLeftBesideTheSelectedFilesAreDeleted()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack(("Engine/Content/A.bin", GitDepsEngine.Random(60, 105), false), ("Engine/Content/Unselected.bin", GitDepsEngine.Random(60, 106), false));
		Engine.WriteManifest();
		string Id = Guid.NewGuid().ToString("N");
		string Leftover = Sandbox.Write(Engine.PathOf($"Engine/Content/A.bin.uak-{Id}.tmp"), "partial");
		string[] Kept =
		[
			// Not the exact staged-name pattern.
			Sandbox.Write(Engine.PathOf("Engine/Content/A.bin.uak-notaguid.tmp"), "kept"),
			Sandbox.Write(Engine.PathOf($"Engine/Content/A.bin.uak-{Id}0.tmp"), "kept"),
			Sandbox.Write(Engine.PathOf($"Engine/Content/A.bin.uak-{Id[..31]}.tmp"), "kept"),
			Sandbox.Write(Engine.PathOf($"Engine/Content/A.bin.uak-{Id}.tmp.bak"), "kept"),
			// A file not selected.
			Sandbox.Write(Engine.PathOf($"Engine/Content/Unselected.bin.uak-{Id}.tmp"), "kept"),
			// Another folder.
			Sandbox.Write(Engine.PathOf($"Engine/Content/Sub/A.bin.uak-{Id}.tmp"), "kept"),
		];

		// A dry run deletes nothing.
		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/A.bin", "-dry-run"), Engine.Logger.Text);
		Assert.IsTrue(File.Exists(Leftover));

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/A.bin"), Engine.Logger.Text);

		Assert.IsFalse(File.Exists(Leftover));
		Assert.Contains($"deleted {Leftover}, a temporary file an earlier fetch left (it was killed)", Engine.Logger.Text);
		foreach (string Path in Kept)
		{
			Assert.IsTrue(File.Exists(Path), Path);
		}
		Assert.HasCount(1, Engine.Logger.Entries.Where(Entry => Entry.Message.StartsWith("deleted ", StringComparison.Ordinal)));
	}

	[TestMethod]
	public async Task ACancelOnceStagingIsDoneStillRenamesListsAndRecordsEveryFile()
	{
		using GitDepsEngine Engine = new();
		byte[] First = GitDepsEngine.Random(60, 107);
		byte[] Second = GitDepsEngine.Random(60, 108);
		Engine.AddPack(("Engine/Content/A.bin", First, false), ("Engine/Content/B.bin", Second, false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest();
		using CancellationTokenSource Cancel = new();
		Engine.AfterStaging = Cancel.Cancel;

		await Assert.ThrowsAsync<OperationCanceledException>(() => Engine.FetchAsync(Cancel.Token, "Engine/Content"));

		CollectionAssert.AreEqual(First, File.ReadAllBytes(Engine.PathOf("Engine/Content/A.bin")));
		CollectionAssert.AreEqual(Second, File.ReadAllBytes(Engine.PathOf("Engine/Content/B.bin")));
		Assert.Contains("fetched Engine/Content/A.bin", Engine.Logger.Text);
		Assert.Contains("fetched Engine/Content/B.bin", Engine.Logger.Text);
		Assert.Contains("Recorded 2 fetched file(s)", Engine.Logger.Text);
		Assert.AreEqual(GitDepsEngine.Sha1(First), Engine.WorkingEntry("Engine/Content/A.bin")?.ExpectedHash);
		Assert.AreEqual(GitDepsEngine.Sha1(Second), Engine.WorkingEntry("Engine/Content/B.bin")?.ExpectedHash);
		Assert.IsEmpty(Engine.TemporaryFiles());
		Assert.Contains("cancelled once the renames had started", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task ARenameFailureIsShownWhenACancelAlsoCame()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack(("Engine/Content/A.bin", GitDepsEngine.Random(30, 110), false), ("Engine/Content/B.bin", GitDepsEngine.Random(30, 111), false));
		Engine.WriteManifest();
		Engine.WriteWorkingManifest();
		// A folder where B.bin goes: its rename fails.
		Directory.CreateDirectory(Engine.PathOf("Engine/Content/B.bin"));
		using CancellationTokenSource Cancel = new();
		Engine.AfterStaging = Cancel.Cancel;

		await Assert.ThrowsAsync<OperationCanceledException>(() => Engine.FetchAsync(Cancel.Token, "Engine/Content"));

		Assert.Contains("renaming Engine/Content/B.bin into place failed: ", Engine.Logger.Text);
		Assert.Contains("cancelled once the renames had started", Engine.Logger.Text);
		Assert.IsEmpty(Engine.TemporaryFiles());
	}

	// ---- retries and time limits ----

	[TestMethod]
	[DataRow(429, 7, 7)]
	[DataRow(408, -1, 1)]
	[DataRow(429, 120, 30)]
	[DataRow(503, 20, 1)]
	public async Task RequestTimeoutAndTooManyRequestsAreRetriedAfterTheirRetryAfter(int status, int retryAfterSeconds, int expectedWaitSeconds)
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(50, 109);
		Engine.AddPack((IosPng, Content, false));
		Engine.WriteManifest();
		Engine.Server.Faults.Enqueue(new FakeFault(FakeFaultKind.Status, Status: status, RetryAfterSeconds: retryAfterSeconds));

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		Assert.HasCount(2, Engine.Server.Requests);
		// A Retry-After is honoured, at most 30 s, only for a 408 or 429; the backoff stays the least wait.
		CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(expectedWaitSeconds) }, Engine.Delays);
		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf(IosPng)));
	}

	[TestMethod]
	public async Task TheTransferTimeLimitEndsATryAndIsRetried()
	{
		// Reads would wait 30 s for data; the transfer's limit ends the try first.
		using GitDepsEngine Engine = new() { TransferTimeout = TimeSpan.FromSeconds(1) };
		byte[] Content = GitDepsEngine.Random(50_000, 110);
		Engine.AddPack(("Engine/Content/A.bin", Content, false));
		Engine.WriteManifest();
		Engine.Server.Faults.Enqueue(new FakeFault(FakeFaultKind.Stall, 10_000));

		Assert.AreEqual(0, await Engine.FetchAsync("Engine/Content/A.bin"), Engine.Logger.Text);

		Assert.HasCount(2, Engine.Server.Requests);
		CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, Engine.Delays);
		CollectionAssert.AreEqual(Content, File.ReadAllBytes(Engine.PathOf("Engine/Content/A.bin")));
		Assert.Contains("the transfer took longer than 1 s, GitDependencies' limit for it", Engine.Logger.Text);
		Assert.IsEmpty(Engine.TemporaryFiles());
	}

	[TestMethod]
	public void TheDefaultHandlerSendsEveryUrlThroughTheProxy()
	{
		string Root = Path.Combine(Path.GetTempPath(), "uak-gitdeps-root");
		Dictionary<string, string> Environment = new() { ["UE_GITDEPS_ARGS"] = "-proxy-user=someuser -proxy-password=S3cretPw" };
		GitDepsSettings Settings = GitDepsSettings.Resolve(Root, "http://proxy.example.com:3128", null, true, Name => Environment.GetValueOrDefault(Name));

		using (HttpClientHandler Handler = (HttpClientHandler)GitDepsServices.CreateDefaultHandler(Settings))
		{
			Assert.IsTrue(Handler.UseProxy);
			Assert.IsNotNull(Handler.Proxy);
			Uri Proxy = new("http://proxy.example.com:3128/");
			Assert.AreEqual(Proxy, Handler.Proxy.GetProxy(new Uri("https://cdn.example.com/dependencies/Pack/abc")));
			Assert.AreEqual(Proxy, Handler.Proxy.GetProxy(new Uri("http://cdn.example.com/dependencies/Pack/abc")));
			Assert.IsFalse(Handler.Proxy.IsBypassed(new Uri("https://cdn.example.com/")));
			NetworkCredential? Credentials = Handler.Proxy.Credentials as NetworkCredential;
			Assert.AreEqual("someuser", Credentials?.UserName);
			Assert.AreEqual("S3cretPw", Credentials?.Password);
			Assert.AreEqual(DecompressionMethods.None, Handler.AutomaticDecompression);
		}

		// No proxy anywhere: the handler keeps .NET's default.
		using HttpClientHandler Default = (HttpClientHandler)GitDepsServices.CreateDefaultHandler(GitDepsSettings.Resolve(Root, null, null, true, _ => null));
		Assert.IsNull(Default.Proxy);
	}

	// ---- names, proxies and credentials ----

	[TestMethod]
	[DataRow("../Outside.bin", "", "holds a '..' segment")]
	[DataRow("Engine/../../Outside.bin", "", "holds a '..' segment")]
	[DataRow("Engine/Content/../Inside.bin", "", "holds a '..' segment")]
	[DataRow("/Outside.bin", "", "is rooted")]
	[DataRow("../../Outside.bin", "plugin", "holds a '..' segment")]
	public async Task ANameOutsideTheRootIsRefused(string name, string where, string problem)
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((name, GitDepsEngine.Random(30, 89), false), ("Fine.bin", GitDepsEngine.Random(30, 90), false));
		if (where == "plugin")
		{
			Sandbox.Write(Path.Combine(Engine.Root, "Engine", "Plugins", "Thing", "Thing.uplugin"), "{}");
			Engine.WriteManifest(Path.Combine("Engine", "Plugins", "Thing", "Build", "Thing.gitdeps.xml"));
		}
		else
		{
			Engine.WriteManifest();
		}

		Assert.AreEqual(2, await new GitDepsStatusCommand(Engine.Services()).RunAsync(Engine.Context(), ["Engine", "-root=" + Engine.Root], CancellationToken.None));

		Assert.Contains(problem, Engine.Logger.Text);
		Assert.Contains("refuses a manifest that could write outside the engine root", Engine.Logger.Text);
	}

	[TestMethod]
	public void TheProxyComesFromOptionsThenUeGitDepsArgsThenHttpProxy()
	{
		string Root = Path.Combine(Path.GetTempPath(), "uak-gitdeps-root");
		Dictionary<string, string> Environment = new()
		{
			["UE_GITDEPS_ARGS"] = "--proxy=args.example.com:1 --proxy-user=argsuser --proxy-password=argspass",
			["HTTP_PROXY"] = "http://upper.example.com:2",
			["http_proxy"] = "http://lower.example.com:3",
		};
		GitDepsSettings Resolve(string? proxy) => GitDepsSettings.Resolve(Root, proxy, null, true, Name => Environment.GetValueOrDefault(Name));

		GitDepsSettings Given = Resolve("http://given.example.com:4");
		Assert.AreEqual("given.example.com", Given.Proxy!.Host);
		Assert.AreEqual("-proxy=", Given.ProxyFrom);
		// UE_GITDEPS_ARGS' -proxy-user= and -proxy-password= fill in what the URL lacks.
		Assert.AreEqual("argsuser", Given.ProxyCredentials!.UserName);
		Assert.AreEqual("argspass", Given.ProxyCredentials.Password);

		GitDepsSettings FromArguments = Resolve(null);
		Assert.AreEqual("args.example.com", FromArguments.Proxy!.Host);
		Assert.AreEqual(1, FromArguments.Proxy.Port);
		Assert.AreEqual("UE_GITDEPS_ARGS", FromArguments.ProxyFrom);

		Environment["UE_GITDEPS_ARGS"] = "--threads=8";
		GitDepsSettings FromUpper = Resolve(null);
		Assert.AreEqual("upper.example.com", FromUpper.Proxy!.Host);
		Assert.AreEqual("HTTP_PROXY", FromUpper.ProxyFrom);
		Assert.IsNull(FromUpper.ProxyCredentials);

		Environment.Remove("HTTP_PROXY");
		GitDepsSettings FromLower = Resolve(null);
		Assert.AreEqual("lower.example.com", FromLower.Proxy!.Host);
		Assert.AreEqual("http_proxy", FromLower.ProxyFrom);

		Environment.Remove("http_proxy");
		Assert.IsNull(Resolve(null).Proxy);

		// User info in the URL wins, and never stays in the proxy's URI.
		GitDepsSettings WithUser = Resolve("http://some%40user:pa%3Ass@given.example.com:4");
		Assert.AreEqual("some@user", WithUser.ProxyCredentials!.UserName);
		Assert.AreEqual("pa:ss", WithUser.ProxyCredentials.Password);
		Assert.AreEqual("", WithUser.Proxy!.UserInfo);
	}

	[TestMethod]
	public async Task TheManifestsIgnoreProxyIsIgnoredAsGitDependenciesDoes()
	{
		using GitDepsEngine Engine = new() { ManifestAttributes = " IgnoreProxy=\"true\"" };
		Engine.AddPack((IosPng, GitDepsEngine.Random(30, 91), false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-proxy=http://proxy.example.com:8080"), Engine.Logger.Text);

		Assert.AreEqual("proxy.example.com", Engine.HandlerSettings!.Proxy!.Host);
	}

	[TestMethod]
	public async Task ProxyCredentialsAreNeverPrinted()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack((IosPng, GitDepsEngine.Random(30, 92), false));
		Engine.WriteManifest();

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-proxy=http://someuser:S3cretPw@127.0.0.1:9", "-dry-run"), Engine.Logger.Text);
		Assert.Contains("proxy: http://127.0.0.1:9 (with credentials) (from -proxy=)", Engine.Logger.Text);

		Engine.Environment["UE_GITDEPS_ARGS"] = "-proxy=http://someuser:S3cretPw@127.0.0.1:9";
		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-dry-run"), Engine.Logger.Text);
		Assert.Contains("proxy: http://127.0.0.1:9 (with credentials) (from UE_GITDEPS_ARGS)", Engine.Logger.Text);

		Engine.Environment["UE_GITDEPS_ARGS"] = "-proxy=http://someuser:S3cretPw@[not a host";
		Assert.AreEqual(2, await Engine.FetchAsync(IosPng, "-dry-run"));
		Assert.Contains("is not a URL", Engine.Logger.Text);

		// A password with an unencoded '/', '?' or '#' makes the URL fail to parse; the error names only where it came from.
		Engine.Environment["UE_GITDEPS_ARGS"] = "-proxy=http://someuser:S3c/retPw@127.0.0.1:9";
		Assert.AreEqual(2, await Engine.FetchAsync(IosPng, "-dry-run"));
		Assert.Contains("the proxy from UE_GITDEPS_ARGS is not a URL", Engine.Logger.Text);

		Assert.DoesNotContain("someuser", Engine.Logger.Text);
		Assert.DoesNotContain("S3cretPw", Engine.Logger.Text);
		Assert.DoesNotContain("S3c", Engine.Logger.Text);
		Assert.DoesNotContain("retPw", Engine.Logger.Text);
	}

	[TestMethod]
	[DataRow("http://user:pa/ss@host:8080")]
	[DataRow("http://user:pa?ss@host:8080")]
	[DataRow("http://user:pa#ss@host:8080")]
	public void ARedactedUrlLosesItsUserInfoEvenWhenItDoesNotParse(string url)
	{
		Assert.AreEqual("http://host:8080", GitDepsRedact.Url(url));
		Assert.AreEqual("http://host:8080/Pack/abc", GitDepsRedact.Url(url + "/Pack/abc"));

		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(() => GitDepsSettings.Resolve(Path.GetTempPath(), url, null, true, _ => null));
		Assert.Contains("the proxy from -proxy= is not a URL", Error.Message);
		Assert.DoesNotContain("user:pa", Error.Message);
		Assert.DoesNotContain("ss@host", Error.Message);
	}

	[TestMethod]
	public void ARedactedUrlKeepsAUrlWithoutUserInfo()
	{
		Assert.AreEqual("https://cdn.example.com/dependencies/Pack/abc", GitDepsRedact.Url("https://cdn.example.com/dependencies/Pack/abc"));
		Assert.AreEqual("https://cdn.example.com/x", GitDepsRedact.Url("https://u:p@cdn.example.com/x"));
		Assert.AreEqual("proxy.example.com:8080", GitDepsRedact.Url("user:pass@proxy.example.com:8080"));
	}

	[TestMethod]
	public async Task CredentialsInTheBaseUrlAreNeverPrinted()
	{
		using GitDepsEngine Engine = new() { BaseUrl = "https://cdnuser:CdnS3cret@cdn.example.com/dependencies" };
		string Pack = Engine.AddPack((IosPng, GitDepsEngine.Random(30, 93), false));
		Engine.WriteManifest();
		Engine.Server.Content.Remove(Engine.UrlOf(Pack));

		Assert.AreEqual(0, await Engine.FetchAsync(IosPng, "-dry-run"), Engine.Logger.Text);
		Assert.AreEqual(1, await Engine.FetchAsync(IosPng), Engine.Logger.Text);

		Assert.Contains("https://cdn.example.com/dependencies/", Engine.Logger.Text);
		Assert.DoesNotContain("cdnuser", Engine.Logger.Text);
		Assert.DoesNotContain("CdnS3cret", Engine.Logger.Text);
	}

	// ---- output size ----

	[TestMethod]
	public async Task EachStateListsAtMostFiftyFiles()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack([.. Enumerable.Range(0, 60).Select(Index => ($"Engine/Content/File{Index:00}.bin", GitDepsEngine.Random(10, 1000 + Index), false))]);
		Engine.WriteManifest();

		Assert.AreEqual(0, await new GitDepsStatusCommand(Engine.Services()).RunAsync(Engine.Context(), ["Engine/Content", "-root=" + Engine.Root], CancellationToken.None));
		Assert.HasCount(50, Engine.Logger.Entries.Where(Entry => Entry.Message.StartsWith("missing ", StringComparison.Ordinal)));
		Assert.Contains("  and 10 more (-all lists every one)", Engine.Logger.Text);
		Assert.Contains("gitdeps status: 60 file(s): 0 present, 0 outdated, 0 modified, 60 missing; 60 untracked", Engine.Logger.Text);

		Engine.Logger.Entries.Clear();
		Assert.AreEqual(0, await new GitDepsStatusCommand(Engine.Services()).RunAsync(Engine.Context(), ["Engine/Content", "-root=" + Engine.Root, "-all"], CancellationToken.None));
		Assert.HasCount(60, Engine.Logger.Entries.Where(Entry => Entry.Message.StartsWith("missing ", StringComparison.Ordinal)));

		StringWriter Output = new();
		Assert.AreEqual(0, await new GitDepsStatusCommand(Engine.Services(Output)).RunAsync(Engine.Context(), ["Engine/Content", "-root=" + Engine.Root, "-json"], CancellationToken.None));
		using JsonDocument Report = JsonDocument.Parse(Output.ToString());
		Assert.AreEqual(60, Report.RootElement.GetProperty("Files").GetArrayLength());

		// A refusal lists at most 50 too, whatever -all says.
		foreach (int Index in Enumerable.Range(0, 60))
		{
			Sandbox.Write(Engine.PathOf($"Engine/Content/File{Index:00}.bin"), "local change");
		}
		Engine.Logger.Entries.Clear();
		Assert.AreEqual(2, await Engine.FetchAsync("Engine/Content", "-all"));
		Assert.HasCount(50, Engine.Logger.Entries.Where(Entry => Entry.Message.StartsWith("modified: ", StringComparison.Ordinal)));
		Assert.Contains("  and 10 more", Engine.Logger.Text);
	}

	// ---- uak gitdeps status ----

	[TestMethod]
	public async Task StatusReportsPresentModifiedMissingAndTracking()
	{
		using GitDepsEngine Engine = new();
		byte[] Present = GitDepsEngine.Random(40, 24);
		Engine.AddPack(("Engine/Content/Present.bin", Present, false), ("Engine/Content/Modified.bin", GitDepsEngine.Random(40, 25), true), ("Engine/Content/Missing.bin", GitDepsEngine.Random(40, 26), false));
		Engine.WriteManifest();
		File.WriteAllBytes(Sandbox.Write(Engine.PathOf("Engine/Content/Present.bin"), ""), Present);
		Sandbox.Write(Engine.PathOf("Engine/Content/Modified.bin"), "changed");
		File.WriteAllText(Path.Combine(Engine.Root, ".uedependencies"), """
			<?xml version="1.0" encoding="utf-8"?>
			<WorkingManifest>
			  <Files>
			    <File Name="Engine/Content/Present.bin" Hash="00" ExpectedHash="00" Timestamp="1" />
			    <File Name="engine/content/missing.bin" Hash="00" ExpectedHash="00" Timestamp="1" />
			  </Files>
			</WorkingManifest>
			""");
		StringWriter Output = new();

		int Exit = await new GitDepsStatusCommand(Engine.Services(Output)).RunAsync(Engine.Context(), ["Engine/Content", "-root=" + Engine.Root, "-json"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Engine.Logger.Text);
		using JsonDocument Report = JsonDocument.Parse(Output.ToString());
		Dictionary<string, JsonElement> Files = Report.RootElement.GetProperty("Files").EnumerateArray().ToDictionary(File => File.GetProperty("Name").GetString()!);
		Assert.HasCount(3, Files);
		// A saved timestamp that isn't the file's: the file is hashed, not trusted.
		Assert.AreEqual("Present", Files["Engine/Content/Present.bin"].GetProperty("State").GetString());
		Assert.IsTrue(Files["Engine/Content/Present.bin"].GetProperty("Tracked").GetBoolean());
		Assert.AreEqual("Modified", Files["Engine/Content/Modified.bin"].GetProperty("State").GetString());
		Assert.IsFalse(Files["Engine/Content/Modified.bin"].GetProperty("Tracked").GetBoolean());
		Assert.IsTrue(Files["Engine/Content/Modified.bin"].GetProperty("IsExecutable").GetBoolean());
		Assert.AreEqual("Missing", Files["Engine/Content/Missing.bin"].GetProperty("State").GetString());
		Assert.AreEqual(40, Files["Engine/Content/Missing.bin"].GetProperty("Size").GetInt64());
		// GitDependencies looks names up case-sensitively, so an entry in another case doesn't track the file.
		Assert.IsFalse(Files["Engine/Content/Missing.bin"].GetProperty("Tracked").GetBoolean());
	}

	[TestMethod]
	public async Task StatusTrustsTheSavedHashWhenTheTimestampMatches()
	{
		using GitDepsEngine Engine = new();
		byte[] Content = GitDepsEngine.Random(40, 27);
		Engine.AddPack(("Engine/Content/A.bin", Content, false));
		Engine.WriteManifest();
		string File = Sandbox.Write(Engine.PathOf("Engine/Content/A.bin"), "different bytes");
		long Ticks = System.IO.File.GetLastWriteTimeUtc(File).Ticks;
		// GitDependencies saved the manifest's hash at this write time, so the file counts as present without hashing.
		System.IO.File.WriteAllText(Path.Combine(Engine.Root, ".uedependencies"),
			$"""<WorkingManifest><Files><File Name="Engine/Content/A.bin" Hash="{GitDepsEngine.Sha1(Content)}" ExpectedHash="{GitDepsEngine.Sha1(Content)}" Timestamp="{Ticks}" /></Files></WorkingManifest>""");

		Assert.AreEqual(0, await new GitDepsStatusCommand(Engine.Services()).RunAsync(Engine.Context(), ["Engine/Content/A.bin", "-root=" + Engine.Root], CancellationToken.None));

		Assert.Contains("present   tracked    Engine/Content/A.bin", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task StatusWarnsOfAnUnreadableWorkingManifest()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack(("Engine/Content/A.bin", GitDepsEngine.Random(4, 94), false));
		Engine.WriteManifest();
		File.WriteAllText(Engine.WorkingPath, "<WorkingManifest><Files>");

		Assert.AreEqual(0, await new GitDepsStatusCommand(Engine.Services()).RunAsync(Engine.Context(), ["Engine/Content/A.bin", "-root=" + Engine.Root], CancellationToken.None));

		Assert.HasCount(1, Engine.Logger.Entries.Where(Entry => Entry.Level == LogLevel.Warning));
		Assert.Contains("missing   untracked  Engine/Content/A.bin", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task StatusRefusesAPatternThatMatchesNothing()
	{
		using GitDepsEngine Engine = new();
		Engine.AddPack(("Engine/Content/A.bin", GitDepsEngine.Random(4, 28), false));
		Engine.WriteManifest();

		Assert.AreEqual(2, await new GitDepsStatusCommand(Engine.Services()).RunAsync(Engine.Context(), ["Engine/Nothing", "-root=" + Engine.Root], CancellationToken.None));
		Assert.Contains("'Engine/Nothing' matches no file", Engine.Logger.Text);
	}

	[TestMethod]
	public async Task ARootWithoutManifestsIsASetupError()
	{
		using GitDepsEngine Engine = new();

		Assert.AreEqual(2, await new GitDepsStatusCommand(Engine.Services()).RunAsync(Engine.Context(), ["Engine", "-root=" + Engine.Root], CancellationToken.None));
		Assert.Contains("no gitdeps manifests", Engine.Logger.Text);
	}

	// ---- patterns and settings ----

	[TestMethod]
	public void PatternsMatchFilesFoldersAndGlobs()
	{
		string Root = Path.Combine(Path.GetTempPath(), "uak-gitdeps-root");
		Assert.IsTrue(GitDepsPattern.Parse("Engine/**/*.png", Root).Matches("Engine/a/b/c.png"));
		Assert.IsTrue(GitDepsPattern.Parse("Engine/**/*.png", Root).Matches("Engine/c.png"));
		Assert.IsFalse(GitDepsPattern.Parse("Engine/*.png", Root).Matches("Engine/a/c.png"));
		Assert.IsTrue(GitDepsPattern.Parse("engine\\source\\x?.TXT", Root).Matches("Engine/Source/x1.txt"));
		Assert.IsTrue(GitDepsPattern.Parse("Engine/Source", Root).Matches("Engine/Source/a/b.txt"));
		Assert.IsTrue(GitDepsPattern.Parse("./Engine/Source/", Root).Matches("Engine/Source/a/b.txt"));
		Assert.IsTrue(GitDepsPattern.Parse("./.tgitconfig", Root).Matches(".tgitconfig"));
		Assert.IsFalse(GitDepsPattern.Parse("Engine/Source", Root).Matches("Engine/SourceCode/b.txt"));
		Assert.IsTrue(GitDepsPattern.Parse(Path.Combine(Root, "Engine", "a.txt"), Root).Matches("Engine/a.txt"));
		Assert.ThrowsExactly<UakUsageException>(() => GitDepsPattern.Parse(Path.Combine(Path.GetTempPath(), "elsewhere", "a.txt"), Root));
		Assert.ThrowsExactly<UakUsageException>(() => GitDepsPattern.Parse("/", Root));
	}

	[TestMethod]
	public void SettingsComeFromOptionsThenUeGitDepsArgs()
	{
		string Root = Path.Combine(Path.GetTempPath(), "uak-gitdeps-root");
		Dictionary<string, string> Environment = new()
		{
			["UE_GITDEPS_ARGS"] = "--threads=8 --cache=\"C:\\Git Cache\" --proxy=user:pass@proxy.example.com:8080 --exclude=IOS",
			["UE_GITDEPS"] = "ignored",
		};
		string? Get(string name) => Environment.TryGetValue(name, out string? Value) ? Value : null;

		GitDepsSettings FromArguments = GitDepsSettings.Resolve(Root, null, null, false, Get);
		Assert.AreEqual(Path.GetFullPath("C:\\Git Cache"), FromArguments.CacheDirectory);
		Assert.AreEqual("UE_GITDEPS_ARGS", FromArguments.CacheFrom);
		Assert.AreEqual("proxy.example.com", FromArguments.Proxy!.Host);
		Assert.AreEqual("", FromArguments.Proxy.UserInfo);
		Assert.AreEqual("user", FromArguments.ProxyCredentials!.UserName);
		Assert.AreEqual("pass", FromArguments.ProxyCredentials.Password);
		Assert.AreEqual(8, FromArguments.Threads);

		GitDepsSettings Given = GitDepsSettings.Resolve(Root, "http://other:3128", Path.Combine(Root, "c"), false, Get);
		Assert.AreEqual("other", Given.Proxy!.Host);
		Assert.AreEqual(Path.Combine(Root, "c"), Given.CacheDirectory);

		Assert.IsNull(GitDepsSettings.Resolve(Root, null, null, true, Get).CacheDirectory);

		// As GitDependencies: an empty -cache= in UE_GITDEPS_ARGS skips UE_GITDEPS and falls back to the .git lookup.
		Environment["UE_GITDEPS_ARGS"] = "--cache=";
		GitDepsSettings EmptyCache = GitDepsSettings.Resolve(Root, null, null, false, Get);
		Assert.AreNotEqual(Path.GetFullPath("ignored"), EmptyCache.CacheDirectory);
		StringAssert.EndsWith(EmptyCache.CacheFrom, "(UE_GITDEPS_ARGS has an empty -cache=)");
		// uak's own -cache= still wins.
		GitDepsSettings OwnCache = GitDepsSettings.Resolve(Root, null, Path.Combine(Root, "c"), false, Get);
		Assert.AreEqual(Path.Combine(Root, "c"), OwnCache.CacheDirectory);
		Assert.AreEqual("-cache=", OwnCache.CacheFrom);

		Environment.Remove("UE_GITDEPS_ARGS");
		Assert.AreEqual(Path.GetFullPath("ignored"), GitDepsSettings.Resolve(Root, null, null, false, Get).CacheDirectory);
	}

	[TestMethod]
	public void TheGitCacheIsFoundWhenUeGitDepsArgsHasAnEmptyCache()
	{
		using GitDepsEngine Engine = new();
		Directory.CreateDirectory(Path.Combine(Engine.Root, ".git"));
		Dictionary<string, string> Environment = new() { ["UE_GITDEPS_ARGS"] = "-cache=", ["UE_GITDEPS"] = "ignored" };

		GitDepsSettings Settings = GitDepsSettings.Resolve(Engine.Root, null, null, false, Name => Environment.GetValueOrDefault(Name));

		Assert.AreEqual(Path.Combine(Engine.Root, ".git", "ue-gitdeps"), Settings.CacheDirectory);
		Assert.AreEqual(".git/ue-gitdeps (UE_GITDEPS_ARGS has an empty -cache=)", Settings.CacheFrom);
	}

	[TestMethod]
	public void TheTimeoutMultiplierIsParsedInTheUsersCulture()
	{
		const string Prefix = "-http-timeout-multiplier=";
		System.Globalization.NumberFormatInfo CommaDecimal = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

		// As GitDependencies' float.TryParse in a comma-decimal locale: "1,5" is 1.5, and "1.5" is 15 (the dot groups digits).
		Assert.AreEqual(1.5f, GitDepsSettings.ParseFloat(Prefix + "1,5", Prefix, CommaDecimal));
		Assert.AreEqual(15f, GitDepsSettings.ParseFloat(Prefix + "1.5", Prefix, CommaDecimal));
		Assert.AreEqual(1.5f, GitDepsSettings.ParseFloat(Prefix + "1.5", Prefix, System.Globalization.NumberFormatInfo.InvariantInfo));
		Assert.IsNull(GitDepsSettings.ParseFloat(Prefix + "fast", Prefix, System.Globalization.NumberFormatInfo.InvariantInfo));
		Assert.IsNull(GitDepsSettings.ParseFloat("-threads=4", Prefix, System.Globalization.NumberFormatInfo.InvariantInfo));
	}

	[TestMethod]
	public void TheHelpSaysWhatAFetchWritesAndWhen()
	{
		string Fetch = new GitDepsFetchCommand().Usage;
		Assert.Contains("safe while builds run", Fetch);
		Assert.Contains("safe while builds run", new GitDepsStatusCommand().Usage);
		Assert.Contains("each fetched file is recorded in it as GitDependencies records what it", Fetch);
		Assert.Contains("A failure before the renames writes nothing.", Fetch);
		Assert.Contains("If a rename fails, the files already renamed stay", Fetch);
		Assert.Contains("'Engine/**/IOS/*.png'", Fetch);
		Assert.Contains("./.name", Fetch);
		Assert.Contains("an upper bound", Fetch);
		Assert.DoesNotContain("never changed", Fetch);
		Assert.Contains("One fetch writes to an engine at a time", Fetch);
		Assert.Contains("never on uak's command line", Fetch);
		Assert.DoesNotContain("user:password@ in the URL", Fetch);
	}
}

/// <summary>A fake source engine root with gitdeps manifests, and an in-process server for its packs.</summary>
internal sealed class GitDepsEngine : IDisposable
{
	readonly List<(string Name, string Hash, bool Executable)> Files = [];
	readonly List<(string Hash, long Size, string Pack, long Offset)> Blobs = [];
	readonly List<(string Hash, long Size, long Compressed, string RemotePath)> Packs = [];

	public GitDepsEngine()
	{
		Root = Path.Combine(Path.GetTempPath(), "uak-gitdeps-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(Root, "Engine", "Build"));
	}

	public string Root { get; }

	public string BaseUrl { get; init; } = "https://cdn.example.com/dependencies";

	/// <summary>Extra attributes for the manifests' DependencyManifest element.</summary>
	public string ManifestAttributes { get; init; } = "";

	public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>The time limit for one try at a pack, in place of GitDependencies' formula; null for the formula.</summary>
	public TimeSpan? TransferTimeout { get; init; }

	/// <summary>What the fetch's check for a running GitDependencies answers; a test may change it during a fetch.</summary>
	public bool GitDependenciesRunning { get; set; }

	/// <summary>Called once a fetch has staged and checked every file, before the renames.</summary>
	public Action? AfterStaging { get; set; }

	/// <summary>Deletes a file while .uedependencies is written, in place of File.Delete.</summary>
	public Action<string>? DeleteFile { get; set; }

	public ListLogger Logger { get; } = new();

	public FakePackServer Server { get; } = new();

	public Dictionary<string, string> Environment { get; } = [];

	/// <summary>The waits between tries, in order.</summary>
	public List<TimeSpan> Delays { get; } = [];

	/// <summary>The settings the last HTTP handler was made with.</summary>
	public GitDepsSettings? HandlerSettings { get; private set; }

	public string WorkingPath => Path.Combine(Root, ".uedependencies");

	public static byte[] Random(int size, int seed)
	{
		byte[] Bytes = new byte[size];
		new Random(seed).NextBytes(Bytes);
		return Bytes;
	}

	public static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

	public string PathOf(string name) => Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));

	public string UrlOf(string pack) => $"{BaseUrl}/{Packs.Single(Pack => Pack.Hash == pack).RemotePath}/{pack}";

	public long CompressedSizeOf(string pack) => Packs.Single(Pack => Pack.Hash == pack).Compressed;

	/// <summary>Adds a pack holding the files' blobs end to end (one blob per distinct content), and serves it; returns its hash.</summary>
	public string AddPack(params (string Name, byte[] Content, bool Executable)[] files)
	{
		using MemoryStream Raw = new();
		List<(string Hash, long Size, long Offset)> PackBlobs = [];
		foreach ((string Name, byte[] Content, bool Executable) in files)
		{
			string Hash = Sha1(Content);
			Files.Add((Name, Hash, Executable));
			if (!PackBlobs.Any(Blob => Blob.Hash == Hash))
			{
				PackBlobs.Add((Hash, Content.Length, Raw.Position));
				Raw.Write(Content);
			}
		}
		byte[] Uncompressed = Raw.ToArray();
		string PackHash = Sha1(Uncompressed);
		byte[] Compressed = Gzip(Uncompressed);
		string RemotePath = "Pack" + Packs.Count;
		Packs.Add((PackHash, Uncompressed.Length, Compressed.Length, RemotePath));
		Blobs.AddRange(PackBlobs.Select(Blob => (Blob.Hash, Blob.Size, PackHash, Blob.Offset)));
		Server.Content[$"{BaseUrl}/{RemotePath}/{PackHash}"] = Compressed;
		return PackHash;
	}

	/// <summary>Adds a file and a blob as given, for manifests GitDepsEngine wouldn't make itself.</summary>
	public void AddRawEntry(string name, string hash, long size, string pack, long offset)
	{
		Files.Add((name, hash, false));
		Blobs.Add((hash, size, pack, offset));
	}

	/// <summary>Replaces a blob's bytes in the served pack with other bytes of the same length, leaving the manifest as it was.</summary>
	public void CorruptBlob(byte[] content, byte[] replacement)
	{
		(string Hash, long Size, string Pack, long Offset) Blob = Blobs.Single(Each => Each.Hash == Sha1(content));
		string Url = UrlOf(Blob.Pack);
		byte[] Uncompressed = Gunzip(Server.Content[Url]);
		replacement.CopyTo(Uncompressed, Blob.Offset);
		Server.Content[Url] = Gzip(Uncompressed);
	}

	/// <summary>Writes one manifest of everything added so far, at a path from the root.</summary>
	public void WriteManifest(string? relativePath = null)
	{
		StringBuilder Xml = new();
		Xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
		Xml.AppendLine($"<DependencyManifest xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" BaseUrl=\"{BaseUrl}\"{ManifestAttributes}>");
		Xml.AppendLine("  <Files>");
		foreach ((string Name, string Hash, bool Executable) in Files)
		{
			Xml.AppendLine($"    <File Name=\"{Name}\" Hash=\"{Hash}\"{(Executable ? " IsExecutable=\"true\"" : "")} />");
		}
		Xml.AppendLine("  </Files>");
		Xml.AppendLine("  <Blobs>");
		foreach ((string Hash, long Size, string Pack, long Offset) in Blobs)
		{
			Xml.AppendLine($"    <Blob Hash=\"{Hash}\" Size=\"{Size}\" PackHash=\"{Pack}\" PackOffset=\"{Offset}\" />");
		}
		Xml.AppendLine("  </Blobs>");
		Xml.AppendLine("  <Packs>");
		foreach ((string Hash, long Size, long Compressed, string RemotePath) in Packs)
		{
			Xml.AppendLine($"    <Pack Hash=\"{Hash}\" Size=\"{Size}\" CompressedSize=\"{Compressed}\" RemotePath=\"{RemotePath}\" />");
		}
		Xml.AppendLine("  </Packs>");
		Xml.AppendLine("</DependencyManifest>");
		Sandbox.Write(Path.Combine(Root, relativePath ?? Path.Combine("Engine", "Build", "Commit.gitdeps.xml")), Xml.ToString());
	}

	/// <summary>
	/// Writes .uedependencies byte for byte as GitDependencies writes it (XmlSerializer through an indented XmlWriter over a
	/// StreamWriter: UTF-8 without a BOM, the platform's line ends, no line end after the last element).
	/// </summary>
	public void WriteWorkingManifest(params (string Name, string? Hash, string? ExpectedHash, long Timestamp)[] files)
	{
		string NewLine = System.Environment.NewLine;
		StringBuilder Xml = new();
		Xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(NewLine);
		Xml.Append("<WorkingManifest xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">").Append(NewLine);
		if (files.Length == 0)
		{
			Xml.Append("  <Files />").Append(NewLine);
		}
		else
		{
			Xml.Append("  <Files>").Append(NewLine);
			foreach ((string Name, string? Hash, string? ExpectedHash, long Timestamp) in files)
			{
				Xml.Append($"    <File Name=\"{EscapeAttribute(Name)}\"{(Hash is null ? "" : $" Hash=\"{Hash}\"")}{(ExpectedHash is null ? "" : $" ExpectedHash=\"{ExpectedHash}\"")} Timestamp=\"{Timestamp}\" />").Append(NewLine);
			}
			Xml.Append("  </Files>").Append(NewLine);
		}
		Xml.Append("</WorkingManifest>");
		File.WriteAllText(WorkingPath, Xml.ToString());
	}

	/// <summary>An attribute value escaped as XmlWriter escapes it.</summary>
	static string EscapeAttribute(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

	/// <summary>The working manifest's entry for a name, or null.</summary>
	public GitDepsWorkingFile? WorkingEntry(string name) => GitDepsWorking.ReadManifest(WorkingPath).Files.FirstOrDefault(File => File.Name == name);

	public GitDepsServices Services(TextWriter? output = null) => new()
	{
		CreateHandler = Settings =>
		{
			HandlerSettings = Settings;
			return new FakePackHandler(Server);
		},
		GetEnvironmentVariable = Name => Environment.GetValueOrDefault(Name),
		IsGitDependenciesRunning = () => GitDependenciesRunning,
		Delay = (Wait, _) =>
		{
			Delays.Add(Wait);
			return Task.CompletedTask;
		},
		IdleTimeout = IdleTimeout,
		TransferTimeout = TransferTimeout,
		AfterStaging = () => AfterStaging?.Invoke(),
		DeleteFile = Target => (DeleteFile ?? System.IO.File.Delete)(Target),
		Output = output,
	};

	public UakContext Context() => new()
	{
		StateDirectory = new DirectoryInfo(Path.Combine(Root, "state")),
		Logger = Logger,
	};

	public Task<int> FetchAsync(params string[] arguments) => FetchAsync(CancellationToken.None, arguments);

	public Task<int> FetchAsync(CancellationToken cancellationToken, params string[] arguments) =>
		new GitDepsFetchCommand(Services()).RunAsync(Context(), [.. arguments, "-root=" + Root], cancellationToken);

	/// <summary>Temporary files a fetch left.</summary>
	public string[] TemporaryFiles() => Directory.GetFiles(Root, "*.uak-*.tmp", SearchOption.AllDirectories);

	static byte[] Gzip(byte[] data)
	{
		using MemoryStream Output = new();
		using (GZipStream Zip = new(Output, CompressionLevel.Fastest, leaveOpen: true))
		{
			Zip.Write(data);
		}
		return Output.ToArray();
	}

	static byte[] Gunzip(byte[] data)
	{
		using GZipStream Zip = new(new MemoryStream(data), CompressionMode.Decompress);
		using MemoryStream Output = new();
		Zip.CopyTo(Output);
		return Output.ToArray();
	}

	public void Dispose()
	{
		try
		{
			foreach (string File in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
			{
				System.IO.File.SetAttributes(File, FileAttributes.Normal);
			}
			Directory.Delete(Root, recursive: true);
		}
		catch (IOException)
		{
		}
	}
}

/// <summary>What a scripted response does: answer 503, answer another status, or serve the pack and break or stall after some bytes.</summary>
internal enum FakeFaultKind
{
	ServiceUnavailable,
	Status,
	Break,
	Stall,
}

/// <summary>A scripted response: for <see cref="FakeFaultKind.Status"/>, the status code, and a Retry-After in seconds unless negative.</summary>
internal sealed record FakeFault(FakeFaultKind Kind, long AfterBytes = 0, int Status = 0, int RetryAfterSeconds = -1);

/// <summary>Packs by URL, the requests made, the bytes served, and scripted faults, one per request in order.</summary>
internal sealed class FakePackServer
{
	readonly object Gate = new();
	long Served;

	public Dictionary<string, byte[]> Content { get; } = [];

	public List<string> Requests { get; } = [];

	public Queue<FakeFault> Faults { get; } = [];

	/// <summary>Called with the bytes served so far, after each read.</summary>
	public Action<long>? OnRead { get; set; }

	public long BytesServed => Interlocked.Read(ref Served);

	public HttpResponseMessage Answer(HttpRequestMessage request)
	{
		lock (Gate)
		{
			string Url = request.RequestUri!.ToString();
			Requests.Add(Url);
			FakeFault? Fault = Faults.Count > 0 ? Faults.Dequeue() : null;
			if (Fault is { Kind: FakeFaultKind.ServiceUnavailable })
			{
				return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
			}
			if (Fault is { Kind: FakeFaultKind.Status })
			{
				HttpResponseMessage Response = new((HttpStatusCode)Fault.Status);
				if (Fault.RetryAfterSeconds >= 0)
				{
					Response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(Fault.RetryAfterSeconds));
				}
				return Response;
			}
			return Content.TryGetValue(Url, out byte[]? Bytes)
				? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FakePackStream(this, Bytes, Fault)) }
				: new HttpResponseMessage(HttpStatusCode.NotFound);
		}
	}

	internal void Count(int bytes) => OnRead?.Invoke(Interlocked.Add(ref Served, bytes));
}

/// <summary>A served pack: counts what is read, and breaks or stalls where its fault says.</summary>
internal sealed class FakePackStream(FakePackServer server, byte[] bytes, FakeFault? fault) : Stream
{
	int Offset;

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => bytes.Length;
	public override long Position { get => Offset; set => throw new NotSupportedException(); }

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		int Limit = bytes.Length;
		if (fault is { Kind: FakeFaultKind.Break or FakeFaultKind.Stall })
		{
			if (Offset >= fault.AfterBytes)
			{
				if (fault.Kind == FakeFaultKind.Break)
				{
					throw new IOException("the connection was reset (test)");
				}
				await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
			}
			Limit = (int)Math.Min(Limit, fault.AfterBytes);
		}
		int Count = Math.Min(buffer.Length, Limit - Offset);
		bytes.AsMemory(Offset, Count).CopyTo(buffer);
		Offset += Count;
		server.Count(Count);
		return Count;
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

/// <summary>An HTTP handler that answers from a <see cref="FakePackServer"/>, without a socket.</summary>
internal sealed class FakePackHandler(FakePackServer server) : HttpMessageHandler
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(server.Answer(request));
}
