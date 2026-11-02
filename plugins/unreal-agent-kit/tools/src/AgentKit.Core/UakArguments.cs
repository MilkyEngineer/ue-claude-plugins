// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;

namespace AgentKit.Core;

/// <summary>What <see cref="UakArguments"/> makes of a '.' token right after an option with a value: see its remarks.</summary>
public enum DotSplitRule
{
	/// <summary>Nothing is joined, as in 0.4.1: the token is a positional. What a command that gives no rule gets, so one built against an older kit, or one that takes dot paths, keeps its meaning.</summary>
	None,

	/// <summary>The command takes no positional: the token is the option's tail, as a positional would be refused anyway.</summary>
	Options,

	/// <summary>
	/// The command takes paths as positionals: the token stays one when it names a path (<see cref="UakArguments.NamesPath"/>), so
	/// what it means depends on the folder. That is safe here: a wrong guess ends in an error, not in another run.
	/// </summary>
	Paths,

	/// <summary>
	/// The command takes a command to run, which must not change with the folder, so the disk is never asked. With a lone "--" the
	/// command is what follows it, so the token is the option's tail; without one, a token written as a path
	/// (<see cref="UakArguments.IsPathForm"/>) starts the command, and any other can be read both ways and is refused.
	/// </summary>
	Command,
}

/// <summary>
/// A command's arguments in UE style: options as -Key=Value or -Flag (case-insensitive; a leading "--" works as "-"),
/// everything else positional. A lone "--" ends the options: what follows is <see cref="Rest"/>, verbatim, for commands that
/// run another command (`uak lock run ... -- &lt;command&gt;`). Read every option (or list it with <see cref="Accept"/>) before
/// <see cref="ThrowIfUnknown"/>, which rejects the rest as a usage error.
/// </summary>
/// <remarks>
/// Windows PowerShell 5.1 splits an unquoted <c>-key=a.b</c> at the first dot for a native program, so uak receives
/// <c>-key=a</c> and <c>.b</c> (and <c>-key=..\a</c> as <c>-key=</c> and <c>..\a</c>). Each of the kit's commands parses with a
/// <see cref="DotSplitRule"/>, which joins them again (see <see cref="RejoinSplit"/>), before any "--". A command that gives no
/// rule, as one built against an older kit does, parses as it always did.
/// </remarks>
public sealed class UakArguments
{
	readonly Dictionary<string, string?> Options = new(StringComparer.OrdinalIgnoreCase);
	readonly HashSet<string> Read = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Parses the arguments that follow the command's name, joining nothing (<see cref="DotSplitRule.None"/>).</summary>
	/// <param name="arguments">The arguments.</param>
	public UakArguments(IEnumerable<string> arguments)
		: this(arguments, DotSplitRule.None)
	{
	}

	/// <summary>Parses the arguments that follow the command's name, joining what Windows PowerShell 5.1 split as <paramref name="dotSplit"/> says.</summary>
	/// <param name="arguments">The arguments.</param>
	/// <param name="dotSplit">What a '.' token right after an option with a value is.</param>
	/// <exception cref="UakUsageException">Under <see cref="DotSplitRule.Command"/>, such a token can be read two ways.</exception>
	public UakArguments(IEnumerable<string> arguments, DotSplitRule dotSplit)
	{
		List<string> Positionals = [];
		List<string> After = [];
		bool InRest = false;
		foreach (string Argument in Rejoin(arguments, dotSplit))
		{
			if (InRest)
			{
				After.Add(Argument);
			}
			else if (Argument == "--")
			{
				InRest = true;
				HasSeparator = true;
			}
			else if (IsOption(Argument))
			{
				string Body = Argument.TrimStart('-');
				int Equals = Body.IndexOf('=', StringComparison.Ordinal);
				string Key = Equals < 0 ? Body : Body[..Equals];
				if (!Options.TryAdd(Key, Equals < 0 ? null : Body[(Equals + 1)..]))
				{
					throw new UakUsageException($"-{Key} is given more than once.");
				}
			}
			else
			{
				Positionals.Add(Argument);
			}
		}
		Positional = Positionals;
		Rest = After;
	}

	/// <summary>The arguments before any "--" that are not options, in order.</summary>
	public IReadOnlyList<string> Positional { get; }

	/// <summary>Everything after the first lone "--", verbatim. Empty when there is none.</summary>
	public IReadOnlyList<string> Rest { get; }

