// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Tests;

[TestClass]
public sealed class MutationGuardTests
{
	[TestMethod]
	public async Task LockRunRefusesWhileAStoppedMutationRunLeftItsPatch()
	{
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		File.WriteAllText(Source, "original\n");
		MutationJournal Journal = MutationJournal.Begin(State.Context, "M", "p.diff", [Source]);
		File.WriteAllText(Source, "mutant\n");
		Journal.MarkApplied();
		// As if that uak had been killed.
		Journal.Record.Pid = int.MaxValue;
		StateFiles.WriteJson(Path.Combine(Journal.Directory, "journal.json"), Journal.Record);

		UakUsageException Refused = await Assert.ThrowsExactlyAsync<UakUsageException>(() => new LockRunCommand().RunAsync(State.Context, ["-name=Guarded", "--", "never-started"], CancellationToken.None));

		Assert.Contains("uak mutate -restore -name=M", Refused.Message);
		Assert.AreEqual("mutant\n", File.ReadAllText(Source));
	}

	/// <summary>A journal whose files are back (phase Restored), as a stopped run would leave it.</summary>
	static MutationJournal LeaveRestored(TempState state, string name, string source)
	{
		File.WriteAllText(source, "original\n");
		MutationJournal Journal = MutationJournal.Begin(state.Context, name, "p.diff", [source]);
		File.WriteAllText(source, "mutant\n");
		Journal.MarkApplied();
		Assert.IsEmpty(Journal.Restore().Conflicts);
		return Journal;
	}

	[TestMethod]
	public void AJournalWhoseBinariesAreRewrittenIsSpentAndClearedByTheGuard()
	{
		using TempState State = new();
		string Dll = State.PathOf("UnrealEditor-Game.dll");
		File.WriteAllText(Dll, "mutant code");
		DateTime Artifact = DateTime.UtcNow.AddSeconds(-30);
		File.SetLastWriteTimeUtc(Dll, Artifact);
		MutationJournal Journal = LeaveRestored(State, "M", State.PathOf("Foo.cpp"));
		Journal.StampForRecompile(Artifact.AddSeconds(2), Artifact);
		Journal.SetBinaries([MutationBinary.Read(Dll)!]);

		UakUsageException Refused = Assert.ThrowsExactly<UakUsageException>(() => MutationJournal.ThrowIfUnfinished(State.Context, null, building: false));
		Assert.Contains(Dll + " may still hold its patch", Refused.Message);
		MutationJournal.ThrowIfUnfinished(State.Context, null, building: true);

		// Linked after the stamp: UBT recompiled the files first, so no binary holds the mutant.
		File.WriteAllText(Dll, "relinked without the mutation");
		MutationJournal.ThrowIfUnfinished(State.Context, null, building: false);
		Assert.IsFalse(Directory.Exists(Journal.Directory));
	}

	[TestMethod]
	public void ABinaryIsRelinkedOnlyWhenItIsNewerThanTheStampAndGoneCountsAsRelinked()
	{
		using TempState State = new();
		string Kept = State.PathOf("Kept.dll");
		string Gone = State.PathOf("Gone.dll");
		DateTime Artifact = DateTime.UtcNow.AddSeconds(-30);
		File.WriteAllText(Kept, "mutant");
		File.WriteAllText(Gone, "mutant");
		File.SetLastWriteTimeUtc(Kept, Artifact);
		File.SetLastWriteTimeUtc(Gone, Artifact);
		MutationJournal Journal = LeaveRestored(State, "M", State.PathOf("Foo.cpp"));
		Journal.SetBinaries([MutationBinary.Read(Kept)!, MutationBinary.Read(Gone)!]);
		Assert.HasCount(2, Journal.StillHeldBinaries()!, "not stamped yet: whatever exists may hold it");
		DateTime Stamp = Artifact.AddSeconds(2);
		Journal.StampForRecompile(Stamp, Artifact);

		// A time changed by a copy or a sync, and bytes changed before the stamp: nothing linked it after the files were back.
		File.SetLastWriteTimeUtc(Kept, Stamp.AddSeconds(-1));
		Assert.HasCount(2, Journal.StillHeldBinaries()!);
		File.SetLastWriteTimeUtc(Kept, Stamp);
		Assert.HasCount(2, Journal.StillHeldBinaries()!, "the stamp's very time isn't after it");
		File.Delete(Gone);
		Assert.AreEqual(Path.GetFullPath(Kept), Journal.StillHeldBinaries()!.Single().Path);
		File.SetLastWriteTimeUtc(Kept, Stamp.AddSeconds(1));
		Assert.IsEmpty(Journal.StillHeldBinaries()!);
		Assert.IsTrue(Journal.IsSpent());
	}

