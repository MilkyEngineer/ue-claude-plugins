// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using AgentKit.Core;
using EpicGames.Perforce;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace AgentKit.Vcs;

/// <summary>
/// An <see cref="IPerforceConnection"/> that runs <c>p4 -G</c> like EpicGames.Perforce's <see cref="PerforceConnection"/>, but:
/// <list type="bullet">
/// <item>p4 is found on PATH only (<see cref="ExecutableLocator"/>). PerforceConnection starts a bare "p4.exe", which Windows
/// looks for in the current directory first, and it has no setting for the program's path.</item>
/// <item>every command has a time limit (<see cref="Timeout"/>, from <c>UAK_P4_TIMEOUT</c>), after which p4 is killed and the
/// read throws <see cref="TimeoutException"/>.</item>
/// </list>
/// The library's record parsing and typed queries (TryFStatAsync and the rest) work on top of it unchanged.
/// </summary>
public sealed class P4ProcessConnection : IPerforceConnection
{
	/// <summary>The environment variable giving the time limit of each p4 command, in seconds.</summary>
	public const string TimeoutVariable = "UAK_P4_TIMEOUT";

	/// <summary>The time limit when <see cref="TimeoutVariable"/> is unset.</summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

	/// <summary>Creates the connection.</summary>
	/// <param name="settings">Server, user, client, host and program name, passed to p4 as global options.</param>
	/// <param name="logger">Receives each command line at Debug level.</param>
	/// <param name="timeout">The time limit of each command; null for <see cref="GetTimeout"/>.</param>
	/// <param name="executable">The p4 program; null to find "p4" on PATH when the first command runs.</param>
	public P4ProcessConnection(IPerforceSettings settings, ILogger logger, TimeSpan? timeout = null, string? executable = null)
	{
		Settings = settings;
		Logger = logger;
		Timeout = timeout ?? GetTimeout();
		_executable = executable;
	}

	string? _executable;

	/// <inheritdoc/>
	public IPerforceSettings Settings { get; }

	/// <inheritdoc/>
	public ILogger Logger { get; }

	/// <inheritdoc/>
	public Tracer Tracer { get; } = TracerProvider.Default.GetTracer("AgentKit.Vcs");

	/// <summary>How long one p4 command may run.</summary>
	public TimeSpan Timeout { get; }

	/// <summary>The time limit from <see cref="TimeoutVariable"/> (positive seconds), else <see cref="DefaultTimeout"/>.</summary>
	public static TimeSpan GetTimeout(Func<string, string?>? getEnvironmentVariable = null)
	{
		string? value = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(TimeoutVariable);
		return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0 && seconds < int.MaxValue / 1000.0
			? TimeSpan.FromSeconds(seconds)
			: DefaultTimeout;
	}

	/// <summary>The p4 program: the one given, else "p4" on PATH. Throws <see cref="Win32Exception"/> when there is none, as a failed start would.</summary>
	public string Executable => _executable ??= ExecutableLocator.FindOnPath("p4")
		?? throw new Win32Exception(2, "p4 was not found on PATH");

	/// <summary>The global options p4 gets before the command, as PerforceConnection builds them.</summary>
	public IReadOnlyList<string> GetGlobalArguments()
	{
		List<string> arguments = [];
		AddIfSet(arguments, "-p", Settings.ServerAndPort);
		AddIfSet(arguments, "-u", Settings.UserName);
		AddIfSet(arguments, "-H", Settings.HostName);
		AddIfSet(arguments, "-c", Settings.ClientName);
		AddIfSet(arguments, "-zprog=", Settings.AppName);
		AddIfSet(arguments, "-zversion=", Settings.AppVersion);
		return arguments;
	}

	static void AddIfSet(List<string> arguments, string prefix, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			arguments.Add(prefix + value);
		}
	}

	/// <inheritdoc/>
	public IPerforceOutput Command(string command, IReadOnlyList<string> arguments, IReadOnlyList<string>? fileArguments, byte[]? inputData, string? promptResponse, bool interceptIo)
	{
		if (interceptIo)
		{
			throw new NotSupportedException("interceptIo is not supported by the p4 command line.");
		}
		if (promptResponse is not null)
		{
			inputData = System.Text.Encoding.UTF8.GetBytes(promptResponse);
		}
		return new P4ChildProcess(Executable, command, arguments, fileArguments, inputData, GetGlobalArguments(), Timeout, Logger);
	}

	/// <inheritdoc/>
	public PerforceRecord CreateRecord(List<KeyValuePair<string, object>> fields) => PerforceRecord.FromFields(fields, true);

	/// <inheritdoc/>
	public void Dispose()
	{
	}
}

/// <summary>
/// One running <c>p4 -G</c> command, read as it writes (what EpicGames.Perforce's PerforceChildProcess does, with a resolved
/// program and a deadline). p4 writes -G records on stdout; anything on stderr follows them, so a plain-text failure still
/// reaches the parser, which reports it as an unexpected response.
/// </summary>
internal sealed class P4ChildProcess : IPerforceOutput, IDisposable
{
	readonly Process _process;
	readonly string _command;
	readonly TimeSpan _timeout;
	readonly DateTime _deadline;
	readonly Task<byte[]> _standardError;
	readonly string? _fileArgumentsFile;
	byte[] _buffer = new byte[64 * 1024];
	int _bufferEnd;
	bool _outputDone;
	bool _errorAppended;

