// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal.Tests;

/// <summary>A long build or compile stays visibly alive: its log is named at the start and written as it goes, and UBT's progress is echoed.</summary>
[TestClass]
public sealed class ProgressTests
{
	/// <summary>A clock that moves on by a fixed step each time it is read.</summary>
	sealed class SteppingClock(TimeSpan step) : TimeProvider
	{
		DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow()
		{
			DateTimeOffset Result = Now;
			Now += step;
			return Result;
		}
	}

	/// <summary>A clock that stands still until moved.</summary>
	sealed class ManualClock : TimeProvider
	{
		public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow() => Now;
	}

	[TestMethod]
	public void ProgressIsEchoedAtMostOncePerInterval()
	{
		ManualClock Clock = new();
		List<string> Echoes = [];
		UbtProgress Progress = new(Clock, TimeSpan.FromMinutes(1), Echoes.Add);

		Progress.AddLine("[1/300] Compile [x64] A.cpp");
		Clock.Now += TimeSpan.FromSeconds(59);
		Progress.AddLine("[2/300] Compile [x64] B.cpp");
		Assert.IsEmpty(Echoes, "Nothing in the first minute.");

		Clock.Now += TimeSpan.FromSeconds(2);
		Progress.AddLine("Some other output");
		Assert.IsEmpty(Echoes, "Only [n/N] lines are progress.");
		Progress.AddLine("[3/300] Compile [x64] C.cpp");
		CollectionAssert.AreEqual(new[] { "[3/300] Compile [x64] C.cpp (1m 01s so far)" }, Echoes);

		Clock.Now += TimeSpan.FromSeconds(30);
		Progress.AddLine("[4/300] Compile [x64] D.cpp");
		Assert.HasCount(1, Echoes);
		Clock.Now += TimeSpan.FromMinutes(70);
		Progress.AddLine("[299/300] Link [x64] Game.dll");
		Assert.AreEqual("[299/300] Link [x64] Game.dll (1h 11m so far)", Echoes[1]);
	}

	[TestMethod]
	public async Task BuildNamesItsLogFirstAndEchoesProgress()
	{
		using Sandbox Box = new();
		string? LogDuringRun = null;
		FakeProcessRunner Runner = new()
		{
			Behaviour = (_, OnLine) =>
			{
				OnLine("[1/3] Compile [x64] A.cpp");
				OnLine("[2/3] Compile [x64] B.cpp");
				// The log is written as it goes: what UBT printed so far is already in it.
				string LogFile = Directory.GetFiles(Path.Combine(Box.StateDirectory, "Logs"), "build-*.log").Single();
				using (FileStream Stream = new(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				using (StreamReader Reader = new(Stream))
				{
					LogDuringRun = Reader.ReadToEnd();
				}
				OnLine("[3/3] Link [x64] Game.dll");
				OnLine("Result: Succeeded");
				return 0;
			},
		};
		UnrealServices Services = new()
		{
			Runner = Runner,
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
			Clock = new SteppingClock(TimeSpan.FromSeconds(45)),
		};

		Assert.AreEqual(0, await new BuildCommand(Services).RunAsync(Box.Context(), [], CancellationToken.None), Box.Logger.Text);
		List<string> Messages = [.. Box.Logger.Entries.Select(Entry => Entry.Message)];
		StringAssert.StartsWith(Messages[0], "Log: " + Path.Combine(Box.StateDirectory, "Logs", "build-"));
		StringAssert.Contains(LogDuringRun!, "[2/3] Compile [x64] B.cpp");
		// Each clock read moves 45 s on: no echo for [1/3] (45 s), one for [2/3] (90 s), none for [3/3] (45 s after it).
		CollectionAssert.AreEqual(new[] { "  [2/3] Compile [x64] B.cpp (1m 30s so far)" }, Messages.Where(Message => Message.Contains("so far", StringComparison.Ordinal)).ToArray());
	}

	[TestMethod]
	public async Task CompileNamesItsLogFirst()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, "[1/1] Compile [x64] Foo.cpp", "Result: Succeeded") };
		UnrealServices Services = new()
		{
			Runner = Runner,
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
		};
		Assert.AreEqual(0, await new CompileCommand(Services).RunAsync(Box.Context(), [Box.SourceFile], CancellationToken.None), Box.Logger.Text);
		StringAssert.StartsWith(Box.Logger.Entries[0].Message, "Log: " + Path.Combine(Box.StateDirectory, "Logs", "compile-"));
	}
}
