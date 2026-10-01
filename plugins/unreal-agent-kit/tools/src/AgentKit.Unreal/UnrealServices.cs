// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text;
using AgentKit.Core;
using AgentKit.Locking;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>A process runner that can decode a child's output in a given encoding (the seam the Unreal commands use).</summary>
public interface IEncodedProcessRunner : IProcessRunner
{
	/// <summary>
	/// As <see cref="IProcessRunner.RunAsync"/>, decoding the child's output with <paramref name="outputEncoding"/>
	/// (null for UTF-8).
	/// </summary>
	Task<int> RunAsync(ProcessInvocation invocation, Encoding? outputEncoding, Action<string> onLine, CancellationToken cancellationToken);

	/// <summary>
	/// As <see cref="RunAsync(ProcessInvocation, Encoding?, Action{string}, CancellationToken)"/>, and reports the child's
	/// process ID to <paramref name="onStarted"/> once it has started. A runner that cannot tell ignores it (the default).
	/// </summary>
	Task<int> RunAsync(ProcessInvocation invocation, Encoding? outputEncoding, Action<string> onLine, Action<int>? onStarted, CancellationToken cancellationToken) =>
		RunAsync(invocation, outputEncoding, onLine, cancellationToken);
}

/// <summary>The real <see cref="IEncodedProcessRunner"/>, on <see cref="ProcessRunner"/>.</summary>
public sealed class EncodedProcessRunner : IEncodedProcessRunner
{
	readonly ProcessRunner Inner;

	/// <summary>Wraps <paramref name="inner"/>, or <see cref="ProcessRunner.Default"/>.</summary>
	public EncodedProcessRunner(ProcessRunner? inner = null)
	{
		Inner = inner ?? ProcessRunner.Default;
	}

	/// <inheritdoc/>
	public Task<int> RunAsync(ProcessInvocation invocation, Action<string> onLine, CancellationToken cancellationToken) =>
		RunAsync(invocation, null, onLine, null, cancellationToken);

	/// <inheritdoc/>
	public Task<int> RunAsync(ProcessInvocation invocation, Encoding? outputEncoding, Action<string> onLine, CancellationToken cancellationToken) =>
		RunAsync(invocation, outputEncoding, onLine, null, cancellationToken);

	/// <inheritdoc/>
	public async Task<int> RunAsync(ProcessInvocation invocation, Encoding? outputEncoding, Action<string> onLine, Action<int>? onStarted, CancellationToken cancellationToken)
	{
		ProcessResult Result = await Inner.RunAsync(invocation, new ProcessOutputOptions { OnLine = onLine, OutputEncoding = outputEncoding, OnStarted = onStarted }, cancellationToken)
			.ConfigureAwait(false);
		return Result.ExitCode;
	}
}

/// <summary>What the Unreal commands run things through. Tests replace the parts that touch processes and the lock.</summary>
public sealed class UnrealServices
{

	/// <summary>Runs child processes. An <see cref="IEncodedProcessRunner"/> lets UBT's output be decoded in the console's code page.</summary>
	public IProcessRunner Runner { get; init; } = new EncodedProcessRunner();

	/// <summary>
	/// How UBT's and the Build script's output is decoded: <see cref="ProcessRunner.ConsoleEncoding"/>, because on Windows
	/// they write redirected output in the console's OEM code page, not UTF-8 (a non-ASCII path in an error would otherwise
	/// be garbled, and no longer match the file it names).
	/// </summary>
	public Encoding ToolOutputEncoding { get; init; } = ProcessRunner.ConsoleEncoding;

	/// <summary>Runs UBT or a Build script through <see cref="Runner"/>, decoding its output with <see cref="ToolOutputEncoding"/>.</summary>
	internal Task<int> RunToolAsync(ProcessInvocation invocation, Action<string> onLine, CancellationToken cancellationToken) =>
		RunToolAsync(invocation, onLine, null, cancellationToken);

	/// <summary>
	/// As <see cref="RunToolAsync(ProcessInvocation, Action{string}, CancellationToken)"/>, and records the child's process
	/// in the editor lock's holder.json (<see cref="RecordCommandProcess"/>), so if uak dies while holding the lock, the next
	/// holder waits for UBT to end.
	/// </summary>
	internal Task<int> RunToolAsync(ProcessInvocation invocation, Action<string> onLine, IAsyncDisposable? lockHold, CancellationToken cancellationToken) =>
		Runner is IEncodedProcessRunner Encoded
			? Encoded.RunAsync(invocation, ToolOutputEncoding, onLine, RecordCommandProcess(lockHold), cancellationToken)
			: Runner.RunAsync(invocation, onLine, cancellationToken);

