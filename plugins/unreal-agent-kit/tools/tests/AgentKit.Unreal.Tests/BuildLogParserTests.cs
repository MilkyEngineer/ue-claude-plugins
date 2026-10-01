// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Unreal.Tests;

[TestClass]
public sealed class BuildLogParserTests
{
	const string PluginTests = @"C:\Work\MyGame\Plugins\MyPlugin\Source\MyPluginTests\Private\";

	[TestMethod]
	public void MsvcCompileErrorsAreParsedWithTheirUnit()
	{
		BuildLogSummary Summary = BuildLogParser.Parse(Fixtures.Lines("build-compile-errors.log"));

		Assert.AreEqual("Failed", Summary.Result);
		Assert.AreEqual("OtherCompilationError", Summary.ResultReason);
		Assert.IsFalse(Summary.Succeeded);
		Assert.AreEqual(11.45, Summary.TotalSeconds);
		Assert.HasCount(2, Summary.Errors);

		CompileDiagnostic First = Summary.Errors[0];
		Assert.AreEqual(PluginTests + "MyActor.cpp", First.File);
		Assert.AreEqual(337, First.Line);
		Assert.AreEqual(63, First.Column);
		Assert.AreEqual("C2440", First.Code);
		Assert.AreEqual("MyActor.cpp", First.Unit);
		Assert.AreEqual(PluginTests + "MyActor.cpp(337): error: C2440: 'initializing': cannot convert from 'initializer list' to 'const TTuple<UMyComponent *,TTuple<KeyType,ValueType>>'", First.ToString());

		CompileDiagnostic Second = Summary.Errors[1];
		Assert.AreEqual(1720, Second.Line);
		Assert.AreEqual("C4456", Second.Code);
		Assert.AreEqual("MyComponentTests.cpp", Second.Unit);

		// Warnings are kept apart; notes are dropped.
		Assert.HasCount(3, Summary.Warnings);
		Assert.IsTrue(Summary.Warnings.All(Warning => Warning.Severity == DiagnosticSeverity.Warning && Warning.Code == "C4996"));
		Assert.IsFalse(Summary.Errors.Concat(Summary.Warnings).Any(Diagnostic => Diagnostic.Message.Contains("see declaration", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void AHeaderErrorRepeatedInEveryUnitIsReportedOnce()
	{
		BuildLogSummary Summary = BuildLogParser.Parse(Fixtures.Lines("build-header-error-repeated.log"));

		Assert.HasCount(1, Summary.Errors);
		Assert.AreEqual(2, Summary.Errors[0].Count);
		Assert.AreEqual("C2280", Summary.Errors[0].Code);
		Assert.EndsWith(@"Templates\MemoryOps.h", Summary.Errors[0].File);
		// The unit is the first that reported it.
		Assert.AreEqual("MyActor.cpp", Summary.Errors[0].Unit);
		Assert.AreEqual(10.87, Summary.TotalSeconds);
	}

	[TestMethod]
	public void MsvcLinkerErrorsAreParsed()
	{
		BuildLogSummary Summary = BuildLogParser.Parse(Fixtures.Lines("build-link-errors.log"));

		Assert.HasCount(4, Summary.Errors);
		Assert.AreEqual(3, Summary.Errors.Count(Error => Error.Code == "LNK2019" && Error.File == "MyActorTests.cpp.obj"));
		CompileDiagnostic Last = Summary.Errors[3];
		Assert.AreEqual("LNK1120", Last.Code);
		Assert.IsNull(Last.Line);
		Assert.AreEqual(@"C:\Work\MyGame\Plugins\MyPlugin\Binaries\Win64\UnrealEditor-MyPluginTests.dll: error: LNK1120: 3 unresolved externals", Last.ToString());
		Assert.AreEqual(new UbtAction("Link", "UnrealEditor-MyPluginTests.dll"), Summary.Actions[0]);
	}

	[TestMethod]
	public void ASuccessfulBuildHasActionsAndNoErrors()
	{
		BuildLogSummary Summary = BuildLogParser.Parse(Fixtures.Lines("build-succeeded.log"));

		Assert.IsTrue(Summary.Succeeded);
		Assert.IsEmpty(Summary.Errors);
		Assert.Contains(new UbtAction("Compile", "MyActor.cpp"), Summary.Actions);
		Assert.Contains(new UbtAction("WriteMetadata", "MyGameEditor.target [NoUba]"), Summary.Actions);
		Assert.AreEqual(72.59, Summary.TotalSeconds);
		// "Warning: Visual Studio compiler ... is not a preferred version" and the ISPC banner are not diagnostics.
		Assert.IsTrue(Summary.Warnings.All(Warning => Warning.Code == "C4996"));
	}

	[TestMethod]
	public void ClangAndLldDiagnosticsAreParsed()
	{
		// Synthetic: no Linux or Mac log exists yet. The forms are clang's and lld's.
		BuildLogSummary Summary = BuildLogParser.Parse(
		[
			"[1/2] Compile Foo.cpp",
			"In file included from /work/Game/Source/Game/Private/Foo.cpp:3:",
			"/work/Game/Source/Game/Public/Foo.h:12:5: error: use of undeclared identifier 'Bar'",
			"/work/Game/Source/Game/Private/Foo.cpp:40:1: warning: unused variable 'X' [-Wunused-variable]",
			"/work/Game/Source/Game/Public/Foo.h:12:5: note: previous definition is here",
			"[2/2] Link libUnrealEditor-Game.so",
			"ld.lld: error: undefined symbol: FBar::Baz()",
			"C:\\Work\\Game\\Source\\Game\\Private\\Foo.cpp(7,3): error: expected ';' after expression",
			"Result: Failed (OtherCompilationError)",
		]);

		Assert.HasCount(3, Summary.Errors);
		Assert.AreEqual("/work/Game/Source/Game/Public/Foo.h(12): error: use of undeclared identifier 'Bar'", Summary.Errors[0].ToString());
		Assert.AreEqual("Foo.cpp", Summary.Errors[0].Unit);
		Assert.AreEqual("ld.lld: error: undefined symbol: FBar::Baz()", Summary.Errors[1].ToString());
		Assert.AreEqual("libUnrealEditor-Game.so", Summary.Errors[1].Unit);
		Assert.AreEqual(7, Summary.Errors[2].Line);
		Assert.HasCount(1, Summary.Warnings);
		Assert.AreEqual(40, Summary.Warnings[0].Line);
	}

	[TestMethod]
	public void UbtErrorsAndUpToDateAreParsed()
	{
		BuildLogSummary Summary = BuildLogParser.Parse(
		[
			"ERROR: Failed to find an Action that can be used to build \"C:\\Game\\Source\\Game\\Foo.cpp\" (does target use this file?)",
			"Target is up to date",
			"Result: Succeeded",
		]);

		Assert.HasCount(1, Summary.Errors);
		Assert.AreEqual("UnrealBuildTool", Summary.Errors[0].File);
		Assert.StartsWith("Failed to find an Action", Summary.Errors[0].Message);
		Assert.IsTrue(Summary.UpToDate);
		Assert.IsTrue(Summary.Succeeded);
	}

	[TestMethod]
	public void OrdinaryLinesAreNotDiagnostics()
	{
		BuildLogSummary Summary = BuildLogParser.Parse(
		[
			"Warning: Visual Studio compiler 14.51.36260 is not a preferred version",
			"  Intel(r) Implicit SPMD Program Compiler (Intel(r) ISPC), 1.24.0 (build commit  @ 20250404, LLVM 18.1.2) ",
			"Log file: C:\\Work\\Logs\\UnrealBuildTool\\Log.txt",
			"Using Visual Studio 14.51.36260 toolchain (C:\\Program Files (x86)\\Microsoft Visual Studio\\18\\BuildTools) and Windows 10.0.26100.0 SDK (C:\\Program Files (x86)\\Windows Kits\\10).",
			"        with",
		]);

		Assert.IsEmpty(Summary.Errors);
		Assert.IsEmpty(Summary.Warnings);
		Assert.IsNull(Summary.Result);
	}
}
