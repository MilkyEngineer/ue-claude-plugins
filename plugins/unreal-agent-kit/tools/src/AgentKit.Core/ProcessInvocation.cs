// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text;

namespace AgentKit.Core;

/// <summary>
/// A program to run: its path, arguments, working directory and extra environment.
/// <para>
/// On Windows the arguments become one command line with Unreal-style quoting (<see cref="ToWindowsCommandLine"/>): an
/// argument "-Key=value with spaces" is written -Key="value with spaces", and any other argument with spaces or quotes is
/// quoted whole. Programs that split their command line the MSVC way (CommandLineToArgvW: .NET, C and C++ programs, git, p4)
/// read every argument back exactly, because -Key="value" and "-Key=value" parse to the same argument. On Linux and Mac
/// the arguments are passed as a list, unquoted.
/// </para>
/// </summary>
/// <param name="FileName">The executable or script (.bat/.cmd on Windows run through cmd.exe). A bare name is looked up on PATH only, never in the current directory (<see cref="ExecutableLocator"/>).</param>
/// <param name="Arguments">The arguments, unquoted.</param>
/// <param name="WorkingDirectory">The working directory, or null for the current one.</param>
/// <param name="Environment">Variables to set (or, with a null value, remove) for the child, on top of this process's environment.</param>
public sealed record ProcessInvocation(
	string FileName,
	IReadOnlyList<string> Arguments,
	string? WorkingDirectory = null,
	IReadOnlyDictionary<string, string?>? Environment = null)
{
	/// <summary>
	/// The arguments as one Windows command line, quoted as Unreal programs expect: "-Key=value with spaces" becomes
	/// -Key="value with spaces", because Unreal programs read their raw command line and FParse::Value only takes a quoted
	/// value after the '='. Other arguments with spaces or quotes are quoted whole, by the CommandLineToArgvW rules, which
	/// .NET and C programs read back exactly.
	/// </summary>
	public string ToWindowsCommandLine() => string.Join(' ', Arguments.Select(QuoteForUnreal));

	/// <summary>A readable one-line form, for logs and messages.</summary>
	public override string ToString() => QuoteWindows(FileName) + (Arguments.Count == 0 ? "" : " " + ToWindowsCommandLine());

	/// <summary>A copy with more environment variables, which win over this invocation's own.</summary>
	public ProcessInvocation WithEnvironment(IReadOnlyDictionary<string, string?> variables)
	{
		Dictionary<string, string?> Merged = new(Environment ?? new Dictionary<string, string?>(), EnvironmentComparer);
		foreach ((string Key, string? Value) in variables)
		{
			Merged[Key] = Value;
		}
		return this with { Environment = Merged };
	}

	/// <summary>How environment variable names compare on the host: case-insensitively on Windows.</summary>
	public static StringComparer EnvironmentComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	/// <summary>Quotes one argument for an Unreal program; see <see cref="ToWindowsCommandLine"/>.</summary>
	public static string QuoteForUnreal(string argument)
	{
		if (argument.Length > 0 && !NeedsQuotes(argument))
		{
			return argument;
		}
		int Equals = argument.IndexOf('=', StringComparison.Ordinal);
		if (argument.StartsWith('-') && Equals > 1 && !NeedsQuotes(argument[..Equals]) && !argument.Contains('"', StringComparison.Ordinal))
		{
			return argument[..(Equals + 1)] + QuoteWindows(argument[(Equals + 1)..]);
		}
		return QuoteWindows(argument);
	}

	/// <summary>Quotes a string as one argument by the Windows (CommandLineToArgvW) rules; returns it unchanged when it needs no quotes.</summary>
	public static string QuoteWindows(string text)
	{
		if (text.Length > 0 && !NeedsQuotes(text))
		{
			return text;
		}
		StringBuilder Result = new("\"");
		int Backslashes = 0;
		foreach (char C in text)
		{
			if (C == '\\')
			{
				Backslashes++;
				continue;
			}
			// Backslashes are literal unless they precede a quote, where each must be doubled and the quote escaped.
			Result.Append('\\', C == '"' ? Backslashes * 2 + 1 : Backslashes);
			Backslashes = 0;
			Result.Append(C);
		}
		// Backslashes before the closing quote are doubled so the quote stays a delimiter.
		Result.Append('\\', Backslashes * 2);
		return Result.Append('"').ToString();
	}

	/// <summary>The arguments as one command line for a .bat or .cmd file, which cmd.exe parses (see <see cref="QuoteForCmd"/>).</summary>
	public string ToCmdCommandLine() => string.Join(' ', Arguments.Select(QuoteForCmd));

	/// <summary>
	/// Quotes one argument for a batch file run through cmd.exe. cmd treats &amp; | &lt; &gt; ^ ( ) as syntax, and , ; as
	/// argument separators, everywhere except inside double quotes, so any argument holding one of them (or a space, tab or
	/// quote) is quoted. A quote inside the argument is doubled (""), which keeps cmd inside the quoted text. Programs that
	/// split their command line the MSVC way read "" there as one quote, so an argument a script forwards with %* reaches
	/// them intact; the script itself sees %~1 with the quotes doubled. "-Key=value" keeps Unreal's -Key="value" form for
	/// scripts that forward %* to Unreal programs; cmd splits %1..%9 at '=' (as it always does), but %* keeps it whole.
	/// <para>
	/// Limits: cmd expands %NAME% even inside quotes when NAME is a defined variable, and a script with delayed expansion
	/// enabled expands !NAME!. Neither can be escaped here, so avoid % and ! in arguments to scripts. A script that parses
	/// its arguments again (call :label %*) may also double carets.
	/// </para>
	/// </summary>
	public static string QuoteForCmd(string argument)
	{
		if (argument.Length > 0 && !NeedsCmdQuotes(argument))
		{
			return argument;
		}
		int Equals = argument.IndexOf('=', StringComparison.Ordinal);
		if (argument.StartsWith('-') && Equals > 1 && !NeedsCmdQuotes(argument[..Equals]) && !argument.Contains('"', StringComparison.Ordinal))
		{
			return argument[..(Equals + 1)] + QuoteCmdWhole(argument[(Equals + 1)..]);
		}
		return QuoteCmdWhole(argument);
	}

	static string QuoteCmdWhole(string text)
	{
		string Body = text.Replace("\"", "\"\"", StringComparison.Ordinal);
		// Backslashes before the closing quote are doubled, so a program reading the MSVC way still sees the quote as the end.
		int Trailing = Body.Length - Body.TrimEnd('\\').Length;
		return "\"" + Body + new string('\\', Trailing) + "\"";
	}

	static bool NeedsCmdQuotes(string text) => text.Any(C => C is ' ' or '\t' or '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')' or ',' or ';');

	static bool NeedsQuotes(string text) => text.Any(C => C == ' ' || C == '\t' || C == '"');
}