	public P4ChildProcess(string executable, string command, IReadOnlyList<string> arguments, IReadOnlyList<string>? fileArguments, byte[]? inputData, IReadOnlyList<string> globalOptions, TimeSpan timeout, ILogger logger)
	{
		_command = command;
		_timeout = timeout;

		ProcessStartInfo info = new()
		{
			FileName = executable,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		info.ArgumentList.Add("-G");
		foreach (string option in globalOptions)
		{
			info.ArgumentList.Add(option);
		}
		if (fileArguments is not null)
		{
			// Like PerforceConnection: file arguments go in a response file, so any number fits on the command line.
			_fileArgumentsFile = Path.GetTempFileName();
			File.WriteAllLines(_fileArgumentsFile, fileArguments);
			info.ArgumentList.Add("-x" + _fileArgumentsFile);
		}
		info.ArgumentList.Add(command);
		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}
		logger.LogDebug("Running {Executable} {Arguments}", executable, string.Join(' ', info.ArgumentList));

		_process = new Process { StartInfo = info };
		try
		{
			_process.Start();
		}
		catch
		{
			DeleteFileArguments();
			_process.Dispose();
			throw;
		}
		_deadline = DateTime.UtcNow + timeout;
		_standardError = ReadAllAsync(_process.StandardError.BaseStream);
		_ = WriteInputAsync(_process.StandardInput.BaseStream, inputData);
	}

	public ReadOnlyMemory<byte> Data => _buffer.AsMemory(0, _bufferEnd);

	public async Task<bool> ReadAsync(CancellationToken token)
	{
		if (_bufferEnd == _buffer.Length)
		{
			Array.Resize(ref _buffer, Math.Min(_buffer.Length + (32 * 1024 * 1024), _buffer.Length * 2));
		}
		int previousEnd = _bufferEnd;
		TimeSpan remaining = _deadline - DateTime.UtcNow;
		using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
		limit.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
		try
		{
			while (!_outputDone && _bufferEnd < _buffer.Length)
			{
				// A pipe read does not always notice cancellation (Windows pipes are opened for synchronous I/O), so the wait is
				// cancelled instead; p4 is then killed, which ends the abandoned read.
				int count = await _process.StandardOutput.BaseStream.ReadAsync(_buffer.AsMemory(_bufferEnd)).AsTask().WaitAsync(limit.Token).ConfigureAwait(false);
				if (count == 0)
				{
					_outputDone = true;
					break;
				}
				_bufferEnd += count;
			}
			if (_outputDone && !_errorAppended && _bufferEnd < _buffer.Length)
			{
				byte[] error = await _standardError.WaitAsync(limit.Token).ConfigureAwait(false);
				if (_bufferEnd + error.Length > _buffer.Length)
				{
					Array.Resize(ref _buffer, _bufferEnd + error.Length);
				}
				error.CopyTo(_buffer, _bufferEnd);
				_bufferEnd += error.Length;
				_errorAppended = true;
			}
		}
		catch (OperationCanceledException) when (!token.IsCancellationRequested)
		{
			Kill();
			throw new TimeoutException($"p4 {_command} did not finish within {_timeout.TotalSeconds:0.#} s (set {P4ProcessConnection.TimeoutVariable} to change the limit).");
		}
		catch (OperationCanceledException)
		{
			Kill();
			throw;
		}
		return _bufferEnd > previousEnd;
	}

	public void Discard(int numBytes)
	{
		if (numBytes > 0)
		{
			Array.Copy(_buffer, numBytes, _buffer, 0, _bufferEnd - numBytes);
			_bufferEnd -= numBytes;
		}
	}

	public ValueTask DisposeAsync()
	{
		Dispose();
		return ValueTask.CompletedTask;
	}

	public void Dispose()
	{
		Kill();
		_process.Dispose();
		DeleteFileArguments();
	}

	void Kill()
	{
		try
		{
			if (!_process.HasExited)
			{
				_process.Kill(entireProcessTree: true);
			}
		}
		catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
		{
		}
	}

	void DeleteFileArguments()
	{
		if (_fileArgumentsFile is not null)
		{
			try
			{
				File.Delete(_fileArgumentsFile);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	static async Task<byte[]> ReadAllAsync(Stream stream)
	{
		using MemoryStream memory = new();
		try
		{
			await stream.CopyToAsync(memory).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
		}
		return memory.ToArray();
	}

	static async Task WriteInputAsync(Stream stdin, byte[]? input)
	{
		try
		{
			if (input is { Length: > 0 })
			{
				await stdin.WriteAsync(input).ConfigureAwait(false);
				await stdin.FlushAsync().ConfigureAwait(false);
			}
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
		}
		finally
		{
			try
			{
				stdin.Close();
			}
			catch (Exception exception) when (exception is IOException or ObjectDisposedException)
			{
			}
		}
	}
}
