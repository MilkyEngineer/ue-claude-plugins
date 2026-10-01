// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using EpicGames.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Core;

/// <summary>Where a run's output goes, besides the exit code.</summary>
public sealed class ProcessOutputOptions
{
	/// <summary>Called for every output line, from stdout and (unless <see cref="OnErrorLine"/> is set) stderr. Calls never overlap.</summary>
	public Action<string>? OnLine { get; init; }

	/// <summary>When set, stderr is kept apart from stdout and its lines come here instead of <see cref="OnLine"/>.</summary>
	public Action<string>? OnErrorLine { get; init; }

	/// <summary>
	/// A file that receives every line (both streams), UTF-8 without a BOM. Its directory is created. Null for none. The run
	/// is its only writer while it lasts: others may read it, but a second writer cannot open it (two appenders would
	/// overwrite each other's lines).
	/// </summary>
	public string? OutputFile { get; init; }

	/// <summary>Whether to append to <see cref="OutputFile"/> rather than replace it.</summary>
	public bool AppendToOutputFile { get; init; }

	/// <summary>Called with the child's process ID once it has started, before its output is read. Null for none.</summary>
	public Action<int>? OnStarted { get; init; }

	/// <summary>A logger that receives every line at <see cref="LineLevel"/>. Null for none.</summary>
	public ILogger? Logger { get; init; }

	/// <summary>The level lines are logged at.</summary>
	public LogLevel LineLevel { get; init; } = LogLevel.Information;

	/// <summary>How the child's output is decoded. UTF-8 by default; <see cref="ProcessRunner.ConsoleEncoding"/> for console programs that write in the console's code page (such as UnrealBuildTool on Windows).</summary>
	public Encoding? OutputEncoding { get; init; }

	/// <summary>
	/// Whether the child's whole process tree dies with this process (a Windows job object; elsewhere only on cancellation).
	/// Commands that start something meant to outlive uak do not use this runner (see AgentKit.Runs).
	/// </summary>
	public bool KillTreeWithParent { get; init; } = true;
}

/// <summary>The result of a run.</summary>
/// <param name="ExitCode">The child's exit code.</param>
/// <param name="LastLine">The last non-blank line it printed, or null.</param>
public sealed record ProcessResult(int ExitCode, string? LastLine);

/// <summary>The captured output of a finished run.</summary>
/// <param name="ExitCode">The child's exit code.</param>
/// <param name="StandardOutput">Everything it printed on stdout, lines joined with '\n'.</param>
/// <param name="StandardError">Everything it printed on stderr, lines joined with '\n'.</param>
public sealed record ProcessCapture(int ExitCode, string StandardOutput, string StandardError);

/// <summary>A program could not be started: it does not exist, or is not executable.</summary>
public sealed class ProcessStartException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>Runs a program to completion, passing each line it prints to a callback. A seam for tests.</summary>
public interface IProcessRunner
{
	/// <summary>
	/// Runs the program and returns its exit code. Each line from stdout and stderr goes to <paramref name="onLine"/>.
	/// Cancelling kills the program and its children, then throws <see cref="OperationCanceledException"/>.
	/// </summary>
	Task<int> RunAsync(ProcessInvocation invocation, Action<string> onLine, CancellationToken cancellationToken);
}

/// <summary>How <see cref="IProcessCapture.CaptureAsync"/> captures a run.</summary>
public sealed class ProcessCaptureOptions
{
	/// <summary>
	/// Whether to return stdout and stderr exactly as written (decoded, but not split into lines), for output such as
	/// `git -z`. Otherwise lines are split at \n, \r\n or a lone \r and joined with '\n', with no trailing newline.
	/// </summary>
	public bool Raw { get; init; }

	/// <summary>Text written to the child's stdin (UTF-8 without a BOM), which is then closed. Null closes stdin at once.</summary>
	public string? StandardInput { get; init; }

	/// <summary>How the child's output is decoded; UTF-8 by default (see <see cref="ProcessRunner.ConsoleEncoding"/>).</summary>
	public Encoding? OutputEncoding { get; init; }
}

