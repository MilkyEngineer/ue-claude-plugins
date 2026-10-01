// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

// The Locking and Runs tests start this program as real, separate processes. Modes:
//   lock <state dir> <project file> <name> <High|Normal|default> <log> <hold ms, -1 for ever> [poll ms]
//       Waits for the editor lock, appends "enter <name>" to the log, holds for the time, appends "leave <name>", releases.
//       Its log messages go to the log as "log <name> <message>". A refused request (EditorLockException) appends
//       "refused <name> <message>" and exits 3.
//   lock-run <state dir> <project file or -> <engine root or -> <lock run arguments...>
//       Runs `uak lock run` with those arguments, for that project (or none) and engine (or none).
//   hold-log <log> <name> <ms, -1 for ever>
//       Appends "pid <name> <PID>" and "enter <name>", sleeps, appends "leave <name>".
//   spawn <log> <name>
//       Starts "hold-log <log> <name> -1" (an ordinary child, not detached), waits until it has entered, and exits 0.
//   start-run <state dir> <name> <result json> -- <command...>
//       Waits for a line on standard input (so a test can first put this process in a job), starts a detached run whose
//       wrapper is this program's wrap mode, writes what it started to the result file, then waits for ever.
//   wrap -record=<file> -detach=<method>
//       The run wrapper (RunWrapper), as `uak runs _wrap` runs it.
//   echo-args <out json> <exit code> <arguments...>
//       Writes its arguments as a JSON array, prints two lines, and exits with the code.
//   env <out json> <variable names...>
//       Writes the variables' values, and the current directory, as a JSON object.
//   sleep <ms> <exit code>
//       Prints a line, sleeps, prints "slept <ms>", exits with the code.

using System.Text.Json;
using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Runs;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.TestChild;

internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		if (args.Length == 0)
		{
			await Console.Error.WriteLineAsync("usage: AgentKit.TestChild <mode> ...").ConfigureAwait(false);
			return 2;
		}
		string[] rest = args[1..];
		return args[0] switch
		{
			"lock" => await LockAsync(rest).ConfigureAwait(false),
			"lock-run" => await LockRunAsync(rest).ConfigureAwait(false),
			"hold-log" => await HoldLogAsync(rest).ConfigureAwait(false),
			"spawn" => Spawn(rest),
			"start-run" => await StartRunAsync(rest).ConfigureAwait(false),
			"wrap" => await WrapAsync(rest).ConfigureAwait(false),
			"echo-args" => EchoArgs(rest),
			"sleep" => await SleepAsync(rest).ConfigureAwait(false),
			"env" => Env(rest),
			_ => 2,
		};
	}

	private static UakContext CreateContext(string stateDirectory, string? projectFile)
	{
		return new UakContext
		{
			ProjectFile = projectFile is null ? null : new FileInfo(projectFile),
			StateDirectory = new DirectoryInfo(stateDirectory),
			Logger = NullLogger.Instance,
		};
	}

	private static async Task<int> LockAsync(string[] args)
	{
		UakContext context = CreateContext(args[0], args[1]);
		string name = args[2];
		LockPriority? priority = LockPriorities.Parse(args[3]);
		string log = args[4];
		int holdMilliseconds = int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
		int pollMilliseconds = args.Length > 6 ? int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture) : 100;
		EditorLockOptions options = new() { PollInterval = TimeSpan.FromMilliseconds(pollMilliseconds) };
		context = new UakContext { ProjectFile = context.ProjectFile, StateDirectory = context.StateDirectory, Logger = new LogFileLogger(log, name) };
		EditorLockHold hold;
		try
		{
			hold = await EditorLock.AcquireAsync(context, name, priority, options, CancellationToken.None).ConfigureAwait(false);
		}
		catch (EditorLockException exception)
		{
			Append(log, $"refused {name} {exception.Message}");
			return 3;
		}
		await using (hold)
		{
			Append(log, $"enter {name}");
			await Task.Delay(holdMilliseconds < 0 ? Timeout.Infinite : holdMilliseconds).ConfigureAwait(false);
			Append(log, $"leave {name}");
		}
		return 0;
	}

	private static async Task<int> LockRunAsync(string[] args)
	{
		UakContext context = new()
		{
			StateDirectory = new DirectoryInfo(args[0]),
			ProjectFile = args[1] == "-" ? null : new FileInfo(args[1]),
			EngineRoot = args[2] == "-" ? null : new DirectoryInfo(args[2]),
			Logger = new UakConsoleLogger(Console.Out, Console.Error),
		};
		return await new LockRunCommand().RunAsync(context, args[3..], CancellationToken.None).ConfigureAwait(false);
	}

	private static async Task<int> HoldLogAsync(string[] args)
	{
		string log = args[0];
		string name = args[1];
		int milliseconds = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
		Append(log, $"pid {name} {Environment.ProcessId}");
		Append(log, $"enter {name}");
		await Task.Delay(milliseconds < 0 ? Timeout.Infinite : milliseconds).ConfigureAwait(false);
		Append(log, $"leave {name}");
		return 0;
	}

	private static int Spawn(string[] args)
	{
		string log = args[0];
		string name = args[1];
		System.Diagnostics.ProcessStartInfo startInfo = new(Environment.ProcessPath!) { UseShellExecute = false };
		foreach (string argument in new[] { "hold-log", log, name, "-1" })
		{
			startInfo.ArgumentList.Add(argument);
		}
		using System.Diagnostics.Process child = System.Diagnostics.Process.Start(startInfo)!;
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		while (!(StateFiles.ReadShared(log) ?? "").Contains($"enter {name}", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
		{
			Thread.Sleep(20);
		}
		return 0;
	}

	/// <summary>Appends log messages to the holds log as "log &lt;name&gt; &lt;message&gt;", so tests can wait for them.</summary>
	private sealed class LogFileLogger(string log, string name) : Microsoft.Extensions.Logging.ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

		public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (IsEnabled(logLevel))
			{
				Append(log, $"log {name} {logLevel}: {formatter(state, exception).ReplaceLineEndings(" ")}");
			}
		}
	}

	/// <summary>
	/// Appends a line to a log several processes write. Each writer opens the file alone (sharing reading and deleting only,
	/// never writing): FileMode.Append seeks to the end once, at open, so two writers open at the same time would write at the
	/// same offset and overwrite each other's lines. A writer that finds the file open retries, with a jittered back-off, for
	/// up to 30 s. Readers (StateFiles.ReadShared) share writing, so they never block a writer for long.
	/// </summary>
	private static void Append(string path, string line)
	{
		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
				stream.Write(bytes);
				return;
			}
			catch (IOException) when (DateTime.UtcNow < deadline)
			{
				Thread.Sleep(Random.Shared.Next(1, 5 + Math.Min(attempt, 20) * 2));
			}
		}
	}

	private static async Task<int> StartRunAsync(string[] args)
	{
		string stateDirectory = args[0];
		string name = args[1];
		string resultFile = args[2];
		int separator = Array.IndexOf(args, "--");
		string[] command = args[(separator + 1)..];
		// Wait until the test has placed this process where it wants it (for example in a job).
		await Console.In.ReadLineAsync().ConfigureAwait(false);

		RunRegistry registry = new(new DirectoryInfo(stateDirectory));
		string self = Environment.ProcessPath!;
		RunStartResult result = await RunStarter.StartAsync(registry, new RunStartRequest { Name = name, Owner = "test", Command = command }, [self, "wrap"],
			CancellationToken.None).ConfigureAwait(false);
		StateFiles.WriteJson(resultFile, new Dictionary<string, object> { ["WrapperPid"] = result.WrapperPid, ["Detach"] = result.Detach, ["Registered"] = result.Registered });
		await Console.Out.WriteLineAsync("STARTED").ConfigureAwait(false);
		await Console.Out.FlushAsync().ConfigureAwait(false);
		await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
		return 0;
	}

	private static async Task<int> WrapAsync(string[] args)
	{
		string? record = args.FirstOrDefault(a => a.StartsWith("-record=", StringComparison.Ordinal))?["-record=".Length..];
		string? detach = args.FirstOrDefault(a => a.StartsWith("-detach=", StringComparison.Ordinal))?["-detach=".Length..];
		return record is null ? 2 : await RunWrapper.RunAsync(record, detach, CancellationToken.None).ConfigureAwait(false);
	}

	private static int EchoArgs(string[] args)
	{
		File.WriteAllText(args[0], JsonSerializer.Serialize(args[2..]));
		Console.Out.WriteLine("echo-args: first line");
		Console.Error.WriteLine("echo-args: to standard error");
		Console.Out.WriteLine($"echo-args: {args.Length - 2} arguments");
		return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
	}

	private static int Env(string[] args)
	{
		Dictionary<string, string?> values = args[1..].ToDictionary(name => name, Environment.GetEnvironmentVariable);
		values["CurrentDirectory"] = Environment.CurrentDirectory;
		File.WriteAllText(args[0], JsonSerializer.Serialize(values));
		return 0;
	}

	private static async Task<int> SleepAsync(string[] args)
	{
		int milliseconds = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
		Console.Out.WriteLine("sleeping");
		Console.Out.Flush();
		await Task.Delay(milliseconds).ConfigureAwait(false);
		Console.Out.WriteLine($"slept {milliseconds}");
		return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
	}
}
