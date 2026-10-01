// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentKit.Core;
using Microsoft.Extensions.Logging;

namespace AgentKit.Vcs;

/// <summary>The options the <c>vcs</c> commands share, parsed UE style (<c>-Key=Value</c>, case-insensitive).</summary>
internal sealed class VcsArguments
{
	public string? Kind { get; private init; }
	public bool Json { get; private init; }
	public string? PathFilter { get; private init; }
	public List<string> Paths { get; } = [];

	public static VcsArguments Parse(IReadOnlyList<string> arguments, bool allowPaths, bool allowPathFilter)
	{
		UakArguments parsed = new(arguments);
		VcsArguments result = new()
		{
			// -vcs= is normally a global option (before the command); accepting it here too overrides that.
			Kind = parsed.GetString("vcs"),
			Json = parsed.GetFlag("json"),
			PathFilter = allowPathFilter ? parsed.GetString("path") : null,
		};
		parsed.ThrowIfUnknown();
		parsed.ThrowIfMorePositionalThan(allowPaths ? int.MaxValue : 0);
		result.Paths.AddRange(parsed.Positional);
		return result;
	}
}

/// <summary>Shared plumbing for the <c>vcs</c> commands: detection, error handling and output.</summary>
public abstract class VcsCommandBase : IUakCommand
{
	static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() },
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

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

	/// <inheritdoc/>
	public async Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		VcsArguments parsed = ParseArguments(arguments);
		VcsDetection detection;
		try
		{
			detection = await VersionControlDetector.DetectAsync(context, parsed.Kind, cancellationToken).ConfigureAwait(false);
		}
		catch (VcsException exception)
		{
			throw new UakUsageException(exception.Message, exception);
		}

		try
		{
			return await RunAsync(detection, parsed, cancellationToken).ConfigureAwait(false);
		}
		catch (VcsException exception)
		{
			context.Logger.LogError("{Message}", exception.Message);
			return UakExitCodes.Failure;
		}
		finally
		{
			detection.VersionControl.Dispose();
		}
	}

	internal abstract VcsArguments ParseArguments(IReadOnlyList<string> arguments);

	internal abstract Task<int> RunAsync(VcsDetection detection, VcsArguments arguments, CancellationToken cancellationToken);

	/// <summary>Writes a value as indented JSON.</summary>
	internal Task WriteJsonAsync<T>(T value) => Output.WriteLineAsync(JsonSerializer.Serialize(value, s_jsonOptions));

	/// <summary>Writes statuses one per line: state, path, and the source for renames and copies.</summary>
	internal async Task WriteStatusesAsync(IEnumerable<VcsFileStatus> statuses)
	{
		foreach (VcsFileStatus status in statuses)
		{
			string line = status.OriginalPath is null ? $"{status.State,-12} {status.Path}" : $"{status.State,-12} {status.Path} (from {status.OriginalPath})";
			await Output.WriteLineAsync(line).ConfigureAwait(false);
		}
	}
}

/// <summary>Adds the version-control line to <c>uak env</c>.</summary>
public sealed class VcsEnvReporter : IUakEnvReporter
{
	/// <inheritdoc/>
	public async Task<IReadOnlyList<KeyValuePair<string, string>>> ReportAsync(UakContext context, CancellationToken cancellationToken)
	{
		string value;
		try
		{
			VcsDetection detection = await VersionControlDetector.DetectAsync(context, null, cancellationToken).ConfigureAwait(false);
			using IVersionControl vcs = detection.VersionControl;
			value = vcs.Kind == VcsKind.None
				? $"None ({detection.Provenance})"
				: $"{await vcs.DescribeAsync(cancellationToken).ConfigureAwait(false)} (found by {detection.Provenance})";
		}
		catch (VcsException exception)
		{
			value = "error: " + exception.Message;
		}
		return [new(EnvCommand.VersionControlLabel, value)];
	}
}

/// <summary><c>uak vcs status [path...]</c>: the workspace summary, or the status of the given files.</summary>
public sealed class VcsStatusCommand : VcsCommandBase
{
	/// <inheritdoc/>
	public override string Name => "vcs status";

	/// <inheritdoc/>
	public override string Summary => "Show the version control in use, or the status of the given files.";

	/// <inheritdoc/>
	public override string Usage =>
		"uak vcs status [-vcs=git|perforce|none] [-json] [<path>...]\n" +
		"  Without paths: the system, its root, the current revision and how it was detected.\n" +
		"  With paths: one line per file: Unmodified, Modified, Added, Deleted, Renamed, Copied, TypeChanged, Conflicted, Untracked, Ignored or Unknown.\n" +
		"  -vcs=     override detection (like the global -vcs= and UAK_VCS).\n" +
		"  -json     print JSON instead of text.";

