// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text;

namespace AgentKit.Unreal;

/// <summary>A place where a three-way merge's two sides change the same or adjacent lines of the base, each otherwise.</summary>
/// <param name="OursLine">The first line "ours" has there, 1-based; where it has none, the line after the place.</param>
/// <param name="OursCount">How many lines "ours" has there (0: it removed the base's lines there).</param>
/// <param name="Ours">The first non-blank line "ours" has there, trimmed ("" when none).</param>
/// <param name="Theirs">The first non-blank line "theirs" has there, trimmed ("" when none).</param>
internal sealed record MergeConflict(int OursLine, int OursCount, string Ours, string Theirs);

/// <summary>A three-way merge's result: the merged bytes, or why there are none.</summary>
/// <param name="Bytes">The merged bytes; null when the sides conflict, or differ too much to be merged.</param>
/// <param name="Conflicts">Where the sides conflict.</param>
/// <param name="Problem">Why there are no bytes when it isn't a conflict (the sides differ from the base in too many lines).</param>
/// <param name="BothSides">How many places both sides changed, the same way (merged) or not (conflicts).</param>
internal sealed record MergeResult(byte[]? Bytes, IReadOnlyList<MergeConflict> Conflicts, string? Problem = null, int BothSides = 0)
{
	/// <summary>The places both sides changed the same way, which the merge took as one change (as git merge-file does).</summary>
	public IReadOnlyList<MergeConflict> SameChanges { get; init; } = [];

	/// <summary>
	/// For each base line, the "ours" line the merge took it to be kept as, or -1 where "ours" changed it: the edit script it
	/// merged by. Null when there was none (a side differs too much, or the given match of "theirs" is wrong).
	/// </summary>
	public IReadOnlyList<int>? OursMatch { get; init; }
}

/// <summary>
/// A line-based three-way merge (diff3), for <c>uak mutate -restore -accept</c>: the changes from a base to "ours" and from
/// the base to "theirs", each a shortest edit script (Myers; for "theirs", or the lines the caller says it changed), are
/// put together where they change different lines of the base, with at least one unchanged line between them. Where they
/// change the same lines, or adjacent ones, it is a conflict, unless both sides hold the same lines there (as git merge-file
/// resolves an identical change; such places are listed, for a caller that refuses them). A merge either has no conflict,
/// or gives no bytes. Among equally short edit scripts for "ours", one keeps lines as early as it can, the other as late
/// (<c>oursFromEnd</c>): a caller that merges both ways can tell a result that rests on that choice.
/// <para>
/// Lines are compared by their bytes without their line end, a '\r' before the '\n' aside, but with whether they have one
/// (a last line without a line end differs from the same line with one); a UTF-8 byte-order mark is not part of the first
/// line. Any bytes merge (the lines are read one char per byte). The merged lines keep their own bytes: a line from
/// "ours" as it is there; a line from "theirs" with the line end "ours" uses, when "ours" uses one kind, else its own. The
/// result has a byte-order mark when "ours" has one.
/// </para>
/// </summary>
internal static class ThreeWayMerge
{
	/// <summary>
	/// The most lines one side may differ from the base by (inserted plus removed) between the first and the last line it
	/// changes, unless it only inserts or only removes there: the edit script's search keeps one row per difference, so a
	/// bigger rewrite is refused rather than merged.
	/// </summary>
	public const int MaxDifferences = 2000;

	/// <summary>One line: its bytes without the line end (one char per byte), its line end ("\r\n", "\n" or "" for a last line without one).</summary>
	internal readonly record struct TextLine(string Content, string Eol)
	{
		/// <summary>What lines are compared by: the content, and whether a line end follows (its kind aside).</summary>
		public string Key => Eol.Length > 0 ? Content + "\n" : Content;
	}

