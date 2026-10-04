// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text.Json;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Vcs;

/// <summary>
/// Shared plumbing for the <c>vcs</c> commands that change a Perforce workspace (shelve, edit, add, reopen, change): parsing,
/// detection, the Perforce-only check, error handling and output. None of them reverts or submits anything.
/// </summary>
public abstract class PerforceCommandBase : IUakCommand
{
	/// <inheritdoc/>
	public abstract string Name { get; }

	/// <inheritdoc/>
	public abstract string Summary { get; }

	/// <inheritdoc/>
	public abstract string Usage { get; }

	/// <inheritdoc/>
	public bool RequiresEngine => false;

	/// <summary>Where results go: standard output unless a test replaces it. Errors go to the context's logger.</summary>
	internal TextWriter Output { get; set; } = Console.Out;

	/// <summary>Where notices go under -json (such as a changelist uak created), so they are seen without breaking the JSON.</summary>
	internal TextWriter ErrorOutput { get; set; } = Console.Error;

	/// <summary>Finds the workspace; tests replace it to hand in a fake Perforce connection.</summary>
	internal Func<UakContext, string?, CancellationToken, Task<VcsDetection>>? Detect { get; set; }

	/// <summary>Whether -json was given.</summary>
	internal bool Json { get; private set; }

	/// <summary>The context of the current run.</summary>
	internal UakContext Context { get; private set; } = null!;

	/// <inheritdoc/>
	public async Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		Context = context;
		UakArguments parsed = new(arguments);
		string? kind = parsed.GetString("vcs");
		Json = parsed.GetFlag("json");
		Func<PerforceVersionControl, CancellationToken, Task<int>> run = Parse(parsed);
		parsed.ThrowIfUnknown();

		VcsDetection detection;
		try
		{
			detection = await (Detect ?? VersionControlDetector.DetectAsync)(context, kind, cancellationToken).ConfigureAwait(false);
		}
		catch (VcsException exception)
		{
			throw new UakUsageException(exception.Message, exception);
		}

