// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentKit.Horde;

/// <summary>A job template as a stream offers it.</summary>
/// <param name="Id">The template's id in the stream.</param>
/// <param name="Name">Its display name.</param>
/// <param name="AllowPreflights">Whether it may run a preflight of a shelved change.</param>
/// <param name="CanRun">Whether the logged-in user may start it.</param>
public sealed record HordeTemplate(string Id, string Name, bool AllowPreflights, bool CanRun)
{
	/// <summary>The template's description, when it has one.</summary>
	public string? Description { get; init; }

	/// <summary>The template's parameters, in the dashboard's order.</summary>
	[JsonIgnore]
	public IReadOnlyList<HordeTemplateParameter> Parameters { get; init; } = [];
}

/// <summary>The kind of a template parameter.</summary>
public enum HordeParameterKind
{
	/// <summary>A checkbox: true or false.</summary>
	Bool,

	/// <summary>Free text, sometimes checked by a regular expression.</summary>
	Text,

	/// <summary>A list of choices; each choice is on or off in Horde.</summary>
	List,
}

/// <summary>How a list parameter is picked.</summary>
public enum HordeListStyle
{
	/// <summary>A drop-down: one choice (Horde's List); some have no default choice.</summary>
	Single,

	/// <summary>Checkboxes: any number of choices (Horde's MultiList).</summary>
	Multi,

	/// <summary>A tag picker: any number of choices (Horde's TagPicker).</summary>
	Tags,
}

/// <summary>A choice of a list parameter.</summary>
/// <param name="Id">Horde's parameter id for the choice: the value Horde reads ("true" or "false") is keyed by it.</param>
/// <param name="Text">Its display text.</param>
/// <param name="Group">The group it is shown in, if any.</param>
/// <param name="Default">Whether it is chosen by default.</param>
public sealed record HordeListChoice(string Id, string Text, string? Group, bool Default);

/// <summary>A template parameter, as the stream's templates list it.</summary>
/// <param name="Id">
/// The parameter's id. Bool and text parameters have Horde's own id. Horde's lists have no id (only their choices do), so a
/// list's id is uak's: its label in Horde's id form (lower case, other characters as '-'), made unique.
/// </param>
/// <param name="Kind">Bool, text or list.</param>
/// <param name="Label">The label the dashboard shows.</param>
/// <param name="Description">The tool tip, if any.</param>
public sealed record HordeTemplateParameter(string Id, HordeParameterKind Kind, string Label, string? Description)
{
	/// <summary>A bool parameter's default.</summary>
	public bool BoolDefault { get; init; }

	/// <summary>A text parameter's default.</summary>
	public string TextDefault { get; init; } = "";

	/// <summary>A text parameter's hint.</summary>
	public string? Hint { get; init; }

	/// <summary>A text parameter's regular expression, which a value must match.</summary>
	public string? Validation { get; init; }

	/// <summary>The message for a text value that doesn't match <see cref="Validation"/>.</summary>
	public string? ValidationError { get; init; }

	/// <summary>A list's style.</summary>
	public HordeListStyle Style { get; init; }

	/// <summary>A list's choices.</summary>
	public IReadOnlyList<HordeListChoice> Choices { get; init; } = [];
}

/// <summary>A Horde stream, as <c>GET api/v1/streams</c> lists it (only those the user may see).</summary>
/// <param name="Id">The Horde stream id.</param>
/// <param name="Name">The Perforce stream it builds, such as "//Project/Main".</param>
/// <param name="ProjectId">The Horde project.</param>
/// <param name="DefaultPreflightTemplate">The template the dashboard picks for a preflight, when the stream names one.</param>
/// <param name="Templates">The stream's templates.</param>
public sealed record HordeStream(string Id, string Name, string? ProjectId, string? DefaultPreflightTemplate, IReadOnlyList<HordeTemplate> Templates);

