// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentKit.Core;

namespace AgentKit.Horde;

/// <summary>A stream's saved build settings: the template preflights use, and each template's saved parameters.</summary>
/// <param name="Stream">The Horde stream id.</param>
/// <param name="Template">The template preflights use; null when none is saved.</param>
/// <param name="Parameters">Each template's saved parameter values, by template id then parameter id, as uak writes them on a command line.</param>
/// <param name="Updated">When they were last saved (ISO 8601), if known.</param>
/// <param name="Path">The file.</param>
public sealed record HordeSavedBuild(string Stream, string? Template, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parameters, string? Updated, string Path)
{
	/// <summary>The saved parameters of a template; empty when there are none.</summary>
	public IReadOnlyDictionary<string, string> ParametersFor(string templateId)
		=> Parameters.TryGetValue(templateId, out IReadOnlyDictionary<string, string>? values) ? values : new Dictionary<string, string>();
}

/// <summary>
/// Saved Horde build settings, one file per server and stream: <c>&lt;UAK_HOME&gt;/horde/&lt;server&gt;/&lt;stream&gt;/templates.json</c>.
/// Writes are atomic, and keep every key uak doesn't know.
/// </summary>
/// <example><code>
/// { "server": "https://horde.example.com/", "stream": "project-main", "template": "editor-preflight",
///   "templates": { "editor-preflight": { "parameters": { "run-tests": false, "platforms": ["win64"] }, "updated": "..." } },
///   "updated": "..." }
/// </code></example>
public sealed class HordeBuildSettingsStore
{
	/// <summary>The file's name in the stream's folder.</summary>
	public const string FileName = "templates.json";

	/// <summary>Creates the store under <paramref name="root"/>, the kit's <c>horde</c> folder.</summary>
	public HordeBuildSettingsStore(string root)
	{
		Root = System.IO.Path.GetFullPath(root);
	}

	/// <summary>The kit's <c>horde</c> folder.</summary>
	public string Root { get; }

	/// <summary>The kit's <c>horde</c> folder for this user (<c>UAK_HOME</c>, else <c>~/.unreal-agent-kit</c>).</summary>
	public static string UserRoot(UakResolveOptions? options = null)
		=> System.IO.Path.Combine(UakContextResolver.GetUserDirectory(options ?? new UakResolveOptions()), "horde");

	/// <summary>This user's store.</summary>
	public static HordeBuildSettingsStore ForUser(UakResolveOptions? options = null) => new(UserRoot(options));

