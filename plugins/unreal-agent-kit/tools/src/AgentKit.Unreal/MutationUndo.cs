// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text;
using AgentKit.Locking;

namespace AgentKit.Unreal;

/// <summary>
/// For <c>uak mutate -restore -accept</c>: what to do with a file that holds neither its original bytes nor the mutation's
/// (an edit made over the mutant, while the check ran or since), which may well have kept the mutated lines. It takes the
/// mutation out only where it is pinned and the edit left the lines around it alone, and refuses the file otherwise; -force
/// then puts the original back, saving the edit. The file is merged three ways (<see cref="ThreeWayMerge"/>): the base is the mutant (the patch applied to the
/// journal's copy of the original with uak's applier, which applies a hunk only at the line its header names), "ours" the
/// file as it is, "theirs" the original, matched with the mutant line for line except where the patch changed it (the
/// patch's own places, each narrowed to the lines that differ, not an edit script's guess among like lines). The edit is
/// lined up with the mutant twice, keeping lines as early and as late as a shortest edit script can. The file gets the
/// merge (the edit kept, the mutated lines as they were) only when all of these hold:
/// <list type="bullet">
/// <item>one of the two merges is clean: the edit and the mutation change different lines, with an unchanged line between;</item>
/// <item>no place is changed by both alike (the edit took the mutation out by hand, or wrote the original's lines next to
/// it): the merge would take that for one change, and uak can't tell which of the two it was;</item>
/// <item>each change of the patch is pinned (the anchor rule): its mutated lines (for a change that only removed lines,
/// the place they were) with the line before and after them, grown upward until the block occurs once in the mutant and,
/// on its own, downward until it does (<see cref="FindAnchor"/>), are in the file once, intact, and the merge's alignment
/// keeps every line of that block as the line it is there: the edit changed none of them and put no line among them. The
/// merge must be exactly the file with each change's mutated lines, there, replaced by the original's. An edit that moves
/// the mutated function, swaps it with one alike, or changes a line around the mutation is refused: the merge may read it
/// as a shorter edit of other lines (two signatures swapped, not two functions) and take the mutation out of the wrong one;</item>
/// <item>the strict near-edit rule (<see cref="FindNearEdit"/>), checked before the rest of the anchor rule: the edit changed,
/// copied, moved or deleted no line of each change's anchor or of its neighbours (one line each side, or the file's start
/// or end). The anchor with its neighbours is in the file exactly once, byte for byte as in the mutant (a line end's '\r'
/// aside only on the lines the patch added and in a file with one kind of line end), and nowhere else with the '\r's aside;
/// both alignments keep its lines in place; none of its lines (but one of punctuation alone other than a brace) is in the
/// file more often than in the mutant; and no line
/// the change pins (a mutated line, or the line before and after lines it only removed) is like another of its lines,
/// which an edit could swap it with without changing a byte. Its refusal names the lines the edit touched and offers
/// -force;</item>
/// <item>for each place the patch changed, its mutated lines with the mutant's line before and after them (one each, where
/// there is one) are in the merge no more often than in the original, and so is each of its non-blank mutated lines on
/// its own (white space aside; a line of code also counts in any line that starts with it, as the mutated line edited in
/// place does): an edit that copied the mutated code (or wrote it anew) may have drawn the alignment to the copy. This
/// refuses an edit that adds such code of its own too;</item>
/// <item>the other merge gives the same bytes, changes that only touch put in order, and its alignment keeps every line of
/// each block and puts each change where the merge's does (<see cref="FindGuessedAnchor"/>): where the two disagree, the
/// result rests on which of several like lines is the edit's, a guess.</item>
/// </list>
/// A file whose merge equals it as it is (the patch changes no line of it) is kept. A file the patch created or deleted is
/// never kept once edited, one deleted since is kept (it holds no mutation), and anything uak can't read is refused. What
/// no comparison of bytes can tell is left: an edit that removes lines around the mutation and writes the very same bytes
/// back where they were (so the file reads, around each anchor, as the mutant did) reads as one that left them alone, and
/// the mutation is taken out of where they now are, the right result for that reading.
/// </summary>
internal static class MutationUndo
{
	/// <summary>The check for one journal's files, from the copy of the patch it kept; a journal without a usable copy refuses every file.</summary>
	public static Func<int, MutationFile, byte[]?, MutationAcceptCheck> For(MutationJournal journal)
	{
		List<DiffFile>? Files = null;
		string? Problem = null;
		try
		{
			Files = UnifiedDiff.Parse(UnifiedDiff.Decode(journal.ReadPatchCopy()));
			if (Files.Count != journal.Record.Files.Count || Files.Where((File, Index) => !EndsWith(journal.Record.Files[Index].Path, File.Path)).Any())
			{
				Problem = "uak can't check whether the mutation is still in it: the journal's copy of the patch doesn't list the files the journal does";
			}
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException or PatchException)
		{
			Problem = $"uak can't check whether the mutation is still in it: {Exception.Message}";
		}
		return (Index, Entry, Current) =>
		{
			if (Problem is not null)
			{
				return Refuse(Problem);
			}
			byte[]? Original;
			try
			{
				Original = journal.ReadOriginal(Entry);
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
			{
				return Refuse($"uak can't check whether the mutation is still in it: {Exception.Message}");
			}
			return Check(Files![Index], Original, Current);
		};
	}

	/// <summary>The verdict on a file the patch <paramref name="file"/> touched, from its bytes before the patch and now (null: none).</summary>
	internal static MutationAcceptCheck Check(DiffFile file, byte[]? original, byte[]? current)
	{
		ArgumentNullException.ThrowIfNull(file);
		if (file.IsNew)
		{
			return Refuse("the patch created it, and it was edited since: uak can't tell the edit from the patch");
		}
		if (file.IsDeleted)
		{
			return Refuse("the patch deleted it, and it is back: uak can't tell the edit from the original");
		}
		if (current is null)
		{
			return new MutationAcceptCheck(MutationAcceptAction.Keep, null, "deleted since: the mutation can't be in a file that is gone");
		}
		byte[]? Mutant;
		try
		{
			Mutant = UnifiedDiff.Apply(file, original);
		}
		catch (PatchException Exception)
		{
			return Refuse($"uak can't make the mutant again from the original, to merge the edit with ({Exception.Message})");
		}
		List<string> MutantLines = Contents(Mutant!);
		List<string> OriginalLines = Contents(original!);
		List<Change>? Changes = Narrow(FindChanges(file), Keys(Mutant!), Keys(original!));
		int[]? Match = Changes is null ? null : MatchMutant(Changes, MutantLines.Count, OriginalLines.Count);
		if (Match is null)
		{
			return Refuse("uak can't line the patch's changes up with the mutant it makes from the original, to merge the edit with");
		}
		// The edit is lined up with the mutant twice, keeping lines as early and as late as a shortest edit script can: the merge
		// needs one of the two clean, and the other the same once changes that only touch are put in order (or clean too).
		MergeResult Early = ThreeWayMerge.Merge(Mutant!, current, original!, Match);
		MergeResult Late = ThreeWayMerge.Merge(Mutant!, current, original!, Match, oursFromEnd: true);
		bool EarlyFirst = Early.Bytes is not null || Late.Bytes is null;
		MergeResult Merged = EarlyFirst ? Early : Late;
		if (Merged.Bytes is null)
		{
			return Refuse(Merged.Problem is not null
				? $"uak can't merge it: {Merged.Problem}"
				: "the later edit and the mutation change the same or adjacent lines, so a merge can't tell them apart: " + Describe(Merged.Conflicts));
		}
		if (Merged.SameChanges.Count > 0 || Merged.BothSides > 0)
		{
			return Refuse("the later edit changes lines the mutation changed, or lines next to them, the way taking the mutation out does (it was taken"
				+ " out by hand, or the edit wrote the original's lines there), so uak can't tell the edit from the mutation: " + Describe(Merged.SameChanges));
		}
		// Each change is pinned to the block of the mutant around it that occurs there once: the merge may take the mutation out
		// only of that block, found once in the file, intact, and kept in place by the edit (it changed no line of it).
		List<string> CurrentKeys = Keys(current);
		List<string> FramedMutant = Frame(Keys(Mutant!));
		List<string> FramedCurrent = Frame(CurrentKeys);
		List<Anchor> Anchors = [.. Changes!.Select(Change => FindAnchor(FramedMutant, Change))];
		string? Near = FindNearEdit(Changes!, Anchors, FramedMutant, FramedCurrent, MutantLines, [Early.OursMatch, Late.OursMatch],
			SameLines(FindChanges(file), Mutant!, current, FramedMutant, FramedCurrent));
		if (Near is not null)
		{
			return Refuse(Near);
		}
		string? Unanchored = PlaceAnchors(Changes!, Anchors, FramedMutant, FramedCurrent, MutantLines, Merged.OursMatch, out List<int> Places);
		if (Unanchored is not null)
		{
			return Refuse(Unanchored);
		}
		if (!Keys(Merged.Bytes).SequenceEqual(Undo(Changes!, Places, CurrentKeys, Keys(original!)), StringComparer.Ordinal))
		{
			return Refuse("the merge doesn't take the mutation out of the lines around each of the patch's changes alone, as they are in the file");
		}
		string? Copied = FindCopiedChange(Changes!, MutantLines, OriginalLines, Contents(Merged.Bytes));
		if (Copied is not null)
		{
			return Refuse($"after a merge, {Copied}: the edit may have copied the mutation, or written it anew, and the merge taken the copy out instead");
		}
		MergeResult Other = EarlyFirst ? Late : Early;
		if (Other.Bytes is null)
		{
			Other = ThreeWayMerge.Merge(Mutant!, current, original!, Match, oursFromEnd: EarlyFirst, touchingMerges: true);
		}
		if (Other.Bytes is null || Other.BothSides > 0 || !Other.Bytes.AsSpan().SequenceEqual(Merged.Bytes))
		{
			return Refuse("the later edit's lines are like the lines around the mutation, so where they go is a guess: two equally short alignments of the"
				+ " file with the mutant merge differently");
		}
		string? Guessed = FindGuessedAnchor(Changes!, Anchors, Merged.OursMatch!, Other.OursMatch, MutantLines);
		if (Guessed is not null)
		{
			return Refuse(Guessed);
		}
		return Merged.Bytes.AsSpan().SequenceEqual(current)
			? new MutationAcceptCheck(MutationAcceptAction.Keep, null, "a three-way merge with the original changes nothing: the patch changes no line of it")
			: new MutationAcceptCheck(MutationAcceptAction.Undo, Merged.Bytes, "a three-way merge of the mutant, the file and the original: the edit is kept,"
				+ " and the lines the mutation changed are as they were");
	}

	/// <summary>
	/// One place a patch changed: a run of '-' and '+' lines in a hunk, between context lines. <paramref name="MutantStart"/>
	/// to <paramref name="MutantEnd"/> are its '+' lines in the mutant (an empty range where it only removed lines: the
	/// place they were), <paramref name="OriginalStart"/> to <paramref name="OriginalEnd"/> its '-' lines in the original;
	/// 0-based, the ends excluded.
	/// </summary>
	internal readonly record struct Change(int MutantStart, int MutantEnd, int OriginalStart, int OriginalEnd);

	/// <summary>
	/// The places the patch changed, from its hunks' headers and bodies, in order. uak's applier applies each hunk exactly at
	/// the line its old start names (and checks its new start names the same place), so these are where the mutant has them.
	/// </summary>
	internal static List<Change> FindChanges(DiffFile file)
	{
		List<Change> Result = [];
		foreach (DiffHunk Hunk in file.Hunks)
		{
			int OriginalAt = Hunk.OldLines.Count == 0 ? Hunk.OldStart : Hunk.OldStart - 1;
			int MutantAt = Hunk.NewLines.Count == 0 ? Hunk.NewStart : Hunk.NewStart - 1;
			int? MutantStart = null;
			int OriginalStart = 0;
			foreach (string Line in Hunk.Body)
			{
				if (Line[0] == ' ')
				{
					if (MutantStart is int Start)
					{
						Result.Add(new Change(Start, MutantAt, OriginalStart, OriginalAt));
						MutantStart = null;
					}
					MutantAt++;
					OriginalAt++;
					continue;
				}
				if (MutantStart is null)
				{
					MutantStart = MutantAt;
					OriginalStart = OriginalAt;
				}
				if (Line[0] == '+')
				{
					MutantAt++;
				}
				else
				{
					OriginalAt++;
				}
			}
			if (MutantStart is int Last)
			{
				Result.Add(new Change(Last, MutantAt, OriginalStart, OriginalAt));
			}
		}
		return Result;
	}

	/// <summary>
	/// The changes, each without the lines at its start and end that the mutant and the original hold alike (a patch that
	/// removes a line and adds it again changes only what is between), and without a change left with none; compared by
	/// <see cref="ThreeWayMerge.TextLine.Key"/>. Null when a change lies outside the files.
	/// </summary>
	static List<Change>? Narrow(List<Change> changes, List<string> mutant, List<string> original)
	{
		List<Change> Result = [];
		foreach (Change Change in changes)
		{
			(int MutantStart, int MutantEnd, int OriginalStart, int OriginalEnd) = Change;
			if (MutantStart < 0 || OriginalStart < 0 || MutantEnd > mutant.Count || OriginalEnd > original.Count)
			{
				return null;
			}
			while (MutantStart < MutantEnd && OriginalStart < OriginalEnd && mutant[MutantStart] == original[OriginalStart])
			{
				MutantStart++;
				OriginalStart++;
			}
			while (MutantStart < MutantEnd && OriginalStart < OriginalEnd && mutant[MutantEnd - 1] == original[OriginalEnd - 1])
			{
				MutantEnd--;
				OriginalEnd--;
			}
			if (MutantStart < MutantEnd || OriginalStart < OriginalEnd)
			{
				Result.Add(new Change(MutantStart, MutantEnd, OriginalStart, OriginalEnd));
			}
		}
		return Result;
	}

	/// <summary>A file's lines as the merge compares them.</summary>
	static List<string> Keys(byte[] bytes) => [.. ThreeWayMerge.Split(bytes).Lines.Select(Line => Line.Key)];

	/// <summary>
	/// For each mutant line, the original's line it is (-1 for a line the patch added or changed): every line outside the
	/// patch's changes, in order. Null when the changes don't add up to both files' line counts.
	/// </summary>
	static int[]? MatchMutant(List<Change> changes, int mutantCount, int originalCount)
	{
		int[] Result = new int[mutantCount];
		int Mutant = 0;
		int Original = 0;
		foreach (Change Change in changes)
		{
			if (Change.MutantStart < Mutant || Change.MutantStart - Mutant != Change.OriginalStart - Original || Change.MutantEnd > mutantCount)
			{
				return null;
			}
			for (; Mutant < Change.MutantStart; Mutant++, Original++)
			{
				Result[Mutant] = Original;
			}
			for (; Mutant < Change.MutantEnd; Mutant++)
			{
				Result[Mutant] = -1;
			}
			Original = Change.OriginalEnd;
		}
		if (mutantCount - Mutant != originalCount - Original)
		{
			return null;
		}
		for (; Mutant < mutantCount; Mutant++, Original++)
		{
			Result[Mutant] = Original;
		}
		return Result;
	}

	/// <summary>
	/// The block of the mutant a change is pinned to: lines <paramref name="From"/> to <paramref name="To"/> (the end excluded)
	/// of the mutant framed by its start and its end (<see cref="Frame"/>: 0 is the file's start, 1 its first line).
	/// </summary>
	internal readonly record struct Anchor(int From, int To);

	/// <summary>A file's line keys between two marks no line has, its start and its end, so a block can reach either.</summary>
	static List<string> Frame(List<string> keys) => ["\n<start>", .. keys, "\n<end>"];

	/// <summary>
	/// A change's anchor in the framed mutant: its lines with the line before and after them (the file's start or end where
	/// there is none), grown upward until the block occurs once in the mutant, and, on its own, grown downward until it occurs
	/// once; the anchor spans both. The smallest block that occurs once is not enough: where two places differ in the line
	/// above them and in the line below (two functions alike but for their signatures, each followed by something else), a
	/// block grown one way may be the other place once the edit swaps the two, with the mutation then under the other
	/// signature; a block that occurs once grown each way on its own spans what tells the two apart on both sides.
	/// </summary>
	static Anchor FindAnchor(List<string> mutant, Change change)
	{
		// The change's lines are mutant[MutantStart + 1, MutantEnd + 1) framed; one line more each side.
		int From = change.MutantStart;
		int To = change.MutantEnd + 2;
		return new Anchor(Grow(mutant, From, To, upward: true), Grow(mutant, From, To, upward: false));
	}

	/// <summary>
	/// Grows the block <paramref name="from"/> to <paramref name="to"/> of <paramref name="lines"/> one line at a time, upward or
	/// downward, until it occurs there once; returns its new start (upward) or end. It ends there at the latest when the block
	/// reaches the file's start or end mark, each of which occurs once.
	/// </summary>
	static int Grow(List<string> lines, int from, int to, bool upward)
	{
		// Where the block starts, for each place it occurs; its own place is always one.
		List<int> Starts = [.. Enumerable.Range(0, lines.Count - (to - from) + 1).Where(Start => Same(lines, Start, lines, from, to - from))];
		while (Starts.Count > 1)
		{
			if (upward)
			{
				from--;
				Starts = [.. Starts.Where(Start => Start > 0 && lines[Start - 1].Equals(lines[from], StringComparison.Ordinal)).Select(Start => Start - 1)];
			}
			else
			{
				to++;
				int Length = to - from;
				Starts = [.. Starts.Where(Start => Start + Length <= lines.Count && lines[Start + Length - 1].Equals(lines[to - 1], StringComparison.Ordinal))];
			}
		}
		return upward ? from : to;
	}

	/// <summary>Whether <paramref name="length"/> lines of <paramref name="a"/> from <paramref name="aStart"/> are those of <paramref name="b"/> from <paramref name="bStart"/>.</summary>
	static bool Same(List<string> a, int aStart, List<string> b, int bStart, int length)
	{
		for (int Line = 0; Line < length; Line++)
		{
			if (!a[aStart + Line].Equals(b[bStart + Line], StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// The strict near-edit rule: whether the edit changed, copied, moved or deleted a line of a change's anchor or of its
	/// neighbours (the line before the anchor and the line after it, or the file's start or end where the anchor reaches it).
	/// Judged by comparing the framed mutant (<paramref name="mutant"/>) and the framed file as it is (<paramref name="current"/>)
	/// around the anchor: the anchor with its neighbours must be in the file once, line for line as in the mutant, byte for byte
	/// but for a line end's '\r' where the file's line ends allow it (<paramref name="sameLine"/>: <see cref="SameLines"/>),
	/// and nowhere else even with the '\r's aside (<see cref="ThreeWayMerge.TextLine.Key"/>); both shortest alignments of the
	/// file with the mutant (<paramref name="alignments"/>, the earliest and the latest) must keep each of those lines as the
	/// line it is there (an edit that moved like lines across them, or removed one and wrote it again, is a move, even where
	/// the bytes come out the same); and no line of the anchor or its neighbours (the mutated lines among them, but not a line
	/// of punctuation alone other than a brace: <see cref="IsTrivial"/>) may be in the file more often than in the mutant (a
	/// copy). And as a move no comparison can see is a move all the same: no line the
	/// change pins (a mutated line, or for a change that only removed lines the line before and after where they were) may be
	/// like another line of the anchor or its neighbours, which an edit could swap it with without changing a byte. The rule
	/// only adds refusals: what it lets through still goes through the other checks. Returns why not, naming the lines the
	/// edit touched, or null.
	/// </summary>
	static string? FindNearEdit(List<Change> changes, List<Anchor> anchors, List<string> mutant, List<string> current, List<string> mutantLines,
		IReadOnlyList<int>?[] alignments, Func<int, int, bool> sameLine)
	{
		if (alignments.Any(Alignment => Alignment is null))
		{
			return "uak can't line the file up with the mutant both ways, to check that the edit left the lines around the mutation alone";
		}
		List<IReadOnlyList<int>> Alignments = [.. alignments.Select(Alignment => Alignment!)];
		Dictionary<string, int> MutantCounts = CountKeys(mutant);
		Dictionary<string, int> CurrentCounts = CountKeys(current);
		for (int Index = 0; Index < changes.Count; Index++)
		{
			Anchor Anchor = anchors[Index];
			// The anchor and one line each side, framed: the file's start or end mark is the neighbour of an anchor that reaches it.
			int From = Math.Max(0, Anchor.From - 1);
			int To = Math.Min(mutant.Count, Anchor.To + 1);
			// Where the block is, byte for byte (but for the '\r's sameLine allows), and how often, with every '\r' aside.
			List<int> Found = [];
			int FoundLoosely = 0;
			for (int Start = 0; Start + (To - From) <= current.Count && FoundLoosely < 2; Start++)
			{
				if (Same(current, Start, mutant, From, To - From))
				{
					FoundLoosely++;
					if (Enumerable.Range(0, To - From).All(Line => sameLine(From + Line, Start + Line)))
					{
						Found.Add(Start);
					}
				}
			}
			List<string> Touched = [];
			foreach (IReadOnlyList<int> Alignment in Alignments.Where(_ => Touched.Count == 0))
			{
				Touched.AddRange(DescribeMoved(Alignment, From, To, mutant.Count, current.Count - 2, mutantLines));
			}
			bool Kept = Found.Count == 1 && FoundLoosely == 1 && Alignments.All(Alignment => KeepsBlock(Alignment, From, To, mutant.Count, Found[0]));
			if (!Kept && Touched.Count == 0)
			{
				Touched.Add(FoundLoosely > 1 ? "they are in it more than once"
					: Found.Count == 0 ? "they are not in it as they were (a line end of one of them changed, or a line moved among like lines with other line ends)"
					: "a shortest alignment of it with the mutant reads the edit as moving them");
			}
			// Copies count for every line but one of punctuation alone (a '};' or ');' line is in many edits), a blank line or a brace
			// alone counting (IsTrivial); the like-line check below guards swaps among such lines too.
			HashSet<string> Copied = new(StringComparer.Ordinal);
			for (int Line = Math.Max(From, 1); Line < Math.Min(To, mutant.Count - 1); Line++)
			{
				if (IsTrivial(mutantLines[Line - 1]))
				{
					continue;
				}
				int Now = CurrentCounts.GetValueOrDefault(mutant[Line]);
				if (Now > MutantCounts[mutant[Line]] && Copied.Add(mutant[Line]))
				{
					Touched.Add($"copied line {Line} ('{Cut(mutantLines[Line - 1].Trim())}': {Now} times in the file, {MutantCounts[mutant[Line]]} in the mutant)");
					Kept = false;
				}
			}
			// A line the change pins like another line of the block (a brace next to a brace, a blank line next to a blank line) can
			// trade places with it without changing a byte: the comparison can't tell whether the edit moved it.
			(int Line, int Other)? Like = Kept ? FindLikeLine(changes[Index], From, To, mutant) : null;
			if (Like is (int Pinned, int Twin))
			{
				return $"line {Pinned} of the mutant ('{Cut(mutantLines[Pinned - 1].Trim())}'), which the patch's change {Index + 1}"
					+ $" {(changes[Index].MutantEnd > changes[Index].MutantStart ? "wrote" : "removed lines next to")}, is like line {Twin} around it"
					+ $" ('{Cut(mutantLines[Twin - 1].Trim())}'), so an edit could have swapped the two without changing a byte, and uak can't tell whether it"
					+ " moved the line: -accept takes a mutation out only where it can tell the edit left the lines around it alone. -force puts the"
					+ " original back and saves the edit beside the journal, to make again on the original";
			}
			if (!Kept)
			{
				(int First, int Last) = (Math.Max(From, 1), Math.Min(To, mutant.Count - 1) - 1);
				string Lines = First > Last ? "the empty file" : First == Last ? $"line {First}" : $"lines {First}-{Last}";
				return $"the later edit touched the lines around the patch's change {Index + 1} ('{Show(changes[Index], mutantLines)}'): {string.Join("; ", Touched.Take(3))}"
					+ $"{(Touched.Count > 3 ? $"; and {Touched.Count - 3} more" : "")} (line numbers are the mutant's). -accept takes a mutation out only where"
					+ $" the edit left those lines alone: {Lines} of the mutant (the fewest around the change that occur there once, and one line each side"
					+ $"{(From == 0 ? ", the file's start" : "")}{(To == mutant.Count ? ", the file's end" : "")}) must be in the file once, as they were, none moved,"
					+ " and none of them (but a line of punctuation alone other than a brace) more often than in the mutant. -force puts the original back and saves the edit beside the journal,"
					+ " to make again on the original";
			}
		}
		return null;
	}

	/// <summary>
	/// Compares framed line m of the mutant with framed line c of the file as it is (<paramref name="framedMutant"/> and
	/// <paramref name="framedCurrent"/> are their keys, <see cref="Frame"/>d), byte for byte, but for a line end's '\r' where the
	/// file's line ends allow it: on a line the patch added (<paramref name="added"/>: its line end's kind is the applier's
	/// choice, git's or uak's, so the mutant made again may not have the one the file had), and everywhere when the file has
	/// one kind of line end only (the edit may have made it so: a file with one kind tells no lines apart by it).
	/// </summary>
	static Func<int, int, bool> SameLines(List<Change> added, byte[] mutant, byte[] current, List<string> framedMutant, List<string> framedCurrent)
	{
		List<ThreeWayMerge.TextLine> MutantLines = ThreeWayMerge.Split(mutant).Lines;
		List<ThreeWayMerge.TextLine> CurrentLines = ThreeWayMerge.Split(current).Lines;
		bool[] Loose = new bool[framedMutant.Count];
		foreach (Change Change in added)
		{
			for (int Line = Math.Max(Change.MutantStart, 0); Line < Math.Min(Change.MutantEnd, MutantLines.Count); Line++)
			{
				Loose[Line + 1] = true;
			}
		}
		bool OneKind = CurrentLines.Select(Line => Line.Eol).Where(Eol => Eol.Length > 0).Distinct(StringComparer.Ordinal).Count() <= 1;
		List<string> MutantBytes = Frame([.. MutantLines.Select(Line => Line.Content + Line.Eol)]);
		List<string> CurrentBytes = Frame([.. CurrentLines.Select(Line => Line.Content + Line.Eol)]);
		return (Mutant, Current) => OneKind || Loose[Mutant]
			? framedMutant[Mutant].Equals(framedCurrent[Current], StringComparison.Ordinal)
			: MutantBytes[Mutant].Equals(CurrentBytes[Current], StringComparison.Ordinal);
	}

	/// <summary>
	/// A line <paramref name="change"/> pins that is like another line of the framed mutant's block <paramref name="from"/> to
	/// <paramref name="to"/> (framed line numbers), or null. It pins its mutated lines (each may be like another mutated line:
	/// they go together), or, for a change that only removed lines, the line before and after where they were.
	/// </summary>
	static (int Line, int Other)? FindLikeLine(Change change, int from, int to, List<string> mutant)
	{
		bool Removed = change.MutantEnd == change.MutantStart;
		IEnumerable<int> Pinned = Removed ? [change.MutantStart, change.MutantStart + 1] : Enumerable.Range(change.MutantStart + 1, change.MutantEnd - change.MutantStart);
		foreach (int Line in Pinned.Where(Line => Line >= 1 && Line < mutant.Count - 1))
		{
			for (int Other = Math.Max(from, 1); Other < Math.Min(to, mutant.Count - 1); Other++)
			{
				bool Own = Removed ? Other == Line : Other > change.MutantStart && Other <= change.MutantEnd;
				if (!Own && mutant[Other].Equals(mutant[Line], StringComparison.Ordinal))
				{
					return (Line, Other);
				}
			}
		}
		return null;
	}

	/// <summary>Whether <paramref name="alignment"/> keeps each line of the framed mutant's block <paramref name="from"/> to <paramref name="to"/> as the line it is in the framed file's block from <paramref name="at"/>.</summary>
	static bool KeepsBlock(IReadOnlyList<int> alignment, int from, int to, int mutantCount, int at)
	{
		for (int Line = Math.Max(from, 1); Line < Math.Min(to, mutantCount - 1); Line++)
		{
			if (alignment[Line - 1] != at + (Line - from) - 1)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// What an alignment of the file with the mutant reads the edit as doing to the framed mutant's block <paramref name="from"/>
	/// to <paramref name="to"/>: lines changed or deleted, and lines put between two of them or before the first at the file's start
	/// or after the last at its end (the block reaching the start or end mark); mutant line numbers, for a message.
	/// </summary>
	static List<string> DescribeMoved(IReadOnlyList<int> alignment, int from, int to, int mutantCount, int currentCount, List<string> mutantLines)
	{
		List<string> Result = [];
		int First = Math.Max(from, 1);
		int Last = Math.Min(to, mutantCount - 1) - 1;
		for (int Line = First; Line <= Last; Line++)
		{
			string Text = $"line {Line} ('{Cut(mutantLines[Line - 1].Trim())}')";
			if (alignment[Line - 1] < 0)
			{
				Result.Add($"changed, moved or deleted {Text}");
			}
			else if (Line > First && alignment[Line - 2] >= 0 && alignment[Line - 1] != alignment[Line - 2] + 1)
			{
				Result.Add($"put lines between lines {Line - 1} and {Line}, or moved one of them");
			}
		}
		if (from == 0 && First <= Last && alignment[First - 1] > 0)
		{
			Result.Add("put lines before line 1, at the file's start");
		}
		if (to == mutantCount && First <= Last && alignment[Last - 1] >= 0 && alignment[Last - 1] != currentCount - 1)
		{
			Result.Add($"put lines after line {Last}, at the file's end");
		}
		return Result;
	}

	/// <summary>
	/// Whether the copy check leaves a line out: one of punctuation alone (no letter or digit, white space aside), but for a
	/// blank line and a brace alone ('{' or '}'). Those stay in: without them review 6's fuzzer finds kept mutations (copies
	/// of a blank line or a brace drawing the merge to the copy, in files of few distinct lines), and review 6's positional
	/// metric more wrong results, each kind alone not enough.
	/// </summary>
	static bool IsTrivial(string line)
	{
		string Trimmed = line.Trim();
		return Trimmed.Length > 0 && Trimmed != "{" && Trimmed != "}" && !Trimmed.Any(char.IsLetterOrDigit);
	}

	/// <summary>How often each line key is in <paramref name="lines"/>.</summary>
	static Dictionary<string, int> CountKeys(List<string> lines)
	{
		Dictionary<string, int> Result = new(StringComparer.Ordinal);
		foreach (string Line in lines)
		{
			Result[Line] = Result.GetValueOrDefault(Line) + 1;
		}
		return Result;
	}

	/// <summary>
	/// Finds each change's anchor in the framed file as it is (<paramref name="current"/>): it must be there once, intact, and
	/// the edit's alignment with the mutant (<paramref name="oursMatch"/>, a merge's) must keep each of its lines as the line
	/// it is there, the change's own lines among them, so the edit changed no line of it and put none inside it. Gives, for each
	/// change, the file's line where its mutated lines start (<paramref name="places"/>); returns why not, or null.
	/// </summary>
	static string? PlaceAnchors(List<Change> changes, List<Anchor> anchors, List<string> mutant, List<string> current, List<string> mutantLines,
		IReadOnlyList<int>? oursMatch, out List<int> places)
	{
		places = [];
		if (oursMatch is null)
		{
			return "uak can't line the file up with the mutant, to find the lines around the mutation in it";
		}
		for (int Index = 0; Index < changes.Count; Index++)
		{
			(Change Change, Anchor Anchor) = (changes[Index], anchors[Index]);
			int Length = Anchor.To - Anchor.From;
			List<int> Found = [];
			for (int Start = 0; Start + Length <= current.Count && Found.Count < 2; Start++)
			{
				if (Same(current, Start, mutant, Anchor.From, Length))
				{
					Found.Add(Start);
				}
			}
			string Lines = $"the mutant's lines around the patch's change {Index + 1} ('{Show(Change, mutantLines)}'; lines {Math.Max(1, Anchor.From)}-{Math.Min(mutantLines.Count, Anchor.To - 1)}"
				+ " of the mutant, the fewest around it that occur there once)";
			if (Found.Count != 1)
			{
				return $"{Lines} are {(Found.Count == 0 ? "not in it as they were: the edit changed, moved or split them" : "in it more than once")}, so uak can't be"
					+ " sure where the mutation is: it takes it out only of those lines, found once, as they were";
			}
			for (int Line = Math.Max(Anchor.From, 1); Line < Math.Min(Anchor.To, oursMatch.Count + 1); Line++)
			{
				if (oursMatch[Line - 1] != Found[0] + (Line - Anchor.From) - 1)
				{
					return $"{Lines} are in it once, as they were, but a shortest alignment of it with the mutant reads the edit as changing them (it moved"
						+ " other lines across them, or wrote them again elsewhere), so uak can't be sure where the mutation is";
				}
			}
			places.Add(Found[0] + (Change.MutantStart + 1 - Anchor.From) - 1);
		}
		return null;
	}

	/// <summary>
	/// Whether the other equally short alignment of the file with the mutant (<paramref name="other"/>) reads the edit as
	/// touching what a change is pinned to, where the merge's (<paramref name="merged"/>) doesn't: it removes a line of the
	/// change's anchor (one of several like lines, say), or puts the change elsewhere (its mutated lines, or, for a change that
	/// only removed lines, the two lines around where they were, which must stay next to each other). The merges agree on the
	/// bytes, but where the edit went is a guess, and the restored lines' place rests on it. It may read lines the edit added as
	/// put between the mutated lines and the anchor's others (a function added after the mutated one, both ending in '}'), as
	/// that changes none of them. Returns why, or null.
	/// </summary>
	static string? FindGuessedAnchor(List<Change> changes, List<Anchor> anchors, IReadOnlyList<int> merged, IReadOnlyList<int>? other, List<string> mutantLines)
	{
		if (other is null)
		{
			return "uak can't line the file up with the mutant both ways, to check where the edit went";
		}
		for (int Index = 0; Index < changes.Count; Index++)
		{
			Change Change = changes[Index];
			int From = Change.MutantEnd > Change.MutantStart ? Change.MutantStart : Math.Max(0, Change.MutantStart - 1);
			int To = Change.MutantEnd > Change.MutantStart ? Change.MutantEnd : Math.Min(merged.Count, Change.MutantStart + 1);
			bool Moved = Enumerable.Range(From, To - From).Any(Line => merged[Line] != other[Line]);
			// The anchor's lines, framed (0 is the file's start): each must be kept as some line of the file.
			bool Removed = Enumerable.Range(Math.Max(anchors[Index].From, 1), Math.Min(anchors[Index].To, merged.Count + 1) - Math.Max(anchors[Index].From, 1))
				.Any(Line => other[Line - 1] < 0);
			if (Moved || Removed)
			{
				return $"two equally short alignments of the file with the mutant disagree on the lines around the patch's change {Index + 1}"
					+ $" ('{Show(Change, mutantLines)}'): one reads the edit as removing or moving one of them (one of several like lines, say), so where"
					+ " the mutation is is a guess";
			}
		}
		return null;
	}

	/// <summary>The file's line keys with each change's mutated lines, at its place, replaced by the original's lines for it.</summary>
	static List<string> Undo(List<Change> changes, List<int> places, List<string> current, List<string> original)
	{
		List<string> Result = [];
		int At = 0;
		for (int Index = 0; Index < changes.Count; Index++)
		{
			Change Change = changes[Index];
			Result.AddRange(current.Skip(At).Take(places[Index] - At));
			Result.AddRange(original.Skip(Change.OriginalStart).Take(Change.OriginalEnd - Change.OriginalStart));
			At = places[Index] + Change.MutantEnd - Change.MutantStart;
		}
		Result.AddRange(current.Skip(At));
		return Result;
	}

	/// <summary>A change, for a message: its first non-blank mutated line, or, for one that only removed lines, the first of the lines around it.</summary>
	static string Show(Change change, List<string> mutant)
	{
		int From = Math.Max(0, change.MutantStart - 1);
		int To = Math.Min(mutant.Count, change.MutantEnd + 1);
		return Cut(mutant.Skip(change.MutantStart).Take(change.MutantEnd - change.MutantStart).Select(Line => Line.Trim()).FirstOrDefault(Line => Line.Length > 0)
			?? mutant.Skip(From).Take(To - From).Select(Line => Line.Trim()).FirstOrDefault(Line => Line.Length > 0) ?? "");
	}

	/// <summary>Places of a merge, for a message: where each is in the file as it is now, and the first line each side has there.</summary>
	static string Describe(IReadOnlyList<MergeConflict> places)
	{
		return string.Join("; ", places.Take(3).Select(Describe)) + (places.Count > 3 ? $"; and {places.Count - 3} more" : "");
	}

	static string Describe(MergeConflict place)
	{
		string Where = place.OursCount switch
		{
			0 => $"before line {place.OursLine} (the edit removed lines there)",
			1 => $"line {place.OursLine}",
			_ => $"lines {place.OursLine}-{place.OursLine + place.OursCount - 1}",
		};
		return $"{Where} as it is now{(place.Ours.Length > 0 ? $" ('{place.Ours}')" : "")}, which the original has as"
			+ (place.Theirs.Length > 0 ? $" '{place.Theirs}'" : " nothing");
	}

	/// <summary>
	/// Where the merge may still hold the mutation, or null: a place the patch changed whose mutated lines, with the mutant's
	/// line before and after them (one each, where there is one; for a place that only removed lines, the two lines around
	/// it), occur in <paramref name="merged"/> more often than in <paramref name="original"/>. The merge took the mutation out
	/// of one such block: one more, beyond the original's own, may be the mutation left in place.
	/// </summary>
	static string? FindCopiedChange(List<Change> changes, List<string> mutant, List<string> original, List<string> merged)
	{
		List<string> OriginalSqueezed = [.. original.Select(Squeeze)];
		List<string> MergedSqueezed = [.. merged.Select(Squeeze)];
		foreach ((Change Change, int Number) in changes.Select((Change, Index) => (Change, Index + 1)))
		{
			int From = Math.Max(0, Change.MutantStart - 1);
			int To = Math.Min(mutant.Count, Change.MutantEnd + 1);
			List<string> Block = mutant.GetRange(From, To - From);
			if (Count(merged, Block) > Count(original, Block))
			{
				return $"the mutated lines of the patch's change {Number}, with the lines around them ('{Show(Change, mutant)}'), are in it more often than in the original";
			}
			foreach (string Line in mutant.Skip(Change.MutantStart).Take(Change.MutantEnd - Change.MutantStart))
			{
				string Key = Squeeze(Line);
				if (Key.Length == 0)
				{
					// A blank line is no code: the block above covers its place.
					continue;
				}
				bool Code = Key.Any(char.IsLetterOrDigit);
				bool Holds(string Other) => Code ? Other.StartsWith(Key, StringComparison.Ordinal) : Other.Equals(Key, StringComparison.Ordinal);
				if (MergedSqueezed.Count(Holds) > OriginalSqueezed.Count(Holds))
				{
					return $"{(Code ? "lines that hold" : "the line")} '{Cut(Line.Trim())}', which the patch's change {Number} wrote, are in it more often than in the original";
				}
			}
		}
		return null;
	}

	/// <summary>A line without its white space, so a mutated line edited in place (re-spaced, or given a comment) still holds it.</summary>
	static string Squeeze(string line) => string.Concat(line.Where(Character => !char.IsWhiteSpace(Character)));

	/// <summary>A line cut short for a message.</summary>
	static string Cut(string line) => line.Length > 80 ? line[..77] + "..." : line;

	/// <summary>A file's lines without their line ends or its byte-order mark, read as UTF-8.</summary>
	static List<string> Contents(byte[] bytes)
	{
		return [.. ThreeWayMerge.Split(bytes).Lines.Select(Line => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(Line.Content)))];
	}

	/// <summary>How often <paramref name="block"/> occurs in <paramref name="lines"/>, line for line (overlapping counted); 0 for an empty block.</summary>
	static int Count(List<string> lines, List<string> block)
	{
		if (block.Count == 0)
		{
			return 0;
		}
		int Result = 0;
		for (int At = 0; At + block.Count <= lines.Count; At++)
		{
			int Line = 0;
			while (Line < block.Count && lines[At + Line].Equals(block[Line], StringComparison.Ordinal))
			{
				Line++;
			}
			if (Line == block.Count)
			{
				Result++;
			}
		}
		return Result;
	}

	/// <summary>Whether a full path ends with a patch's relative path (its '/' parts), ignoring case.</summary>
	static bool EndsWith(string fullPath, string relative)
	{
		string[] Full = fullPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
		string[] Relative = relative.Split('/');
		return Full.Length >= Relative.Length && Full[^Relative.Length..].SequenceEqual(Relative, StringComparer.OrdinalIgnoreCase);
	}

	static MutationAcceptCheck Refuse(string why) => new(MutationAcceptAction.Refuse, null, why);
}
