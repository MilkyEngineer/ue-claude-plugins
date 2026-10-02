// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using AgentKit.Core;
using AgentKit.Locking;
using Microsoft.Win32.SafeHandles;

namespace AgentKit.Runs;

/// <summary>
/// The wrapper of a detached run (<c>uak runs _wrap</c>), started detached by <see cref="RunStarter"/>. It points its own
/// standard output and error at the run's output file (so the command inherits them, and its output survives the wrapper),
/// records its own process in the run's record, runs the command, and records the end time, exit code and last line.
/// </summary>
public static class RunWrapper
{
	/// <summary>Runs the run recorded in <paramref name="recordFile"/>. Returns the command's exit code, or 1 when it could not run.</summary>
	/// <param name="recordFile">The run's record, written by the starter.</param>
	/// <param name="detach">How this process was detached (see <see cref="RunRecord.Detach"/>). "session" makes it call setsid() on Unix.</param>
	/// <param name="cancellationToken">Stops waiting for the command (the command runs on; no end is recorded).</param>
	public static async Task<int> RunAsync(string recordFile, string? detach, CancellationToken cancellationToken)
	{
		RunRecord? record = null;
		for (int attempt = 0; attempt < 50 && record is null; attempt++)
		{
			record = RunRegistry.ReadFile(recordFile);
			if (record is null)
			{
				await Task.Delay(100, cancellationToken).ConfigureAwait(false);
			}
		}
		if (record is null)
		{
			return UakExitCodes.Failure;
		}

		string outputFile = record.OutputFile ?? Path.ChangeExtension(recordFile, ".log");
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
		using OutputRedirect output = OutputRedirect.Open(outputFile, detach);

		ProcessIdentity self = ProcessIdentity.Current;
		record.Pid = self.Pid;
		record.ProcessStart = self.StartTimeUtc;
		record.Detach = detach;
		record.OutputFile = outputFile;
		StateFiles.WriteJson(recordFile, record);
		output.WriteLine($"{RunRegistry.WrapperLinePrefix}{record.Name}' (owner {record.Owner}) started {UakJson.FormatTime(DateTime.UtcNow)}: {record.CommandLine}");

		int exitCode;
		string? failure = null;
		using KeepAwake awake = KeepAwake.Begin(!record.AllowSleep);
		if (!record.AllowSleep && OperatingSystem.IsWindows() && !awake.IsActive)
		{
			output.WriteLine($"{RunRegistry.WrapperLinePrefix}{record.Name}': Windows refused the keep-awake request; the machine may sleep during the run");
		}
		try
		{
			exitCode = await RunCommandAsync(record, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Whatever stopped the command from starting (or from being waited for) is the run's outcome.
			failure = $"The run could not start: {exception.Message}";
			output.WriteLine($"{RunRegistry.WrapperLinePrefix}{record.Name}': {failure}");
			exitCode = UakExitCodes.Failure;
		}
		output.WriteLine($"{RunRegistry.WrapperLinePrefix}{record.Name}' ended {UakJson.FormatTime(DateTime.UtcNow)}, exit code {exitCode}");
		RecordEnd(recordFile, record, outputFile, exitCode, failure, output.WriteLine);
		return exitCode;
	}

	/// <summary>How many times the wrapper tries to record the end before it gives up and writes it to the output instead.</summary>
	internal const int EndWriteAttempts = 5;

	/// <summary>
	/// Records the end in the run's record, retrying a few times (a reader or a virus scanner may hold the file; each write
	/// also retries for a few seconds itself). If it still fails, the outcome goes to the end of the output instead, through
	/// <paramref name="writeLine"/>, so it is never lost silently. Returns whether the record has it.
	/// </summary>
	internal static bool RecordEnd(string recordFile, RunRecord record, string outputFile, int exitCode, string? failure, Action<string> writeLine,
		int attempts = EndWriteAttempts, TimeSpan? retryDelay = null)
	{
		DateTime ended = DateTime.UtcNow;
		string lastLine = failure ?? RunRegistry.GetLastLine(outputFile);
		Exception? lastError = null;
		for (int attempt = 0; attempt < attempts; attempt++)
		{
			if (attempt > 0)
			{
				Thread.Sleep(retryDelay ?? TimeSpan.FromMilliseconds(500));
			}
			try
			{
				// Read again, in case something else updated the record meanwhile.
				RunRecord final = RunRegistry.ReadFile(recordFile) ?? record;
				final.Ended = ended;
				final.ExitCode = exitCode;
				final.LastLine = lastLine;
				StateFiles.WriteJson(recordFile, final);
				return true;
			}
			catch (Exception exception)
			{
				lastError = exception;
			}
		}
		try
		{
			writeLine($"{RunRegistry.WrapperLinePrefix}{record.Name}': could not record the end in {recordFile} ({lastError?.Message}). " +
				$"Outcome: ended {UakJson.FormatTime(ended)}, exit code {exitCode}, last line: {lastLine}");
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			// Nowhere left to write it.
		}
		return false;
	}

	private static async Task<int> RunCommandAsync(RunRecord record, CancellationToken cancellationToken)
	{
		ProcessStartInfo startInfo = CommandProcess.CreateStartInfo([record.Command, .. record.Arguments], record.WorkingDirectory);
		// The wrapper has no console: without this, each console command would open a window. Redirecting standard input (to
		// nothing) makes .NET pass this process's standard output and error, the run's output file, to the command.
		startInfo.CreateNoWindow = true;
		startInfo.RedirectStandardInput = true;
		// A run outlives whatever lock its starter held: its own lock requests must queue, not join a hold that has ended.
		startInfo.Environment.Remove(EditorLock.HeldVariable);
		startInfo.Environment[EditorLock.NameVariable] = record.Name;
		if (record.Priority is LockPriority priority)
		{
			startInfo.Environment[EditorLock.PriorityVariable] = priority.ToString();
		}
		using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Cannot start '{record.Command}'.");
		process.StandardInput.Close();
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		return process.ExitCode;
	}

	/// <summary>This process's standard output and error, pointed at the run's output file, and a way to write the wrapper's own lines to it.</summary>
	private abstract class OutputRedirect : IDisposable
	{
		public static OutputRedirect Open(string outputFile, string? detach)
		{
			if (OperatingSystem.IsWindows())
			{
				return new WindowsRedirect(outputFile);
			}
			return new UnixRedirect(outputFile, detach == "session");
		}

		public void WriteLine(string line)
		{
			Write(Encoding.UTF8.GetBytes(line + Environment.NewLine));
		}

		protected abstract void Write(byte[] bytes);

		public abstract void Dispose();
	}

	[SupportedOSPlatform("windows")]
	private sealed class WindowsRedirect : OutputRedirect
	{
		private readonly IntPtr _output;
		private readonly IntPtr _input;

		public WindowsRedirect(string outputFile)
		{
			// Append-only access: every write, the command's and the wrapper's, goes to the end of the file.
			_output = WindowsNative.OpenInheritable(outputFile, WindowsNative.FILE_APPEND_DATA | WindowsNative.SYNCHRONIZE, WindowsNative.OPEN_ALWAYS);
			if (_output == WindowsNative.INVALID_HANDLE_VALUE)
			{
				throw new Win32Exception();
			}
			_input = WindowsNative.OpenInheritable("NUL", WindowsNative.GENERIC_READ, WindowsNative.OPEN_EXISTING);
			WindowsNative.SetStdHandle(WindowsNative.STD_OUTPUT_HANDLE, _output);
			WindowsNative.SetStdHandle(WindowsNative.STD_ERROR_HANDLE, _output);
			if (_input != WindowsNative.INVALID_HANDLE_VALUE)
			{
				WindowsNative.SetStdHandle(WindowsNative.STD_INPUT_HANDLE, _input);
			}
		}

		protected override void Write(byte[] bytes)
		{
			WindowsNative.WriteFile(_output, bytes, bytes.Length, out _, IntPtr.Zero);
		}

		public override void Dispose()
		{
			// The standard handles stay valid until the process exits.
		}
	}

	/// <summary>TODO(unix): untested; test on Linux and Mac.</summary>
	[UnsupportedOSPlatform("windows")]
	private sealed class UnixRedirect : OutputRedirect
	{
		private const int StandardInput = 0;
		private const int StandardOutput = 1;
		private const int StandardError = 2;

		public UnixRedirect(string outputFile, bool newSession)
		{
			if (newSession)
			{
				// Leave the caller's session and process group, so its terminal's hangup and group signals miss the run.
				UnixNative.setsid();
			}
			using (SafeFileHandle output = File.OpenHandle(outputFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
			{
				int fd = (int)output.DangerousGetHandle();
				UnixNative.lseek(fd, 0, UnixNative.SEEK_END);
				// The duplicates share the file offset, so the wrapper and the command write one after the other.
				UnixNative.dup2(fd, StandardOutput);
				UnixNative.dup2(fd, StandardError);
			}
			using (SafeFileHandle input = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			{
				UnixNative.dup2((int)input.DangerousGetHandle(), StandardInput);
			}
		}

		protected override void Write(byte[] bytes)
		{
			UnixNative.write(StandardOutput, bytes, bytes.Length);
		}

		public override void Dispose()
		{
		}
	}
}