	/// <summary>
	/// A server's folder name: its host in lower case, then "_" and the port when it isn't the scheme's default, then its path
	/// segments, each joined by "_"; characters other than letters, digits, '-' and '.' become '-'.
	/// </summary>
	public static string ServerFolder(Uri server)
	{
		StringBuilder name = new(Clean(server.Host.ToLowerInvariant()));
		if (!server.IsDefaultPort)
		{
			name.Append('_').Append(server.Port.ToString(CultureInfo.InvariantCulture));
		}
		foreach (string segment in server.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			name.Append('_').Append(Clean(Uri.UnescapeDataString(segment)));
		}
		return name.ToString();

		static string Clean(string text)
		{
			string cleaned = new(text.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' ? character : '-').ToArray());
			return cleaned.Trim('.').Length == 0 ? "-" : cleaned;
		}
	}

	/// <summary>The settings file of a stream. Throws <see cref="UakUsageException"/> for an id that isn't Horde's id form.</summary>
	public string PathFor(Uri server, string streamId)
	{
		if (!IsId(streamId))
		{
			throw new UakUsageException($"'{streamId}' is not a Horde stream id.");
		}
		return System.IO.Path.Combine(Root, ServerFolder(server), streamId.ToLowerInvariant(), FileName);
	}

	/// <summary>A stream's saved settings, or null when there are none. Throws <see cref="UakUsageException"/> when the file can't be read.</summary>
	public HordeSavedBuild? Read(Uri server, string streamId)
	{
		string path = PathFor(server, streamId);
		JsonObject? root = ReadFile(path);
		return root is null ? null : ToSaved(root, streamId, path);
	}

	/// <summary>Every stream's saved settings for a server, by stream id.</summary>
	public IReadOnlyList<HordeSavedBuild> ReadAll(Uri server)
	{
		string folder = System.IO.Path.Combine(Root, ServerFolder(server));
		if (!Directory.Exists(folder))
		{
			return [];
		}
		List<HordeSavedBuild> saved = [];
		foreach (string directory in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
		{
			string stream = System.IO.Path.GetFileName(directory);
			string path = System.IO.Path.Combine(directory, FileName);
			if (IsId(stream) && ReadFile(path) is JsonObject root)
			{
				saved.Add(ToSaved(root, stream, path));
			}
		}
		return saved;
	}

	/// <summary>
	/// Saves a stream's template and that template's parameters (normalized values, see <see cref="HordeBuildParameters"/>),
	/// replacing the template's saved parameters and keeping the other templates' and any unknown keys.
	/// </summary>
	public HordeSavedBuild Save(Uri server, string streamId, HordeTemplate template, IReadOnlyDictionary<string, string> parameters, DateTime utcNow)
	{
		string path = PathFor(server, streamId);
		JsonObject root = ReadFile(path) ?? [];
		string updated = utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
		root["server"] = server.ToString();
		root["stream"] = streamId;
		root["template"] = template.Id;
		root["updated"] = updated;
		if (root["templates"] is not JsonObject templates)
		{
			templates = [];
			root["templates"] = templates;
		}
		if (templates[template.Id] is not JsonObject entry)
		{
			entry = [];
			templates[template.Id] = entry;
		}
		JsonObject values = [];
		foreach ((string id, string value) in parameters)
		{
			HordeTemplateParameter? parameter = template.Parameters.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
			values[parameter?.Id ?? id] = parameter?.Kind switch
			{
				HordeParameterKind.Bool => JsonValue.Create(value == "true"),
				HordeParameterKind.List => new JsonArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(choice => (JsonNode)JsonValue.Create(choice)).ToArray()),
				_ => JsonValue.Create(value),
			};
		}
		entry["parameters"] = values;
		entry["updated"] = updated;
		StateFiles.WriteText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
		return ToSaved(root, streamId, path);
	}

	/// <summary>
	/// Forgets a stream's saved settings: removes the template and every template's parameters. The file is deleted unless it
	/// holds keys uak doesn't know, which are kept. Returns whether anything was saved.
	/// </summary>
	public bool Reset(Uri server, string streamId)
	{
		string path = PathFor(server, streamId);
		JsonObject? root = ReadFile(path);
		if (root is null)
		{
			return false;
		}
		bool had = root.ContainsKey("template") || root.ContainsKey("templates");
		root.Remove("template");
		root.Remove("templates");
		if (root.All(pair => pair.Key is "server" or "stream" or "updated"))
		{
			File.Delete(path);
		}
		else
		{
			StateFiles.WriteText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
		}
		return had;
	}

	/// <summary>Whether text is in Horde's id form: letters, digits, '-', '_' and '.', not starting or ending with '.'.</summary>
	internal static bool IsId(string text)
		=> text.Length is > 0 and <= 128 && text[0] != '.' && text[^1] != '.' && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

	static JsonObject? ReadFile(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			string text = File.ReadAllText(path);
			return string.IsNullOrWhiteSpace(text)
				? null
				: JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
					?? throw new UakUsageException($"{path} does not hold a JSON object. Fix or delete it (uak horde config -reset-build).");
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
		{
			throw new UakUsageException($"Could not read {path}: {exception.Message}. Fix or delete it.", exception);
		}
	}

	static HordeSavedBuild ToSaved(JsonObject root, string streamId, string path)
	{
		Dictionary<string, IReadOnlyDictionary<string, string>> templates = new(StringComparer.OrdinalIgnoreCase);
		if (Json.Get(root, "templates") is JsonObject saved)
		{
			foreach ((string templateId, JsonNode? entry) in saved)
			{
				Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
				if (Json.Get(entry as JsonObject, "parameters") is JsonObject parameters)
				{
					foreach ((string id, JsonNode? value) in parameters)
					{
						values[id] = value switch
						{
							JsonArray array => string.Join(',', array.Select(item => item?.ToString() ?? "")),
							JsonValue scalar when scalar.TryGetValue(out bool flag) => flag ? "true" : "false",
							null => "",
							_ => value.ToString(),
						};
					}
				}
				templates[templateId] = values;
			}
		}
		string? template = Json.String(root, "template");
		return new HordeSavedBuild(Json.String(root, "stream") ?? streamId, string.IsNullOrEmpty(template) ? null : template, templates, Json.String(root, "updated"), path);
	}
}

/// <summary>Checks and converts template parameter values (DESIGN.md, "Horde").</summary>
public static class HordeBuildParameters
{
	/// <summary>
	/// A value in uak's form: bool "true" or "false" (also accepted: 1/0, yes/no, on/off); a list's chosen ids (ids or texts
	/// accepted), comma-separated in the template's order, at most one for a drop-down; text as given, checked against the template's regular expression.
	/// </summary>
	public static bool TryNormalize(HordeTemplateParameter parameter, string value, out string normalized, out string? error)
	{
		normalized = value;
		error = null;
		switch (parameter.Kind)
		{
			case HordeParameterKind.Bool:
				switch (value.Trim().ToLowerInvariant())
				{
					case "true" or "1" or "yes" or "on":
						normalized = "true";
						return true;
					case "false" or "0" or "no" or "off":
						normalized = "false";
						return true;
				}
				error = $"{parameter.Id} is true or false, not '{value}'.";
				return false;
			case HordeParameterKind.List:
				HashSet<string> chosen = new(StringComparer.OrdinalIgnoreCase);
				foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					HordeListChoice? choice = parameter.Choices.FirstOrDefault(candidate => candidate.Id.Equals(part, StringComparison.OrdinalIgnoreCase))
						?? parameter.Choices.FirstOrDefault(candidate => candidate.Text.Equals(part, StringComparison.OrdinalIgnoreCase));
					if (choice is null)
					{
						error = $"{parameter.Id} has no choice '{part}'. Its choices: {string.Join(", ", parameter.Choices.Select(candidate => candidate.Id + (candidate.Text == candidate.Id ? "" : $" ({candidate.Text})")))}.";
						return false;
					}
					chosen.Add(choice.Id);
				}
				// A drop-down takes one choice; none is allowed too, since some templates' drop-downs have no default choice.
				if (parameter.Style == HordeListStyle.Single && chosen.Count > 1)
				{
					error = $"{parameter.Id} takes one choice, not {chosen.Count}.";
					return false;
				}
				normalized = string.Join(',', parameter.Choices.Where(choice => chosen.Contains(choice.Id)).Select(choice => choice.Id));
				return true;
			default:
				if (parameter.Validation is not null)
				{
					try
					{
						if (!Regex.IsMatch(value, parameter.Validation, RegexOptions.None, TimeSpan.FromSeconds(1)))
						{
							error = $"{parameter.Id}: {(string.IsNullOrWhiteSpace(parameter.ValidationError) ? $"'{value}' doesn't match {parameter.Validation}" : parameter.ValidationError)}.";
							return false;
						}
					}
					catch (ArgumentException)
					{
						// A pattern .NET can't read (the dashboard checks it in the browser): leave the check to Horde.
					}
					catch (RegexMatchTimeoutException)
					{
					}
				}
				return true;
		}
	}