	/// <summary>
	/// Runs the editor through <see cref="Runner"/> (its output in UTF-8), recording its process in the editor lock's
	/// holder.json, as <see cref="RunToolAsync(ProcessInvocation, Action{string}, IAsyncDisposable?, CancellationToken)"/> does.
	/// </summary>
	internal Task<int> RunEditorAsync(ProcessInvocation invocation, Action<string> onLine, IAsyncDisposable? lockHold, CancellationToken cancellationToken) =>
		Runner is IEncodedProcessRunner Encoded
			? Encoded.RunAsync(invocation, null, onLine, RecordCommandProcess(lockHold), cancellationToken)
			: Runner.RunAsync(invocation, onLine, cancellationToken);

	/// <summary>
	/// What records a child's process in the lock's holder.json (<see cref="EditorLockHold.RecordCommandProcess(ProcessIdentity)"/>),
	/// or null when the hold is not the real editor lock (a test's). The child runs in ProcessRunner's job, which dies with
	/// uak on Windows; the record covers the moments its tree takes to go, and Linux and Mac, where nothing kills it.
	/// </summary>
	static Action<int>? RecordCommandProcess(IAsyncDisposable? lockHold) =>
		lockHold is EditorLockHold Hold
			? Pid => Hold.RecordCommandProcess(ProcessIdentity.TryGet(Pid) ?? new ProcessIdentity(Pid, null))
			: null;

	/// <summary>Takes the editor lock.</summary>
	public IEditorLockProvider Lock { get; init; } = new EditorLockProvider();

	/// <summary>Reads an environment variable.</summary>
	public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

	/// <summary>The host platform.</summary>
	public UnrealPlatform HostPlatform { get; init; } = UnrealPlatform.Host;

	/// <summary>The clock, for log names and progress echoes.</summary>
	public TimeProvider Clock { get; init; } = TimeProvider.System;

	/// <summary>The -MaxParallelActions to pass: the option when given, else UAK_MAX_PARALLEL_ACTIONS, else none.</summary>
	internal int? MaxParallelActions(UakArguments arguments)
	{
		int? Given = arguments.GetInt("MaxParallelActions", 1);
		if (Given is not null)
		{
			return Given;
		}
		string? FromEnvironment = GetEnvironmentVariable("UAK_MAX_PARALLEL_ACTIONS");
		if (string.IsNullOrWhiteSpace(FromEnvironment))
		{
			return null;
		}
		if (!int.TryParse(FromEnvironment, out int Value) || Value < 1)
		{
			throw new UakUsageException($"UAK_MAX_PARALLEL_ACTIONS must be a positive whole number, not '{FromEnvironment}'");
		}
		return Value;
	}

	/// <summary>A new log path under &lt;State&gt;/Logs, unique per command, time and process.</summary>
	internal string NewLogFile(UakContext context, string command)
	{
		string Directory = Path.Combine(context.StateDirectory.FullName, "Logs");
		System.IO.Directory.CreateDirectory(Directory);
		string Stamp = Clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
		return Path.Combine(Directory, $"{command}-{Stamp}-{Environment.ProcessId}.log");
	}

	/// <summary>The engine's layout for <see cref="HostPlatform"/> (a service, so tests can build other platforms' paths).</summary>
	internal EngineLayout Paths(UakContext context)
	{
		return new EngineLayout(context.RequireEngine().RootDirectory, HostPlatform);
	}

	internal static string RequireProject(UakContext context) => context.RequireProject().FullName;

	internal static UnrealPlatform ParsePlatform(UakArguments arguments)
	{
		string Name = arguments.GetString("platform", "host")!;
		return UnrealPlatform.FromName(Name) ?? throw new UakUsageException($"unknown -platform={Name} (Win64, Linux, Mac or host)");
	}

	/// <summary>Writes a JSON result file when -resultfile= was given (UTF-8 without a BOM).</summary>
	internal static void WriteResultFile(string? path, object result)
	{
		if (path is null)
		{
			return;
		}
		string Full = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(Full)!);
		File.WriteAllText(Full, UakJson.Serialize(result) + "\n", UakJson.Utf8NoBom);
	}

	/// <summary>Runs a command body, turning usage and setup exceptions into exit code 2.</summary>
	internal static async Task<int> GuardAsync(UakContext context, Func<Task<int>> body)
	{
		try
		{
			return await body().ConfigureAwait(false);
		}
		catch (UakUsageException Exception)
		{
			// Also catches UakSetupException, which derives from it.
			context.Logger.LogError("{Kind}: {Message}", Exception is UakSetupException ? "setup" : "usage", Exception.Message);
			return UakExitCodes.UsageError;
		}
		catch (ProcessStartException Exception)
		{
			// UBT, dotnet or the editor is missing or not executable.
			context.Logger.LogError("setup: {Message}", Exception.Message);
			return UakExitCodes.UsageError;
		}
	}
}