	/// <summary>
	/// <see cref="Rest"/> for a command that passes it to UBT, the editor or the game: with arguments PowerShell 5.1 split joined
	/// again (<see cref="RejoinSplit"/>). A command that runs another command keeps <see cref="Rest"/> verbatim: a uak it runs
	/// joins them itself.
	/// </summary>
	public IReadOnlyList<string> PassThrough => RejoinSplit(Rest, IsPathForm);

	/// <summary>Whether a lone "--" was given.</summary>
	public bool HasSeparator { get; }

	/// <summary>The cause of a stray '.' argument and the forms that avoid it, for a refusal that names one.</summary>
	public const string DotSplitHint =
		"Windows PowerShell 5.1 splits an unquoted argument at a dot: quote it ('-key=a.b') or give it two dashes (--key=a.b)";

	/// <summary>
	/// Joins each <c>-key=value</c> before a lone "--" with a '.' token right after it, once per option: what Windows PowerShell
	/// 5.1 makes of an unquoted <c>-key=value.tail</c> for a native program (it never splits after a ':', so a drive path and
	/// <c>-filter=StartsWith:A.B</c> arrive whole, and only the first dot splits, so the tail is one token). With an empty value
	/// the token is always that option's, as the value would be refused otherwise; with a value,
	/// <paramref name="keepApart"/> can leave it where it is. Everything from a lone "--" on stays as it is.
	/// </summary>
	public static IReadOnlyList<string> RejoinSplit(IEnumerable<string> arguments, Func<string, bool>? keepApart = null)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		List<string> Joined = [];
		bool InRest = false;
		bool CanJoin = false;
		foreach (string Argument in arguments)
		{
			if (CanJoin && Argument.StartsWith('.') && (Joined[^1].EndsWith('=') || keepApart?.Invoke(Argument) != true))
			{
				Joined[^1] += Argument;
				CanJoin = false;
				continue;
			}
			InRest |= Argument == "--";
			CanJoin = !InRest && IsOption(Argument) && Argument.Contains('=', StringComparison.Ordinal);
			Joined.Add(Argument);
		}
		return Joined;
	}

	static IReadOnlyList<string> Rejoin(IEnumerable<string> arguments, DotSplitRule rule)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		List<string> Given = [.. arguments];
		return rule switch
		{
			DotSplitRule.None => Given,
			DotSplitRule.Options => RejoinSplit(Given),
			DotSplitRule.Paths => RejoinSplit(Given, NamesPath),
			DotSplitRule.Command => RejoinSplit(Given, Given.Contains("--") ? IsPathForm : StartsACommandOrRefuse),
			_ => throw new ArgumentOutOfRangeException(nameof(rule)),
		};
	}

	static bool StartsACommandOrRefuse(string token)
	{
		return IsPathForm(token) ? true : throw new UakUsageException($"{token}, right after an option with a value, could be that option's value split at a dot ({DotSplitHint}) or the start of"
			+ " the command to run, so nothing was run. Give the command after a lone \"--\": what comes before it is then uak's");
	}

	/// <summary>Whether a '.' token is written as a path: ".", "..", or starting with ".\", "./", "..\" or "../".</summary>
	public static bool IsPathForm(string token)
	{
		ArgumentNullException.ThrowIfNull(token);
		return token is "." or ".." || token.StartsWith(@".\", StringComparison.Ordinal) || token.StartsWith("./", StringComparison.Ordinal)
			|| token.StartsWith(@"..\", StringComparison.Ordinal) || token.StartsWith("../", StringComparison.Ordinal);
	}

	/// <summary>Whether a '.' token is a path, not an option's tail: written as one (<see cref="IsPathForm"/>), or named so on disk (a .gitignore).</summary>
	public static bool NamesPath(string token) => IsPathForm(token) || File.Exists(token) || Directory.Exists(token);

	/// <summary>Whether -Key or -Key=Value is present. Counts as reading it.</summary>
	public bool Has(string key)
	{
		Read.Add(key);
		return Options.ContainsKey(key);
	}

	/// <summary>The value of -Key=Value, or the default when absent. A bare -Key is a usage error.</summary>
	public string? GetString(string key, string? defaultValue = null)
	{
		Read.Add(key);
		if (!Options.TryGetValue(key, out string? Value))
		{
			return defaultValue;
		}
		if (string.IsNullOrEmpty(Value))
		{
			throw new UakUsageException($"-{key} needs a value (-{key}=...).");
		}
		return Value;
	}

	/// <summary>The value of -Key=Value; a usage error when absent.</summary>
	public string GetRequiredString(string key) => GetString(key) ?? throw new UakUsageException($"-{key}=... is required.");

	/// <summary>Whether the flag -Key is present. -Key=true and -Key=false are accepted too.</summary>
	public bool GetFlag(string key)
	{
		Read.Add(key);
		if (!Options.TryGetValue(key, out string? Value))
		{
			return false;
		}
		if (Value is null)
		{
			return true;
		}
		if (bool.TryParse(Value, out bool Parsed))
		{
			return Parsed;
		}
		throw new UakUsageException($"-{key} is a flag; give it without a value.");
	}

	/// <summary>The integer value of -Key=N, or null when absent. A usage error when it is not a whole number in [min, max].</summary>
	public int? GetInt(string key, int min = int.MinValue, int max = int.MaxValue)
	{
		string? Text = GetString(key);
		if (Text is null)
		{
			return null;
		}
		if (!int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int Value) || Value < min || Value > max)
		{
			throw new UakUsageException($"-{key} must be a whole number from {min} to {max}, not '{Text}'.");
		}
		return Value;
	}

	/// <summary>The enum value of -Key=Name (case-insensitive), or the default when absent. A usage error for other names, listing the valid ones.</summary>
	public T GetEnum<T>(string key, T defaultValue) where T : struct, Enum
	{
		string? Text = GetString(key);
		if (Text is null)
		{
			return defaultValue;
		}
		if (!int.TryParse(Text, out _) && Enum.TryParse(Text, ignoreCase: true, out T Value))
		{
			return Value;
		}
		throw new UakUsageException($"-{key} must be one of {string.Join(", ", Enum.GetNames<T>())}, not '{Text}'.");
	}

	/// <summary>
	/// Every -&lt;prefix&gt;&lt;name&gt;=Value option, such as -param:clean=true for the prefix "param:", as (name, value) in the
	/// order given; counts as reading them. A value may be empty (-param:name=); a missing name or a missing '=' is a usage
	/// error.
	/// </summary>
	public IReadOnlyList<KeyValuePair<string, string>> GetPrefixed(string prefix)
	{
		List<KeyValuePair<string, string>> Found = [];
		foreach ((string Key, string? Value) in Options)
		{
			if (!Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			Read.Add(Key);
			string Name = Key[prefix.Length..];
			if (Name.Length == 0 || Value is null)
			{
				throw new UakUsageException($"-{prefix}<name>=<value> needs a name and a value, not -{Key}{(Value is null ? "" : "=" + Value)}.");
			}
			Found.Add(new KeyValuePair<string, string>(Name, Value));
		}
		return Found;
	}

	/// <summary>Marks options as known without reading them, so <see cref="ThrowIfUnknown"/> allows them.</summary>
	public void Accept(params string[] keys)
	{
		foreach (string Key in keys)
		{
			Read.Add(Key);
		}
	}

	/// <summary>Rejects options no getter asked for, naming them.</summary>
	public void ThrowIfUnknown()
	{
		List<string> Unknown = Options.Keys.Where(Key => !Read.Contains(Key)).Select(Key => "-" + Key).ToList();
		if (Unknown.Count > 0)
		{
			throw new UakUsageException("Unknown option" + (Unknown.Count > 1 ? "s " : " ") + string.Join(", ", Unknown) + ".");
		}
	}

	/// <summary>Rejects positional arguments beyond the first <paramref name="count"/>.</summary>
	public void ThrowIfMorePositionalThan(int count)
	{
		if (Positional.Count > count)
		{
			throw new UakUsageException("Unexpected argument" + (Positional.Count - count > 1 ? "s " : " ") + string.Join(" ", Positional.Skip(count)) + "." + PositionalHint(Positional.Skip(count)));
		}
	}

	/// <summary>
	/// Rejects any positional argument, naming the command: the refusal of a command that takes none. A '.' argument left here
	/// followed no option with a value, so it can't be joined to one; the message says why it may be there.
	/// </summary>
	public void ThrowIfPositional(string command)
	{
		if (Positional.Count > 0)
		{
			throw new UakUsageException($"{command} takes no positional arguments: {string.Join(' ', Positional)}.{PositionalHint(Positional)}");
		}
	}

	static string PositionalHint(IEnumerable<string> arguments) => arguments.Any(Argument => Argument.StartsWith('.')) ? $" {DotSplitHint}." : "";

	/// <summary>Whether an argument is an option: a '-' followed by a letter (so "-1" and "-" are positional).</summary>
	public static bool IsOption(string argument)
	{
		string Body = argument.StartsWith("--", StringComparison.Ordinal) ? argument[2..] : argument.StartsWith('-') ? argument[1..] : "";
		return Body.Length > 0 && char.IsLetter(Body[0]);
	}
}
