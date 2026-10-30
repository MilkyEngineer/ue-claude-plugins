// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.RegularExpressions;
using AgentKit.Core;
using AgentKit.Locking;
using AgentKit.Vcs;
using Microsoft.Extensions.Logging;

namespace AgentKit.Unreal;

/// <summary>
/// `uak mutate -name=&lt;unique&gt; -patch=&lt;diff&gt; -filter=&lt;filter&gt; [test options]`: a guarded mutation check. Under one hold
/// of the editor lock it applies the patch (git apply in a Git workspace, else a strict unified-diff applier), builds the
/// editor target, runs the editor tests, and then always puts the touched files back byte for byte and rebuilds, even when
/// the build or the tests fail or the run is cancelled, before it lets the lock go. A crash-safe journal
/// (<see cref="MutationJournal"/>) covers uak being killed. The mutation is "caught" (exit 0) when a test fails, "survived"
/// (exit 6) when the tests pass; anything that can't tell the two apart is inconclusive (exit 4), never a verdict.
/// </summary>
public sealed partial class MutateCommand : IUakCommand
{
	/// <summary>A test failed with the mutation.</summary>
	public const int CaughtExitCode = 0;

	/// <summary>
	/// The tests passed with the mutation. Not 1, which uak returns when it fails or is cancelled, and Windows gives a process
	/// that is killed: a mutation run that died must never read as one that survived.
	/// </summary>
	public const int SurvivedExitCode = 6;

	/// <summary>The mutated code didn't build (an error in a patched file, or in one that includes it), so no test ran.</summary>
	public const int NotBuiltExitCode = 3;

	/// <summary>
	/// The run says nothing about the mutation: the build compiled nothing or failed elsewhere, or the tests didn't start, ran
	/// nothing, or ended without a failed test but not cleanly (a crash, the editor's exit code).
	/// </summary>
	public const int InconclusiveExitCode = 4;

	/// <summary>The files could not all be put back, or the rebuild after failed: the tree or its binaries may hold the mutation.</summary>
	public const int RestoreFailedExitCode = 5;

	/// <summary>The extensions of the files a C++ build compiles: a patch to one that the mutant build compiled nothing for never reached the binaries.</summary>
	static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cpp", ".c", ".cc", ".cxx", ".h", ".hpp", ".hh", ".hxx", ".inl", ".inc", ".ixx", ".cppm", ".ispc" };

	/// <summary>The extensions of headers, whose errors may show in the files that include them.</summary>
	static readonly HashSet<string> HeaderExtensions = new(StringComparer.OrdinalIgnoreCase) { ".h", ".hpp", ".hh", ".hxx", ".inl", ".inc" };

	readonly UnrealServices Services;

	/// <summary>Creates the command with the real services.</summary>
	public MutateCommand() : this(new UnrealServices())
	{
	}

	/// <summary>Creates the command with the given services (for tests).</summary>
	public MutateCommand(UnrealServices services)
	{
		Services = services;
	}

	/// <inheritdoc/>
	public string Name => "mutate";

	/// <inheritdoc/>
	public string Summary => "Check that tests catch a change: apply a patch, build, test, then always revert and rebuild, under one editor lock.";

