// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using EpicGames.Perforce;

namespace AgentKit.Vcs;

/// <summary>
/// The Perforce settings (P4PORT, P4USER, P4CLIENT, ...) that apply in a directory: the global environment (environment
/// variables, <c>p4 set</c> on Windows, P4ENVIRO), overridden by every P4CONFIG file from the file-system root down to the
/// directory, as <c>p4</c> itself resolves them.
/// </summary>
/// <remarks>
/// EpicGames.Perforce's <see cref="PerforceEnvironment.FromDirectory"/> does the same, but caches per directory for the
/// process's lifetime and always starts from the process-wide environment. This version takes its base environment as an
/// argument, so tests can use a fake P4CONFIG, and it reports which config file it found.
/// </remarks>
public sealed class PerforceWorkspaceEnvironment : IPerforceEnvironment
{
	readonly IPerforceEnvironment _baseEnvironment;
	readonly Dictionary<string, string> _configValues = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Resolves the settings for a directory.</summary>
	/// <param name="directory">The directory to resolve for, usually the project directory.</param>
	/// <param name="baseEnvironment">The global environment; null uses <see cref="PerforceEnvironment.Default"/>.</param>
	public PerforceWorkspaceEnvironment(DirectoryInfo directory, IPerforceEnvironment? baseEnvironment = null)
	{
		_baseEnvironment = baseEnvironment ?? PerforceEnvironment.Default;

		string? configName = _baseEnvironment.GetValue("P4CONFIG");
		if (string.IsNullOrWhiteSpace(configName))
		{
			return;
		}

		// Apply config files from the root down, so the nearest one wins; P4CONFIG itself may not be changed by a config file.
		List<DirectoryInfo> chain = [];
		for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
		{
			chain.Add(current);
		}
		chain.Reverse();
		foreach (DirectoryInfo current in chain)
		{
			string candidate = Path.Combine(current.FullName, configName);
			if (File.Exists(candidate))
			{
				ConfigFile = new FileInfo(candidate);
				foreach (string line in File.ReadAllLines(candidate))
				{
					string trimmed = line.Trim();
					int equals = trimmed.IndexOf('=', StringComparison.Ordinal);
					if (equals > 0 && !trimmed.StartsWith('#'))
					{
						_configValues[trimmed[..equals].TrimEnd()] = trimmed[(equals + 1)..].TrimStart();
					}
				}
			}
		}
	}

	/// <summary>The nearest P4CONFIG file at or above the directory, or null when P4CONFIG is unset or no such file exists.</summary>
	public FileInfo? ConfigFile { get; }

	/// <inheritdoc/>
	public string? GetValue(string name)
	{
		if (_configValues.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value))
		{
			return value;
		}
		return _baseEnvironment.GetValue(name);
	}

	/// <summary>Whether any server or client setting is present, which makes a <c>p4 info</c> check worth its cost.</summary>
	public bool HasConnectionSettings => GetValue("P4PORT") is not null || GetValue("P4CLIENT") is not null;
}

/// <summary>An <see cref="IPerforceEnvironment"/> over a fixed set of values, for tests and explicit settings.</summary>
public sealed class FixedPerforceEnvironment : IPerforceEnvironment
{
	readonly Dictionary<string, string> _values;

	/// <summary>Creates the environment from name/value pairs (names are case-insensitive, as in p4).</summary>
	public FixedPerforceEnvironment(IEnumerable<KeyValuePair<string, string>> values)
	{
		_values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
	}

	/// <inheritdoc/>
	public string? GetValue(string name) => _values.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value) ? value : null;
}