	/// <summary>
	/// Merges <paramref name="ours"/> and <paramref name="theirs"/>, two edits of <paramref name="baseBytes"/>. Where the caller
	/// knows how "theirs" was made from the base (the lines a patch changed), <paramref name="theirsMatch"/> gives it, for each
	/// base line, the "theirs" line it is kept as, or -1 where "theirs" changed it, in place of an edit script, which may pick
	/// another of several like lines; it must keep lines in order, and keep each as it is (its key), or there are no bytes.
	/// With <paramref name="oursFromEnd"/>, the edit script for "ours" breaks ties the other way (it is found on both sides'
	/// lines reversed): among equally short scripts, one that keeps lines as late as it can, where the other keeps them as
	/// early. A merge whose result depends on that choice depends on a guess. With <paramref name="touchingMerges"/>, a change
	/// of one side that only touches one of the other's (it starts where the other ends, and they are not both insertions at
	/// one place) is no conflict: the two are put one after the other, as their order is plain; it finds that second
	/// reading's result, where the default refuses any change next to another.
	/// </summary>
	public static MergeResult Merge(byte[] baseBytes, byte[] ours, byte[] theirs, int[]? theirsMatch = null, bool oursFromEnd = false, bool touchingMerges = false)
	{
		ArgumentNullException.ThrowIfNull(baseBytes);
		ArgumentNullException.ThrowIfNull(ours);
		ArgumentNullException.ThrowIfNull(theirs);
		(_, List<TextLine> Base) = Split(baseBytes);
		(bool OursBom, List<TextLine> Ours) = Split(ours);
		(_, List<TextLine> Theirs) = Split(theirs);

		// Each distinct key gets a number, so the edit scripts compare numbers.
		Dictionary<string, int> Numbers = new(StringComparer.Ordinal);
		int[] Number(List<TextLine> lines) => [.. lines.Select(Line => Numbers.TryGetValue(Line.Key, out int Found) ? Found : Numbers[Line.Key] = Numbers.Count)];
		int[] BaseKeys = Number(Base);
		int[] OursKeys = Number(Ours);
		int[] TheirsKeys = Number(Theirs);
		if (theirsMatch is not null && !KeepsLines(theirsMatch, BaseKeys, TheirsKeys))
		{
			return new MergeResult(null, [], "the given match of the base's lines with the other side's doesn't keep them in order, as they are");
		}
		int[]? OursMatch = oursFromEnd ? MatchBaseFromEnd(BaseKeys, OursKeys) : MatchBaseFromStart(BaseKeys, OursKeys);
		int[]? TheirsMatch = theirsMatch ?? MatchBase(BaseKeys, TheirsKeys);
		if (OursMatch is null || TheirsMatch is null)
		{
			return new MergeResult(null, [], $"one side differs from the base in more than {MaxDifferences} lines, too many to merge");
		}

		// The base's ranges each side changed ([Start, End); an insertion is empty), with the side: false ours, true theirs.
		List<(int Start, int End, bool Theirs)> Changes = [.. Changed(OursMatch, Ours.Count).Select(Range => (Range.Start, Range.End, false)),
			.. Changed(TheirsMatch, Theirs.Count).Select(Range => (Range.Start, Range.End, true))];
		// By start, then end: an insertion before a change that starts at the same line.
		Changes.Sort((A, B) => A.Start != B.Start ? A.Start.CompareTo(B.Start) : A.End.CompareTo(B.End));

		string? Eol = SingleEol(Ours);
		List<(TextLine Line, bool FromTheirs)> Merged = [];
		List<MergeConflict> Conflicts = [];
		List<MergeConflict> SameChanges = [];
		int BothSides = 0;
		int At = 0;
		for (int Index = 0; Index < Changes.Count;)
		{
			// A region: the changes that overlap, or touch, one after the other.
			int First = Index;
			int Start = Changes[Index].Start;
			int End = Changes[Index].End;
			bool ByOurs = false;
			bool ByTheirs = false;
			for (; Index < Changes.Count && Changes[Index].Start <= End; Index++)
			{
				End = Math.Max(End, Changes[Index].End);
				ByOurs |= !Changes[Index].Theirs;
				ByTheirs |= Changes[Index].Theirs;
			}
			for (; At < Start; At++)
			{
				Merged.Add((Ours[OursMatch[At]], false));
			}
			if (touchingMerges && Index - First == 2 && Changes[First].End == Changes[First + 1].Start
				&& (Changes[First].Start < Changes[First].End || Changes[First + 1].Start < Changes[First + 1].End))
			{
				// One change of each side, the second starting where the first ends: each side kept the lines around its own.
				foreach ((int ChangeStart, int ChangeEnd, bool IsTheirs) in Changes.GetRange(First, 2))
				{
					(int SideStart, int SideEnd) = IsTheirs ? Side(TheirsMatch, Theirs.Count, ChangeStart, ChangeEnd) : Side(OursMatch, Ours.Count, ChangeStart, ChangeEnd);
					Merged.AddRange((IsTheirs ? Theirs : Ours).GetRange(SideStart, SideEnd - SideStart).Select(Line => (Line, IsTheirs)));
				}
				At = End;
				continue;
			}
			// The lines just outside a region are in no change of either side, so both sides keep them.
			(int OursStart, int OursEnd) = Side(OursMatch, Ours.Count, Start, End);
			(int TheirsStart, int TheirsEnd) = Side(TheirsMatch, Theirs.Count, Start, End);
			BothSides += ByOurs && ByTheirs ? 1 : 0;
			if (!ByTheirs || (ByOurs && OursKeys.AsSpan(OursStart, OursEnd - OursStart).SequenceEqual(TheirsKeys.AsSpan(TheirsStart, TheirsEnd - TheirsStart))))
			{
				if (ByOurs && ByTheirs)
				{
					SameChanges.Add(new MergeConflict(OursStart + 1, OursEnd - OursStart, Show(Ours, OursStart, OursEnd), Show(Theirs, TheirsStart, TheirsEnd)));
				}
				Merged.AddRange(Ours.GetRange(OursStart, OursEnd - OursStart).Select(Line => (Line, false)));
			}
			else if (!ByOurs)
			{
				Merged.AddRange(Theirs.GetRange(TheirsStart, TheirsEnd - TheirsStart).Select(Line => (Line, true)));
			}
			else
			{
				Conflicts.Add(new MergeConflict(OursStart + 1, OursEnd - OursStart, Show(Ours, OursStart, OursEnd), Show(Theirs, TheirsStart, TheirsEnd)));
			}
			At = End;
		}
		for (; At < Base.Count; At++)
		{
			Merged.Add((Ours[OursMatch[At]], false));
		}
		if (Conflicts.Count > 0)
		{
			return new MergeResult(null, Conflicts, null, BothSides) { OursMatch = OursMatch };
		}

		StringBuilder Text = new();
		for (int Index = 0; Index < Merged.Count; Index++)
		{
			(TextLine Line, bool FromTheirs) = Merged[Index];
			if (Line.Eol.Length == 0 && Index + 1 < Merged.Count)
			{
				// A line without a line end can only be the last: no merge joins two lines.
				return new MergeResult(null, [new MergeConflict(Text.ToString().Count(Character => Character == '\n') + 1, 1, Line.Content.Trim(), "")], null, BothSides)
				{
					OursMatch = OursMatch,
				};
			}
			Text.Append(Line.Content).Append(FromTheirs && Line.Eol.Length > 0 && Eol is not null ? Eol : Line.Eol);
		}
		byte[] Bytes = Encoding.Latin1.GetBytes(Text.ToString());
		return new MergeResult(OursBom ? [0xEF, 0xBB, 0xBF, .. Bytes] : Bytes, [], null, BothSides) { SameChanges = SameChanges, OursMatch = OursMatch };
	}

