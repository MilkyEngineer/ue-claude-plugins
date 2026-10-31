// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentKit.Unreal;

/// <summary>How bad a diagnostic is.</summary>
public enum DiagnosticSeverity
{
	/// <summary>A warning.</summary>
	Warning,

	/// <summary>An error or fatal error.</summary>
	Error,
}

/// <summary>One compiler, linker or UBT diagnostic, de-duplicated across translation units.</summary>
/// <param name="File">The file it names (a source, header, object or binary), or "UnrealBuildTool" for UBT's own errors.</param>
/// <param name="Line">The line, when it has one.</param>
/// <param name="Column">The column, when it has one.</param>
/// <param name="Severity">Error or warning.</param>
/// <param name="Code">The tool's code (C2440, LNK2019), when it has one.</param>
/// <param name="Message">The message.</param>
/// <param name="Unit">The action that reported it first (e.g. "Foo.cpp" of "Compile [x64] Foo.cpp"), when known.</param>
public sealed record CompileDiagnostic(string File, int? Line, int? Column, DiagnosticSeverity Severity, string? Code, string Message, string? Unit)
{
	/// <summary>How many times it was reported (a header's error repeats in every unit that includes it).</summary>
	public int Count { get; init; } = 1;

	/// <summary>"file(line): error: CODE: message", the form every agent reads.</summary>
	public override string ToString()
	{
		string Where = Line is int L ? $"{File}({L})" : File;
		string Kind = Severity == DiagnosticSeverity.Error ? "error" : "warning";
		return $"{Where}: {Kind}: {(Code is null ? "" : Code + ": ")}{Message}";
	}
}

/// <summary>One action UBT ran, from its "[i/n] Kind [arch] Item" line.</summary>
/// <param name="Kind">"Compile", "Link", "WriteMetadata", ...</param>
/// <param name="Item">The file it works on, e.g. "Foo.cpp" or "UnrealEditor-Foo.dll".</param>
public sealed record UbtAction(string Kind, string Item);

/// <summary>What a UBT run's output says.</summary>
public sealed class BuildLogSummary
{
	/// <summary>Errors, in first-seen order.</summary>
	public List<CompileDiagnostic> Errors { get; } = [];

	/// <summary>Warnings, in first-seen order.</summary>
	public List<CompileDiagnostic> Warnings { get; } = [];

	/// <summary>The actions UBT ran.</summary>
	public List<UbtAction> Actions { get; } = [];