	/// <summary>A parameter's default, in uak's form.</summary>
	public static string DefaultValue(HordeTemplateParameter parameter) => parameter.Kind switch
	{
		HordeParameterKind.Bool => parameter.BoolDefault ? "true" : "false",
		HordeParameterKind.List => string.Join(',', parameter.Choices.Where(choice => choice.Default).Select(choice => choice.Id)),
		_ => parameter.TextDefault,
	};

	/// <summary>
	/// Checks values against a template: each id must be one of its parameters (ignoring case), each value valid. Returns the
	/// normalized values by the template's ids; adds one message per problem to <paramref name="errors"/>.
	/// </summary>
	public static Dictionary<string, string> Validate(HordeTemplate template, IEnumerable<KeyValuePair<string, string>> values, List<string> errors)
	{
		Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string id, string value) in values)
		{
			HordeTemplateParameter? parameter = template.Parameters.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
			if (parameter is null)
			{
				errors.Add($"Template {template.Id} has no parameter '{id}'. Its parameters: {(template.Parameters.Count == 0 ? "none" : string.Join(", ", template.Parameters.Select(candidate => candidate.Id)))}.");
				continue;
			}
			if (TryNormalize(parameter, value, out string result, out string? error))
			{
				normalized[parameter.Id] = result;
			}
			else
			{
				errors.Add(error!);
			}
		}
		return normalized;
	}

	/// <summary>
	/// The values for <c>CreateJobRequest.Parameters</c>: a bool or text by its id; a list as every choice's id with "true" or
	/// "false", so exactly the chosen ones are on. Parameters with no value keep the template's defaults on the server.
	/// </summary>
	public static Dictionary<string, string> ToHorde(HordeTemplate template, IReadOnlyDictionary<string, string> values)
	{
		Dictionary<string, string> result = new(StringComparer.Ordinal);
		foreach (HordeTemplateParameter parameter in template.Parameters)
		{
			if (!values.TryGetValue(parameter.Id, out string? value))
			{
				continue;
			}
			if (parameter.Kind == HordeParameterKind.List)
			{
				HashSet<string> chosen = new(value.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
				foreach (HordeListChoice choice in parameter.Choices)
				{
					result[choice.Id] = chosen.Contains(choice.Id) ? "true" : "false";
				}
			}
			else
			{
				result[parameter.Id] = value;
			}
		}
		return result;
	}

	/// <summary>
	/// Every value a job of this template gets (its parameters as Horde's ids, as a job reports them): the template's defaults,
	/// with <paramref name="sent"/> (<see cref="ToHorde"/>) over them.
	/// </summary>
	public static Dictionary<string, string> Effective(HordeTemplate template, IReadOnlyDictionary<string, string> sent)
	{
		Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
		foreach (HordeTemplateParameter parameter in template.Parameters)
		{
			switch (parameter.Kind)
			{
				case HordeParameterKind.Bool:
					values[parameter.Id] = parameter.BoolDefault ? "true" : "false";
					break;
				case HordeParameterKind.Text:
					values[parameter.Id] = parameter.TextDefault;
					break;
				default:
					foreach (HordeListChoice choice in parameter.Choices)
					{
						values[choice.Id] = choice.Default ? "true" : "false";
					}
					break;
			}
		}
		foreach ((string id, string value) in sent)
		{
			values[id] = value;
		}
		return values;
	}

	/// <summary>Whether two parameter values are the same: as booleans when both read as one (Horde writes "True"), else exactly.</summary>
	public static bool SameValue(string a, string b)
		=> bool.TryParse(a, out bool left) && bool.TryParse(b, out bool right) ? left == right : string.Equals(a, b, StringComparison.Ordinal);

	/// <summary>One line naming the template and the values that differ from its defaults.</summary>
	public static string Describe(HordeTemplate template, IReadOnlyDictionary<string, string> values)
	{
		List<string> changed = template.Parameters
			.Where(parameter => values.TryGetValue(parameter.Id, out string? value) && value != DefaultValue(parameter))
			.Select(parameter => $"{parameter.Id}={(parameter.Kind == HordeParameterKind.Text ? "\"" + values[parameter.Id] + "\"" : values[parameter.Id])}")
			.ToList();
		return $"Template: {template.Name} ({template.Id}); " + (changed.Count == 0 ? "parameters: template defaults" : "non-default parameters: " + string.Join(", ", changed));
	}

	/// <summary>The command that saves build settings, as the shape agents fill in.</summary>
	public static string SaveCommand(string streamId) => $"uak horde config -stream={streamId} -template=<template id> -param:<parameter id>=<value> ...";

	/// <summary>The templates a preflight can use: those that allow preflights and that the user can run, the stream's default first.</summary>
	public static IReadOnlyList<HordeTemplate> PreflightTemplates(HordeStream stream)
		=> stream.Templates.Where(template => template.AllowPreflights && template.CanRun)
			.OrderBy(template => template.Id.Equals(stream.DefaultPreflightTemplate, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
			.ToList();

	/// <summary>
	/// What <c>uak horde templates -json</c> prints, and preflight's exit 6: the stream, its preflight templates with their
	/// parameters (type, label, description, default, choices, and the saved value as "current"), the saved settings, and the
	/// command that saves new ones.
	/// </summary>
	public static JsonObject DescribeTemplates(Uri server, HordeStream stream, HordeSavedBuild? saved)
	{
		JsonArray templates = [];
		foreach (HordeTemplate template in PreflightTemplates(stream))
		{
			IReadOnlyDictionary<string, string> current = saved?.ParametersFor(template.Id) ?? new Dictionary<string, string>();
			JsonArray parameters = [];
			foreach (HordeTemplateParameter parameter in template.Parameters)
			{
				JsonObject entry = new()
				{
					["id"] = parameter.Id,
					["type"] = parameter.Kind.ToString().ToLowerInvariant(),
					["label"] = parameter.Label,
				};
				if (parameter.Description is not null)
				{
					entry["description"] = parameter.Description;
				}
				current.TryGetValue(parameter.Id, out string? value);
				switch (parameter.Kind)
				{
					case HordeParameterKind.Bool:
						entry["default"] = parameter.BoolDefault;
						if (value is not null)
						{
							entry["current"] = value == "true";
						}
						break;
					case HordeParameterKind.List:
						entry["style"] = parameter.Style.ToString().ToLowerInvariant();
						entry["multiSelect"] = parameter.Style != HordeListStyle.Single;
						entry["choices"] = new JsonArray(parameter.Choices.Select(choice =>
						{
							JsonObject item = new() { ["id"] = choice.Id, ["text"] = choice.Text, ["default"] = choice.Default };
							if (choice.Group is not null)
							{
								item["group"] = choice.Group;
							}
							return (JsonNode)item;
						}).ToArray());
						entry["default"] = new JsonArray(parameter.Choices.Where(choice => choice.Default).Select(choice => (JsonNode)JsonValue.Create(choice.Id)).ToArray());
						if (value is not null)
						{
							entry["current"] = new JsonArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(choice => (JsonNode)JsonValue.Create(choice)).ToArray());
						}
						break;
					default:
						entry["default"] = parameter.TextDefault;
						if (parameter.Hint is not null)
						{
							entry["hint"] = parameter.Hint;
						}
						if (parameter.Validation is not null)
						{
							entry["validation"] = parameter.Validation;
						}
						if (parameter.ValidationError is not null)
						{
							entry["validationError"] = parameter.ValidationError;
						}
						if (value is not null)
						{
							entry["current"] = value;
						}
						break;
				}
				parameters.Add(entry);
			}
			JsonObject described = new()
			{
				["id"] = template.Id,
				["name"] = template.Name,
				["default"] = template.Id.Equals(stream.DefaultPreflightTemplate, StringComparison.OrdinalIgnoreCase),
				["saved"] = saved?.Template is not null && template.Id.Equals(saved.Template, StringComparison.OrdinalIgnoreCase),
			};
			if (template.Description is not null)
			{
				described["description"] = template.Description;
			}
			described["parameters"] = parameters;
			templates.Add(described);
		}
		JsonObject result = new()
		{
			["server"] = server.ToString(),
			["stream"] = new JsonObject { ["id"] = stream.Id, ["name"] = stream.Name },
		};
		if (saved?.Template is not null)
		{
			JsonObject values = [];
			foreach ((string id, string value) in saved.ParametersFor(saved.Template))
			{
				values[id] = value;
			}
			result["saved"] = new JsonObject { ["template"] = saved.Template, ["parameters"] = values, ["updated"] = saved.Updated, ["file"] = saved.Path };
		}
		else
		{
			result["saved"] = null;
		}
		result["templates"] = templates;
		result["saveCommand"] = SaveCommand(stream.Id);
		result["valueForms"] = "bool: true or false; list: choice ids, comma-separated (at most one when multiSelect is false); text: the text";
		return result;
	}
}

