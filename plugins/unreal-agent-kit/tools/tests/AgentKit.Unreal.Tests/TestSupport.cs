// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal.Tests;

/// <summary>A temporary fake engine and project on disk, deleted on dispose.</summary>
internal sealed class Sandbox : IDisposable
{
	public Sandbox(UnrealPlatform? platform = null, bool installed = true)
	{
		Platform = platform ?? UnrealPlatform.Win64;
		Root = Path.Combine(Path.GetTempPath(), "uak-unreal-tests", Guid.NewGuid().ToString("N"));
		EngineRoot = Path.Combine(Root, "UE");
		Write(Path.Combine(EngineRoot, "Engine", "Build", "Build.version"), """{ "MajorVersion": 5, "MinorVersion": 8, "PatchVersion": 0 }""");
		if (installed)
		{
			Write(Path.Combine(EngineRoot, "Engine", "Build", "InstalledBuild.txt"), "");
		}
		else
		{
			Write(Path.Combine(EngineRoot, "Engine", "Source", "Programs", "UnrealBuildTool", "UnrealBuildTool.csproj"), "<Project />");
			Write(Layout.BuildScript, "");
		}
		Directory.CreateDirectory(Path.Combine(EngineRoot, "Engine", "Source"));
		Write(Path.Combine(EngineRoot, "Engine", "Binaries", "ThirdParty", "DotNet", "10.0", Platform.DotNetRid, Platform.ExecutableName("dotnet")), "");
		Write(Layout.UnrealBuildToolAssembly, "");
		Write(Layout.EditorCommandExecutable, "");

		ProjectDirectory = Path.Combine(Root, "Game");
		ProjectFile = Path.Combine(ProjectDirectory, "Game.uproject");
		Write(ProjectFile, "{}");
		Write(Path.Combine(ProjectDirectory, "Source", "Game.Target.cs"), "public class GameTarget : TargetRules { public GameTarget(TargetInfo Target) : base(Target) { Type = TargetType.Game; } }");
		Write(Path.Combine(ProjectDirectory, "Source", "GameEditor.Target.cs"), "public class GameEditorTarget : TargetRules { public GameEditorTarget(TargetInfo Target) : base(Target) { Type = TargetType.Editor; } }");
		Write(Path.Combine(ProjectDirectory, "Source", "Game", "Game.Build.cs"), "");
		SourceFile = Write(Path.Combine(ProjectDirectory, "Source", "Game", "Private", "Foo.cpp"), "#include \"Foo.h\"\n");
		HeaderFile = Write(Path.Combine(ProjectDirectory, "Source", "Game", "Public", "Foo.h"), "#pragma once\n");
		StateDirectory = Path.Combine(ProjectDirectory, "Saved", "AgentKit");
	}

	public UnrealPlatform Platform { get; }
	public string Root { get; }
	public string EngineRoot { get; }
	public string ProjectDirectory { get; }
	public string ProjectFile { get; }
	public string SourceFile { get; }
	public string HeaderFile { get; }
	public string StateDirectory { get; }
	public EngineLayout Layout => new(EngineRoot, Platform);

	public ListLogger Logger { get; } = new();

	public UakContext Context(bool withProject = true) => new()
	{
		ProjectFile = withProject ? new FileInfo(ProjectFile) : null,
		EngineRoot = new DirectoryInfo(EngineRoot),
		StateDirectory = new DirectoryInfo(StateDirectory),
		Logger = Logger,
	};

	public static string Write(string path, string contents)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, contents);
		return path;
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Root, recursive: true);
		}
		catch (IOException)
		{
		}
	}
}

/// <summary>A process runner that records invocations and plays back scripted output.</summary>
internal sealed class FakeProcessRunner : IEncodedProcessRunner
{
	/// <summary>Called with each invocation; prints lines through the callback and returns the exit code.</summary>
	public Func<ProcessInvocation, Action<string>, int> Behaviour { get; set; } = (_, _) => 0;

	public List<ProcessInvocation> Invocations { get; } = [];

	/// <summary>The output encoding each invocation asked for (null: the default, UTF-8).</summary>
	public List<System.Text.Encoding?> Encodings { get; } = [];

	/// <summary>The process ID reported as the child's, before <see cref="Behaviour"/> runs; null reports none.</summary>
	public int? StartedPid { get; set; }

	public Task<int> RunAsync(ProcessInvocation invocation, Action<string> onLine, CancellationToken cancellationToken) =>
		RunAsync(invocation, null, onLine, null, cancellationToken);

	public Task<int> RunAsync(ProcessInvocation invocation, System.Text.Encoding? outputEncoding, Action<string> onLine, CancellationToken cancellationToken) =>
		RunAsync(invocation, outputEncoding, onLine, null, cancellationToken);

	public Task<int> RunAsync(ProcessInvocation invocation, System.Text.Encoding? outputEncoding, Action<string> onLine, Action<int>? onStarted, CancellationToken cancellationToken)
	{
		Invocations.Add(invocation);
		Encodings.Add(outputEncoding);
		if (StartedPid is int Pid)
		{
			onStarted?.Invoke(Pid);
		}
		return Task.FromResult(Behaviour(invocation, onLine));
	}

	/// <summary>A behaviour that prints the lines and returns the exit code.</summary>
	public static Func<ProcessInvocation, Action<string>, int> Prints(int exitCode, params string[] lines) => (_, OnLine) =>
	{
		foreach (string Line in lines)
		{
			OnLine(Line);
		}
		return exitCode;
	};
}

/// <summary>An editor lock that records who took it and whether it was released.</summary>
internal sealed class FakeLock : IEditorLockProvider
{
	public List<string> Names { get; } = [];
	public int Held { get; private set; }
	public int Released { get; private set; }

	/// <summary>Runs as the lock is released: what the next holder does first.</summary>
	public Action? OnRelease { get; set; }

	public Task<IAsyncDisposable> AcquireAsync(UakContext context, string name, CancellationToken cancellationToken)
	{
		Names.Add(name);
		Held++;
		return Task.FromResult<IAsyncDisposable>(new Hold(this));
	}

	sealed class Hold(FakeLock owner) : IAsyncDisposable
	{
		public ValueTask DisposeAsync()
		{
			owner.Held--;
			owner.Released++;
			owner.OnRelease?.Invoke();
			return ValueTask.CompletedTask;
		}
	}
}

/// <summary>A logger that keeps every formatted message.</summary>
internal sealed class ListLogger : ILogger
{
	readonly object Gate = new();

	public List<(LogLevel Level, string Message)> Entries { get; } = [];

	public string Text
	{
		get
		{
			lock (Gate)
			{
				return string.Join('\n', Entries.Select(Entry => Entry.Message));
			}
		}
	}

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		lock (Gate)
		{
			Entries.Add((logLevel, formatter(state, exception)));
		}
	}
}

internal static class Fixtures
{
	public static string[] Lines(string name) => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