		using IVersionControl vcs = detection.VersionControl;
		if (vcs is not PerforceVersionControl p4)
		{
			string found = vcs.Kind == VcsKind.None ? $"no Perforce workspace was found ({detection.Provenance})" : $"this workspace uses {vcs.Kind} (found by {detection.Provenance})";
			throw new UakUsageException($"uak {Name} works with Perforce only: {found}.");
		}
		try
		{
			return await run(p4, cancellationToken).ConfigureAwait(false);
		}
		catch (VcsException exception)
		{
			context.Logger.LogError("{Message}", exception.Message);
			return UakExitCodes.Failure;
		}
	}

	/// <summary>Reads the command's own options (and positional arguments), and returns what to run once the workspace is found.</summary>
	internal abstract Func<PerforceVersionControl, CancellationToken, Task<int>> Parse(UakArguments arguments);

	/// <summary>Writes a value as indented JSON.</summary>
	internal Task WriteJsonAsync<T>(T value) => Output.WriteLineAsync(JsonSerializer.Serialize(value, VcsCommandBase.s_jsonOptions));

	/// <summary>
	/// The target changelist from -c=: a number, or "default" (null). <paramref name="required"/> makes it mandatory; without it,
	/// an absent -c= is the default changelist too.
	/// </summary>
	internal static int? ParseChange(UakArguments arguments, bool required, bool allowDefault)
	{
		string? text = required ? arguments.GetRequiredString("c") : arguments.GetString("c");
		if (text is null)
		{
			return null;
		}
		if (allowDefault && text.Equals(PerforceVersionControl.DefaultChangeName, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
		{
			throw new UakUsageException(allowDefault ? $"-c must be a changelist number or 'default', not '{text}'." : $"-c must be a changelist number, not '{text}'.");
		}
		return number;
	}

	/// <summary>The positional file arguments; a usage error when there are none.</summary>
	internal static List<string> RequireFiles(UakArguments arguments)
	{
		if (arguments.Positional.Count == 0)
		{
			throw new UakUsageException("Name at least one file.");
		}
		if (arguments.HasSeparator)
		{
			throw new UakUsageException("Unexpected \"--\".");
		}
		return [.. arguments.Positional];
	}

	/// <summary>Prints per-file results (or JSON), and returns 0 only when every file is opened where it was asked to be.</summary>
	internal async Task<int> ReportFilesAsync(IReadOnlyList<PerforceFileResult> results, int? change)
	{
		bool allOpened = results.Count > 0 && results.All(result => result.Opened);
		string target = change is null ? PerforceVersionControl.DefaultChangeName : change.Value.ToString(CultureInfo.InvariantCulture);
		if (Json)
		{
			await WriteJsonAsync(new { change = target, succeeded = allOpened, files = results }).ConfigureAwait(false);
		}
		else
		{
			foreach (PerforceFileResult result in results)
			{
				string line = result.Opened
					? $"opened  {result.Action,-12} change {result.Change,-9} {result.Path}"
					: $"FAILED  {result.Path}: {result.Message}";
				await Output.WriteLineAsync(line).ConfigureAwait(false);
			}
			int failed = results.Count(result => !result.Opened);
			await Output.WriteLineAsync(failed == 0
				? $"{results.Count} file(s) opened in change {target}."
				: $"{failed} of {results.Count} file(s) are not opened in change {target}.").ConfigureAwait(false);
		}
		return allOpened ? UakExitCodes.Success : UakExitCodes.Failure;
	}
}

/// <summary><c>uak vcs shelve</c>: shelves a pending changelist's opened files, or moves given files into a new changelist and shelves that.</summary>
public sealed class VcsShelveCommand : PerforceCommandBase
{
	/// <summary>The description of a changelist created by <c>uak vcs shelve &lt;file&gt;...</c> without <c>-description=</c>.</summary>
	public const string DefaultDescription = "Shelved with uak vcs shelve.";

	/// <inheritdoc/>
	public override string Name => "vcs shelve";

	/// <inheritdoc/>
	public override string Summary => "Shelve a pending changelist's opened files, or shelve given opened files in a new changelist (Perforce). Never reverts or submits.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak vcs shelve -c=<changelist> [-keep-unopened] [-json]\n" +
		"uak vcs shelve <file>... [-description=<text>] [-force] [-json]\n" +
		"  -c=           shelve every file opened in this pending changelist of this client, replacing its shelf (p4 shelve\n" +
		"                -r): the shelf becomes exactly the opened files. Shelved files that are no longer opened in the\n" +
		"                changelist (reverted, or moved to another changelist) are DELETED from the shelf, so a preflight\n" +
		"                doesn't build them and a submit of the shelf doesn't include them; each is printed (\"REMOVED from\n" +
		"                the shelf\"). Files whose shelved content or action changed are listed too. Refused when nothing is\n" +
		"                opened in the changelist (that would empty the shelf). A file that left the changelist while uak\n" +
		"                shelved (so p4 deleted it from the shelf although uak hadn't seen it go) fails the command (exit 1),\n" +
		"                naming it. Refused while an auto-submit Horde preflight of the changelist runs, when a Horde server\n" +
		"                is configured: Horde submits the changelist's current shelf when the preflight succeeds. When Horde\n" +
		"                can't be asked, uak warns and shelves.\n" +
		"  -keep-unopened  p4 shelve -f instead (the behaviour before 0.3.3): opened files are shelved over their shelved\n" +
		"                copies, and shelved files that are no longer opened STAY on the shelf (listed as kept: a preflight\n" +
		"                builds them and a submit of the shelf would include them). Use it when the shelf is the only copy of\n" +
		"                work that is no longer opened.\n" +
		"  -replace, -drop-unopened  accepted and ignored, so older scripts keep working: replacing the shelf is the default.\n" +
		"  <file>...     files already opened in this client: creates a new changelist, moves them into it (p4 reopen), shelves\n" +
		"                it, and prints its number, with each file's original changelist. Fails before changing anything when\n" +
		"                a file is not opened, when a file is in a numbered changelist (it would leave that changelist; -force\n" +
		"                moves it anyway), or when only one half of a move is named. A failure after the changelist was created\n" +
		"                names it and the files it holds; under -json \"Created change <N>.\" goes to standard error at once.\n" +
		"  -description= the new changelist's description (default: \"" + DefaultDescription + "\").\n" +
		"  -json         print JSON instead of text.\n" +
		"  The files stay opened in the workspace: nothing is reverted or submitted. Perforce only.";

	/// <summary>The guards checked before an existing changelist's shelf changes: every <see cref="IPerforceShelveGuard"/> the host loaded; tests replace them.</summary>
	internal Func<IReadOnlyList<IPerforceShelveGuard>> Guards { get; set; } = () => UakCommandCatalog.Current?.CreateAll<IPerforceShelveGuard>() ?? [];

	internal override Func<PerforceVersionControl, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		int? change = ParseChange(arguments, required: false, allowDefault: false);
		// -replace and -drop-unopened were how 0.3.2 and earlier asked for p4 shelve -r; it is the default now, so they are
		// read (an unread flag is an error) and ignored.
		bool replace = arguments.GetFlag("replace");
		bool dropUnopened = arguments.GetFlag("drop-unopened");
		bool keepUnopened = arguments.GetFlag("keep-unopened");
		bool force = arguments.GetFlag("force");
		string? description = arguments.GetString("description");
		if (keepUnopened && (replace || dropUnopened))
		{
			throw new UakUsageException("-keep-unopened keeps shelved files that are not opened; -replace and -drop-unopened ask for the opposite. Give one or the other.");
		}
		if (change is not null)
		{
			if (arguments.Positional.Count > 0)
			{
				throw new UakUsageException("Give either -c=<changelist> or files, not both.");
			}
			if (description is not null)
			{
				throw new UakUsageException("-description= is for a new changelist (with files); use uak vcs change describe to change an existing one.");
			}
			if (force)
			{
				throw new UakUsageException("-force goes with files, not -c=.");
			}
			ShelveMode mode = keepUnopened ? ShelveMode.Update : ShelveMode.Replace;
			return (p4, token) => ShelveChangeAsync(p4, change.Value, mode, created: false, null, token);
		}
		if (arguments.Positional.Count == 0)
		{
			throw new UakUsageException("Give -c=<changelist>, or the opened files to shelve in a new changelist.");
		}
		if (keepUnopened || replace || dropUnopened)
		{
			throw new UakUsageException("-keep-unopened, -replace and -drop-unopened go with -c=<changelist>: a new changelist has no shelf yet.");
		}
		List<string> files = RequireFiles(arguments);
		return (p4, token) => ShelveFilesAsync(p4, files, description ?? DefaultDescription, force, token);
	}

	async Task<int> ShelveFilesAsync(PerforceVersionControl p4, List<string> files, string description, bool force, CancellationToken cancellationToken)
	{
		// Every file must already be opened here, before anything changes.
		IReadOnlyDictionary<string, PerforceFileResult> opened = await p4.GetOpenedAsync(files, cancellationToken).ConfigureAwait(false);
		List<string> notOpened = files.Select(Path.GetFullPath).Where(path => !opened.ContainsKey(path)).ToList();
		if (notOpened.Count > 0)
		{
			throw new VcsException("Not opened in this client, so not shelved (open them first with uak vcs edit or uak vcs add): " + string.Join(", ", notOpened));
		}
		// A file in a numbered changelist would leave it: that changelist may be someone's work in progress, or a shelf.
		List<PerforceFileResult> numbered = opened.Values.Where(file => file.Change != PerforceVersionControl.DefaultChangeName).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
		if (numbered.Count > 0 && !force)
		{
			throw new VcsException("These files are in numbered changelists, and shelving them here would move them out: " +
				string.Join(", ", numbered.Select(file => $"{file.Path} (change {file.Change})")) + ". Shelve that changelist with uak vcs shelve -c=<changelist> instead, or pass -force to move them. Nothing was changed.");
		}
		PerforceVersionControl.ThrowOnHalfMoves(opened.Values);
		Dictionary<string, string?> originalChanges = opened.Values.ToDictionary(file => file.DepotFile ?? file.Path, file => file.Change, StringComparer.Ordinal);

		int change = await p4.CreateChangeAsync(description, cancellationToken).ConfigureAwait(false);
		// At once, and on standard error under -json, so the new changelist is known whatever happens next (a failure, Ctrl+C).
		TextWriter notices = Json ? ErrorOutput : Output;
		await notices.WriteLineAsync($"Created change {change}.").ConfigureAwait(false);
		await notices.FlushAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			IReadOnlyList<PerforceFileResult> moved = await p4.ReopenAsync(files, change, allowDirectories: false, cancellationToken).ConfigureAwait(false);
			List<PerforceFileResult> failed = moved.Where(result => !result.Opened).ToList();
			if (failed.Count > 0)
			{
				throw new VcsException("These files could not be moved into the new changelist, so nothing was shelved: " +
					string.Join("; ", failed.Select(result => $"{result.Path}: {result.Message}")) + ".");
			}
			return await ShelveChangeAsync(p4, change, ShelveMode.Replace, created: true, originalChanges, cancellationToken).ConfigureAwait(false);
		}
		catch (VcsException exception)
		{
			throw new VcsException($"{exception.Message} Change {change} was created and holds the files: {await DescribeHoldingsAsync(p4, change, cancellationToken).ConfigureAwait(false)}.", exception);
		}
	}

	/// <summary>The files a changelist holds, for an error message; says so when they can't be read.</summary>
	static async Task<string> DescribeHoldingsAsync(PerforceVersionControl p4, int change, CancellationToken cancellationToken)
	{
		try
		{
			PerforceChange current = await p4.GetOwnPendingChangeAsync(change, cancellationToken).ConfigureAwait(false);
			return current.Files.Count == 0 ? "none" : string.Join(", ", current.Files);
		}
		catch (VcsException exception)
		{
			return $"unknown (uak could not read change {change}: {exception.Message})";
		}
	}

	/// <summary>
	/// Asks every <see cref="IPerforceShelveGuard"/> whether an existing changelist's shelf may change now. Throws when one
	/// refuses (nothing is shelved); a guard that can't check is a warning.
	/// </summary>
	async Task CheckGuardsAsync(int change, CancellationToken cancellationToken)
	{
		foreach (IPerforceShelveGuard guard in Guards())
		{
			PerforceShelveGuardResult result;
			try
			{
				result = await guard.CheckAsync(Context, change, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
			{
				result = new PerforceShelveGuardResult(PerforceShelveGuardVerdict.Unknown, $"{guard.GetType().Name} could not check change {change}: {exception.Message}");
			}
			if (result.Verdict == PerforceShelveGuardVerdict.Refuse)
			{
				throw new VcsException(result.Message ?? $"{guard.GetType().Name} refused to let change {change} be shelved again. Nothing was shelved.");
			}
			if (result.Verdict == PerforceShelveGuardVerdict.Unknown)
			{
				Context.Logger.LogWarning("{Message}", result.Message);
			}
		}
	}

	async Task<int> ShelveChangeAsync(PerforceVersionControl p4, int change, ShelveMode mode, bool created, IReadOnlyDictionary<string, string?>? originalChanges, CancellationToken cancellationToken)
	{
		if (!created)
		{
			await CheckGuardsAsync(change, cancellationToken).ConfigureAwait(false);
		}
		// Replacing drops the shelved files that uak sees are not opened (each is printed); one that leaves the changelist
		// while uak shelves still fails (see PerforceVersionControl.ShelveAsync).
		PerforceShelveResult result = await p4.ShelveAsync(change, mode, dropUnopened: mode == ShelveMode.Replace, cancellationToken).ConfigureAwait(false);
		string? Original(string depotFile) => originalChanges is not null && originalChanges.TryGetValue(depotFile, out string? original) ? original : null;
		if (Json)
		{
			await WriteJsonAsync(new
			{
				change,
				created,
				mode = mode == ShelveMode.Replace ? "replace" : "update",
				files = result.Shelved.Select(file => new { depotFile = file.DepotFile, action = file.Action, originalChange = Original(file.DepotFile) }),
				replaced = result.Replaced,
				removed = result.Removed,
				kept = result.Kept,
			}).ConfigureAwait(false);
			return UakExitCodes.Success;
		}
		foreach (PerforceShelvedFile file in result.Shelved)
		{
			string? original = Original(file.DepotFile);
			await Output.WriteLineAsync($"shelved {file.Action,-12} {file.DepotFile}" + (original is null ? "" : $"  (was in change {original})")).ConfigureAwait(false);
		}
		foreach (string file in result.Replaced)
		{
			await Output.WriteLineAsync($"replaced on the shelf (its shelved content or action changed): {file}").ConfigureAwait(false);
		}
		foreach (string file in result.Removed)
		{
			await Output.WriteLineAsync($"REMOVED from the shelf: {file}").ConfigureAwait(false);
		}
		foreach (string file in result.Kept)
		{
			await Output.WriteLineAsync($"kept on the shelf, but no longer opened in change {change} (a submit of the shelf would include it): {file}").ConfigureAwait(false);
		}
		string how = mode == ShelveMode.Replace ? "p4 shelve -r: the shelf is now exactly these files" : "p4 shelve -f";
		await Output.WriteLineAsync($"Shelved {result.Shelved.Count} file(s) in change {change} ({how})" + (result.Removed.Count == 0 ? "." : $"; {result.Removed.Count} file(s) removed from the shelf.")).ConfigureAwait(false);
		return UakExitCodes.Success;
	}
}

/// <summary><c>uak vcs edit</c>: opens files for edit.</summary>
public sealed class VcsEditCommand : PerforceCommandBase
{
	/// <inheritdoc/>
	public override string Name => "vcs edit";

	/// <inheritdoc/>
	public override string Summary => "Open files for edit, in the default or a given pending changelist (Perforce).";

	/// <inheritdoc/>
	public override string Usage =>
		"uak vcs edit <file>... [-c=<changelist>|default] [-folders] [-json]\n" +
		"  -c=        a pending changelist of this client (default: the default changelist).\n" +
		"  -folders   also accept directories: every file under each is opened.\n" +
		"  -json      print JSON instead of text.\n" +
		"  Each file is checked afterwards: exit 0 only when all are opened in that changelist. A file already opened in another\n" +
		"  changelist stays there (move it with uak vcs reopen). Wildcards are refused. Perforce only.";

	internal override Func<PerforceVersionControl, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		int? change = ParseChange(arguments, required: false, allowDefault: true);
		bool folders = arguments.GetFlag("folders");
		List<string> files = RequireFiles(arguments);
		return async (p4, token) => await ReportFilesAsync(await p4.EditAsync(files, change, folders, token).ConfigureAwait(false), change).ConfigureAwait(false);
	}
}