	/// <summary>
	/// The items of the actions an error was reported under, even one that repeats another's (<see cref="Errors"/> keeps the
	/// first): UBT prints an action's output straight after its line, whole (ActionLogger).
	/// </summary>
	public HashSet<string> FailedActions { get; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The items of the actions with a line in their output that reads as a failure, but that no pattern parsed (MSVC's
	/// "c1xx: fatal error C1083: ...", "cl : Command line error D8021 : ...", a crashed compiler's own words). Not in
	/// <see cref="Errors"/>: it is only a guess, which a build that succeeds shows was wrong.
	/// </summary>
	public HashSet<string> UnparsedFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Whether UBT said an action exited with an error code: its own line for a tool that crashed with no output
	/// (ActionLogger), which isn't under that action's line for sure, as tool output goes through a parser that holds lines
	/// back. So it tells no action.
	/// </summary>
	public bool ActionExitedWithError { get; set; }

	/// <summary>
	/// Whether each failure the log shows is known by its action: an action that failed with nothing parsed may be any of those
	/// the log lists, so a failed build's list of them says nothing of which compiled.
	/// </summary>
	public bool EveryFailureAttributed => !ActionExitedWithError && UnparsedFailures.IsSubsetOf(FailedActions);

	/// <summary>UBT's "Result: X" word (Succeeded, Failed, Cancelled), or null when it printed none.</summary>
	public string? Result { get; set; }

	/// <summary>The reason in "Result: Failed (Reason)", or null.</summary>
	public string? ResultReason { get; set; }

	/// <summary>Whether UBT said "Target is up to date".</summary>
	public bool UpToDate { get; set; }

	/// <summary>UBT's "Total execution time", in seconds, or null.</summary>
	public double? TotalSeconds { get; set; }

	/// <summary>Whether UBT reported success.</summary>
	public bool Succeeded => string.Equals(Result, "Succeeded", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses UBT output line by line into a <see cref="BuildLogSummary"/>. Understands MSVC and clang-cl
/// ("file(line,col): error C1234: ..."), clang and gcc ("file:line:col: error: ..."), the MSVC linker ("x.obj : error
/// LNK2019: ..."), lld, and UBT's own "ERROR:" lines. Notes and template-context lines are skipped.
/// </summary>
public sealed partial class BuildLogParser
{
	readonly Dictionary<string, int> Seen = new(StringComparer.Ordinal);
	string? CurrentUnit;

	/// <summary>The summary so far.</summary>
	public BuildLogSummary Summary { get; } = new();

	/// <summary>Parses a whole log.</summary>
	public static BuildLogSummary Parse(IEnumerable<string> lines)
	{
		BuildLogParser Parser = new();
		foreach (string Line in lines)
		{
			Parser.AddLine(Line);
		}
		return Parser.Summary;
	}

	/// <summary>Takes the next line of output.</summary>
	public void AddLine(string line)
	{
		string Text = line.TrimEnd();
		Match M;
		if (RetriedPattern().IsMatch(Text))
		{
			// UBA's own note: the action runs again, and that run says how it ended. Neither a failure nor a word that tells of one.
			return;
		}
		if (ExitedPattern().IsMatch(Text))
		{
			Summary.ActionExitedWithError = true;
		}
		if ((M = ActionPattern().Match(Text)).Success)
		{
			UbtAction Action = new(M.Groups["kind"].Value, M.Groups["item"].Value.Trim());
			Summary.Actions.Add(Action);
			CurrentUnit = Action.Item;
		}
		else if ((M = MsvcPattern().Match(Text)).Success || (M = GccPattern().Match(Text)).Success || (M = LinkerPattern().Match(Text)).Success)
		{
			Add(M.Groups["file"].Value.Trim(), ParseInt(M.Groups["line"]), ParseInt(M.Groups["col"]), M.Groups["sev"].Value,
				M.Groups["code"].Success ? M.Groups["code"].Value : null, M.Groups["msg"].Value.Trim());
		}
		else if ((M = UbtErrorPattern().Match(Text)).Success)
		{
			Add("UnrealBuildTool", null, null, "error", null, M.Groups["msg"].Value.Trim());
		}
		else if ((M = ResultPattern().Match(Text)).Success)
		{
			Summary.Result = M.Groups["result"].Value;
			Summary.ResultReason = M.Groups["why"].Success ? M.Groups["why"].Value : null;
		}
		else if (Text.Contains("Target is up to date", StringComparison.Ordinal))
		{
			Summary.UpToDate = true;
		}
		else if ((M = TotalTimePattern().Match(Text)).Success)
		{
			Summary.TotalSeconds = double.Parse(M.Groups["seconds"].Value, CultureInfo.InvariantCulture);
		}
		else if (CurrentUnit is not null && FailureWordPattern().IsMatch(Text) && !NotePattern().IsMatch(Text))
		{
			Summary.UnparsedFailures.Add(CurrentUnit);
		}
	}

	void Add(string file, int? line, int? column, string severity, string? code, string message)
	{
		DiagnosticSeverity Severity = severity.Contains("error", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
		List<CompileDiagnostic> List = Severity == DiagnosticSeverity.Error ? Summary.Errors : Summary.Warnings;
		if (Severity == DiagnosticSeverity.Error && CurrentUnit is not null)
		{
			Summary.FailedActions.Add(CurrentUnit);
		}
		string Key = $"{Severity}|{file.ToUpperInvariant()}|{line}|{column}|{code}|{message}";
		if (Seen.TryGetValue(Key, out int Index))
		{
			List[Index] = List[Index] with { Count = List[Index].Count + 1 };
			return;
		}
		Seen[Key] = List.Count;
		List.Add(new CompileDiagnostic(file, line, column, Severity, code, message, CurrentUnit));
	}

	static int? ParseInt(Group group) => group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : null;

	// "[3/30] Compile [x64] Foo.cpp", "[6/30] Link [x64] UnrealEditor-Foo.dll", "[1/1] Compile Foo.cpp" (clang hosts). UE 5.8's
	// UBT may add the target ("[3/30][FooEditor Win64 Development] ...", bPrintActionTargetNames) and times ("[3/30] (Wall:
	// 1.20s CPU: 1.10s) ...", bShowPerActionCompilationTimes) after the count, and where the action ran after the item
	// (" [Cache]", " [NoUba]", " [RemoteExecutor: host]", ActionLogger's AdditionalDescription), none of them part of it.
	[GeneratedRegex(@"^\[\d+/\d+\](?:\[[^\]]*\])?(?:\s+\(Wall:[^)]*\))?\s+(?<kind>\S+)\s+(?:\[[^\]]*\]\s+)?(?<item>.+?)(?:\s+\[[^\]]*\])?$")]
	private static partial Regex ActionPattern();

	// "C:\x\Foo.cpp(337,63): error C2440: ...", clang-cl "Foo.cpp(12,5): error: ...", UHT "Foo.h(12): Error: ...".
	[GeneratedRegex(@"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+)(?:,(?<col>\d+))?\)\s*:\s*(?<sev>fatal error|error|warning)(?:\s+(?<code>[A-Z]+\d+))?\s*:\s*(?<msg>.*)$", RegexOptions.IgnoreCase)]
	private static partial Regex MsvcPattern();

	// "/src/Foo.cpp:12:5: error: ...", "C:\src\Foo.cpp:12: warning: ...".
	[GeneratedRegex(@"^\s*(?<file>\S.*?):(?<line>\d+):(?:(?<col>\d+):)?\s*(?<sev>fatal error|error|warning):\s*(?<msg>.*)$", RegexOptions.IgnoreCase)]
	private static partial Regex GccPattern();

	// "Foo.cpp.obj : error LNK2019: ...", "x.dll : fatal error LNK1120: ...", "ld.lld: error: undefined symbol: ...".
	[GeneratedRegex(@"^\s*(?<file>\S[^:]*?|[A-Za-z]:\\[^:]*?)\s*:\s*(?<sev>fatal error|error|warning)(?:\s+(?<code>LNK\d+))?\s*:\s*(?<msg>.*)$")]
	private static partial Regex LinkerPattern();

	// UBT's own errors, e.g. "ERROR: Failed to find an Action ..." or "Error: Unable to ...".
	[GeneratedRegex(@"^\s*(?:ERROR|Error):\s*(?<msg>.+)$")]
	private static partial Regex UbtErrorPattern();

	[GeneratedRegex(@"^Result:\s*(?<result>\w+)(?:\s*\((?<why>[^)]*)\))?")]
	private static partial Regex ResultPattern();

	[GeneratedRegex(@"^Total execution time:\s*(?<seconds>[\d.]+)\s*seconds")]
	private static partial Regex TotalTimePattern();

	// "Compile [x64] Foo.cpp: Exited with error code -1073741819 (Access violation). The build will fail." (ActionLogger.cs).
	[GeneratedRegex(@"\bExited with error code\b")]
	private static partial Regex ExitedPattern();

	// "Compile [x64] Foo.cpp: Exited with error code 9001. This action will retry (More info in uba trace)", "... This action will
	// retry without UBA", "... with no output. This action will retry locally" (UBAExecutor.cs).
	[GeneratedRegex(@"\bExited with error code\b.*\bThis action will retry\b")]
	private static partial Regex RetriedPattern();

	// A word that may tell of a failure, in a line no pattern above took. Wide on purpose: a source line a warning quotes may
	// match, which at worst takes a failed build's compiles from the evidence (BuildOutcome.CompiledClean).
	[GeneratedRegex(@"\b(?:error|fatal|crash(?:ed)?|abort(?:ed)?|failed|failure|segmentation fault|access violation|out of memory|stack dump)\b", RegexOptions.IgnoreCase)]
	private static partial Regex FailureWordPattern();

	// A compiler's context line ("Foo.h(3): note: see declaration of 'Error'"), which fails nothing.
	[GeneratedRegex(@"\bnote\s*:", RegexOptions.IgnoreCase)]
	private static partial Regex NotePattern();
}
