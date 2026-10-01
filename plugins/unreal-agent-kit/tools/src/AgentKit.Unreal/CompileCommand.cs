// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>
/// `uak compile &lt;file&gt;...`: compiles single files through UBT's -SingleFile= for the project's editor target, with the
/// platform's real compiler and the module's real compile environment. Nothing is linked and no binary changes, so it needs
/// no editor lock and runs while an editor is open. UBT allows one instance per engine; -WaitMutex queues behind any other.
/// </summary>
public sealed class CompileCommand : IUakCommand
{
	readonly UnrealServices Services;

	/// <summary>Creates the command with the real services.</summary>
	public CompileCommand() : this(new UnrealServices())
	{
	}

	/// <summary>Creates the command with the given services (for tests).</summary>
	public CompileCommand(UnrealServices services)
	{
		Services = services;
	}

	/// <inheritdoc/>
	public string Name => "compile";

	/// <inheritdoc/>
	public string Summary => "Compile source files or headers through UBT -SingleFile (no link, no editor lock).";

	/// <inheritdoc/>
	public string Usage =>
		"""
		uak compile <file>... [options]
		  <file>                    A path, or a bare file name found under the project's Source or Plugins.
		                            A header is compiled on its own through a generated file (it must be self-contained).
		  -dependents               Also compile every source file of the target that includes a given header
		                            (UBT -SingleFileBuildDependents). Each dependent gets its own line; the header
		                            still counts as compiled only through its own generated file.
		  -target=<name>            The UBT target (default: the project's editor target).
		  -config=<name>            Default Development.
		  -platform=<name>          Default host.
		  -MaxParallelActions=<n>   Default UAK_MAX_PARALLEL_ACTIONS, else UBT's own.
		  -maxerrors=<n>            Errors to print (default 10).
		  -resultfile=<path>        Also write the result as JSON.
		Exit code: 0 clean, 1 errors, 2 usage or setup error (including a file UBT did not compile).
		""";

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return UnrealServices.GuardAsync(context, () => RunCheckedAsync(context, arguments, cancellationToken));
	}

	async Task<int> RunCheckedAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments Arguments = new(arguments);
		bool Dependents = Arguments.GetFlag("dependents");
		string? Target = Arguments.GetString("target");
		string Configuration = Arguments.GetString("config", "Development")!;
		UnrealPlatform Platform = UnrealServices.ParsePlatform(Arguments);
		int? MaxParallel = Services.MaxParallelActions(Arguments);
		int MaxErrors = Arguments.GetInt("maxerrors", 1) ?? 10;
		string? ResultFile = Arguments.GetString("resultfile");
		Arguments.ThrowIfUnknown();
		if (Arguments.Positional.Count == 0)
		{
			throw new UakUsageException("give at least one file: uak compile <file>...");
		}

		string Project = UnrealServices.RequireProject(context);
		string ProjectDirectory = Path.GetDirectoryName(Project)!;
		EngineLayout Paths = Services.Paths(context);
		Target ??= TargetResolver.ResolveEditorTarget(Project);

		List<SourceFile> Files = [];
		foreach (string Argument in Arguments.Positional)
		{
			SourceFile File = SourceFiles.Describe(SourceFiles.Resolve(Argument, Environment.CurrentDirectory, ProjectDirectory));
			if (File.Module is null)
			{
				throw new UakSetupException($"{File.Path} is in no module (no *.Build.cs above it)");
			}
			if (!Files.Any(Existing => Existing.Path.Equals(File.Path, StringComparison.OrdinalIgnoreCase)))
			{
				Files.Add(File);
			}
		}
		foreach (SourceFile File in Files)
		{
			SourceFiles.DeleteSingleFileOutputs(File, ProjectDirectory);
		}

		UbtTarget UbtTarget = new(Target, Platform, Configuration, Project, MaxParallel);
		List<string> Extra = Files.Select(File => "-SingleFile=" + File.Path).ToList();
		if (Dependents)
		{
			Extra.Add("-SingleFileBuildDependents");
		}
		ProcessInvocation Invocation = UbtCommandLine.Build(Paths, UbtTarget, Extra);

		string LogFile = Services.NewLogFile(context, "compile");
		context.Logger.LogInformation("Compiling {Count} file(s) for {Target} {Platform} {Configuration} (UBT -SingleFile)...", Files.Count, Target, Platform.Name, Configuration);
		BuildLogParser Parser = new();
		Stopwatch Timer = Stopwatch.StartNew();
		int ExitCode;
		await using (StreamWriter Log = new(LogFile, append: false, new System.Text.UTF8Encoding(false)))
		{
			await Log.WriteLineAsync("> " + Invocation).ConfigureAwait(false);
			ExitCode = await Services.RunToolAsync(Invocation, Line =>
			{
				Log.WriteLine(Line);
				Parser.AddLine(Line);
			}, cancellationToken).ConfigureAwait(false);
		}
		Timer.Stop();

		CompileReport Report = CompileReport.Create(Files, Parser.Summary, ExitCode, Dependents, Target, Timer.Elapsed.TotalSeconds, LogFile);
		Report.Print(context.Logger, MaxErrors);
		UnrealServices.WriteResultFile(ResultFile, Report);
		return Report.ExitCode;
	}
}