/// <summary><c>uak vcs add</c>: opens new files for add.</summary>
public sealed class VcsAddCommand : PerforceCommandBase
{
	/// <inheritdoc/>
	public override string Name => "vcs add";

	/// <inheritdoc/>
	public override string Summary => "Open new files for add, in the default or a given pending changelist (Perforce).";

	/// <inheritdoc/>
	public override string Usage =>
		"uak vcs add <file>... [-c=<changelist>|default] [-json]\n" +
		"  -c=        a pending changelist of this client (default: the default changelist).\n" +
		"  -json      print JSON instead of text.\n" +
		"  Existing files only: no directories, wildcards or missing files. Ignored files (P4IGNORE) are not added. Each file\n" +
		"  is checked afterwards: exit 0 only when all are opened in that changelist. Perforce only.";

	internal override Func<PerforceVersionControl, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		int? change = ParseChange(arguments, required: false, allowDefault: true);
		List<string> files = RequireFiles(arguments);
		return async (p4, token) => await ReportFilesAsync(await p4.AddAsync(files, change, token).ConfigureAwait(false), change).ConfigureAwait(false);
	}
}

/// <summary><c>uak vcs reopen</c>: moves opened files to another changelist.</summary>
public sealed class VcsReopenCommand : PerforceCommandBase
{
	/// <inheritdoc/>
	public override string Name => "vcs reopen";

