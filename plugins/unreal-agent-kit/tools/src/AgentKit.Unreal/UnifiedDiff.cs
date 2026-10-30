// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>A patch that can't be read, or doesn't apply cleanly.</summary>
public sealed class PatchException : Exception
{
	/// <summary>Creates the exception with a message.</summary>
	public PatchException(string message) : base(message)
	{
	}

	/// <summary>Creates the exception.</summary>
	public PatchException()
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public PatchException(string message, Exception innerException) : base(message, innerException)
	{
	}
}

/// <summary>One hunk: the old lines it replaces (context and '-') and the new ones (context and '+').</summary>
/// <param name="OldStart">The old side's first line, 1-based (0 for an empty old side).</param>
/// <param name="OldLines">The old side's lines, without line ends.</param>
/// <param name="NewLines">The new side's lines, without line ends.</param>
/// <param name="OldNoNewlineAtEnd">"\ No newline at end of file" follows the old side's last line.</param>
/// <param name="NewNoNewlineAtEnd">"\ No newline at end of file" follows the new side's last line.</param>
public sealed record DiffHunk(int OldStart, IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines, bool OldNoNewlineAtEnd, bool NewNoNewlineAtEnd)
{
	/// <summary>How many of its lines are context (' '): in both <see cref="OldLines"/> and <see cref="NewLines"/>.</summary>
	public int ContextLines { get; init; }

	/// <summary>The new side's first line, 1-based (0 for an empty new side), as its header gives it.</summary>
	public int NewStart { get; init; }

	/// <summary>Its lines in order, each with its kind (' ', '-' or '+') first; the "\ No newline" lines left out.</summary>
	public IReadOnlyList<string> Body { get; init; } = [];

	/// <summary>Its '+' lines.</summary>
	public int Added => NewLines.Count - ContextLines;

	/// <summary>Its '-' lines.</summary>
	public int Removed => OldLines.Count - ContextLines;
}

/// <summary>One file of a patch.</summary>
/// <param name="Path">The file, relative to the patch's root, with '/' separators.</param>
/// <param name="IsNew">The patch creates it (old side /dev/null).</param>
/// <param name="IsDeleted">The patch deletes it (new side /dev/null).</param>
/// <param name="Hunks">Its hunks, in order.</param>
public sealed record DiffFile(string Path, bool IsNew, bool IsDeleted, IReadOnlyList<DiffHunk> Hunks)
{
	/// <summary>Its '+' lines, over every hunk.</summary>
	public int Added => Hunks.Sum(Hunk => Hunk.Added);

	/// <summary>Its '-' lines, over every hunk.</summary>
	public int Removed => Hunks.Sum(Hunk => Hunk.Removed);
}