/// <summary>The outcome of one file in a compile check.</summary>
/// <param name="Path">The file.</param>
/// <param name="Module">Its module.</param>
/// <param name="Status">"ok", "failed" or "not compiled".</param>
/// <param name="Errors">How many distinct errors came from its translation unit(s).</param>
public sealed record CompiledFile(string Path, string? Module, string Status, int Errors);

/// <summary>The outcome of `uak compile`, printed and written as JSON.</summary>
public sealed class CompileReport
{
	/// <summary>0 clean, 1 errors, 2 a file UBT did not compile.</summary>
	public int ExitCode { get; init; }

	/// <summary>"passed", "failed" or "not compiled".</summary>
	public required string Outcome { get; init; }

	/// <summary>The UBT target.</summary>
	public required string Target { get; init; }

	/// <summary>UBT's exit code.</summary>
	public int UbtExitCode { get; init; }

	/// <summary>Wall time of the UBT run, in seconds.</summary>
	public double Seconds { get; init; }

	/// <summary>The full UBT output.</summary>
	public required string LogFile { get; init; }

	/// <summary>Per file.</summary>
	public required List<CompiledFile> Files { get; init; }

	/// <summary>
	/// With -dependents: the other translation units UBT compiled, by their action item (e.g. "Bar.cpp"), each with its own
	/// status. They never stand in for a header that was not compiled itself.
	/// </summary>
	public List<CompiledFile> Dependents { get; init; } = [];

	/// <summary>Errors, as "file(line): error: ...".</summary>
	public required List<string> Errors { get; init; }

	/// <summary>Warnings in the given files, as "file(line): warning: ...".</summary>
	public required List<string> Warnings { get; init; }

