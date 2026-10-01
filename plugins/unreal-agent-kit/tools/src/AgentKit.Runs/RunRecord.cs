// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json.Serialization;
using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Runs;

/// <summary>
/// One detached run, as <c>&lt;State&gt;/Runs/&lt;Name&gt;.json</c> records it (DESIGN.md, "Detached runs"). Always written whole
/// (temporary file, then rename). The fields up to <see cref="LastLine"/> are the run-record protocol that other tools may
/// read and write too; the rest are optional additions that readers may ignore.
/// </summary>
public sealed class RunRecord
{
	/// <summary>The run's unique name: letters, digits, '_', '.' and '-'.</summary>
	public string Name { get; set; } = "";

	/// <summary>Who started it (for example an epic, "E3", or "lead").</summary>
	public string Owner { get; set; } = "";

	/// <summary>The run's top process: the wrapper, or the adopted process. Null until the wrapper has recorded itself.</summary>
	public int? Pid { get; set; }

	/// <summary>When <see cref="Pid"/> started (UTC). With the PID, it tells the run's process from a later one reusing the ID.</summary>
	public DateTime? ProcessStart { get; set; }

	/// <summary>The command (the program run).</summary>
	public string Command { get; set; } = "";

	/// <summary>The command's arguments, exactly as passed.</summary>
	public List<string> Arguments { get; set; } = [];

	/// <summary>The command and arguments as one line, for reading only.</summary>
	public string CommandLine { get; set; } = "";

	/// <summary>Where the run's standard output and error go. Null for an adopted run with no known output.</summary>
	public string? OutputFile { get; set; }

	/// <summary>A file the run writes its verdict to, whose last PASSED/FAILED line stands in for a missing output file. Optional.</summary>
	public string? ResultFile { get; set; }

	/// <summary>The priority the run's lock requests take (UAK_LOCK_PRIORITY), or null for the default.</summary>
	public LockPriority? Priority { get; set; }

	/// <summary>Whether the run was started some other way and only recorded (<c>uak runs adopt</c>): no end is ever recorded.</summary>
	public bool Adopted { get; set; }

	/// <summary>When the run was started (UTC).</summary>
	public DateTime? Started { get; set; }

	/// <summary>When the run ended (UTC). Null until the wrapper records the end.</summary>
	public DateTime? Ended { get; set; }

	/// <summary>The command's exit code. Null until the wrapper records the end.</summary>
	public int? ExitCode { get; set; }

	/// <summary>The last line of the run's output at its end. Null until the wrapper records the end.</summary>
	public string? LastLine { get; set; }

	/// <summary>Addition: the directory the command runs in.</summary>
	public string? WorkingDirectory { get; set; }

	/// <summary>
	/// Addition: how the wrapper was detached: "breakaway" (out of the caller's job), "job" (inside it: the job refused
	/// breakaway, so the run ends with the job), or "session" (a new Unix session).
	/// </summary>
	public string? Detach { get; set; }

	/// <summary>The run's process as an identity, or null before the wrapper recorded itself.</summary>
	[JsonIgnore]
	public ProcessIdentity? Process => Pid is int pid ? new ProcessIdentity(pid, ProcessStart) : null;
}

/// <summary>Where a run stands, from <see cref="RunRegistry.GetState"/>.</summary>
public enum RunState
{
	/// <summary>Recorded, and its wrapper has not recorded itself yet.</summary>
	Starting,

	/// <summary>Its process runs.</summary>
	Running,

	/// <summary>It ended and its exit code is recorded.</summary>
	Exited,

	/// <summary>An adopted run whose process has gone (no exit code is known).</summary>
	Ended,

	/// <summary>Its process has gone without recording an end: it was killed, or its wrapper crashed.</summary>
	Died,

	/// <summary>Its wrapper never recorded itself (a minute after the start).</summary>
	NeverStarted,
}