/// <summary>The template and parameters a preflight runs with, and where they came from.</summary>
/// <param name="Template">The template.</param>
/// <param name="Parameters">Normalized values by parameter id; parameters left out keep the template's defaults.</param>
/// <param name="Source">"saved", "command line", "saved and command line", or "template defaults".</param>
public sealed record HordeBuildChoice(HordeTemplate Template, IReadOnlyDictionary<string, string> Parameters, string Source);

/// <summary>Chooses a preflight's build settings (DESIGN.md, "Horde").</summary>
public static class HordeBuildResolver
{
	/// <summary>
	/// The template and parameters: <c>-use-template-defaults</c> takes <paramref name="templateArgument"/> or the stream's
	/// default with no parameters. Otherwise the template is <paramref name="templateArgument"/>, else the saved one, else (with
	/// <c>-param:</c> only) the stream's default; its saved parameters, overridden by <paramref name="parameterArguments"/>.
	/// Returns null with <paramref name="needed"/> (exit 6) when nothing is saved and nothing was given, or when the saved
	/// settings no longer fit the server's templates. Throws <see cref="UakUsageException"/> for bad command-line values.
	/// </summary>
	public static HordeBuildChoice? Resolve(HordeStream stream, HordeSavedBuild? saved, string? templateArgument, IReadOnlyList<KeyValuePair<string, string>> parameterArguments, bool useTemplateDefaults, out string? needed)
	{
		needed = null;
		if (useTemplateDefaults)
		{
			return new HordeBuildChoice(HordeSelection.ChooseTemplate(stream, templateArgument), new Dictionary<string, string>(), "template defaults");
		}
		string? savedTemplate = saved?.Template;
		if (templateArgument is null && parameterArguments.Count == 0 && savedTemplate is null)
		{
			needed = $"No build settings are saved for stream {stream.Id}.";
			return null;
		}

		HordeTemplate template;
		if (templateArgument is null && savedTemplate is not null)
		{
			HordeTemplate? found = stream.Templates.FirstOrDefault(candidate => candidate.Id.Equals(savedTemplate, StringComparison.OrdinalIgnoreCase));
			if (found is null || !found.AllowPreflights)
			{
				needed = $"The saved template '{savedTemplate}' of stream {stream.Id} is {(found is null ? "no longer offered" : "no longer allowed for preflights")}.";
				return null;
			}
			template = found;
		}
		else
		{
			template = HordeSelection.ChooseTemplate(stream, templateArgument);
		}

		IReadOnlyDictionary<string, string> savedValues = saved?.ParametersFor(template.Id) ?? new Dictionary<string, string>();
		List<string> savedErrors = [];
		Dictionary<string, string> values = HordeBuildParameters.Validate(template, savedValues, savedErrors);
		if (savedErrors.Count > 0)
		{
			needed = $"The saved parameters of template {template.Id} no longer fit it: {string.Join(" ", savedErrors)}";
			return null;
		}
		List<string> errors = [];
		foreach ((string id, string value) in HordeBuildParameters.Validate(template, parameterArguments, errors))
		{
			values[id] = value;
		}
		if (errors.Count > 0)
		{
			throw new UakUsageException(string.Join(" ", errors) + " uak horde templates lists the parameters.");
		}
		string source = savedValues.Count > 0 || (templateArgument is null && savedTemplate is not null)
			? parameterArguments.Count > 0 || templateArgument is not null ? "saved and command line" : "saved"
			: "command line";
		return new HordeBuildChoice(template, values, source);
	}
}