	/// <summary>Whether a given match of base lines with a side's keeps them in order (each side line once at most) and each as it is.</summary>
	static bool KeepsLines(int[] match, int[] baseKeys, int[] sideKeys)
	{
		if (match.Length != baseKeys.Length)
		{
			return false;
		}
		int Previous = -1;
		for (int Index = 0; Index < match.Length; Index++)
		{
			if (match[Index] < 0)
			{
				continue;
			}
			if (match[Index] <= Previous || match[Index] >= sideKeys.Length || sideKeys[match[Index]] != baseKeys[Index])
			{
				return false;
			}
			Previous = match[Index];
		}
		return true;
	}

	/// <summary>A file's lines, without its UTF-8 byte-order mark (and whether it had one).</summary>
	internal static (bool Bom, List<TextLine> Lines) Split(byte[] bytes)
	{
		ArgumentNullException.ThrowIfNull(bytes);
		bool Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
		string Text = Encoding.Latin1.GetString(bytes, Bom ? 3 : 0, bytes.Length - (Bom ? 3 : 0));
		List<TextLine> Lines = [];
		for (int Start = 0; Start < Text.Length;)
		{
			int End = Text.IndexOf('\n', Start);
			if (End < 0)
			{
				Lines.Add(new TextLine(Text[Start..], ""));
				break;
			}
			int ContentEnd = End > Start && Text[End - 1] == '\r' ? End - 1 : End;
			Lines.Add(new TextLine(Text[Start..ContentEnd], Text[ContentEnd..(End + 1)]));
			Start = End + 1;
		}
		return (Bom, Lines);
	}

