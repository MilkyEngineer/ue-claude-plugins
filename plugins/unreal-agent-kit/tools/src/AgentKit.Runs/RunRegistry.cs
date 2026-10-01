// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text;
using System.Text.RegularExpressions;
using AgentKit.Core;

namespace AgentKit.Runs;

/// <summary>The registry of detached runs: <c>&lt;State&gt;/Runs/&lt;Name&gt;.json</c>, one record per run, with output in <c>&lt;Name&gt;.log</c>.</summary>
public sealed partial class RunRegistry
{
	/// <summary>The prefix of the lines the wrapper itself writes to a run's output, which <see cref="GetLastLine"/> skips.</summary>
	public const string WrapperLinePrefix = "UakRun '";

	/// <summary>How long a run may stay <see cref="RunState.Starting"/> before it counts as <see cref="RunState.NeverStarted"/>.</summary>
	public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(1);

	/// <summary>Creates the registry for a state directory.</summary>
	public RunRegistry(DirectoryInfo stateDirectory)
	{
		ArgumentNullException.ThrowIfNull(stateDirectory);
		Directory = Path.Combine(stateDirectory.FullName, "Runs");
	}

	/// <summary>&lt;State&gt;/Runs.</summary>
	public string Directory { get; }

	/// <summary>Whether a run name is valid: letters, digits, '_', '.' and '-', starting with a letter or digit.</summary>
	public static bool IsValidName(string? name)
	{
		return !string.IsNullOrEmpty(name) && NamePattern().IsMatch(name);
	}

	[GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
	private static partial Regex NamePattern();

	/// <summary>The record file of a run.</summary>
	public string GetRecordFile(string name)
	{
		return Path.Combine(Directory, name + ".json");
	}

	/// <summary>The default output file of a run.</summary>
	public string GetDefaultOutputFile(string name)
	{
		return Path.Combine(Directory, name + ".log");
	}

	/// <summary>A run's record, or null when there is none (or it cannot be read).</summary>
	public RunRecord? Read(string name)
	{
		return ReadFile(GetRecordFile(name));
	}

	/// <summary>A record file, or null when it is missing or cannot be read.</summary>
	public static RunRecord? ReadFile(string path)
	{
		return StateFiles.ReadJson<RunRecord>(path);
	}

	/// <summary>Writes a run's record whole.</summary>
	public void Write(RunRecord record)
	{
		ArgumentNullException.ThrowIfNull(record);
		StateFiles.WriteJson(GetRecordFile(record.Name), record);
	}

	/// <summary>Every readable record, by name.</summary>
	public List<RunRecord> List()
	{
		if (!System.IO.Directory.Exists(Directory))
		{
			return [];
		}
		List<RunRecord> records = [];
		foreach (string file in System.IO.Directory.GetFiles(Directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
		{
			RunRecord? record = ReadFile(file);
			if (record is not null)
			{
				records.Add(record);
			}
		}
		return records;
	}

	/// <summary>Whether the run's process runs: its PID names a process that started at <see cref="RunRecord.ProcessStart"/>.</summary>
	public static bool IsAlive(RunRecord record)
	{
		ArgumentNullException.ThrowIfNull(record);
		return record.Process?.IsAlive() ?? false;
	}

	/// <summary>
	/// Where a run stands now. A run whose end is recorded (<see cref="RunRecord.Ended"/> and <see cref="RunRecord.ExitCode"/>)
	/// has exited, whatever its PID shows: the wrapper records the end as its last act, and its process lingers for a moment
	/// after, so <c>uak runs start -force</c> works at once.
	/// </summary>
	public static RunState GetState(RunRecord record, DateTime nowUtc)
	{
		ArgumentNullException.ThrowIfNull(record);
		if (record.Ended is not null && record.ExitCode is not null)
		{
			return RunState.Exited;
		}
		if (record.Pid is null)
		{
			if (record.ExitCode is not null)
			{
				return RunState.Exited;
			}
			return record.Started is DateTime started && nowUtc - started > StartTimeout ? RunState.NeverStarted : RunState.Starting;
		}
		if (IsAlive(record))
		{
			return RunState.Running;
		}
		if (record.ExitCode is not null)
		{
			return RunState.Exited;
		}
		return record.Adopted ? RunState.Ended : RunState.Died;
	}

	/// <summary>
	/// The last non-empty line of a text file another process may be writing (UTF-8 or UTF-16), skipping the wrapper's own
	/// lines. With <paramref name="prefer"/>, the last line matching it, if any. Empty when there is none.
	/// </summary>
	public static string GetLastLine(string? path, Regex? prefer = null)
	{
		if (string.IsNullOrEmpty(path))
		{
			return "";
		}
		byte[] data;
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long start = Math.Max(0, stream.Length - 65536);
			stream.Seek(start, SeekOrigin.Begin);
			using MemoryStream buffer = new();
			stream.CopyTo(buffer);
			data = buffer.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return "";
		}
		bool utf16 = (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) || data.Take(200).Contains((byte)0);
		string text = utf16 ? Encoding.Unicode.GetString(data) : Encoding.UTF8.GetString(data);
		string[] lines = [.. text.Split('\n')
			.Select(line => line.Trim().Trim('\0', '﻿'))
			.Where(line => line.Length > 0 && !line.StartsWith(WrapperLinePrefix, StringComparison.Ordinal))
			.TakeLast(40)];
		if (prefer is not null)
		{
			string? match = lines.LastOrDefault(line => prefer.IsMatch(line));
			if (match is not null)
			{
				return match;
			}
		}
		return lines.Length > 0 ? lines[^1] : "";
	}

	/// <summary>The verdict lines of a result file.</summary>
	[GeneratedRegex(@"\b(PASSED|FAILED)\b")]
	public static partial Regex VerdictPattern();

	/// <summary>
	/// The line to show for a run: the recorded last line of a finished run, else the output's current last line; for a run
	/// with no output, the last PASSED/FAILED line of its result file.
	/// </summary>
	public static string GetDisplayLine(RunRecord record, RunState state)
	{
		ArgumentNullException.ThrowIfNull(record);
		string line = state != RunState.Running && !string.IsNullOrEmpty(record.LastLine) ? record.LastLine : GetLastLine(record.OutputFile);
		if (line.Length == 0 && !string.IsNullOrEmpty(record.ResultFile))
		{
			line = GetLastLine(record.ResultFile, VerdictPattern());
			if (line.Length == 0)
			{
				line = "(result file not written yet)";
			}
		}
		return line;
	}
}
