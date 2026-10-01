// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>
/// `uak build`: builds a target through UBT under the editor lock, because a running editor holds the modules' binaries.
/// </summary>
public sealed class BuildCommand : IUakCommand
{
	readonly UnrealServices Services;

	/// <summary>Creates the command with the real services.</summary>
	public BuildCommand() : this(new UnrealServices())
	{
	}

	/// <summary>Creates the command with the given services (for tests).</summary>
	public BuildCommand(UnrealServices services)
	{
		Services = services;
	}

	/// <inheritdoc/>
	public string Name => "build";

	/// <inheritdoc/>
	public string Summary => "Build a target through UBT, under the editor lock.";

	/// <inheritdoc/>
	public string Usage =>
		"""
		uak build [options]
		  -target=<name>            The UBT target (default: the project's editor target).
		  -config=<name>            Default Development.
		  -platform=<name>          Default host.
		  -MaxParallelActions=<n>   Default UAK_MAX_PARALLEL_ACTIONS, else UBT's own.
		  -maxerrors=<n>            Errors to print (default 10).
		  -resultfile=<path>        Also write the result as JSON.
		Exit code: 0 built, 1 failed, 2 usage or setup error.
		""";

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return UnrealServices.GuardAsync(context, () => RunCheckedAsync(context, arguments, cancellationToken));
	}

	async Task<int> RunCheckedAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments Arguments = new(arguments);
		string? Target = Arguments.GetString("target");
		string Configuration = Arguments.GetString("config", "Development")!;
		UnrealPlatform Platform = UnrealServices.ParsePlatform(Arguments);
		int? MaxParallel = Services.MaxParallelActions(Arguments);
		int MaxErrors = Arguments.GetInt("maxerrors", 1) ?? 10;
		string? ResultFile = Arguments.GetString("resultfile");
		Arguments.ThrowIfUnknown();
		if (Arguments.Positional.Count > 0)
		{
			throw new UakUsageException("uak build takes no positional arguments: " + string.Join(' ', Arguments.Positional));
		}

		string? Project = context.ProjectFile?.FullName;
		if (Target is null)
		{
			Target = TargetResolver.ResolveEditorTarget(Project ?? throw new UakSetupException("no project found: give -project= or -target="));
		}
		EngineLayout Paths = Services.Paths(context);
		ProcessInvocation Invocation = UbtCommandLine.Build(Paths, new UbtTarget(Target, Platform, Configuration, Project, MaxParallel));
		string LogFile = Services.NewLogFile(context, "build");
		// Named first, and written as it goes, so a long build can be followed in its log while it runs.
		context.Logger.LogInformation("Log: {Log}", LogFile);

		BuildLogParser Parser = new();
		int ExitCode;
		Stopwatch Timer;
		await using (StreamWriter Log = new(LogFile, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true })
		{
			await Log.WriteLineAsync("> " + Invocation).ConfigureAwait(false);
			await using IAsyncDisposable Lock = await Services.Lock.AcquireAsync(context, $"uak build {Target}", cancellationToken).ConfigureAwait(false);
			context.Logger.LogInformation("Building {Target} {Platform} {Configuration}...", Target, Platform.Name, Configuration);
			UbtProgress Progress = new(Services.Clock, UbtProgress.DefaultInterval, Text => context.Logger.LogInformation("  {Progress}", Text));
			Timer = Stopwatch.StartNew();
			ExitCode = await Services.RunToolAsync(Invocation, Line =>
			{
				Log.WriteLine(Line);
				Parser.AddLine(Line);
				Progress.AddLine(Line);
			}, Lock, cancellationToken).ConfigureAwait(false);
			Timer.Stop();
		}

		BuildLogSummary Summary = Parser.Summary;
		foreach (CompileDiagnostic Error in Summary.Errors.Take(MaxErrors))
		{
			context.Logger.LogError("    {Error}", Error);
		}
		if (Summary.Errors.Count > MaxErrors)
		{
			context.Logger.LogError("    ... and {More} more errors", Summary.Errors.Count - MaxErrors);
		}
		bool Built = ExitCode == 0 && Summary.Errors.Count == 0;
		int Compiled = Summary.Actions.Count(Action => Action.Kind.Equals("Compile", StringComparison.OrdinalIgnoreCase));
		context.Logger.LogInformation("build: {Outcome} ({Result}{Reason}; {Actions} actions, {Compiled} compiles{UpToDate}) in {Seconds:0.0} s. Log: {Log}",
			Built ? "SUCCEEDED" : "FAILED", Summary.Result ?? $"exit code {ExitCode}", Summary.ResultReason is null ? "" : $" ({Summary.ResultReason})",
			Summary.Actions.Count, Compiled, Summary.UpToDate ? ", up to date" : "", Timer.Elapsed.TotalSeconds, LogFile);
		UnrealServices.WriteResultFile(ResultFile, new
		{
			Outcome = Built ? "succeeded" : "failed",
			Target,
			UbtExitCode = ExitCode,
			Summary.Result,
			Summary.ResultReason,
			Actions = Summary.Actions.Count,
			Compiles = Compiled,
			Summary.UpToDate,
			Seconds = Math.Round(Timer.Elapsed.TotalSeconds, 1),
			LogFile,
			Errors = Summary.Errors.Select(Error => Error.ToString()).ToList(),
			Warnings = Summary.Warnings.Count,
		});
		return Built ? 0 : 1;
	}
}