	/// <summary>The line end every line of <paramref name="lines"/> that has one uses, or null when they use both kinds or none.</summary>
	static string? SingleEol(List<TextLine> lines)
	{
		List<string> Kinds = [.. lines.Select(Line => Line.Eol).Where(Eol => Eol.Length > 0).Distinct(StringComparer.Ordinal)];
		return Kinds.Count == 1 ? Kinds[0] : null;
	}

	/// <summary>For each base line, the side's line a shortest edit script keeps it as, or -1 when it changed it; null when they differ too much.</summary>
	static int[]? MatchBase(int[] baseKeys, int[] side)
	{
		List<(int A, int B)>? Pairs = Matches(baseKeys, side);
		if (Pairs is null)
		{
			return null;
		}
		int[] Result = new int[baseKeys.Length];
		Array.Fill(Result, -1);
		foreach ((int A, int B) in Pairs)
		{
			Result[A] = B;
		}
		return Result;
	}

	/// <summary>
	/// As <see cref="MatchBase"/>, keeping each line as early as it can: the lines both share at their end are not set aside
	/// first (they would be kept as late as they can, so a function added after another, both ending in '}', would be taken
	/// for lines added inside the first), unless the search can't do without (the sides differ in too many lines).
	/// </summary>
	static int[]? MatchBaseFromStart(int[] baseKeys, int[] side)
	{
		List<(int A, int B)>? Pairs = Matches(baseKeys, side, stripEnd: false) ?? Matches(baseKeys, side);
		if (Pairs is null)
		{
			return null;
		}
		int[] Result = new int[baseKeys.Length];
		Array.Fill(Result, -1);
		foreach ((int A, int B) in Pairs)
		{
			Result[A] = B;
		}
		return Result;
	}

	/// <summary>
	/// As <see cref="MatchBaseFromStart"/>, with the edit script found on both sides' lines reversed, so its ties break the
	/// other way: it keeps each line as late as it can.
	/// </summary>
	static int[]? MatchBaseFromEnd(int[] baseKeys, int[] side)
	{
		int[] BaseReversed = [.. baseKeys.Reverse()];
		int[] SideReversed = [.. side.Reverse()];
		List<(int A, int B)>? Pairs = Matches(BaseReversed, SideReversed, stripEnd: false) ?? Matches(BaseReversed, SideReversed);
		if (Pairs is null)
		{
			return null;
		}
		int[] Result = new int[baseKeys.Length];
		Array.Fill(Result, -1);
		foreach ((int A, int B) in Pairs)
		{
			Result[baseKeys.Length - 1 - A] = side.Length - 1 - B;
		}
		return Result;
	}

	/// <summary>
	/// The base's ranges a side changed: each run of base lines it doesn't keep, with the side's lines between the kept ones
	/// around it; a range with no base line is an insertion before the base line it starts at.
	/// </summary>
	static List<(int Start, int End)> Changed(int[] match, int sideCount)
	{
		List<(int, int)> Result = [];
		int PreviousBase = -1;
		int PreviousSide = -1;
		for (int Index = 0; Index <= match.Length; Index++)
		{
			if (Index < match.Length && match[Index] < 0)
			{
				continue;
			}
			int SideAt = Index < match.Length ? match[Index] : sideCount;
			if (Index > PreviousBase + 1 || SideAt > PreviousSide + 1)
			{
				Result.Add((PreviousBase + 1, Index));
			}
			PreviousBase = Index;
			PreviousSide = SideAt;
		}
		return Result;
	}

	/// <summary>A side's lines for the base's range [start, end): those between the side's lines for the base lines around it.</summary>
	static (int Start, int End) Side(int[] match, int sideCount, int start, int end)
	{
		return (start == 0 ? 0 : match[start - 1] + 1, end == match.Length ? sideCount : match[end]);
	}

