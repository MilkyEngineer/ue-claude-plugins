// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;
using AgentKit.Core;

namespace AgentKit.Unreal.Tests;

[TestClass]
public sealed class CommandTests
{
	static UnrealServices Services(FakeProcessRunner runner, FakeLock? lockProvider = null, Dictionary<string, string>? environment = null) => new()
	{
		Runner = runner,
		Lock = lockProvider ?? new FakeLock(),
		HostPlatform = UnrealPlatform.Win64,
		GetEnvironmentVariable = Name => environment is not null && environment.TryGetValue(Name, out string? Value) ? Value : null,
	};

	[TestMethod]
	public async Task UbtOutputIsDecodedInTheConsoleCodePage()
	{
		Assert.AreSame(ProcessRunner.ConsoleEncoding, new UnrealServices().ToolOutputEncoding);
		Assert.IsInstanceOfType<IEncodedProcessRunner>(new UnrealServices().Runner);

		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, "[1/1] Compile [x64] Foo.cpp", "Result: Succeeded") };
		UnrealServices WithLatin1 = new()
		{
			Runner = Runner,
			Lock = new FakeLock(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
			ToolOutputEncoding = System.Text.Encoding.Latin1,
		};

		await new CompileCommand(WithLatin1).RunAsync(Box.Context(), [Box.SourceFile], CancellationToken.None);
		await new BuildCommand(WithLatin1).RunAsync(Box.Context(), [], CancellationToken.None);
		Runner.Behaviour = (_, _) => 0;
		await new TestCommand(WithLatin1).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None);

		// UBT (compile, build) is decoded in the console's code page; the editor is not UBT and keeps the default.
		CollectionAssert.AreEqual(new[] { System.Text.Encoding.Latin1, System.Text.Encoding.Latin1, null }, Runner.Encodings.ToArray());
	}

	// ---- uak compile ----

	[TestMethod]
	public async Task CompileRunsSingleFileWithoutTheLock()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, "[1/1] Compile [x64] Foo.cpp", "Result: Succeeded") };
		FakeLock Lock = new();
		string ResultFile = Path.Combine(Box.Root, "result.json");

		int Exit = await new CompileCommand(Services(Runner, Lock, new() { ["UAK_MAX_PARALLEL_ACTIONS"] = "3" }))
			.RunAsync(Box.Context(), [Box.SourceFile, "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.IsEmpty(Lock.Names);
		ProcessInvocation Invocation = Runner.Invocations.Single();
		CollectionAssert.IsSubsetOf(new[] { "GameEditor", "Win64", "Development", "-Project=" + Box.ProjectFile, "-WaitMutex", "-MaxParallelActions=3", "-SingleFile=" + Box.SourceFile },
			Invocation.Arguments.ToArray());
		Assert.DoesNotContain("-SingleFileBuildDependents", Invocation.Arguments);
		Assert.Contains("compile: PASSED (1 of 1 clean)", Box.Logger.Text);

		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual("passed", Result.RootElement.GetProperty("Outcome").GetString());
		Assert.AreEqual("ok", Result.RootElement.GetProperty("Files")[0].GetProperty("Status").GetString());
		// The UBT output is kept in the state directory.
		Assert.StartsWith(Path.Combine(Box.StateDirectory, "Logs", "compile-"), Result.RootElement.GetProperty("LogFile").GetString()!);
		Assert.Contains("[1/1] Compile [x64] Foo.cpp", File.ReadAllText(Result.RootElement.GetProperty("LogFile").GetString()!));
	}

	[TestMethod]
	public async Task CompileErrorsAreReportedInTheCommonForm()
	{
		using Sandbox Box = new();
		string ErrorLine = Box.SourceFile + "(3,5): error C2065: 'X': undeclared identifier";
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(6, "[1/1] Compile [x64] Foo.cpp", ErrorLine, "Result: Failed (OtherCompilationError)") };

		int Exit = await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.SourceFile], CancellationToken.None);

		Assert.AreEqual(1, Exit);
		Assert.Contains(Box.SourceFile + "(3): error: C2065: 'X': undeclared identifier", Box.Logger.Text);
		Assert.Contains("FAILED", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AHeaderCountsAsCompiledThroughItsGeneratedFile()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, "[1/1] Compile [x64] Foo.h.cpp", "Result: Succeeded") };

		int Exit = await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.HeaderFile, "-dependents"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.Contains("-SingleFileBuildDependents", Runner.Invocations[0].Arguments);
	}

	[TestMethod]
	[DataRow("Foo.h.cpp")]
	[DataRow("Foo.h.obj")]
	public async Task DependentsAreReportedOnTheirOwnLines(string headerItem)
	{
		using Sandbox Box = new();
		string ResultFile = Path.Combine(Box.Root, "result.json");
		string DependentError = Path.Combine(Box.ProjectDirectory, "Source", "Game", "Private", "Bar.cpp") + "(9,1): error C2065: 'Y': undeclared identifier";
		FakeProcessRunner Runner = new()
		{
			Behaviour = FakeProcessRunner.Prints(6, $"[1/3] Compile [x64] {headerItem}", "[2/3] Compile [x64] Foo.cpp", "[3/3] Compile [x64] Bar.cpp", DependentError,
				"Result: Failed (OtherCompilationError)"),
		};

		int Exit = await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.HeaderFile, "-dependents", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains("ok           Foo.cpp (dependent)", Box.Logger.Text);
		Assert.Contains("FAILED       Bar.cpp (dependent)", Box.Logger.Text);
		Assert.Contains("(1 of 1 clean; 1 of 2 dependents clean)", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual("ok", Result.RootElement.GetProperty("Files")[0].GetProperty("Status").GetString());
		JsonElement Dependents = Result.RootElement.GetProperty("Dependents");
		Assert.AreEqual(2, Dependents.GetArrayLength());
		Assert.AreEqual("Bar.cpp", Dependents[1].GetProperty("Path").GetString());
		Assert.AreEqual("failed", Dependents[1].GetProperty("Status").GetString());
	}

	[TestMethod]
	public async Task AHeaderIsNotCompiledJustBecauseItsDependentsWere()
	{
		// UBT skipped the header itself (e.g. HEADER_UNIT_SKIP) but compiled the files that include it.
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, "[1/2] Compile [x64] Foo.cpp", "[2/2] Compile [x64] Bar.cpp", "Result: Succeeded") };

		int Exit = await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.HeaderFile, "-dependents"], CancellationToken.None);

		Assert.AreEqual(2, Exit, Box.Logger.Text);
		Assert.Contains("NOT COMPILED " + Box.HeaderFile, Box.Logger.Text);
		Assert.Contains("ok           Foo.cpp (dependent)", Box.Logger.Text);
		Assert.Contains("ok           Bar.cpp (dependent)", Box.Logger.Text);
		Assert.Contains("the file itself was not checked", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AHeaderCountsOnlyThroughItsGeneratedFile()
	{
		// An action on the bare header name is not its generated translation unit.
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, "[1/1] Compile [x64] Foo.h", "Result: Succeeded") };

		Assert.AreEqual(2, await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.HeaderFile], CancellationToken.None));
		Assert.Contains("NOT COMPILED", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AFileUbtDidNotCompileIsASetupError()
	{
		// UBT silently drops a -SingleFile that is not part of the target, and then says the target is up to date.
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, "Target is up to date", "Result: Succeeded") };

		int Exit = await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.SourceFile], CancellationToken.None);

		Assert.AreEqual(2, Exit);
		Assert.Contains("NOT COMPILED", Box.Logger.Text);
		Assert.Contains("not part of target GameEditor", Box.Logger.Text);
	}

	[TestMethod]
	public async Task AUbtFailureWithoutDiagnosticsIsAFailure()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(999, "Something went wrong") };

		int Exit = await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.SourceFile], CancellationToken.None);

		Assert.AreEqual(1, Exit);
		Assert.Contains("UBT failed (exit code 999)", Box.Logger.Text);
	}

	[TestMethod]
	public async Task CompileUsageErrors()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new();
		CompileCommand Command = new(Services(Runner));

		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), [], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), [Box.SourceFile, "-bogus"], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), [Path.Combine(Box.Root, "Missing.cpp")], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), [Box.ProjectFile], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(withProject: false), [Box.SourceFile], CancellationToken.None));
		Assert.AreEqual(2, await new CompileCommand(Services(Runner, environment: new() { ["UAK_MAX_PARALLEL_ACTIONS"] = "lots" }))
			.RunAsync(Box.Context(), [Box.SourceFile], CancellationToken.None));
		Assert.IsEmpty(Runner.Invocations);
	}

	[TestMethod]
	public async Task AProgramThatWillNotStartIsASetupError()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = (_, _) => throw new ProcessStartException("Could not run dotnet", new IOException("gone")) };

		Assert.AreEqual(2, await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.SourceFile], CancellationToken.None));
		Assert.Contains("Could not run dotnet", Box.Logger.Text);
	}

	// ---- uak build ----

	[TestMethod]
	public async Task BuildHoldsTheLockAroundUbt()
	{
		using Sandbox Box = new();
		FakeLock Lock = new();
		FakeProcessRunner Runner = new();
		Runner.Behaviour = (Invocation, OnLine) =>
		{
			Assert.AreEqual(1, Lock.Held, "UBT must run while the lock is held");
			foreach (string Line in Fixtures.Lines("build-succeeded.log"))
			{
				OnLine(Line);
			}
			return 0;
		};

		int Exit = await new BuildCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-config=DebugGame", "-MaxParallelActions=2"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { "uak build GameEditor" }, Lock.Names);
		Assert.AreEqual(1, Lock.Released);
		CollectionAssert.AreEqual(new[] { Box.Layout.UnrealBuildToolAssembly, "GameEditor", "Win64", "DebugGame", "-Project=" + Box.ProjectFile, "-WaitMutex", "-NoHotReloadFromIDE", "-MaxParallelActions=2" },
			Runner.Invocations[0].Arguments.ToArray());
		Assert.Contains("build: SUCCEEDED", Box.Logger.Text);
	}

	[TestMethod]
	public async Task BuildFailuresPrintTheErrors()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(6, Fixtures.Lines("build-link-errors.log")) };

		int Exit = await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), ["-target=Other", "-platform=Linux"], CancellationToken.None);

		Assert.AreEqual(1, Exit);
		Assert.AreEqual("Other", Runner.Invocations[0].Arguments[1]);
		Assert.AreEqual("Linux", Runner.Invocations[0].Arguments[2]);
		Assert.Contains("error: LNK1120: 3 unresolved externals", Box.Logger.Text);
		Assert.Contains("build: FAILED (Failed (OtherCompilationError)", Box.Logger.Text);
	}

	[TestMethod]
	public async Task BuildPassesUbtOptionsAfterTheSeparator()
	{
		using Sandbox Box = new();
		FakeLock Lock = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, Fixtures.Lines("build-succeeded.log")) };

		int Exit = await new BuildCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-target=Game", "--", "-DisableAdaptiveUnity", "-Module=Foo", "-Module=Bar"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { "uak build Game" }, Lock.Names);
		CollectionAssert.AreEqual(new[] { Box.Layout.UnrealBuildToolAssembly, "Game", "Win64", "Development", "-Project=" + Box.ProjectFile, "-WaitMutex", "-NoHotReloadFromIDE", "-DisableAdaptiveUnity", "-Module=Foo", "-Module=Bar" },
			Runner.Invocations[0].Arguments.ToArray());
	}

	[TestMethod]
	[DataRow("-NoMutex")]
	[DataRow("-MaxParallelActions=64")]
	[DataRow("OtherEditor")]
	public async Task BuildRejectsAPassThroughThatBreaksItsGuarantees(string argument)
	{
		using Sandbox Box = new();
		FakeLock Lock = new();
		FakeProcessRunner Runner = new();

		Assert.AreEqual(2, await new BuildCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["--", argument], CancellationToken.None));
		Assert.IsEmpty(Runner.Invocations);
		Assert.IsEmpty(Lock.Names, "A usage error never waits for the lock.");
		Assert.Contains(argument, Box.Logger.Text);
	}

	[TestMethod]
	public async Task CompilePassesUbtOptionsAfterTheSeparator()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(0, "[1/1] Compile [x64] Foo.cpp", "Result: Succeeded") };

		int Exit = await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.SourceFile, "--", "-DisableAdaptiveUnity"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.AreEqual("-DisableAdaptiveUnity", Runner.Invocations[0].Arguments[^1]);
		Assert.Contains("-SingleFile=" + Box.SourceFile, Runner.Invocations[0].Arguments);

		Assert.AreEqual(2, await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.SourceFile, "--", "-SingleFile=Other.cpp"], CancellationToken.None));
		Assert.AreEqual(2, await new CompileCommand(Services(Runner)).RunAsync(Box.Context(), [Box.SourceFile, "--", "-WaitMutex"], CancellationToken.None));
		Assert.HasCount(1, Runner.Invocations);
	}

	[TestMethod]
	public async Task BuildRejectsAnUnknownPlatform()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new();
		Assert.AreEqual(2, await new BuildCommand(Services(Runner)).RunAsync(Box.Context(), ["-platform=Amiga"], CancellationToken.None));
		Assert.IsEmpty(Runner.Invocations);
	}

	// ---- uak test ----

	static Func<ProcessInvocation, Action<string>, int> WritesLog(string fixture, int exitCode) => (Invocation, _) =>
	{
		string LogFile = Invocation.Arguments.Single(Argument => Argument.StartsWith("-abslog=", StringComparison.Ordinal))["-abslog=".Length..];
		File.WriteAllLines(LogFile, Fixtures.Lines(fixture));
		return exitCode;
	};

	[TestMethod]
	public async Task TestPassesWhenEveryFoundTestPassed()
	{
		using Sandbox Box = new();
		FakeLock Lock = new();
		FakeProcessRunner Runner = new() { Behaviour = WritesLog("test-passed.log", 0) };
		string ResultFile = Path.Combine(Box.Root, "r.json");

		int Exit = await new TestCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-filter=Project.Math", "-name=MathRun", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { "uak test Project.Math" }, Lock.Names);
		Assert.AreEqual(1, Lock.Released);
		Assert.Contains("-abslog=" + Path.Combine(Box.ProjectDirectory, "Saved", "Logs", "MathRun.log"), Runner.Invocations[0].Arguments);
		Assert.Contains("test: PASSED: 17 of 17 passed, 0 failed, 0 skipped; found 17.", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual(17, Result.RootElement.GetProperty("FoundCount").GetInt32());
	}

	[TestMethod]
	public async Task TestFailuresAreListedWithTheirErrors()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = WritesLog("test-failed.log", 255) };

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Gameplay", "-gpu"], CancellationToken.None);

		Assert.AreEqual(1, Exit);
		Assert.Contains("-RenderOffscreen", Runner.Invocations[0].Arguments);
		Assert.Contains("Project.Gameplay.SpawnAtOrigin", Box.Logger.Text);
		Assert.Contains("Expected 'With armour: health after 10 damage is 95' to be true.", Box.Logger.Text);
		Assert.Contains("the editor exited with code 255", Box.Logger.Text);
	}

	[TestMethod]
	public async Task WindowedTestRunsTheEditorItselfWithThePassThrough()
	{
		using Sandbox Box = new();
		Sandbox.Write(Box.Layout.EditorExecutable, "");
		FakeLock Lock = new();
		FakeProcessRunner Runner = new() { Behaviour = WritesLog("test-passed.log", 0) };

		int Exit = await new TestCommand(Services(Runner, Lock)).RunAsync(Box.Context(),
			["-filter=Project.Math", "-windowed", "-resx=1280", "-resy=720", "-name=Windowed", "--", "-SCCProvider=None", "-GameSandbox"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		CollectionAssert.AreEqual(new[] { "uak test Project.Math" }, Lock.Names);
		ProcessInvocation Invocation = Runner.Invocations.Single();
		Assert.AreEqual(Box.Layout.EditorExecutable, Invocation.FileName);
		CollectionAssert.IsSubsetOf(new[] { "-windowed", "-ResX=1280", "-ResY=720", "-testexit=Automation Test Queue Empty" }, Invocation.Arguments.ToArray());
		Assert.DoesNotContain("-nullrhi", Invocation.Arguments);
		CollectionAssert.AreEqual(new[] { "-SCCProvider=None", "-GameSandbox" }, Invocation.Arguments.TakeLast(2).ToArray());
		Assert.Contains("(windowed 1280x720)", Box.Logger.Text);
		Assert.Contains("test: PASSED: 17 of 17 passed", Box.Logger.Text);
	}

	[TestMethod]
	public async Task TestPassesEditorArgumentsToAHeadlessRunToo()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = WritesLog("test-passed.log", 0) };

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math", "--", "-SCCProvider=None"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.AreEqual(Box.Layout.EditorCommandExecutable, Runner.Invocations[0].FileName);
		Assert.Contains("-nullrhi", Runner.Invocations[0].Arguments);
		Assert.AreEqual("-SCCProvider=None", Runner.Invocations[0].Arguments[^1]);
	}

	[TestMethod]
	public async Task AWindowedRunWithoutTheEditorBuiltIsASetupError()
	{
		// The sandbox has only the -Cmd editor.
		using Sandbox Box = new();
		FakeProcessRunner Runner = new();

		Assert.AreEqual(2, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math", "-windowed"], CancellationToken.None));
		Assert.Contains("editor not found: " + Box.Layout.EditorExecutable, Box.Logger.Text);
		Assert.IsEmpty(Runner.Invocations);
	}

	[TestMethod]
	public async Task NoMatchingTestsFails()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new() { Behaviour = WritesLog("test-no-match.log", 0) };

		Assert.AreEqual(1, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Missing"], CancellationToken.None));
		Assert.Contains("no automation tests matched 'Project.Missing'", Box.Logger.Text);
	}

	[TestMethod]
	public async Task EveryTestSkippedFailsAsNothingRan()
	{
		using Sandbox Box = new();
		string ResultFile = Path.Combine(Box.Root, "r.json");
		FakeProcessRunner Runner = new()
		{
			Behaviour = (Invocation, _) =>
			{
				string LogFile = Invocation.Arguments.Single(Argument => Argument.StartsWith("-abslog=", StringComparison.Ordinal))["-abslog=".Length..];
				File.WriteAllLines(LogFile,
				[
					"LogAutomationCommandLine: Display: Found 1 automation test based on 'Project.Gpu'",
					"LogAutomationController: Display: Test Completed. Result={Skipped} Name={Draw} Path={Project.Gpu.Draw}",
					"LogAutomationCommandLine: Display: **** TEST COMPLETE. EXIT CODE: 0 ****",
				]);
				return 0;
			},
		};

		int Exit = await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Gpu", "-resultfile=" + ResultFile], CancellationToken.None);

		Assert.AreEqual(1, Exit, Box.Logger.Text);
		Assert.Contains("problem: NOTHING RAN", Box.Logger.Text);
		Assert.Contains("test: FAILED (NOTHING RAN): 0 of 1 passed, 0 failed, 1 skipped; found 1.", Box.Logger.Text);
		using JsonDocument Result = JsonDocument.Parse(File.ReadAllText(ResultFile));
		Assert.AreEqual("nothing ran", Result.RootElement.GetProperty("Outcome").GetString());
		Assert.AreEqual(1, Result.RootElement.GetProperty("Skipped").GetInt32());
	}

	[TestMethod]
	public async Task AStaleLogIsNeverRead()
	{
		using Sandbox Box = new();
		// A passing log from an earlier run; this run's editor dies before writing its own.
		string Stale = Path.Combine(Box.ProjectDirectory, "Saved", "Logs", "uak-test.log");
		Sandbox.Write(Stale, string.Join('\n', Fixtures.Lines("test-passed.log")));
		FakeProcessRunner Runner = new() { Behaviour = FakeProcessRunner.Prints(-1073741819, "Fatal error!") };

		Assert.AreEqual(1, await new TestCommand(Services(Runner)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None));
		Assert.Contains("The editor wrote no log", Box.Logger.Text);
		Assert.Contains("Fatal error!", Box.Logger.Text);
	}

	[TestMethod]
	public async Task TheLogIsReadBeforeTheLockIsReleased()
	{
		using Sandbox Box = new();
		// The next `uak test` with the same (default) -name deletes the log as soon as it holds the lock.
		string Log = Path.Combine(Box.ProjectDirectory, "Saved", "Logs", "uak-test.log");
		FakeLock Lock = new() { OnRelease = () => File.Delete(Log) };
		FakeProcessRunner Runner = new() { Behaviour = WritesLog("test-passed.log", 0) };

		int Exit = await new TestCommand(Services(Runner, Lock)).RunAsync(Box.Context(), ["-filter=Project.Math"], CancellationToken.None);

		Assert.AreEqual(0, Exit, Box.Logger.Text);
		Assert.Contains("test: PASSED: 17 of 17 passed", Box.Logger.Text);
		Assert.DoesNotContain("wrote no log", Box.Logger.Text);
		Assert.IsFalse(File.Exists(Log), "The next holder removed the log, after this run had read it.");
	}

	[TestMethod]
	public async Task BuildAndTestRecordTheirChildInTheHolderRecord()
	{
		// With the real editor lock: if uak dies while UBT or the editor runs, the next holder waits for that process.
		using Sandbox Box = new();
		UakContext Context = Box.Context();
		List<int?> Recorded = [];
		FakeProcessRunner Runner = new() { StartedPid = Environment.ProcessId };
		UnrealServices RealLock = new()
		{
			Runner = Runner,
			Lock = new EditorLockProvider(),
			HostPlatform = UnrealPlatform.Win64,
			GetEnvironmentVariable = _ => null,
		};

		Runner.Behaviour = (_, OnLine) =>
		{
			Recorded.Add(AgentKit.Locking.EditorLock.GetStatus(Context).Holder?.CommandPid);
			foreach (string Line in Fixtures.Lines("build-succeeded.log"))
			{
				OnLine(Line);
			}
			return 0;
		};
		Assert.AreEqual(0, await new BuildCommand(RealLock).RunAsync(Context, [], CancellationToken.None), Box.Logger.Text);

		Func<ProcessInvocation, Action<string>, int> WritePassingLog = WritesLog("test-passed.log", 0);
		Runner.Behaviour = (Invocation, OnLine) =>
		{
			Recorded.Add(AgentKit.Locking.EditorLock.GetStatus(Context).Holder?.CommandPid);
			return WritePassingLog(Invocation, OnLine);
		};
		Assert.AreEqual(0, await new TestCommand(RealLock).RunAsync(Context, ["-filter=Project.Math"], CancellationToken.None), Box.Logger.Text);

		CollectionAssert.AreEqual(new int?[] { Environment.ProcessId, Environment.ProcessId }, Recorded);
		Assert.IsNull(AgentKit.Locking.EditorLock.GetStatus(Context).Holder, "Both released the lock.");
	}

	[TestMethod]
	public async Task TestUsageErrors()
	{
		using Sandbox Box = new();
		FakeProcessRunner Runner = new();
		TestCommand Command = new(Services(Runner));

		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), [], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), ["-filter=A;Quit"], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), ["-filter=A", "-name=a b"], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(withProject: false), ["-filter=A"], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), ["-filter=A", "-gpu", "-windowed"], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), ["-filter=A", "-resx=800"], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), ["-filter=A", "-windowed", "-resy=0"], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), ["-filter=A", "--", "-ExecCmds=Quit"], CancellationToken.None));
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), ["-filter=A", "--", "-abslog=other.log"], CancellationToken.None));
		File.Delete(Box.Layout.EditorCommandExecutable);
		Assert.AreEqual(2, await Command.RunAsync(Box.Context(), ["-filter=A"], CancellationToken.None));
		Assert.IsEmpty(Runner.Invocations);
	}

	[TestMethod]
	public void CommandsDescribeThemselves()
	{
		IUakCommand[] Commands = [new CompileCommand(), new BuildCommand(), new TestCommand()];
		CollectionAssert.AreEqual(new[] { "compile", "build", "test" }, Commands.Select(Command => Command.Name).ToArray());
		Assert.IsTrue(Commands.All(Command => Command.Summary.Length > 0 && Command.Usage.StartsWith("uak " + Command.Name, StringComparison.Ordinal) && Command.RequiresEngine));
	}
}