	/// <inheritdoc/>
	public string Usage =>
		"""
		uak mutate -name=<unique> -patch=<diff file> -filter=<filter> [test options] [-MaxParallelActions=<n>] [-- <editor argument>...]
		uak mutate -restore [-name=<name>] [-force | -accept] [-MaxParallelActions=<n>]
		uak mutate -status
		  Under one hold of the editor lock: applies the patch, builds the project's editor target, runs the editor
		  tests, then always puts every file the patch touched back (byte for byte, from copies taken first) and
		  rebuilds, even when the build or tests fail or on Ctrl+C, before releasing the lock.
		  The mutation is in the shared tree for the whole run: patch only files no other agent is editing (another
		  agent's `uak compile` sees the mutant, with a warning), don't edit them, or rely on reading them, yourself
		  while it runs (an edit made then is made over the mutant), and commit, shelve or preflight only once `uak
		  mutate -status` exits 0. Uncommitted changes in a patched file are fine: the copy keeps them. Run the same
		  tests green first: a verdict means something only against a passing run.
		  -name=<unique>        Names the run: its test log (<Project>/Saved/Logs/<name>.log), its journal
		                        (<State>/Mutations/<name>) and its summary for `uak runs show -name=<name>`, whose
		                        results it drops as it starts. A plain file name without spaces, not ending in '.',
		                        and no device name such as NUL.
		  -patch=<diff file>    A unified diff (git diff, diff -u) that changes, adds or deletes text files; paths are
		                        relative to the workspace root (the Git work tree or Perforce client root, else the
		                        project folder). No renames, mode changes, symbolic or hard links, or binary files.
		                        Each hunk must match exactly at the line its header names (make the patch against the
		                        file as it is: there is no search for moved lines, in a Git workspace either), and a
		                        hunk without context lines after its changes must end at the file's end (git reads it
		                        as the end). Applied with git apply in a Git workspace, which must apply every hunk at
		                        its header's line (git apply --check -v; a file with CRLF line ends under
		                        core.autocrlf=false needs a patch with CRLF ones, as git diff writes there), else by uak
		                        (every line it doesn't add keeps its own bytes and line end; an added line gets the
		                        kind most of the file's lines have). Each file's +/- lines are printed.
		  Test options: as for `uak test` editor runs (-filter, -expect, -allow-unexpected, -watch, -gpu, -windowed,
		  -resx, -resy, and -- editor arguments). Not -cooked: a staged game would keep the mutation after the files
		  are put back. Not -quiet.
		  -resultfile=<path>    Writes this check's result as JSON (Verdict, ExitCode, the files and their +/- lines,
		                        the tests' counts, Problems): deleted first, written at every exit.
		  -restore              Finish a mutation run that was killed: put its files back from its journal (those that
		                        still hold the mutation, or, for a run killed while it wrote the patch, the bytes the
		                        patch was to give them), rebuild, and clear the journal. Without -name=, every journal
		                        left. Run it on its own, not inside `uak lock run`: it takes the editor lock itself. A
		                        file changed since the patch was applied (an edit made over the mutant, which may keep
		                        it) is left and reported; then, with -name=, either
		  -accept               keeps such an edit, merged three ways (the mutant as base, the file as it is, the
		                        original), only where the edit left the lines around the mutation alone: the file
		                        gets the edit with the mutated lines as they were (what it held is saved beside the
		                        journal first) only when the strict near-edit rule holds for each change of the
		                        patch: its anchor (the lines around it, grown up and down until they occur once in
		                        the mutant) with one line each side (or the file's start or end) is in the file
		                        exactly once, byte for byte as in the mutant (a line end's '\r' aside only on the
		                        lines the patch added, or in a file with one kind of line end), both ways of lining
		                        the file up with the mutant keep those lines in place, none of them is in the file
		                        more often than in the mutant (a copy; a line of punctuation alone, such as '};',
		                        isn't counted, but a blank line and a brace alone are), and no mutated line (or line next to removed
		                        ones) is like another of them (an edit could swap the two without changing a byte);
		                        and the merge is clean (the edit and the mutation change different lines, an
		                        unchanged line between), no place is changed by both alike (the mutation taken out
		                        by hand), and the merge holds no more of the mutated lines than the original does.
		                        So an edit that changed, copied, moved or deleted a line there is refused, naming the
		                        lines it touched; so is anything else uak can't place for sure. Then it rebuilds and
		                        sets the journal aside.
		                        It also clears a journal uak can't read, once you have checked its files. Not for a
		                        file of a run killed while it wrote the patch (it may hold part of it), nor one the
		                        patch created or deleted: -force only. For a refused file, -force puts the original
		                        back and saves the edit: make the edit again on the original from there. A file that
		                        became a link (another hard link, or a symbolic link) is never written: remove the
		                        other link first.
		                        Or
		  -force                puts the originals back over every file, saving the bytes it overwrites beside the
		                        journal (<n>.<time>.current, never over bytes saved before), rebuilds, and sets the
		                        journal aside.
		                        Set aside: moved to <State>/DiscardedMutations/<name>-<time>, with its copies; printed.
		  -status               Lists the journals left, running or stopped, without the lock: exit 0 when none is,
		                        5 when one is (the tree or its binaries may hold a mutation: don't commit, shelve or
		                        preflight it).
		Refused (exit 2) when the patch can't be read or doesn't apply cleanly, when it changes nothing, when a file it
		creates exists, when a file it changes or deletes is a symbolic link or one of several hard links to one file,
		when the journal's folder can't be made, when a file can't be copied first or changes while the patch is
		written (the files written are put back; uak writes each file whole, and puts one whose write fails back at once
		while it holds exactly the run's own bytes, leaving anything else), when a folder of its name with no journal
		in it can't be deleted, or while an earlier mutation run's journal is left (or one uak can't read). In a Git
		workspace, an edit can get in before git reads a file: when git then refuses the patch (a hunk no longer
		applies, a file to change is gone, or one to create exists), git wrote nothing, so that file is left as it is
		and the run is refused (exit 2) with its journal cleared; when git applies it over the edit, the file holds
		neither its original nor the patch's bytes, and is left in place with its bytes saved beside the journal: no
		verdict, exit 5, and -accept merges the mutation out of it, or refuses it.
		While a stopped run's journal is left, `uak test` and `uak lock run` refuse to run; `uak compile` and `uak build`
		refuse until its files are put back (then a build is what clears the binaries); `uak vcs shelve` and `uak horde
		preflight -shelve` refuse while any journal is left, a running one's too (and warn that they can't look when no
		project is found). A journal a reader holds a file of open when the run clears it is left whole: the files are
		back and rebuilt and the verdict stands, with a warning, and `uak mutate -restore` clears it. A check can
		outlast a background shell: start a long one with `uak runs start`; `uak runs wait` then exits 1 for any exit
		code but 0, and prints the code and the verdict line.
		Exit code: 0 caught (a test failed with the mutation: a test, or a listed test, completed with Result Fail),
		1 no verdict (uak failed or was cancelled; the files were put back), 2 usage error or refused, 3 the mutant
		didn't build (an error in a patched file or one that includes it directly), 4 inconclusive (the build shows no
		compile of a patched source file, by its name or its module's Module.<name>.cpp unity file, <name> being the
		ShortName its Build.cs sets, if any, or no compile at all for a patched header; or it failed elsewhere; or the
		tests didn't start, ran nothing, skipped a listed test, or ended without a failed test but not cleanly), 5
		restore failed (a file changed since the patch was applied, or the rebuild after failed: run uak mutate
		-restore), 6 survived (the tests passed; not 1, so a killed run never reads as survived). A compile matched by
		the file's name alone while the project has another file of that name is warned about: it may be the other's.
		-restore: 0 restored and rebuilt (or nothing to restore), 5 not (or its journal could not be cleared).
		-status: 0 none left, 5 left.
		""";

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		return UnrealServices.GuardAsync(context, () => SummaryWriter.RecordingAsync(context, Services, "mutate", PeekName(arguments),
			() => RunCheckedAsync(context, arguments, cancellationToken)));
	}

	/// <summary>The -name= a check records its summary under, read before anything else is checked; null for -restore and -status, which record none.</summary>
	static string? PeekName(IReadOnlyList<string> arguments)
	{
		try
		{
			UakArguments Arguments = new(arguments);
			return Arguments.GetFlag("restore") || Arguments.GetFlag("status") ? null : Arguments.GetString("name");
		}
		catch (UakUsageException)
		{
			return null;
		}
	}

	async Task<int> RunCheckedAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		UakArguments Arguments = new(arguments);
		if (Arguments.GetFlag("restore"))
		{
			return await RestoreCommandAsync(context, Arguments, cancellationToken).ConfigureAwait(false);
		}
		if (Arguments.GetFlag("status"))
		{
			return Status(context, Arguments);
		}
		// The result file is this check's own, never the inner test's: an old one must never be read as this run's.
		string? ResultFile = Arguments.GetString("resultfile");
		if (ResultFile is not null && !StateFiles.TryDelete(Path.GetFullPath(ResultFile)))
		{
			throw new UakUsageException($"-resultfile={ResultFile} exists and can't be deleted");
		}
		MutateResult Result = new();
		try
		{
			Result.ExitCode = await MutateAsync(context, Arguments, Result, cancellationToken).ConfigureAwait(false);
			UnrealServices.WriteResultFile(ResultFile, Result);
			return Result.ExitCode;
		}
		catch (Exception Exception) when (Exception is not OutOfMemoryException)
		{
			bool Usage = Exception is UakUsageException;
			Result.Verdict ??= Usage ? "refused" : "stopped";
			Result.ExitCode = Usage ? UakExitCodes.UsageError : UakExitCodes.Failure;
			Result.Message = Exception is OperationCanceledException ? "cancelled" : Exception.Message;
			UnrealServices.WriteResultFile(ResultFile, Result);
			throw;
		}
	}

	async Task<int> MutateAsync(UakContext context, UakArguments arguments, MutateResult result, CancellationToken cancellationToken)
	{
		string PatchFile = Path.GetFullPath(arguments.GetRequiredString("patch"));
		int? MaxParallel = Services.MaxParallelActions(arguments);
		TestOptions Options = TestOptions.Parse(arguments, defaultName: null) with { RecordSummary = false, ResultFile = null };
		arguments.ThrowIfUnknown();
		arguments.ThrowIfMorePositionalThan(0);
		result.Name = Options.Name;
		result.Patch = PatchFile;
		if (Options.Cooked)
		{
			throw new UakUsageException("uak mutate runs editor tests only: a cooked run stages the game, which would keep the mutation after the files are put back");
		}
		if (Options.Quiet)
		{
			throw new UakUsageException("-quiet is for benchmarks (uak test -quiet), not mutation runs");
		}
		ExpectedTests? Expected = Options.ExpectFile is null ? null : ExpectedTests.Load(Options.ExpectFile, Options.Filter);
		if (!File.Exists(PatchFile))
		{
			throw new UakUsageException($"-patch file not found: {PatchFile}");
		}
		// Read once: the journal keeps these very bytes, for -accept to take the mutation back out of a file edited since.
		byte[] PatchBytes = File.ReadAllBytes(PatchFile);
		List<DiffFile> Files;
		try
		{
			Files = UnifiedDiff.Parse(UnifiedDiff.Decode(PatchBytes));
		}
		catch (PatchException Exception)
		{
			throw new UakUsageException($"-patch={PatchFile}: {Exception.Message}", Exception);
		}
		string ProjectDirectory = Path.GetDirectoryName(UnrealServices.RequireProject(context))!;
		BuildRequest Build = new() { Platform = Services.HostPlatform, MaxParallelActions = MaxParallel, Name = Options.Name, RecordSummary = false, CaptureTree = false };

		await using IAsyncDisposable Lock = await Services.Lock.AcquireAsync(context, $"uak mutate {Options.Name}", cancellationToken).ConfigureAwait(false);
		MutationJournal.ThrowIfUnfinished(context);
		VcsDetection Detection;
		try
		{
			Detection = await VersionControlDetector.DetectAsync(context, null, cancellationToken).ConfigureAwait(false);
		}
		catch (VcsException Exception)
		{
			throw new UakSetupException(Exception.Message, Exception);
		}
		using IVersionControl Vcs = Detection.VersionControl;
		string Root = Vcs.RootDirectory?.FullName ?? ProjectDirectory;
		List<string> Paths = UnifiedDiff.Resolve(Files, Root);
		result.Files = [.. Files.Select((File, Index) => new MutatedFile { Path = Paths[Index], Added = File.Added, Removed = File.Removed })];
		CheckFilesCanBeWritten(Files, Paths);
		// Everything is checked before anything is written: the patch must apply as a whole.
		string? GitPatch = null;
		string LfCopy = Path.Combine(MutationFolders.GetPendingRoot(context.StateDirectory), Options.Name + ".lf.diff");
		if (Vcs.Kind == VcsKind.Git)
		{
			GitPatch = await GitCheckAsync(Root, PatchFile, Files, LfCopy, cancellationToken).ConfigureAwait(false);
		}

		SummaryWriter Out = new(context.Logger);
		Out.Info($"mutate: {PatchFile} changes {Files.Count} file{(Files.Count == 1 ? "" : "s")} under {Root}: "
			+ string.Join(", ", Files.Select(File => $"{File.Path} (+{File.Added} -{File.Removed})")));
		MutationJournal Journal = MutationJournal.Begin(context, Options.Name, PatchFile, Paths, PatchBytes);
		Dictionary<string, byte[]?> Planned;
		try
		{
			// Planned from the copies just taken, so the plan is of exactly the bytes the journal can put back. Recorded before
			// anything is written: a run stopped while it writes can then tell its own bytes from a later edit. (For git apply,
			// uak's applier predicts git's bytes: each hunk applies at the line its header names, where git looks first; a file
			// that isn't UTF-8 text is left unpredicted, and is put back only by -force.)
			Planned = Plan(Journal, Files, byGit: GitPatch is not null);
			Journal.SetPlanned(Planned);
			Journal.ThrowIfChangedSinceBegin();
		}
		catch
		{
			// Nothing was written to the tree.
			try
			{
				Journal.Delete();
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
			{
				context.Logger.LogWarning("The journal {Directory} could not be cleared ({Message}); nothing was written to the tree: `uak mutate -restore -name={Name}` clears it.",
					Journal.Directory, Exception.Message, Options.Name);
			}
			StateFiles.TryDelete(LfCopy);
			throw;
		}

		bool BuildStarted = false;
		bool NoChange = false;
		BuildOutcome? MutantBuild = null;
		TestRunOutcome? Outcome = null;
		Exception? Stopped = null;
		List<string> Uncompiled = [];
		// The files this process surely never wrote: a restore leaves them alone, whatever they hold (another agent's edit).
		HashSet<string> Untouched = new(Paths, StringComparer.Ordinal);
		try
		{
			HashSet<string> EditedMeanwhile = new(StringComparer.Ordinal);
			if (GitPatch is not null)
			{
				// Each file still as it was copied, just before git writes them all. An edit can still get in before git reads a
				// file: what git leaves is checked against the plan right after.
				Journal.ThrowIfChangedSinceBegin();
				Untouched.Clear();
				foreach (string File in Paths)
				{
					MutationJournal.MakeWritable(File);
				}
				ProcessCapture Applied = await GitAsync(Root, GitPatch, cancellationToken).ConfigureAwait(false);
				// git refuses a patch that no longer applies, or a file gone or made (an edit got in before it read a file), before it
				// writes anything: a file that then holds neither its original nor the plan is that edit, which the restore leaves.
				bool Refused = Applied.ExitCode != 0 && GitRefused(Applied.StandardError);
				foreach (string Note in CheckGitWrite(Journal, Planned, EditedMeanwhile, Applied.ExitCode, Refused, Untouched))
				{
					Out.Error("mutate: " + Note);
					result.Problems.Add(Note);
				}
				if (Applied.ExitCode != 0)
				{
					throw new UakUsageException($"git apply failed (exit code {Applied.ExitCode}){(Refused ? ", and wrote nothing" : "")}: {Applied.StandardError.Trim()}");
				}
				if (EditedMeanwhile.Count > 0)
				{
					// Their bytes, the edit's and the mutation's, are recorded as no mutation's, so the restore leaves them.
					Journal.MarkApplied(EditedMeanwhile);
					throw new UakUsageException($"{string.Join(", ", EditedMeanwhile)} changed while git wrote the patch: no verdict");
				}
			}
			else
			{
				WritePlanned(Journal, Planned, Untouched);
			}
			// Only a whole write records the files' bytes as the mutation's: after a failed one, the journal stays in Applying,
			// where only the planned bytes count as this run's.
			List<string> Unchanged = Journal.MarkApplied();
			NoChange = Unchanged.Count == Paths.Count;
			if (!NoChange)
			{
				foreach (string File in Unchanged)
				{
					Out.Warning($"mutate: the patch leaves {File} as it was");
				}
				context.Logger.LogInformation("Applied the patch; building the mutant...");
				BuildStarted = true;
				MutantBuild = await new BuildCommand(Services).BuildAsync(context, Build, Lock, cancellationToken).ConfigureAwait(false);
				List<string> Ambiguous = [];
				Uncompiled = MutantBuild.Built ? FindUncompiled(MutantBuild, Files, Paths, Ambiguous, ProjectDirectory) : [];
				foreach (string Note in Ambiguous)
				{
					Out.Warning("mutate: " + Note);
					result.Problems.Add(Note);
				}
				if (MutantBuild.Built && Uncompiled.Count == 0)
				{
					Outcome = await new TestCommand(Services).RunInEditorAsync(context, Options, Expected, Lock, cancellationToken).ConfigureAwait(false);
					Out.Lines.AddRange(Outcome.Lines);
				}
			}
		}
		catch (Exception Exception) when (Exception is not OutOfMemoryException)
		{
			Stopped = Exception;
		}
		// Never cancelled: the files go back, and the binaries are rebuilt, even after Ctrl+C or a failure. (A second Ctrl+C
		// kills uak; the journal then stays, and uak mutate -restore finishes the job.)
		RestoreResult Restored = await RestoreAndRebuildAsync(context, [Journal], [], BuildStarted, Build, Lock, MutationRestoreMode.Normal, Untouched).ConfigureAwait(false);
		StateFiles.TryDelete(LfCopy);
		result.Tests = Outcome is null ? null : MutantTestRun.From(Outcome);

		int ExitCode;
		string Put = $"files put back{(BuildStarted ? " and rebuilt" : "")}";
		foreach (string Note in Restored.Notes)
		{
			// Where the journal was set aside, with bytes it saved (a git write it couldn't predict).
			Out.Warning("restore: " + Note);
		}
		foreach (string Folder in Restored.NotCleared)
		{
			// The files are back (and rebuilt): the verdict stands, but the journal is left whole, so others are refused until it goes.
			string Note = $"the files are back{(BuildStarted ? " and rebuilt" : "")}, but the journal could not be cleared ({Folder}): `uak test` refuses"
				+ $" until `uak mutate -restore -name={Options.Name}`, run on its own, clears it";
			Out.Warning("mutate: " + Note);
			result.Problems.Add(Note);
		}
		if (Restored.FileProblems.Count > 0 || Restored.RebuildFailed)
		{
			foreach (string Problem in Restored.FileProblems)
			{
				Out.Error("restore: " + Problem);
			}
			string Tested = Outcome is null ? "" : $" (the tests {(Outcome.Verdict is TestVerdict.Passed or TestVerdict.PassedWithUnexpected ? "passed" : "failed")} with it)";
			Out.Error(Restored.FileProblems.Count > 0
				? $"mutate: RESTORE FAILED: the files above don't hold their originals, so the tree may still hold the mutation{Tested}: look at them, then run"
					+ (Restored.StoppedWhileApplying
						? $" `uak mutate -restore -name={Options.Name} -force` (the run stopped while it wrote the patch, so they may hold part of it), which puts the"
							+ " originals back, saving what it overwrites"
						: $" `uak mutate -restore -name={Options.Name}` on its own"
							+ (Restored.BecameLink
								? ", once each file the lines above say became a link is a plain file again (remove the other hard link, or put the file back in place"
									+ " of the symbolic link): uak never writes a link. Any other file there was edited after the patch was applied"
								: ". They were edited after the patch was applied")
							+ ": an edit made while the check ran is made over the mutant, so it may keep the mutation. With -accept it merges such an edit with"
							+ " the original, three ways, and keeps the edit with the mutation taken out only where it is sure (the lines around each change of"
							+ " the patch, and one line each side, are in the file once, as they were: the edit changed, copied, moved or deleted none of them); it"
							+ " refuses any other file, saying why. With -force it puts the originals back over them, saving what it overwrites (make a refused edit again"
							+ " from there)")
				: $"mutate: RESTORE FAILED: the files are back, but the rebuild after failed or was stopped, so the binaries may still hold the mutation{Tested}:"
					+ $" run `uak mutate -restore -name={Options.Name}`, which rebuilds");
			result.Verdict = "restore failed";
			result.Problems.AddRange(Restored.FileProblems);
			ExitCode = RestoreFailedExitCode;
		}
		else if (Stopped is UakUsageException Usage)
		{
			// git apply failed after its check passed: the files are back, so it is the usage error it would have been.
			Out.Error($"mutate: STOPPED: {Usage.Message} ({Put})");
			Out.Record(context, Services, "mutate", Options.Name, UakExitCodes.UsageError);
			throw Usage;
		}
		else if (Stopped is not null)
		{
			string Why = Stopped is OperationCanceledException ? "cancelled" : Stopped.Message;
			Out.Error($"mutate: STOPPED ({Why}): no verdict ({Put})");
			result.Verdict = "stopped";
			result.Message = Why;
			ExitCode = UakExitCodes.Failure;
		}
		else if (NoChange)
		{
			Out.Error("mutate: REFUSED: the patch changes nothing (each file holds the same bytes after it as before), so it can't test anything");
			Out.Record(context, Services, "mutate", Options.Name, UakExitCodes.UsageError);
			throw new UakUsageException($"-patch={PatchFile} changes nothing: each file holds the same bytes after it as before");
		}
		else if (!MutantBuild!.Built)
		{
			List<CompileDiagnostic> Blamed = [.. MutantBuild.Errors.Where(Error => BlamesPatch(Error, Paths))];
			if (Blamed.Count > 0)
			{
				Out.Error($"mutate: NOT BUILT: the mutated code didn't build ({Blamed.Count} error{(Blamed.Count == 1 ? "" : "s")} in a patched file or one that includes it,"
					+ $" first: {Blamed[0]}), so no test ran ({Put})");
				result.Verdict = "not built";
				ExitCode = NotBuiltExitCode;
			}
			else
			{
				Out.Error($"mutate: INCONCLUSIVE: the build failed, but no error is in a patched file or one that includes it"
					+ $" ({(MutantBuild.Errors.Count == 0 ? "UBT reported no error" : $"first: {MutantBuild.Errors[0]}")}): another change, or a locked file, may have broken it ({Put})");
				result.Verdict = "inconclusive";
				ExitCode = InconclusiveExitCode;
			}
		}
		else if (Outcome is null)
		{
			Out.Error($"mutate: INCONCLUSIVE: the mutant build{(MutantBuild.UpToDate ? " was up to date and" : "")} shows no compile of {string.Join(", ", Uncompiled)}"
				+ " (a patched source file is compiled by its name or in its module's Module.<name>.cpp unity file, <name> being the ShortName its Build.cs"
				+ " sets, if any; a header needs at least one compile),"
				+ $" so the binaries may not hold the patch: the editor target may not build it; no test ran ({Put})");
			result.Verdict = "inconclusive";
			result.Problems = [.. Uncompiled.Select(File => $"no compile of {File}")];
			ExitCode = InconclusiveExitCode;
		}
		else if (Outcome.Verdict is TestVerdict.Passed or TestVerdict.PassedWithUnexpected)
		{
			Out.Error($"mutate: SURVIVED: the tests passed with the mutation, so they don't catch it ({Put})");
			result.Verdict = "survived";
			ExitCode = SurvivedExitCode;
		}
		else if (Outcome.Summary.Tests.Any(Test => Test.IsFailure) || Outcome.ListedFailed > 0)
		{
			// A test that completed with a failure. A crash, the editor's exit code, a skipped test, or a listed test that was
			// skipped or never completed may be the machine's doing, so on their own they are no verdict.
			Out.Info($"mutate: CAUGHT: the tests failed with the mutation ({string.Join("; ", Outcome.Problems)}; {Put})");
			result.Verdict = "caught";
			ExitCode = CaughtExitCode;
		}
		else
		{
			Out.Error($"mutate: INCONCLUSIVE: no test failed, but the run didn't pass ({string.Join("; ", Outcome.Problems)}): run it again, or read its log ({Put})");
			result.Verdict = "inconclusive";
			ExitCode = InconclusiveExitCode;
		}
		Out.Record(context, Services, "mutate", Options.Name, ExitCode);
		return ExitCode;
	}

	/// <summary>Whether a file is one a C++ build compiles (a source or a header).</summary>
	static bool IsSource(string path) => SourceExtensions.Contains(Path.GetExtension(path));

	/// <summary>
	/// The patched files the mutant build shows no sign of having compiled, so the binaries may not hold the patch to them: a
	/// source file (.cpp and the like) needs a compile named after it (adaptive unity compiles an edited file on its own,
	/// "Compile [x64] Foo.cpp") or of its module's unity file ("Module.Game.cpp", "Module.Game.2.cpp"; not the generated
	/// code's "Module.Game.gen.cpp"), its module being the folder above it with a *.Build.cs, whose ShortName, when it sets
	/// one, names the unity file instead (UEBuildModuleCPP: Rules.ShortName ?? Name). A header, or a source file the patch
	/// deletes, needs at least one compile. A file that isn't C++ (a config file, say) needs none. A source file counted as
	/// compiled by its name alone, while the project (<paramref name="projectDirectory"/>) holds another file of that name,
	/// adds a note to <paramref name="ambiguous"/>: UBT names the file it compiles by its name alone, so that compile may be
	/// of the other one.
	/// </summary>
	internal static List<string> FindUncompiled(BuildOutcome build, List<DiffFile> files, List<string> paths, List<string>? ambiguous = null, string? projectDirectory = null)
	{
		List<string> Compiled = [.. build.Compiled.Select(Item => Path.GetFileName(Item.Trim()))];
		List<string> Result = [];
		for (int Index = 0; Index < paths.Count; Index++)
		{
			string Patched = paths[Index];
			if (!IsSource(Patched))
			{
				continue;
			}
			if (HeaderExtensions.Contains(Path.GetExtension(Patched)) || files[Index].IsDeleted)
			{
				if (Compiled.Count == 0)
				{
					Result.Add(Patched);
				}
				continue;
			}
			string Name = Path.GetFileName(Patched);
			List<string> UnityNames = FindUnityNames(Patched);
			bool ByUnity = Compiled.Any(Item => UnityNames.Any(Unity => IsUnityFileOf(Item, Unity)));
			bool ByName = Compiled.Any(Item => Item.Equals(Name, StringComparison.OrdinalIgnoreCase));
			if (!ByUnity && !ByName)
			{
				Result.Add(Patched);
			}
			else if (!ByUnity && ambiguous is not null && projectDirectory is not null)
			{
				List<string> Others = FindSameNamed(projectDirectory, Patched);
				if (Others.Count > 0)
				{
					ambiguous.Add($"the only compile of {Patched} is one named {Name}, and the project has another file of that name ({string.Join(", ", Others)}):"
						+ " UBT names a file it compiles on its own by its name alone, so that compile may be of the other one, and the verdict assumes it wasn't");
				}
			}
		}
		return Result;
	}

	/// <summary>Whether a compiled item is a unity file of a module: Module.&lt;module&gt;.cpp or Module.&lt;module&gt;.&lt;n&gt;.cpp.</summary>
	static bool IsUnityFileOf(string item, string module)
	{
		string Prefix = "Module." + module + ".";
		if (!item.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string[] Rest = item[Prefix.Length..].Split('.');
		return Rest.Length switch
		{
			1 => Rest[0].Length > 0 && !Rest[0].All(char.IsAsciiDigit),
			2 => Rest[0].Length > 0 && Rest[0].All(char.IsAsciiDigit) && Rest[1].Length > 0,
			_ => false,
		};
	}

	/// <summary>
	/// The names a source file's module may give its unity files: the module's (the name of the first *.Build.cs in its folder
	/// or a folder above it) and the ShortName that Build.cs sets, if any; none when no Build.cs is found.
	/// </summary>
	static List<string> FindUnityNames(string path)
	{
		try
		{
			for (string? Folder = Path.GetDirectoryName(path); !string.IsNullOrEmpty(Folder); Folder = Path.GetDirectoryName(Folder))
			{
				if (!Directory.Exists(Folder))
				{
					continue;
				}
				string? BuildFile = Directory.EnumerateFiles(Folder, "*.Build.cs").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
				if (BuildFile is not null)
				{
					List<string> Names = [Path.GetFileName(BuildFile)[..^".Build.cs".Length]];
					Match ShortName = ShortNamePattern().Match(File.ReadAllText(BuildFile));
					if (ShortName.Success)
					{
						Names.Add(ShortName.Groups["name"].Value);
					}
					return Names;
				}
			}
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			// Unknown: only a compile named after the file counts.
		}
		return [];
	}

	// ModuleRules.ShortName, as a Build.cs sets it: ShortName = "Short";
	[GeneratedRegex(@"\bShortName\s*=\s*""(?<name>[^""]+)""")]
	private static partial Regex ShortNamePattern();

	/// <summary>
	/// The project's other source files (in Source and Plugins) with a patched file's name, at most three. Build output
	/// (Intermediate, Binaries, Saved, DerivedDataCache) is never walked into.
	/// <paramref name="entered"/> is called with each folder walked (for tests).
	/// </summary>
	internal static List<string> FindSameNamed(string projectDirectory, string patched, Action<string>? entered = null)
	{
		string[] Skipped = ["Intermediate", "Binaries", "Saved", "DerivedDataCache"];
		string Name = Path.GetFileName(patched);
		string Patched = Path.GetFullPath(patched);
		EnumerationOptions Options = new() { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
		List<string> Result = [];
		Stack<string> Folders = new(new[] { "Plugins", "Source" }.Select(Folder => Path.Combine(projectDirectory, Folder)).Where(Directory.Exists));
		while (Folders.Count > 0)
		{
			string Folder = Folders.Pop();
			entered?.Invoke(Folder);
			try
			{
				foreach (string Found in Directory.EnumerateFiles(Folder, Name, Options))
				{
					string Full = Path.GetFullPath(Found);
					if (!Full.Equals(Patched, StringComparison.OrdinalIgnoreCase))
					{
						Result.Add(Full);
						if (Result.Count == 3)
						{
							return Result;
						}
					}
				}
				foreach (string Below in Directory.EnumerateDirectories(Folder, "*", Options).Where(Below => !Skipped.Contains(Path.GetFileName(Below), StringComparer.OrdinalIgnoreCase)))
				{
					Folders.Push(Below);
				}
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
			{
				// What can't be read can't be compared.
			}
		}
		return Result;
	}

	/// <summary>
	/// Whether a build error surely comes from the patch: it is in a patched file (a relative path must match the patched
	/// file's last path parts), or its file directly includes a patched header (the include's last path part names it
	/// exactly). Anything else may be another change's doing (another agent's broken header, which every unit that includes
	/// it reports, the patched one too; a locked DLL; a linker error, which names no source file), so it is no verdict; and
	/// so is an error in a file that includes a patched header only through another header.
	/// </summary>
	internal static bool BlamesPatch(CompileDiagnostic error, List<string> paths)
	{
		bool Is(string? file)
		{
			if (string.IsNullOrWhiteSpace(file))
			{
				return false;
			}
			if (Path.IsPathFullyQualified(file))
			{
				string Full = Path.GetFullPath(file);
				return paths.Any(Patched => Patched.Equals(Full, StringComparison.OrdinalIgnoreCase));
			}
			string[] Parts = file.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
			if (Parts.Length == 0 || Parts.Contains("..", StringComparer.Ordinal) || Parts.Contains(".", StringComparer.Ordinal))
			{
				return false;
			}
			return paths.Any(Patched =>
			{
				string[] PatchedParts = Patched.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
				return PatchedParts.Length >= Parts.Length && PatchedParts[^Parts.Length..].SequenceEqual(Parts, StringComparer.OrdinalIgnoreCase);
			});
		}
		if (Is(error.File))
		{
			return true;
		}
		List<string> Headers = [.. paths.Where(Patched => HeaderExtensions.Contains(Path.GetExtension(Patched))).Select(Patched => Path.GetFileName(Patched))];
		if (Headers.Count == 0 || string.IsNullOrWhiteSpace(error.File) || !Path.IsPathFullyQualified(error.File) || !File.Exists(error.File))
		{
			return false;
		}
		try
		{
			return File.ReadLines(error.File).Select(IncludedName).Any(Included => Included is not null
				&& Headers.Any(Header => Header.Equals(Included, StringComparison.OrdinalIgnoreCase)));
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>The last path part of the file an #include line names ("Foo.h" for #include "Game/Foo.h" or &lt;Foo.h&gt;), or null.</summary>
	internal static string? IncludedName(string line)
	{
		string Text = line.TrimStart();
		if (!Text.StartsWith('#'))
		{
			return null;
		}
		Text = Text[1..].TrimStart();
		if (!Text.StartsWith("include", StringComparison.Ordinal))
		{
			return null;
		}
		Text = Text["include".Length..].TrimStart();
		if (Text.Length < 2 || (Text[0] != '"' && Text[0] != '<'))
		{
			return null;
		}
		int End = Text.IndexOf(Text[0] == '"' ? '"' : '>', 1);
		if (End <= 1)
		{
			return null;
		}
		string Included = Text[1..End];
		int Slash = Included.LastIndexOfAny(['/', '\\']);
		return Slash < 0 ? Included : Included[(Slash + 1)..];
	}

	/// <summary>
	/// Refuses a file the patch creates that exists, and a file it changes or deletes that is a link (a symbolic link, or one
	/// of several hard links to one file: <see cref="MutationJournal.DescribeLink"/>), which writing it whole, or deleting and
	/// putting it back, would turn into a file of its own. A file it changes may have uncommitted changes: the journal's copy
	/// keeps them, byte for byte, and puts them back.
	/// </summary>
	internal static void CheckFilesCanBeWritten(List<DiffFile> files, List<string> paths)
	{
		List<string> Problems = [];
		for (int Index = 0; Index < files.Count; Index++)
		{
			if (files[Index].IsNew && (File.Exists(paths[Index]) || Directory.Exists(paths[Index])))
			{
				Problems.Add($"{paths[Index]} exists, but the patch creates it");
			}
			else if (!files[Index].IsNew && MutationJournal.DescribeLink(paths[Index]) is string Link)
			{
				Problems.Add($"{paths[Index]} is {Link}, so uak mutate doesn't patch it");
			}
		}
		if (Problems.Count > 0)
		{
			throw new UakUsageException("refused: " + string.Join("; ", Problems));
		}
	}

	/// <summary>
	/// The new bytes of every file (null: delete it), computed from the journal's copies before anything is written. A file
	/// the patch doesn't apply to exactly (each hunk at the line its header names) refuses the run, whoever applies it: git
	/// would look for a moved hunk elsewhere, so its bytes could differ from the plan, and -accept would merge from the wrong
	/// mutant. Only when git applies the patch (<paramref name="byGit"/>) is a file that isn't UTF-8 text, which uak's
	/// applier can't read and git can, left out: its bytes aren't predicted (only -force puts it back, and -accept refuses it).
	/// </summary>
	static Dictionary<string, byte[]?> Plan(MutationJournal journal, List<DiffFile> files, bool byGit)
	{
		Dictionary<string, byte[]?> Result = new(StringComparer.Ordinal);
		for (int Index = 0; Index < files.Count; Index++)
		{
			MutationFile Entry = journal.Record.Files[Index];
			byte[]? Original = journal.ReadOriginal(Entry);
			try
			{
				Result[Entry.Path] = UnifiedDiff.Apply(files[Index], Original);
			}
			catch (PatchException) when (byGit && Original is not null && !UnifiedDiff.IsUtf8Text(Original))
			{
				// Left unpredicted.
			}
			catch (PatchException Exception)
			{
				throw new UakUsageException("the patch doesn't apply cleanly: " + Exception.Message, Exception);
			}
		}
		return Result;
	}

	/// <summary>
	/// Writes the planned bytes, file by file, each just after checking that the file still holds the bytes the journal
	/// copied (<see cref="MutationJournal.ThrowIfChangedSinceBegin(MutationFile)"/>), and takes each file out of
	/// <paramref name="untouched"/> before it is written. A file changed meanwhile stops the write there: it and the files
	/// after it are never written. A write that fails is put right before it throws (<see cref="PutBackOwnWrite"/>): a file
	/// that holds the run's own bytes gets its original back, and one that holds any other bytes goes back into
	/// <paramref name="untouched"/>. <paramref name="write"/> writes a file (for tests; <see cref="MutationJournal.WriteFile"/>,
	/// which writes it whole).
	/// </summary>
	internal static void WritePlanned(MutationJournal journal, Dictionary<string, byte[]?> planned, HashSet<string> untouched, Action<string, byte[]>? write = null)
	{
		write ??= MutationJournal.WriteFile;
		for (int Index = 0; Index < journal.Record.Files.Count; Index++)
		{
			MutationFile Entry = journal.Record.Files[Index];
			MutationJournal.ThrowIfChangedSinceBegin(Entry);
			untouched.Remove(Entry.Path);
			MutationJournal.MakeWritable(Entry.Path);
			byte[]? Bytes = planned[Entry.Path];
			try
			{
				if (Bytes is null)
				{
					File.Delete(Entry.Path);
				}
				else
				{
					Directory.CreateDirectory(Path.GetDirectoryName(Entry.Path)!);
					write(Entry.Path, Bytes);
				}
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
			{
				if (PutBackOwnWrite(journal, Index, Entry, Bytes) == WriteOwner.Other)
				{
					untouched.Add(Entry.Path);
				}
				throw;
			}
		}
	}

	/// <summary>Whose bytes a file holds after a write of the patch to it failed.</summary>
	internal enum WriteOwner
	{
		/// <summary>Its original: nothing to put back.</summary>
		Original,

		/// <summary>The run's own: the bytes it planned for it, or none for a delete.</summary>
		Run,

		/// <summary>Anyone else's: the run never wrote them.</summary>
		Other,

		/// <summary>Unknown: it can't be read.</summary>
		Unknown,
	}

	/// <summary>
	/// Puts right a file whose write by this run failed, by what it holds now: the run's own bytes (exactly its planned bytes,
	/// as a whole write leaves them when only keeping its attributes failed after; or no file, for a delete) are saved beside
	/// the journal and the original goes back from the journal's copy (checked against its hash), or, for a file the patch was
	/// creating, it is deleted. A file that holds its original, or any other bytes (anyone else's: <see cref="MutationJournal.WriteFile"/>
	/// never leaves part of a write), is left as it is. When that fails, the file stays as it is, and the restore after says
	/// it may hold part of the patch. Returns whose bytes it held.
	/// </summary>
	internal static WriteOwner PutBackOwnWrite(MutationJournal journal, int index, MutationFile entry, byte[]? planned)
	{
		byte[]? Now;
		try
		{
			Now = File.Exists(entry.Path) ? File.ReadAllBytes(entry.Path) : null;
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			return WriteOwner.Unknown;
		}
		if ((Now is null ? null : MutationJournal.Hash(Now)) == entry.OriginalHash)
		{
			return WriteOwner.Original;
		}
		bool Own = planned is null ? Now is null : Now is not null && Now.AsSpan().SequenceEqual(planned);
		if (!Own)
		{
			return WriteOwner.Other;
		}
		try
		{
			byte[]? Original = journal.ReadOriginal(entry);
			if (Now is { Length: > 0 })
			{
				journal.SaveCurrent(index, Now);
			}
			if (Original is not null)
			{
				MutationJournal.WriteFile(entry.Path, Original);
			}
			else if (Now is not null)
			{
				File.Delete(entry.Path);
			}
		}
		catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
		{
			// The restore after finds it as it is, and says it may hold part of the patch.
		}
		return WriteOwner.Run;
	}

	/// <summary>
	/// After git applied the patch (exit code <paramref name="gitExitCode"/>), finds each file that holds neither its original
	/// bytes nor the ones uak planned for it (line ends aside: git may write them otherwise, under core.autocrlf). When git
	/// refused the patch (<paramref name="gitRefused"/>: its check found a hunk that no longer applies, a file to change that is
	/// gone or one to create that exists, and then writes nothing: <see cref="GitRefused"/>) and no
	/// file holds its planned bytes, such a file is an edit that got in before git read it: it is added to
	/// <paramref name="untouched"/>, so the restore leaves it, as git never wrote it. Otherwise its bytes are saved beside the
	/// journal; a file whose bytes uak predicted is added to <paramref name="edited"/> (an edit got in between the check before
	/// git and git's write, or git failed while it wrote), and one uak couldn't predict can't be told apart. Returns a note for each.
	/// </summary>
	internal static List<string> CheckGitWrite(MutationJournal journal, Dictionary<string, byte[]?> planned, HashSet<string> edited, int gitExitCode,
		bool gitRefused = false, HashSet<string>? untouched = null)
	{
		List<(int Index, MutationFile Entry, byte[]? Now, bool Predicted)> Neither = [];
		bool AnyPlanned = false;
		for (int Index = 0; Index < journal.Record.Files.Count; Index++)
		{
			MutationFile Entry = journal.Record.Files[Index];
			byte[]? Now = File.Exists(Entry.Path) ? File.ReadAllBytes(Entry.Path) : null;
			string? Hash = Now is null ? null : MutationJournal.Hash(Now);
			if (Hash == Entry.OriginalHash)
			{
				continue;
			}
			bool Predicted = planned.TryGetValue(Entry.Path, out byte[]? Plan);
			if (Predicted && (Plan is null ? Now is null : Now is not null && SameButLineEnds(Now, Plan)))
			{
				AnyPlanned = true;
				continue;
			}
			Neither.Add((Index, Entry, Now, Predicted));
		}
		List<string> Notes = [];
		bool WroteNothing = gitExitCode != 0 && gitRefused && !AnyPlanned && untouched is not null;
		foreach ((int Index, MutationFile Entry, byte[]? Now, bool Predicted) in Neither)
		{
			if (WroteNothing)
			{
				untouched!.Add(Entry.Path);
				Notes.Add($"{Entry.Path} was changed after uak's last look, before git read it, so git refused the patch and wrote nothing: the change"
					+ " (someone else's) is left as it is");
				continue;
			}
			string Saved = Now is null ? "it is gone" : "its bytes are saved in " + journal.SaveCurrent(Index, Now);
			if (!Predicted)
			{
				Notes.Add($"uak couldn't predict git's bytes for {Entry.Path}, so it can't tell an edit made while git wrote the patch from the patch: {Saved}");
			}
			else
			{
				edited.Add(Entry.Path);
				Notes.Add(gitExitCode == 0
					? $"{Entry.Path} holds neither its original nor the bytes the patch gives it: an edit got in while git wrote the patch, and is left in place"
						+ $" with the mutation ({Saved})"
					: $"git apply failed, and {Entry.Path} holds neither its original nor the bytes the patch gives it: an edit made meanwhile, or part of"
						+ $" git's write ({Saved})");
			}
		}
		return Notes;
	}

	/// <summary>
	/// Whether git apply's error output says it refused the patch before writing anything, as git, run with LC_ALL=C
	/// (<see cref="GitAsync"/>), words it: every error line is one git's check of the patch gives (git checks every hunk of
	/// every file before it writes one), and there is one: a hunk that doesn't apply ("error: patch failed: &lt;file&gt;:&lt;line&gt;",
	/// "error: &lt;file&gt;: patch does not apply"), a file to change that is gone ("error: &lt;file&gt;: No such file or directory"),
	/// or a file to create that exists ("error: &lt;file&gt;: already exists in working directory"). Any other error (a failed
	/// write names the file in quotes: "error: unable to write file '&lt;file&gt;' ...: No such file or directory") may come
	/// after git wrote something, so it is no refusal.
	/// </summary>
	internal static bool GitRefused(string standardError)
	{
		ArgumentNullException.ThrowIfNull(standardError);
		List<string> Errors = [.. standardError.Split('\n').Select(Line => Line.TrimEnd('\r'))
			.Where(Line => Line.StartsWith("error:", StringComparison.Ordinal) || Line.StartsWith("fatal:", StringComparison.Ordinal))];
		return Errors.Count > 0 && Errors.All(Line => GitCheckError().IsMatch(Line));
	}

	[GeneratedRegex(@"^error: (patch failed: .+:\d+|[^']+: (patch does not apply|No such file or directory|already exists in working directory))$")]
	private static partial Regex GitCheckError();

	/// <summary>Whether two files' bytes are the same once each CR before a LF is dropped.</summary>
	static bool SameButLineEnds(byte[] a, byte[] b)
	{
		static List<byte> Lf(byte[] bytes)
		{
			List<byte> Result = new(bytes.Length);
			for (int Index = 0; Index < bytes.Length; Index++)
			{
				if (bytes[Index] != '\r' || Index + 1 >= bytes.Length || bytes[Index + 1] != '\n')
				{
					Result.Add(bytes[Index]);
				}
			}
			return Result;
		}
		return a.SequenceEqual(b) || Lf(a).SequenceEqual(Lf(b));
	}

	Task<ProcessCapture> GitAsync(string root, string patchFile, CancellationToken cancellationToken, params string[] arguments)
	{
		string Git = ExecutableLocator.Resolve("git") ?? throw new UakSetupException("git was not found on PATH: uak mutate applies a patch in a Git workspace with git apply");
		// In the C locale, so its messages are the English ones GitRefused reads.
		Dictionary<string, string?> Locale = new() { ["LC_ALL"] = "C", ["LANGUAGE"] = null };
		return Services.Capture(new ProcessInvocation(Git, ["-C", root, "-c", "core.quotepath=false", "apply", "--whitespace=nowarn", .. arguments, patchFile], null, Locale),
			cancellationToken);
	}

	/// <summary>
	/// Checks the patch with <c>git apply</c> in the work tree: that git reads the same files from it as uak did, with the
	/// same added and removed lines (so the journal covers every file git writes, and both read every hunk alike), and that
	/// it applies cleanly. Git takes a '\r' before a line end as content, so a patch saved with CRLF line ends that doesn't
	/// apply as it is is tried again with LF ones (<paramref name="lfCopy"/>), which fits a CRLF work tree under
	/// core.autocrlf. Returns the patch file that applies.
	/// </summary>
	async Task<string> GitCheckAsync(string root, string patchFile, List<DiffFile> files, string lfCopy, CancellationToken cancellationToken)
	{
		ProcessCapture NumStat = await GitAsync(root, patchFile, cancellationToken, "--numstat").ConfigureAwait(false);
		if (NumStat.ExitCode != 0)
		{
			throw new UakUsageException($"git apply can't read the patch (exit code {NumStat.ExitCode}): {NumStat.StandardError.Trim()}");
		}
		List<string> GitCounts = [.. NumStat.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(Line => Line.Split('\t')).Where(Parts => Parts.Length == 3).Select(Parts => $"{Parts[2]} +{Parts[0]} -{Parts[1]}").Order(StringComparer.Ordinal)];
		List<string> UakCounts = [.. files.Select(File => $"{File.Path} +{File.Added} -{File.Removed}").Order(StringComparer.Ordinal)];
		if (!GitCounts.SequenceEqual(UakCounts, StringComparer.Ordinal))
		{
			throw new UakUsageException($"git reads the patch otherwise than uak does (git: {string.Join(", ", GitCounts)}; uak: {string.Join(", ", UakCounts)}):"
				+ " give a plain patch of text files whose hunk headers count their lines");
		}
		ProcessCapture Checked = await GitAsync(root, patchFile, cancellationToken, "--check", "-v").ConfigureAwait(false);
		if (Checked.ExitCode == 0)
		{
			ThrowIfGitMovesHunks(Checked);
			return patchFile;
		}
		string Text = File.ReadAllText(patchFile);
		if (Text.Contains("\r\n", StringComparison.Ordinal))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(lfCopy)!);
			File.WriteAllText(lfCopy, Text.Replace("\r\n", "\n", StringComparison.Ordinal), UakJson.Utf8NoBom);
			ProcessCapture LfChecked = await GitAsync(root, lfCopy, cancellationToken, "--check", "-v").ConfigureAwait(false);
			if (LfChecked.ExitCode == 0)
			{
				try
				{
					ThrowIfGitMovesHunks(LfChecked);
				}
				catch (UakUsageException)
				{
					File.Delete(lfCopy);
					throw;
				}
				return lfCopy;
			}
			File.Delete(lfCopy);
		}
		throw new UakUsageException($"the patch doesn't apply cleanly (git apply --check): {Checked.StandardError.Trim()}");
	}

	/// <summary>
	/// Refuses a patch git would apply a hunk of elsewhere than the line its header names, as git apply -v says when it checks
	/// it ("Hunk #2 succeeded at 40 (offset 3 lines).", in the C locale): its search for a moved hunk, its reading of a hunk
	/// without context after its changes as the file's end, or its comparing a line's '\r' (core.autocrlf=false) where uak's
	/// applier doesn't. uak's plan, which the journal records and -accept merges from, puts each hunk at its header's line.
	/// </summary>
	static void ThrowIfGitMovesHunks(ProcessCapture checkedVerbose)
	{
		List<string> Moved = GitMovedHunks(checkedVerbose.StandardError + "\n" + checkedVerbose.StandardOutput);
		if (Moved.Count > 0)
		{
			throw new UakUsageException("git apply would apply the patch elsewhere than its hunk headers say, so its bytes wouldn't be the ones uak plans and"
				+ $" checks: {string.Join(" ", Moved)} Make the patch again against the files as they are, with context lines (git diff -U3), and the"
				+ " files' own line ends");
		}
	}

	/// <summary>The lines of git apply -v's output that say it applies a hunk elsewhere than its header's line, or reduced its context.</summary>
	internal static List<string> GitMovedHunks(string verboseOutput)
	{
		ArgumentNullException.ThrowIfNull(verboseOutput);
		return [.. verboseOutput.Split('\n').Select(Line => Line.Trim())
			.Where(Line => GitMovedHunk().IsMatch(Line) || Line.StartsWith("Context reduced to", StringComparison.Ordinal))];
	}

	[GeneratedRegex(@"^Hunk #\d+ succeeded at \d+")]
	private static partial Regex GitMovedHunk();

	/// <summary>What <see cref="RestoreAndRebuildAsync"/> did.</summary>
	sealed class RestoreResult
	{
		/// <summary>Files that could not be put back, and journals that could not be read or cleared.</summary>
		public List<string> FileProblems { get; } = [];

		/// <summary>The files are back, but the rebuild after failed or could not run.</summary>
		public bool RebuildFailed { get; set; }

		/// <summary>What was saved or kept (-force, -accept), and where journals were set aside.</summary>
		public List<string> Notes { get; } = [];

		/// <summary>A file problem is a file of a run that stopped while it wrote the patch: -accept can't keep it, -force puts it back.</summary>
		public bool StoppedWhileApplying { get; set; }

		/// <summary>A file problem is a file that became a link after the patch was applied, which uak never writes.</summary>
		public bool BecameLink { get; set; }

		/// <summary>
		/// Journals whose files are back (and rebuilt) but whose folder could not be cleared (a reader held a file in it): each
		/// stays whole, in phase Restored, until a -restore clears it.
		/// </summary>
		public List<string> NotCleared { get; } = [];
	}

	/// <summary>
	/// Puts back the journals' files (as <paramref name="mode"/> says; -accept checks a file it would keep with
	/// <see cref="MutationUndo"/>) and rebuilds (when <paramref name="rebuild"/>), never cancelled and never throwing: returns
	/// what went wrong. A journal is cleared only once its files are back and the rebuild succeeded: deleted in
	/// <see cref="MutationRestoreMode.Normal"/> (unless it holds bytes saved beside it), else set aside with what it saved, as are
	/// the <paramref name="unreadable"/> folders (only given with <see cref="MutationRestoreMode.Accept"/>); one that can't be
	/// cleared stays whole (<see cref="RestoreResult.NotCleared"/>). The files in <paramref name="untouched"/> are ones this
	/// process knows it never wrote: they are left alone.
	/// </summary>
	async Task<RestoreResult> RestoreAndRebuildAsync(UakContext context, List<MutationJournal> journals, List<UnreadableMutationJournal> unreadable, bool rebuild,
		BuildRequest build, IAsyncDisposable heldLock, MutationRestoreMode mode, IReadOnlySet<string>? untouched = null)
	{
		RestoreResult Result = new();
		foreach (MutationJournal Journal in journals)
		{
			try
			{
				MutationRestore Restore = Journal.Restore(mode, untouched, mode == MutationRestoreMode.Accept ? MutationUndo.For(Journal) : null);
				Result.FileProblems.AddRange(Restore.Conflicts);
				Result.Notes.AddRange(Restore.Saved);
				Result.StoppedWhileApplying |= Restore.StoppedWhileApplying;
				Result.BecameLink |= Restore.BecameLink;
				if (Restore.Conflicts.Count == 0)
				{
					context.Logger.LogInformation("Put back the {Count} file(s) of '{Name}'.", Journal.Record.Files.Count, Journal.Record.Name);
				}
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
			{
				Result.FileProblems.Add($"could not put back the files of '{Journal.Record.Name}': {Exception.Message}");
			}
		}
		if (Result.FileProblems.Count > 0)
		{
			// The files still differ: a rebuild now would build them as they are.
			return Result;
		}
		if (rebuild)
		{
			context.Logger.LogInformation("Rebuilding without the mutation...");
			try
			{
				Result.RebuildFailed = !(await new BuildCommand(Services).BuildAsync(context, build, heldLock, CancellationToken.None).ConfigureAwait(false)).Built;
			}
			catch (Exception Exception) when (Exception is not OutOfMemoryException)
			{
				Result.RebuildFailed = true;
				context.Logger.LogError("The rebuild could not run: {Message}", Exception.Message);
			}
			if (Result.RebuildFailed)
			{
				return Result;
			}
		}
		List<string> Folders = [.. journals.Select(Journal => Journal.Directory), .. unreadable.Select(Folder => Folder.Directory)];
		foreach (string Folder in Folders)
		{
			try
			{
				// Bytes saved beside the journal (<n>.<time>.current) are kept, even by a plain -restore that only rebuilds.
				if (mode == MutationRestoreMode.Normal && !Directory.EnumerateFiles(Folder, "*.current").Any())
				{
					MutationJournal.DeleteFolder(Folder);
				}
				else
				{
					Result.Notes.Add($"set the journal {Folder} aside in {MutationJournal.SetAside(context, Folder)}");
				}
			}
			catch (Exception Exception) when (Exception is IOException or UnauthorizedAccessException)
			{
				Result.NotCleared.Add($"{Folder}: {Exception.Message}");
			}
		}
		return Result;
	}

	async Task<int> RestoreCommandAsync(UakContext context, UakArguments arguments, CancellationToken cancellationToken)
	{
		string? Name = arguments.GetString("name");
		bool Force = arguments.GetFlag("force");
		bool Accept = arguments.GetFlag("accept");
		int? MaxParallel = Services.MaxParallelActions(arguments);
		arguments.ThrowIfUnknown();
		arguments.ThrowIfMorePositionalThan(0);
		if (Force && Accept)
		{
			throw new UakUsageException("-force puts the originals back and -accept keeps the files as they are: give one");
		}
		if (Name is not null)
		{
			RunSummaries.CheckName(Name);
		}
		if ((Force || Accept) && Name is null)
		{
			throw new UakUsageException($"-{(Force ? "force" : "accept")} needs -name=<name>: it overrides the journal's checks, so one journal at a time");
		}
		MutationRestoreMode Mode = Force ? MutationRestoreMode.Force : Accept ? MutationRestoreMode.Accept : MutationRestoreMode.Normal;
		UnrealServices.RequireProject(context);
		BuildRequest Build = new() { Platform = Services.HostPlatform, MaxParallelActions = MaxParallel, Name = Name ?? "uak-mutate-restore", RecordSummary = false, CaptureTree = false };

		// Taking the lock waits for an orphaned build of the killed check (EditorLock.WaitForOrphanedCommandAsync), up to 30 s.
		// TODO(unix): without PDEATHSIG, a killed check's UBT can outlive that on Linux and Mac, and this may rebuild while it
		// still writes; recording the build's process in the journal and waiting for it here would close that.
		await using IAsyncDisposable Lock = await Services.Lock.AcquireAsync(context, "uak mutate -restore", cancellationToken).ConfigureAwait(false);
		// Journals a killed run was writing or deleting: they hold no patch in the tree (the lock keeps out a live run's).
		MutationJournal.CleanPending(context);
		// Under the lock, a journal is a stopped run's unless its run is the holder this process joined: a live run holds the lock.
		LockHolderInfo? Holder = EditorLock.GetInheritedHolder(context);
		(List<MutationJournal> All, List<UnreadableMutationJournal> AllUnreadable) = MutationJournal.List(context);
		bool Named(string Of) => Name is null || Of.Equals(Name, StringComparison.OrdinalIgnoreCase);
		List<MutationJournal> Journals = [.. All.Where(Journal => Named(Journal.Record.Name) && !MutationJournal.IsRunOf(Journal.Record, Holder))];
		List<UnreadableMutationJournal> Unreadable = [.. AllUnreadable.Where(Folder => Named(Folder.Name))];
		if (Journals.Count == 0 && Unreadable.Count == 0)
		{
			context.Logger.LogInformation("mutate: nothing to restore{Which} in {Root}.", Name is null ? "" : $" named '{Name}'", MutationJournal.GetRoot(context));
			return UakExitCodes.Success;
		}
		List<string> Problems = [];
		if (Mode != MutationRestoreMode.Accept)
		{
			Problems.AddRange(Unreadable.Select(MutationJournal.DescribeUnreadable));
			Unreadable = [];
		}
		RestoreResult Restored = await RestoreAndRebuildAsync(context, Journals, Unreadable, rebuild: Journals.Count > 0 || Unreadable.Count > 0, Build, Lock, Mode).ConfigureAwait(false);
		Problems.AddRange(Restored.FileProblems);
		foreach (string Note in Restored.Notes)
		{
			context.Logger.LogWarning("restore: {Note}", Note);
		}
		foreach (string Problem in Problems)
		{
			context.Logger.LogError("restore: {Problem}", Problem);
		}
		if (Restored.FileProblems.Count > 0)
		{
			context.Logger.LogError(Restored.StoppedWhileApplying
				? "mutate: RESTORE FAILED: the run stopped while it wrote the patch, so the files above may hold part of it. Look at them; then -force (with -name=)"
					+ " puts the originals back over them, saving what it overwrites beside the journal"
				: "mutate: RESTORE FAILED: the files above don't hold their originals: "
					+ (Restored.BecameLink
						? "a file the lines above say became a link is left as it is, as uak never writes a link: remove the other hard link (or put the file"
							+ " back in place of the symbolic link), then run -restore again. Any other file there was edited after the patch was applied"
						: "they were edited after the patch was applied")
					+ ", and an edit made over the mutant may keep the mutation. Look at them; then -accept merges such an edit with the original, three"
					+ " ways, and keeps the edit with the mutation taken out only where it is sure (the lines around each change of the patch, and one line"
					+ " each side, are in the file once, as they were: the edit changed, copied, moved or deleted none of them); it refuses any other file (the line"
					+ " above says why). -force puts the originals back over them, saving what it overwrites beside the journal, so a refused edit can be"
					+ " made again on the original from there (both with -name=)");
			return RestoreFailedExitCode;
		}
		if (Restored.RebuildFailed)
		{
			context.Logger.LogError("mutate: RESTORE FAILED: the files are back, but the rebuild failed, so the binaries may still hold the mutation: fix the build and run -restore again");
			return RestoreFailedExitCode;
		}
		if (Restored.NotCleared.Count > 0)
		{
			foreach (string Folder in Restored.NotCleared)
			{
				context.Logger.LogError("restore: the journal could not be cleared: {Folder}", Folder);
			}
			context.Logger.LogError("mutate: RESTORE FAILED: the files are back and rebuilt, but a reader held a file in the journal's folder, so it could not be"
				+ " cleared and stays whole: run -restore again, which clears it");
			return RestoreFailedExitCode;
		}
		string Names = string.Join(", ", Journals.Select(Journal => Journal.Record.Name).Concat(Unreadable.Select(Folder => Folder.Name)));
		if (Problems.Count > 0)
		{
			context.Logger.LogError("mutate: RESTORE FAILED: {Count} journal(s) can't be read (above){Done}", Problems.Count, Names.Length == 0 ? "" : $"; restored {Names}");
			return RestoreFailedExitCode;
		}
		context.Logger.LogInformation("mutate: restored {Names}: {What}, rebuilt, journal {Cleared}.", Names,
			Mode == MutationRestoreMode.Accept ? "later edits kept, the rest put back" : "files put back", Mode == MutationRestoreMode.Normal ? "cleared" : "set aside");
		return UakExitCodes.Success;
	}

	/// <summary>
	/// <c>uak mutate -status</c>: lists the journals left, live or stopped, without the lock (a live run holds it), for an agent
	/// about to commit, shelve or preflight the tree. 0 when none is left; <see cref="RestoreFailedExitCode"/> when one is (or a
	/// folder can't be read): the tree or its binaries may hold a mutation.
	/// </summary>
	int Status(UakContext context, UakArguments arguments)
	{
		arguments.ThrowIfUnknown();
		arguments.ThrowIfMorePositionalThan(0);
		(List<MutationJournal> Journals, List<UnreadableMutationJournal> Unreadable) = MutationJournal.ListSettled(context, MutationJournal.DefaultSettleTime);
		if (Journals.Count == 0 && Unreadable.Count == 0)
		{
			context.Logger.LogInformation("mutate: no journal is left in {Root}: no mutation run has its patch in the tree.", MutationJournal.GetRoot(context));
			return UakExitCodes.Success;
		}
		foreach (MutationJournal Journal in Journals)
		{
			MutationRecord Record = Journal.Record;
			bool Live = new ProcessIdentity(Record.Pid, Record.ProcessStart).IsSurelyAlive();
			string Where = Record.Phase switch
			{
				MutationPhase.Applying => $"was writing its patch to {string.Join(", ", Record.Files.Select(File => File.Path))}",
				MutationPhase.Applied => $"has its patch in {string.Join(", ", Record.Files.Select(File => File.Path))}",
				_ => "has put its files back, but its journal was not cleared (not rebuilt, or a reader held it open): the binaries may hold its patch",
			};
			context.Logger.LogError("mutate: '{Name}' (PID {Pid}, {Live}) {Where}; patch {Patch}{Next}.", Record.Name, Record.Pid,
				Live ? "running" : "stopped", Where, Record.Patch,
				Live ? ": it puts the files back and rebuilds when it ends" : $": run `uak mutate -restore -name={Record.Name}` on its own");
		}
		foreach (UnreadableMutationJournal Folder in Unreadable)
		{
			context.Logger.LogError("mutate: {Problem}", MutationJournal.DescribeUnreadable(Folder));
		}
		context.Logger.LogError("mutate: {Count} journal(s) left: the tree or its binaries may hold a mutation, so don't commit, shelve or preflight it until none is"
			+ " (wait for a running one; restore a stopped one).", Journals.Count + Unreadable.Count);
		return RestoreFailedExitCode;
	}
}

/// <summary>What <c>uak mutate -resultfile</c> writes: the check's own result, at every exit.</summary>
internal sealed class MutateResult
{
	public string Command { get; set; } = "mutate";

	public string? Name { get; set; }

	public string? Patch { get; set; }

	/// <summary>"caught", "survived", "not built", "inconclusive", "restore failed", "stopped" or "refused".</summary>
	public string? Verdict { get; set; }

	public int ExitCode { get; set; }

	public List<MutatedFile> Files { get; set; } = [];

	public MutantTestRun? Tests { get; set; }

	public List<string> Problems { get; set; } = [];

	/// <summary>Why it was refused or stopped.</summary>
	public string? Message { get; set; }
}

/// <summary>A file the patch changes, and its '+' and '-' lines.</summary>
internal sealed class MutatedFile
{
	public string Path { get; set; } = "";

	public int Added { get; set; }

	public int Removed { get; set; }
}

/// <summary>The mutant's test run, in <see cref="MutateResult"/>.</summary>
internal sealed class MutantTestRun
{
	public string Outcome { get; set; } = "";

	public int? Found { get; set; }

	public int Passed { get; set; }

	public int Failed { get; set; }

	public int Skipped { get; set; }

	public int ListedFailed { get; set; }

	public List<string> Problems { get; set; } = [];

	public static MutantTestRun From(TestRunOutcome outcome) => new()
	{
		Outcome = outcome.ResultOutcome,
		Found = outcome.Summary.FoundCount,
		Passed = outcome.Summary.Passed,
		Failed = outcome.Summary.Failed,
		Skipped = outcome.Summary.Skipped,
		ListedFailed = outcome.ListedFailed,
		Problems = outcome.Problems,
	};
}