	[TestMethod]
	public void TheFileSystemsClockIsReadBesideAFileAndLeavesNothing()
	{
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		File.WriteAllText(Source, "original\n");
		DateTime Before = DateTime.UtcNow.AddSeconds(-5);

		Assert.IsGreaterThan(Before, MutationJournal.ProbeFileTime(Source)!.Value);

		Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(Source)!, "Foo.cpp*"));
		Assert.IsNull(MutationJournal.ProbeFileTime(State.PathOf(Path.Combine("NoSuchFolder", "Foo.cpp"))));
	}

	[TestMethod]
	public void AFileWhoseTimeCannotBeSetIsWrittenWholeWithItsOwnBytes()
	{
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		File.WriteAllText(Source, "original\n");
		File.SetAttributes(Source, FileAttributes.ReadOnly);
		DateTime Now = DateTime.UtcNow;
		File.SetLastWriteTimeUtc(Source, Now.AddHours(-1));
		static void Refuses(string path, DateTime time) => throw new UnauthorizedAccessException("no change of attributes on this share");

		MutationJournal.StampFiles([Source], Now.AddSeconds(-3), Now.AddSeconds(-5), Refuses);

		Assert.IsGreaterThan(Now.AddSeconds(-3), File.GetLastWriteTimeUtc(Source), "the clock's time, past the stamp");
		Assert.AreEqual("original\n", File.ReadAllText(Source));
		Assert.IsTrue(File.GetAttributes(Source).HasFlag(FileAttributes.ReadOnly));
		File.SetAttributes(Source, FileAttributes.Normal);

		// When the file system's own time isn't past the reference either, it didn't take.
		IOException Late = Assert.ThrowsExactly<IOException>(() => MutationJournal.StampFiles([Source], Now.AddHours(1), Now.AddHours(1), Refuses));
		Assert.Contains("for a recompile", Late.Message);
	}

	[TestMethod]
	public void AWholeWriteNeedsNoRightToChangeAttributesUnlessTheFileIsReadOnly()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("The refused right is a Windows ACL.");
			return;
		}
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		File.WriteAllText(Source, "original\n");
		DateTime Now = DateTime.UtcNow;
		File.SetLastWriteTimeUtc(Source, Now.AddHours(-1));
		FileInfo Info = new(Source);
		System.Security.AccessControl.FileSecurity Security = Info.GetAccessControl();
		Security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
			System.Security.AccessControl.FileSystemRights.WriteAttributes, System.Security.AccessControl.AccessControlType.Deny));
		Info.SetAccessControl(Security);
		Assert.ThrowsExactly<UnauthorizedAccessException>(() => File.SetLastWriteTimeUtc(Source, Now), "the share this stands for refuses the time, so the bytes go instead");

		// With the real setter: the time is refused, and so would a change of attributes be, which the whole write doesn't ask for.
		MutationJournal.StampFiles([Source], Now.AddSeconds(-3), Now.AddSeconds(-5));

		Assert.IsGreaterThan(Now.AddSeconds(-3), File.GetLastWriteTimeUtc(Source));
		Assert.AreEqual("original\n", File.ReadAllText(Source));
	}

	[TestMethod]
	public void AStampDeletesTheProbeARunKilledBeforeItLeft()
	{
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		string Created = State.PathOf("Created.cpp");
		File.WriteAllText(Source, "original\n");
		// Each of these runs after the files are back, in phase Restored, where a restore no longer looks.
		string Probe = $"{Source}.1234.{Guid.NewGuid():N}.tmp";
		string ProbeOfDeleted = $"{Created}.1234.{Guid.NewGuid():N}.tmp";
		string Alike = Source + ".notes.tmp";
		File.WriteAllBytes(Probe, []);
		File.WriteAllBytes(ProbeOfDeleted, []);
		File.WriteAllBytes(Alike, []);
		DateTime Now = DateTime.UtcNow;

		MutationJournal.StampFiles([Source, Created], Now.AddSeconds(3), Now);

		Assert.IsFalse(File.Exists(Probe));
		Assert.IsFalse(File.Exists(ProbeOfDeleted));
		Assert.IsTrue(File.Exists(Alike), "only a name WriteFile or the probe gives is theirs");
	}

	[TestMethod]
	public void AJournalThatListsABinaryWithoutAFullPathCannotBeTrusted()
	{
		using TempState State = new();
		MutationJournal Journal = LeaveRestored(State, "M", State.PathOf("Foo.cpp"));
		Journal.SetBinaries([new MutationBinary { Path = "Relative.dll" }]);

		(List<MutationJournal> Journals, List<UnreadableMutationJournal> Unreadable) = MutationJournal.List(State.Context);

		Assert.IsEmpty(Journals);
		Assert.Contains("lists a binary without a full path", Unreadable.Single().Problem);
		Assert.IsNull(MutationBinary.Read("no\0such.dll"), "a path that can't be one is no binary");
	}

	[TestMethod]
	public void AShelfIsRefusedOnlyWhenItHoldsABinaryThatMayHoldTheMutation()
	{
		using TempState State = new();
		DirectoryInfo Folder = State.Context.StateDirectory;
		string Dll = State.PathOf("UnrealEditor-Game.dll");
		File.WriteAllText(Dll, "mutant code");
		MutationJournal Journal = LeaveRestored(State, "M", State.PathOf("Foo.cpp"));
		string[] Depot = ["//Project/Main/Game/Source/Foo.cpp", "//Project/Main/Game/Binaries/Win64/UnrealEditor-Game.dll"];
		string[] Other = ["//Project/Main/Game/Source/Foo.cpp", "//Project/Main/Game/Binaries/Win64/UnrealEditor-Other.dll"];
		Assert.IsTrue(MutationFolders.HasBinariesPending(Folder));

		// It doesn't say which it wrote: any file that loads as code.
		StringAssert.Contains(MutationFolders.DescribeBinariesIn(Folder, Other, "shelve"), "UnrealEditor-Other.dll");
		Assert.IsNull(MutationFolders.DescribeBinariesIn(Folder, [Other[0]], "shelve"));

		Journal.SetBinaries([MutationBinary.Read(Dll)!]);
		StringAssert.Contains(MutationFolders.DescribeBinariesIn(Folder, Depot, "shelve"), "UnrealEditor-Game.dll (run 'M')");
		Assert.IsNull(MutationFolders.DescribeBinariesIn(Folder, Other, "shelve"), "a binary its mutant build never wrote");

		Journal.Delete();
		Assert.IsFalse(MutationFolders.HasBinariesPending(Folder));
		Assert.IsNull(MutationFolders.DescribeBinariesIn(Folder, Depot, "shelve"));
	}

	[TestMethod]
	public void AnUnstampedJournalIsNeverSpentWhateverItsBinaries()
	{
		using TempState State = new();
		MutationJournal Journal = LeaveRestored(State, "M", State.PathOf("Foo.cpp"));
		Journal.SetBinaries([]);

		Assert.IsFalse(Journal.IsSpent(), "a build without the stamp may find the mutant's objects up to date");
		Assert.ThrowsExactly<UakUsageException>(() => MutationJournal.ThrowIfUnfinished(State.Context, null, building: false));
		Assert.IsEmpty(MutationJournal.ClearAfterBuild(State.Context, "GameEditor", "Win64", "Development", succeeded: true, _ => true));
	}

	[TestMethod]
	public void ABuildThatSucceededClearsOnlyStampedJournalsOfItsOwnTargetPlatformAndConfiguration()
	{
		using TempState State = new();
		MutationJournal Journal = LeaveRestored(State, "M", State.PathOf("Foo.cpp"));
		Journal.SetBuild("GameEditor", "Win64", "Development");
		DateTime Artifact = DateTime.UtcNow;
		Journal.StampForRecompile(Artifact.AddSeconds(2), Artifact);
		Assert.IsNull(Journal.Record.Binaries, "unknown: any binary may hold the mutant");
		Assert.IsFalse(Journal.IsSpent());
		List<string> Build(string Target, string Platform, string Configuration) => MutationJournal.ClearAfterBuild(State.Context, Target, Platform, Configuration, succeeded: true, _ => true);

		Assert.IsEmpty(Build("Game", "Win64", "Development"));
		Assert.IsEmpty(Build("GameEditor", "Win64", "DebugGame"));
		Assert.IsEmpty(Build("GameEditor", "Linux", "Development"));
		Assert.IsTrue(Directory.Exists(Journal.Directory));
		CollectionAssert.AreEqual(new[] { "M" }, Build("gameeditor", "Win64", "Development"));
		Assert.IsFalse(Directory.Exists(Journal.Directory));
	}

	[TestMethod]
	public void ABuildClearsAJournalOnlyOnceBuildsShowedEveryFileRecompiled()
	{
		using TempState State = new();
		string First = State.PathOf("First.cpp");
		string Second = State.PathOf("Second.cpp");
		File.WriteAllText(First, "original\n");
		File.WriteAllText(Second, "original\n");
		MutationJournal Journal = MutationJournal.Begin(State.Context, "M", "p.diff", [First, Second]);
		File.WriteAllText(First, "mutant\n");
		File.WriteAllText(Second, "mutant\n");
		Journal.MarkApplied();
		Assert.IsEmpty(Journal.Restore().Conflicts);
		Journal.SetBuild("GameEditor", "Win64", "Development");
		DateTime Artifact = DateTime.UtcNow;
		Journal.StampForRecompile(Artifact.AddSeconds(2), Artifact);
		CollectionAssert.AreEqual(new[] { First, Second }, Journal.Record.AwaitingRecompile, "not known what the mutant build compiled: all of them");
		List<string> Build(bool Succeeded, params string[] Compiled) =>
			MutationJournal.ClearAfterBuild(State.Context, "GameEditor", "Win64", "Development", Succeeded, Entry => Compiled.Contains(Entry.Path));

		// Up to date: it may have kept the mutant's objects.
		Assert.IsEmpty(Build(true));
		Assert.IsFalse(Journal.HasRecompiled());

		// A build that fails elsewhere counts for what it compiled, and the next one needn't compile it again.
		Assert.IsEmpty(Build(false, First));
		CollectionAssert.AreEqual(new[] { Second }, StateFiles.ReadJson<MutationRecord>(Path.Combine(Journal.Directory, "journal.json"))!.AwaitingRecompile, "saved");
		Assert.IsEmpty(Build(true, First));
		CollectionAssert.AreEqual(new[] { "M" }, Build(true, Second));
		Assert.IsFalse(Directory.Exists(Journal.Directory));
	}

	[TestMethod]
	public void ABuildThatFailsNeverClearsAJournalEvenWithEveryFileRecompiled()
	{
		using TempState State = new();
		MutationJournal Journal = LeaveRestored(State, "M", State.PathOf("Foo.cpp"));
		Journal.SetBuild("GameEditor", "Win64", "Development");
		DateTime Artifact = DateTime.UtcNow;
		Journal.StampForRecompile(Artifact.AddSeconds(2), Artifact);

		Assert.IsEmpty(MutationJournal.ClearAfterBuild(State.Context, "GameEditor", "Win64", "Development", succeeded: false, _ => true));

		Assert.IsTrue(Directory.Exists(Journal.Directory), "it may not have linked them");
		Assert.IsTrue(MutationJournal.List(State.Context).Journals.Single().HasRecompiled());
		StringAssert.Contains(Assert.ThrowsExactly<UakUsageException>(() => MutationJournal.ThrowIfUnfinished(State.Context, null, building: false)).Message,
			"the next `uak build` of GameEditor (Win64 Development) that succeeds clears this");
		CollectionAssert.AreEqual(new[] { "M" }, MutationJournal.ClearAfterBuild(State.Context, "GameEditor", "Win64", "Development", succeeded: true, _ => false));
	}

	[TestMethod]
	public void TheShelveGuardLeavesOutOnlyAJournalWhoseFilesAreBack()
	{
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		File.WriteAllText(Source, "original\n");
		MutationJournal Journal = MutationJournal.Begin(State.Context, "M", "p.diff", [Source]);
		StringAssert.Contains(MutationFolders.DescribeLeft(State.Context.StateDirectory, "shelve"), Source, "applying");
		File.WriteAllText(Source, "mutant\n");
		Journal.MarkApplied();
		StringAssert.Contains(MutationFolders.DescribeLeft(State.Context.StateDirectory, "shelve"), Source, "applied");

		Assert.IsEmpty(Journal.Restore().Conflicts);
		Assert.IsNull(MutationFolders.DescribeLeft(State.Context.StateDirectory, "shelve"), "the files are back: only binaries may hold the patch, and no shelf ships them");

		// A journal that can't be read may say anything: it is in.
		string Record = Path.Combine(Journal.Directory, "journal.json");
		File.WriteAllText(Record, File.ReadAllText(Record)[..40]);
		Assert.IsNotNull(MutationFolders.DescribeLeft(State.Context.StateDirectory, "shelve"));
	}
}
