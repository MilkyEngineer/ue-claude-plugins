// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text;
using AgentKit.Locking;

namespace AgentKit.Unreal.Tests;

/// <summary>
/// <c>uak mutate -restore -accept</c>'s check of a file edited over the mutant (<see cref="MutationUndo"/>): a three-way merge
/// of the mutant, the file and the original (<see cref="ThreeWayMerge"/>). The cases are review 4's (U-1): edits the old
/// line-count check kept with the mutation still in, and edits it refused although the mutation was out; and review 5's
/// (R-1, R-2, R-4): edits the merge's fallbacks kept with the mutation still in; review 6's (V-1) and its fuzzer's: edits
/// that move, copy or swap the mutated lines or what pins them. The rule: -accept keeps an edit of other lines only; an edit
/// that touched the lines around a change of the patch (the strict near-edit rule), or that uak can't place for sure, is
/// refused.
/// </summary>
[TestClass]
public sealed class MutationMergeTests
{
	static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

	static string Text(byte[]? bytes) => bytes is null ? "<none>" : Encoding.UTF8.GetString(bytes);

	const string AddPatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,4 +1,4 @@\n int Add(int A, int B)\n {\n-\treturn A + B;\n+\treturn A - B;\n }\n";
	const string AddOriginal = "int Add(int A, int B)\n{\n\treturn A + B;\n}\n";

	const string SwapPatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,6 +1,6 @@\n void F()\n {\n-\tX();\n+\tZ();\n \tY();\n-\tZ();\n+\tX();\n }\n";
	const string SwapOriginal = "void F()\n{\n\tX();\n\tY();\n\tZ();\n}\n";