	/// <inheritdoc/>
	public override string Summary => "Move opened files to another pending changelist of this client (Perforce).";

	/// <inheritdoc/>
	public override string Usage =>
		"uak vcs reopen <file>... -c=<changelist>|default [-folders] [-json]\n" +
		"  -c=        the pending changelist to move them to (required), or 'default'.\n" +
		"  -folders   also accept directories. Careful: that moves EVERY file opened under them, including unrelated ones.\n" +
		"  -json      print JSON instead of text.\n" +
		"  Name the files: wildcards are refused, and so are directories without -folders, and half of a move without the\n" +
		"  other half (p4 moves both). Each file is checked afterwards: exit 0 only when all are opened in that changelist.\n" +
		"  Perforce only.";

	internal override Func<PerforceVersionControl, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		int? change = ParseChange(arguments, required: true, allowDefault: true);
		bool folders = arguments.GetFlag("folders");
		List<string> files = RequireFiles(arguments);
		return async (p4, token) => await ReportFilesAsync(await p4.ReopenAsync(files, change, folders, token).ConfigureAwait(false), change).ConfigureAwait(false);
	}
}

/// <summary><c>uak vcs change new</c>: creates an empty pending changelist.</summary>
public sealed class VcsChangeNewCommand : PerforceCommandBase
{
	/// <inheritdoc/>
	public override string Name => "vcs change new";