	/// <summary>Builds the report from UBT's parsed output.</summary>
	public static CompileReport Create(List<SourceFile> files, BuildLogSummary summary, int ubtExitCode, bool dependents, string target, double seconds, string logFile)
	{
		HashSet<string> CompiledItems = summary.Actions.Where(Action => Action.Kind.Equals("Compile", StringComparison.OrdinalIgnoreCase))
			.Select(Action => Action.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
		List<CompiledFile> Results = [];
		HashSet<string> OwnItems = new(StringComparer.OrdinalIgnoreCase);
		foreach (SourceFile File in files)
		{
			// A file counts as compiled only through its own action: for a header, its generated "<Name>.h.cpp" (or ".h.obj").
			// A dependent that includes the header is reported on its own line, never as the header's proof of compiling.
			List<string> Items = File.ActionItems.ToList();
			OwnItems.UnionWith(Items);
			bool Compiled = Items.Any(CompiledItems.Contains);
			int Errors = summary.Errors.Count(Error => Error.Unit is not null && Items.Contains(Error.Unit, StringComparer.OrdinalIgnoreCase)
				|| Error.File.Equals(File.Path, StringComparison.OrdinalIgnoreCase));
			string Status = !Compiled ? "not compiled" : Errors > 0 ? "failed" : "ok";
			Results.Add(new CompiledFile(File.Path, File.Module, Status, Errors));
		}
		List<CompiledFile> DependentResults = [];
		if (dependents)
		{
			foreach (string Item in summary.Actions.Where(Action => Action.Kind.Equals("Compile", StringComparison.OrdinalIgnoreCase))
				.Select(Action => Action.Item).Distinct(StringComparer.OrdinalIgnoreCase).Where(Item => !OwnItems.Contains(Item)))
			{
				int Errors = summary.Errors.Count(Error => Item.Equals(Error.Unit, StringComparison.OrdinalIgnoreCase));
				DependentResults.Add(new CompiledFile(Item, null, Errors > 0 ? "failed" : "ok", Errors));
			}
		}
		bool AnyErrors = summary.Errors.Count > 0 || ubtExitCode != 0;
		bool AllCompiled = Results.All(Result => Result.Status != "not compiled");
		int ExitCode = AnyErrors ? 1 : AllCompiled ? 0 : 2;
		HashSet<string> Paths = files.Select(File => File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new CompileReport
		{
			ExitCode = ExitCode,
			Outcome = ExitCode == 0 ? "passed" : ExitCode == 1 ? "failed" : "not compiled",
			Target = target,
			UbtExitCode = ubtExitCode,
			Seconds = Math.Round(seconds, 1),
			LogFile = logFile,
			Files = Results,
			Dependents = DependentResults,
			Errors = summary.Errors.Select(Error => Error.ToString()).ToList(),
			Warnings = summary.Warnings.Where(Warning => Paths.Contains(Warning.File)).Select(Warning => Warning.ToString()).ToList(),
		};
	}

	/// <summary>Prints the report: one line per file, the first errors, the warnings in the given files, and a summary.</summary>
	public void Print(ILogger logger, int maxErrors)
	{
		foreach (CompiledFile File in Files)
		{
			string Mark = File.Status switch { "ok" => "ok          ", "failed" => "FAILED      ", _ => "NOT COMPILED" };
			logger.LogInformation("{Mark} {Path} [{Module}]", Mark, File.Path, File.Module);
			if (File.Status == "not compiled" && ExitCode == 2)
			{
				logger.LogError("    UBT did not compile it: the file is not part of target {Target}, or it is a header UBT skips (HEADER_UNIT_SKIP or UE_DEPRECATED_HEADER).", Target);
				if (Dependents.Count > 0)
				{
					logger.LogError("    Only files that include it were compiled (the dependent lines below); the file itself was not checked.");
				}
			}
		}
		foreach (CompiledFile Dependent in Dependents)
		{
			string Mark = Dependent.Status == "ok" ? "ok          " : "FAILED      ";
			logger.LogInformation("{Mark} {Item} (dependent)", Mark, Dependent.Path);
		}
		foreach (string Error in Errors.Take(maxErrors))
		{
			logger.LogError("    {Error}", Error);
		}
		if (Errors.Count > maxErrors)
		{
			logger.LogError("    ... and {More} more errors", Errors.Count - maxErrors);
		}
		foreach (string Warning in Warnings.Take(maxErrors))
		{
			logger.LogWarning("    {Warning}", Warning);
		}
		if (UbtExitCode != 0 && Errors.Count == 0)
		{
			logger.LogError("    UBT failed (exit code {Code}) without a compile error; see the log.", UbtExitCode);
		}
		int Clean = Files.Count(File => File.Status == "ok");
		string DependentText = Dependents.Count == 0 ? "" : $"; {Dependents.Count(Dependent => Dependent.Status == "ok")} of {Dependents.Count} dependents clean";
		logger.LogInformation("compile: {Outcome} ({Clean} of {Count} clean{Dependents}) in {Seconds:0.0} s. Log: {Log}", Outcome.ToUpperInvariant(), Clean, Files.Count, DependentText, Seconds, LogFile);
	}
}
