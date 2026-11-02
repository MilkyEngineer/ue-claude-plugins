// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
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

		UakRefusalException Refused = await Assert.ThrowsExactlyAsync<UakRefusalException>(() => new LockRunCommand().RunAsync(State.Context, ["-name=Guarded", "--", "never-started"], CancellationToken.None));

		Assert.Contains("uak mutate -restore -name=M", Refused.Message);
		Assert.AreEqual("mutant\n", File.ReadAllText(Source));
	}

	/// <summary>The file a rebuild leaves when an edit overlapped it.</summary>
	static void LeaveRebuildNeeded(TempState state)
	{
		File.WriteAllText(state.PathOf("B.h"), "");
		StateFiles.WriteJson(RebuildNeededFile.GetFile(state.Context), new[]
		{
			new RebuildNeededEntry { Target = "GameEditor", Platform = "Win64", Configuration = "Development", Stamped = ["A.h"] },
			new RebuildNeededEntry
			{
				Target = "Game", Platform = "Win64", Configuration = "Shipping", NotMarked = [state.PathOf("B.h")], Problems = ["could not be marked"],
				NotMarkedWritten = new(StringComparer.OrdinalIgnoreCase) { [state.PathOf("B.h")] = DateTime.UtcNow },
			},
		});
	}

	[TestMethod]
	public async Task LockRunRefusesWhileARebuildLeftBinariesThatMayMixOldAndNew()
	{
		using TempState State = new();
		LeaveRebuildNeeded(State);
		string Output = State.PathOf("ran.json");

		UakRefusalException Refused = await Assert.ThrowsExactlyAsync<UakRefusalException>(() => new LockRunCommand().RunAsync(State.Context, ["-name=Guarded", "--", .. Child.Command("env", Output)], CancellationToken.None));

		// A relative path in the marker is the project's.
		Assert.Contains($"`uak build -target=GameEditor -platform=Win64 -config=Development` for GameEditor Win64 Development ({State.PathOf("A.h")})", Refused.Message);
		Assert.Contains($"`uak build -target=Game -platform=Win64 -config=Shipping` for Game Win64 Shipping (not marked, so save first: {State.PathOf("B.h")})", Refused.Message);
		Assert.Contains($"delete {RebuildNeededFile.GetFile(State.Context)}", Refused.Message);
		Assert.IsFalse(File.Exists(Output), "nothing ran");

		File.Delete(RebuildNeededFile.GetFile(State.Context));
		Assert.AreEqual(0, await new LockRunCommand().RunAsync(State.Context, ["-name=Guarded", "--", .. Child.Command("env", Output)], CancellationToken.None));
		Assert.IsTrue(File.Exists(Output));
	}

	[TestMethod]
	public async Task LockRunRefusesAMarkerItCantRead()
	{
		// Unread, it may hold a refusal.
		using TempState State = new();
		File.WriteAllText(RebuildNeededFile.GetFile(State.Context), "{ not json");

		UakRefusalException Refused = await Assert.ThrowsExactlyAsync<UakRefusalException>(() => new LockRunCommand().RunAsync(State.Context, ["-name=Guarded", "--", "never-started"], CancellationToken.None));

		Assert.Contains("can't be read", Refused.Message);
	}

	[TestMethod]
	public async Task ALockRunInsideAHoldLeavesTheMarkerToTheHolder()
	{
		// A pre-build step's run, inside the very `uak build` that clears the marker: refusing it would fail that build for good.
		using TempState State = new();
		LeaveRebuildNeeded(State);
		string Output = State.PathOf("ran.json");
		await using EditorLockHold Outer = await EditorLock.AcquireAsync(State.Context, "Outer", null, new EditorLockOptions { PollInterval = TimeSpan.FromMilliseconds(100) }, CancellationToken.None);

		// A join reads the marker this process inherited, so it takes a child.
		Process Inner = State.StartChildWithEnvironment(new Dictionary<string, string?> { [EditorLock.HeldVariable] = Outer.Marker },
			["lock-run", State.StateDirectory, State.ProjectFile, "-", "-name=Inner", "--", .. Child.Command("env", Output)]);

		Assert.IsTrue(Inner.WaitForExit(TimeSpan.FromSeconds(30)));
		Assert.AreEqual(0, Inner.ExitCode);
		Assert.IsTrue(File.Exists(Output));
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

		UakUsageException Refused = Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(State.Context, null, building: false));
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

	/// <summary>Writes an empty file whose write time is <paramref name="age"/> ago.</summary>
	static string WriteAged(string path, TimeSpan age)
	{
		File.WriteAllBytes(path, []);
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
		return path;
	}

	/// <summary>Older than a leftover must be, and younger.</summary>
	static readonly TimeSpan Old = MutationJournal.LeftoverAge + TimeSpan.FromMinutes(10);
	static readonly TimeSpan Young = MutationJournal.LeftoverAge - TimeSpan.FromMinutes(10);

	[TestMethod]
	public void AProbeIsNamedAsUaksAndDeletesThoseOfProcessesThatEnded()
	{
		// A run killed between a probe's write and its delete leaves it in a source folder: the next probe there deletes it (K7-F-6),
		// once it is old too: a probe of another machine's live run, on a share, has a process ID this machine can't ask about (K8-F-2).
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		File.WriteAllText(Source, "original\n");
		string Folder = Path.GetDirectoryName(Source)!;
		string Stale = WriteAged($"{State.PathOf("Bar.cpp")}.{int.MaxValue}.{Guid.NewGuid():N}{MutationJournal.ProbeSuffix}", Old);
		string Live = WriteAged($"{State.PathOf("Bar.cpp")}.{Environment.ProcessId}.{Guid.NewGuid():N}{MutationJournal.ProbeSuffix}", Old);
		string Recent = WriteAged($"{State.PathOf("Bar.cpp")}.{int.MaxValue}.{Guid.NewGuid():N}{MutationJournal.ProbeSuffix}", Young);
		// A process id that ended, but no guid after it: a name the probe doesn't give.
		string Alike = WriteAged(State.PathOf($"Bar.cpp.{int.MaxValue}.notes{MutationJournal.ProbeSuffix}"), Old);
		List<string> Written = [];
		using FileSystemWatcher Watcher = new(Folder, "Foo.cpp.*");
		Watcher.Created += (_, Args) =>
		{
			lock (Written)
			{
				Written.Add(Args.Name!);
			}
		};
		Watcher.EnableRaisingEvents = true;

		Assert.IsNotNull(MutationJournal.ProbeFileTime(Source));

		Assert.IsFalse(File.Exists(Stale), "its process has ended, and it is old");
		Assert.AreEqual(TimeSpan.FromHours(1), MutationJournal.LeftoverAge, "the age DESIGN states");
		CollectionAssert.AreEquivalent(new[] { Live, Recent, Alike }, Directory.GetFiles(Folder, "*" + MutationJournal.ProbeSuffix),
			"the live one stays, so does a recent one whose process ID this machine doesn't know, and a name the probe doesn't give; its own is gone");
		Assert.IsTrue(SpinWait.SpinUntil(() =>
		{
			lock (Written)
			{
				return Written.Count > 0;
			}
		}, TimeSpan.FromSeconds(5)), "no probe was seen");
		lock (Written)
		{
			Assert.IsTrue(Written.All(Name => Name.EndsWith(MutationJournal.ProbeSuffix, StringComparison.Ordinal)), string.Join(", ", Written));
		}
	}

	[TestMethod]
	public void AStampDeletesTheProbeARunKilledBeforeItLeft()
	{
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		string Created = State.PathOf("Created.cpp");
		File.WriteAllText(Source, "original\n");
		// Each of these runs after the files are back, in phase Restored, where a restore no longer looks.
		string Probe = WriteAged($"{Source}.{int.MaxValue}.{Guid.NewGuid():N}{MutationJournal.ProbeSuffix}", Old);
		string ProbeOfDeleted = WriteAged($"{Created}.{int.MaxValue}.{Guid.NewGuid():N}{MutationJournal.ProbeSuffix}", Old);
		string Written = WriteAged($"{Source}.{int.MaxValue}.{Guid.NewGuid():N}.tmp", Old);
		// What a live run may be writing: this machine's, or a recent one of a process ID this machine doesn't know (K8).
		string Live = WriteAged($"{Source}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp", Old);
		string Recent = WriteAged($"{Source}.{int.MaxValue}.{Guid.NewGuid():N}.tmp", Young);
		string RecentProbe = WriteAged($"{Source}.{int.MaxValue}.{Guid.NewGuid():N}{MutationJournal.ProbeSuffix}", Young);
		string Alike = WriteAged(Source + ".notes.tmp", Old);
		DateTime Now = DateTime.UtcNow;

		MutationJournal.StampFiles([Source, Created], Now.AddSeconds(3), Now);

		Assert.IsFalse(File.Exists(Probe));
		Assert.IsFalse(File.Exists(ProbeOfDeleted));
		Assert.IsFalse(File.Exists(Written));
		Assert.IsTrue(File.Exists(Live), "its process runs");
		Assert.IsTrue(File.Exists(Recent), "a live write's, maybe, on another machine");
		Assert.IsTrue(File.Exists(RecentProbe), "a live probe's, maybe, on another machine");
		Assert.IsTrue(File.Exists(Alike), "only a name WriteFile or the probe gives is theirs");
	}

	[TestMethod]
	public void AProbeGoneBeforeItsTimeIsReadGivesNoTime()
	{
		// Another machine's uak, building in the same tree on a share, may delete it between its write and its read; a missing file
		// reads back as 1601, which would be the build's end, and any time past it a save (K8-F-2).
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		File.WriteAllText(Source, "original\n");

		Assert.IsNull(MutationJournal.ProbeFileTime(Source, File.Delete));
		Assert.IsNotNull(MutationJournal.ProbeFileTime(Source, _ => { }));
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
	static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, IntPtr creation, IntPtr access, ref long write);

	/// <summary>Writes an empty file whose write time is past what a <see cref="DateTime"/> holds: a year after 9999, which only SetFileTime sets.</summary>
	static string WritePastDateTime(string path)
	{
		File.WriteAllBytes(path, []);
		using FileStream Stream = new(path, FileMode.Open, FileAccess.Write);
		long Write = DateTime.MaxValue.ToFileTimeUtc() + TimeSpan.FromDays(400).Ticks;
		Assert.IsTrue(SetFileTime(Stream.SafeFileHandle, IntPtr.Zero, IntPtr.Zero, ref Write), $"SetFileTime failed: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
		return path;
	}

	[TestMethod]
	public void AFileTimePastWhatADateTimeHoldsIsNoLeftoverAndThrowsNothing()
	{
		// File.GetLastWriteTimeUtc throws for it, which a probe or a stamp would let out of a restore or a build (K9-F-6). It
		// gives no age, so the file stays.
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("such a file time is set with SetFileTime, Windows'");
			return;
		}
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		File.WriteAllText(Source, "original\n");
		string Probe = WritePastDateTime($"{Source}.{int.MaxValue}.{Guid.NewGuid():N}{MutationJournal.ProbeSuffix}");
		string Written = WritePastDateTime($"{Source}.{int.MaxValue}.{Guid.NewGuid():N}.tmp");
		Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => File.GetLastWriteTimeUtc(Probe), "a time no DateTime holds");

		Assert.IsNotNull(MutationJournal.ProbeFileTime(Source));
		DateTime Now = DateTime.UtcNow;
		MutationJournal.StampFiles([Source], Now.AddSeconds(3), Now);
		MutationJournal.DeleteStaleProbes(Path.GetDirectoryName(Source)!);

		Assert.IsTrue(File.Exists(Probe), "a probe with no age is no leftover");
		Assert.IsTrue(File.Exists(Written), "nor is a temporary file");
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
	public void AProjectCommandBuiltAgainstAnOlderKitStillFindsClearAfterBuild()
	{
		Assert.IsNotNull(typeof(MutationJournal).GetMethod(nameof(MutationJournal.ClearAfterBuild),
			[typeof(UakContext), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(Func<MutationFile, bool>)]));
	}

	[TestMethod]
	public void ARebuildThatCouldNotRunIsRecordedOnlyForItsOwnBuild()
	{
		using TempState State = new();
		MutationJournal Journal = LeaveRestored(State, "M", State.PathOf("Foo.cpp"));
		Journal.SetBuild("GameEditor", "Win64", "Development");
		DateTime Artifact = DateTime.UtcNow;
		Journal.StampForRecompile(Artifact.AddSeconds(2), Artifact);
		MutationRebuildFailure Failure = new() { Started = Artifact, Message = "could not start" };
		string Saved = Path.Combine(Journal.Directory, "journal.json");

		MutationJournal.RecordRebuildFailure(State.Context, "OtherEditor", "Win64", "Development", Failure);
		Assert.IsNull(StateFiles.ReadJson<MutationRecord>(Saved)!.RebuildFailure);

		MutationJournal.RecordRebuildFailure(State.Context, "gameeditor", "Win64", "Development", Failure);
		Assert.AreEqual("could not start", StateFiles.ReadJson<MutationRecord>(Saved)!.RebuildFailure!.Message);
	}

	[TestMethod]
	public void AnUnstampedJournalIsNeverSpentWhateverItsBinaries()
	{
		using TempState State = new();
		MutationJournal Journal = LeaveRestored(State, "M", State.PathOf("Foo.cpp"));
		Journal.SetBinaries([]);

		Assert.IsFalse(Journal.IsSpent(), "a build without the stamp may find the mutant's objects up to date");
		Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(State.Context, null, building: false));
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
		StringAssert.Contains(Assert.ThrowsExactly<UakRefusalException>(() => MutationJournal.ThrowIfUnfinished(State.Context, null, building: false)).Message,
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
