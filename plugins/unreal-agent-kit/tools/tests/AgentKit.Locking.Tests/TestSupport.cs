// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

// Shared by the Locking and Runs tests (Runs links this file): a temporary project and state directory per test, never the
// real ones, and the AgentKit.TestChild program started as real, separate processes.

using System.Diagnostics;
using AgentKit.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentKit.Tests;

/// <summary>Runs once per test assembly, before anything touches the editor lock.</summary>
[TestClass]
public sealed class TestEnvironment
{
	/// <summary>
	/// Tests must not run under a hold of the machine they run on: if the test host itself was started inside
	/// <c>uak lock run</c>, its inherited UAK_LOCK_HELD would make every lock request for a test project fail at once.
	/// </summary>
	[AssemblyInitialize]
	public static void Initialize(TestContext context)
	{
		Environment.SetEnvironmentVariable("UAK_LOCK_HELD", null);
	}
}

/// <summary>A temporary directory holding a fake project (so the lock's mutex name is unique to the test) and a state directory.</summary>
internal sealed class TempState : IDisposable
{
	private readonly List<Process> _processes = [];

	public TempState()
	{
		Root = Path.Combine(Path.GetTempPath(), "UakTest", Guid.NewGuid().ToString("N")[..12]);
		Directory.CreateDirectory(Root);
		ProjectFile = Path.Combine(Root, "Test.uproject");
		File.WriteAllText(ProjectFile, "{}");
		StateDirectory = Path.Combine(Root, "Saved", "AgentKit");
		Directory.CreateDirectory(StateDirectory);
		Context = new UakContext
		{
			ProjectFile = new FileInfo(ProjectFile),
			StateDirectory = new DirectoryInfo(StateDirectory),
			Logger = NullLogger.Instance,
		};
	}

	public string Root { get; }

	public string ProjectFile { get; }

	public string StateDirectory { get; }

	public UakContext Context { get; }

	/// <summary>A path inside the temporary directory.</summary>
	public string PathOf(string name)
	{
		return Path.Combine(Root, name);
	}

	/// <summary>Starts the test child with these arguments; killed at the end of the test if still running.</summary>
	public Process StartChild(params string[] arguments)
	{
		Process process = Child.Start(arguments, redirectInput: false);
		_processes.Add(process);
		return process;
	}

	/// <summary>Starts the test child with environment variables set (a null value removes one).</summary>
	public Process StartChildWithEnvironment(IReadOnlyDictionary<string, string?> environment, params string[] arguments)
	{
		Process process = Child.Start(arguments, redirectInput: false, environment);
		_processes.Add(process);
		return process;
	}

	/// <summary>Starts the test child with standard input and output as pipes the test drives.</summary>
	public Process StartChildWithPipes(params string[] arguments)
	{
		Process process = Child.Start(arguments, redirectInput: true);
		_processes.Add(process);
		return process;
	}

	/// <summary>Registers a process to kill at the end of the test.</summary>
	public void Track(Process process)
	{
		_processes.Add(process);
	}

	/// <summary>Waits for every started child to exit.</summary>
	public void WaitAll(TimeSpan timeout)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		foreach (Process process in _processes)
		{
			TimeSpan left = deadline - DateTime.UtcNow;
			if (!process.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero))
			{
				throw new AssertFailedException($"Process {process.Id} did not exit within {timeout.TotalSeconds} s.");
			}
		}
	}

	public void Dispose()
	{
		foreach (Process process in _processes)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
					process.WaitForExit(10000);
				}
			}
			catch (InvalidOperationException)
			{
			}
			process.Dispose();
		}
		for (int attempt = 0; attempt < 20; attempt++)
		{
			try
			{
				Directory.Delete(Root, recursive: true);
				return;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				Thread.Sleep(100);
			}
			catch (DirectoryNotFoundException)
			{
				return;
			}
		}
	}
}

/// <summary>The AgentKit.TestChild program, copied next to the tests by the project reference.</summary>
internal static class Child
{
	/// <summary>The program's path (its native launcher).</summary>
	public static string ExecutablePath { get; } = FindExecutable();