	// A guard's 'return false;' is in the mutated hunk's context, and the mutation adds another 'return false;'.
	const string GuardPatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -4,5 +4,5 @@\n \t{\n \t\treturn false;\n \t}\n-\treturn true;\n+\treturn false;\n }\n";
	const string GuardOriginal = "bool G(int V)\n{\n\tif (V < 0)\n\t{\n\t\treturn false;\n\t}\n\treturn true;\n}\n";

	const string DeletePatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,5 +1,4 @@\n void F()\n {\n-\tCount++;\n \tRun();\n }\n";
	const string DeleteOriginal = "void F()\n{\n\tCount++;\n\tRun();\n}\n";

	const string StringPatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,4 +1,4 @@\n void F()\n {\n-\tLog(\"a b\");\n+\tLog(\"ab\");\n }\n";

	const string NoNewlinePatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,3 +1,3 @@\n int Add(int A, int B)\n {\n-\treturn A + B; }\n\\ No newline at end of file\n"
		+ "+\treturn A - B; }\n\\ No newline at end of file\n";

	const string StructPatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,5 +1,5 @@\n struct S\n {\n-\tint A = 1;\n+\tint A = 2;\n };\n // end\n";
	const string StructOriginal = "struct S\n{\n\tint A = 1;\n};\n// end\n";

	const string BlockPatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,4 +1,6 @@\n void F()\n {\n+\tif (Skip)\n+\t\treturn;\n \tRun();\n }\n";

	// Review 5's cases: IsValid, a blank line, then Count; the patch makes IsValid return false.
	const string ValidPatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,6 +1,6 @@\n bool IsValid() const\n {\n-\treturn true;\n+\treturn false;\n }\n \n int Count() const\n";
	const string ValidOriginal = "bool IsValid() const\n{\n\treturn true;\n}\n\nint Count() const\n{\n\treturn Num;\n}\n";
	const string ReadyAndCount = "\nbool IsReady() const\n{\n\treturn true;\n}\n\nint Count() const\n{\n\treturn Num;\n}\n";

	static MutationAcceptCheck Check(string patch, string original, string now) => Check(patch, Bytes(original), Bytes(now));

	static MutationAcceptCheck Check(string patch, byte[] original, byte[] now) => MutationUndo.Check(UnifiedDiff.Parse(patch).Single(), original, now);

	[TestMethod]
	[DataRow("a far edit", AddPatch, AddOriginal + "\n// more\n", "int Add(int A, int B)\n{\n\treturn A - B;\n}\n\n// more, edited\nint Z;\n",
		"int Add(int A, int B)\n{\n\treturn A + B;\n}\n\n// more, edited\nint Z;\n")]
	[DataRow("e: a swap, with a line added above the function", SwapPatch, SwapOriginal, "// edit\nvoid F()\n{\n\tZ();\n\tY();\n\tX();\n}\n", "// edit\n" + SwapOriginal)]
	[DataRow("h: a deleted line, the same line added elsewhere", DeletePatch, DeleteOriginal + "\nvoid G()\n{\n}\n", "void F()\n{\n\tRun();\n}\n\nvoid G()\n{\n\tCount++;\n}\n",
		"void F()\n{\n\tCount++;\n\tRun();\n}\n\nvoid G()\n{\n\tCount++;\n}\n")]
	[DataRow("i: white space inside a string literal", StringPatch, "void F()\n{\n\tLog(\"a b\");\n}\n", "// edit\nvoid F()\n{\n\tLog(\"ab\");\n}\n",
		"// edit\nvoid F()\n{\n\tLog(\"a b\");\n}\n")]
	[DataRow("l: no line end at the end, an edit above", NoNewlinePatch, "int Add(int A, int B)\n{\n\treturn A + B; }", "// edit\nint Add(int A, int B)\n{\n\treturn A - B; }",
		"// edit\nint Add(int A, int B)\n{\n\treturn A + B; }")]
	[DataRow("q: lines added above", AddPatch, AddOriginal, "// header\n// more\nint Add(int A, int B)\n{\n\treturn A - B;\n}\n",
		"// header\n// more\nint Add(int A, int B)\n{\n\treturn A + B;\n}\n")]
	[DataRow("j: CRLF, an edit above", AddPatch, "int Add(int A, int B)\r\n{\r\n\treturn A + B;\r\n}\r\n", "// edit\r\nint Add(int A, int B)\r\n{\r\n\treturn A - B;\r\n}\r\n",
		"// edit\r\nint Add(int A, int B)\r\n{\r\n\treturn A + B;\r\n}\r\n")]
	[DataRow("an edit that made a CRLF file LF: the original's line gets LF too", AddPatch, "int Add(int A, int B)\r\n{\r\n\treturn A + B;\r\n}\r\n",
		"// edit\nint Add(int A, int B)\n{\n\treturn A - B;\n}\n", "// edit\nint Add(int A, int B)\n{\n\treturn A + B;\n}\n")]
	[DataRow("an edit with mixed line ends: the original's line keeps its own", AddPatch, AddOriginal, "// edit\r\nint Add(int A, int B)\n{\n\treturn A - B;\n}\n",
		"// edit\r\nint Add(int A, int B)\n{\n\treturn A + B;\n}\n")]
	[DataRow("a change that removes a line and adds it again: only what differs is the patch's change", "--- a/G.cpp\n+++ b/G.cpp\n@@ -1,3 +1,2 @@\n int A;\n-int B;\n-int C;\n+int B;\n",
		"int A;\nint B;\nint C;\n", "// edit\nint A;\nint B;\n", "// edit\nint A;\nint B;\nint C;\n")]
	[DataRow("a '};' like the one after the mutated line written further down: a line of punctuation alone is no copy", StructPatch, StructOriginal,
		"struct S\n{\n\tint A = 2;\n};\n// end\nint Values[] = { 1,\n\t2,\n};\n", "struct S\n{\n\tint A = 1;\n};\n// end\nint Values[] = { 1,\n\t2,\n};\n")]
	public void AnEditOfOtherLinesIsKeptAndTheMutationTakenOut(string label, string patch, string original, string now, string expected)
	{
		MutationAcceptCheck Result = Check(patch, original, now);

		Assert.AreEqual(MutationAcceptAction.Undo, Result.Action, $"{label}: {Result.Why}");
		Assert.AreEqual(expected, Text(Result.Bytes), label);
	}

	[TestMethod]
	public void AByteOrderMarkIsKept()
	{
		byte[] Bom = [0xEF, 0xBB, 0xBF];
		MutationAcceptCheck Result = Check(AddPatch, [.. Bom, .. Bytes(AddOriginal)], [.. Bom, .. Bytes("// edit\nint Add(int A, int B)\n{\n\treturn A - B;\n}\n")]);

		Assert.AreEqual(MutationAcceptAction.Undo, Result.Action, Result.Why);
		CollectionAssert.AreEqual(Bom.Concat(Bytes("// edit\n" + AddOriginal)).ToArray(), Result.Bytes);
	}

	[TestMethod]
	[DataRow("the line before the mutated block edited (one line from the mutated line)", AddPatch, AddOriginal,
		"int Add(int A, int B) // edited\n{\n\treturn A - B;\n}\n", "changed, moved or deleted line 1 ('int Add(int A, int B)')")]
	[DataRow("lines added after the mutated function, which ends the file", AddPatch, AddOriginal, "int Add(int A, int B)\n{\n\treturn A - B;\n}\n\n\n// edit\nint Z;\n",
		"put lines after line 4, at the file's end")]
	[DataRow("acceptguard: a comment on the guard's 'return false;', the line before the mutated block", GuardPatch, GuardOriginal,
		"bool G(int V)\n{\n\tif (V < 0)\n\t{\n\t\treturn false; // edited\n\t}\n\treturn false;\n}\n", "changed, moved or deleted line 5 ('return false;')")]
	[DataRow("f0: the same, with a function returning false after", GuardPatch, GuardOriginal + "bool H() { return false; }\n",
		"bool G(int V)\n{\n\tif (V < 0)\n\t{\n\t\treturn false; // edited\n\t}\n\treturn false;\n}\nbool H() { return false; }\n", "changed, moved or deleted line 5")]
	[DataRow("h: a deleted line's neighbour edited, and the line added elsewhere", DeletePatch, DeleteOriginal, "void F() // edited\n{\n\tRun();\n}\nvoid G()\n{\n\tCount++;\n}\n",
		"changed, moved or deleted line 1 ('void F()')")]
	[DataRow("M2: a function added after the mutated one, with braces like the mutated block's", ValidPatch, ValidOriginal,
		"bool IsValid() const\n{\n\treturn false;\n}\n" + ReadyAndCount, "copied line 2 ('{': 3 times in the file, 2 in the mutant)")]
	[DataRow("M1: and the signature commented", ValidPatch, ValidOriginal, "bool IsValid() const // HUD\n{\n\treturn false;\n}\n" + ReadyAndCount, "line 1 ('bool IsValid() const')")]
	[DataRow("M4: and a comment above the signature, the block's neighbour", ValidPatch, ValidOriginal,
		"// Valid once loaded.\nbool IsValid() const\n{\n\treturn false;\n}\n" + ReadyAndCount, "copied line 2 ('{'")]
	[DataRow("a function appended right after the mutated one, both ending in '}'", AddPatch, AddOriginal, "int Add(int A, int B)\n{\n\treturn A - B;\n}\n\nint Zero()\n{\n\treturn 0;\n}\n",
		"put lines after line 4, at the file's end")]
	[DataRow("the line ends of the lines around the mutation changed (CRLF in an LF file)", AddPatch, AddOriginal, "// edit\nint Add(int A, int B)\r\n{\r\n\treturn A - B;\n}\n",
		"a line end of one of them changed")]
	public void AnEditThatTouchedTheLinesAroundAChangeIsRefusedAndNamed(string label, string patch, string original, string now, string named)
	{
		// The strict near-edit rule: the anchor (the fewest lines around the change that occur once in the mutant) and one line
		// each side must be in the file once, as they were (a line end's '\r' aside only on the lines the patch added, or in a
		// file with one kind of line end), none moved, and none of them more often than in the mutant. Each of these edits
		// changes no mutated line, and the merge took the mutation out before the rule; now it is refused, and -force offered.
		MutationAcceptCheck Result = Check(patch, original, now);

		Assert.AreEqual(MutationAcceptAction.Refuse, Result.Action, $"{label}: {Text(Result.Bytes)}");
		Assert.Contains("the later edit touched the lines around the patch's change 1", Result.Why, label);
		Assert.Contains(named, Result.Why, label);
		Assert.Contains("-force puts the original back and saves the edit", Result.Why, label);
	}

	[TestMethod]
	// Review 6's fuzzer (rv6/fz, seeds 1-4, 200k each): an edit the merge read as leaving the mutated lines alone, where they
	// were really cut and pasted back, copied with the original then removed, or swapped with like lines; the merge took the
	// mutation out of the copy, or put deleted lines back between the wrong two, and kept the mutation. Each was merged
	// (exit 0) before the strict near-edit rule.
	[DataRow("delete and paste back: the brace after the mutated line edited, removed, and the two lines pasted above (seed 1 iter 140499)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -1,2 +1,2 @@\n-}\n+\n {\n", "﻿}\n{\n", "﻿\n{\n\n")]
	[DataRow("delete and paste back: lines removed around the mutated brace, and a copy of it with its neighbours pasted above (seed 3 iter 23183)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -2,5 +2,3 @@\n }\n-\n-{\n-\n+{\n o5\n", "\r\n}\r\n\r\n{\r\n\r\no5\r\n\r\n{\r\n}\r\n", "\r\n}\r\n{\r\no5\r\n}\r\n{\r\n}\r\n")]
	[DataRow("copy then delete the original: the mutated lines copied above, a line next to them removed (seed 3 iter 175866)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -1,4 +1,3 @@\n-\treturn A - B;\n-x;\n-\treturn A - B;\n+\treturn A + B;\n+\n \treturn A + B;\n",
		"\treturn A - B;\nx;\n\treturn A - B;\n\treturn A + B;\n", "\treturn A + B;\n\n\treturn A + B;\n\n")]
	[DataRow("copy then delete the original: two changes, the second's lines copied up and its neighbours removed (seed 4 iter 195432)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -1,3 +1,4 @@\n-{\n-y;\n+\n+\treturn A - B;\n+\treturn A - B;\n y;\n@@ -5,3 +6,4 @@\n }\n-\treturn A - B;\n+y;\n+\n y;\n",
		"{\r\ny;\r\ny;\r\ny;\r\n}\r\n\treturn A - B;\r\ny;\r\n\treturn A + B;\r\n}\r\n", "\r\n\treturn A - B;\r\n\treturn A - B;\r\ny;\r\ny;\r\n}\r\n}\r\ny;\r\n\r\ny;\r\n\r\n}\r\n")]
	[DataRow("copy then delete the original: a blank mutation among blank lines (seed 4 iter 131119)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -1,4 +1,3 @@\n \n-{\n-o2\n+\n \n", "﻿\n{\no2\n\n\n\r\n", "﻿\r\n\n\n\n\n")]
	[DataRow("a brace mutation in a file of two distinct lines, a like brace moved across it (seed 1 iter 59777)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -2,5 +2,3 @@\n {\n-}\n-}\n-}\n+{\n }\n", "}\n{\n}\n}\n}\n}\n{\n", "﻿}\n{\n{\n}\n{\n")]
	[DataRow("a brace mutation swapped with the like brace beside it, an edit further down (seed 1 iter 17231)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -1,2 +1,2 @@\n-}\n+{\n {\n", "}\n{\n\n{\n}\r\n}\r\n}\n", "{\n{\n\n{\n}\r\n} // e\n} // e\n}\r\n}\n")]
	[DataRow("a blank mutation swapped with the blank line beside it, which has the other line end (seed 1 iter 22588)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -1,5 +1,5 @@\n {\n \n-o2\n+\n \n \n", "{\r\n\r\no2\n\r\n\r\n", "{\r\n\n\r\n\r\n\r\n")]
	[DataRow("removed lines between two like braces, in a run of them the edit rotated (seed 3 iter 153116)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -1,6 +1,3 @@\n }\n-}\n-}\n-{\n }\n }\n", "}\n}\n}\n{\n}\n}\n{\n}\n{\n{\n", "}\n}\n}\n{\n}\n{\nE22969\nE22968\n")]
	public void AnEditThatMovedOrCopiedTheMutatedLinesIsRefused(string label, string patch, string original, string now)
	{
		MutationAcceptCheck Result = Check(patch, original, now);

		Assert.AreEqual(MutationAcceptAction.Refuse, Result.Action, $"{label}: {Text(Result.Bytes)}");
		Assert.Contains("-force puts the original back and saves the edit", Result.Why, label);
	}

	[TestMethod]
	[DataRow("acceptrename / c: a rename over the mutant", AddPatch, AddOriginal, "int Add(int Lhs, int Rhs)\n{\n\treturn Lhs - Rhs;\n}\n", "line 3 as it is now ('return Lhs - Rhs;')")]
	[DataRow("d: the mutant in parentheses", AddPatch, AddOriginal, "int Add(int A, int B)\n{\n\treturn (A - B);\n}\n", "'return A + B;'")]
	[DataRow("d2: the mutant split over two lines", AddPatch, AddOriginal, "int Add(int A, int B)\n{\n\treturn A\n\t\t- B;\n}\n", "lines 3-4 as it is now")]
	[DataRow("the mutant re-spaced, a line above edited", AddPatch, AddOriginal, "int Add(int A, int B) // edited\n{\n\treturn A-B;\n}\n", "line 3")]
	[DataRow("the mutated line rewritten", AddPatch, AddOriginal, "int Add(int A, int B)\n{\n\treturn B + A;\n}\n", "line 3")]
	[DataRow("re-indented into a namespace", AddPatch, AddOriginal, "namespace N\n{\n\tint Add(int A, int B)\n\t{\n\t\treturn A - B;\n\t}\n}\n", "as it is now")]
	[DataRow("n: a line inserted inside an added two-line block", BlockPatch, "void F()\n{\n\tRun();\n}\n",
		"void F()\n{\n\tif (Skip)\n\t\t// edited\n\t\treturn;\n\tRun();\n}\n", "lines 3-5 as it is now ('if (Skip)')")]
	[DataRow("an edit next to a deleted line", "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,3 +1,2 @@\n void F()\n-{ Check(); }\n int X;\n", "void F()\n{ Check(); }\nint X;\n",
		"void F() // edited\nint X;\n", "line 1 as it is now")]
	[DataRow("o: the function deleted", AddPatch, AddOriginal, "// Add removed\n", "as it is now")]
	[DataRow("p: the file emptied", AddPatch, AddOriginal, "", "the edit removed lines there")]
	public void AnEditOfTheMutatedLinesOrTheirNeighboursIsRefusedAndNamed(string label, string patch, string original, string now, string named)
	{
		MutationAcceptCheck Result = Check(patch, original, now);

		Assert.AreEqual(MutationAcceptAction.Refuse, Result.Action, $"{label}: {Text(Result.Bytes)}");
		Assert.Contains("change the same or adjacent lines", Result.Why, label);
		Assert.Contains(named, Result.Why, label);
	}

	[TestMethod]
	public void AFileTheMutationWasTakenOutOfByHandIsRefused()
	{
		// R-1. falserefuse g2: the mutant taken out by hand, and a new function that returns false. The old fallback merged it
		// again from the original and kept it; one shape of that merge kept the mutation (M4, G1, D1 below).
		const string Fixed = "bool G(int V)\n{\n\tif (V < 0)\n\t{\n\t\treturn false;\n\t}\n\treturn true;\n}\nbool K()\n{\n\treturn false;\n}\n";
		// g: the same, with a context line edited too.
		const string FixedAndEdited = "bool G(int V)\n{\n\tif (V < 0)\n\t{\n\t\treturn false; // edited\n\t}\n\treturn true;\n}\nbool H() { return false; }\nbool K()\n{\n\treturn false;\n}\n";
		// The agent's own fix: the original function, and a line it wants.
		const string OwnFix = "// a fix the agent wants\nint Add(int A, int B)\n{\n\treturn A + B;\n}\n";

		foreach ((string Patch, string Original, string Now) in new[] { (GuardPatch, GuardOriginal, Fixed), (GuardPatch, GuardOriginal + "bool H() { return false; }\n", FixedAndEdited),
			(AddPatch, AddOriginal, OwnFix) })
		{
			MutationAcceptCheck Result = Check(Patch, Original, Now);
			Assert.AreEqual(MutationAcceptAction.Refuse, Result.Action, Now);
		}
	}

	[TestMethod]
	public void AnEditThatChangesAPlaceTheWayTheUndoDoesIsRefused()
	{
		// R-2: where the edit and taking the mutation out change a place alike, the merge takes them for one change, and can't
		// tell the edit's from the undo's. The mutation taken out by hand, with a line added at the end: refused, and named.
		MutationAcceptCheck ByHand = Check(AddPatch, AddOriginal, AddOriginal + "// edit\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, ByHand.Action);
		Assert.Contains("the way taking the mutation out does", ByHand.Why);
		Assert.Contains("line 3 as it is now ('return A + B;')", ByHand.Why);

		// S1: one of two Step(); deleted; the edit replaces Draw(); Flush(); with Step(); Draw();. Kept before ("changes nothing").
		MutationAcceptCheck Steps = Check("--- a/F.cpp\n+++ b/F.cpp\n@@ -1,5 +1,4 @@\n void Tick()\n {\n-\tStep();\n \tStep();\n \tDraw();\n",
			"void Tick()\n{\n\tStep();\n\tStep();\n\tDraw();\n\tFlush();\n}\n", "void Tick()\n{\n\tStep();\n\tStep();\n\tDraw();\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Steps.Action, Text(Steps.Bytes));
	}

	[TestMethod]
	public void AMergeThatStillHoldsTheMutatedLinesIsRefused()
	{
		// The edit copied the mutated function, context and all: the merge would take the mutation out of one copy only. The
		// strict near-edit rule refuses it first (lines around the mutated one copied); behind it, the anchor rule (the lines
		// around it are in the file twice) and the copy check (the merge holds the mutated lines more often than the original).
		MutationAcceptCheck Copied = Check(AddPatch, AddOriginal, "int Add(int A, int B)\n{\n\treturn A - B;\n}\n// a copy\nint Add(int A, int B)\n{\n\treturn A - B;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Copied.Action);
		Assert.Contains("the later edit touched the lines around the patch's change 1", Copied.Why);
		Assert.Contains("copied line 1 ('int Add(int A, int B)'", Copied.Why);

		// R-4, R1: Add renamed to Sum, and a new Add with the mutant body after it. The merge lines the mutant's Add up with the
		// new one, so it would put A + B there and leave Sum mutated; the old check needed the whole hunk's context copied. The
		// lines around the mutated one are in the file twice now: copied.
		MutationAcceptCheck Renamed = Check(AddPatch, AddOriginal, "int Sum(int A, int B)\n{\n\treturn A - B;\n}\nint Add(int A, int B)\n{\n\treturn A - B;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Renamed.Action, Text(Renamed.Bytes));
		Assert.Contains("copied line 3 ('return A - B;'", Renamed.Why);

		// Sub's body is the mutant's already, so the mutated line is pinned with Add's signature; the edit adds Neg, a third body
		// like it. The pinned block is there once, but the merge would hold the mutated lines with the lines around them more
		// often than the original does: a copy may be what it took the mutation out of.
		const string AddAndSub = "int Add(int A, int B)\n{\n\treturn A + B;\n}\nint Sub(int A, int B)\n{\n\treturn A - B;\n}\n";
		MutationAcceptCheck Third = Check(AddPatch, AddAndSub, AddAndSub.Replace("A + B", "A - B", StringComparison.Ordinal) + "int Neg(int A, int B)\n{\n\treturn A - B;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Third.Action, Text(Third.Bytes));
		Assert.Contains("copied line 3 ('return A - B;': 3 times in the file, 2 in the mutant)", Third.Why);

		// The same with a hunk without context lines (diff -U0), which the old check skipped.
		MutationAcceptCheck NoContext = Check("--- a/F.cpp\n+++ b/F.cpp\n@@ -3 +3 @@\n-\treturn A + B;\n+\treturn A - B;\n", AddOriginal,
			"int Sum(int A, int B)\n{\n\treturn A - B;\n}\nint Add(int A, int B)\n{\n\treturn A - B;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, NoContext.Action, Text(NoContext.Bytes));

		// The mutated line edited in place (a comment), and a function with the mutant body added: the merge takes the new
		// function's line out, and the commented one, still mutated, stays. Lines that start with the mutated line count.
		MutationAcceptCheck Edited = Check(ValidPatch, ValidOriginal, "bool IsValid() const\n{\n\treturn false; // why\n}\n\nbool IsReady() const\n{\n\treturn false;\n}\n\nint Count() const\n{\n\treturn Num;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Edited.Action, Text(Edited.Bytes));
		Assert.Contains("the later edit touched the lines around the patch's change 1", Edited.Why);

		// A new function whose body is the mutant line may be such a copy: refused too, the price of never keeping one.
		MutationAcceptCheck Own = Check(AddPatch, AddOriginal, "int Add(int A, int B)\n{\n\treturn A - B;\n}\nint Sub(int A, int B)\n{\n\treturn A - B;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Own.Action, Text(Own.Bytes));
		// falserefuse g3: the mutant kept, and a new function returning false appended.
		MutationAcceptCheck Appended = Check(GuardPatch, GuardOriginal, "bool G(int V)\n{\n\tif (V < 0)\n\t{\n\t\treturn false;\n\t}\n\treturn false;\n}\nbool K()\n{\n\treturn false;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Appended.Action, Text(Appended.Bytes));
	}

	[TestMethod]
	public void EditsTheOldFallbackKeptWithTheMutationAreRefused()
	{
		// R-1's G1: a comment on the guard's 'return false;' (in the hunk's context), and a function H like G added.
		MutationAcceptCheck Guard = Check(GuardPatch, GuardOriginal, "bool G(int V)\n{\n\tif (V < 0)\n\t{\n\t\treturn false; // edited\n\t}\n\treturn false;\n}\n"
			+ "bool H(int V)\n{\n\tif (V > 9)\n\t{\n\t\treturn false;\n\t}\n\treturn true;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Guard.Action, Text(Guard.Bytes));
		// R-1's D1: a statement deleted; Tick's signature edited, and Reset added with the deleted statement's block.
		MutationAcceptCheck Deleted = Check("--- a/F.cpp\n+++ b/F.cpp\n@@ -1,5 +1,4 @@\n void Tick()\n {\n \tUpdate();\n-\tCount++;\n }\n",
			"void Tick()\n{\n\tUpdate();\n\tCount++;\n}\n", "void Tick() // per frame\n{\n\tUpdate();\n}\nvoid Reset()\n{\n\tUpdate();\n\tCount++;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Deleted.Action, Text(Deleted.Bytes));
		// C1: the brace above the mutated line edited, and G added with the original's body.
		MutationAcceptCheck Brace = Check("--- a/F.cpp\n+++ b/F.cpp\n@@ -1,4 +1,4 @@\n void F()\n {\n-\treturn true;\n+\treturn false;\n }\n",
			"void F()\n{\n\treturn true;\n}\n", "void F()\n{ // note\n\treturn false;\n}\nvoid G()\n{\n\treturn true;\n}\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Brace.Action, Text(Brace.Bytes));
	}

	[TestMethod]
	public void AnEditWhosePlaceAmongLikeLinesIsAGuessIsRefused()
	{
		// Count++; deleted between Step(); and Run();, and the edit adds a Run(): right where Count++; was (next to the mutation:
		// a conflict), or after the other Run() (not). Two equally short alignments say each: the merge would guess the order.
		// The strict near-edit rule refuses it first: a line of the block that pins the deletion was copied.
		const string Patch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,6 +1,5 @@\n void F()\n {\n \tStep();\n-\tCount++;\n \tRun();\n }\n";
		MutationAcceptCheck Result = Check(Patch, "void F()\n{\n\tStep();\n\tCount++;\n\tRun();\n}\n", "void F()\n{\n\tStep();\n\tRun();\n\tRun();\n}\n");

		Assert.AreEqual(MutationAcceptAction.Refuse, Result.Action, Text(Result.Bytes));
		Assert.Contains("copied line 4 ('Run();': 2 times in the file, 1 in the mutant)", Result.Why);
	}

	// Review 6's V-1: two functions alike but for their signatures. The patch makes IsA return false; IsB already does.
	const string IsAPatch = "--- a/F.cpp\n+++ b/F.cpp\n@@ -1,4 +1,4 @@\n bool IsA() const\n {\n-\treturn true;\n+\treturn false;\n }\n";
	const string IsA = "bool IsA() const\n{\n\treturn true;\n}\n";
	const string IsAMutated = "bool IsA() const\n{\n\treturn false;\n}\n";
	const string IsB = "bool IsB() const\n{\n\treturn false;\n}\n";
	const string Count = "int Count() const\n{\n\treturn Num;\n}\n";

	[TestMethod]
	[DataRow("swap: the edit moves IsB above IsA (CRLF)", IsAPatch, "bool IsA() const\r\n{\r\n\treturn true;\r\n}\r\n\r\nbool IsB() const\r\n{\r\n\treturn false;\r\n}\r\n",
		"bool IsB() const\r\n{\r\n\treturn false;\r\n}\r\n\r\nbool IsA() const\r\n{\r\n\treturn false;\r\n}\r\n")]
	[DataRow("n3: the same, LF", IsAPatch, IsA + "\n" + IsB, IsB + "\n" + IsAMutated)]
	[DataRow("v4: swapped, and a far edit too", IsAPatch, IsA + "\n" + IsB + "\n" + Count, IsB + "\n" + IsAMutated + "\n" + "int Count() const\n{\n\treturn Num + 0;\n}\n")]
	[DataRow("v5: 1 -> 2 in GetA, the edit moves GetB above GetA", "--- a/F.cpp\n+++ b/F.cpp\n@@ -2,4 +2,4 @@\n {\n \tcheck(bValid);\n-\treturn 1;\n+\treturn 2;\n }\n",
		"int GetA() const\n{\n\tcheck(bValid);\n\treturn 1;\n}\n\nint GetB() const\n{\n\tcheck(bValid);\n\treturn 2;\n}\n",
		"int GetB() const\n{\n\tcheck(bValid);\n\treturn 2;\n}\n\nint GetA() const\n{\n\tcheck(bValid);\n\treturn 2;\n}\n")]
	[DataRow("d2: Count++; deleted from A, the edit moves B above A", "--- a/F.cpp\n+++ b/F.cpp\n@@ -2,4 +2,3 @@\n {\n \tFoo();\n-\tCount++;\n }\n",
		"void A()\n{\n\tFoo();\n\tCount++;\n}\n\nvoid B()\n{\n\tFoo();\n}\n", "void B()\n{\n\tFoo();\n}\n\nvoid A()\n{\n\tFoo();\n}\n")]
	[DataRow("eq1: an equally short reading between the earliest and the latest (fuzzer seed 2 iter 29574)",
		"--- a/F.cpp\n+++ b/F.cpp\n@@ -2,5 +2,4 @@\n }\n return false;\n-\treturn A - B;\n }\n }\n",
		"return false;\n}\nreturn false;\n\treturn A - B;\n}\n}\n{\nx;\n\treturn A + B;\n\treturn A - B;\n}\n{\n\treturn A + B;\nreturn true;\n",
		"E5774\n}\nreturn false;\n}\nreturn false;\n}\n{\nE5775\n\treturn A + B;\n\treturn A - B;\n}\n{\n\treturn A + B;\nreturn true;\n")]
	public void AnEditThatMovesTheMutatedLinesAwayFromWhatPinsThemIsRefused(string label, string patch, string original, string now)
	{
		// V-1: the merge read the swap as the two signatures swapped (a shorter edit than a move), so it took 'return false;' out
		// of the function now named IsA, the one that was IsB's, and kept the mutation in IsA, exit 0. The anchor rule pins each
		// change to the block around it that occurs once in the mutant, grown both ways (here the signature above and the blank
		// line below): it must be in the file once, intact, and the edit's alignment must keep it in place. The strict near-edit
		// rule refuses these first, naming the lines the edit moved.
		MutationAcceptCheck Result = Check(patch, original, now);

		Assert.AreEqual(MutationAcceptAction.Refuse, Result.Action, $"{label}: {Text(Result.Bytes)}");
		Assert.Contains("the later edit touched the lines around the patch's change 1", Result.Why, label);
		Assert.Contains("changed, moved or deleted line", Result.Why, label);
	}

	[TestMethod]
	[DataRow("v1: three functions after a comment, the edit moves Count to the top", "// F\n" + IsA + "\n" + IsB + "\n" + Count,
		Count + "\n// F\n" + IsAMutated + "\n" + IsB, Count + "\n// F\n" + IsA + "\n" + IsB)]
	[DataRow("v3: the edit moves IsB to the top, above what pins IsA's change", "// F\n" + IsA + "\n" + Count + "\n" + IsB,
		IsB + "\n// F\n" + IsAMutated + "\n" + Count, IsB + "\n// F\n" + IsA + "\n" + Count)]
	public void AMoveOfOtherLinesAwayFromWhatPinsTheMutatedLinesIsMerged(string label, string original, string now, string expected)
	{
		// A move of other lines that leaves the anchor and its neighbours intact, once, is merged (the patch's hunk names line 2
		// here: the comment above is the anchor's neighbour).
		MutationAcceptCheck Result = Check(IsAPatch.Replace("@@ -1,4 +1,4 @@", "@@ -2,4 +2,4 @@", StringComparison.Ordinal), original, now);

		Assert.AreEqual(MutationAcceptAction.Undo, Result.Action, $"{label}: {Result.Why}");
		Assert.AreEqual(expected, Text(Result.Bytes), label);
	}

	[TestMethod]
	[DataRow("v1: the edit moves Count above IsA, the file's first line", IsA + "\n" + IsB + "\n" + Count, Count + "\n" + IsAMutated + "\n" + IsB)]
	[DataRow("v3: the edit moves IsB above IsA, IsA moving with what pins it", IsA + "\n" + Count + "\n" + IsB, IsB + "\n" + IsAMutated + "\n" + Count)]
	[DataRow("n10: IsB's signature edited, the line that pins IsA's change below", IsA + "\n" + IsB, IsAMutated + "\n" + "bool IsB() const // x\n{\n\treturn false;\n}\n")]
	public void AMoveOrEditOfTheLinesThatPinTheMutatedLinesIsRefused(string label, string original, string now)
	{
		// Merged before the strict near-edit rule (the anchor went with the lines it pins): the file's start before IsA, or IsB's
		// signature, is the anchor's neighbour or one of its lines, so the edit touched it.
		MutationAcceptCheck Result = Check(IsAPatch, original, now);

		Assert.AreEqual(MutationAcceptAction.Refuse, Result.Action, $"{label}: {Text(Result.Bytes)}");
		Assert.Contains("the later edit touched the lines around the patch's change 1", Result.Why, label);
	}

	[TestMethod]
	public void AnEditTheAlignmentReadsAsTouchingWhatPinsTheMutationIsRefused()
	{
		// Fuzzer seed 1 iter 1694: the block around the patch's second change is in the file once, as it was, but the alignment
		// the merge goes by reads the edit as having removed its lines and kept like lines elsewhere. Each of these is refused
		// first by the strict near-edit rule now, which reads both alignments.
		MutationAcceptCheck Across = Check("--- a/F.cpp\n+++ b/F.cpp\n@@ -1,14 +1,8 @@\n o0\n {\n {\n-{\n-{\n-}\n }\n }\n-o8\n-{\n-}\n }\n {\n }\n\\ No newline at end of file\n",
			"o0\r\n{\r\n{\r\n{\r\n{\r\n}\r\n}\r\n}\r\no8\r\n{\r\n}\r\n}\r\n{\r\n}", "o0\r\n{\r\n{\r\n}\r\n}\r\n}\r\n}\r\n{\r\n}");
		Assert.AreEqual(MutationAcceptAction.Refuse, Across.Action, Text(Across.Bytes));
		Assert.Contains("put lines between lines 3 and 4, or moved one of them", Across.Why);

		// eq2 (fuzzer seed 1 iter 75875): the edit replaced the mutated line and wrote it again elsewhere, between like lines.
		// The earliest alignment reads it as untouched there; the latest reads the edit as removing the '}' after it.
		MutationAcceptCheck Rewritten = Check("--- a/F.cpp\n+++ b/F.cpp\n@@ -1,7 +1,7 @@\n \n {\n }\n-}\n+M00\n }\n }\n \n",
			"\r\n{\r\n}\r\n}\r\n}\r\n}\r\n\r\nreturn true;\r\n}\r\n\r\n}\r\n}\r\n", "}\r\n{\r\n}\r\n\r\n\r\n{\r\nreturn true;\r\n}\r\nM00\r\n}\r\n\r\n}\r\n}\r\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Rewritten.Action, Text(Rewritten.Bytes));
		Assert.Contains("changed, moved or deleted line 6 ('}')", Rewritten.Why);

		// A line deleted at the top, before three like lines; the edit removes two of the three. The earliest alignment keeps
		// the first (the deleted line goes back above it), the latest the last (the edit removed the line the deletion is
		// pinned to): the merges agree on the bytes, but where the edit went is a guess (fuzzer seed 1 iter 4289).
		MutationAcceptCheck Guess = Check("--- a/F.cpp\n+++ b/F.cpp\n@@ -1,4 +1,3 @@\n-o0\n \treturn A + B;\n \treturn A + B;\n \treturn A + B;\n",
			"o0\n\treturn A + B;\n\treturn A + B;\n\treturn A + B;\n}\nint G()\no6\no7\n", "\treturn A + B;\n}\nint G()\no6\no7\n");
		Assert.AreEqual(MutationAcceptAction.Refuse, Guess.Action, Text(Guess.Bytes));
		Assert.Contains("changed, moved or deleted line 2 ('return A + B;')", Guess.Why);
	}

	[TestMethod]
	public void CreatedDeletedAndUnreadableFilesAreNeverMerged()
	{
		DiffFile Created = UnifiedDiff.Parse("--- /dev/null\n+++ b/New.cpp\n@@ -0,0 +1 @@\n+int New;\n").Single();
		Assert.AreEqual(MutationAcceptAction.Refuse, MutationUndo.Check(Created, null, Bytes("int New; // edited\n")).Action);
		DiffFile Deleted = UnifiedDiff.Parse("--- a/Old.cpp\n+++ /dev/null\n@@ -1 +0,0 @@\n-int Old;\n").Single();
		Assert.AreEqual(MutationAcceptAction.Refuse, MutationUndo.Check(Deleted, Bytes("int Old;\n"), Bytes("int Old; // back\n")).Action);

		// A file deleted since can't hold the mutation.
		Assert.AreEqual(MutationAcceptAction.Keep, MutationUndo.Check(UnifiedDiff.Parse(AddPatch).Single(), Bytes(AddOriginal), null).Action);

		// An original the patch doesn't apply to (not UTF-8) can't give the mutant back: refused.
		MutationAcceptCheck NotText = Check(AddPatch, [0xFF, 0xFE, 0x00], Bytes("x\n"));
		Assert.AreEqual(MutationAcceptAction.Refuse, NotText.Action);
		Assert.Contains("can't make the mutant again", NotText.Why);
	}

	// ---- the merge itself ----

	static MergeResult Merge(string baseText, string ours, string theirs) => ThreeWayMerge.Merge(Bytes(baseText), Bytes(ours), Bytes(theirs));

	[TestMethod]
	public void ChangesOneLineApartMergeAndAdjacentOrOverlappingOnesConflict()
	{
		const string Base = "1\n2\n3\n4\n5\n";
		Assert.AreEqual("1x\n2\n3x\n4\n5\n", Text(Merge(Base, "1x\n2\n3\n4\n5\n", "1\n2\n3x\n4\n5\n").Bytes), "one unchanged line between");
		Assert.IsNull(Merge(Base, "1x\n2\n3\n4\n5\n", "1\n2x\n3\n4\n5\n").Bytes, "adjacent lines");
		Assert.IsNull(Merge(Base, "1\n2\nnew\n3\n4\n5\n", "1\n2\n3x\n4\n5\n").Bytes, "an insertion right above a change");
		Assert.IsNull(Merge(Base, "1\n2\n3\nnew\n4\n5\n", "1\n2\n3x\n4\n5\n").Bytes, "an insertion right below a change");
		Assert.AreEqual("1\n2\n3\n4x\n5\n", Text(Merge(Base, "1\n2\n3\n4x\n5\n", "1\n2\n3\n4x\n5\n").Bytes), "the same change on both sides");
		Assert.AreEqual("1\nours\n2\n3\n4\n5\ntheirs\n", Text(Merge(Base, "1\nours\n2\n3\n4\n5\n", "1\n2\n3\n4\n5\ntheirs\n").Bytes));
		Assert.AreEqual("2\n3\n4\n", Text(Merge(Base, "2\n3\n4\n5\n", "1\n2\n3\n4\n").Bytes), "removals at both ends");

		MergeResult Conflict = Merge(Base, "1\n2\n3 ours\n4\n5\n", "1\n2\n3 theirs\n4\n5\n");
		Assert.HasCount(1, Conflict.Conflicts);
		Assert.AreEqual(new MergeConflict(3, 1, "3 ours", "3 theirs"), Conflict.Conflicts[0]);
	}

	[TestMethod]
	public void AGivenMatchOfTheirsTakesThePlaceOfAnEditScript()
	{
		// Theirs removes one of two 'a' lines: an edit script takes the second, one line from ours' change; the given match
		// says the first, next to it.
		const string Base = "x\na\na\ny\n";
		Assert.AreEqual("X\na\ny\n", Text(Merge(Base, "X\na\na\ny\n", "x\na\ny\n").Bytes));
		Assert.IsNull(ThreeWayMerge.Merge(Bytes(Base), Bytes("X\na\na\ny\n"), Bytes("x\na\ny\n"), [0, -1, 1, 2]).Bytes);

		// A match that changes a line it says is kept, or keeps lines out of order, gives no bytes.
		Assert.IsNotNull(ThreeWayMerge.Merge(Bytes(Base), Bytes(Base), Bytes("x\nb\ny\n"), [0, 1, -1, 2]).Problem);
		Assert.IsNotNull(ThreeWayMerge.Merge(Bytes(Base), Bytes(Base), Bytes("x\na\ny\n"), [0, 1, 1, 2]).Problem);
	}

	[TestMethod]
	public void OursCanBeLinedUpFromTheEndAndChangesThatOnlyTouchPutInOrder()
	{
		// Ours adds a second 'b': as early as it can be kept, the base's 'b' is the first, and the new one is after it, one line
		// from theirs' change of 'a'; as late, the base's 'b' is the second, and the new one is right after 'a'.
		const string Base = "a\nb\n";
		Assert.AreEqual("A\nb\nb\n", Text(Merge(Base, "a\nb\nb\n", "A\nb\n").Bytes));
		Assert.IsNull(ThreeWayMerge.Merge(Bytes(Base), Bytes("a\nb\nb\n"), Bytes("A\nb\n"), oursFromEnd: true).Bytes);
		Assert.AreEqual("A\nb\nb\n", Text(ThreeWayMerge.Merge(Bytes(Base), Bytes("a\nb\nb\n"), Bytes("A\nb\n"), oursFromEnd: true, touchingMerges: true).Bytes));

		// Both inserting at one place is no order: a conflict, even so.
		Assert.IsNull(ThreeWayMerge.Merge(Bytes(Base), Bytes("a\nours\nb\n"), Bytes("a\ntheirs\nb\n"), touchingMerges: true).Bytes);
		// A change that overlaps another is a conflict, even so.
		Assert.IsNull(ThreeWayMerge.Merge(Bytes("1\n2\n3\n"), Bytes("1\n2x\n3x\n"), Bytes("1\n2y\n3\n"), touchingMerges: true).Bytes);
		// One that starts where the other ends follows it.
		Assert.AreEqual("1\n2y\n3x\n", Text(ThreeWayMerge.Merge(Bytes("1\n2\n3\n"), Bytes("1\n2\n3x\n"), Bytes("1\n2y\n3\n"), touchingMerges: true).Bytes));
		Assert.AreEqual("1\nnew\n2y\n3\n", Text(ThreeWayMerge.Merge(Bytes("1\n2\n3\n"), Bytes("1\nnew\n2\n3\n"), Bytes("1\n2y\n3\n"), touchingMerges: true).Bytes));
	}

	[TestMethod]
	public void ALineEndIsPartOfTheLastLineButItsKindIsNot()
	{
		// Adding the missing line end at the end is a change of the last line: it conflicts with a change there.
		Assert.IsNull(Merge("a\nb", "a\nb\n", "a\nc").Bytes);
		// A CRLF-for-LF change alone is no change: the other side's change merges.
		Assert.AreEqual("a\r\nb\r\nc\r\nX\r\n", Text(Merge("a\nb\nc\nd\n", "a\r\nb\r\nc\r\nd\r\n", "a\nb\nc\nX\n").Bytes));
	}

	[TestMethod]
	public void SidesThatDifferInTooManyLinesAreNotMerged()
	{
		string Base = string.Concat(Enumerable.Range(0, ThreeWayMerge.MaxDifferences + 10).Select(Index => $"line {Index}\n"));
		string Rewritten = string.Concat(Enumerable.Range(0, ThreeWayMerge.MaxDifferences + 10).Select(Index => $"other {Index}\n"));

		MergeResult Result = Merge(Base, Rewritten, Base.Replace("line 5\n", "line five\n", StringComparison.Ordinal));

		Assert.IsNull(Result.Bytes);
		Assert.IsNotNull(Result.Problem);

		// Only inserting, however many lines, needs no search: it merges.
		string Added = string.Concat(Enumerable.Range(0, ThreeWayMerge.MaxDifferences + 10).Select(Index => $"new {Index}\n"));
		MergeResult Inserted = Merge(Base, Added + Base, Base.Replace("line 5\n", "line five\n", StringComparison.Ordinal));
		Assert.AreEqual(Added + Base.Replace("line 5\n", "line five\n", StringComparison.Ordinal), Text(Inserted.Bytes));
	}

	[TestMethod]
	public void TheEditScriptKeepsALongestCommonSubsequence()
	{
		Random Random = new(4);
		for (int Round = 0; Round < 400; Round++)
		{
			int[] A = [.. Enumerable.Range(0, Random.Next(0, 30)).Select(_ => Random.Next(0, 4))];
			int[] B = [.. Enumerable.Range(0, Random.Next(0, 30)).Select(_ => Random.Next(0, 4))];

			List<(int A, int B)> Pairs = ThreeWayMerge.Matches(A, B)!;

			Assert.HasCount(LongestCommon(A, B), Pairs, $"round {Round}");
			for (int Index = 0; Index < Pairs.Count; Index++)
			{
				Assert.AreEqual(A[Pairs[Index].A], B[Pairs[Index].B]);
				Assert.IsTrue(Index == 0 || (Pairs[Index].A > Pairs[Index - 1].A && Pairs[Index].B > Pairs[Index - 1].B), $"round {Round}: in order");
			}
		}
	}

	static int LongestCommon(int[] a, int[] b)
	{
		int[,] Table = new int[a.Length + 1, b.Length + 1];
		for (int I = a.Length - 1; I >= 0; I--)
		{
			for (int J = b.Length - 1; J >= 0; J--)
			{
				Table[I, J] = a[I] == b[J] ? Table[I + 1, J + 1] + 1 : Math.Max(Table[I + 1, J], Table[I, J + 1]);
			}
		}
		return Table[0, 0];
	}
}
