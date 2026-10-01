// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentKit.Core.Tests;

[TestClass]
public sealed class UakArgumentsTests
{
	[TestMethod]
	public void OptionsPositionalsAndRest()
	{
		UakArguments Arguments = new(["first", "-Name=Build", "--Flag", "second", "--", "cmd", "-Name=child", "--"]);
		CollectionAssert.AreEqual(new[] { "first", "second" }, Arguments.Positional.ToArray());
		CollectionAssert.AreEqual(new[] { "cmd", "-Name=child", "--" }, Arguments.Rest.ToArray());
		Assert.IsTrue(Arguments.HasSeparator);
		Assert.AreEqual("Build", Arguments.GetString("name"));
		Assert.IsTrue(Arguments.GetFlag("FLAG"));
		Arguments.ThrowIfUnknown();
	}

	[TestMethod]
	public void NegativeNumbersAndLoneDash_ArePositional()
	{
		UakArguments Arguments = new(["-1", "-"]);
		CollectionAssert.AreEqual(new[] { "-1", "-" }, Arguments.Positional.ToArray());
		Assert.IsFalse(Arguments.HasSeparator);
	}

	[TestMethod]
	public void Getters_ValidateValues()
	{
		UakArguments Arguments = new(["-Count=5", "-Bad=x", "-Bare", "-Level=high", "-On=false"]);
		Assert.AreEqual(5, Arguments.GetInt("count", 1, 10));
		Assert.IsNull(Arguments.GetInt("missing"));
		Assert.ThrowsExactly<UakUsageException>(() => Arguments.GetInt("bad"));
		Assert.ThrowsExactly<UakUsageException>(() => Arguments.GetInt("count", 6, 10));
		Assert.ThrowsExactly<UakUsageException>(() => Arguments.GetString("bare"));
		Assert.AreEqual(LogLevel.Critical, Arguments.GetEnum("missing-level", LogLevel.Critical));
		Assert.ThrowsExactly<UakUsageException>(() => Arguments.GetEnum("level", LogLevel.None));
		Assert.IsFalse(Arguments.GetFlag("on"));
		Assert.AreEqual("fallback", Arguments.GetString("missing", "fallback"));
		Assert.ThrowsExactly<UakUsageException>(() => Arguments.GetRequiredString("missing"));
	}

	[TestMethod]
	public void PrefixedOptions_AreReadTogether()
	{
		UakArguments Arguments = new(["-param:clean=true", "-Param:Platforms=Win64,PS5", "-param:empty=", "-other=1"]);
		CollectionAssert.AreEqual(new[] { "clean=true", "Platforms=Win64,PS5", "empty=" }, Arguments.GetPrefixed("param:").Select(Pair => Pair.Key + "=" + Pair.Value).ToArray());
		Assert.ThrowsExactly<UakUsageException>(Arguments.ThrowIfUnknown, "-other is still unread");
		Assert.ThrowsExactly<UakUsageException>(() => new UakArguments(["-param:flag"]).GetPrefixed("param:"));
		Assert.ThrowsExactly<UakUsageException>(() => new UakArguments(["-param:=1"]).GetPrefixed("param:"));
		Assert.IsEmpty(new UakArguments(["-x"]).GetPrefixed("param:"));
	}

	[TestMethod]
	public void Duplicates_And_Unknowns_AreUsageErrors()
	{
		Assert.ThrowsExactly<UakUsageException>(() => new UakArguments(["-A=1", "-a=2"]));
		UakArguments Arguments = new(["-Known", "-Other=1", "-Accepted"]);
		Arguments.GetFlag("known");
		Arguments.Accept("accepted");
		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(Arguments.ThrowIfUnknown);
		StringAssert.Contains(Error.Message, "-Other");
		Assert.IsFalse(Error.Message.Contains("-Known", StringComparison.Ordinal));
	}

	[TestMethod]
	public void TooManyPositionals_IsAUsageError()
	{
		UakArguments Arguments = new(["a", "b", "c"]);
		Arguments.ThrowIfMorePositionalThan(3);
		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(() => Arguments.ThrowIfMorePositionalThan(1));
		StringAssert.Contains(Error.Message, "b c");
	}
}

[TestClass]
public sealed class UakJsonTests
{
	public enum Colour
	{
		Red,
		Green,
	}

	public sealed record Sample(string Name, DateTime When, DateTimeOffset? Offset, Colour Colour, int? Missing);

	[TestMethod]
	public void Times_AreIso8601Utc()
	{
		DateTime Local = new(2026, 10, 1, 12, 30, 0, 123, DateTimeKind.Local);
		Assert.AreEqual(Local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), UakJson.FormatTime(Local));
		Assert.AreEqual("2026-10-01T06:05:00.000Z", UakJson.FormatTime(new DateTime(2026, 10, 1, 6, 5, 0, DateTimeKind.Utc)));
		Assert.AreEqual("2026-10-01T06:05:00.000Z", UakJson.FormatTime(new DateTime(2026, 10, 1, 6, 5, 0, DateTimeKind.Unspecified)));