/// <summary>Runs a program to completion and returns what it printed. A seam for tests (see <see cref="ProcessRunner"/>).</summary>
public interface IProcessCapture
{
	/// <summary>
	/// Runs the program and returns its exit code, stdout and stderr, kept apart. Cancelling kills the program and its
	/// children, then throws <see cref="OperationCanceledException"/>. A program that cannot start throws <see cref="ProcessStartException"/>.
	/// </summary>
	Task<ProcessCapture> CaptureAsync(ProcessInvocation invocation, ProcessCaptureOptions? options, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IProcessRunner"/> and <see cref="IProcessCapture"/>.
/// <list type="bullet">
/// <item>Windows: EpicGames.Core's <see cref="ManagedProcess"/>, which takes one command line (built with Unreal's quoting,
/// see <see cref="ProcessInvocation.ToWindowsCommandLine"/>) and puts the child in a job object so its tree dies with us.
/// .bat and .cmd files run through cmd.exe.</item>
/// <item>Linux and Mac: <see cref="Process"/> with an argument list, so arguments reach the child exactly. ManagedProcess's
/// portable path turns every ' into ", which would corrupt arguments.</item>
/// </list>
/// </summary>
public sealed class ProcessRunner : IProcessRunner, IProcessCapture
{
	/// <summary>A shared instance.</summary>
	public static ProcessRunner Default { get; } = new();

	/// <summary>How long to wait for the output pipes to close after the child exits, before giving up on grandchildren that hold them.</summary>
	public TimeSpan OutputDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

	/// <inheritdoc/>
	public async Task<int> RunAsync(ProcessInvocation invocation, Action<string> onLine, CancellationToken cancellationToken)
	{
		ProcessResult Result = await RunAsync(invocation, new ProcessOutputOptions { OnLine = onLine }, cancellationToken).ConfigureAwait(false);
		return Result.ExitCode;
	}

	/// <summary>This process's environment with the invocation's changes applied, or null when there are none (inherit it).</summary>
	public static Dictionary<string, string>? BuildEnvironment(IReadOnlyDictionary<string, string?>? changes)
	{
		if (changes is null || changes.Count == 0)
		{
			return null;
		}
		Dictionary<string, string> Environment = new(ProcessInvocation.EnvironmentComparer);
		foreach (DictionaryEntry Entry in System.Environment.GetEnvironmentVariables())
		{
			if (Entry.Key is string Key && Entry.Value is string Value)
			{
				Environment[Key] = Value;
			}
		}
		foreach ((string Key, string? Value) in changes)
		{
			if (Value is null)
			{
				Environment.Remove(Key);
			}
			else
			{
				Environment[Key] = Value;
			}
		}
		return Environment;
	}

	/// <summary>The file and command line ManagedProcess is given on Windows: scripts run through cmd.exe /d /s /c "...".</summary>
	public static (string FileName, string CommandLine) GetWindowsCommand(ProcessInvocation invocation)
	{
		string Extension = Path.GetExtension(invocation.FileName);
		if (Extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || Extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
		{
			// With /s, cmd strips exactly the outer quotes and parses the rest as written, so every argument is quoted for cmd
			// (ProcessInvocation.QuoteForCmd, which also lists what cannot be escaped).
			string Shell = System.Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } ComSpec && Path.IsPathFullyQualified(ComSpec)
				? ComSpec
				: Path.Combine(System.Environment.SystemDirectory, "cmd.exe");
			string CmdArguments = invocation.ToCmdCommandLine();
			string Inner = ProcessInvocation.QuoteForCmd(invocation.FileName) + (CmdArguments.Length == 0 ? "" : " " + CmdArguments);
			return (Shell, "/d /s /c \"" + Inner + "\"");
		}
		return (invocation.FileName, invocation.ToWindowsCommandLine());
	}

	/// <summary>Runs the program, sending its output where <paramref name="output"/> says.</summary>
	public async Task<ProcessResult> RunAsync(ProcessInvocation invocation, ProcessOutputOptions output, CancellationToken cancellationToken)
	{
		using LineSink Sink = new(output);
		bool Separate = output.OnErrorLine is not null;
		Encoding Encoding = output.OutputEncoding ?? DefaultEncoding;
		int ExitCode = await RunCoreAsync(invocation, Separate, output.KillTreeWithParent, input: null, output.OnStarted,
			(Stream, IsError) => PumpLinesAsync(Stream, Sink, IsError, Encoding), cancellationToken).ConfigureAwait(false);
		return new ProcessResult(ExitCode, Sink.LastLine);
	}

	/// <summary>Runs the program and returns its exit code and output, with stdout and stderr kept apart, split into lines and joined with '\n'.</summary>
	public Task<ProcessCapture> CaptureAsync(ProcessInvocation invocation, CancellationToken cancellationToken) =>
		CaptureAsync(invocation, null, cancellationToken);

	/// <inheritdoc/>
	public async Task<ProcessCapture> CaptureAsync(ProcessInvocation invocation, ProcessCaptureOptions? options, CancellationToken cancellationToken)
	{
		options ??= new ProcessCaptureOptions();
		Encoding Encoding = options.OutputEncoding ?? DefaultEncoding;
		byte[]? Input = options.StandardInput is null ? null : DefaultEncoding.GetBytes(options.StandardInput);
		if (options.Raw)
		{
			MemoryStream Out = new();
			MemoryStream Err = new();
			int RawExitCode = await RunCoreAsync(invocation, separateStandardError: true, killTreeWithParent: true, Input, onStarted: null,
				(Stream, IsError) => CopyAllAsync(Stream, IsError ? Err : Out), cancellationToken).ConfigureAwait(false);
			return new ProcessCapture(RawExitCode, Decode(Out, Encoding), Decode(Err, Encoding));
		}

		List<string> OutLines = [];
		List<string> ErrLines = [];
		using LineSink Sink = new(new ProcessOutputOptions { OnLine = OutLines.Add, OnErrorLine = ErrLines.Add });
		int ExitCode = await RunCoreAsync(invocation, separateStandardError: true, killTreeWithParent: true, Input, onStarted: null,
			(Stream, IsError) => PumpLinesAsync(Stream, Sink, IsError, Encoding), cancellationToken).ConfigureAwait(false);
		// Stop late lines (from a grandchild holding the pipe) before reading the lists.
		Sink.Dispose();
		return new ProcessCapture(ExitCode, string.Join('\n', OutLines), string.Join('\n', ErrLines));
	}

	static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	/// <summary>
	/// The encoding a console program started by this runner writes in, for <see cref="ProcessOutputOptions.OutputEncoding"/>
	/// and <see cref="ProcessCaptureOptions.OutputEncoding"/>. On Windows that is the OEM code page: the runner starts children
	/// with their own hidden console (ManagedProcess's CREATE_NO_WINDOW), whose output code page is the OEM one, and .NET
	/// programs such as UnrealBuildTool, cmd.exe and most C runtimes encode redirected output in it. It is UTF-8 (65001) when
	/// Windows' "Use Unicode UTF-8 for worldwide language support" is on. On Linux and Mac it is UTF-8.
	/// </summary>
	public static Encoding ConsoleEncoding { get; } = GetConsoleEncoding();

	static Encoding GetConsoleEncoding()
	{
		if (!OperatingSystem.IsWindows())
		{
			return DefaultEncoding;
		}
		int CodePage;
		try
		{
			CodePage = (int)GetOEMCP();
		}
		catch (Exception Error) when (Error is DllNotFoundException or EntryPointNotFoundException)
		{
			return DefaultEncoding;
		}
		if (CodePage is 0 or 65001)
		{
			return DefaultEncoding;
		}
		try
		{
			// The OEM code pages (437, 850, 932, ...) come from the code-pages provider, which .NET ships but does not register.
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			return Encoding.GetEncoding(CodePage);
		}
		catch (Exception Error) when (Error is ArgumentException or NotSupportedException)
		{
			return DefaultEncoding;
		}
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	static extern uint GetOEMCP();

	static string Decode(MemoryStream stream, Encoding encoding)
	{
		lock (stream)
		{
			return encoding.GetString(stream.GetBuffer(), 0, (int)stream.Length);
		}
	}

	/// <summary>
	/// Starts the program, feeds <paramref name="input"/> to its stdin, hands its output streams to <paramref name="consume"/>
	/// (stdout with isError false; stderr with isError true when kept apart), waits for it, and returns its exit code.
	/// <paramref name="onStarted"/> gets its process ID once it has started.
	/// </summary>
	Task<int> RunCoreAsync(ProcessInvocation invocation, bool separateStandardError, bool killTreeWithParent, byte[]? input, Action<int>? onStarted, Func<Stream, bool, Task> consume, CancellationToken cancellationToken)
	{
		invocation = ResolveFileName(invocation);
		return OperatingSystem.IsWindows()
			? RunManagedAsync(invocation, separateStandardError, killTreeWithParent, input, onStarted, consume, cancellationToken)
			: RunPortableAsync(invocation, separateStandardError, input, onStarted, consume, cancellationToken);
	}

	/// <summary>
	/// The invocation with a bare program name ("git") replaced by its absolute path on PATH (<see cref="ExecutableLocator"/>),
	/// so the current directory and uak's own folder are never searched. Throws <see cref="ProcessStartException"/> when it is
	/// not on PATH.
	/// </summary>
	public static ProcessInvocation ResolveFileName(ProcessInvocation invocation)
	{
		string? Resolved = ExecutableLocator.Resolve(invocation.FileName, invocation.Environment);
		if (Resolved is null)
		{
			throw new ProcessStartException($"Could not run {invocation.FileName}: it was not found on PATH.", new FileNotFoundException("Not on PATH.", invocation.FileName));
		}
		return ReferenceEquals(Resolved, invocation.FileName) ? invocation : invocation with { FileName = Resolved };
	}

	async Task<int> RunManagedAsync(ProcessInvocation invocation, bool separate, bool killTreeWithParent, byte[]? input, Action<int>? onStarted, Func<Stream, bool, Task> consume, CancellationToken cancellationToken)
	{
		(string FileName, string CommandLine) = GetWindowsCommand(invocation);
		using ManagedProcessGroup? Group = killTreeWithParent ? new ManagedProcessGroup() : null;
		ManagedProcess Child;
		try
		{
			// This overload leaves stdin open, for WriteInputAsync.
			Child = new ManagedProcess(Group, FileName, CommandLine, invocation.WorkingDirectory, BuildEnvironment(invocation.Environment),
				ProcessPriorityClass.Normal, null, separate ? ManagedProcessFlags.None : ManagedProcessFlags.MergeOutputPipes);
		}
		catch (Win32Exception Error)
		{
			throw new ProcessStartException($"Could not run {invocation.FileName}: {Error.Message}", Error);
		}

		using (Child)
		{
			onStarted?.Invoke(Child.Id);
			Task InputTask = WriteInputAsync(Child.StdIn, input);
			Task OutTask = consume(Child.StdOut, false);
			Task ErrTask = separate ? consume(Child.StdErr, true) : Task.CompletedTask;
			try
			{
				await Child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// We started it, so we stop it, with everything it started.
				try
				{
					Child.Kill(killDescendants: true);
				}
				catch (Exception Error) when (Error is Win32Exception or InvalidOperationException)
				{
				}
				throw;
			}
			await DrainAsync(InputTask, OutTask, ErrTask).ConfigureAwait(false);
			return Child.ExitCode;
		}
	}

	async Task<int> RunPortableAsync(ProcessInvocation invocation, bool separate, byte[]? input, Action<int>? onStarted, Func<Stream, bool, Task> consume, CancellationToken cancellationToken)
	{
		ProcessStartInfo Info = new()
		{
			FileName = invocation.FileName,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		if (invocation.WorkingDirectory is not null)
		{
			Info.WorkingDirectory = invocation.WorkingDirectory;
		}
		foreach (string Argument in invocation.Arguments)
		{
			Info.ArgumentList.Add(Argument);
		}
		foreach ((string Key, string? Value) in invocation.Environment ?? new Dictionary<string, string?>())
		{
			if (Value is null)
			{
				Info.Environment.Remove(Key);
			}
			else
			{
				Info.Environment[Key] = Value;
			}
		}

		using Process Child = new() { StartInfo = Info };
		try
		{
			Child.Start();
		}
		catch (Win32Exception Error)
		{
			throw new ProcessStartException($"Could not run {invocation.FileName}: {Error.Message}", Error);
		}

		onStarted?.Invoke(Child.Id);
		Task InputTask = WriteInputAsync(Child.StandardInput.BaseStream, input);
		Task OutTask = consume(Child.StandardOutput.BaseStream, false);
		Task ErrTask = consume(Child.StandardError.BaseStream, separate);
		try
		{
			await Child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			try
			{
				Child.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
			}
			throw;
		}
		await DrainAsync(InputTask, OutTask, ErrTask).ConfigureAwait(false);
		return Child.ExitCode;
	}

	/// <summary>Writes the input (if any) and closes stdin. A child that exits without reading it all is not an error.</summary>
	static async Task WriteInputAsync(Stream stdin, byte[]? input)
	{
		try
		{
			if (input is not null && input.Length > 0)
			{
				await stdin.WriteAsync(input).ConfigureAwait(false);
				await stdin.FlushAsync().ConfigureAwait(false);
			}
		}
		catch (Exception Error) when (Error is IOException or ObjectDisposedException)
		{
		}
		finally
		{
			try
			{
				stdin.Close();
			}
			catch (Exception Error) when (Error is IOException or ObjectDisposedException)
			{
			}
		}
	}

	/// <summary>Waits for the input and output to finish after the child exits; a grandchild holding a pipe open must not hang us.</summary>
	async Task DrainAsync(params Task[] tasks)
	{
		Task All = Task.WhenAll(tasks);
		if (await Task.WhenAny(All, Task.Delay(OutputDrainTimeout)).ConfigureAwait(false) == All)
		{
			await All.ConfigureAwait(false);
		}
	}

	static async Task CopyAllAsync(Stream stream, MemoryStream destination)
	{
		byte[] Buffer = new byte[16 * 1024];
		try
		{
			for (; ; )
			{
				int Read = await stream.ReadAsync(Buffer).ConfigureAwait(false);
				if (Read == 0)
				{
					break;
				}
				lock (destination)
				{
					destination.Write(Buffer, 0, Read);
				}
			}
		}
		catch (Exception Error) when (Error is IOException or ObjectDisposedException or OperationCanceledException)
		{
			// The pipe was closed under us (the child was killed, or we stopped waiting for a grandchild).
		}
	}

	static async Task PumpLinesAsync(Stream stream, LineSink sink, bool isError, Encoding encoding)
	{
		LineSplitter Splitter = new(encoding, Line => sink.Write(Line, isError));
		byte[] Buffer = new byte[16 * 1024];
		try
		{
			for (; ; )
			{
				int Read = await stream.ReadAsync(Buffer).ConfigureAwait(false);
				if (Read == 0)
				{
					break;
				}
				Splitter.Write(Buffer, Read);
			}
		}
		catch (Exception Error) when (Error is IOException or ObjectDisposedException or OperationCanceledException)
		{
			// The pipe was closed under us (the child was killed, or we stopped waiting for a grandchild).
		}
		Splitter.Flush();
	}

	/// <summary>Serialises lines from both streams into the callbacks, the output file and the logger.</summary>
	sealed class LineSink : IDisposable
	{
		readonly ProcessOutputOptions Options;
		readonly StreamWriter? File;
		readonly object Gate = new();
		bool Disposed;

		public string? LastLine { get; private set; }

		public LineSink(ProcessOutputOptions options)
		{
			Options = options;
			if (options.OutputFile is not null)
			{
				string FullPath = Path.GetFullPath(options.OutputFile);
				Directory.CreateDirectory(Path.GetDirectoryName(FullPath)!);
				// The only writer: readers may share it, another writer may not (see ProcessOutputOptions.OutputFile).
				FileStream Stream = new(FullPath, options.AppendToOutputFile ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
				File = new StreamWriter(Stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
			}
		}

		public void Write(string line, bool isError)
		{
			lock (Gate)
			{
				// Lines from a grandchild that still holds the pipe after the run has returned are dropped.
				if (Disposed)
				{
					return;
				}
				if (!string.IsNullOrWhiteSpace(line))
				{
					LastLine = line;
				}
				File?.WriteLine(line);
				Options.Logger?.Log(Options.LineLevel, "{Line}", line);
				if (isError && Options.OnErrorLine is not null)
				{
					Options.OnErrorLine(line);
				}
				else
				{
					Options.OnLine?.Invoke(line);
				}
			}
		}

		public void Dispose()
		{
			lock (Gate)
			{
				Disposed = true;
				File?.Dispose();
			}
		}
	}
}

/// <summary>Splits a byte stream into lines at \n, \r\n or a lone \r, decoding incrementally so multi-byte characters may span reads.</summary>
internal sealed class LineSplitter(Encoding encoding, Action<string> onLine)
{
	readonly Decoder Decoder = encoding.GetDecoder();
	readonly StringBuilder Current = new();
	char[] Chars = new char[1024];
	bool PendingCarriageReturn;

	public void Write(byte[] buffer, int count)
	{
		int Needed = encoding.GetMaxCharCount(count);
		if (Chars.Length < Needed)
		{
			Chars = new char[Needed];
		}
		int CharCount = Decoder.GetChars(buffer, 0, count, Chars, 0, flush: false);
		for (int Index = 0; Index < CharCount; Index++)
		{
			char C = Chars[Index];
			if (PendingCarriageReturn)
			{
				PendingCarriageReturn = false;
				if (C == '\n')
				{
					continue;
				}
			}
			if (C == '\r' || C == '\n')
			{
				PendingCarriageReturn = C == '\r';
				onLine(Current.ToString());
				Current.Clear();
			}
			else
			{
				Current.Append(C);
			}
		}
	}

	public void Flush()
	{
		int CharCount = Decoder.GetChars([], 0, 0, Chars, 0, flush: true);
		Current.Append(Chars, 0, CharCount);
		if (Current.Length > 0)
		{
			onLine(Current.ToString());
			Current.Clear();
		}
	}
}
