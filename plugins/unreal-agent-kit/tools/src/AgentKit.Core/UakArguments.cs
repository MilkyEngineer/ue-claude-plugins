// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;

namespace AgentKit.Core;

/// <summary>
/// A command's arguments in UE style: options as -Key=Value or -Flag (case-insensitive; a leading "--" works as "-"),
/// everything else positional. A lone "--" ends the options: what follows is <see cref="Rest"/>, verbatim, for commands that
/// run another command (`uak lock run ... -- &lt;command&gt;`). Read every option (or list it with <see cref="Accept"/>) before
/// <see cref="ThrowIfUnknown"/>, which rejects the rest as a usage error.
/// </summary>
public sealed class UakArguments
{
	readonly Dictionary<string, string?> Options = new(StringComparer.OrdinalIgnoreCase);
	readonly HashSet<string> Read = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Parses the arguments that follow the command's name.</summary>
	public UakArguments(IEnumerable<string> arguments)
	{
		List<string> Positionals = [];
		List<string> After = [];
		bool InRest = false;
		foreach (string Argument in arguments)
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

	/// <summary>Whether a lone "--" was given.</summary>
	public bool HasSeparator { get; }

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
			throw new UakUsageException("Unexpected argument" + (Positional.Count - count > 1 ? "s " : " ") + string.Join(" ", Positional.Skip(count)) + ".");
		}
	}

	/// <summary>Whether an argument is an option: a '-' followed by a letter (so "-1" and "-" are positional).</summary>
	public static bool IsOption(string argument)
	{
		string Body = argument.StartsWith("--", StringComparison.Ordinal) ? argument[2..] : argument.StartsWith('-') ? argument[1..] : "";
		return Body.Length > 0 && char.IsLetter(Body[0]);
	}
}
