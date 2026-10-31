// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Vcs;

namespace AgentKit.Unreal.Tests;

[TestClass]
public sealed class MutateTests
{
	// ---- UnifiedDiff ----

	static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

	static string Text(byte[]? bytes) => bytes is null ? "<deleted>" : Encoding.UTF8.GetString(bytes);

	const string GitPatch =
		"""
		diff --git a/Source/Game/Private/Foo.cpp b/Source/Game/Private/Foo.cpp
		index 1111111..2222222 100644
		--- a/Source/Game/Private/Foo.cpp
		+++ b/Source/Game/Private/Foo.cpp
		@@ -1,4 +1,4 @@
		 int Add(int A, int B)
		 {
		-	return A + B;
		+	return A - B;
		 }

		""";

	[TestMethod]
	public void AGitPatchLosesItsPrefixesAndKeepsTheFilesLineEnds()
	{
		DiffFile File = UnifiedDiff.Parse(GitPatch).Single();
		Assert.AreEqual("Source/Game/Private/Foo.cpp", File.Path);
		Assert.AreEqual(1, File.Added);
		Assert.AreEqual(1, File.Removed);

		Assert.AreEqual("int Add(int A, int B)\r\n{\r\n\treturn A - B;\r\n}\r\n", Text(UnifiedDiff.Apply(File, Bytes("int Add(int A, int B)\r\n{\r\n\treturn A + B;\r\n}\r\n"))));
		Assert.AreEqual("int Add(int A, int B)\n{\n\treturn A - B;\n}\n", Text(UnifiedDiff.Apply(File, Bytes("int Add(int A, int B)\n{\n\treturn A + B;\n}\n"))));
		// The patch says the file ends with a line end ("\ No newline at end of file" would say otherwise): this one doesn't.
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(File, Bytes("int Add(int A, int B)\n{\n\treturn A + B;\n}")));
		byte[] WithBom = UnifiedDiff.Apply(File, [0xEF, 0xBB, 0xBF, .. Bytes("int Add(int A, int B)\n{\n\treturn A + B;\n}\n")])!;
		CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, WithBom.Take(3).ToArray());
	}

	[TestMethod]
	public void AHunkAppliesOnlyAtTheLineItsHeaderNames()
	{
		DiffFile File = UnifiedDiff.Parse(GitPatch).Single();

		// R-3: lines added above since the patch was made: no search (git and a search here would pick different places).
		PatchException Moved = Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(File, Bytes("// new\n// lines\nint Add(int A, int B)\n{\n\treturn A + B;\n}\n")));
		Assert.Contains("at the line its header names", Moved.Message);
		PatchException Error = Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(File, Bytes("int Add(int A, int B)\n{\n\treturn A + B + 0;\n}\n")));
		Assert.Contains("hunk 1", Error.Message);
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(File, null));

		// Review 5's gitorder: the block the hunk names matches at line 4 and line 8, not at 6. uak searched up first (line 4),
		// git searches down first (line 8): refused either way.
		const string Twice = "l1\nl2\nl3\n{\n\treturn true;\n}\nl7\n{\n\treturn true;\n}\nl11\n";
		DiffFile Between = UnifiedDiff.Parse("--- a/F.cpp\n+++ b/F.cpp\n@@ -6,3 +6,3 @@\n {\n-\treturn true;\n+\treturn false;\n }\n").Single();
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(Between, Bytes(Twice)));
		Assert.AreEqual(Twice.Replace("l7\n{\n\treturn true;", "l7\n{\n\treturn false;", StringComparison.Ordinal),
			Text(UnifiedDiff.Apply(UnifiedDiff.Parse("--- a/F.cpp\n+++ b/F.cpp\n@@ -8,3 +8,3 @@\n {\n-\treturn true;\n+\treturn false;\n }\n").Single(), Bytes(Twice))));

		// git starts where a hunk's new start says: one that doesn't follow from its old start and the hunks above is refused.
		PatchException NewStart = Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(
			UnifiedDiff.Parse("--- a/F.cpp\n+++ b/F.cpp\n@@ -4,3 +8,3 @@\n {\n-\treturn true;\n+\treturn false;\n }\n").Single(), Bytes(Twice)));
		Assert.Contains("new start", NewStart.Message);
		DiffFile TwoHunks = UnifiedDiff.Parse("--- a/F.cpp\n+++ b/F.cpp\n@@ -1,2 +1,3 @@\n l1\n+added\n l2\n@@ -8,3 +9,3 @@\n {\n-\treturn true;\n+\treturn false;\n }\n").Single();
		StringAssert.StartsWith(Text(UnifiedDiff.Apply(TwoHunks, Bytes(Twice))), "l1\nadded\nl2\n");
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(TwoHunks with { Hunks = [TwoHunks.Hunks[0], TwoHunks.Hunks[1] with { NewStart = 8 }] }, Bytes(Twice)));
	}

	[TestMethod]
	public void AHunkWithoutContextAppliesOnlyAtTheLineItsHeaderNames()
	{
		byte[] Four = Bytes("a\nb\nc\nd\n");

		// Searching would put it after the end of the file: diff -U0 says "after line 50".
		DiffFile Far = UnifiedDiff.Parse("--- a/A.cpp\n+++ b/A.cpp\n@@ -50,0 +51 @@\n+x\n").Single();
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(Far, Four));
		DiffFile Moved = UnifiedDiff.Parse("--- a/A.cpp\n+++ b/A.cpp\n@@ -3 +3 @@\n-b\n+B\n").Single();
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(Moved, Four), "b is line 2, not 3: no context, no search");

		// At the file's end, where git looks for a hunk without trailing context, it applies.
		Assert.AreEqual("a\nb\nc\nd\nx\n", Text(UnifiedDiff.Apply(UnifiedDiff.Parse("--- a/A.cpp\n+++ b/A.cpp\n@@ -4,0 +5 @@\n+x\n").Single(), Four)));
		Assert.AreEqual("a\nb\nc\nD\n", Text(UnifiedDiff.Apply(UnifiedDiff.Parse("--- a/A.cpp\n+++ b/A.cpp\n@@ -4 +4 @@\n-d\n+D\n").Single(), Four)));
	}

	[TestMethod]
	public void AHunkWithoutTrailingContextIsRefusedAwayFromTheFilesEnd()
	{
		// V-2 (a): git reads a hunk with no context line after its changes as the file's end, and applies it there whenever its
		// old lines match there too: here at line 5, where the header says line 2. uak's plan said line 2, so it took git's
		// write for a concurrent edit. Refused unless it ends at the file's end, which is where git puts it.
		byte[] Twice = Bytes("a\nX\nY\nb\nX\nY\n");
		DiffFile NoTrailing = UnifiedDiff.Parse("--- a/F.cpp\n+++ b/F.cpp\n@@ -2,2 +2,2 @@\n X\n-Y\n+MUT\n").Single();
		PatchException Error = Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(NoTrailing, Twice));
		Assert.Contains("no context line after its changes", Error.Message);
		Assert.AreEqual("a\nX\nY\nb\nX\nMUT\n", Text(UnifiedDiff.Apply(UnifiedDiff.Parse("--- a/F.cpp\n+++ b/F.cpp\n@@ -5,2 +5,2 @@\n X\n-Y\n+MUT\n").Single(), Twice)));

		// A zero-context insertion: git appends it at the end, wherever its header puts it.
		byte[] Four = Bytes("a\nb\nc\nd\n");
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(UnifiedDiff.Parse("--- a/A.cpp\n+++ b/A.cpp\n@@ -2,0 +3 @@\n+x\n").Single(), Four));
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(UnifiedDiff.Parse("--- a/A.cpp\n+++ b/A.cpp\n@@ -0,0 +1 @@\n+x\n").Single(), Four));
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(UnifiedDiff.Parse("--- a/A.cpp\n+++ b/A.cpp\n@@ -2 +2 @@\n-b\n+B\n").Single(), Four));
		// With context after its changes, a hunk without context before them applies at its line.
		Assert.AreEqual("a\nB\nc\nd\n", Text(UnifiedDiff.Apply(UnifiedDiff.Parse("--- a/A.cpp\n+++ b/A.cpp\n@@ -2,2 +2,2 @@\n-b\n+B\n c\n").Single(), Four)));
	}

	[TestMethod]
	public void EveryLineThePatchDoesNotAddKeepsItsOwnBytes()
	{
		// V-2: the applier wrote every line with one kind of line end, and dropped a '\r' a line held before its CR LF.
		DiffFile Patch = UnifiedDiff.Parse("--- a/F.cpp\n+++ b/F.cpp\n@@ -1,3 +1,3 @@\n x\n-y\n+MUT\n z\n").Single();
		Assert.AreEqual("x\r\nMUT\r\nz\r\r\nw\r\n", Text(UnifiedDiff.Apply(Patch, Bytes("x\r\ny\r\nz\r\r\nw\r\n"))));
		// Mixed line ends: each line keeps its own (a context line too); the added line gets the kind most lines have.
		Assert.AreEqual("x\r\nMUT\nz\nw\r\nv\n", Text(UnifiedDiff.Apply(Patch, Bytes("x\r\ny\nz\nw\r\nv\n"))));
		Assert.AreEqual("x\nMUT\r\nz\r\nw\n", Text(UnifiedDiff.Apply(Patch, Bytes("x\ny\r\nz\r\nw\n"))));

		// An empty file has no line: lines added to it end as the patch says.
		DiffFile Two = UnifiedDiff.Parse("--- a/E.cpp\n+++ b/E.cpp\n@@ -0,0 +1,2 @@\n+a\n+b\n\\ No newline at end of file\n").Single();
		Assert.AreEqual("a\nb", Text(UnifiedDiff.Apply(Two, [])));
		Assert.AreEqual("a\nb\n", Text(UnifiedDiff.Apply(UnifiedDiff.Parse("--- a/E.cpp\n+++ b/E.cpp\n@@ -0,0 +1,2 @@\n+a\n+b\n").Single(), [])));
	}

	[TestMethod]
	public void APatchCanAddAndDeleteFilesAndSaysWhereANewlineIsMissing()
	{
		List<DiffFile> Files = UnifiedDiff.Parse(
			"""
			--- /dev/null
			+++ b/New.h
			@@ -0,0 +1,2 @@
			+#pragma once
			+int X;
			\ No newline at end of file
			--- a/Old.h	2026-10-29 12:00:00.000000000 +0000
			+++ /dev/null
			@@ -1 +0,0 @@
			-int Y;

			""");

		Assert.IsTrue(Files[0].IsNew);
		Assert.AreEqual("#pragma once\nint X;", Text(UnifiedDiff.Apply(Files[0], null)));
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(Files[0], Bytes("exists")));
		Assert.IsTrue(Files[1].IsDeleted);
		Assert.AreEqual("Old.h", Files[1].Path);
		Assert.IsNull(UnifiedDiff.Apply(Files[1], Bytes("int Y;\n")));
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Apply(Files[1], Bytes("int Y;\nint Z;\n")));
	}

	[TestMethod]
	[DataRow("diff --git a/A b/B\nsimilarity index 90%\nrename from A\nrename to B\n")]
	[DataRow("diff --git a/A b/A\nold mode 100644\nnew mode 100755\n")]
	[DataRow("diff --git a/A.png b/A.png\nGIT binary patch\nliteral 1\n")]
	[DataRow("Binary files a/A.png and b/A.png differ\n")]
	[DataRow("--- a/../Outside.cpp\n+++ b/../Outside.cpp\n@@ -1 +1 @@\n-a\n+b\n")]
	[DataRow("--- C:/Abs.cpp\n+++ C:/Abs.cpp\n@@ -1 +1 @@\n-a\n+b\n")]
	[DataRow("--- a/A.cpp\n+++ b/B.cpp\n@@ -1 +1 @@\n-a\n+b\n")]
	[DataRow("--- a/A.cpp\n+++ b/A.cpp\n@@ -1,2 +1,2 @@\n-a\n+b\n")]
	[DataRow("no diff here\n")]
	// The header counts fewer lines than the hunk has: the rest would be dropped (git apply calls it a corrupt patch).
	[DataRow("--- a/A.cpp\n+++ b/A.cpp\n@@ -1,2 +1,2 @@\n a\n b\n-c\n+C\n d\n")]
	// An empty line parts a hunk from the next: the second would be dropped.
	[DataRow("--- a/A.cpp\n+++ b/A.cpp\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n\n@@ -10,3 +10,3 @@\n x\n-y\n+Y\n z\n")]
	// An empty line parts the file's header from its hunk.
	[DataRow("--- a/A.cpp\n+++ b/A.cpp\n\n@@ -1 +1 @@\n-a\n+b\n")]
	[DataRow("--- a/A.cpp\n+++ b/A.cpp\n@@ -1 +1 @@\n-a\n+b\nstray text\n")]
	public void APatchUakDoesNotApplyIsRefused(string patch)
	{
		Assert.ThrowsExactly<PatchException>(() => UnifiedDiff.Parse(patch));
	}

	[TestMethod]
	public void TrailingEmptyLinesAndAFormatPatchMailAreRead()
	{
		Assert.HasCount(1, UnifiedDiff.Parse("--- a/A.cpp\n+++ b/A.cpp\n@@ -1 +1 @@\n-a\n+b\n\n\n"));
		List<DiffFile> Mail = UnifiedDiff.Parse(
			"From 0123456789abcdef Mon Sep 17 00:00:00 2001\nSubject: [PATCH] Break Add\n\n---\n A.cpp | 2 +-\n 1 file changed, 1 insertion(+), 1 deletion(-)\n\n"
			+ "diff --git a/A.cpp b/A.cpp\nindex 1111111..2222222 100644\n--- a/A.cpp\n+++ b/A.cpp\n@@ -1 +1 @@\n-a\n+b\n"
			+ "diff --git a/B.cpp b/B.cpp\nindex 1111111..2222222 100644\n--- a/B.cpp\n+++ b/B.cpp\n@@ -1 +1 @@\n-c\n+d\n"
			+ "-- \n2.40.0\n\n");
		CollectionAssert.AreEqual(new[] { "A.cpp", "B.cpp" }, Mail.Select(File => File.Path).ToArray());
	}

	[TestMethod]
	public void APathThatResolvesToAnotherNameIsRefused()
	{
		using Sandbox Box = new();
		Assert.HasCount(1, UnifiedDiff.Resolve(UnifiedDiff.Parse("--- a/Foo.cpp\n+++ b/Foo.cpp\n@@ -1 +1 @@\n-a\n+b\n"), Box.ProjectDirectory));
		if (OperatingSystem.IsWindows())
		{
			// Windows drops a trailing '.': "Foo.cpp." is Foo.cpp, which the patch would change twice.
			List<DiffFile> Twice = UnifiedDiff.Parse("--- a/Foo.cpp\n+++ b/Foo.cpp\n@@ -1 +1 @@\n-a\n+b\n--- a/Foo.cpp.\n+++ b/Foo.cpp.\n@@ -1 +1 @@\n-b\n+c\n");
			Assert.ThrowsExactly<UakUsageException>(() => UnifiedDiff.Resolve(Twice, Box.ProjectDirectory));
		}
	}

	// ---- the journal ----

	[TestMethod]
	public void TheJournalPutsBackReadOnlyAndCreatedFilesAndTheFoldersItMade()
	{
		using Sandbox Box = new();
		string ReadOnly = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Locked.cpp"), "locked\n");
		File.SetAttributes(ReadOnly, FileAttributes.ReadOnly);
		string NewFolder = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "New");
		string Created = Path.Combine(NewFolder, "Deeper", "New.cpp");

		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "J", "p.diff", [ReadOnly, Created]);
		Assert.ThrowsExactly<UakUsageException>(() => MutationJournal.Begin(Box.Context(), "J", "p.diff", [ReadOnly]));
		MutationJournal.MakeWritable(ReadOnly);
		File.WriteAllText(ReadOnly, "mutated\n");
		Directory.CreateDirectory(Path.GetDirectoryName(Created)!);
		File.WriteAllText(Created, "new\n");
		Assert.IsEmpty(Journal.MarkApplied());

		Assert.IsEmpty(Journal.Restore().Conflicts);
		Assert.AreEqual("locked\n", File.ReadAllText(ReadOnly));
		Assert.IsTrue(File.GetAttributes(ReadOnly).HasFlag(FileAttributes.ReadOnly));
		Assert.IsFalse(File.Exists(Created));
		Assert.IsFalse(Directory.Exists(NewFolder), "the folders the patch made go with it");
		Assert.AreEqual(MutationPhase.Restored, Journal.Record.Phase);
		File.SetAttributes(ReadOnly, FileAttributes.Normal);
	}

	[TestMethod]
	public void ACopyThatNoLongerMatchesItsHashIsNeverWrittenBack()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "J", "p.diff", [Box.SourceFile]);
		File.WriteAllText(Box.SourceFile, "mutated\n");
		Journal.MarkApplied();
		File.WriteAllText(Path.Combine(Journal.Directory, Journal.Record.Files[0].Backup!), "damaged\n");

		Assert.Contains("doesn't match its recorded hash", Journal.Restore().Conflicts.Single());
		Assert.Contains("doesn't match its recorded hash", Journal.Restore(MutationRestoreMode.Force).Conflicts.Single());
		Assert.AreEqual("mutated\n", File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public void AFileChangedAfterItWasCopiedStopsThePatch()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "J", "p.diff", [Box.SourceFile]);
		Journal.ThrowIfChangedSinceBegin();

		File.WriteAllText(Box.SourceFile, Original + "// another agent's edit\n");

		Assert.ThrowsExactly<MutationFileChangedException>(Journal.ThrowIfChangedSinceBegin);
	}

	[TestMethod]
	public void ARestoredJournalLeavesALaterEditAlone()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "J", "p.diff", [Box.SourceFile]);
		File.WriteAllText(Box.SourceFile, "mutated\n");
		Journal.MarkApplied();
		Assert.IsEmpty(Journal.Restore().Conflicts);
		File.WriteAllText(Box.SourceFile, "a later edit\n");

		Assert.IsEmpty(Journal.Restore().Conflicts);
		Assert.AreEqual("a later edit\n", File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public void AJournalIsTheRunOfTheJoinedHolderOnlyWhenBothStartTimesAreKnownAndMatch()
	{
		ProcessIdentity Self = ProcessIdentity.Current;
		MutationRecord Record = new() { Pid = Self.Pid, ProcessStart = Self.StartTimeUtc };
		LockHolderInfo Holder = new() { Pid = Self.Pid, ProcessStart = Self.StartTimeUtc };

		Assert.IsTrue(MutationJournal.IsRunOf(Record, Holder));
		Assert.IsFalse(MutationJournal.IsRunOf(Record, null), "no hold was joined: every journal is a stopped run's");
		Assert.IsFalse(MutationJournal.IsRunOf(new MutationRecord { Pid = Self.Pid, ProcessStart = null }, Holder));
		Assert.IsFalse(MutationJournal.IsRunOf(Record, new LockHolderInfo { Pid = Self.Pid, ProcessStart = null }));
		Assert.IsFalse(MutationJournal.IsRunOf(Record, new LockHolderInfo { Pid = Self.Pid + 1, ProcessStart = Self.StartTimeUtc }));
		Assert.IsFalse(MutationJournal.IsRunOf(new MutationRecord { Pid = Self.Pid, ProcessStart = Self.StartTimeUtc!.Value.AddMinutes(-5) },
			new LockHolderInfo { Pid = Self.Pid, ProcessStart = Self.StartTimeUtc.Value.AddMinutes(-5) }), "the process with that ID started later: reused");
	}

	// ---- uak mutate ----

	static string Original => "int Add(int A, int B)\r\n{\r\n\treturn A + B;\r\n}\r\n";

	/// <summary>A sandbox whose Foo.cpp the patch mutates, and the patch file.</summary>
	static (Sandbox Box, string Patch) WithPatch(string? patch = null)
	{
		Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		return (Box, Sandbox.Write(Path.Combine(Box.Root, "mutation.diff"), patch ?? GitPatch));
	}

	/// <summary>
	/// The services a check runs with. The restore's wait before it stamps the files for a recompile is skipped (the stamp is
	/// then up to <paramref name="recompileMargin"/> ahead of the clock) unless <paramref name="realDelay"/>.
	/// </summary>
	static UnrealServices Services(FakeProcessRunner runner, IEditorLockProvider? lockProvider = null, bool realDelay = false, TimeSpan? recompileMargin = null,
		Func<string, DateTime?>? probe = null, List<TimeSpan>? delays = null, Func<UakContext, Task<bool>>? waitForCompiles = null) => new()
	{
		Runner = runner,
		Lock = lockProvider ?? new FakeLock(),
		HostPlatform = UnrealPlatform.Win64,
		GetEnvironmentVariable = _ => null,
		CaptureTree = TestSupportTrees.None,
		Delay = realDelay ? Wait => Task.Delay(Wait) : Wait =>
		{
			delays?.Add(Wait);
			return Task.CompletedTask;
		},
		RecompileMargin = recompileMargin ?? TimeSpan.FromSeconds(2),
		ProbeFileTime = probe ?? MutationJournal.ProbeFileTime,
		WaitForCompiles = waitForCompiles ?? (_ => Task.FromResult(true)),
	};

	/// <summary>What UBT prints for a build that compiles: a mutant build that compiles nothing can't hold the patch.</summary>
	static readonly string[] CompileLines = ["[1/2] Compile [x64] Foo.cpp", "[2/2] Link [x64] UnrealEditor-Game.dll"];

	/// <summary>
	/// UBT prints <paramref name="buildOutput"/> (by build, from 0; default <see cref="CompileLines"/>) and exits with the next
	/// of <paramref name="builds"/>; the editor writes the fixture (through <paramref name="editLog"/>), after <paramref name="duringTest"/>.
	/// </summary>
	static Func<ProcessInvocation, Action<string>, int> Plays(string testFixture, int testExit, int[] builds, Action? duringTest = null,
		Func<int, string[]>? buildOutput = null, Func<string[], IEnumerable<string>>? editLog = null)
	{
		int Build = 0;
		return (Invocation, Output) =>
		{
			string? AbsLog = Invocation.Arguments.FirstOrDefault(Argument => Argument.StartsWith("-abslog=", StringComparison.Ordinal));
			if (AbsLog is null)
			{
				foreach (string Line in buildOutput?.Invoke(Build) ?? CompileLines)
				{
					Output(Line);
				}
				return builds[Math.Min(Build++, builds.Length - 1)];
			}
			duringTest?.Invoke();
			string[] Log = Fixtures.Lines(testFixture);
			File.WriteAllLines(AbsLog["-abslog=".Length..], editLog is null ? Log : editLog(Log));
			return testExit;
		};
	}

	static bool IsBuild(ProcessInvocation invocation) => !invocation.Arguments.Any(Argument => Argument.StartsWith("-abslog=", StringComparison.Ordinal));

	static string JournalFile(Sandbox box, string name) => Path.Combine(MutationJournal.GetRoot(box.Context()), name, "journal.json");

	[TestMethod]
	public async Task AMutationTheTestsCatchIsCaughtAndTheTreeIsPutBack()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string? DuringTest = null;
		FakeLock Lock = new();
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0], () => DuringTest = File.ReadAllText(Box.SourceFile)) };

		int Exit = await new MutateCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-name=M1", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains("return A - B;", DuringTest!);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		CollectionAssert.AreEqual(new[] { true, false, true }, Runner.Invocations.Select(IsBuild).ToArray());
		Assert.AreEqual(1, Lock.Released);
		Assert.HasCount(1, Lock.Names);
		Assert.IsFalse(Directory.Exists(Path.Combine(MutationJournal.GetRoot(Box.Context()), "M1")));
		Assert.Contains("Source/Game/Private/Foo.cpp (+1 -1)", Box.Logger.Text);
		Assert.Contains("mutate: CAUGHT", Box.Logger.Text);
		Assert.StartsWith("mutate: CAUGHT", RunSummaries.Read(new DirectoryInfo(Box.StateDirectory), "M1")!.Entries.Single().Lines[^1]);
	}

	[TestMethod]
	public async Task AMutationTheTestsMissSurvivesAndTheResultFileIsMutatesOwn()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		string ResultFile = Path.Combine(Box.Root, "result.json");

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M2", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(MutateCommand.SurvivedExitCode, Exit, Box.Logger.Text);
		Assert.Contains("mutate: SURVIVED", Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual("survived", Result.RootElement.GetProperty("Verdict").GetString());
		Assert.AreEqual(MutateCommand.SurvivedExitCode, Result.RootElement.GetProperty("ExitCode").GetInt32());
		Assert.AreEqual("passed", Result.RootElement.GetProperty("Tests").GetProperty("Outcome").GetString());
		Assert.AreEqual(1, Result.RootElement.GetProperty("Files")[0].GetProperty("Added").GetInt32());
	}

	[TestMethod]
	public async Task ARefusedRunReplacesAnOldResultFileWithItsOwn()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string ResultFile = Sandbox.Write(Path.Combine(Box.Root, "result.json"), "{ \"Outcome\": \"passed\" }");

		int Exit = await new MutateCommand(Services(new FakeProcessRunner())).RunAsync(Box.Context(), ["-name=M2", "-patch=" + Patch, "-filter=Project.Math", "-cooked", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(2, Exit);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual("refused", Result.RootElement.GetProperty("Verdict").GetString());
		Assert.AreEqual(2, Result.RootElement.GetProperty("ExitCode").GetInt32());
		Assert.IsFalse(Result.RootElement.TryGetProperty("Outcome", out JsonElement _));
	}

	[TestMethod]
	public async Task AMutantThatDoesNotBuildRunsNoTestAndIsStillPutBackAndRebuilt()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Error = Box.SourceFile + "(3,9): error C2065: 'C': undeclared identifier";
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [1, 0], buildOutput: Build => Build == 0 ? [CompileLines[0], Error] : CompileLines) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M3", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.NotBuiltExitCode, Exit, Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { true, true }, Runner.Invocations.Select(IsBuild).ToArray());
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public async Task ABuildThatFailsElsewhereIsInconclusive()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		// Another agent's half-edited file, or a locked DLL: not the mutation's doing.
		string Other = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Other.cpp") + "(1): error C1083: Cannot open include file: 'Missing.h'";
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [1, 0], buildOutput: Build => Build == 0 ? [CompileLines[0], Other] : CompileLines) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M3b", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Exit, Box.Logger.Text);
		Assert.Contains("no error is in a patched file", Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public void AnErrorBlamesThePatchInAPatchedFileOrAFileThatIncludesAPatchedHeader()
	{
		using Sandbox Box = new();
		string Includer = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "User.cpp"), "#include \"Foo.h\"\nint Use();\n");
		CompileDiagnostic InIncluder = new(Includer, 2, null, DiagnosticSeverity.Error, "C2065", "undeclared", null);
		CompileDiagnostic InPatched = new(Box.SourceFile, 3, null, DiagnosticSeverity.Error, "C2065", "undeclared", null);

		Assert.IsTrue(MutateCommand.BlamesPatch(InPatched, [Box.SourceFile]));
		Assert.IsTrue(MutateCommand.BlamesPatch(InIncluder, [Box.HeaderFile]));
		Assert.IsFalse(MutateCommand.BlamesPatch(InIncluder, [Box.SourceFile]));
		Assert.IsFalse(MutateCommand.BlamesPatch(new CompileDiagnostic("LINK", null, null, DiagnosticSeverity.Error, "LNK1104", "cannot open file", null), [Box.SourceFile]));
	}

	[TestMethod]
	public async Task AMutantBuildThatCompilesNothingIsInconclusive()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		// The patched file isn't part of the editor target: UBT finds it up to date, and the tests would run the old binaries.
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => ["Target is up to date", "Result: Succeeded"]) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M9", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Exit, Box.Logger.Text);
		Assert.Contains("shows no compile of", Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { true, true }, Runner.Invocations.Select(IsBuild).ToArray());
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public async Task TestsThatPassButEndBadlyAreInconclusiveNotCaught()
	{
		// The editor's exit code after every test passed (a shutdown crash), and a crash before TEST COMPLETE without a failed test.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner ShutdownCrash = new() { Behaviour = Plays("test-passed.log", 255, [0]) };
		Assert.AreEqual(MutateCommand.InconclusiveExitCode, await new MutateCommand(Services(ShutdownCrash)).RunAsync(Box.Context(),
			["-name=M10", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		FakeProcessRunner MidRunCrash = new()
		{
			Behaviour = Plays("test-passed.log", 3, [0], editLog: Log => Log.Where(Line => !Line.Contains("TEST COMPLETE", StringComparison.Ordinal))),
		};
		Assert.AreEqual(MutateCommand.InconclusiveExitCode, await new MutateCommand(Services(MidRunCrash)).RunAsync(Box.Context(),
			["-name=M10", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("mutate: INCONCLUSIVE: no test failed", Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public async Task APatchThatChangesNothingIsRefusedWithoutABuild()
	{
		(Sandbox Box, string Patch) = WithPatch(GitPatch.Replace("+\treturn A - B;", "+\treturn A + B;", StringComparison.Ordinal));
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new();
		string ResultFile = Path.Combine(Box.Root, "result.json");

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M11", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(2, Exit, Box.Logger.Text);
		Assert.Contains("changes nothing", Box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations);
		Assert.IsFalse(Directory.Exists(Path.Combine(MutationJournal.GetRoot(Box.Context()), "M11")));
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual("refused", Result.RootElement.GetProperty("Verdict").GetString());
	}

	[TestMethod]
	public async Task ACancelledRunIsPutBackAndRebuiltWithNoVerdict()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		using CancellationTokenSource Cancel = new();
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], () =>
			{
				Cancel.Cancel();
				throw new OperationCanceledException(Cancel.Token);
			}),
		};

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M4", "-patch=" + Patch, "-filter=Project.Math"], Cancel.Token);

		Assert.AreEqual(UakExitCodes.Failure, Exit, Box.Logger.Text);
		Assert.Contains("mutate: STOPPED (cancelled)", Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		CollectionAssert.AreEqual(new[] { true, false, true }, Runner.Invocations.Select(IsBuild).ToArray());
	}

	[TestMethod]
	public async Task ARebuildThatFailsAfterTheFilesAreBackKeepsTheVerdictAndOnlyTestsWaitForABuildOfTheTarget()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0, 1]) };
		string ResultFile = Path.Combine(Box.Root, "result.json");

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M12", "-patch=" + Patch, "-filter=Project.Gameplay", "-resultfile=" + ResultFile],
			CancellationToken.None);

		// The files are back byte for byte and stamped, so the verdict stands; with no receipt, which binaries the mutant build
		// wrote isn't known, so any may still hold it.
		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains("mutate: BINARIES PENDING: the files are back", Box.Logger.Text);
		Assert.Contains("uak doesn't know which", Box.Logger.Text);
		Assert.StartsWith("mutate: CAUGHT", RunSummaries.Read(new DirectoryInfo(Box.StateDirectory), "M12")!.Entries.Single().Lines[^1]);
		using (JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile)))
		{
			Assert.AreEqual("caught", Result.RootElement.GetProperty("Verdict").GetString());
			Assert.Contains("BINARIES PENDING", Result.RootElement.GetProperty("Problems")[0].GetString()!);
		}
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		MutationRecord Record = StateFiles.ReadJson<MutationRecord>(JournalFile(Box, "M12"))!;
		Assert.AreEqual(MutationPhase.Restored, Record.Phase);
		Assert.IsNotNull(Record.RecompileStamp);
		Assert.AreEqual("GameEditor", Record.BuildTarget);

		// The tree holds no mutation: a commit, a shelf or a preflight may go ahead; tests may not.
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-status"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("'M12' (PID", Box.Logger.Text);
		Assert.IsNull(MutationFolders.DescribeLeft(new DirectoryInfo(Box.StateDirectory), "shelve"));
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), "the binaries may hold the mutation");
		Assert.Contains($"a `uak build` of GameEditor (Win64 Development) that shows a compile of {Box.SourceFile} and succeeds clears this", Box.Logger.Text);

		// Only a whole build of the mutant's own target relinks what it linked.
		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), ["-target=Game"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), ["--", "-SingleFile=" + Box.SourceFile], CancellationToken.None), Box.Logger.Text);
		Assert.IsTrue(File.Exists(JournalFile(Box, "M12")), "another target, or a build given UBT arguments, may not relink the mutant's binaries");
		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "M12")), "a build of the target that succeeds clears it: the stamp made it recompile the files");
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task APatchThatDoesNotApplyIsRefusedBeforeAnythingChanges()
	{
		(Sandbox Box, string Patch) = WithPatch(GitPatch.Replace("return A + B;", "return A * B;", StringComparison.Ordinal));
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new();

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M5", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(2, Exit);
		Assert.Contains("doesn't apply cleanly", Box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations);
		Assert.IsFalse(Directory.Exists(Path.Combine(MutationJournal.GetRoot(Box.Context()), "M5")));
		Assert.AreEqual(2, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M5", "-patch=" + Patch, "-filter=Project.Math", "-cooked"], CancellationToken.None));
	}

	[TestMethod]
	public async Task AFileEditedDuringTheRunIsNeverOverwrittenAndForceSavesItBeforePuttingTheOriginalBack()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0], () => File.WriteAllText(Box.SourceFile, "another agent's edit\n")) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M6", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, Exit, Box.Logger.Text);
		Assert.AreEqual("another agent's edit\n", File.ReadAllText(Box.SourceFile));
		Assert.Contains("mutate: RESTORE FAILED", Box.Logger.Text);
		// No rebuild of files that still differ: build, test, and nothing after.
		Assert.HasCount(2, Runner.Invocations);

		// Its journal stays, and blocks tests, builds and other mutations until -restore.
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		Assert.Contains("uak mutate -restore -name=M6", Box.Logger.Text);
		Assert.AreEqual(2, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), [], CancellationToken.None));
		Assert.AreEqual(2, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-name=M6b", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None));
		Assert.IsEmpty(Later.Invocations);

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None));
		Assert.AreEqual(2, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-force"], CancellationToken.None), "-force needs -name=");
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=M6", "-force"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		Assert.IsFalse(File.Exists(JournalFile(Box, "M6")));
		string SetAside = Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "M6-*").Single();
		Assert.AreEqual("another agent's edit\n", File.ReadAllText(Directory.GetFiles(SetAside, "0.*.current").Single()), "-force saves what it overwrites");
		Assert.Contains(SetAside, Box.Logger.Text);
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None));
		Assert.Contains("nothing to restore", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AcceptKeepsALaterEditRebuildsAndClearsTheJournal()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		// The agent's edit, made over the mutant: two lines above (a function after would touch the file's end, which pins the
		// mutated block, and copy its braces: refused, AcceptRefusesAnEditNextToTheMutation...).
		const string Edited = "// a line the agent wants\r\n// and another\r\nint Add(int A, int B)\r\n{\r\n\treturn A - B;\r\n}\r\n";
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0], () => File.WriteAllText(Box.SourceFile, Edited)) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(),
			["-name=M7", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None));
		Assert.Contains("With -accept it merges such an edit with the original", Box.Logger.Text);

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=M7", "-accept"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(Edited.Replace("A - B", "A + B", StringComparison.Ordinal), File.ReadAllText(Box.SourceFile));
		Assert.Contains("kept the later edit in " + Box.SourceFile + " and took the mutation out of it", Box.Logger.Text);
		Assert.IsTrue(IsBuild(Later.Invocations.Single()), "accepting rebuilds: the binaries hold the mutant");
		Assert.IsFalse(File.Exists(JournalFile(Box, "M7")));
		string SetAside = Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "M7-*").Single();
		Assert.AreEqual(Edited, File.ReadAllText(Directory.GetFiles(SetAside, "0.*.current").Single()), "what it held is saved");
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task AcceptRefusesAnEditNextToTheMutationAndForcePutsTheOriginalBack()
	{
		// Review 5's acceptkept (R-1): a comment on the signature, one line from the mutated line, and a function whose body is the
		// original's line added after. The old fallback merged again from the original, took IsReady's line for the original's,
		// and kept the file with 'return false;' in IsValid: -accept, uak test and -status all exited 0. The merge took the
		// mutation out since; the strict near-edit rule refuses it: the signature is the line before the mutated block, and the
		// function added after ends the file and copies its braces. -force puts the original back and saves the edit.
		const string Orig = "bool IsValid() const\r\n{\r\n\treturn true;\r\n}\r\n";
		(Sandbox Box, string Patch) = WithPatch("diff --git a/Source/Game/Private/Foo.cpp b/Source/Game/Private/Foo.cpp\n--- a/Source/Game/Private/Foo.cpp\n+++ b/Source/Game/Private/Foo.cpp\n"
			+ "@@ -1,4 +1,4 @@\n bool IsValid() const\n {\n-\treturn true;\n+\treturn false;\n }\n");
		using Sandbox _ = Box;
		File.WriteAllText(Box.SourceFile, Orig);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0], () => File.WriteAllText(Box.SourceFile, File.ReadAllText(Box.SourceFile)
				.Replace("bool IsValid() const\r\n", "bool IsValid() const // used by the HUD\r\n", StringComparison.Ordinal) + "\r\nbool IsReady() const\r\n{\r\n\treturn true;\r\n}\r\n")),
		};
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(),
			["-name=KP", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		const string Edited = "bool IsValid() const // used by the HUD\r\n{\r\n\treturn false;\r\n}\r\n\r\nbool IsReady() const\r\n{\r\n\treturn true;\r\n}\r\n";
		Assert.AreEqual(Edited, File.ReadAllText(Box.SourceFile));
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		int Before = Box.Logger.Entries.Count;

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=KP", "-accept"], CancellationToken.None));

		string Said = string.Join("\n", Box.Logger.Entries.Skip(Before).Select(Entry => Entry.Message));
		Assert.Contains("-accept can't keep it: the later edit touched the lines around the patch's change 1 ('return false;')", Said);
		Assert.Contains("changed, moved or deleted line 1 ('bool IsValid() const')", Said, "it names the line the edit touched");
		Assert.Contains("-force puts the original back and saves the edit beside the journal", Said);
		Assert.AreEqual(Edited, File.ReadAllText(Box.SourceFile), "refused: left as it is");
		Assert.IsEmpty(Later.Invocations, "no rebuild of a tree that may hold the mutation");

		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=KP", "-force"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Orig, File.ReadAllText(Box.SourceFile));
		string SetAside = Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "KP-*").Single();
		Assert.AreEqual(Edited, File.ReadAllText(Directory.GetFiles(SetAside, "0.*.current").Single()), "the edit is saved, to make again on the original");
	}

	[TestMethod]
	public async Task AcceptRefusesAnEditThatTookTheMutationOutByHandAndForcePutsTheOriginalBack()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		// The agent's own fix: the original function, and a line it wants. Its edit and the undo change the mutated line alike, so
		// uak can't tell its edit from taking the mutation out: refused (R-1, R-2), never kept as it is.
		const string Fix = "// a fix the agent wants\r\nint Add(int A, int B)\r\n{\r\n\treturn A + B;\r\n}\r\n";
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0], () => File.WriteAllText(Box.SourceFile, Fix)) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(),
			["-name=M7", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None));

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=M7", "-accept"], CancellationToken.None));
		Assert.Contains("-accept can't keep it", Box.Logger.Text);
		Assert.AreEqual(Fix, File.ReadAllText(Box.SourceFile), "refused: left as it is");
		Assert.IsEmpty(Later.Invocations);

		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=M7", "-force"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		string SetAside = Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "M7-*").Single();
		Assert.AreEqual(Fix, File.ReadAllText(Directory.GetFiles(SetAside, "0.*.current").Single()), "the edit is saved, to make again on the original");
	}

	/// <summary>Leaves a journal as a mutation run killed after it applied its patch would, with the run's identity given.</summary>
	static void LeaveJournal(Sandbox box, string name, int pid, DateTime? processStart)
	{
		MutationJournal Journal = MutationJournal.Begin(box.Context(), name, "p.diff", [box.SourceFile]);
		File.WriteAllText(box.SourceFile, "int Add(int A, int B) { return A - B; }\n");
		Journal.MarkApplied();
		Journal.Record.Pid = pid;
		Journal.Record.ProcessStart = processStart;
		StateFiles.WriteJson(Path.Combine(Journal.Directory, "journal.json"), Journal.Record);
	}

	[TestMethod]
	public async Task AJournalWhosePidRunsWithAnUnreadableStartTimeIsAStoppedRunAndIsRestored()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		// The killed run's ID now belongs to a running process whose start time can't be read: on Windows the System process
		// (a known start time recorded, none readable), else this process with none recorded. Either way "alive" would match.
		ProcessIdentity Self = ProcessIdentity.Current;
		(int Pid, DateTime? Start) = OperatingSystem.IsWindows() && ProcessIdentity.TryGet(4) is { StartTimeUtc: null } ? (4, Self.StartTimeUtc) : (Self.Pid, (DateTime?)null);
		Assert.IsTrue(new ProcessIdentity(Pid, Start).IsAlive(), "the lenient check would take the run for alive");
		LeaveJournal(Box, "R1", Pid, Start);

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		Assert.AreEqual(2, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), [], CancellationToken.None));
		Assert.IsEmpty(Later.Invocations);

		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		Assert.IsFalse(File.Exists(JournalFile(Box, "R1")));
	}

	[TestMethod]
	public async Task OnlyTheJoinedHoldersOwnRunIsLeftToIt()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		ProcessIdentity Self = ProcessIdentity.Current;
		// A live run that holds the lock is seen only from inside its hold: as the holder this process joined, it is its own
		// business; as anyone else (a sibling under `uak lock run`), it is refused like a stopped one.
		LeaveJournal(Box, "Live", Self.Pid, Self.StartTimeUtc);
		LockHolderInfo Joined = new() { Pid = Self.Pid, ProcessStart = Self.StartTimeUtc, HoldId = "h" };

		MutationJournal.ThrowIfUnfinished(Box.Context(), Joined, building: false);
		Assert.ThrowsExactly<UakUsageException>(() => MutationJournal.ThrowIfUnfinished(Box.Context(), null, building: false));
		Assert.ThrowsExactly<UakUsageException>(() => MutationJournal.ThrowIfUnfinished(Box.Context(), new LockHolderInfo { Pid = 1, ProcessStart = Self.StartTimeUtc }, building: false));
		Assert.AreEqual(0, await new MutateCommand(Services(new FakeProcessRunner { Behaviour = Plays("test-passed.log", 0, [0]) })).RunAsync(Box.Context(), ["-restore"], CancellationToken.None));
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	[DataRow("truncated", "{ \"Name\": \"Bad\", \"Phase\": \"Appl")]
	[DataRow("empty", "")]
	[DataRow("unknown phase", "{ \"Name\": \"Bad\", \"Phase\": \"Exploded\", \"Files\": [] }")]
	[DataRow("undefined phase", "{ \"Name\": \"Bad\", \"Phase\": 7, \"Files\": [ { \"Path\": \"C:\\\\X.cpp\", \"Existed\": false } ] }")]
	[DataRow("no files", "{ \"Name\": \"Bad\", \"Phase\": \"Applied\", \"Files\": [] }")]
	[DataRow("missing copy", "{ \"Name\": \"Bad\", \"Phase\": \"Applied\", \"Files\": [ { \"Path\": \"C:\\\\X.cpp\", \"Existed\": true, \"OriginalHash\": \"00\", \"Backup\": \"0.orig\" } ] }")]
	[DataRow("copies only", null)]
	public async Task AJournalUakCannotReadBlocksEveryRunUntilAccepted(string why, string? journal)
	{
		using Sandbox Box = new();
		string Folder = Path.Combine(MutationJournal.GetRoot(Box.Context()), "Bad");
		Directory.CreateDirectory(Folder);
		if (journal is null)
		{
			File.WriteAllText(Path.Combine(Folder, "0.orig"), Original);
		}
		else
		{
			File.WriteAllText(Path.Combine(Folder, "journal.json"), journal);
		}
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };

		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), why);
		Assert.Contains("can't be trusted", Box.Logger.Text, why);
		Assert.AreEqual(2, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), [], CancellationToken.None), why);
		Assert.ThrowsExactly<UakUsageException>(() => MutationJournal.CheckForCompile(Box.Context()), why);
		Assert.IsEmpty(Later.Invocations, why);
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), why);

		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=Bad", "-accept"], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(Directory.Exists(Folder), why);
		Assert.HasCount(1, Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "Bad-*"), why);
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task CompileWarnsWhileARunHasItsPatchInAndRefusesAfterOneStopped()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		ProcessIdentity Self = ProcessIdentity.Current;
		LeaveJournal(Box, "Running", Self.Pid, Self.StartTimeUtc);
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, "[1/1] Compile [x64] Foo.cpp", "Result: Succeeded") };

		Assert.AreEqual(0, await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.SourceFile], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("this compile sees the mutant", Box.Logger.Text);

		string Record = JournalFile(Box, "Running");
		MutationRecord Stopped = StateFiles.ReadJson<MutationRecord>(Record)!;
		Stopped.Pid = int.MaxValue;
		StateFiles.WriteJson(Record, Stopped);
		Assert.AreEqual(2, await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.SourceFile], CancellationToken.None));
		Assert.Contains("uak mutate -restore -name=Running", Box.Logger.Text);
		Assert.HasCount(1, Runner.Invocations);
	}

	[TestMethod]
	public async Task ANameThatCannotNameASummaryIsRefused()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new();

		Assert.AreEqual(2, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=a b"], CancellationToken.None));
		Assert.AreEqual(2, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math", "-name=a b"], CancellationToken.None));
		Assert.AreEqual(2, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=../M", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None));
		Assert.IsEmpty(Runner.Invocations);
	}

	[TestMethod]
	public async Task ReadingTheTreeNeverHoldsTheRunUp()
	{
		using Sandbox Box = new();
		UnrealServices Slow = new()
		{
			// A server that never answers, nor heeds the cancellation.
			CaptureTree = (_, _) => new TaskCompletionSource<VcsTreeState>().Task,
			CaptureTreeTimeout = TimeSpan.FromMilliseconds(100),
		};

		VcsTreeState Tree = await Slow.StartCaptureTree(Box.Context(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Contains("not read within", Tree.ToString());
	}

	// ---- a Git workspace: git apply, and uncommitted changes kept ----

	static async Task GitAsync(string directory, params string[] arguments)
	{
		string Git = ExecutableLocator.Resolve("git") ?? throw new AssertInconclusiveException("git is not on PATH");
		ProcessCapture Result = await ProcessRunner.Default.CaptureAsync(new ProcessInvocation(Git, ["-C", directory, "-c", "core.autocrlf=false", .. arguments]), CancellationToken.None);
		Assert.AreEqual(0, Result.ExitCode, Result.StandardError);
	}

	/// <summary>Git writes its objects read-only: makes them writable on dispose (before the sandbox's), so the sandbox can delete them.</summary>
	sealed class GitObjectsWritable(string directory) : IDisposable
	{
		public void Dispose()
		{
			foreach (string File in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
			{
				System.IO.File.SetAttributes(File, FileAttributes.Normal);
			}
		}
	}

	[TestMethod]
	public async Task InAGitWorkspaceGitAppliesThePatchAndUncommittedChangesAreKept()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		using GitObjectsWritable __ = new(Box.ProjectDirectory);
		// LF in the work tree: git apply then matches it whatever core.autocrlf this machine sets.
		string Lf = Original.Replace("\r\n", "\n", StringComparison.Ordinal);
		File.WriteAllText(Box.SourceFile, Lf);
		await GitAsync(Box.ProjectDirectory, "init", "-q");
		await GitAsync(Box.ProjectDirectory, "add", "-A");
		await GitAsync(Box.ProjectDirectory, "-c", "user.name=UAK Tests", "-c", "user.email=uak-tests@example.invalid", "commit", "-q", "-m", "base");
		string? DuringTest = null;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0], () => DuringTest = File.ReadAllText(Box.SourceFile)) };
		UnrealServices Real = Services(Runner);

		int Exit = await new MutateCommand(Real).RunAsync(Box.Context(), ["-name=G1", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains("return A - B;", DuringTest!);
		Assert.AreEqual(Lf, File.ReadAllText(Box.SourceFile));

		// The agent's own uncommitted work in the file: the patch applies on top, and the copy puts it back byte for byte.
		File.WriteAllText(Box.SourceFile, Lf + "// work in progress\n");
		Assert.AreEqual(MutateCommand.CaughtExitCode, await new MutateCommand(Real).RunAsync(Box.Context(), ["-name=G2", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("return A - B;", DuringTest!);
		Assert.Contains("// work in progress", DuringTest!);
		Assert.AreEqual(Lf + "// work in progress\n", File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public async Task APatchMadeInAGitWorkTreeBelowTheProjectIsAppliedThereByGit()
	{
		// The project's own folder has no version control, but a plugin's repository below it does: `git diff` there writes
		// paths relative to it.
		(Sandbox Box, string Patch) = WithPatch(GitPatch.Replace("Source/Game/Private/Foo.cpp", "Game/Private/Foo.cpp", StringComparison.Ordinal));
		using Sandbox _ = Box;
		string Nested = Path.Combine(Box.ProjectDirectory, "Source");
		using GitObjectsWritable __ = new(Nested);
		string Lf = Original.Replace("\r\n", "\n", StringComparison.Ordinal);
		File.WriteAllText(Box.SourceFile, Lf);
		await GitAsync(Nested, "init", "-q");
		await GitAsync(Nested, "add", "-A");
		await GitAsync(Nested, "-c", "user.name=UAK Tests", "-c", "user.email=uak-tests@example.invalid", "commit", "-q", "-m", "base");
		string? DuringTest = null;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0], () => DuringTest = File.ReadAllText(Box.SourceFile)) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=N1", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains($"are under the Git work tree {Nested}", Box.Logger.Text);
		Assert.Contains("return A - B;", DuringTest!);
		Assert.AreEqual(Lf, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public void ANestedWorkTreeIsTakenOnlyWhenTheRootDoesNotFitAndOnlyOneDoes()
	{
		using Sandbox Box = new();
		List<DiffFile> InSource = UnifiedDiff.Parse(GitPatch.Replace("Source/Game/Private/Foo.cpp", "Game/Private/Foo.cpp", StringComparison.Ordinal));
		Assert.IsNull(MutateCommand.FindNestedGitRoot(InSource, Box.ProjectDirectory, Box.ProjectDirectory), "no work tree below");
		string Nested = Path.Combine(Box.ProjectDirectory, "Source");
		Directory.CreateDirectory(Path.Combine(Nested, ".git"));
		Assert.AreEqual(Nested, MutateCommand.FindNestedGitRoot(InSource, Box.ProjectDirectory, Box.ProjectDirectory));
		Assert.IsNull(MutateCommand.FindNestedGitRoot(UnifiedDiff.Parse(GitPatch), Box.ProjectDirectory, Box.ProjectDirectory), "the paths fit the root");
		Assert.IsNull(MutateCommand.FindNestedGitRoot(InSource, Nested, Box.ProjectDirectory), "the root found is that work tree");

		// A second work tree the paths fit: which one was meant can't be told.
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Copy", "Game", "Private", "Foo.cpp"), Original);
		File.WriteAllText(Path.Combine(Box.ProjectDirectory, "Plugins", "Copy", ".git"), "gitdir: elsewhere");
		Assert.IsNull(MutateCommand.FindNestedGitRoot(InSource, Box.ProjectDirectory, Box.ProjectDirectory));
	}

	[TestMethod]
	public async Task InAGitWorkspaceAHunkThatMatchesOnlyAwayFromItsLineIsRefusedThoughGitWouldApplyIt()
	{
		// R-3 (review 5's gitorder): the block the hunk names is at line 4 and line 8, not at 6. git apply searches down first and
		// changes line 8; uak's plan searched up first (line 4), so it took git's write for an edit, and -accept merged from the
		// wrong mutant. uak now refuses a hunk away from its line in a Git workspace too, before anything is written: git apply
		// --check -v names the line it would apply it at.
		const string Twice = "l1\nl2\nl3\n{\n\treturn true;\n}\nl7\n{\n\treturn true;\n}\nl11\n";
		(Sandbox Box, string Patch) = WithPatch("--- a/Source/Game/Private/Foo.cpp\n+++ b/Source/Game/Private/Foo.cpp\n@@ -6,3 +6,3 @@\n {\n-\treturn true;\n+\treturn false;\n }\n");
		using Sandbox _ = Box;
		using GitObjectsWritable __ = new(Box.ProjectDirectory);
		File.WriteAllText(Box.SourceFile, Twice);
		await GitAsync(Box.ProjectDirectory, "init", "-q");
		await GitAsync(Box.ProjectDirectory, "add", "-A");
		await GitAsync(Box.ProjectDirectory, "-c", "user.name=UAK Tests", "-c", "user.email=uak-tests@example.invalid", "commit", "-q", "-m", "base");
		await GitAsync(Box.ProjectDirectory, "apply", "--check", Patch);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=GO", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(2, Exit, Box.Logger.Text);
		Assert.Contains("Hunk #1 succeeded at 8", Box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations);
		Assert.AreEqual(Twice, File.ReadAllText(Box.SourceFile));
		Assert.IsFalse(Directory.Exists(Path.Combine(MutationJournal.GetRoot(Box.Context()), "GO")));
	}

	[TestMethod]
	[DataRow("an LF patch, LF lines further down", "a\r\nX\r\nY\r\nZ\r\nb\nX\nY\nZ\nc\n", "--- a/Source/Game/Private/Foo.cpp\n+++ b/Source/Game/Private/Foo.cpp\n@@ -2,3 +2,3 @@\n X\n-Y\n+MUT\n Z\n")]
	[DataRow("a CRLF patch naming LF lines, CRLF lines further down", "a\nX\nY\nZ\nb\r\nX\r\nY\r\nZ\r\nc\r\n",
		"--- a/Source/Game/Private/Foo.cpp\r\n+++ b/Source/Game/Private/Foo.cpp\r\n@@ -2,3 +2,3 @@\r\n X\r\n-Y\r\n+MUT\r\n Z\r\n")]
	[DataRow("no context after the change, the same lines at the end", "a\nX\nY\nb\nX\nY\n", "--- a/Source/Game/Private/Foo.cpp\n+++ b/Source/Game/Private/Foo.cpp\n@@ -2,2 +2,2 @@\n X\n-Y\n+MUT\n")]
	public async Task InAGitWorkspaceAPatchGitWouldApplyElsewhereIsRefused(string label, string text, string patch)
	{
		// V-2: under core.autocrlf=false git compares a line's '\r' and uak's applier doesn't, and git reads a hunk with no context
		// after its changes as the file's end. Either way git wrote other lines than uak planned: a false "concurrent edit", the
		// mutant left in place. git apply --check -v says where it would apply each hunk: anywhere else is refused up front.
		(Sandbox Box, string Patch) = WithPatch(patch);
		using Sandbox _ = Box;
		using GitObjectsWritable __ = new(Box.ProjectDirectory);
		File.WriteAllText(Box.SourceFile, text);
		await GitAsync(Box.ProjectDirectory, "init", "-q");
		await GitAsync(Box.ProjectDirectory, "config", "core.autocrlf", "false");
		await GitAsync(Box.ProjectDirectory, "add", "-A");
		await GitAsync(Box.ProjectDirectory, "-c", "user.name=UAK Tests", "-c", "user.email=uak-tests@example.invalid", "commit", "-q", "-m", "base");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=GM", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(2, Exit, $"{label}: {Box.Logger.Text}");
		Assert.Contains("elsewhere than its hunk headers say", Box.Logger.Text, label);
		Assert.IsEmpty(Runner.Invocations, label);
		Assert.AreEqual(text, File.ReadAllText(Box.SourceFile), label);
		Assert.IsFalse(Directory.Exists(Path.Combine(MutationJournal.GetRoot(Box.Context()), "GM")), label);
	}

	[TestMethod]
	[DataRow("Checking patch F.cpp...\nHunk #1 succeeded at 6 (offset 4 lines).\n", 1)]
	[DataRow("Checking patch F.cpp...\r\nHunk #2 succeeded at 40 (offset -3 lines).\r\nHunk #3 succeeded at 50 (offset 1 line).\r\n", 2)]
	[DataRow("Checking patch F.cpp...\nContext reduced to (2/2) to apply fragment at 7\n", 1)]
	[DataRow("Checking patch F.cpp...\nChecking patch G.cpp...\n", 0)]
	[DataRow("", 0)]
	public void GitVerboseOutputNamesTheHunksItWouldMove(string output, int moved)
	{
		Assert.HasCount(moved, MutateCommand.GitMovedHunks(output));
	}

	[TestMethod]
	[DataRow("error: patch failed: Source/Foo.cpp:1\nerror: Source/Foo.cpp: patch does not apply\n", true)]
	[DataRow("error: Source/Foo.cpp: No such file or directory\n", true)]
	[DataRow("error: Source/New.cpp: already exists in working directory\n", true)]
	[DataRow("error: Source/Foo.cpp: No such file or directory\r\nerror: Source/New.cpp: already exists in working directory\r\n", true)]
	// A failed write names its file in quotes, and may come after git wrote others; any other error may too.
	[DataRow("error: unable to write file 'Source/Foo.cpp' mode 100644: No such file or directory\n", false)]
	[DataRow("error: Source/Foo.cpp: patch does not apply\nfatal: unable to write new index file\n", false)]
	[DataRow("error: Source/Foo.cpp: wrong type\n", false)]
	[DataRow("", false)]
	public void GitApplyRefusedOnlyWhenEveryErrorIsOneOfItsCheck(string standardError, bool refused)
	{
		Assert.AreEqual(refused, MutateCommand.GitRefused(standardError));
	}

	[TestMethod]
	[DataRow("deleted")]
	[DataRow("created")]
	public async Task AFileGoneOrMadeBeforeGitReadsItMakesGitRefuseAndIsLeftAsItIs(string race)
	{
		// R-5: git checks every file before it writes any, and refuses a file to change that is gone, or one to create that
		// exists: it wrote nothing, so the file is someone else's (left as it is), and the run is refused with its journal cleared.
		bool Created = race == "created";
		(Sandbox Box, string Patch) = WithPatch(Created ? "--- /dev/null\n+++ b/Source/Game/Private/New.cpp\n@@ -0,0 +1 @@\n+int New;\n" : null);
		using Sandbox _ = Box;
		using GitObjectsWritable __ = new(Box.ProjectDirectory);
		File.WriteAllText(Box.SourceFile, Original.Replace("\r\n", "\n", StringComparison.Ordinal));
		string Target = Created ? Path.Combine(Path.GetDirectoryName(Box.SourceFile)!, "New.cpp") : Box.SourceFile;
		await GitAsync(Box.ProjectDirectory, "init", "-q");
		await GitAsync(Box.ProjectDirectory, "add", "-A");
		await GitAsync(Box.ProjectDirectory, "-c", "user.name=UAK Tests", "-c", "user.email=uak-tests@example.invalid", "commit", "-q", "-m", "base");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };
		UnrealServices Racing = new()
		{
			Runner = Runner,
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
			CaptureTree = TestSupportTrees.None,
			Capture = (Invocation, Token) =>
			{
				if (!Invocation.Arguments.Contains("--check") && !Invocation.Arguments.Contains("--numstat"))
				{
					if (Created)
					{
						File.WriteAllText(Target, "// another agent's file\n");
					}
					else
					{
						File.Delete(Target);
					}
				}
				return ProcessRunner.Default.CaptureAsync(Invocation, Token);
			},
		};

		int Exit = await new MutateCommand(Racing).RunAsync(Box.Context(), ["-name=GN", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(2, Exit, Box.Logger.Text);
		Assert.Contains("wrote nothing", Box.Logger.Text);
		Assert.AreEqual(Created, File.Exists(Target), "left as the race left it");
		if (Created)
		{
			Assert.AreEqual("// another agent's file\n", File.ReadAllText(Target));
		}
		Assert.IsEmpty(Runner.Invocations);
		Assert.IsFalse(Directory.Exists(Path.Combine(MutationJournal.GetRoot(Box.Context()), "GN")));
	}

	// ---- what review 2 found ----

	[TestMethod]
	public async Task AMutantBuildThatCompilesOnlyOtherFilesIsInconclusive()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		// Another agent's file compiled, the patched one didn't: the binaries may not hold the patch.
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: Build => Build == 0 ? ["[1/2] Compile [x64] OtherAgentsFile.cpp", "[2/2] Link [x64] UnrealEditor-Game.dll"] : CompileLines),
		};

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M13", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Exit, Box.Logger.Text);
		Assert.Contains("shows no compile of " + Box.SourceFile, Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { true, true }, Runner.Invocations.Select(IsBuild).ToArray(), "no test ran");
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public async Task AJournalAsksNoRebuildToCompileWhatTheMutantBuildNeverCompiled()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		// The dll it wrote is another agent's change: the patched file reached no binary, and no later build need compile it,
		// which is what keeps a file outside the target from holding its journal for ever.
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: Build =>
			{
				if (Build > 0)
				{
					return UpToDate;
				}
				File.WriteAllText(Dll, "another agent's change");
				return ["[1/2] Compile [x64] OtherAgentsFile.cpp", "[2/2] Link [x64] UnrealEditor-Game.dll"];
			}),
		};

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M13b", "-patch=" + Patch, "-filter=Project.Math", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Exit, Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "M13b")), Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.IsFalse(Result.RootElement.GetProperty("BinariesPending").GetBoolean());
	}

	[TestMethod]
	[DataRow("Foo.cpp", true)]
	[DataRow("foo.cpp", true)]
	[DataRow("Module.Game.cpp", true)]
	[DataRow("Module.Game.3.cpp", true)]
	[DataRow("Module.Game.gen.cpp", false)]
	[DataRow("Module.Game.gen.1.cpp", false)]
	[DataRow("Module.GameExtra.cpp", false)]
	[DataRow("OtherAgentsFile.cpp", false)]
	public void APatchedSourceFileCountsAsCompiledByItsNameOrItsModulesUnityFile(string item, bool compiled)
	{
		using Sandbox Box = new();
		List<DiffFile> Files = UnifiedDiff.Parse(GitPatch);

		List<string> Uncompiled = MutateCommand.FindUncompiled(new BuildOutcome(true, false, [item], []), Files, [Box.SourceFile]);

		Assert.AreEqual(compiled, Uncompiled.Count == 0, item);
		// A header needs a compile, of any file; a file that isn't C++ needs none.
		List<DiffFile> Header = [Files[0] with { Path = "Source/Game/Public/Foo.h" }];
		Assert.IsEmpty(MutateCommand.FindUncompiled(new BuildOutcome(true, false, [item], []), Header, [Box.HeaderFile]));
		Assert.HasCount(1, MutateCommand.FindUncompiled(new BuildOutcome(true, true, [], []), Header, [Box.HeaderFile]));
		string Config = Path.Combine(Box.ProjectDirectory, "Config", "DefaultGame.ini");
		Assert.IsEmpty(MutateCommand.FindUncompiled(new BuildOutcome(true, true, [], []), [Files[0] with { Path = "Config/DefaultGame.ini" }], [Config]));
	}

	[TestMethod]
	public async Task AListedTestThatIsSkippedIsInconclusiveNotCaught()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		List<string> Names = [.. Fixtures.Lines("test-passed.log").Where(Line => Line.Contains("Path={", StringComparison.Ordinal))
			.Select(Line => Line[(Line.IndexOf("Path={", StringComparison.Ordinal) + "Path={".Length)..].TrimEnd('}'))];
		string Expect = Sandbox.Write(Path.Combine(Box.Root, "expect.txt"), string.Join("\n", Names) + "\n");
		string Skipped = $"Path={{{Names[0]}}}";
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], editLog: Log => Log.Select(Line => Line.Contains(Skipped, StringComparison.Ordinal)
				? Line.Replace("Result={Success}", "Result={Skipped}", StringComparison.Ordinal) : Line)),
		};

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=M14", "-patch=" + Patch, "-filter=Project.Math", "-expect=" + Expect], CancellationToken.None);

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Exit, Box.Logger.Text);
		Assert.Contains("mutate: INCONCLUSIVE: no test failed", Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public async Task ARunThatIsRefusedOrStillRunsNeverShowsAnEarlierResultUnderItsName()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		DirectoryInfo State = new(Box.StateDirectory);
		RunSummaryEntry? DuringTest = null;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0], () => DuringTest ??= RunSummaries.Read(State, "S1")!.Entries.Single()) };
		Assert.AreEqual(MutateCommand.SurvivedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(),
			["-name=S1", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.IsNull(DuringTest!.ExitCode, "while it runs, its name has no result");
		string Bad = Sandbox.Write(Path.Combine(Box.Root, "bad.diff"), "--- a/Source/Game/Private/Foo.cpp\n+++ b/Source/Game/Private/Foo.cpp\n@@ -3 +3 @@\n-\treturn X;\n+\treturn Y;\n");

		Assert.AreEqual(2, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=S1", "-patch=" + Bad, "-filter=Project.Math"], CancellationToken.None));

		RunSummaryEntry Mutate = RunSummaries.Read(State, "S1")!.Entries.Single();
		Assert.AreEqual(2, Mutate.ExitCode);
		Assert.Contains("mutate: REFUSED", Mutate.Lines[^1]);

		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math", "-name=T1"], CancellationToken.None), Box.Logger.Text);
		LeaveJournal(Box, "J", int.MaxValue, DateTime.UtcNow);
		Assert.AreEqual(2, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math", "-name=T1"], CancellationToken.None));
		RunSummaryEntry Test = RunSummaries.Read(State, "T1")!.Entries.Single();
		Assert.AreEqual(2, Test.ExitCode);
		Assert.Contains("test: REFUSED", Test.Lines[^1]);
		Assert.AreEqual(2, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=B1"], CancellationToken.None));
		Assert.Contains("build: REFUSED", RunSummaries.Read(State, "B1")!.Entries.Single().Lines[^1]);
	}

	[TestMethod]
	public async Task AFileThatCannotBeCopiedLeavesNoJournalBehind()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };

		using (FileStream Held = new(Box.SourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			Assert.AreEqual(2, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=BF", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		}

		Assert.Contains("could not be copied before the patch", Box.Logger.Text);
		Assert.IsFalse(Directory.Exists(Path.Combine(MutationJournal.GetRoot(Box.Context()), "BF")));
		Assert.IsNull(MutationFolders.DescribeLeft(new DirectoryInfo(Box.StateDirectory), "shelve"));
		string Pending = MutationFolders.GetPendingRoot(new DirectoryInfo(Box.StateDirectory));
		Assert.IsFalse(Directory.Exists(Pending) && Directory.EnumerateDirectories(Pending).Any(), "the copies taken so far are gone");
		Assert.IsEmpty(Runner.Invocations);
		Assert.AreEqual(0, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	[DataRow("Dot.")]
	[DataRow("NUL")]
	[DataRow("nul.txt")]
	[DataRow("COM1")]
	[DataRow("Lpt9.log")]
	[DataRow("CON")]
	public async Task ANameWindowsWouldChangeIsRefused(string name)
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new();

		Assert.IsFalse(RunSummaries.IsValidName(name), name);
		Assert.AreEqual(2, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=" + name, "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None));
		Assert.AreEqual(2, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math", "-name=" + name], CancellationToken.None));
		Assert.AreEqual(2, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=" + name], CancellationToken.None));
		Assert.AreEqual(2, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-restore", "-name=" + name, "-accept"], CancellationToken.None));
		Assert.ThrowsExactly<UakUsageException>(() => MutationJournal.Begin(Box.Context(), name, "p.diff", [Box.SourceFile]));
		Assert.IsEmpty(Runner.Invocations);
		Assert.IsFalse(Directory.Exists(MutationJournal.GetRoot(Box.Context())) && Directory.EnumerateFileSystemEntries(MutationJournal.GetRoot(Box.Context())).Any());
		foreach (string Good in new[] { "Dot.1", "CONSOLE", "NULL", "Com10", "nul-check" })
		{
			Assert.IsTrue(RunSummaries.IsValidName(Good), Good);
		}
	}

	/// <summary>
	/// Leaves a journal as a run killed while it wrote its patch would: phase Applying, the bytes the patch was to give the
	/// file recorded, and the file holding <paramref name="written"/>.
	/// </summary>
	static void LeaveApplyingJournal(Sandbox box, string name, string planned, string written)
	{
		MutationJournal Journal = MutationJournal.Begin(box.Context(), name, "p.diff", [box.SourceFile]);
		Journal.SetPlanned(new Dictionary<string, byte[]?> { [box.SourceFile] = Bytes(planned) });
		File.WriteAllText(box.SourceFile, written);
		MutationRecord Record = StateFiles.ReadJson<MutationRecord>(JournalFile(box, name))!;
		Assert.AreEqual(MutationPhase.Applying, Record.Phase);
		Record.Pid = int.MaxValue;
		StateFiles.WriteJson(JournalFile(box, name), Record);
	}

	[TestMethod]
	public async Task ARunKilledWhileItWroteThePatchIsPutBackFromItsPlanAndPartOfAPatchIsNeverAccepted()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		const string Mutant = "int Add(int A, int B) { return A - B; }\n";
		const string Part = "int Add(int A, int B) { ret";
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };

		// The whole patch was written: the bytes are the planned ones, so they are this run's, put back without asking.
		LeaveApplyingJournal(Box, "Ay", Mutant, Mutant);
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));

		// Part of it: neither the original nor the plan. Never kept, and -accept isn't offered.
		LeaveApplyingJournal(Box, "Ay2", Mutant, Part);
		int Before = Box.Logger.Entries.Count;
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None));
		string Said = string.Join("\n", Box.Logger.Entries.Skip(Before).Select(Entry => Entry.Message));
		Assert.Contains("stopped while it wrote the patch", Said);
		Assert.IsFalse(Said.Contains("-accept", StringComparison.Ordinal), Said);
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=Ay2", "-accept"], CancellationToken.None));
		Assert.AreEqual(Part, File.ReadAllText(Box.SourceFile), "nothing is written while it is refused");
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), "-accept never keeps part of a patch");

		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=Ay2", "-force"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		string SetAside = Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "Ay2-*").Single();
		Assert.AreEqual(Part, File.ReadAllText(Directory.GetFiles(SetAside, "0.*.current").Single()));
	}

	[TestMethod]
	public async Task AcceptPutsBackAFileThatStillHoldsTheMutation()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "Mu", int.MaxValue, DateTime.UtcNow);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };

		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=Mu", "-accept"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile), "the mutation is no later edit");
	}

	[TestMethod]
	public void AFileChangedJustBeforeItIsWrittenIsNeitherWrittenNorPutBack()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		List<string> Paths = [Box.SourceFile, Box.HeaderFile];
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "W", "p.diff", Paths);
		Assert.AreEqual("0.Foo.cpp.orig", Journal.Record.Files[0].Backup, "a copy is named after its file");
		Dictionary<string, byte[]?> Planned = new() { [Box.SourceFile] = Bytes("mutant\n"), [Box.HeaderFile] = Bytes("#pragma once\n// mutant\n") };
		Journal.SetPlanned(Planned);
		File.WriteAllText(Box.HeaderFile, "another agent's edit\n");
		HashSet<string> Untouched = new(Paths, StringComparer.Ordinal);

		Assert.ThrowsExactly<MutationFileChangedException>(() => MutateCommand.WritePlanned(Journal, Planned, Untouched));

		Assert.AreEqual("mutant\n", File.ReadAllText(Box.SourceFile), "written before the change was seen");
		CollectionAssert.AreEqual(new[] { Box.HeaderFile }, Untouched.ToArray());
		MutationRestore Restore = Journal.Restore(MutationRestoreMode.Normal, Untouched);
		Assert.IsEmpty(Restore.Conflicts);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		Assert.AreEqual("another agent's edit\n", File.ReadAllText(Box.HeaderFile));
	}

	[TestMethod]
	public void TheBlameRuleMatchesWholeIncludeNamesAndPathParts()
	{
		using Sandbox Box = new();
		string Private = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private");
		string Similar = Sandbox.Write(Path.Combine(Private, "Bar.cpp"), "#include \"BarFoo.h\"\nint X;\n");
		string ByPath = Sandbox.Write(Path.Combine(Private, "Baz.cpp"), "#include \"Game/Public/Foo.h\"\nint X;\n");
		string Angled = Sandbox.Write(Path.Combine(Private, "Qux.cpp"), "  #  include <Foo.h> // spaced\nint X;\n");
		static CompileDiagnostic In(string file) => new(file, 2, 1, DiagnosticSeverity.Error, "C2065", "undeclared", null);

		Assert.IsFalse(MutateCommand.BlamesPatch(In(Similar), [Box.HeaderFile]), "BarFoo.h is not Foo.h");
		Assert.IsTrue(MutateCommand.BlamesPatch(In(ByPath), [Box.HeaderFile]));
		Assert.IsTrue(MutateCommand.BlamesPatch(In(Angled), [Box.HeaderFile]));
		Assert.IsFalse(MutateCommand.BlamesPatch(In("Other/Foo.cpp"), [Box.SourceFile]), "another folder's Foo.cpp");
		Assert.IsTrue(MutateCommand.BlamesPatch(In("Private/Foo.cpp"), [Box.SourceFile]));
		Assert.IsTrue(MutateCommand.BlamesPatch(In("Foo.cpp"), [Box.SourceFile]));
		Assert.AreEqual("Foo.h", MutateCommand.IncludedName("#include \"A\\B\\Foo.h\""));
		Assert.IsNull(MutateCommand.IncludedName("// #include \"Foo.h\""));
	}

	[TestMethod]
	public async Task ACompileBesideRunningChecksIsNeverRefusedForAJournalBeingWrittenOrDeleted()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		int Refused = 0;
		string? First = null;
		using CancellationTokenSource Stop = new();
		Task Poller = Task.Run(() =>
		{
			while (!Stop.IsCancellationRequested)
			{
				try
				{
					MutationJournal.CheckForCompile(Box.Context());
				}
				catch (UakUsageException Exception)
				{
					Interlocked.Increment(ref Refused);
					First ??= Exception.Message;
				}
			}
		});

		for (int Index = 0; Index < 12; Index++)
		{
			FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };
			Assert.AreEqual(MutateCommand.CaughtExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(),
				[$"-name=Race{Index}", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);
		}
		await Stop.CancelAsync();
		await Poller;

		Assert.AreEqual(0, Refused, First);
	}

	[TestMethod]
	public async Task StatusSaysWhetherAJournalIsLeftWithoutTheLock()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		FakeLock Lock = new();
		UnrealServices Status = Services(new FakeProcessRunner(), Lock);

		Assert.AreEqual(0, await new MutateCommand(Status).RunAsync(Box.Context(), ["-status"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("no journal is left", Box.Logger.Text);

		ProcessIdentity Self = ProcessIdentity.Current;
		LeaveJournal(Box, "Live", Self.Pid, Self.StartTimeUtc);
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Status).RunAsync(Box.Context(), ["-status"], CancellationToken.None));
		Assert.Contains("'Live' (PID " + Self.Pid + ", running) has its patch in " + Box.SourceFile, Box.Logger.Text);
		Assert.Contains("don't commit, shelve or preflight", Box.Logger.Text);
		StringAssert.Contains(MutationFolders.DescribeLeft(new DirectoryInfo(Box.StateDirectory), "shelve"), Box.SourceFile);
		Assert.IsEmpty(Lock.Names, "it takes no lock: a running check holds it");
		Assert.AreEqual(2, await new MutateCommand(Status).RunAsync(Box.Context(), ["-status", "-name=Live"], CancellationToken.None));
	}

	// ---- what review 3 found ----

	[TestMethod]
	public async Task AcceptTakesTheMutationOutOfAnEditMadeOverItAndKeepsTheEdit()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		// Another agent edits the patched file while the check runs: its edit keeps the mutant line, and adds one above.
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0], () => File.WriteAllText(Box.SourceFile, "// agent B's edit\r\n" + File.ReadAllText(Box.SourceFile))),
		};
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(),
			["-name=AM", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("made over the mutant", Box.Logger.Text);

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=AM", "-accept"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual("// agent B's edit\r\n" + Original, File.ReadAllText(Box.SourceFile), "the mutation is out, the edit kept");
		Assert.Contains("took the mutation out of it", Box.Logger.Text);
		string SetAside = Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "AM-*").Single();
		Assert.AreEqual("// agent B's edit\r\n" + Original.Replace("A + B", "A - B", StringComparison.Ordinal),
			File.ReadAllText(Directory.GetFiles(SetAside, "0.*.current").Single()), "what it held is saved first");
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-status"], CancellationToken.None));
	}

	[TestMethod]
	public async Task AcceptRefusesARenameOverTheMutantAndForceSavesItBeforePuttingTheOriginalBack()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		// acceptrename: the edit renames over the mutated line, so the mutation is still in it, in other words.
		const string Renamed = "int Add(int Lhs, int Rhs)\r\n{\r\n\treturn Lhs - Rhs;\r\n}\r\n";
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0], () => File.WriteAllText(Box.SourceFile, Renamed)) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(),
			["-name=AR", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		int Before = Box.Logger.Entries.Count;

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=AR", "-accept"], CancellationToken.None));

		string Said = string.Join("\n", Box.Logger.Entries.Skip(Before).Select(Entry => Entry.Message));
		Assert.Contains("-accept can't keep it", Said);
		Assert.Contains("line 3 as it is now ('return Lhs - Rhs;'), which the original has as 'return A + B;'", Said);
		Assert.DoesNotContain("by hand, then -accept again", Said, "a hint that can never clear it");
		Assert.AreEqual(Renamed, File.ReadAllText(Box.SourceFile), "nothing is written while it is refused");
		Assert.IsEmpty(Later.Invocations, "no rebuild of a tree that may hold the mutation");
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-status"], CancellationToken.None));

		// -force puts the original back, and keeps the edit beside the journal, to be made again on the original.
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=AR", "-force"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		string SetAside = Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "AR-*").Single();
		Assert.AreEqual(Renamed, File.ReadAllText(Directory.GetFiles(SetAside, "0.*.current").Single()));
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task AcceptKeepsACommentOnAGuardAboveTheMutationAndTakesTheMutationOut()
	{
		// acceptguard: the guard's 'return false;' is in the hunk's context, and the mutation adds another 'return false;'. The
		// comment goes on the guard's condition, two lines above the block that pins the change (on the guard's 'return false;',
		// the line before that block, it is refused: MutationMergeTests).
		const string Guard = "bool IsValid(int V)\r\n{\r\n\tif (V < 0)\r\n\t{\r\n\t\treturn false;\r\n\t}\r\n\treturn true;\r\n}\r\n";
		const string GuardDiff = "diff --git a/Source/Game/Private/Foo.cpp b/Source/Game/Private/Foo.cpp\n--- a/Source/Game/Private/Foo.cpp\n+++ b/Source/Game/Private/Foo.cpp\n"
			+ "@@ -4,5 +4,5 @@ bool IsValid(int V)\n \t{\n \t\treturn false;\n \t}\n-\treturn true;\n+\treturn false;\n }\n";
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Guard);
		string Patch = Sandbox.Write(Path.Combine(Box.Root, "guard.diff"), GuardDiff);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0], () => File.WriteAllText(Box.SourceFile,
				File.ReadAllText(Box.SourceFile).Replace("\tif (V < 0)\r\n", "\tif (V < 0) // negative values are invalid\r\n", StringComparison.Ordinal))),
		};
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(),
			["-name=GD", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);
		StringAssert.Contains(File.ReadAllText(Box.SourceFile), "\r\n\treturn false;\r\n}", "the mutant, under the edit");

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=GD", "-accept"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(Guard.Replace("\tif (V < 0)\r\n", "\tif (V < 0) // negative values are invalid\r\n", StringComparison.Ordinal), File.ReadAllText(Box.SourceFile),
			"the comment kept, 'return true;' back");
		Assert.Contains("took the mutation out of it", Box.Logger.Text);
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-status"], CancellationToken.None));
	}

	[TestMethod]
	public async Task AJournalAReaderHoldsOpenStaysWholeAndTheVerdictStands()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Record = JournalFile(Box, "CF");
		Task? Reader = null;
		// During the rebuild after, a reader opens journal.json without delete sharing, and holds it past the clear's retries.
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0], buildOutput: Build =>
			{
				if (Build == 1)
				{
					using ManualResetEventSlim Opened = new();
					Reader = Task.Run(() =>
					{
						using FileStream Held = new(Record, FileMode.Open, FileAccess.Read, FileShare.Read);
						Opened.Set();
						Thread.Sleep(MutationJournal.ClearRetryTime + TimeSpan.FromSeconds(1));
					});
					Opened.Wait();
				}
				return CompileLines;
			}),
		};

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=CF", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);
		await Reader!;

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains("the journal could not be cleared", Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		Assert.AreEqual(MutationPhase.Restored, StateFiles.ReadJson<MutationRecord>(Record)!.Phase);
		Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(Record)!, "*.orig"), "left whole, never deleted in place");

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=CF"], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(Record)));
	}

	[TestMethod]
	public async Task ASecondForceKeepsTheBytesTheFirstSaved()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		File.WriteAllText(Box.HeaderFile, "#pragma once\r\nint F();\r\n");
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "F2", "p.diff", [Box.SourceFile, Box.HeaderFile]);
		File.WriteAllText(Box.SourceFile, "mutant\r\n");
		File.WriteAllText(Box.HeaderFile, "#pragma once\r\nint G();\r\n");
		Journal.MarkApplied();
		Journal.Record.Pid = int.MaxValue;
		StateFiles.WriteJson(JournalFile(Box, "F2"), Journal.Record);
		File.WriteAllText(Box.SourceFile, "agent A's edit\r\n");
		File.WriteAllText(Box.HeaderFile, "a header edit\r\n");
		// The header's copy is damaged: -force can't put it back, so the journal stays after each -force.
		File.WriteAllText(Path.Combine(Journal.Directory, Journal.Record.Files[1].Backup!), "damaged\r\n");
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=F2", "-force"], CancellationToken.None));
		File.WriteAllText(Box.SourceFile, "agent B's edit\r\n");
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=F2", "-force"], CancellationToken.None));

		CollectionAssert.AreEquivalent(new[] { "agent A's edit\r\n", "agent B's edit\r\n" },
			Directory.GetFiles(Journal.Directory, "0.*.current").Select(File.ReadAllText).ToArray(), "each -force keeps what it overwrote");
	}

	[TestMethod]
	public async Task AnEditThatGetsInWhileGitWritesThePatchIsNeverLost()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		using GitObjectsWritable __ = new(Box.ProjectDirectory);
		// Lines after the function: the edit appended to the file is then two lines from the block that pins the mutated line.
		string Lf = Original.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n// end\n";
		File.WriteAllText(Box.SourceFile, Lf);
		await GitAsync(Box.ProjectDirectory, "init", "-q");
		await GitAsync(Box.ProjectDirectory, "add", "-A");
		await GitAsync(Box.ProjectDirectory, "-c", "user.name=UAK Tests", "-c", "user.email=uak-tests@example.invalid", "commit", "-q", "-m", "base");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };
		UnrealServices Racing = new()
		{
			Runner = Runner,
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
			CaptureTree = TestSupportTrees.None,
			Capture = (Invocation, Token) =>
			{
				// Another agent's edit lands after uak's last look, before git reads the file.
				if (!Invocation.Arguments.Contains("--check") && !Invocation.Arguments.Contains("--numstat"))
				{
					File.AppendAllText(Box.SourceFile, "// agent B's edit\n");
				}
				return ProcessRunner.Default.CaptureAsync(Invocation, Token);
			},
		};

		int Exit = await new MutateCommand(Racing).RunAsync(Box.Context(), ["-name=GR", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, Exit, Box.Logger.Text);
		Assert.Contains("an edit got in while git wrote the patch", Box.Logger.Text);
		string Now = File.ReadAllText(Box.SourceFile);
		Assert.Contains("// agent B's edit", Now, "left in place, never overwritten");
		Assert.Contains("return A - B;", Now);
		Assert.AreEqual(Now, File.ReadAllText(Directory.GetFiles(Path.GetDirectoryName(JournalFile(Box, "GR"))!, "0.*.current").Single()), "and saved");
		Assert.IsEmpty(Runner.Invocations, "no verdict: nothing built or tested");

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=GR", "-accept"], CancellationToken.None), Box.Logger.Text);
		string After = File.ReadAllText(Box.SourceFile);
		Assert.Contains("// agent B's edit", After);
		Assert.Contains("return A + B;", After);
		Assert.DoesNotContain("A - B", After);
	}

	[TestMethod]
	public void AWriteOfThePatchThatFailsAfterItsBytesLandedPutsTheFileBackAtOnce()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "WF", "p.diff", [Box.SourceFile]);
		Dictionary<string, byte[]?> Planned = new() { [Box.SourceFile] = Bytes("int Add(int A, int B) { return A - B; }\n") };
		Journal.SetPlanned(Planned);
		HashSet<string> Untouched = new([Box.SourceFile], StringComparer.Ordinal);

		Assert.ThrowsExactly<IOException>(() => MutateCommand.WritePlanned(Journal, Planned, Untouched, (Written, Content) =>
		{
			// The bytes are in place (WriteFile renames them in whole), then keeping the file's attributes fails.
			File.WriteAllBytes(Written, Content);
			throw new IOException("the attributes could not be set");
		}));

		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile), "the run's own failed write is put back at once");
		Assert.AreEqual("int Add(int A, int B) { return A - B; }\n", File.ReadAllText(Directory.GetFiles(Journal.Directory, "0.*.current").Single()), "what it held is saved first");
		MutationRestore Restore = Journal.Restore(MutationRestoreMode.Normal, Untouched);
		Assert.IsEmpty(Restore.Conflicts);
		Assert.IsFalse(Restore.StoppedWhileApplying);
	}

	[TestMethod]
	public void PartOfTheRunsBytesIsNoWriteOfTheRun()
	{
		// R-7: WriteFile writes a file whole, so one that holds only the start of the planned bytes holds someone else's: it is
		// never put back over. (The old check took any start of the plan for the run's own write.)
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "WP", "p.diff", [Box.SourceFile]);
		Dictionary<string, byte[]?> Planned = new() { [Box.SourceFile] = Bytes("int Add(int A, int B) { return A - B; }\n") };
		Journal.SetPlanned(Planned);
		HashSet<string> Untouched = new([Box.SourceFile], StringComparer.Ordinal);

		Assert.ThrowsExactly<IOException>(() => MutateCommand.WritePlanned(Journal, Planned, Untouched, (Written, Content) =>
		{
			File.WriteAllBytes(Written, Content[..10]);
			throw new IOException("the disk is full");
		}));

		Assert.AreEqual("int Add(in", File.ReadAllText(Box.SourceFile), "never put back over");
		CollectionAssert.AreEqual(new[] { Box.SourceFile }, Untouched.ToArray(), "so the restore leaves it too");
		Assert.IsEmpty(Directory.GetFiles(Journal.Directory, "0.*.current"));
	}

	[TestMethod]
	public async Task APatchedFileThatIsAHardLinkIsRefusedBeforeAnythingIsWritten()
	{
		// R-8: a new file renamed over one of several hard links parts them (the other name keeps the old bytes), and clearing the
		// read-only flag first clears it for every name.
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("the test makes its hard link with CreateHardLinkW");
		}
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Other = Path.Combine(Box.Root, "Other.cpp");
		Assert.IsTrue(CreateHardLinkW(Other, Box.SourceFile, IntPtr.Zero), "CreateHardLinkW failed");
		File.SetAttributes(Box.SourceFile, FileAttributes.ReadOnly);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };
		try
		{
			int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=HL", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

			Assert.AreEqual(2, Exit, Box.Logger.Text);
			Assert.Contains("one of 2 hard links", Box.Logger.Text);
			Assert.IsEmpty(Runner.Invocations);
			Assert.IsFalse(Directory.Exists(Path.Combine(MutationJournal.GetRoot(Box.Context()), "HL")));
			Assert.IsTrue(File.GetAttributes(Other).HasFlag(FileAttributes.ReadOnly), "the flag both names share is kept");
			IOException Error = Assert.ThrowsExactly<IOException>(() => MutationJournal.WriteFile(Box.SourceFile, Bytes("int Mutated;\n")));
			Assert.Contains("hard links", Error.Message);
			Assert.AreEqual(Original, File.ReadAllText(Other));
			Assert.IsTrue(File.GetAttributes(Other).HasFlag(FileAttributes.ReadOnly));
		}
		finally
		{
			File.SetAttributes(Box.SourceFile, FileAttributes.Normal);
		}
		// Still one file: a write through one name shows through the other.
		File.AppendAllText(Box.SourceFile, "// more\r\n");
		Assert.AreEqual(File.ReadAllText(Box.SourceFile), File.ReadAllText(Other));
	}

	[TestMethod]
	public async Task AFileThatBecameALinkDuringTheRunIsLeftAndSaidSo()
	{
		// V-5: a hard link made to the patched file while the tests ran. uak never writes a link, so the restore leaves it; the
		// hint said the file was edited. It says it became a link now, and -restore puts it back once the other link is gone.
		if (!OperatingSystem.IsWindows())
		{
			Assert.Inconclusive("the test makes its hard link with CreateHardLinkW");
		}
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Other = Path.Combine(Box.Root, "Other.cpp");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0], () => Assert.IsTrue(CreateHardLinkW(Other, Box.SourceFile, IntPtr.Zero))) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=HL2", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, Exit, Box.Logger.Text);
		Assert.Contains("it became one of 2 hard links", Box.Logger.Text);
		Assert.Contains("once each file the lines above say became a link is a plain file again", Box.Logger.Text);
		Assert.DoesNotContain("They were edited after the patch was applied", Box.Logger.Text);
		Assert.Contains("return A - B;", File.ReadAllText(Box.SourceFile), "left as it is");

		// -force can't write it either, and says so.
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Box.Logger.Entries.Clear();
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=HL2", "-force"], CancellationToken.None));
		Assert.Contains("became a link is left as it is", Box.Logger.Text);

		File.Delete(Other);
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=HL2"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public void ASymbolicLinkIsNeverWrittenOver()
	{
		// R-8: a new file renamed over a symbolic link replaces the link with a file of its own.
		using Sandbox Box = new();
		string Target = Sandbox.Write(Path.Combine(Box.Root, "Target.cpp"), "int Target;\n");
		string Link = Path.Combine(Box.Root, "Link.cpp");
		try
		{
			File.CreateSymbolicLink(Link, Target);
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			Assert.Inconclusive($"this machine makes no symbolic links ({Exception.Message})");
		}

		Assert.Contains("a symbolic link", MutationJournal.DescribeLink(Link)!);
		Assert.ThrowsExactly<IOException>(() => MutationJournal.WriteFile(Link, Bytes("int Mutated;\n")));
		Assert.IsNotNull(new FileInfo(Link).LinkTarget, "still a link");
		Assert.AreEqual("int Target;\n", File.ReadAllText(Target));
		Assert.IsNull(MutationJournal.DescribeLink(Target));
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
	static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

	[TestMethod]
	public async Task AFolderLeftWithoutAJournalThatCannotBeDeletedIsRefused()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Junk = Sandbox.Write(Path.Combine(MutationJournal.GetRoot(Box.Context()), "LF", "junk.txt"), "junk");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };

		using (FileStream Held = new(Junk, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			Assert.AreEqual(2, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=LF", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		}

		Assert.Contains("can't be deleted", Box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public void AModulesShortNameNamesItsUnityFileAndASameNamedFileElsewhereIsSaid()
	{
		using Sandbox Box = new();
		List<DiffFile> Files = UnifiedDiff.Parse(GitPatch);
		File.WriteAllText(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Game.Build.cs"),
			"public class Game : ModuleRules { public Game(ReadOnlyTargetRules Target) : base(Target) { ShortName = \"Gm\"; } }");
		List<string> Uncompiled(params string[] compiled) => MutateCommand.FindUncompiled(new BuildOutcome(true, false, compiled, []), Files, [Box.SourceFile]);

		Assert.IsEmpty(Uncompiled("Module.Gm.cpp"), "UBT names the unity file after the ShortName");
		Assert.IsEmpty(Uncompiled("Module.Gm.2.cpp"));
		Assert.HasCount(1, Uncompiled("Module.Other.cpp"));

		// Compiled by its name alone while another module has a file of that name: said, as that compile may be the other's.
		string Other = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Tools", "Source", "Tools", "Private", "Foo.cpp"), "int Other;\n");
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Tools", "Intermediate", "Foo.cpp"), "");
		List<string> Ambiguous = [];
		Assert.IsEmpty(MutateCommand.FindUncompiled(new BuildOutcome(true, false, ["Foo.cpp"], []), Files, [Box.SourceFile], Ambiguous, Box.ProjectDirectory));
		Assert.Contains(Other, Ambiguous.Single());
		Ambiguous.Clear();
		Assert.IsEmpty(MutateCommand.FindUncompiled(new BuildOutcome(true, false, ["Foo.cpp", "Module.Gm.cpp"], []), Files, [Box.SourceFile], Ambiguous, Box.ProjectDirectory));
		Assert.IsEmpty(Ambiguous, "its own module's unity file settles it");
	}

	[TestMethod]
	public async Task ACompileUbtTagsWithWhereItRanStillCounts()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0], buildOutput: Build => Build == 0
				? ["[1/2] Compile [x64] Foo.cpp [RemoteExecutor: build-agent-7]", "[2/2] Link [x64] UnrealEditor-Game.dll [Cache]"] : CompileLines),
		};

		Assert.AreEqual(MutateCommand.CaughtExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(),
			["-name=RX", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task ARefusedBuildTestOrCompileNeverLeavesAnOldResultFileInPlace()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "Old", int.MaxValue, DateTime.UtcNow);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		(IUakCommand Command, string[] Arguments)[] Refused =
		[
			(new TestCommand(Services(Runner)), ["-filter=Project.Math"]),
			(new BuildCommand(Services(Runner)), []),
			(new CompileCommand(Services(Runner)), [Box.SourceFile]),
		];

		foreach ((IUakCommand Command, string[] Arguments) in Refused)
		{
			string ResultFile = Sandbox.Write(Path.Combine(Box.Root, "result.json"), "{ \"Outcome\": \"passed\" }");
			Assert.AreEqual(2, await Command.RunAsync(Box.Context(), [.. Arguments, "-resultfile=" + ResultFile], CancellationToken.None), Command.Name);
			using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
			Assert.AreEqual("refused", Result.RootElement.GetProperty("Outcome").GetString(), Command.Name);
			Assert.AreEqual(2, Result.RootElement.GetProperty("ExitCode").GetInt32(), Command.Name);
			Assert.Contains("uak mutate -restore", Result.RootElement.GetProperty("Message").GetString()!, Command.Name);
		}
		Assert.IsEmpty(Runner.Invocations);
	}

	// ---- what review 4 found (the -accept merge's own cases are in MutationMergeTests) ----

	[TestMethod]
	public async Task WhenGitRefusesThePatchAnEditThatGotInIsLeftAndTheJournalCleared()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		using GitObjectsWritable __ = new(Box.ProjectDirectory);
		string Lf = Original.Replace("\r\n", "\n", StringComparison.Ordinal);
		File.WriteAllText(Box.SourceFile, Lf);
		await GitAsync(Box.ProjectDirectory, "init", "-q");
		await GitAsync(Box.ProjectDirectory, "add", "-A");
		await GitAsync(Box.ProjectDirectory, "-c", "user.name=UAK Tests", "-c", "user.email=uak-tests@example.invalid", "commit", "-q", "-m", "base");
		string Edited = Lf.Replace("int Add(int A, int B)", "int Add(int A, int B) // agent B", StringComparison.Ordinal);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };
		UnrealServices Racing = new()
		{
			Runner = Runner,
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
			CaptureTree = TestSupportTrees.None,
			Capture = (Invocation, Token) =>
			{
				// Another agent edits a context line after uak's last look, before git reads the file: the patch no longer applies.
				if (!Invocation.Arguments.Contains("--check") && !Invocation.Arguments.Contains("--numstat"))
				{
					File.WriteAllText(Box.SourceFile, Edited);
				}
				return ProcessRunner.Default.CaptureAsync(Invocation, Token);
			},
		};

		Assert.AreEqual(2, await new MutateCommand(Racing).RunAsync(Box.Context(), ["-name=GF", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("git refused the patch and wrote nothing", Box.Logger.Text);
		Assert.AreEqual(Edited, File.ReadAllText(Box.SourceFile), "the other agent's edit is left as it is");
		Assert.IsEmpty(Runner.Invocations, "nothing built or tested");
		Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(JournalFile(Box, "GF"))), "the journal is cleared");
		Assert.IsFalse(Directory.Exists(MutationJournal.GetDiscardedRoot(Box.Context())), "nothing needed saving");
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-status"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	[DataRow(MutationRestoreMode.Normal, false)]
	[DataRow(MutationRestoreMode.Force, true)]
	[DataRow(MutationRestoreMode.Accept, true)]
	public void ARestoreNeverWritesOverAFileChangedAfterItWasLookedAt(MutationRestoreMode mode, bool edited)
	{
		using Sandbox Box = new();
		const string Late = "a late edit\r\n";
		string Mutant = Original.Replace("A + B", "A - B", StringComparison.Ordinal);
		// Normal puts back the mutant; Force overwrites an edit; Accept takes the mutation out of an edit made over it (above it:
		// the strict near-edit rule refuses one at the file's end, right after the mutated block).
		string Now = !edited ? Mutant : mode == MutationRestoreMode.Force ? "an edit\r\n" : "// an edit\r\n" + Mutant;
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "RC", "p.diff", [Box.SourceFile], Bytes(GitPatch));
		File.WriteAllText(Box.SourceFile, Mutant);
		Journal.MarkApplied();
		File.WriteAllText(Box.SourceFile, Now);

		// An edit lands after the file was read and judged, just before it would be written.
		MutationRestore Restore = Journal.Restore(mode, null, mode == MutationRestoreMode.Accept ? MutationUndo.For(Journal) : null, Path => File.WriteAllText(Path, Late));

		Assert.AreEqual(Late, File.ReadAllText(Box.SourceFile), "never written over");
		Assert.Contains("changed again while it was being restored", Restore.Conflicts.Single());
		if (edited)
		{
			Assert.AreEqual(Now, File.ReadAllText(Directory.GetFiles(Journal.Directory, "0.*.current").Single()), "what it held is saved first");
		}
	}

	[TestMethod]
	public void ATreeFileIsWrittenWholeWithItsAttributesAndALeftoverTemporaryFileGoes()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		FileAttributes Kept = FileAttributes.ReadOnly | FileAttributes.Hidden;
		File.SetAttributes(Box.SourceFile, Kept);
		try
		{
			MutationJournal.WriteFile(Box.SourceFile, Bytes("new\n"));

			Assert.AreEqual("new\n", File.ReadAllText(Box.SourceFile));
			Assert.AreEqual(Kept, File.GetAttributes(Box.SourceFile) & Kept);
			Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(Box.SourceFile)!, "Foo.cpp*"), "no temporary file is left");
		}
		finally
		{
			File.SetAttributes(Box.SourceFile, FileAttributes.Normal);
		}

		// A temporary file a killed write left beside a file goes with the next restore; a file merely named alike stays.
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "TF", "p.diff", [Box.SourceFile]);
		File.WriteAllText(Box.SourceFile, "mutant\r\n");
		Journal.MarkApplied();
		string Leftover = Sandbox.Write($"{Box.SourceFile}.1234.{Guid.NewGuid():N}.tmp", "part of a write");
		string Alike = Sandbox.Write(Box.SourceFile + ".notes.tmp", "someone's");

		Assert.IsEmpty(Journal.Restore().Conflicts);

		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		Assert.IsFalse(File.Exists(Leftover));
		Assert.IsTrue(File.Exists(Alike));
	}

	[TestMethod]
	public void AFailedWriteIsPutBackOnlyWhileTheFileHoldsTheRunsOwnBytes()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "WO", "p.diff", [Box.SourceFile]);
		Dictionary<string, byte[]?> Planned = new() { [Box.SourceFile] = Bytes("int Add(int A, int B) { return A - B; }\n") };
		Journal.SetPlanned(Planned);
		HashSet<string> Untouched = new([Box.SourceFile], StringComparer.Ordinal);

		// The write fails without writing, and another agent's edit lands before uak looks again.
		Assert.ThrowsExactly<IOException>(() => MutateCommand.WritePlanned(Journal, Planned, Untouched, (Written, Content) =>
		{
			File.WriteAllText(Written, "another agent's edit\n");
			throw new IOException("the disk is full");
		}));

		Assert.AreEqual("another agent's edit\n", File.ReadAllText(Box.SourceFile), "never put back over: the run never wrote it");
		CollectionAssert.AreEqual(new[] { Box.SourceFile }, Untouched.ToArray(), "so the restore leaves it too");
		MutationRestore Restore = Journal.Restore(MutationRestoreMode.Normal, Untouched);
		Assert.IsEmpty(Restore.Conflicts);
		Assert.AreEqual("another agent's edit\n", File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public async Task AJournalFolderThatCannotBeMadeIsARefusalThatLeavesNothing()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		// A file where the folder journals are written in first: the journal's folder can't be made.
		Sandbox.Write(MutationFolders.GetPendingRoot(new DirectoryInfo(Box.StateDirectory)), "in the way");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };

		Assert.ThrowsExactly<UakUsageException>(() => MutationJournal.Begin(Box.Context(), "PF", "p.diff", [Box.SourceFile]));
		Assert.AreEqual(2, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=PF", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("nothing was changed", Box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		Assert.IsFalse(Directory.Exists(MutationJournal.GetRoot(Box.Context())));
	}

	[TestMethod]
	public async Task TwoTestsUnderTheDefaultNameEachRecordTheirOwnResult()
	{
		using Sandbox Box = new();
		using SemaphoreSlim Running = new(0);
		using SemaphoreSlim Release = new(0);
		Func<ProcessInvocation, Action<string>, int> Failing = Plays("test-failed.log", 255, [0]);
		// B starts first and is held in its editor run; A runs and ends meanwhile. Both have no -name=, so both are uak-test.
		FakeProcessRunner RunnerB = new()
		{
			Behaviour = (Invocation, Output) =>
			{
				if (!IsBuild(Invocation))
				{
					Running.Release();
					Release.Wait();
				}
				return Failing(Invocation, Output);
			},
		};
		FakeProcessRunner RunnerA = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		DirectoryInfo State = new(Box.StateDirectory);

		Task<int> B = Task.Run(() => new TestCommand(Services(RunnerB)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		await Running.WaitAsync();
		Assert.AreEqual(0, await new TestCommand(Services(RunnerA)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		List<RunSummaryEntry> Entries = RunSummaries.Read(State, "uak-test")!.Entries;
		Assert.HasCount(2, Entries, "one entry each: A's result took its own entry's place");
		Assert.AreEqual(0, Entries.Single(Entry => Entry.ExitCode is not null).ExitCode);
		Assert.IsTrue(Entries.Single(Entry => Entry.ExitCode is null).MayStillRun(), "B's entry waits for its result");

		Release.Release();
		Assert.AreEqual(1, await B);

		Entries = RunSummaries.Read(State, "uak-test")!.Entries;
		CollectionAssert.AreEquivalent(new int?[] { 0, 1 }, Entries.Select(Entry => Entry.ExitCode).ToArray());
		Assert.HasCount(2, Entries.Select(Entry => Entry.Invocation).Distinct().ToList());
	}

	[TestMethod]
	public void TheSameNamedFileSearchNeverWalksIntoBuildOutput()
	{
		using Sandbox Box = new();
		string Other = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Tools", "Source", "Tools", "Private", "Foo.cpp"), "int Other;\n");
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Tools", "Intermediate", "Build", "Foo.cpp"), "");
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Binaries", "Foo.cpp"), "");
		List<string> Entered = [];

		List<string> Found = MutateCommand.FindSameNamed(Box.ProjectDirectory, Box.SourceFile, Entered.Add);

		CollectionAssert.AreEqual(new[] { Path.GetFullPath(Other) }, Found);
		Assert.IsFalse(Entered.Any(Folder => Folder.Split(Path.DirectorySeparatorChar).Any(Part => Part is "Intermediate" or "Binaries")), string.Join("\n", Entered));
		Assert.Contains(Path.GetDirectoryName(Box.SourceFile)!, Entered);
	}

	// ---- 0.4.1: a restore the next build recompiles, and a rebuild that fails elsewhere ----

	/// <summary>An object file the mutant build wrote, of the patched file.</summary>
	static string ObjectFile(Sandbox box) => Path.Combine(box.ProjectDirectory, "Intermediate", "Build", "Win64", "UnrealEditor", "Development", "Game", "Foo.cpp.obj");

	[TestMethod]
	public async Task TheFilesPutBackAreNewerThanWhatTheMutantBuildWroteByMoreThanUbtsSlop()
	{
		// UBT counts a prerequisite as newer only when it is more than a second newer: written back within a second of the
		// mutant's object, the source would count as built, and the binaries would keep the mutant.
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Object = ObjectFile(Box);
		DateTime? SourceAtRebuild = null;
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					Sandbox.Write(Object, "mutant code");
				}
				else
				{
					SourceAtRebuild = File.GetLastWriteTimeUtc(Box.SourceFile);
				}
				return CompileLines;
			}),
		};

		int Exit = await new MutateCommand(Services(Runner, realDelay: true)).RunAsync(Box.Context(), ["-name=S1", "-patch=" + Patch, "-filter=Project.Gameplay"],
			CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		DateTime ObjectTime = File.GetLastWriteTimeUtc(Object);
		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, SourceAtRebuild!.Value - ObjectTime, "stamped before the rebuild");
		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.SourceFile) - ObjectTime);
		Assert.IsLessThanOrEqualTo(DateTime.UtcNow, File.GetLastWriteTimeUtc(Box.SourceFile), "the wait keeps the stamp out of the future");
		Assert.IsFalse(File.Exists(JournalFile(Box, "S1")));
	}

	[TestMethod]
	public async Task ARestoreOfAKilledRunStampsItsFilesBeforeItRebuilds()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "S2", 0, null);
		string Object = Sandbox.Write(ObjectFile(Box), "mutant code");
		DateTime? SourceAtRebuild = null;
		FakeProcessRunner Later = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				SourceAtRebuild = File.GetLastWriteTimeUtc(Box.SourceFile);
				return CompileLines;
			}),
		};

		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, SourceAtRebuild!.Value - File.GetLastWriteTimeUtc(Object));
		Assert.IsFalse(File.Exists(JournalFile(Box, "S2")));
	}

	[TestMethod]
	public void AStampKeepsTheBytesAndTheReadOnlyFlagAndMustClearTheSlop()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		MutationJournal Journal = MutationJournal.Begin(Box.Context(), "S3", "p.diff", [Box.SourceFile]);
		File.SetAttributes(Box.SourceFile, FileAttributes.ReadOnly);
		try
		{
			DateTime Artifact = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

			IOException Refused = Assert.ThrowsExactly<IOException>(() => Journal.StampForRecompile(Artifact.AddSeconds(1), Artifact));
			Assert.Contains("for a recompile", Refused.Message);
			Assert.IsNull(Journal.Record.RecompileStamp, "a slop's worth newer is still up to date to UBT");

			Journal.StampForRecompile(Artifact.AddSeconds(2), Artifact);
			Assert.AreEqual(Artifact.AddSeconds(2), File.GetLastWriteTimeUtc(Box.SourceFile));
			Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
			Assert.IsTrue(File.GetAttributes(Box.SourceFile).HasFlag(FileAttributes.ReadOnly));
			Assert.AreEqual(Artifact.AddSeconds(2), StateFiles.ReadJson<MutationRecord>(JournalFile(Box, "S3"))!.RecompileStamp);
		}
		finally
		{
			File.SetAttributes(Box.SourceFile, FileAttributes.Normal);
		}
	}

	[TestMethod]
	public async Task AStampThatDoesNotTakeLeavesTheJournalForRestore()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };

		// A stamp older than the end of the mutant build stands for a file system that didn't take the time.
		int Exit = await new MutateCommand(Services(Runner, recompileMargin: TimeSpan.FromSeconds(-5))).RunAsync(Box.Context(),
			["-name=S4", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, Exit, Box.Logger.Text);
		Assert.Contains("could not be marked for a recompile", Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		CollectionAssert.AreEqual(new[] { true, false }, Runner.Invocations.Select(IsBuild).ToArray(), "no rebuild: it could find the mutant's objects up to date");
		MutationRecord Record = StateFiles.ReadJson<MutationRecord>(JournalFile(Box, "S4"))!;
		Assert.AreEqual(MutationPhase.Restored, Record.Phase);
		Assert.IsNull(Record.RecompileStamp);

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);
		Assert.IsTrue(File.Exists(JournalFile(Box, "S4")), "a build without the stamp may keep the mutant's objects");
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "S4")));
	}

	/// <summary>The project's editor receipt, which lists its module's DLL; the DLL, built an hour before the check. Returns the DLL.</summary>
	static string WriteReceipt(Sandbox box)
	{
		string Binaries = Path.Combine(box.ProjectDirectory, "Binaries", "Win64");
		Sandbox.Write(Path.Combine(Binaries, "GameEditor.target"),
			"""
			{
				"TargetName": "GameEditor",
				"BuildProducts": [
					{ "Path": "$(ProjectDir)/Binaries/Win64/UnrealEditor-Game.dll", "Type": "DynamicLibrary" },
					{ "Path": "$(ProjectDir)/Binaries/Win64/UnrealEditor-Game.pdb", "Type": "SymbolFile" }
				]
			}
			""");
		Sandbox.Write(Path.Combine(Binaries, "UnrealEditor-Game.pdb"), "symbols");
		string Dll = Sandbox.Write(Path.Combine(Binaries, "UnrealEditor-Game.dll"), "built before the check");
		File.SetLastWriteTimeUtc(Dll, DateTime.UtcNow.AddHours(-1));
		return Dll;
	}

	[TestMethod]
	public async Task ARebuildThatFailsElsewhereButRelinkedWhatTheMutantLinkedClearsTheJournal()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		string Other = Path.Combine(Box.ProjectDirectory, "Source", "Other", "Private", "Other.cpp") + "(1): error C1083: Cannot open include file: 'Missing.h'";
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 1], buildOutput: Build =>
			{
				// UBT goes on after an error: the rebuild relinks the patched module, and fails on another agent's half-edited file.
				File.WriteAllText(Dll, Build == 0 ? "mutant" : "relinked without the mutation");
				return Build == 0 ? CompileLines : [.. CompileLines, Other];
			}),
		};

		int Exit = await new MutateCommand(Services(Runner, realDelay: true)).RunAsync(Box.Context(), ["-name=B1", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains("no binary the mutant build wrote is left as it wrote it", Box.Logger.Text);
		Assert.DoesNotContain("BINARIES PENDING", Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "B1")));
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task ABinaryTheMutantBuildWroteHoldsTestsBackUntilSomethingRewritesIt()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0, 1], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Dll, "mutant");
				}
				return CompileLines;
			}),
		};

		int Exit = await new MutateCommand(Services(Runner, realDelay: true)).RunAsync(Box.Context(), ["-name=B2", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.SurvivedExitCode, Exit, Box.Logger.Text);
		Assert.Contains("BINARIES PENDING: the files are back (the tree holds no mutation) and marked so the next build recompiles them, but the rebuild after failed"
			+ $" (its errors are above: another change may have broken the tree), so {Path.GetFullPath(Dll)} may still hold the mutation", Box.Logger.Text);
		MutationRecord Record = StateFiles.ReadJson<MutationRecord>(JournalFile(Box, "B2"))!;
		CollectionAssert.AreEqual(new[] { Path.GetFullPath(Dll) }, Record.Binaries!.Select(Binary => Binary.Path).ToArray(), "the PDB and the receipt aren't code");

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		Assert.Contains(Path.GetFullPath(Dll) + " may still hold its patch", Box.Logger.Text);
		// A later build that relinks the module, even one that fails on another file, takes the mutant out of the binaries.
		File.WriteAllText(Dll, "relinked by a later build");
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("Cleared the mutation journal 'B2'", Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "B2")));
	}

	[TestMethod]
	public async Task AMutantBuildThatFailsElsewhereAndLinksNothingLeavesNoJournal()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		string Other = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Other.cpp") + "(1): error C1083: Cannot open include file: 'Missing.h'";
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [1], buildOutput: _ => [CompileLines[0], Other]) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=B3", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Exit, Box.Logger.Text);
		Assert.Contains("no binary the mutant build wrote is left as it wrote it (it wrote none", Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "B3")));
		Assert.AreEqual("built before the check", File.ReadAllText(Dll));
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task APatchThatAddsAFileLeavesWhichBinariesItWroteUnknown()
	{
		// A new file may be a new module, whose DLL the receipt doesn't list yet.
		string AddPatch = GitPatch + "diff --git a/Source/Game/Private/New.cpp b/Source/Game/Private/New.cpp\nnew file mode 100644\n--- /dev/null\n+++ b/Source/Game/Private/New.cpp\n@@ -0,0 +1 @@\n+int New;\n";
		(Sandbox Box, string Patch) = WithPatch(AddPatch);
		using Sandbox _ = Box;
		WriteReceipt(Box);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0, 1], buildOutput: _ => ["[1/3] Compile [x64] Foo.cpp", "[2/3] Compile [x64] New.cpp"]) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=B4", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains("uak doesn't know which", Box.Logger.Text);
		Assert.IsNull(StateFiles.ReadJson<MutationRecord>(JournalFile(Box, "B4"))!.Binaries);
		Assert.IsFalse(File.Exists(Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "New.cpp")));
	}

	[TestMethod]
	public void TheTargetsBinariesAreTheCodeFilesInTheFoldersOfItsReceiptsBuildProducts()
	{
		using Sandbox Box = new();
		string Engine = Box.Layout.EngineDirectory;
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Binaries", "Win64", "GameEditor.target"),
			"""
			{ "BuildProducts": [
				{ "Path": "$(EngineDir)/Plugins/Tools/Binaries/Win64/UnrealEditor-Tools.dll", "Type": "DynamicLibrary" },
				{ "Path": "$(ProjectDir)/Plugins/P/Binaries/Win64/Launcher", "Type": "Executable" },
				{ "Path": "$(ProjectDir)/Plugins/P/Binaries/Win64/UnrealEditor-P.pdb", "Type": "SymbolFile" }
			] }
			""");
		string EngineDll = Sandbox.Write(Path.Combine(Engine, "Plugins", "Tools", "Binaries", "Win64", "UnrealEditor-Tools.dll"), "a");
		string Launcher = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "P", "Binaries", "Win64", "Launcher"), "b");
		string NewModule = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "P", "Binaries", "Win64", "UnrealEditor-P2.dll"), "c");
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "P", "Binaries", "Win64", "UnrealEditor-P.pdb"), "d");
		string ProjectDll = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Binaries", "Win64", "UnrealEditor-Game.dll"), "e");

		TargetBinaries Binaries = TargetBinaries.Find(Box.ProjectDirectory, Engine, "GameEditor", UnrealPlatform.Win64, "Development")!;
		List<MutationBinary> Before = Binaries.Snapshot()!;

		CollectionAssert.AreEquivalent(new[] { EngineDll, Launcher, NewModule, ProjectDll }.Select(Path.GetFullPath).ToArray(), Before.Select(Binary => Binary.Path).ToArray());
		File.WriteAllText(Launcher, "rebuilt");
		string Added = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Binaries", "Win64", "UnrealEditor-Game-0001.dll"), "f");
		CollectionAssert.AreEquivalent(new[] { Launcher, Added }.Select(Path.GetFullPath).ToArray(),
			TargetBinaries.WrittenSince(Before, Binaries.Snapshot()!).Select(Binary => Binary.Path).ToArray());

		Assert.IsNull(TargetBinaries.Find(Box.ProjectDirectory, Engine, "GameEditor", UnrealPlatform.Win64, "DebugGame"), "no receipt: unknown");
		Assert.AreEqual(Path.Combine(Box.ProjectDirectory, "Binaries", "Win64", "GameEditor-Win64-DebugGame.target"),
			TargetBinaries.ReceiptPath(Box.ProjectDirectory, "GameEditor", UnrealPlatform.Win64, "DebugGame"));
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Binaries", "Win64", "GameEditor.target"), "{ \"BuildProducts\": [ { \"Path\": 3 } ] }");
		Assert.IsNull(TargetBinaries.Find(Box.ProjectDirectory, Engine, "GameEditor", UnrealPlatform.Win64, "Development"), "a receipt uak can't read: unknown");
	}

	// ---- 0.4.1: what a rebuild must show, and what the stamp must pass (each RX test turns a review experiment around) ----

	/// <summary>What UBT prints for a build with nothing to do.</summary>
	static readonly string[] UpToDate = ["Target is up to date", "Result: Succeeded"];

	/// <summary>A build whose first run writes the mutant to <paramref name="dll"/>, with <paramref name="later"/> as the output of each later one.</summary>
	static Func<int, string[]> MutantThen(string dll, string[] later) => Build =>
	{
		if (Build == 0)
		{
			File.WriteAllText(dll, "mutant");
			return CompileLines;
		}
		return later;
	};

	static MutationRecord Record(Sandbox box, string name) => StateFiles.ReadJson<MutationRecord>(JournalFile(box, name))!;

	static string Binary(Sandbox box, string name) => Path.Combine(box.ProjectDirectory, "Binaries", "Win64", name);

	[TestMethod]
	public async Task RX1_ARebuildThatIsUpToDateProvesNothingSoTheJournalStaysUntilABuildCompiles()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: MutantThen(Dll, UpToDate)) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X1", "-patch=" + Patch, "-filter=Project.Gameplay", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains($"BINARIES PENDING: the files are back (the tree holds no mutation) and marked so the next build recompiles them, but the rebuild after shows no compile of {Box.SourceFile},"
			+ $" so {Path.GetFullPath(Dll)} may still hold the mutation", Box.Logger.Text);
		Assert.Contains("files put back; the binaries may hold the mutation until a build recompiles them", Box.Logger.Text);
		Assert.AreEqual("mutant", File.ReadAllText(Dll));
		CollectionAssert.AreEqual(new[] { Box.SourceFile }, Record(Box, "X1").AwaitingRecompile);
		using (JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile)))
		{
			Assert.IsTrue(Result.RootElement.GetProperty("BinariesPending").GetBoolean(), "a structured field: the verdict's exit code doesn't say it");
			Assert.AreEqual("caught", Result.RootElement.GetProperty("Verdict").GetString());
			Assert.AreEqual(0, Result.RootElement.GetProperty("ExitCode").GetInt32());
		}

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), "tests must not run the mutant");
		Assert.Contains($"a `uak build` of GameEditor (Win64 Development) that shows a compile of {Box.SourceFile} and succeeds clears this", Box.Logger.Text);
		FakeProcessRunner Compiles = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
		{
			File.WriteAllText(Dll, "relinked without the mutation");
			return CompileLines;
		}) };
		Assert.AreEqual(0, await new BuildCommand(Services(Compiles)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "X1")));
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task ARebuildThatShowsTheCompileClearsTheJournalAndSaysNothingIsPending()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		string ResultFile = Path.Combine(Box.Root, "result.json");
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: MutantThen(Dll, CompileLines)) };

		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X1b", "-patch=" + Patch, "-filter=Project.Gameplay", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.IsFalse(File.Exists(JournalFile(Box, "X1b")), Box.Logger.Text);
		Assert.Contains("files put back and rebuilt", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.IsFalse(Result.RootElement.GetProperty("BinariesPending").GetBoolean());
	}

	[TestMethod]
	public async Task ARestoreMarksTheFilesAgainAndForceSetsAsideAJournalNoBuildRecompiles()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: MutantThen(Dll, UpToDate)) };
		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X1c", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);
		DateTime FirstStamp = Record(Box, "X1c").RecompileStamp!.Value;
		await Task.Delay(50);

		FakeProcessRunner Still = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => UpToDate) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Still)).RunAsync(Box.Context(), ["-restore", "-name=X1c"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains("the rebuild shows no compile of " + Box.SourceFile, Box.Logger.Text);
		Assert.Contains("-force (with -name=) sets the journal aside", Box.Logger.Text);
		Assert.IsGreaterThan(FirstStamp, Record(Box, "X1c").RecompileStamp!.Value, "a new stamp: the rebuild gets another chance");

		Assert.AreEqual(0, await new MutateCommand(Services(Still)).RunAsync(Box.Context(), ["-restore", "-name=X1c", "-force"], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "X1c")));
		Assert.Contains("-force sets the journal aside all the same", Box.Logger.Text);
		Assert.HasCount(1, Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "X1c-*"));
	}

	[TestMethod]
	public async Task ForceClearsNoJournalWhenTheRebuildFails()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: MutantThen(Dll, UpToDate)) };
		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X1d", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		FakeProcessRunner Fails = new() { Behaviour = Plays("test-passed.log", 0, [1]) };

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Fails)).RunAsync(Box.Context(), ["-restore", "-name=X1d", "-force"], CancellationToken.None), Box.Logger.Text);
		Assert.IsTrue(File.Exists(JournalFile(Box, "X1d")));
	}

	[TestMethod]
	public async Task RX2_AnUpToDateBuildNeverClearsAPendingJournal()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 1], buildOutput: MutantThen(Dll, CompileLines)) };
		Assert.AreEqual(MutateCommand.SurvivedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X2", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None));
		Assert.Contains("BINARIES PENDING", Box.Logger.Text);

		// That rebuild failed without an error its log names: any of its actions may have failed, so nothing it printed counts.
		CollectionAssert.AreEqual(new[] { Box.SourceFile }, Record(Box, "X2").AwaitingRecompile);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => UpToDate) };
		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual("mutant", File.ReadAllText(Dll));
		Assert.IsTrue(File.Exists(JournalFile(Box, "X2")), "an up-to-date build, DLL untouched, must not clear the journal");
		Assert.Contains("The mutation journal 'X2' stays: this build shows no compile of", Box.Logger.Text);
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
	}

	[TestMethod]
	public async Task ARebuildThatFailsElsewhereCountsForWhatItCompiledAndABinaryNoBuildRelinksHoldsNothingUp()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		string Unrelated = Sandbox.Write(Binary(Box, "UnrealEditor-Unrelated.dll"), "built before the check");
		string OtherError = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Other.cpp") + "(1): error C1083: Cannot open include file: 'Missing.h'";
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0, 1], buildOutput: Build =>
			{
				if (Build == 0)
				{
					// Another agent's change got linked too: nothing about the mutation will ever relink it.
					File.WriteAllText(Dll, "mutant");
					File.WriteAllText(Unrelated, "another change");
					return [.. CompileLines, "[3/3] Link [x64] UnrealEditor-Unrelated.dll"];
				}
				return ["[1/2] Compile [x64] Foo.cpp", "[2/2] Compile [x64] Other.cpp", OtherError];
			}),
		};

		Assert.AreEqual(MutateCommand.SurvivedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X2b", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("BINARIES PENDING", Box.Logger.Text);
		Assert.IsEmpty(Record(Box, "X2b").AwaitingRecompile!, "Foo.cpp compiled, though the build failed on Other.cpp");
		Assert.HasCount(2, Record(Box, "X2b").Binaries!);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
		{
			File.WriteAllText(Dll, "relinked without the mutation");
			return ["[1/1] Link [x64] UnrealEditor-Game.dll"];
		}) };
		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "X2b")), "Unrelated.dll is never relinked: waiting for it would keep the journal for good");
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task ACompileThatFailedIsNoEvidenceEvenWhenItsErrorRepeatsAnothers()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		string Broken = Box.HeaderFile + "(1): error C2143: syntax error";
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0, 1], buildOutput: MutantThen(Dll, ["[1/2] Compile [x64] Bar.cpp", Broken, "[2/2] Compile [x64] Foo.cpp", Broken])),
		};

		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X2c", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		CollectionAssert.AreEqual(new[] { Box.SourceFile }, Record(Box, "X2c").AwaitingRecompile, "the failed compile left the mutant's object");
	}

	[TestMethod]
	public void AFailedActionIsKnownByAnyErrorUnderItAndABuildSaysWhatItLinked()
	{
		string Broken = "C:\\Game\\Foo.h(1): error C2143: syntax error";
		BuildLogSummary Failed = BuildLogParser.Parse(["[1/5] Compile [x64] Bar.cpp", Broken, "[2/5] Compile [x64] Foo.cpp", Broken, "[3/5] Compile [x64] Clean.cpp",
			"[4/5] Link [x64] UnrealEditor-Game.lib", "[5/5] Link [x64] UnrealEditor-Game.dll [Cache]"]);

		Assert.HasCount(1, Failed.Errors);
		BuildOutcome Outcome = BuildOutcome.From(Failed, 1);
		CollectionAssert.AreEqual(new[] { "Bar.cpp", "Foo.cpp", "Clean.cpp" }, Outcome.Compiled.ToArray());
		CollectionAssert.AreEqual(new[] { "Clean.cpp" }, Outcome.CompiledClean.ToArray());
		CollectionAssert.AreEqual(new[] { "UnrealEditor-Game.lib", "UnrealEditor-Game.dll" }, Outcome.Linked.ToArray());

		BuildLogSummary Silent = BuildLogParser.Parse(["[1/1] Compile [x64] Foo.cpp"]);
		Assert.IsEmpty(BuildOutcome.From(Silent, 1).CompiledClean, "it failed, and its log doesn't say what did");
		CollectionAssert.AreEqual(new[] { "Foo.cpp" }, BuildOutcome.From(Silent, 0).CompiledClean.ToArray());

		BuildOutcome Others = BuildOutcome.From(BuildLogParser.Parse(["[1/6] Link-LTO [x64] libA.so", "[2/6] Relink [x64] libB.so", "[3/6] Link-PGO [x64] libC.so",
			"[4/6] LinkStub [arm64] D.dylib", "[5/6] Archive [x64] libE.a", "[6/6] WriteMetadata GameEditor.target"]), 0);
		CollectionAssert.AreEqual(new[] { "libA.so", "libB.so", "libC.so" }, Others.Linked.ToArray());
	}

	const string SyntaxError = "C:\\Game\\Foo.h(1): error C2143: syntax error";

	[TestMethod]
	[DataRow("c1xx: fatal error C1083: Cannot open source file: 'Bad.cpp': No such file or directory")]
	[DataRow("cl : Command line error D8021 : invalid numeric argument '/Wextra'")]
	[DataRow("INTERNAL COMPILER ERROR in 'C:\\VC\\c2.dll'")]
	[DataRow("error: unable to open output file 'Bad.o'")]
	[DataRow("Stack dump:")]
	[DataRow("Segmentation fault")]
	public void AFailureNoPatternParsesTakesEveryCompileOfAFailedBuildFromTheEvidence(string line)
	{
		BuildLogSummary Failed = BuildLogParser.Parse(["[1/3] Compile [x64] Clean.cpp", "[2/3] Compile [x64] Bad.cpp", line, "[3/3] Compile [x64] Bar.cpp", SyntaxError,
			"Result: Failed (OtherCompilationError)"]);

		Assert.HasCount(1, Failed.Errors);
		Assert.IsFalse(Failed.EveryFailureAttributed);
		Assert.IsEmpty(BuildOutcome.From(Failed, 6).CompiledClean, "Bad.cpp failed, and no error says so: any compile here may have");

		// A build that succeeded shows the words meant nothing.
		BuildLogSummary Fine = BuildLogParser.Parse(["[1/2] Compile [x64] Clean.cpp", "[2/2] Compile [x64] Bad.cpp", line, "Result: Succeeded"]);
		Assert.HasCount(2, BuildOutcome.From(Fine, 0).CompiledClean);
	}

	[TestMethod]
	[DataRow("ERROR:  Compile [x64] Bad.cpp: Exited with error code -1073741819 (Access violation). The build will fail.")]
	[DataRow(" Compile [x64] Bad.cpp: Exited with error code -1073741819 (Access violation). The build will fail.")]
	public void ACompilerThatCrashedWithNoOutputTakesEveryCompileFromTheEvidence(string line)
	{
		BuildLogSummary Failed = BuildLogParser.Parse(["[1/3] Compile [x64] Clean.cpp", "[2/3] Compile [x64] Bad.cpp", "[3/3] Compile [x64] Bar.cpp", line, SyntaxError]);

		Assert.IsEmpty(BuildOutcome.From(Failed, 6).CompiledClean, "UBT's line can reach the log ahead of its action's: it tells no action");
	}

	[TestMethod]
	[DataRow("Compile [x64] Good.cpp: Exited with error code 9001. This action will retry (More info in uba trace)")]
	[DataRow("Compile [x64] Good.cpp: Exited with error code 9001. This action will retry without UBA")]
	[DataRow("Compile [x64] Good.cpp [RemoteExecutor: host]: Exited with error code 5 (Access denied). This action will retry locally")]
	[DataRow("Compile [x64] Good.cpp [RemoteExecutor: host]: Exited with error code 5 with no output. This action will retry locally")]
	public void AnActionThatWillRetryLeavesAFailedBuildsEvidence(string line)
	{
		BuildLogSummary Failed = BuildLogParser.Parse(["[1/2] Compile [x64] Good.cpp", line, "[2/2] Compile [x64] Bad.cpp", SyntaxError, "Result: Failed (OtherCompilationError)"]);

		Assert.IsFalse(Failed.ActionExitedWithError);
		Assert.IsEmpty(Failed.UnparsedFailures);
		Assert.IsTrue(Failed.EveryFailureAttributed);
		CollectionAssert.AreEqual(new[] { "Good.cpp" }, BuildOutcome.From(Failed, 6).CompiledClean.ToArray());
	}

	[TestMethod]
	public void AnUnparsedLineUnderAFailureAlreadyKnownOrANoteLeavesTheRestAsEvidence()
	{
		BuildLogSummary Failed = BuildLogParser.Parse(["Failed to read a cache: that's before any action",
			"[1/3] Compile [x64] Clean.cpp", "C:\\Game\\Foo.h(3): note: see declaration of 'Error'",
			"[2/3] Compile [x64] Bad.cpp", "c1xx: fatal error C1083: Cannot open include file", SyntaxError,
			"[3/3] Compile [x64] Other.cpp", SyntaxError, "cl : Command line error D8021 : invalid numeric argument",
			"Result: Failed (OtherCompilationError)"]);

		Assert.IsTrue(Failed.EveryFailureAttributed);
		CollectionAssert.AreEqual(new[] { "Clean.cpp" }, BuildOutcome.From(Failed, 6).CompiledClean.ToArray());

		BuildLogSummary Fixture = BuildLogParser.Parse(Fixtures.Lines("build-compile-errors.log"));
		Assert.IsTrue(Fixture.EveryFailureAttributed, string.Join(", ", Fixture.UnparsedFailures));
		CollectionAssert.Contains(BuildOutcome.From(Fixture, 6).CompiledClean.ToArray(), "MyComponent.cpp");
	}

	[TestMethod]
	public async Task ARebuildThatFailedWithAnUnparsedErrorShowsNoRecompile()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		string[] Rebuild = ["[1/2] Compile [x64] Foo.cpp", "c1xx: fatal error C1083: Cannot open compiler generated file", "[2/2] Compile [x64] Bar.cpp", SyntaxError];
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 1], buildOutput: MutantThen(Dll, Rebuild)) };

		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=N3", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		CollectionAssert.AreEqual(new[] { Box.SourceFile }, Record(Box, "N3").AwaitingRecompile, "the failed compile left the mutant's object");
	}

	[TestMethod]
	public async Task AMutantBuildKilledWithNoResultLineLeavesWhatItDidUnknown()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		// UBT died: no Result: line and no error, and Foo.cpp's compile may have written its object with no line to show for it.
		string[] Killed = ["[1/2] Compile [x64] Bar.cpp"];
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [1, 1], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Dll, "mutant");
				}
				return Killed;
			}),
		};

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=N7", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		MutationRecord Left = Record(Box, "N7");
		Assert.IsNull(Left.Binaries, "the log can't say what it wrote");
		CollectionAssert.AreEqual(new[] { Box.SourceFile }, Left.AwaitingRecompile, "it may have compiled anything");
	}

	[TestMethod]
	public void OnlyABuildThatFailedWithNeitherAResultNorAnErrorStoppedPartWay()
	{
		string[] Compile = ["[1/1] Compile [x64] Foo.cpp"];
		Assert.IsTrue(BuildOutcome.From(BuildLogParser.Parse(Compile), 1).StoppedPartWay);
		Assert.IsFalse(BuildOutcome.From(BuildLogParser.Parse(Compile), 0).StoppedPartWay);
		Assert.IsFalse(BuildOutcome.From(BuildLogParser.Parse([.. Compile, "Result: Failed (OtherCompilationError)"]), 6).StoppedPartWay);
		Assert.IsFalse(BuildOutcome.From(BuildLogParser.Parse([.. Compile, SyntaxError]), 1).StoppedPartWay);
	}

	[TestMethod]
	[DataRow("Foo.m", true)]
	[DataRow("Foo.mm", true)]
	[DataRow("Foo.ispc", true)]
	[DataRow("Foo.ipp", false)]
	[DataRow("Foo.tpp", false)]
	[DataRow("Foo.def", false)]
	[DataRow("Foo.isph", false)]
	public void AFileACompileReachesNeedsACompile(string name, bool needsItsOwn)
	{
		string File_ = Path.GetFullPath(name);
		List<DiffFile> Files = UnifiedDiff.Parse($"--- a/{name}\n+++ b/{name}\n@@ -1 +1 @@\n-a\n+b\n");
		BuildOutcome None = BuildOutcome.From(BuildLogParser.Parse(UpToDate), 0);
		BuildOutcome Other = BuildOutcome.From(BuildLogParser.Parse(["[1/1] Compile [x64] Bar.cpp"]), 0);
		BuildOutcome Own = BuildOutcome.From(BuildLogParser.Parse([$"[1/1] Compile [x64] {name}"]), 0);

		CollectionAssert.AreEqual(new[] { File_ }, MutateCommand.FindUncompiled(None, Files, [File_]));
		Assert.HasCount(needsItsOwn ? 1 : 0, MutateCommand.FindUncompiled(Other, Files, [File_]));
		Assert.IsEmpty(MutateCommand.FindUncompiled(Own, Files, [File_]));
	}

	[TestMethod]
	[DataRow("Foo.asm")]
	[DataRow("Foo.S")]
	[DataRow("Foo.ini")]
	public void AFileNoCompileReachesNeedsNone(string name)
	{
		string File_ = Path.GetFullPath(name);
		List<DiffFile> Files = UnifiedDiff.Parse($"--- a/{name}\n+++ b/{name}\n@@ -1 +1 @@\n-a\n+b\n");

		Assert.IsEmpty(MutateCommand.FindUncompiled(BuildOutcome.From(BuildLogParser.Parse(UpToDate), 0), Files, [File_]));
	}

	[TestMethod]
	public void WhatARebuildMustShowDependsOnWhatKindOfFileWasPatched()
	{
		using Sandbox Box = new();
		string Config = Path.Combine(Box.ProjectDirectory, "Config", "DefaultGame.ini");
		BuildOutcome None = BuildOutcome.From(BuildLogParser.Parse(UpToDate), 0);
		BuildOutcome Other = BuildOutcome.From(BuildLogParser.Parse(["[1/1] Compile [x64] Bar.cpp"]), 0);
		BuildOutcome Own = BuildOutcome.From(BuildLogParser.Parse(["[1/1] Compile [x64] Foo.cpp"]), 0);
		BuildOutcome Unity = BuildOutcome.From(BuildLogParser.Parse(["[1/1] Compile [x64] Module.Game.2.cpp"]), 0);
		BuildOutcome LinkOnly = BuildOutcome.From(BuildLogParser.Parse(["[1/1] Link [x64] UnrealEditor-Game.dll"]), 0);
		MutationFile Source = new() { Path = Box.SourceFile, Existed = true };
		MutationFile Header = new() { Path = Box.HeaderFile, Existed = true };
		MutationFile Deleted = new() { Path = Box.SourceFile, Existed = true, PlannedDeleted = true };
		MutationFile Created = new() { Path = Path.Combine(Path.GetDirectoryName(Box.SourceFile)!, "New.cpp") };

		Assert.IsTrue(MutateCommand.ShowsRecompile(None, new MutationFile { Path = Config, Existed = true }), "no compile is due for a file that isn't C++");
		Assert.IsFalse(MutateCommand.ShowsRecompile(None, Source));
		Assert.IsFalse(MutateCommand.ShowsRecompile(Other, Source));
		Assert.IsTrue(MutateCommand.ShowsRecompile(Own, Source));
		Assert.IsTrue(MutateCommand.ShowsRecompile(Unity, Source));
		Assert.IsFalse(MutateCommand.ShowsRecompile(None, Header));
		Assert.IsTrue(MutateCommand.ShowsRecompile(Other, Header), "a header shows only in what includes it");
		Assert.IsFalse(MutateCommand.ShowsRecompile(LinkOnly, Header));
		Assert.IsFalse(MutateCommand.ShowsRecompile(None, Deleted));
		Assert.IsTrue(MutateCommand.ShowsRecompile(Other, Deleted));
		Assert.IsFalse(MutateCommand.ShowsRecompile(None, Created));
		Assert.IsTrue(MutateCommand.ShowsRecompile(LinkOnly, Created), "the file is gone: its object leaves the link");
		Assert.IsFalse(MutateCommand.ShowsRecompile(BuildOutcome.From(BuildLogParser.Parse(LinkOnly.Linked.Select(Item => "[1/1] Link [x64] " + Item)), 1), Created), "that link may have failed");
	}

	const string HeaderPatch = "--- a/Source/Game/Public/Foo.h\n+++ b/Source/Game/Public/Foo.h\n@@ -1 +1 @@\n-#pragma once\n+#pragma   once\n";

	[TestMethod]
	public async Task APatchedHeaderIsShownByACompileOfWhatIncludesIt()
	{
		(Sandbox Box, string Patch) = WithPatch(HeaderPatch);
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		string[] Includer = ["[1/2] Compile [x64] Bar.cpp", "[2/2] Link [x64] UnrealEditor-Game.dll"];
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: Build =>
			{
				File.WriteAllText(Dll, Build == 0 ? "mutant" : "relinked");
				return Includer;
			}),
		};

		Assert.AreEqual(MutateCommand.CaughtExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=H1", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);

		Assert.IsFalse(File.Exists(JournalFile(Box, "H1")), "no compile names a header");
		Assert.DoesNotContain("BINARIES PENDING", Box.Logger.Text);
	}

	[TestMethod]
	[DataRow(HeaderPatch)]
	[DataRow(GitPatch)]
	public async Task AFileTheMutantBuildNeverCompiledAsksNoRebuildToCompileIt(string patch)
	{
		// Not in the editor target, or included by nothing: no build will ever compile it, so a journal waiting for one could never clear.
		(Sandbox Box, string Patch) = WithPatch(patch);
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 0], buildOutput: _ => UpToDate) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=H2", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.InconclusiveExitCode, Exit, Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "H2")), Box.Logger.Text);
		Assert.DoesNotContain("BINARIES PENDING", Box.Logger.Text);
		Assert.AreEqual("built before the check", File.ReadAllText(Dll));
	}

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public async Task AFileThePatchCreatedIsGoneOnceARebuildLinksWithoutIt(bool relinks)
	{
		const string AddPatch = "--- /dev/null\n+++ b/Source/Game/Private/New.cpp\n@@ -0,0 +1 @@\n+int New;\n";
		(Sandbox Box, string Patch) = WithPatch(AddPatch);
		using Sandbox _ = Box;
		string[] Compiled = ["[1/2] Compile [x64] New.cpp", "[2/2] Link [x64] UnrealEditor-Game.dll"];
		string[] Rebuilt = relinks ? ["[1/1] Link [x64] UnrealEditor-Game.dll"] : UpToDate;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: Build => Build == 0 ? Compiled : Rebuilt) };

		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=H3", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(!relinks, File.Exists(JournalFile(Box, "H3")), Box.Logger.Text);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public async Task AKilledRunsRestoreAsksForACompileOnlyWhenAMutantMayHaveBeenBuilt(bool buildStarted)
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "K", 0, null);
		if (!buildStarted)
		{
			// As the run records it before its build; it then clears that once its build may have started.
			MutationRecord Saved = Record(Box, "K");
			Saved.AwaitingRecompile = [];
			StateFiles.WriteJson(JournalFile(Box, "K"), Saved);
		}
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => UpToDate) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None);

		Assert.AreEqual(buildStarted ? MutateCommand.RestoreFailedExitCode : 0, Exit, Box.Logger.Text);
		Assert.AreEqual(buildStarted, File.Exists(JournalFile(Box, "K")), "no mutant was built: an up-to-date rebuild is all there is to see");
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public async Task ARunRecordsWhatItsMutantBuildCompiledBeforeItsTests()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		List<string>? DuringTest = null;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0], () => DuringTest = Record(Box, "K2").AwaitingRecompile) };

		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=K2", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		CollectionAssert.AreEqual(new[] { Box.SourceFile }, DuringTest, "a run killed in its tests leaves it");
	}

	[TestMethod]
	public async Task RX3_TheStampPassesAFileServersClock()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Object = ObjectFile(Box);
		DateTime Server = DateTime.UtcNow.AddSeconds(30);
		List<TimeSpan> Delays = [];
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0], buildOutput: Build =>
			{
				if (Build == 0)
				{
					// A file server whose clock is 30 s ahead of this machine stamps what the build writes.
					Sandbox.Write(Object, "mutant code");
					File.SetLastWriteTimeUtc(Object, Server);
				}
				return CompileLines;
			}),
		};

		int Exit = await new MutateCommand(Services(Runner, probe: _ => Server, delays: Delays)).RunAsync(Box.Context(), ["-name=X3", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.SourceFile) - File.GetLastWriteTimeUtc(Object), "or UBT keeps the object");
		Assert.AreEqual(TimeSpan.FromSeconds(2), Delays.Single(), "the server's clock passes the stamp as fast as this one: only the margin is waited out");
		Assert.IsFalse(File.Exists(JournalFile(Box, "X3")));
	}

	[TestMethod]
	public async Task TheStampPassesTheNewestBinaryTheMutantBuildWroteWaitingItOutUpToALimit()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		foreach ((double Ahead, double Waited) in new[] { (30.0, 32.0), (3600.0, 60.0) })
		{
			DateTime Dated = DateTime.UtcNow.AddSeconds(Ahead);
			List<TimeSpan> Delays = [];
			string Name = $"X3b{Ahead}";
			FakeProcessRunner Runner = new()
			{
				Behaviour = Plays("test-failed.log", 255, [0, 0], buildOutput: Build =>
				{
					if (Build == 0)
					{
						File.WriteAllText(Dll, "mutant " + Name);
						File.SetLastWriteTimeUtc(Dll, Dated);
					}
					return Build == 0 ? CompileLines : UpToDate;
				}),
			};

			await new MutateCommand(Services(Runner, delays: Delays)).RunAsync(Box.Context(), ["-name=" + Name, "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

			Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, File.GetLastWriteTimeUtc(Box.SourceFile) - Dated);
			Assert.AreEqual(Waited, Delays.Single().TotalSeconds, 1.0, Box.Logger.Text);
			// Dated past the stamp, it would pass for one relinked since.
			Assert.AreEqual(Path.GetFullPath(Dll), MutationJournal.List(Box.Context()).Journals.Single().StillHeldBinaries()!.Single().Path);
			Assert.AreEqual(0, await new MutateCommand(Services(new FakeProcessRunner { Behaviour = Plays("test-passed.log", 0, [0]) })).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);
		}
		Assert.Contains("ahead of every clock", Box.Logger.Text);
	}

	[TestMethod]
	public async Task TheStampWaitsForRunningCompilesBeforeItTakesItsTime()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		bool Waited = false;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0]) };
		Func<UakContext, Task<bool>> Compiles = _ =>
		{
			// The files are back, not stamped yet: a compile that read the mutant may still write its object.
			Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
			Assert.IsNull(Record(Box, "P3").RecompileStamp);
			Waited = true;
			return Task.FromResult(false);
		};

		Assert.AreEqual(MutateCommand.CaughtExitCode, await new MutateCommand(Services(Runner, waitForCompiles: Compiles)).RunAsync(Box.Context(), ["-name=P3", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None), Box.Logger.Text);

		Assert.IsTrue(Waited);
		Assert.IsFalse(File.Exists(JournalFile(Box, "P3")), "a compile that outlives the wait is warned about, and stops nothing");
	}

	[TestMethod]
	public async Task RX4_APluginTheReceiptDoesNotListIsFoundByItsFolder()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		WriteReceipt(Box);
		string PluginDll = Path.Combine(Box.ProjectDirectory, "Plugins", "NewSincePlugin", "Binaries", "Win64", "UnrealEditor-NewSincePlugin.dll");
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 1], buildOutput: Build =>
			{
				if (Build == 0)
				{
					// Linked for the first time by the mutant build, in a folder that didn't exist before it, and no receipt names it.
					Sandbox.Write(PluginDll, "mutant");
					return ["[1/2] Compile [x64] Foo.cpp", "[2/2] Link [x64] UnrealEditor-NewSincePlugin.dll"];
				}
				return CompileLines;
			}),
		};

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X4", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains("BINARIES PENDING", Box.Logger.Text);
		Assert.Contains(Path.GetFullPath(PluginDll) + " may still hold the mutation", Box.Logger.Text);
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
	}

	[TestMethod]
	public async Task ALinkOutsideTheFoldersLookedAtLeavesWhichBinariesItWroteUnknown()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		// An engine plugin, say: it wrote where nothing was listed, so `Written` can't be the whole of what the build wrote.
		string[] Mutant = ["[1/3] Compile [x64] Foo.cpp", "[2/3] Link [x64] UnrealEditor-Game.dll", "[3/3] Link [x64] UnrealEditor-EngineTool.dll"];
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-failed.log", 255, [0, 1], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Dll, "mutant");
				}
				return Mutant;
			}),
		};

		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X4b", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.IsNull(Record(Box, "X4b").Binaries, Box.Logger.Text);
		Assert.Contains("the mutant build linked UnrealEditor-EngineTool.dll", Box.Logger.Text);
		Assert.Contains("uak doesn't know which", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AnImportLibraryLinkAsksForNoBinary()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		FakeProcessRunner Runner = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0, 1], buildOutput: Build =>
			{
				if (Build == 0)
				{
					File.WriteAllText(Dll, "mutant");
				}
				return ["[1/3] Compile [x64] Foo.cpp", "[2/3] Link [x64] UnrealEditor-Game.lib", "[3/3] Link [x64] UnrealEditor-Game.dll"];
			}),
		};

		await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X4c", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(Path.GetFullPath(Dll), Record(Box, "X4c").Binaries!.Single().Path, Box.Logger.Text);
	}

	[TestMethod]
	public async Task AMutantBuildThatStopsPartWayLeavesWhichBinariesItWroteUnknown()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		WriteReceipt(Box);
		using CancellationTokenSource Cancel = new();
		int Runs = 0;
		FakeProcessRunner Runner = new()
		{
			Behaviour = (_, _) =>
			{
				if (Runs++ == 0)
				{
					Cancel.Cancel();
					throw new OperationCanceledException(Cancel.Token);
				}
				return 1;
			},
		};

		Assert.AreEqual(UakExitCodes.Failure, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X4d", "-patch=" + Patch, "-filter=Project.Math"], Cancel.Token), Box.Logger.Text);

		MutationRecord Left = Record(Box, "X4d");
		Assert.IsNull(Left.Binaries, "no log says what it linked");
		CollectionAssert.AreEqual(new[] { Box.SourceFile }, Left.AwaitingRecompile, "it may have compiled anything");
	}

	[TestMethod]
	public void EveryPluginsBinariesFolderCountsAndTheListIsUnionedAfterTheBuild()
	{
		using Sandbox Box = new();
		string Engine = Box.Layout.EngineDirectory;
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Binaries", "Win64", "GameEditor.target"), """{ "BuildProducts": [ { "Path": "$(ProjectDir)/Binaries/Win64/UnrealEditor-Game.dll", "Type": "DynamicLibrary" } ] }""");
		string Nested = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Group", "Inner", "Binaries", "Win64", "UnrealEditor-Inner.dll"), "a");
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Group", "Inner", "Inner.uplugin"), "{}");
		string Stale = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "NoDescriptor", "Binaries", "Win64", "UnrealEditor-Stale.dll"), "b");
		string Other = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Group", "Inner", "Binaries", "Linux", "libUnrealEditor-Inner.so"), "c");
		string InPlugin = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "Group", "Inner", "Nested", "Binaries", "Win64", "UnrealEditor-NotAPlugin.dll"), "d");

		TargetBinaries First = TargetBinaries.Find(Box.ProjectDirectory, Engine, "GameEditor", UnrealPlatform.Win64, "Development")!;
		List<string> Found = [.. First.Snapshot()!.Select(Binary => Binary.Path)];

		CollectionAssert.Contains(Found, Path.GetFullPath(Nested));
		CollectionAssert.Contains(Found, Path.GetFullPath(Stale));
		CollectionAssert.DoesNotContain(Found, Path.GetFullPath(Other));
		CollectionAssert.DoesNotContain(Found, Path.GetFullPath(InPlugin), "UBT looks for no plugin below a plugin");

		string Later = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "New", "Binaries", "Win64", "UnrealEditor-New.dll"), "e");
		TargetBinaries Second = First.Union(TargetBinaries.Find(Box.ProjectDirectory, Engine, "GameEditor", UnrealPlatform.Win64, "Development"))!;
		Assert.HasCount(First.Folders.Count + 1, Second.Folders);
		CollectionAssert.Contains(Second.Snapshot()!.Select(Binary => Binary.Path).ToList(), Path.GetFullPath(Later));
		Assert.IsNull(First.Union(null), "a receipt lost since: unknown");
	}

	[TestMethod]
	public async Task RX5_ARestoreRecordsTheBuildSoTheBuildItsMessageNamesClearsTheJournal()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "Old", 0, null);
		Assert.IsNull(Record(Box, "Old").BuildTarget, "as 0.4.0 left it");

		FakeProcessRunner Fail = new() { Behaviour = Plays("test-passed.log", 0, [1]) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Fail)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None));
		Assert.Contains("refuse until a `uak build`", Box.Logger.Text);
		Assert.AreEqual("GameEditor", Record(Box, "Old").BuildTarget);
		Assert.AreEqual("Win64", Record(Box, "Old").BuildPlatform);
		Assert.AreEqual("Development", Record(Box, "Old").BuildConfiguration);

		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new BuildCommand(Services(Later)).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "Old")), "the build that message promised clears it");
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public async Task RX6_RestoreAcceptOfAnUnreadableJournalRefusesARebuildThatCompiledNothing()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "U", 0, null);
		string Folder = Path.GetDirectoryName(JournalFile(Box, "U"))!;
		File.WriteAllText(JournalFile(Box, "U"), "{ not json");
		// Put back by hand with a copy that keeps the copy's write time, as Copy-Item and Explorer do.
		string Copy = Directory.GetFiles(Folder, "*.orig").Single();
		File.Copy(Copy, Box.SourceFile, overwrite: true);

		FakeProcessRunner Refused = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Refused)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		Assert.Contains("Give each file you put back a new write time", Box.Logger.Text);

		FakeProcessRunner Fail = new() { Behaviour = Plays("test-passed.log", 0, [1]) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Fail)).RunAsync(Box.Context(), ["-restore", "-name=U", "-accept"], CancellationToken.None), Box.Logger.Text);
		Assert.IsTrue(Directory.Exists(Folder), "after a failed rebuild the unreadable folder stays");
		Assert.Contains("(U) stay", Box.Logger.Text);

		FakeProcessRunner Good = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => UpToDate) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Good)).RunAsync(Box.Context(), ["-restore", "-name=U", "-accept"], CancellationToken.None), Box.Logger.Text);
		Assert.IsTrue(Directory.Exists(Folder), "up to date: the source it can't name is older than the mutant's object");
		Assert.Contains("the rebuild compiled nothing, so the journal(s) uak can't read (U) stay", Box.Logger.Text);
		Assert.Contains("Give each file that run touched a new write time", Box.Logger.Text);

		FakeProcessRunner Compiles = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(0, await new MutateCommand(Services(Compiles)).RunAsync(Box.Context(), ["-restore", "-name=U", "-accept"], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(Directory.Exists(Folder));
	}

	[TestMethod]
	public async Task TheFilesAnUnreadableJournalStillNamesAreStampedBeforeItsRebuild()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "V", 0, null);
		// Parses, and names its files, but names another run: uak can't trust it, and can still stamp them.
		File.WriteAllText(JournalFile(Box, "V"), File.ReadAllText(JournalFile(Box, "V")).Replace("\"V\"", "\"Other\"", StringComparison.Ordinal));
		File.WriteAllText(Box.SourceFile, Original);
		string Object = Sandbox.Write(ObjectFile(Box), "mutant code");
		File.SetLastWriteTimeUtc(Box.SourceFile, DateTime.UtcNow.AddHours(-1));
		DateTime? SourceAtRebuild = null;
		FakeProcessRunner Later = new()
		{
			Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ =>
			{
				SourceAtRebuild = File.GetLastWriteTimeUtc(Box.SourceFile);
				return CompileLines;
			}),
		};

		Assert.AreEqual(0, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-restore", "-name=V", "-accept"], CancellationToken.None), Box.Logger.Text);

		Assert.IsGreaterThan(MutationJournal.UbtTimestampSlop, SourceAtRebuild!.Value - File.GetLastWriteTimeUtc(Object));
		Assert.HasCount(1, Directory.GetDirectories(MutationJournal.GetDiscardedRoot(Box.Context()), "V-*"));
	}

	[TestMethod]
	public async Task APartialOrMisnamedListOfNoSourcesLeavesItsSourcesUnknown()
	{
		using Sandbox Box = new();
		string Config = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Config", "DefaultGame.ini"), "a=1\n");
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "L", 0, null);
		string Folder = Path.GetDirectoryName(JournalFile(Box, "L"))!;
		string Written = File.ReadAllText(JournalFile(Box, "L")).Replace(JsonSerializer.Serialize(Box.SourceFile), JsonSerializer.Serialize(Config), StringComparison.Ordinal);
		// Lists only a config file, and an entry with no full path, which may have been anything: List() can't read it.
		JsonObject Partial = JsonNode.Parse(Written)!.AsObject();
		Partial["Files"]!.AsArray().Add(new JsonObject { ["Path"] = "Lost.cpp" });
		File.WriteAllText(JournalFile(Box, "L"), Partial.ToJsonString());
		File.WriteAllText(Box.SourceFile, Original);

		FakeProcessRunner Good = new() { Behaviour = Plays("test-passed.log", 0, [0], buildOutput: _ => UpToDate) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Good)).RunAsync(Box.Context(), ["-restore", "-name=L", "-accept"], CancellationToken.None), Box.Logger.Text);
		Assert.IsTrue(Directory.Exists(Folder), "up to date, and the list leaves a file out: that may be a source");
		Assert.Contains("the rebuild compiled nothing", Box.Logger.Text);

		// One that names another run.
		File.WriteAllText(JournalFile(Box, "L"), Written.Replace("\"L\"", "\"Other\"", StringComparison.Ordinal));
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Good)).RunAsync(Box.Context(), ["-restore", "-name=L", "-accept"], CancellationToken.None), Box.Logger.Text);
		Assert.IsTrue(Directory.Exists(Folder), "it says nothing of this folder's run");

		// Its own whole list, of no source, that uak can't otherwise read: nothing is due for a compile.
		JsonObject Whole = JsonNode.Parse(Written)!.AsObject();
		Whole["Phase"] = "Unheard of";
		File.WriteAllText(JournalFile(Box, "L"), Whole.ToJsonString());
		Assert.AreEqual(0, await new MutateCommand(Services(Good)).RunAsync(Box.Context(), ["-restore", "-name=L", "-accept"], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(Directory.Exists(Folder));

		string Other = Path.Combine(Box.Root, "NoRecord");
		Directory.CreateDirectory(Other);
		Assert.IsNull(MutationFolders.ReadWholeList(Other));
	}

	[TestMethod]
	public async Task RX7_ARecordThatNamesAnotherRunNeverPassesTheShelveGuard()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "V", 0, null);
		string Json = File.ReadAllText(JournalFile(Box, "V"));
		// Parses, phase Restored, but names another run: List() calls it unreadable, so ListLeft() must not trust its phase.
		string Restored = Json.Replace("\"Applied\"", "\"Restored\"", StringComparison.Ordinal);
		File.WriteAllText(JournalFile(Box, "V"), Restored.Replace("\"V\"", "\"Other\"", StringComparison.Ordinal));

		StringAssert.Contains(MutationFolders.DescribeLeft(new DirectoryInfo(Box.StateDirectory), "shelve"), "uak mutate journal is left");
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Later)).RunAsync(Box.Context(), ["-status"], CancellationToken.None), Box.Logger.Text);

		// Nor one whose paths aren't all whole; as uak writes it, its files are back.
		File.WriteAllText(JournalFile(Box, "V"), Restored.Replace(JsonSerializer.Serialize(Box.SourceFile), "\"Foo.cpp\"", StringComparison.Ordinal));
		Assert.IsNotNull(MutationFolders.DescribeLeft(new DirectoryInfo(Box.StateDirectory), "shelve"));
		File.WriteAllText(JournalFile(Box, "V"), Restored);
		Assert.IsNull(MutationFolders.DescribeLeft(new DirectoryInfo(Box.StateDirectory), "shelve"));
	}

	[TestMethod]
	public async Task RX8_AWriteTimeChangeAloneIsNoRelink()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		string Dll = WriteReceipt(Box);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 1], buildOutput: MutantThen(Dll, CompileLines)) };
		Assert.AreEqual(MutateCommand.SurvivedExitCode, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X8", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None));

		// Same bytes, a time before the stamp: nothing relinked it.
		File.SetLastWriteTimeUtc(Dll, DateTime.UtcNow.AddDays(-1));
		FakeProcessRunner Later = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		Assert.AreEqual(2, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual("mutant", File.ReadAllText(Dll));
		Assert.IsTrue(File.Exists(JournalFile(Box, "X8")));

		// A time past the stamp is a link after the files were back.
		File.SetLastWriteTimeUtc(Dll, Record(Box, "X8").RecompileStamp!.Value.AddSeconds(1));
		Assert.AreEqual(0, await new TestCommand(Services(Later)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);
		Assert.IsFalse(File.Exists(JournalFile(Box, "X8")));
	}

	[TestMethod]
	[DataRow("Source/Game/Game.Build.cs")]
	[DataRow("Source/Extra.Target.cs")]
	[DataRow("Plugins/Tool/Tool.uplugin")]
	[DataRow("Game.uproject")]
	public async Task RX9_APatchToWhatTheBuildIsMadeOfLeavesWhichBinariesItWroteUnknown(string file)
	{
		string BuildPatch = $"--- a/{file}\n+++ b/{file}\n@@ -1 +1 @@\n-// a\n+// b\n";
		(Sandbox Box, string Patch) = WithPatch(BuildPatch);
		using Sandbox _ = Box;
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, file.Replace('/', Path.DirectorySeparatorChar)), "// a\n");
		string Dll = WriteReceipt(Box);
		// All it links is in the list, so only the kind of file patched can say the list isn't whole.
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-failed.log", 255, [0, 1], buildOutput: MutantThen(Dll, CompileLines)) };

		int Exit = await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-name=X9", "-patch=" + Patch, "-filter=Project.Gameplay"], CancellationToken.None);

		Assert.AreEqual(MutateCommand.CaughtExitCode, Exit, Box.Logger.Text);
		Assert.Contains("uak doesn't know which", Box.Logger.Text);
		Assert.IsNull(Record(Box, "X9").Binaries, "it may add a module or a plugin, whose binaries no receipt lists");
	}

	[TestMethod]
	[DataRow("Foo.Build.cs", true)]
	[DataRow("Game.Target.cs", true)]
	[DataRow("Tool.UPLUGIN", true)]
	[DataRow("Game.uproject", true)]
	[DataRow("Foo.cs", false)]
	[DataRow("Foo.cpp", false)]
	public void TheFilesThatSayWhatABuildIsMadeOfAreKnown(string path, bool expected)
	{
		Assert.AreEqual(expected, MutateCommand.IsBuildDescription(path));
	}

	[TestMethod]
	public async Task ARestoreRefusesWhileTheKilledChecksCommandStillRuns()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "O", 0, null);
		string Mutant = File.ReadAllText(Box.SourceFile);
		FakeLock Lock = new() { OrphanedCommand = "PID 42", OrphanedHoldIds = ["abc"] };
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };

		Assert.AreEqual(2, await new MutateCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("the killed check's command still runs (PID 42)", Box.Logger.Text);
		Assert.Contains("`uak lock forget-orphan -hold=abc`", Box.Logger.Text);
		Assert.Contains("may have been reused", Box.Logger.Text);
		Assert.AreEqual(Mutant, File.ReadAllText(Box.SourceFile));
		Assert.IsEmpty(Runner.Invocations);
		Assert.AreEqual(1, Lock.Released);
		Lock.OrphanedCommand = null;
		Assert.AreEqual(0, await new MutateCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	public async Task ACheckRefusesWhileADeadHoldsCommandStillRuns()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeLock Lock = new() { OrphanedCommand = "PID 42", OrphanedHoldIds = ["abc", "def"] };
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 0]) };

		Assert.AreEqual(2, await new MutateCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-name=O2", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("a dead hold's command still runs (PID 42)", Box.Logger.Text);
		Assert.Contains("`uak lock forget-orphan -hold=abc` and `uak lock forget-orphan -hold=def`", Box.Logger.Text);
		Assert.Contains("may have been reused", Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		Assert.IsEmpty(Runner.Invocations);
		Assert.IsFalse(File.Exists(JournalFile(Box, "O2")));
		Assert.AreEqual(1, Lock.Released);
	}

	/// <summary>The real lock, with the wait for a dead holder's command cut short.</summary>
	sealed class ShortWaitLock : IEditorLockProvider
	{
		public async Task<IAsyncDisposable> AcquireAsync(UakContext context, string name, CancellationToken cancellationToken)
		{
			EditorLockOptions Options = new() { PollInterval = TimeSpan.FromMilliseconds(50), OrphanedCommandTimeout = TimeSpan.FromMilliseconds(100) };
			return await EditorLock.AcquireAsync(context, name, null, Options, cancellationToken).ConfigureAwait(false);
		}
	}

	[TestMethod]
	public async Task ForgetOrphanLetsARestoreThroughThatTheRealLockRefused()
	{
		using Sandbox Box = new();
		File.WriteAllText(Box.SourceFile, Original);
		LeaveJournal(Box, "O3", 0, null);
		string Mutant = File.ReadAllText(Box.SourceFile);
		// This process stands for a reused PID: it runs, and it won't end.
		ProcessIdentity Self = ProcessIdentity.Current;
		EditorLockScope Scope = EditorLock.GetScope(Box.Context());
		LockHolderInfo Record = new()
		{
			Name = "uak mutate O3",
			Pid = 1,
			Machine = Environment.MachineName,
			HoldId = Guid.NewGuid().ToString("N"),
			Scope = Scope.ToString(),
			ScopeKey = Scope.Key,
			CommandPid = Self.Pid,
			CommandProcessStart = Self.StartTimeUtc,
		};
		StateFiles.WriteJson(Path.Combine(Box.StateDirectory, "Lock", "Orphans", Record.HoldId + ".json"), Record);
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0]) };
		UnrealServices Real = Services(Runner, new ShortWaitLock());

		Assert.AreEqual(2, await new MutateCommand(Real).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);
		Assert.Contains($"`uak lock forget-orphan -hold={Record.HoldId}`", Box.Logger.Text);
		Assert.AreEqual(Mutant, File.ReadAllText(Box.SourceFile));

		Assert.AreEqual(0, await new LockForgetOrphanCommand().RunAsync(Box.Context(), ["-hold=" + Record.HoldId], CancellationToken.None), Box.Logger.Text);

		Assert.AreEqual(0, await new MutateCommand(Real).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
	}

	[TestMethod]
	[DataRow("Proto/Foo.proto", "Foo.pb.cc")]
	[DataRow("Foo.swift", "Foo.swift Bar.swift")]
	[DataRow("Foo.rc", "Resource [x64] Foo.rc")]
	public void ASourceNoCompileIsNamedAfterNeedsOneCompile(string name, string item)
	{
		List<DiffFile> Files = UnifiedDiff.Parse($"--- a/{name}\n+++ b/{name}\n@@ -1 +1 @@\n-a\n+b\n");
		MutationFile Entry = new() { Path = name, Existed = true };
		BuildOutcome Built = new(true, false, [item], []);
		BuildOutcome Nothing = new(true, true, [], []);

		Assert.IsEmpty(MutateCommand.FindUncompiled(Built, Files, [name]));
		Assert.HasCount(1, MutateCommand.FindUncompiled(Nothing, Files, [name]), "the patch never reached the binaries");
		Assert.IsTrue(MutateCommand.ShowsRecompile(Built with { CompiledClean = [item] }, Entry));
		Assert.IsFalse(MutateCommand.ShowsRecompile(Nothing, Entry));
	}

	[TestMethod]
	public void AResourceCompileIsACompileItemThatNamesItsFile()
	{
		BuildLogSummary Summary = BuildLogParser.Parse(["[1/1] Compile Resource [x64] Foo.rc"]);

		Assert.AreEqual("Compile", Summary.Actions.Single().Kind);
		Assert.Contains("Foo.rc", Summary.Actions.Single().Item);
	}

	[TestMethod]
	public async Task AStampThatThrowsLeavesTheJournalAndTheRunStillReportsIt()
	{
		(Sandbox Box, string Patch) = WithPatch();
		using Sandbox _ = Box;
		FakeProcessRunner Runner = new() { Behaviour = Plays("test-passed.log", 0, [0, 0]) };
		static Task<bool> Throws(UakContext _) => throw new InvalidOperationException("the compiles can't be listed");

		Assert.AreEqual(MutateCommand.RestoreFailedExitCode, await new MutateCommand(Services(Runner, waitForCompiles: Throws)).RunAsync(Box.Context(), ["-name=N10", "-patch=" + Patch, "-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		Assert.Contains("could not mark the files for a recompile: the compiles can't be listed", Box.Logger.Text);
		Assert.AreEqual(Original, File.ReadAllText(Box.SourceFile));
		Assert.IsTrue(File.Exists(JournalFile(Box, "N10")));
		Assert.AreEqual(0, await new MutateCommand(Services(Runner)).RunAsync(Box.Context(), ["-restore"], CancellationToken.None), Box.Logger.Text);
	}

	[TestMethod]
	public void ADotFolderIsNeverTakenForTheWorkTreeOfAPatch()
	{
		using Sandbox Box = new();
		const string Path_ = "Source/Tool/Tool.cpp";
		List<DiffFile> Files = UnifiedDiff.Parse($"--- a/{Path_}\n+++ b/{Path_}\n@@ -1 +1 @@\n-a\n+b\n");
		string Plugin = Path.Combine(Box.ProjectDirectory, "Plugins", "Tool");
		string Worktree = Path.Combine(Box.ProjectDirectory, ".claude", "worktree");
		foreach (string Folder in new[] { Plugin, Worktree })
		{
			Sandbox.Write(Path.Combine(Folder, ".git"), "gitdir: elsewhere\n");
			Sandbox.Write(Path.Combine(Folder, "Source", "Tool", "Tool.cpp"), "a\n");
		}

		Assert.AreEqual(Plugin, MutateCommand.FindNestedGitRoot(Files, Box.ProjectDirectory, Box.ProjectDirectory), "or two fit, and the patch is refused");
	}
}