/// <summary>
/// A unified diff (as <c>git diff</c> or <c>diff -u</c> writes it) that changes, adds or deletes text files: its parser,
/// and a strict applier for workspaces without Git. Renames, copies, mode changes and binary patches are refused, so the
/// files a patch touches are exactly the ones listed, whichever tool applies it. Paths with "a/" and "b/" prefixes (Git's)
/// lose them; a path must be relative and stay inside the root.
/// </summary>
public static partial class UnifiedDiff
{
	/// <summary>A patch file's text, decoded as <see cref="File.ReadAllText(string)"/> does (UTF-8, or the encoding its byte-order mark names).</summary>
	public static string Decode(byte[] bytes)
	{
		using StreamReader Reader = new(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return Reader.ReadToEnd();
	}

	/// <summary>
	/// The patch that undoes <paramref name="file"/>: each hunk's sides swapped (a file it creates is one it deletes, and the
	/// other way round), for <see cref="Apply"/> to take a mutation back out of a file.
	/// </summary>
	public static DiffFile Reverse(DiffFile file)
	{
		ArgumentNullException.ThrowIfNull(file);
		List<DiffHunk> Hunks = [.. file.Hunks.Select(Hunk => new DiffHunk(Hunk.NewStart, Hunk.NewLines, Hunk.OldLines, Hunk.NewNoNewlineAtEnd, Hunk.OldNoNewlineAtEnd)
		{
			ContextLines = Hunk.ContextLines,
			NewStart = Hunk.OldStart,
			Body = [.. Hunk.Body.Select(Line => Line[0] switch { '+' => "-" + Line[1..], '-' => "+" + Line[1..], _ => Line })],
		})];
		return new DiffFile(file.Path, file.IsDeleted, file.IsNew, Hunks);
	}

	/// <summary>Reads a patch's files.</summary>
	/// <exception cref="PatchException">It is not a unified diff of text files, or holds something uak doesn't apply.</exception>
	public static List<DiffFile> Parse(string text)
	{
		List<string> AllLines = [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
		// The split's last element after a final line end is no line: read as an empty context line, it would hide a short hunk.
		if (AllLines.Count > 0 && AllLines[^1].Length == 0)
		{
			AllLines.RemoveAt(AllLines.Count - 1);
		}
		string[] Lines = [.. AllLines];
		List<DiffFile> Files = [];
		int Index = 0;
		while (Index < Lines.Length)
		{
			string Line = Lines[Index];
			if (Line.StartsWith("rename from ", StringComparison.Ordinal) || Line.StartsWith("copy from ", StringComparison.Ordinal)
				|| Line.StartsWith("old mode ", StringComparison.Ordinal) || Line.StartsWith("GIT binary patch", StringComparison.Ordinal)
				|| (Line.StartsWith("Binary files ", StringComparison.Ordinal) && Line.EndsWith(" differ", StringComparison.Ordinal)))
			{
				throw new PatchException($"line {Index + 1}: '{Line}': uak mutate applies changes to text files only (no renames, copies, mode changes or binary files)");
			}
			if (!Line.StartsWith("--- ", StringComparison.Ordinal) || Index + 1 >= Lines.Length || !Lines[Index + 1].StartsWith("+++ ", StringComparison.Ordinal))
			{
				// Before the first file anything goes (a commit message); after it, only the headers that start a file, or the
				// end. A line a parser skipped there would be a hunk line its header doesn't count, which one tool drops and
				// another applies, or a hunk cut off from its file.
				if (Files.Count > 0 && !IsBetweenFiles(Lines, Index))
				{
					throw new PatchException($"line {Index + 1}: '{Line}' follows a hunk but is no part of one: a hunk has more lines than its header"
						+ " (@@ -a,b +c,d @@) counts, or a line (an empty one?) parts a hunk from the next");
				}
				if (Files.Count > 0 && Line == "-- ")
				{
					// git format-patch's signature: "-- ", then the Git version, to the end.
					break;
				}
				Index++;
				continue;
			}
			string OldPath = HeaderPath(Lines[Index]);
			string NewPath = HeaderPath(Lines[Index + 1]);
			Index += 2;
			List<DiffHunk> Hunks = [];
			while (Index < Lines.Length && Lines[Index].StartsWith("@@", StringComparison.Ordinal))
			{
				Hunks.Add(ParseHunk(Lines, ref Index));
			}
			Files.Add(MakeFile(OldPath, NewPath, Hunks, Index));
		}
		if (Files.Count == 0)
		{
			throw new PatchException("it holds no file changes (no '--- ' and '+++ ' lines)");
		}
		List<string> Duplicates = Files.GroupBy(File => File.Path, StringComparer.OrdinalIgnoreCase).Where(Group => Group.Count() > 1).Select(Group => Group.Key).ToList();
		if (Duplicates.Count > 0)
		{
			throw new PatchException($"it changes {string.Join(", ", Duplicates)} more than once");
		}
		return Files;
	}

	/// <summary>
	/// Whether the line at <paramref name="index"/>, after a file's last hunk, may stand there: a header that starts the next
	/// file (git's "diff --git", "index", "new file mode", "deleted file mode"; diff's "diff -u ..."; svn's "Index:" and its
	/// "===" rule), git format-patch's signature ("-- "), or an empty line with nothing but empty lines after it.
	/// </summary>
	static bool IsBetweenFiles(string[] lines, int index)
	{
		string Line = lines[index];
		if (Line.Length == 0)
		{
			return lines.Skip(index).All(Rest => Rest.Length == 0);
		}
		return Line == "-- " || Line.StartsWith("diff ", StringComparison.Ordinal) || Line.StartsWith("index ", StringComparison.Ordinal)
			|| Line.StartsWith("new file mode ", StringComparison.Ordinal) || Line.StartsWith("deleted file mode ", StringComparison.Ordinal)
			|| Line.StartsWith("Index: ", StringComparison.Ordinal) || Line.StartsWith("===", StringComparison.Ordinal);
	}

	static string HeaderPath(string line)
	{
		// "--- a/Source/X.cpp\t2026-10-29 12:00:00" (diff -u adds a time after a tab).
		string Path = line[4..];
		int Tab = Path.IndexOf('\t', StringComparison.Ordinal);
		if (Tab >= 0)
		{
			Path = Path[..Tab];
		}
		Path = Path.TrimEnd();
		if (Path.Length > 1 && Path[0] == '"' && Path[^1] == '"')
		{
			throw new PatchException($"'{line}': a quoted path (Git quotes unusual characters) isn't supported");
		}
		return Path;
	}

	static DiffFile MakeFile(string oldPath, string newPath, List<DiffHunk> hunks, int lineIndex)
	{
		bool IsNew = oldPath == "/dev/null";
		bool IsDeleted = newPath == "/dev/null";
		if (IsNew && IsDeleted)
		{
			throw new PatchException($"line {lineIndex}: both sides are /dev/null");
		}
		bool GitStyle = (IsNew || oldPath.StartsWith("a/", StringComparison.Ordinal)) && (IsDeleted || newPath.StartsWith("b/", StringComparison.Ordinal));
		string Old = IsNew ? "" : GitStyle ? oldPath[2..] : oldPath;
		string New = IsDeleted ? "" : GitStyle ? newPath[2..] : newPath;
		if (!IsNew && !IsDeleted && !Old.Equals(New, StringComparison.Ordinal))
		{
			throw new PatchException($"line {lineIndex}: '{Old}' becomes '{New}': uak mutate doesn't rename files");
		}
		string Path = IsNew ? New : Old;
		if (Path.Length == 0 || System.IO.Path.IsPathRooted(Path) || Path.Contains('\\', StringComparison.Ordinal)
			|| Path.Split('/').Any(Part => Part is "" or "." or ".."))
		{
			throw new PatchException($"'{Path}' must be a relative path inside the workspace, with '/' separators");
		}
		if (hunks.Count == 0)
		{
			throw new PatchException($"{Path}: no hunks (a file without content changes)");
		}
		if (IsNew && (hunks.Count != 1 || hunks[0].OldLines.Count != 0))
		{
			throw new PatchException($"{Path}: a new file's patch must be one hunk that adds every line");
		}
		if (IsDeleted && hunks.Any(Hunk => Hunk.NewLines.Count != 0))
		{
			throw new PatchException($"{Path}: a deleted file's patch must only remove lines");
		}
		return new DiffFile(Path, IsNew, IsDeleted, hunks);
	}

	static DiffHunk ParseHunk(string[] lines, ref int index)
	{
		Match Header = HunkHeader().Match(lines[index]);
		if (!Header.Success)
		{
			throw new PatchException($"line {index + 1}: '{lines[index]}' is not a hunk header (@@ -a,b +c,d @@)");
		}
		int OldStart = int.Parse(Header.Groups["os"].Value, CultureInfo.InvariantCulture);
		int OldCount = Header.Groups["oc"].Success ? int.Parse(Header.Groups["oc"].Value, CultureInfo.InvariantCulture) : 1;
		int NewStart = int.Parse(Header.Groups["ns"].Value, CultureInfo.InvariantCulture);
		int NewCount = Header.Groups["nc"].Success ? int.Parse(Header.Groups["nc"].Value, CultureInfo.InvariantCulture) : 1;
		index++;
		List<string> Old = [];
		List<string> New = [];
		List<string> Ordered = [];
		bool OldNoNewline = false;
		bool NewNoNewline = false;
		int Context = 0;
		char Last = ' ';
		while (Old.Count < OldCount || New.Count < NewCount || (index < lines.Length && lines[index].StartsWith('\\')))
		{
			if (index >= lines.Length)
			{
				throw new PatchException($"the hunk at line {index} ends early: it promised {OldCount} old and {NewCount} new lines");
			}
			string Line = lines[index];
			// An editor may strip a context line's single space: an empty line inside a hunk is empty context.
			char Kind = Line.Length == 0 ? ' ' : Line[0];
			string Body = Line.Length == 0 ? "" : Line[1..];
			switch (Kind)
			{
				case ' ':
					Old.Add(Body);
					New.Add(Body);
					Context++;
					break;
				case '-':
					Old.Add(Body);
					break;
				case '+':
					New.Add(Body);
					break;
				case '\\':
					// "\ No newline at end of file" belongs to the line before it.
					OldNoNewline |= Last is ' ' or '-';
					NewNoNewline |= Last is ' ' or '+';
					break;
				default:
					throw new PatchException($"line {index + 1}: '{Line}' is not part of a hunk (' ', '-', '+' or '\\')");
			}
			if (Kind != '\\')
			{
				Last = Kind;
				Ordered.Add(Kind + Body);
			}
			index++;
			if (Old.Count > OldCount || New.Count > NewCount)
			{
				throw new PatchException($"the hunk before line {index + 1} has more lines than its header says");
			}
		}
		return new DiffHunk(OldStart, Old, New, OldNoNewline, NewNoNewline) { ContextLines = Context, NewStart = NewStart, Body = Ordered };
	}

	[GeneratedRegex(@"^@@ -(?<os>\d+)(?:,(?<oc>\d+))? \+(?<ns>\d+)(?:,(?<nc>\d+))? @@")]
	private static partial Regex HunkHeader();

	static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	/// <summary>Whether a file's bytes are UTF-8 text (after a byte-order mark, if any), which <see cref="Apply"/> reads.</summary>
	public static bool IsUtf8Text(byte[] bytes)
	{
		ArgumentNullException.ThrowIfNull(bytes);
		int Skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
		try
		{
			StrictUtf8.GetCharCount(bytes, Skip, bytes.Length - Skip);
			return true;
		}
		catch (DecoderFallbackException)
		{
			return false;
		}
	}

	/// <summary>
	/// Applies one file's hunks to its bytes (null when it doesn't exist), strictly: each hunk's old lines must match exactly
	/// (ignoring only the '\r's at a line's end) at the line its header names, counted after the hunks above it, and nowhere
	/// else; and its header's new start must be where that puts its new lines. There is no search for a hunk whose lines
	/// moved: git apply searches one way and a search here another, so the two could change different lines, and -accept
	/// would then merge from a mutant that isn't the one in the file. git tries the line a header's new start names first,
	/// except for a hunk with no context line after its changes, which git takes for the file's end and applies there only:
	/// such a hunk (a zero-context insertion among them) is refused unless it ends at the file's end. (Where git compares a
	/// line's '\r' and this doesn't, under core.autocrlf=false, uak mutate has git say where it applies each hunk.) Returns the
	/// new bytes, or null to delete the file. Every line the patch doesn't add keeps its own bytes and line end, a context line
	/// of a hunk too; an added line gets the line end most of the file's lines have (CRLF or LF); the byte-order mark is kept.
	/// </summary>
	/// <exception cref="PatchException">A hunk doesn't match at its line, its header doesn't add up, or the file isn't UTF-8 text.</exception>
	public static byte[]? Apply(DiffFile file, byte[]? original)
	{
		ArgumentNullException.ThrowIfNull(file);
		if (file.IsNew)
		{
			if (original is not null)
			{
				throw new PatchException($"{file.Path}: the patch creates it, but it exists");
			}
			DiffHunk Hunk = file.Hunks[0];
			return Encoding.UTF8.GetBytes(string.Join("\n", Hunk.NewLines) + (Hunk.NewNoNewlineAtEnd || Hunk.NewLines.Count == 0 ? "" : "\n"));
		}
		if (original is null)
		{
			throw new PatchException($"{file.Path}: the patch changes it, but it doesn't exist");
		}
		bool Bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
		string Text;
		try
		{
			Text = StrictUtf8.GetString(original, Bom ? 3 : 0, original.Length - (Bom ? 3 : 0));
		}
		catch (DecoderFallbackException Exception)
		{
			throw new PatchException($"{file.Path}: not a UTF-8 text file", Exception);
		}
		int Crlf = CountOf(Text, "\r\n");
		string Eol = Crlf > 0 && Crlf * 2 >= CountOf(Text, "\n") ? "\r\n" : "\n";
		// Each line as it is: its text, and its line end ("\r\n", "\n", or "" for a last line without one). An empty file has
		// no line, and nothing to end without a line end.
		List<(string Content, string Eol)> Lines = [];
		for (int Start = 0; Start < Text.Length;)
		{
			int End = Text.IndexOf('\n', Start);
			if (End < 0)
			{
				Lines.Add((Text[Start..], ""));
				break;
			}
			int ContentEnd = End > Start && Text[End - 1] == '\r' ? End - 1 : End;
			Lines.Add((Text[Start..ContentEnd], Text[ContentEnd..(End + 1)]));
			Start = End + 1;
		}
		bool EndsWithNewline = Lines.Count == 0 || Lines[^1].Eol.Length > 0;

		int Offset = 0;
		int Floor = 0;
		foreach ((DiffHunk Hunk, int Number) in file.Hunks.Select((Hunk, Number) => (Hunk, Number + 1)))
		{
			List<string> Old = [.. Hunk.OldLines.Select(Line => Line.TrimEnd('\r'))];
			// The hunk's new lines: a context line is the file's own, as it is; an added line gets the file's line end.
			List<(string Content, string Eol)> New = [];
			// Where the header puts it: its old start (an empty old side names the line it follows), moved by the lines the hunks
			// above it added or removed. Its new start names the same place in the new lines, where git looks first.
			int At = (Hunk.OldLines.Count == 0 ? Hunk.OldStart : Hunk.OldStart - 1) + Offset;
			int NewAt = Hunk.NewLines.Count == 0 ? Hunk.NewStart : Hunk.NewStart - 1;
			if (At < Floor)
			{
				throw new PatchException($"{file.Path}: hunk {Number} (@@ -{Hunk.OldStart}) starts before the hunk above it ends: hunks must be in order, without overlap");
			}
			if (NewAt != At)
			{
				throw new PatchException($"{file.Path}: hunk {Number} (@@ -{Hunk.OldStart} +{Hunk.NewStart}): its new start doesn't follow from its old start and the"
					+ $" hunks above it (+{(Hunk.NewLines.Count == 0 ? At : At + 1)}), so git would look for it elsewhere");
			}
			if (!Matches(Lines, Old, At, Floor, Lines.Count - Old.Count))
			{
				throw new PatchException($"{file.Path}: hunk {Number} (@@ -{Hunk.OldStart}) doesn't match the file at the line its header names (the file has"
					+ $" {Lines.Count} lines): it was changed since the patch was made, or the patch was made against other lines. Make it again against the"
					+ " file as it is");
			}
			bool AtEnd = At + Old.Count == Lines.Count;
			if (!AtEnd && Hunk.Body.Count > 0 && Hunk.Body[^1][0] != ' ')
			{
				// git takes a hunk without trailing context for the file's end (as diff makes one only there), and looks for it there
				// first: it would change the file's last lines when they match too, and refuse the patch when they don't.
				throw new PatchException($"{file.Path}: hunk {Number} (@@ -{Hunk.OldStart}) has no context line after its changes, which git reads as the end of"
					+ " the file, and it isn't there: make the patch with context lines (git diff -U3)");
			}
			if (AtEnd && Old.Count > 0 && EndsWithNewline == Hunk.OldNoNewlineAtEnd)
			{
				throw new PatchException($"{file.Path}: hunk {Number} (@@ -{Hunk.OldStart}) says the file {(Hunk.OldNoNewlineAtEnd ? "doesn't end" : "ends")} with a line end, and it {(EndsWithNewline ? "does" : "doesn't")}");
			}
			int OldAt = At;
			foreach (string Line in Hunk.Body)
			{
				if (Line[0] == '+')
				{
					New.Add((Line[1..].TrimEnd('\r'), Eol));
				}
				else
				{
					if (Line[0] == ' ')
					{
						New.Add(Lines[OldAt]);
					}
					OldAt++;
				}
			}
			Lines.RemoveRange(At, Old.Count);
			Lines.InsertRange(At, New);
			Offset += New.Count - Old.Count;
			Floor = At + New.Count;
			if (AtEnd && (Hunk.OldNoNewlineAtEnd || Hunk.NewNoNewlineAtEnd || Old.Count > 0))
			{
				EndsWithNewline = !Hunk.NewNoNewlineAtEnd;
			}
		}
		if (file.IsDeleted)
		{
			if (Lines.Count != 0)
			{
				throw new PatchException($"{file.Path}: the patch deletes it, but it holds lines the patch doesn't remove");
			}
			return null;
		}
		// A line without a line end gets the file's where a line now follows it; the last line has one as the patch says.
		StringBuilder Result = new();
		for (int Index = 0; Index < Lines.Count; Index++)
		{
			(string Content, string LineEol) = Lines[Index];
			Result.Append(Content).Append(Index + 1 < Lines.Count || EndsWithNewline ? (LineEol.Length > 0 ? LineEol : Eol) : "");
		}
		byte[] Bytes = Encoding.UTF8.GetBytes(Result.ToString());
		return Bom ? [0xEF, 0xBB, 0xBF, .. Bytes] : Bytes;
	}

	static bool Matches(List<(string Content, string Eol)> lines, List<string> old, int at, int floor, int last)
	{
		if (at < floor || at > last)
		{
			return false;
		}
		for (int Line = 0; Line < old.Count; Line++)
		{
			if (!lines[at + Line].Content.TrimEnd('\r').Equals(old[Line], StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	static int CountOf(string text, string what)
	{
		int Count = 0;
		for (int At = text.IndexOf(what, StringComparison.Ordinal); At >= 0; At = text.IndexOf(what, At + what.Length, StringComparison.Ordinal))
		{
			Count++;
		}
		return Count;
	}

	/// <summary>
	/// Checks a patch's file paths resolve inside <paramref name="root"/>, each to itself (Windows drops a trailing '.' or
	/// space, so "Foo.cpp." would be Foo.cpp), and to different files, ignoring case; returns their full paths in order.
	/// </summary>
	/// <exception cref="UakUsageException">A path leaves the root, resolves to another name, or names a file twice.</exception>
	public static List<string> Resolve(IEnumerable<DiffFile> files, string root)
	{
		string Root = Path.GetFullPath(root);
		List<string> Result = [];
		Dictionary<string, string> Seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (DiffFile File in files)
		{
			string Full = Path.GetFullPath(Path.Combine(Root, File.Path.Replace('/', Path.DirectorySeparatorChar)));
			string Relative = Path.GetRelativePath(Root, Full);
			if (Relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(Relative))
			{
				throw new UakUsageException($"the patch's {File.Path} is outside {Root}");
			}
			if (!Relative.Replace(Path.DirectorySeparatorChar, '/').Equals(File.Path, StringComparison.Ordinal))
			{
				throw new UakUsageException($"the patch's {File.Path} resolves to {Relative}: give the file's own name");
			}
			if (!Seen.TryAdd(Full, File.Path))
			{
				throw new UakUsageException($"the patch changes {Full} more than once ({Seen[Full]}, {File.Path})");
			}
			Result.Add(Full);
		}
		return Result;
	}
}
