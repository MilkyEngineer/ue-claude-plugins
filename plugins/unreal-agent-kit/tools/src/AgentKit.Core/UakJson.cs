// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentKit.Core;

/// <summary>
/// The kit's JSON conventions (DESIGN.md, "The queued lock"): System.Text.Json, property names as declared, indented,
/// enums as names, and every time in ISO 8601 UTC with a "Z" suffix. Text is UTF-8 without a BOM. Writing files atomically
/// and reading them while others write is AgentKit.Core's StateFiles; this class only turns values into JSON and back.
/// </summary>
public static class UakJson
{
	/// <summary>The serializer options every kit file uses. Shared and read-only.</summary>
	public static JsonSerializerOptions Options { get; } = CreateOptions();

	/// <summary>UTF-8 without a byte-order mark.</summary>
	public static Encoding Utf8NoBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	/// <summary>Serializes a value to JSON text.</summary>
	public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

	/// <summary>Serializes a value to UTF-8 bytes without a BOM, ending with a newline: the content of a kit JSON file.</summary>
	public static byte[] SerializeToUtf8<T>(T value) => Utf8NoBom.GetBytes(Serialize(value) + "\n");

	/// <summary>Deserializes JSON text. Throws <see cref="JsonException"/> on bad JSON or a null document.</summary>
	public static T Deserialize<T>(string json) =>
		JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"The JSON is null, not a {typeof(T).Name}.");

	/// <summary>Deserializes UTF-8 JSON, with or without a BOM. Throws <see cref="JsonException"/> on bad JSON or a null document.</summary>
	public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json)
	{
		ReadOnlySpan<byte> Bom = [0xEF, 0xBB, 0xBF];
		if (utf8Json.StartsWith(Bom))
		{
			utf8Json = utf8Json[Bom.Length..];
		}
		return JsonSerializer.Deserialize<T>(utf8Json, Options) ?? throw new JsonException($"The JSON is null, not a {typeof(T).Name}.");
	}

	/// <summary>Formats a time as ISO 8601 UTC with milliseconds, e.g. "2026-10-01T09:30:00.000Z". Local and unspecified times are converted (unspecified is taken as UTC).</summary>
	public static string FormatTime(DateTime time) => ToUtc(time).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

	/// <summary>Parses an ISO 8601 time into a UTC <see cref="DateTime"/>. A time without an offset is taken as UTC.</summary>
	public static DateTime ParseTime(string text) =>
		DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

	static DateTime ToUtc(DateTime time) => time.Kind switch
	{
		DateTimeKind.Utc => time,
		DateTimeKind.Local => time.ToUniversalTime(),
		_ => DateTime.SpecifyKind(time, DateTimeKind.Utc),
	};

	static JsonSerializerOptions CreateOptions()
	{
		JsonSerializerOptions Result = new(JsonSerializerDefaults.General)
		{
			WriteIndented = true,
			IndentCharacter = '\t',
			IndentSize = 1,
			AllowTrailingCommas = true,
			ReadCommentHandling = JsonCommentHandling.Skip,
			PropertyNameCaseInsensitive = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never,
			NewLine = "\n",
		};
		Result.Converters.Add(new JsonStringEnumConverter());
		Result.Converters.Add(new UtcDateTimeConverter());
		Result.Converters.Add(new UtcDateTimeOffsetConverter());
		Result.MakeReadOnly(populateMissingResolver: true);
		return Result;
	}

	/// <summary>Writes <see cref="DateTime"/> as ISO 8601 UTC; reads any ISO 8601 time back as UTC.</summary>
	sealed class UtcDateTimeConverter : JsonConverter<DateTime>
	{
		public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			ParseTime(reader.GetString() ?? throw new JsonException("A time must be a string."));

		public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
			writer.WriteStringValue(FormatTime(value));
	}

	/// <summary>Writes <see cref="DateTimeOffset"/> as ISO 8601 UTC; reads any ISO 8601 time back with a zero offset.</summary>
	sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
	{
		public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			new(ParseTime(reader.GetString() ?? throw new JsonException("A time must be a string.")), TimeSpan.Zero);

		public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
			writer.WriteStringValue(FormatTime(value.UtcDateTime));
	}
}