	/// <inheritdoc/>
	public override string Summary => "Create an empty pending changelist and print its number (Perforce).";

	/// <inheritdoc/>
	public override string Usage =>
		"uak vcs change new -description=<text> [-json]\n" +
		"  Prints the new changelist's number alone on one line. Nothing is moved into it: use uak vcs reopen, or -c= on\n" +
		"  uak vcs edit and uak vcs add. Perforce only.";

	internal override Func<PerforceVersionControl, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		string description = arguments.GetRequiredString("description");
		arguments.ThrowIfMorePositionalThan(0);
		return async (p4, token) =>
		{
			int change = await p4.CreateChangeAsync(description, token).ConfigureAwait(false);
			if (Json)
			{
				await WriteJsonAsync(new { change }).ConfigureAwait(false);
			}
			else
			{
				await Output.WriteLineAsync(change.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
			}
			return UakExitCodes.Success;
		};
	}
}

/// <summary><c>uak vcs change describe</c>: replaces a pending changelist's description.</summary>
public sealed class VcsChangeDescribeCommand : PerforceCommandBase
{
	/// <inheritdoc/>
	public override string Name => "vcs change describe";

	/// <inheritdoc/>
	public override string Summary => "Replace the description of a pending changelist of this client (Perforce).";

	/// <inheritdoc/>
	public override string Usage =>
		"uak vcs change describe -c=<changelist> -description=<text> [-json]\n" +
		"  Only a pending changelist of this client. Its files, jobs and other fields stay as they are. p4 makes the changelist\n" +
		"  hold the files it held when uak read it: a file reopened into it while this runs is moved out to the default\n" +
		"  changelist, and one moved from it to the default changelist comes back in. uak moves each back where it was and\n" +
		"  lists it; when it can't tell exactly which files p4 moved, it moves none and fails, naming them. Perforce only.";

	internal override Func<PerforceVersionControl, CancellationToken, Task<int>> Parse(UakArguments arguments)
	{
		int change = ParseChange(arguments, required: true, allowDefault: false)!.Value;
		string description = arguments.GetRequiredString("description");
		arguments.ThrowIfMorePositionalThan(0);
		return async (p4, token) =>
		{
			PerforceDescriptionUpdate update = await p4.UpdateDescriptionAsync(change, description, token).ConfigureAwait(false);
			if (Json)
			{
				await WriteJsonAsync(new { change, description, restored = update.Restored, released = update.Released }).ConfigureAwait(false);
			}
			else
			{
				await Output.WriteLineAsync($"Change {change}: description updated.").ConfigureAwait(false);
				foreach (string file in update.Restored)
				{
					await Output.WriteLineAsync($"  moved back into change {change} (p4 had moved it out to the default changelist while the description changed): {file}").ConfigureAwait(false);
				}
				foreach (string file in update.Released)
				{
					await Output.WriteLineAsync($"  moved back to the default changelist (p4 had pulled it into change {change} while the description changed): {file}").ConfigureAwait(false);
				}
			}
			return UakExitCodes.Success;
		};
	}
}