		DateTime Parsed = UakJson.ParseTime("2026-10-01T08:05:00+02:00");
		Assert.AreEqual(DateTimeKind.Utc, Parsed.Kind);
		Assert.AreEqual(new DateTime(2026, 10, 1, 6, 5, 0, DateTimeKind.Utc), Parsed);
		Assert.AreEqual(DateTimeKind.Utc, UakJson.ParseTime("2026-10-01T06:05:00").Kind);
	}

	[TestMethod]
	public void RoundTrip_WithNamesAsDeclared_EnumsAsStrings_AndTabs()
	{
		Sample Value = new("run", new DateTime(2026, 10, 1, 6, 5, 0, DateTimeKind.Utc), new DateTimeOffset(2026, 10, 1, 8, 5, 0, TimeSpan.FromHours(2)), Colour.Green, null);
		string Json = UakJson.Serialize(Value);
		StringAssert.Contains(Json, "\"Name\": \"run\"");
		StringAssert.Contains(Json, "\"When\": \"2026-10-01T06:05:00.000Z\"");
		StringAssert.Contains(Json, "\"Offset\": \"2026-10-01T06:05:00.000Z\"");
		StringAssert.Contains(Json, "\"Colour\": \"Green\"");
		StringAssert.Contains(Json, "\n\t\"Name\"");
		Assert.IsFalse(Json.Contains('\r', StringComparison.Ordinal));

		Sample Back = UakJson.Deserialize<Sample>(Json);
		Assert.AreEqual(Value with { Offset = Value.Offset!.Value.ToUniversalTime() }, Back);
	}

	[TestMethod]
	public void Utf8_HasNoBom_EndsWithNewline_AndReadsWithABom()
	{
		byte[] Bytes = UakJson.SerializeToUtf8(new Sample("ü", DateTime.UnixEpoch, null, Colour.Red, 1));
		Assert.AreNotEqual(0xEF, Bytes[0]);
		Assert.AreEqual((byte)'\n', Bytes[^1]);
		byte[] WithBom = [0xEF, 0xBB, 0xBF, .. Bytes];
		Assert.AreEqual("ü", UakJson.Deserialize<Sample>(WithBom).Name);
		Assert.AreEqual("ü", UakJson.Deserialize<Sample>(Encoding.UTF8.GetString(Bytes)).Name);
	}

	[TestMethod]
	public void NullDocument_AndBadJson_Throw()
	{
		Assert.ThrowsExactly<JsonException>(() => UakJson.Deserialize<Sample>("null"));
		Assert.ThrowsExactly<JsonException>(() => UakJson.Deserialize<Sample>("{ nope"));
		Assert.IsTrue(UakJson.Options.IsReadOnly);
	}
}

[TestClass]
public sealed class UakSelfTests
{
	[TestMethod]
	public void Apphost_RunsAlone()
	{
		(string File, IReadOnlyList<string> Prefix) = UakSelf.GetCommand(@"C:\kit\uak.exe", @"C:\kit\uak.dll");
		Assert.AreEqual(@"C:\kit\uak.exe", File);
		Assert.IsEmpty(Prefix);
	}

	[TestMethod]
	[DataRow(@"C:\UE\Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe")]
	[DataRow("/opt/UE/Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/dotnet")]
	public void Muxer_RunsTheEntryAssembly(string dotnet)
	{
		(string File, IReadOnlyList<string> Prefix) = UakSelf.GetCommand(dotnet, "/kit/uak.dll");
		Assert.AreEqual(dotnet, File);
		CollectionAssert.AreEqual(new[] { "/kit/uak.dll" }, Prefix.ToArray());
	}

	[TestMethod]
	public void Unknown_Throws()
	{
		Assert.ThrowsExactly<InvalidOperationException>(() => UakSelf.GetCommand(null, null));
		Assert.ThrowsExactly<InvalidOperationException>(() => UakSelf.GetCommand("dotnet", null));
	}

	[TestMethod]
	public void GlobalOptions_RepeatTheContext()
	{
		using TempTree Tree = new();
		UakContext Context = new()
		{
			ProjectFile = new FileInfo(Tree.Project("Game/Game.uproject")),
			EngineRoot = new DirectoryInfo(Tree.Engine("UE")),
			RequestedVersionControl = "git",
			StateDirectory = new DirectoryInfo(Tree.Root),
			Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
		};
		CollectionAssert.AreEqual(new[] { "-project=" + Tree.Path("Game", "Game.uproject"), "-engine=" + Tree.Path("UE"), "-vcs=git" }, UakSelf.GetGlobalOptions(Context).ToArray());
		ProcessInvocation Invocation = UakSelf.GetInvocation(["env"]);
		Assert.AreEqual("env", Invocation.Arguments[^1]);
	}
}

[TestClass]
public sealed class UakConsoleLoggerTests
{
	[TestMethod]
	public void Levels_GoToTheRightWriter_WithPrefixes()
	{
		StringWriter Out = new();
		StringWriter Err = new();
		UakConsoleLogger Logger = new(Out, Err);
		Logger.LogDebug("hidden");
		Logger.LogInformation("shown {Value}", 1);
		Logger.LogWarning("careful");
		Logger.LogError(new InvalidOperationException("boom"), "failed");
		Assert.AreEqual("shown 1" + Environment.NewLine, Out.ToString());
		StringAssert.Contains(Err.ToString(), "warning: careful");
		StringAssert.Contains(Err.ToString(), "error: failed: boom");
		Assert.IsFalse(Err.ToString().Contains("   at ", StringComparison.Ordinal));
	}

	[TestMethod]
	public void Verbose_ShowsDebugAndStacks()
	{
		StringWriter Out = new();
		StringWriter Err = new();
		UakConsoleLogger Logger = new(Out, Err, LogLevel.Debug);
		Logger.LogDebug("detail");
		StringAssert.Contains(Out.ToString(), "detail");
		Assert.IsFalse(Logger.IsEnabled(LogLevel.Trace));
		Assert.IsFalse(Logger.IsEnabled(LogLevel.None));
	}
}
