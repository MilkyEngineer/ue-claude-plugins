// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal;

/// <summary>The options of a test run, parsed and checked: <c>uak test</c>'s, and the test options <c>uak mutate</c> passes on.</summary>
internal sealed record TestOptions
{
	public required string Filter { get; init; }
	public bool Cooked { get; init; }
	public EditorRendering Rendering { get; init; }
	public int ResX { get; init; } = EditorTestCommandLine.DefaultResX;
	public int ResY { get; init; } = EditorTestCommandLine.DefaultResY;
	public string? Maps { get; init; }
	public string? Configuration { get; init; }
	public string? PlatformName { get; init; }
	public bool SkipBuild { get; init; }
	public bool SkipCook { get; init; }
	public int? Timeout { get; init; }
	public string? ExpectFile { get; init; }
	public bool AllowUnexpected { get; init; }
	public IReadOnlyList<string> Watch { get; init; } = [];
	public bool Quiet { get; init; }
	public required string Name { get; init; }
	public string? ResultFile { get; init; }
	public IReadOnlyList<string> PassThrough { get; init; } = [];

	/// <summary>Whether the run records its summary for <c>uak runs show</c>; <c>uak mutate</c> records its own instead.</summary>
	public bool RecordSummary { get; init; } = true;

	/// <summary>Reads every test option from <paramref name="arguments"/> and checks them together. The caller then rejects the rest.</summary>
	/// <param name="arguments">The command's arguments.</param>
	/// <param name="defaultName">The -name= default; null makes -name= required.</param>
	public static TestOptions Parse(UakArguments arguments, string? defaultName)
	{
		string Filter = arguments.GetString("filter") ?? throw new UakUsageException("give -filter=<filter>");
		bool Cooked = arguments.GetFlag("cooked");
		bool Gpu = arguments.GetFlag("gpu");
		bool Windowed = arguments.GetFlag("windowed");
		int? ResX = arguments.GetInt("resx", 1, 16384);
		int? ResY = arguments.GetInt("resy", 1, 16384);
		string? Maps = arguments.GetString("map");
		string? Configuration = arguments.GetString("config");
		string? PlatformName = arguments.GetString("platform");
		bool SkipBuild = arguments.GetFlag("skipbuild");
		bool SkipCook = arguments.GetFlag("skipcook");
		int? Timeout = arguments.GetInt("timeout", 1);
		string? ExpectFile = arguments.GetString("expect");
		bool AllowUnexpected = arguments.GetFlag("allow-unexpected");
		IReadOnlyList<string> Watch = LogCategoryCounts.ParseWatch(arguments.GetString("watch"));
		bool Quiet = arguments.GetFlag("quiet");
		string Name = arguments.GetString("name", defaultName is null ? null : Cooked ? defaultName + "-cooked" : defaultName)
			?? throw new UakUsageException("give -name=<unique name>");
		string? ResultFile = arguments.GetString("resultfile");

		// The name is a file and, in a cooked run, a folder UAT empties: never a path, nor "." or "..".
		RunSummaries.CheckName(Name);
		if (AllowUnexpected && ExpectFile is null)
		{
			throw new UakUsageException("-allow-unexpected goes with -expect=<file>");
		}
		IReadOnlyList<string> PassThrough;
		if (Cooked)
		{
			if (Gpu || Windowed || ResX is not null || ResY is not null)
			{
				throw new UakUsageException("-gpu, -windowed, -resx= and -resy= are for editor runs: a cooked run's game renders with -nullrhi");
			}
			CookedTestCommandLine.CheckFilter(Filter);
			PassThrough = CookedTestCommandLine.CheckPassThrough(arguments.Rest);
		}
		else
		{
			if (Maps is not null || Configuration is not null || PlatformName is not null || SkipBuild || SkipCook || Timeout is not null)
			{
				throw new UakUsageException("-map=, -config=, -platform=, -skipbuild, -skipcook and -timeout= are for cooked runs: add -cooked");
			}
			if (Gpu && Windowed)
			{
				throw new UakUsageException("give -gpu or -windowed, not both: -windowed renders with a real RHI too");
			}
			if (!Windowed && (ResX is not null || ResY is not null))
			{
				throw new UakUsageException("-resx= and -resy= size the window of a -windowed run");
			}
			EditorTestCommandLine.CheckFilter(Filter);
			PassThrough = EditorTestCommandLine.CheckPassThrough(arguments.Rest);
		}
		return new TestOptions
		{
			Filter = Filter,
			Cooked = Cooked,
			Rendering = Windowed ? EditorRendering.Windowed : Gpu ? EditorRendering.Offscreen : EditorRendering.NullRhi,
			ResX = ResX ?? EditorTestCommandLine.DefaultResX,
			ResY = ResY ?? EditorTestCommandLine.DefaultResY,
			Maps = Maps,
			Configuration = Configuration,
			PlatformName = PlatformName,
			SkipBuild = SkipBuild,
			SkipCook = SkipCook,
			Timeout = Timeout,
			ExpectFile = ExpectFile,
			AllowUnexpected = AllowUnexpected,
			Watch = Watch,
			Quiet = Quiet,
			Name = Name,
			ResultFile = ResultFile,
			PassThrough = PassThrough,
		};
	}
}
