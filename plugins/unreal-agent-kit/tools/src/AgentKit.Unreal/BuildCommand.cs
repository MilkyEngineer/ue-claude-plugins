// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;
using System.Globalization;
using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Vcs;
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
		uak build [options] [-- <UBT option>...]
		  -target=<name>            The UBT target (default: the project's editor target).
		  -config=<name>            Default Development.
		  -platform=<name>          Default host.
		  -MaxParallelActions=<n>   Default UAK_MAX_PARALLEL_ACTIONS, else UBT's own.
		  -maxerrors=<n>            Errors to print (default 10).
		  -name=<name>              Names the build's summary for `uak runs show -name=<name>` (default uak-build).
		  -resultfile=<path>        Also write the result as JSON. Deleted first, so an old one is never read
		                            as this run's; a refused or stopped run writes Outcome refused or stopped.
		  -- <UBT option>...        More UBT options, passed as given, e.g. -- -DisableAdaptiveUnity -Module=Foo.
		                            Options only: UBT reads a bare word as a target. Rejected because uak sets them or
		                            they break the lock or UBT's mutex: -Project, -Target, -TargetList, -Mode, -WaitMutex,
		                            -NoMutex, -NoHotReloadFromIDE, -ForceHotReload, -LiveCoding, -MaxParallelActions.
		The summary names the tree as the build found it once it held the lock: the revision and the changed files (or
		their count and hash), read alongside the build.
		A source file written while UBT ran may be older than what UBT made from its old text, which every later build
		would keep. So after a build, even one that failed, such a file under the project's Source or Plugins gets a write
		time past the build (its bytes aren't touched), and the next build recompiles what uses it. A warning names it:
		build again before you test, as this build's binaries may mix old and new. A build that compiled nothing marks
		such a file and says nothing, as its binaries mix nothing, but UBT rewrites UHT's timestamps even then.
		A file the build writes itself (a pre-build step's output) counts until `uak test config -build-writes=` names
		it; a file dated past the build's end doesn't count. A file whose time can't be set is reported too, even by a
		build that compiled nothing, and -resultfile lists both: EditedDuringBuild has all of them, EditNotMarked those
		that couldn't be marked.
		A whole build (no UBT arguments) that succeeds and overlaps no edit also clears what an earlier build of its
		target left (`uak test`'s help says what), and says so, but keeps a file that couldn't be marked unless it was
		saved after the build that reported it and before this one, and tries that mark again. Any build that reports
		such a file, with UBT arguments or not, leaves that refusal itself, as no later build would pick the file up;
		a build given UBT arguments clears none, and says what it leaves standing.
		It refuses to build while an interrupted `uak mutate` has left its patch in the tree (`uak mutate -restore`).
		-name= is a plain file name without spaces.
		Exit code: 0 built, 1 failed, 2 usage or setup error, or a refusal.
		""";

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return UnrealServices.GuardAsync(context, () => SummaryWriter.RecordingAsync(context, Services, "build", PeekName(arguments),
			() => UnrealServices.WithFreshResultFileAsync("build", arguments, () => RunCheckedAsync(context, arguments, cancellationToken))));
	}

	/// <summary>The -name= the build records its summary under, read before anything else is checked (null when it can't be read).</summary>
	static string? PeekName(IReadOnlyList<string> arguments)
	{
		try
		{
			return new UakArguments(arguments, DotSplitRule.Options).GetString("name", "uak-build");
		}
		catch (UakUsageException)
		{
			return null;
		}
	}

	async Task<int> RunCheckedAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments Arguments = new(arguments, DotSplitRule.Options);
		string? Target = Arguments.GetString("target");
		string Configuration = Arguments.GetString("config", "Development")!;
		UnrealPlatform Platform = UnrealServices.ParsePlatform(Arguments);
		int? MaxParallel = Services.MaxParallelActions(Arguments);
		int MaxErrors = Arguments.GetInt("maxerrors", 1) ?? 10;
		string Name = Arguments.GetString("name", "uak-build")!;
		RunSummaries.CheckName(Name);
		string? ResultFile = Arguments.GetString("resultfile");
		Arguments.ThrowIfUnknown();
		Arguments.ThrowIfPositional("uak build");
		IReadOnlyList<string> PassThrough = UbtCommandLine.CheckPassThrough(Arguments.PassThrough);

		BuildRequest Request = new()
		{
			Target = Target,
			Configuration = Configuration,
			Platform = Platform,
			MaxParallelActions = MaxParallel,
			MaxErrors = MaxErrors,
			PassThrough = PassThrough,
			Name = Name,
			ResultFile = ResultFile,
		};
		return (await BuildAsync(context, Request, heldLock: null, cancellationToken).ConfigureAwait(false)).Built ? 0 : 1;
	}

	/// <summary>
	/// Builds and reports. Without <paramref name="heldLock"/> it takes the editor lock, and refuses to build a tree that a
	/// mutation run left changed; <c>uak mutate</c> passes the lock it holds. Returns whether the target built, and what
	/// UBT did.
	/// </summary>
	internal async Task<BuildOutcome> BuildAsync(UakContext context, BuildRequest request, IAsyncDisposable? heldLock, CancellationToken cancellationToken)
	{
		string? Project = context.ProjectFile?.FullName;
		string Target = request.Target ?? TargetResolver.ResolveEditorTarget(Project ?? throw new UakSetupException("no project found: give -project= or -target="));
		EngineLayout Paths = Services.Paths(context);
		ProcessInvocation Invocation = UbtCommandLine.Build(Paths, new UbtTarget(Target, request.Platform, request.Configuration, Project, request.MaxParallelActions),
			request.PassThrough);
		string LogFile = Services.NewLogFile(context, "build");
		// Named first, and written as it goes, so a long build can be followed in its log while it runs.
		context.Logger.LogInformation("Log: {Log}", LogFile);

		BuildLogParser Parser = new();
		int ExitCode;
		Stopwatch Timer;
		Task<VcsTreeState>? TreeTask;
		BuildOutcome Outcome;
		BuildEditReport Edits;
		List<string> MarkerNotes = [];
		List<string> MarkerInfos = [];
		// A build that asks to count clears journals as `uak build` does: a retry under another command's lock.
		bool ClearsJournals = heldLock is null || request.ClearsJournals;
		await using (StreamWriter Log = new(LogFile, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true })
		{
			await Log.WriteLineAsync("> " + Invocation).ConfigureAwait(false);
			IAsyncDisposable? OwnLock = heldLock is null
				? await Services.Lock.AcquireAsync(context, $"uak build {Target}", cancellationToken).ConfigureAwait(false)
				: null;
			await using (OwnLock)
			{
				IAsyncDisposable Lock = heldLock ?? OwnLock!;
				if (ClearsJournals)
				{
					MutationJournal.ThrowIfUnfinished(context, building: true);
				}
				// Read alongside the build, so it never holds the lock up.
				TreeTask = request.CaptureTree ? Services.StartCaptureTree(context, cancellationToken) : null;
				context.Logger.LogInformation("Building {Target} {Platform} {Configuration}...", Target, request.Platform.Name, request.Configuration);
				UbtProgress Progress = new(Services.Clock, UbtProgress.DefaultInterval, Text => context.Logger.LogInformation("  {Progress}", Text));
				BuildEditWindow Window = BuildEditWindow.Open(context, Services);
				Timer = Stopwatch.StartNew();
				ExitCode = await Services.RunToolAsync(Invocation, Line =>
				{
					Log.WriteLine(Line);
					Parser.AddLine(Line);
					Progress.AddLine(Line);
				}, Lock, cancellationToken).ConfigureAwait(false);
				Timer.Stop();
				Window.Close();
				Outcome = BuildOutcome.From(Parser.Summary, ExitCode) with { Started = Window.Opened, LogFile = LogFile };
				// Still under the lock, so no build writes past the stamp. What journals look after is read first, as clearing one turns its
				// stamp into an edit; and nothing that refuses goes before the stamp, so a kill or a throw up to there leaves all of it standing.
				Dictionary<string, DateTime> Managed = BuildEdits.GetManaged(context);
				// Up to date says nothing of the rest of the build: a pre-build step's own UBT prints it too.
				Edits = BuildEdits.Reported(await BuildEdits.StampAsync(context, Services, Managed, Window).ConfigureAwait(false), Parser.Summary.UpToDate && Parser.Summary.Actions.Count == 0);
				Outcome = Outcome with { Edits = Edits };
				// UBT arguments may build part of the target: such a build clears neither a journal nor the marker, and a plain `uak build` is there for that.
				bool Whole = request.PassThrough.Count == 0;
				// Asked before this build keeps anything, for what it says of the marker at its end (RebuildNeeded.DescribeEntry).
				bool StoodBefore = RebuildNeeded.Stands(context, Target, request.Platform.Name, request.Configuration);
				// What the next command must be told is on disk before the journal that would have stopped it can go. A file that couldn't be
				// marked is told too, after any build and with none standing: a stamped file recompiles at the next build, and nothing picks this one up.
				bool Keeps = (Whole && (request.KeepsOverlap || (ClearsJournals && Managed.Count > 0)
					|| RebuildNeeded.Stands(context, Target, request.Platform.Name, request.Configuration))) || Edits.NotMarked.Count > 0;
				bool Kept = !Keeps || RebuildNeeded.Keep(context, Target, request.Platform.Name, request.Configuration, Edits);
				// What a journal stands for goes to the marker first, so that the journal can go too.
				bool HandedOver = !(ClearsJournals && Whole) || RebuildNeeded.KeepWhatJournalsHold(context);
				Outcome = Outcome with { OverlapNotKept = !Kept || !HandedOver, NotKept = Kept ? null : RebuildNeeded.EntryOf(Target, request.Platform.Name, request.Configuration, Edits) };
				if (ClearsJournals && Outcome.OverlapNotKept)
				{
					// Said only when a journal stands: with none, what wasn't kept is recorded nowhere, and HoldInJournals says that.
					bool Held = Outcome.NotKept is not null && RebuildNeeded.HoldInJournals(context, Outcome.NotKept);
					if (Held || !HandedOver)
					{
						context.Logger.LogWarning("The mutation journals stay, as nothing would refuse once they went.");
					}
				}
				else if (ClearsJournals && Whole)
				{
					// A stopped mutation run whose files are back and stamped for a recompile waits for a whole build of its target to recompile them and succeed.
					try
					{
						MutationJournal.ClearAfterBuild(context, Target, request.Platform.Name, request.Configuration, Outcome.Built, Entry => MutateCommand.ShowsRecompile(Outcome, Entry),
							Outcome.DescribeFailure());
					}
					catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
					{
						context.Logger.LogWarning("Could not look for mutation journals this build clears: {Message}", Exception.Message);
					}
				}
				if (Outcome.Built && Whole && Edits.Overlapped.Count == 0)
				{
					try
					{
						MarkerNotes = await RebuildNeeded.AfterBuildAsync(context, Services, Target, request.Platform.Name, request.Configuration, Window.Opened).ConfigureAwait(false);
					}
					catch (Exception Exception) when (Exception is not OutOfMemoryException && !(Exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
					{
						// UBT succeeded, and nothing may throw that away. AfterBuildAsync writes the marker after what can throw, so it stands
						// as it was. It is given no token, so an OperationCanceledException there is no cancel of this command: .NET throws
						// one for ERROR_OPERATION_ABORTED, a query a file system or a share cancelled.
						string MarkerFile = RebuildNeededFile.GetFile(context);
						MarkerNotes = [$"build: could not clear what {MarkerFile} holds for {Target} {request.Platform.Name} {request.Configuration}, so it stays as it was,"
							+ $" and `uak test`, `uak mutate` and `uak lock run` go on refusing: {Exception.Message}; if no build can clear it, delete {MarkerFile}"];
					}
				}
				(MarkerInfos, List<string> EntryNotes) = RebuildNeeded.DescribeEntry(context, Target, request.Platform.Name, request.Configuration, StoodBefore, Whole,
					Kept && Keeps ? Edits.NotMarked : []);
				MarkerNotes.AddRange(EntryNotes);
			}
		}

		VcsTreeState? Tree = TreeTask is null ? null : await TreeTask.ConfigureAwait(false);
		BuildLogSummary Summary = Parser.Summary;
		foreach (CompileDiagnostic Error in Summary.Errors.Take(request.MaxErrors))
		{
			context.Logger.LogError("    {Error}", Error);
		}
		if (Summary.Errors.Count > request.MaxErrors)
		{
			context.Logger.LogError("    ... and {More} more errors", Summary.Errors.Count - request.MaxErrors);
		}
		bool Built = Outcome.Built;
		int Compiled = Outcome.Compiled.Count;
		SummaryWriter Out = new(context.Logger);
		if (Tree is not null)
		{
			Out.Info("tree: " + Tree);
		}
		string Result = Summary.Result ?? $"exit code {ExitCode}";
		string Reason = Summary.ResultReason is null ? "" : $" ({Summary.ResultReason})";
		string UpToDate = Summary.UpToDate ? ", up to date" : "";
		string Line = string.Create(CultureInfo.InvariantCulture,
			$"build: {(Built ? "SUCCEEDED" : "FAILED")} ({Result}{Reason}; {Summary.Actions.Count} actions, {Compiled} compiles{UpToDate}) in {Timer.Elapsed.TotalSeconds:0.0} s. Log: {LogFile}");
		foreach (string Warning in Edits.Warnings("build").Concat(MarkerNotes))
		{
			Out.Warning(Warning);
		}
		foreach (string Info in MarkerInfos)
		{
			Out.Info(Info);
		}
		if (Built)
		{
			Out.Info(Line);
		}
		else
		{
			Out.Error(Line);
		}
		if (request.RecordSummary)
		{
			Out.Record(context, Services, "build", request.Name, Built ? 0 : 1);
		}
		UnrealServices.WriteResultFile(request.ResultFile, new
		{
			Outcome = Built ? "succeeded" : "failed",
			Target,
			UbtExitCode = ExitCode,
			Summary.Result,
			Summary.ResultReason,
			Actions = Summary.Actions.Count,
			Compiles = Compiled,
			Summary.UpToDate,
			Tree,
			Seconds = Math.Round(Timer.Elapsed.TotalSeconds, 1),
			LogFile,
			Errors = Summary.Errors.Select(Error => Error.ToString()).ToList(),
			Warnings = Summary.Warnings.Count,
			Outcome.EditedDuringBuild,
			EditNotMarked = Edits.NotMarked,
		});
		return Outcome;
	}

	/// <summary>
	/// For a command that needs the binaries, holding the lock, and about to be refused: retries the rebuild of mutation journals
	/// whose rebuild failed (<see cref="MutationJournal.FindRebuildable"/>), once a source file changed since that failure, as
	/// another agent's half-edited file is the usual cause. It is the very <c>uak build</c> the refusal asks for, with the same
	/// checks and the same rule for clearing: the caller still calls <see cref="MutationJournal.ThrowIfUnfinished(UakContext, bool)"/>,
	/// which refuses unless it cleared them. Nothing changed, or a dead hold's command still runs and may be building beside it:
	/// nothing to try. A failed retry leaves the refusal, and records its own failure, as one that can't run at all does. Returns
	/// the edits the retry overlapped: the retry fires because a file changed, so another agent is likely still at work, and
	/// what those binaries are worth is the caller's to judge. The build keeps them (<see cref="RebuildNeeded"/>), before it can
	/// clear the journal, for the commands after it, whom that journal no longer stops.
	/// </summary>
	internal async Task<BuildEditReport> RetryPendingRebuildAsync(UakContext context, IAsyncDisposable heldLock, CancellationToken cancellationToken)
	{
		List<MutationJournal> Pending = MutationJournal.FindRebuildable(context);
		if (Pending.Count == 0 || heldLock is IOrphanedCommandHold { OrphanedCommand: not null })
		{
			return BuildEditReport.None;
		}
		// A file dated past now is no change, or every command would retry for it; the margin lets in what a build stamped with its
		// wait cut short, and a clock behind the file system's.
		DateTime Until = BuildEdits.End(context, Services) + Services.RecompileMargin;
		if (BuildEdits.Find(context, BuildEdits.GetManaged(context), Pending.Min(Journal => Journal.Record.RebuildFailure!.Started), Until).Count == 0)
		{
			context.Logger.LogInformation("Mutation: not retrying the failed rebuild of {Names}: no source file changed since.", string.Join(", ", Pending.Select(Journal => Journal.Record.Name)));
			return BuildEditReport.None;
		}
		BuildEditReport Edits = BuildEditReport.None;
		foreach (MutationRecord Record in Pending.Select(Journal => Journal.Record).DistinctBy(Record => (Record.BuildTarget, Record.BuildPlatform, Record.BuildConfiguration)))
		{
			if (UnrealPlatform.FromName(Record.BuildPlatform!) != Services.HostPlatform)
			{
				continue;
			}
			context.Logger.LogInformation("Mutation: source files changed since the rebuild of '{Name}' failed: retrying it, as a `uak build` of {Target} would.", Record.Name, Record.BuildTarget);
			BuildRequest Request = new()
			{
				Target = Record.BuildTarget,
				Configuration = Record.BuildConfiguration!,
				Platform = Services.HostPlatform,
				MaxParallelActions = Services.MaxParallelActions(new UakArguments([])),
				Name = "mutate-rebuild-" + Record.Name,
				RecordSummary = false,
				CaptureTree = false,
				ClearsJournals = true,
				KeepsOverlap = true,
			};
			DateTime Tried = BuildEdits.Start(context, Services);
			try
			{
				Edits = Edits.Plus((await BuildAsync(context, Request, heldLock, cancellationToken).ConfigureAwait(false)).Edits);
			}
			catch (Exception Exception) when (Exception is not OperationCanceledException and not OutOfMemoryException and not UakUsageException)
			{
				context.Logger.LogWarning("The retried rebuild could not run: {Message}", Exception.Message);
				RecordRetryFailure(context, Record, Tried, Exception.Message);
			}
		}
		return Edits;
	}

	/// <summary>Records why a retry never ran, so the next command doesn't try again until a file changes since this one.</summary>
	static void RecordRetryFailure(UakContext context, MutationRecord record, DateTime started, string why)
	{
		try
		{
			MutationJournal.RecordRebuildFailure(context, record.BuildTarget!, record.BuildPlatform!, record.BuildConfiguration!, new MutationRebuildFailure { Started = started, Message = why });
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			context.Logger.LogWarning("Could not record that the retried rebuild could not run: {Message}", Exception.Message);
		}
	}
}

/// <summary>
/// What a build did, for <c>uak mutate</c>: whether it built, whether UBT found it up to date, the items its Compile actions
/// named ("Foo.cpp", "Module.Game.2.cpp"), and its errors.
/// </summary>
internal sealed record BuildOutcome(bool Built, bool UpToDate, IReadOnlyList<string> Compiled, IReadOnlyList<CompileDiagnostic> Errors)
{
	/// <summary>
	/// The files its link actions wrote or tried to ("UnrealEditor-Game.dll", "libUnrealEditor-Game.so"): Link and its
	/// variants (Link-LTO, Link-PGO, Relink), not a stub (LinkStub) nor an archive.
	/// </summary>
	public IReadOnlyList<string> Linked { get; init; } = [];

	/// <summary>
	/// The items of <see cref="Compiled"/> that surely compiled: those no error was reported under, and none after a build
	/// that failed with no error its log names, or with any failure it can't lay on an action
	/// (<see cref="BuildLogSummary.EveryFailureAttributed"/>), where any action may have failed. A failed compile leaves the
	/// old object, so it is no evidence that the object is gone.
	/// </summary>
	public IReadOnlyList<string> CompiledClean { get; init; } = [];

	/// <summary>
	/// Whether UBT stopped part way: it failed with no "Result:" line and no error parsed, so it was killed, or crashed, or never
	/// got that far, and an action that was running may have written its output without its line ever being printed. The items
	/// above then say what UBT finished, not what it did.
	/// </summary>
	public bool StoppedPartWay { get; init; }

	/// <summary>When its window for edits opened (<see cref="BuildEditWindow"/>): just before UBT ran.</summary>
	public DateTime Started { get; init; }

	/// <summary>Its log.</summary>
	public string? LogFile { get; init; }

	/// <summary>What <see cref="BuildEdits.StampAsync"/> found while it ran.</summary>
	public BuildEditReport Edits { get; init; } = BuildEditReport.None;

	/// <summary>Whether an overlap it had to keep (<see cref="RebuildNeeded.Keep"/>) couldn't be written: nothing that refuses may go then.</summary>
	public bool OverlapNotKept { get; init; }

	/// <summary>Its own overlap, when it couldn't be written; a journal holds it instead (<see cref="MutationRecord.UnkeptOverlap"/>). Null otherwise.</summary>
	public RebuildNeededEntry? NotKept { get; init; }

	/// <summary>
	/// The source files that changed while it ran, marked for a recompile or not (<see cref="BuildEditReport.Overlapped"/>): its
	/// binaries may mix their old and new text. A mutation journal's files never count: they are the journal's.
	/// </summary>
	public IReadOnlyList<string> EditedDuringBuild => Edits.Overlapped;

	/// <summary>What stopped it, for a mutation journal; null when it built.</summary>
	public MutationRebuildFailure? DescribeFailure()
	{
		CompileDiagnostic? First = Errors.Count > 0 ? Errors[0] : null;
		return Built ? null : new MutationRebuildFailure { Started = Started, File = First?.File, Line = First?.Line, Message = First?.Message, LogFile = LogFile };
	}

	/// <summary>What a UBT run's log and exit code say.</summary>
	public static BuildOutcome From(BuildLogSummary summary, int exitCode)
	{
		bool Built = exitCode == 0 && summary.Errors.Count == 0;
		List<string> Compiled = [.. summary.Actions.Where(Action => Action.Kind.Equals("Compile", StringComparison.OrdinalIgnoreCase)).Select(Action => Action.Item)];
		return new BuildOutcome(Built, summary.UpToDate, Compiled, summary.Errors)
		{
			Linked = [.. summary.Actions.Where(Action => IsLink(Action.Kind)).Select(Action => Action.Item)],
			CompiledClean = Built || (summary.Errors.Count > 0 && summary.EveryFailureAttributed) ? [.. Compiled.Where(Item => !summary.FailedActions.Contains(Item))] : [],
			StoppedPartWay = !Built && summary.Result is null && summary.Errors.Count == 0,
		};
	}

	static bool IsLink(string kind)
	{
		return (kind.StartsWith("Link", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("Relink", StringComparison.OrdinalIgnoreCase))
			&& !kind.Equals("LinkStub", StringComparison.OrdinalIgnoreCase);
	}
}

/// <summary>What <see cref="BuildCommand.BuildAsync"/> builds.</summary>
internal sealed record BuildRequest
{
	/// <summary>The UBT target; null for the project's editor target.</summary>
	public string? Target { get; init; }

	public string Configuration { get; init; } = "Development";

	public required UnrealPlatform Platform { get; init; }

	public int? MaxParallelActions { get; init; }

	public int MaxErrors { get; init; } = 10;

	public IReadOnlyList<string> PassThrough { get; init; } = [];

	/// <summary>The name its summary is recorded under, for <c>uak runs show</c>.</summary>
	public string Name { get; init; } = "uak-build";

	public string? ResultFile { get; init; }

	/// <summary>Whether the build records its summary; <c>uak mutate</c> records its own instead.</summary>
	public bool RecordSummary { get; init; } = true;

	/// <summary>Whether the build reads and prints the tree it built (<see cref="VcsTreeState"/>); <c>uak mutate</c> reads it once, for its tests.</summary>
	public bool CaptureTree { get; init; } = true;

	/// <summary>
	/// With a held lock: whether the build counts for mutation journals as a <c>uak build</c> does, with its check first and
	/// <see cref="AgentKit.Locking.MutationJournal.ClearAfterBuild"/> after. Without a lock it always does.
	/// </summary>
	public bool ClearsJournals { get; init; }

	/// <summary>Whether what it overlaps is kept (<see cref="RebuildNeeded"/>), however it ends, as what clears the journal after it no longer stops the next command.</summary>
	public bool KeepsOverlap { get; init; }
}