	/// <summary>A region's first non-blank line, trimmed and cut short, for a message; read as UTF-8.</summary>
	static string Show(List<TextLine> lines, int start, int end)
	{
		string First = lines.Skip(start).Take(end - start).Select(Line => Line.Content.Trim()).FirstOrDefault(Content => Content.Length > 0) ?? "";
		First = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(First));
		return First.Length > 80 ? First[..77] + "..." : First;
	}

	/// <summary>
	/// The pairs of equal lines (an index in <paramref name="a"/>, one in <paramref name="b"/>) a shortest edit script from
	/// <paramref name="a"/> to <paramref name="b"/> keeps, in order (Myers' O(ND) search, after the lines both share at their
	/// start and, unless not <paramref name="stripEnd"/>, at their end); null when more than <see cref="MaxDifferences"/> lines differ.
	/// </summary>
	internal static List<(int A, int B)>? Matches(int[] a, int[] b, bool stripEnd = true)
	{
		ArgumentNullException.ThrowIfNull(a);
		ArgumentNullException.ThrowIfNull(b);
		int Prefix = 0;
		while (Prefix < a.Length && Prefix < b.Length && a[Prefix] == b[Prefix])
		{
			Prefix++;
		}
		int Suffix = 0;
		while (stripEnd && Suffix < a.Length - Prefix && Suffix < b.Length - Prefix && a[a.Length - 1 - Suffix] == b[b.Length - 1 - Suffix])
		{
			Suffix++;
		}
		int N = a.Length - Prefix - Suffix;
		int M = b.Length - Prefix - Suffix;
		List<(int A, int B)> Result = [.. Enumerable.Range(0, Prefix).Select(Index => (Index, Index))];
		if (N > 0 && M > 0)
		{
			List<(int A, int B)>? Middle = Myers(a.AsSpan(Prefix, N), b.AsSpan(Prefix, M));
			if (Middle is null)
			{
				return null;
			}
			Result.AddRange(Middle.Select(Pair => (Pair.A + Prefix, Pair.B + Prefix)));
		}
		Result.AddRange(Enumerable.Range(0, Suffix).Select(Index => (a.Length - Suffix + Index, b.Length - Suffix + Index)));
		return Result;
	}

	/// <summary>Myers' greedy search for a shortest edit script, keeping each step's furthest points to walk it back; null past <see cref="MaxDifferences"/>.</summary>
	static List<(int A, int B)>? Myers(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
	{
		int N = a.Length;
		int M = b.Length;
		int Limit = Math.Min(N + M, MaxDifferences);
		int Offset = Limit + 1;
		// V[k + Offset]: the furthest x reached on diagonal k (y = x - k).
		int[] V = new int[2 * Limit + 3];
		// Trace[d]: V on diagonals -d..d before step d, as the walk back reads it.
		List<int[]> Trace = [];
		int Steps = -1;
		for (int D = 0; D <= Limit && Steps < 0; D++)
		{
			int[] Row = new int[2 * D + 1];
			Array.Copy(V, Offset - D, Row, 0, 2 * D + 1);
			Trace.Add(Row);
			for (int K = -D; K <= D; K += 2)
			{
				int X = K == -D || (K != D && V[Offset + K - 1] < V[Offset + K + 1]) ? V[Offset + K + 1] : V[Offset + K - 1] + 1;
				int Y = X - K;
				while (X < N && Y < M && a[X] == b[Y])
				{
					X++;
					Y++;
				}
				V[Offset + K] = X;
				if (X >= N && Y >= M)
				{
					Steps = D;
					break;
				}
			}
		}
		if (Steps < 0)
		{
			return null;
		}
		List<(int A, int B)> Result = [];
		int AtX = N;
		int AtY = M;
		for (int D = Steps; D > 0; D--)
		{
			int[] Row = Trace[D];
			int K = AtX - AtY;
			int Previous = K == -D || (K != D && Row[K - 1 + D] < Row[K + 1 + D]) ? K + 1 : K - 1;
			int PreviousX = Row[Previous + D];
			int PreviousY = PreviousX - Previous;
			// The snake after the step: equal lines.
			while (AtX > PreviousX && AtY > PreviousY)
			{
				AtX--;
				AtY--;
				Result.Add((AtX, AtY));
			}
			AtX = PreviousX;
			AtY = PreviousY;
		}
		// Step 0's snake, from the start.
		while (AtX > 0 && AtY > 0)
		{
			AtX--;
			AtY--;
			Result.Add((AtX, AtY));
		}
		Result.Reverse();
		return Result;
	}
}