	private static string FindExecutable()
	{
		string name = OperatingSystem.IsWindows() ? "AgentKit.TestChild.exe" : "AgentKit.TestChild";
		string path = Path.Combine(AppContext.BaseDirectory, name);
		if (!File.Exists(path))
		{
			throw new FileNotFoundException($"The test child is missing: {path}. Build tests/AgentKit.TestChild.");
		}
		return path;
	}

	/// <summary>The command line to run the test child in a mode: <c>[path, mode, arguments...]</c>.</summary>
	public static List<string> Command(params string[] arguments)
	{
		return [ExecutablePath, .. arguments];
	}

	/// <summary>Starts the test child. Its output is read and dropped unless the test takes standard output.</summary>
	public static Process Start(IEnumerable<string> arguments, bool redirectInput, IReadOnlyDictionary<string, string?>? environment = null)
	{
		ProcessStartInfo startInfo = new(ExecutablePath)
		{
			UseShellExecute = false,
			RedirectStandardInput = redirectInput,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}
		// Never inherit a run's lock defaults from the environment the tests run in.
		startInfo.Environment.Remove("UAK_LOCK_NAME");
		startInfo.Environment.Remove("UAK_LOCK_PRIORITY");
		// Nor a hold of the test process's own (an in-process hold marks this process's environment while it lasts).
		startInfo.Environment.Remove("UAK_LOCK_HELD");
		foreach ((string name, string? value) in environment ?? new Dictionary<string, string?>())
		{
			if (value is null)
			{
				startInfo.Environment.Remove(name);
			}
			else
			{
				startInfo.Environment[name] = value;
			}
		}
		Process process = Process.Start(startInfo)!;
		process.ErrorDataReceived += (_, _) => { };
		process.BeginErrorReadLine();
		if (!redirectInput)
		{
			process.OutputDataReceived += (_, _) => { };
			process.BeginOutputReadLine();
		}
		return process;
	}

	/// <summary>(PID, start time) of a process that has exited.</summary>
	public static ProcessIdentity DeadProcess()
	{
		using Process process = Start(["sleep", "300", "0"], redirectInput: false);
		ProcessIdentity identity = ProcessIdentity.TryGet(process.Id) ?? new ProcessIdentity(process.Id, null);
		process.WaitForExit();
		return identity;
	}
}

/// <summary>Directory links for tests of paths that reach one place two ways.</summary>
internal static class DirectoryLinks
{
	/// <summary>
	/// Creates <paramref name="link"/> pointing at <paramref name="target"/>: a junction on Windows (no privilege needed), a
	/// symbolic link elsewhere. False when the system refuses.
	/// </summary>
	public static bool TryCreate(string link, string target)
	{
		if (!OperatingSystem.IsWindows())
		{
			try
			{
				Directory.CreateSymbolicLink(link, target);
				return true;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return false;
			}
		}
		ProcessStartInfo startInfo = new(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target })
		{
			startInfo.ArgumentList.Add(argument);
		}
		using Process process = Process.Start(startInfo)!;
		process.StandardOutput.ReadToEnd();
		process.StandardError.ReadToEnd();
		process.WaitForExit();
		return process.ExitCode == 0 && Directory.Exists(link);
	}

	/// <summary>Removes a link made by <see cref="TryCreate"/>, never what it points at.</summary>
	public static void Remove(string link)
	{
		try
		{
			if (Directory.Exists(link))
			{
				// A junction or symbolic link is removed as itself; Directory.Delete does not follow it.
				Directory.Delete(link, recursive: false);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}
}

/// <summary>Polling helpers.</summary>
internal static class Wait
{
	/// <summary>Waits until the condition holds, or fails the test after the timeout.</summary>
	public static void Until(Func<bool> condition, TimeSpan timeout, string what)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
			{
				throw new AssertFailedException($"Timed out after {timeout.TotalSeconds} s waiting for {what}.");
			}
			Thread.Sleep(50);
		}
	}
}