/// <summary>One step of a job, as the job's batches list it.</summary>
/// <param name="Id">The step id.</param>
/// <param name="Name">The node's name.</param>
/// <param name="GroupIdx">The batch's group.</param>
/// <param name="NodeIdx">The node within the group.</param>
/// <param name="State">Waiting, Ready, Running, Completed, Skipped or Aborted.</param>
/// <param name="Outcome">Unspecified, Success, Warnings or Failure.</param>
/// <param name="LogId">Its log, once it has one.</param>
public sealed record HordeStep(string Id, string Name, int GroupIdx, int NodeIdx, string State, string Outcome, string? LogId)
{
	/// <summary>Whether the step may still run: anything but Completed, Skipped or Aborted (Horde's IsPendingState).</summary>
	public bool IsPending => !State.Equals("Completed", StringComparison.OrdinalIgnoreCase) && !State.Equals("Skipped", StringComparison.OrdinalIgnoreCase) && !State.Equals("Aborted", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A batch that failed to run (no agent, a bad workspace...), from its error.</summary>
/// <param name="GroupIdx">The batch's group.</param>
/// <param name="Error">Horde's error name, such as "UnknownAgentType".</param>
public sealed record HordeBatchError(int GroupIdx, string Error);

/// <summary>How a job ended, or that it hasn't.</summary>
public enum HordeJobResult
{
	/// <summary>Steps are still waiting or running.</summary>
	Running,

	/// <summary>Every step succeeded.</summary>
	Success,

	/// <summary>No step failed, but at least one has warnings.</summary>
	Warnings,

	/// <summary>At least one step failed.</summary>
	Failure,

	/// <summary>Nothing failed, but steps were skipped or aborted, so it did not complete.</summary>
	Incomplete,
}

/// <summary>A job's state: what <c>GET api/v1/jobs/{id}</c> returns, reduced to what uak reports.</summary>
/// <param name="Id">The job id.</param>
/// <param name="Name">The job's name.</param>
/// <param name="StreamId">The Horde stream.</param>
/// <param name="TemplateId">The template.</param>
/// <param name="State">Waiting, Running or Complete.</param>
/// <param name="UpdateTime">The job's last update, as the server wrote it; passed back as <c>modifiedAfter</c>.</param>
/// <param name="PreflightChange">The shelved change it preflights, if any.</param>
/// <param name="AutoSubmit">Whether Horde will submit the change when the preflight succeeds.</param>
/// <param name="AbortedBy">Who aborted the job, when someone did.</param>
/// <param name="Steps">Every step, in the server's order (a retried node has several).</param>
/// <param name="BatchErrors">Batches that failed to run.</param>
public sealed record HordeJob(string Id, string Name, string StreamId, string TemplateId, string State, string? UpdateTime, int? PreflightChange, bool AutoSubmit, string? AbortedBy, IReadOnlyList<HordeStep> Steps, IReadOnlyList<HordeBatchError> BatchErrors)
{
	/// <summary>The latest step of each node: a retry adds a step for the same (group, node), later in the list.</summary>
	public IReadOnlyList<HordeStep> LatestSteps =>
		Steps.GroupBy(step => (step.GroupIdx, step.NodeIdx)).Select(group => group.Last()).ToList();

	/// <summary>
	/// The job's result, as Horde computes a target's (JobExtensions.GetTargetState): any failed step fails it, else any
	/// warnings, else any skipped or aborted step leaves it incomplete, else success. It is Running while a step is pending
	/// and the job is not Complete. A finished job whose batches failed to run, with no failed step, is Incomplete.
	/// </summary>
	public HordeJobResult Result
	{
		get
		{
			IReadOnlyList<HordeStep> steps = LatestSteps;
			bool complete = State.Equals("Complete", StringComparison.OrdinalIgnoreCase);
			if (!complete && (steps.Count == 0 || steps.Any(step => step.IsPending)))
			{
				return HordeJobResult.Running;
			}
			if (steps.Any(step => step.Outcome.Equals("Failure", StringComparison.OrdinalIgnoreCase)))
			{
				return HordeJobResult.Failure;
			}
			if (steps.Any(step => step.Outcome.Equals("Warnings", StringComparison.OrdinalIgnoreCase)))
			{
				return HordeJobResult.Warnings;
			}
			bool skipped = steps.Any(step => step.State.Equals("Skipped", StringComparison.OrdinalIgnoreCase) || step.State.Equals("Aborted", StringComparison.OrdinalIgnoreCase));
			return skipped || BatchErrors.Count > 0 || steps.Count == 0 || AbortedBy is not null ? HordeJobResult.Incomplete : HordeJobResult.Success;
		}
	}

	/// <summary>Reads a job response. Null for the empty object the server returns when the job hasn't changed since <c>modifiedAfter</c>.</summary>
	public static HordeJob? Parse(JsonNode? node)
	{
		if (node is not JsonObject job || job.Count == 0)
		{
			return null;
		}
		List<HordeStep> steps = [];
		List<HordeBatchError> errors = [];
		foreach (JsonObject batch in Json.Objects(job, "batches"))
		{
			int group = Json.Int(batch, "groupIdx") ?? 0;
			string? error = Json.String(batch, "error");
			if (!string.IsNullOrEmpty(error) && !error.Equals("None", StringComparison.OrdinalIgnoreCase))
			{
				errors.Add(new HordeBatchError(group, error));
			}
			foreach (JsonObject step in Json.Objects(batch, "steps"))
			{
				steps.Add(new HordeStep(Json.String(step, "id") ?? "", Json.String(step, "name") ?? "", group, Json.Int(step, "nodeIdx") ?? 0,
					Json.String(step, "state") ?? "Unspecified", Json.String(step, "outcome") ?? "Unspecified", Json.String(step, "logId")));
			}
		}
		string? aborted = Json.String(job, "abortedByUser") ?? Json.String(job["abortedByUserInfo"] as JsonObject, "name");
		int? preflight = Json.Int(job, "preflightChange") ?? (int.TryParse(Json.String(job, "preflightCommitId"), NumberStyles.None, CultureInfo.InvariantCulture, out int commit) ? commit : null);
		return new HordeJob(Json.String(job, "id") ?? "", Json.String(job, "name") ?? "", Json.String(job, "streamId") ?? "", Json.String(job, "templateId") ?? "",
			Json.String(job, "state") ?? "", Json.String(job, "updateTime"), preflight is > 0 ? preflight : null, Json.Bool(job, "autoSubmit") ?? false, aborted, steps, errors);
	}
}

/// <summary>A job found by a search (<c>GET api/v1/jobs?preflightChange=</c>).</summary>
/// <param name="Id">The job id.</param>
/// <param name="StreamId">The Horde stream.</param>
/// <param name="TemplateId">The template.</param>
/// <param name="State">Waiting, Running or Complete.</param>
/// <param name="CreateTime">When it was created, as the server wrote it.</param>
public sealed record HordeJobSummary(string Id, string StreamId, string TemplateId, string State, string? CreateTime)
{
	/// <summary>Whether Horde will submit the change when this preflight succeeds.</summary>
	public bool AutoSubmit { get; init; }

	/// <summary>The job's parameter values by Horde's ids (the template's defaults with the request's values over them), when the server sent them.</summary>
	public IReadOnlyDictionary<string, string>? Parameters { get; init; }

	/// <summary>
	/// The request's additional command-line arguments (CreateJobRequest.AdditionalArguments), when the server sent them. uak
	/// never sends any, so a job with some is not uak's request.
	/// </summary>
	public IReadOnlyList<string>? AdditionalArguments { get; init; }

	/// <summary>The request's custom targets (CreateJobRequest.Targets), when it had any. uak never sends any.</summary>
	public IReadOnlyList<string>? Targets { get; init; }

	/// <summary>Whether it is still waiting or running.</summary>
	public bool IsActive => !State.Equals("Complete", StringComparison.OrdinalIgnoreCase);

	/// <summary><see cref="CreateTime"/> as a time, or null when it is missing or unreadable.</summary>
	public DateTimeOffset? Created => DateTimeOffset.TryParse(CreateTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset created) ? created : null;

	/// <summary>Reads a search result array.</summary>
	public static IReadOnlyList<HordeJobSummary> ParseList(JsonNode? node)
		=> node is JsonArray array
			? array.OfType<JsonObject>().Select(job => new HordeJobSummary(Json.String(job, "id") ?? "", Json.String(job, "streamId") ?? "", Json.String(job, "templateId") ?? "", Json.String(job, "state") ?? "", Json.String(job, "createTime"))
			{
				AutoSubmit = Json.Bool(job, "autoSubmit") ?? false,
				Parameters = Json.Get(job, "parameters") is JsonObject parameters
					? parameters.Where(pair => pair.Value is JsonValue).ToDictionary(pair => pair.Key, pair => pair.Value!.ToString(), StringComparer.OrdinalIgnoreCase)
					: null,
				AdditionalArguments = Strings(job, "additionalArguments"),
				Targets = Strings(job, "targets"),
			}).ToList()
			: [];

	/// <summary>A JSON array of strings, or null when it is missing; an entry that isn't a string still counts (as its JSON text).</summary>
	static List<string>? Strings(JsonObject job, string name)
		=> Json.Get(job, name) is JsonArray values ? values.Select(value => value is JsonValue text ? text.ToString() : value?.ToJsonString() ?? "null").ToList() : null;
}

/// <summary>Reads stream lists.</summary>
public static class HordeStreamParser
{
	/// <summary>Reads <c>GET api/v1/streams</c>.</summary>
	public static IReadOnlyList<HordeStream> ParseList(JsonNode? node)
	{
		if (node is not JsonArray array)
		{
			return [];
		}
		List<HordeStream> streams = [];
		foreach (JsonObject stream in array.OfType<JsonObject>())
		{
			List<HordeTemplate> templates = Json.Objects(stream, "templates")
				.Select(template => new HordeTemplate(Json.String(template, "id") ?? "", Json.String(template, "name") ?? "", Json.Bool(template, "allowPreflights") ?? false, Json.Bool(template, "canRun") ?? true)
				{
					Description = string.IsNullOrWhiteSpace(Json.String(template, "description")) ? null : Json.String(template, "description"),
					Parameters = ParseParameters(template),
				})
				.ToList();
			string? defaultPreflight = Json.String(stream["defaultPreflight"] as JsonObject, "templateId") ?? Json.String(stream, "defaultPreflightTemplate");
			streams.Add(new HordeStream(Json.String(stream, "id") ?? "", Json.String(stream, "name") ?? "", Json.String(stream, "projectId"), string.IsNullOrEmpty(defaultPreflight) ? null : defaultPreflight, templates));
		}
		return streams;
	}

	/// <summary>
	/// Reads a template's parameters (GetTemplateParameterResponse: a "type" of Bool, Text or List). Parameters of an unknown
	/// type are skipped. Lists get uak's id (see <see cref="HordeTemplateParameter.Id"/>).
	/// </summary>
	public static IReadOnlyList<HordeTemplateParameter> ParseParameters(JsonObject template)
	{
		List<HordeTemplateParameter> parameters = [];
		foreach (JsonObject parameter in Json.Objects(template, "parameters"))
		{
			string type = Json.String(parameter, "type") ?? "";
			string label = Json.String(parameter, "label") ?? "";
			string? tip = string.IsNullOrWhiteSpace(Json.String(parameter, "toolTip")) ? null : Json.String(parameter, "toolTip");
			string? id = Json.String(parameter, "id");
			if (type.Equals("Bool", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(id))
			{
				parameters.Add(new HordeTemplateParameter(id, HordeParameterKind.Bool, label, tip) { BoolDefault = Json.Bool(parameter, "default") ?? false });
			}
			else if (type.Equals("Text", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(id))
			{
				parameters.Add(new HordeTemplateParameter(id, HordeParameterKind.Text, label, tip)
				{
					TextDefault = Json.String(parameter, "default") ?? "",
					Hint = string.IsNullOrEmpty(Json.String(parameter, "hint")) ? null : Json.String(parameter, "hint"),
					Validation = string.IsNullOrEmpty(Json.String(parameter, "validation")) ? null : Json.String(parameter, "validation"),
					ValidationError = string.IsNullOrEmpty(Json.String(parameter, "validationError")) ? null : Json.String(parameter, "validationError"),
				});
			}
			else if (type.Equals("List", StringComparison.OrdinalIgnoreCase))
			{
				string style = Json.String(parameter, "style") ?? "List";
				List<HordeListChoice> choices = Json.Objects(parameter, "items")
					.Where(item => !string.IsNullOrEmpty(Json.String(item, "id")))
					.Select(item => new HordeListChoice(Json.String(item, "id")!, Json.String(item, "text") ?? Json.String(item, "id")!, string.IsNullOrEmpty(Json.String(item, "group")) ? null : Json.String(item, "group"), Json.Bool(item, "default") ?? false))
					.ToList();
				parameters.Add(new HordeTemplateParameter("", HordeParameterKind.List, label, tip)
				{
					Style = style.Equals("MultiList", StringComparison.OrdinalIgnoreCase) || style == "1" ? HordeListStyle.Multi
						: style.Equals("TagPicker", StringComparison.OrdinalIgnoreCase) || style == "2" ? HordeListStyle.Tags
						: HordeListStyle.Single,
					Choices = choices,
				});
			}
		}

		// Lists have no id in Horde: give each one its label in Horde's id form, unique among the template's ids.
		HashSet<string> taken = new(parameters.Where(parameter => parameter.Kind != HordeParameterKind.List).Select(parameter => parameter.Id), StringComparer.OrdinalIgnoreCase);
		for (int index = 0; index < parameters.Count; index++)
		{
			if (parameters[index].Kind != HordeParameterKind.List)
			{
				continue;
			}
			string baseId = ToId(parameters[index].Label);
			if (baseId.Length == 0)
			{
				baseId = "list";
			}
			string unique = baseId;
			for (int suffix = 2; !taken.Add(unique); suffix++)
			{
				unique = baseId + "-" + suffix.ToString(CultureInfo.InvariantCulture);
			}
			parameters[index] = parameters[index] with { Id = unique };
		}
		return parameters;
	}

	/// <summary>Text in Horde's id form: lower case letters, digits, '_' and '.'; any other run of characters becomes one '-'.</summary>
	internal static string ToId(string text)
	{
		System.Text.StringBuilder result = new();
		foreach (char character in text.Trim())
		{
			char lower = char.ToLowerInvariant(character);
			if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9') || lower == '_' || lower == '.')
			{
				result.Append(lower);
			}
			else if (result.Length > 0 && result[^1] != '-')
			{
				result.Append('-');
			}
		}
		return result.ToString().Trim('-', '.');
	}
}

/// <summary>Case-insensitive reads from Horde's camelCase JSON, tolerant of numbers written as strings and enums as numbers.</summary>
internal static class Json
{
	public static JsonNode? Get(JsonObject? node, string name)
	{
		if (node is null)
		{
			return null;
		}
		if (node.TryGetPropertyValue(name, out JsonNode? value))
		{
			return value;
		}
		foreach ((string key, JsonNode? other) in node)
		{
			if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				return other;
			}
		}
		return null;
	}

	public static string? String(JsonObject? node, string name) => Get(node, name) is JsonValue value ? value.ToString() : null;

	public static int? Int(JsonObject? node, string name)
		=> Get(node, name) is JsonValue value && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;

	public static bool? Bool(JsonObject? node, string name)
		=> Get(node, name) is JsonValue value && bool.TryParse(value.ToString(), out bool flag) ? flag : null;

	public static IEnumerable<JsonObject> Objects(JsonObject? node, string name)
		=> Get(node, name) is JsonArray array ? array.OfType<JsonObject>() : [];
}