	internal override VcsArguments ParseArguments(IReadOnlyList<string> arguments) => VcsArguments.Parse(arguments, allowPaths: true, allowPathFilter: false);

	internal override async Task<int> RunAsync(VcsDetection detection, VcsArguments arguments, CancellationToken cancellationToken)
	{
		IVersionControl vcs = detection.VersionControl;
		if (arguments.Paths.Count == 0)
		{
			string description = await vcs.DescribeAsync(cancellationToken).ConfigureAwait(false);
			if (arguments.Json)
			{
				string? revision = vcs.Kind == VcsKind.None ? null : await vcs.GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false);
				await WriteJsonAsync(new { kind = vcs.Kind, root = vcs.RootDirectory?.FullName, revision, description, detectedBy = detection.Provenance }).ConfigureAwait(false);
			}
			else
			{
				await Output.WriteLineAsync(description).ConfigureAwait(false);
				await Output.WriteLineAsync($"Detected by: {detection.Provenance}").ConfigureAwait(false);
			}
			return UakExitCodes.Success;
		}

		IReadOnlyList<VcsFileStatus> statuses = await vcs.GetFileStatusAsync(arguments.Paths, cancellationToken).ConfigureAwait(false);
		if (arguments.Json)
		{
			await WriteJsonAsync(statuses).ConfigureAwait(false);
		}
		else
		{
			await WriteStatusesAsync(statuses).ConfigureAwait(false);
		}
		return UakExitCodes.Success;
	}
}

/// <summary><c>uak vcs changed</c>: files that differ from the current revision.</summary>
public sealed class VcsChangedCommand : VcsCommandBase
{
	/// <inheritdoc/>
	public override string Name => "vcs changed";

	/// <inheritdoc/>
	public override string Summary => "List changed files: modified, added, deleted, renamed or untracked (Git), or opened (Perforce).";

	/// <inheritdoc/>
	public override string Usage =>
		"uak vcs changed [-vcs=git|perforce|none] [-path=<file or directory>] [-json]\n" +
		"  One line per file: its state and absolute path. Ignored files are not listed.\n" +
		"  -path=    limit to a file or directory.\n" +
		"  -vcs=     override detection (like the global -vcs= and UAK_VCS).\n" +
		"  -json     print JSON instead of text.";

	internal override VcsArguments ParseArguments(IReadOnlyList<string> arguments) => VcsArguments.Parse(arguments, allowPaths: false, allowPathFilter: true);

	internal override async Task<int> RunAsync(VcsDetection detection, VcsArguments arguments, CancellationToken cancellationToken)
	{
		IReadOnlyList<VcsFileStatus> statuses = await detection.VersionControl.GetChangedFilesAsync(arguments.PathFilter, cancellationToken).ConfigureAwait(false);
		if (arguments.Json)
		{
			await WriteJsonAsync(statuses).ConfigureAwait(false);
		}
		else
		{
			await WriteStatusesAsync(statuses).ConfigureAwait(false);
		}
		return UakExitCodes.Success;
	}
}

/// <summary><c>uak vcs revision</c>: the commit hash (Git) or have changelist (Perforce).</summary>
public sealed class VcsRevisionCommand : VcsCommandBase
{
	/// <inheritdoc/>
	public override string Name => "vcs revision";

	/// <inheritdoc/>
	public override string Summary => "Print the current revision: the HEAD commit hash (Git) or the highest synced changelist (Perforce).";

	/// <inheritdoc/>
	public override string Usage =>
		"uak vcs revision [-vcs=git|perforce|none] [-json]\n" +
		"  Prints the revision alone on one line. Exits 1 when there is none (no commits, nothing synced, or no version control).\n" +
		"  -vcs=     override detection (like the global -vcs= and UAK_VCS).\n" +
		"  -json     print JSON instead of text.";

	internal override VcsArguments ParseArguments(IReadOnlyList<string> arguments) => VcsArguments.Parse(arguments, allowPaths: false, allowPathFilter: false);

	internal override async Task<int> RunAsync(VcsDetection detection, VcsArguments arguments, CancellationToken cancellationToken)
	{
		string? revision = await detection.VersionControl.GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false);
		if (arguments.Json)
		{
			await WriteJsonAsync(new { kind = detection.VersionControl.Kind, revision }).ConfigureAwait(false);
		}
		else if (revision is not null)
		{
			await Output.WriteLineAsync(revision).ConfigureAwait(false);
		}
		return revision is null ? UakExitCodes.Failure : UakExitCodes.Success;
	}
}
