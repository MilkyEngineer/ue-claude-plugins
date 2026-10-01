// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using EpicGames.Core;
using EpicGames.Perforce;

namespace AgentKit.Vcs;

/// <summary>
/// One <c>p4 -G</c> record as it came from the server: every field, in order, parsed by EpicGames.Perforce's own reader
/// (<see cref="PerforceOutput.ParseRecord"/>). Used for commands the library has no typed record for (its ReopenRecord has
/// no fields), and for specs that must go back to the server unchanged apart from one field (<c>p4 change -o</c>, then
/// <c>p4 change -i</c>).
/// </summary>
public sealed class PerforceRawRecord
{
	internal PerforceRawRecord(List<KeyValuePair<Utf8String, PerforceValue>> rows)
	{
		Rows = rows;
	}

	/// <summary>The fields, in the server's order, including <c>code</c>.</summary>
	internal List<KeyValuePair<Utf8String, PerforceValue>> Rows { get; }

	/// <summary>"stat", "info" or "error".</summary>
	public string Code => Get("code") ?? string.Empty;

	/// <summary>Whether this is a data record (code "stat").</summary>
	public bool IsStat => Code == "stat";

	/// <summary>Whether this is an error or a warning (code "error"); see <see cref="Severity"/>.</summary>
	public bool IsError => Code == "error";

	/// <summary>p4's severity for an error record: 2 is a warning (for example "file(s) not opened on this client"), 3 and 4 are failures.</summary>
	public int Severity => int.TryParse(Get("severity"), out int severity) ? severity : 0;

	/// <summary>The message of an info or error record, on one line.</summary>
	public string Message => VcsPaths.OneLine(Get("data") ?? string.Empty);

	/// <summary>A field's value as text (integers are formatted), or null when the record has no such field.</summary>
	public string? Get(string key)
	{
		foreach ((Utf8String name, PerforceValue value) in Rows)
		{
			if (name.ToString() == key)
			{
				return value.IsEmpty ? string.Empty : value.ToString();
			}
		}
		return null;
	}

	/// <summary>The values of a numbered list field (Files0, Files1...), in order.</summary>
	public IReadOnlyList<string> GetList(string key)
	{
		List<string> values = [];
		for (int index = 0; ; index++)
		{
			string? value = Get(key + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
			if (value is null)
			{
				return values;
			}
			values.Add(value);
		}
	}

	/// <summary>
	/// This record as <c>p4 -G</c> input, without its <c>code</c> field and with <paramref name="key"/> set to <paramref name="value"/>
	/// (added at the end when missing). Every other field goes back exactly as the server sent it.
	/// </summary>
	internal byte[] SerializeWith(string key, string value)
	{
		PerforceRecord record = new();
		bool replaced = false;
		foreach ((Utf8String name, PerforceValue fieldValue) in Rows)
		{
			string text = name.ToString();
			if (text == "code")
			{
				continue;
			}
			if (text == key)
			{
				record.Rows.Add(new(name, new PerforceValue(value)));
				replaced = true;
			}
			else
			{
				record.Rows.Add(new(name, fieldValue));
			}
		}
		if (!replaced)
		{
			record.Rows.Add(new(new Utf8String(key), new PerforceValue(value)));
		}
		return record.Serialize();
	}

	/// <summary>Builds a record from text fields, for tests.</summary>
	internal static PerforceRawRecord FromFields(params (string Key, string Value)[] fields)
		=> new(fields.Select(field => new KeyValuePair<Utf8String, PerforceValue>(new Utf8String(field.Key), new PerforceValue(field.Value))).ToList());

	/// <summary>
	/// Runs a command and reads every record it writes. Plain text where a record should start (p4 prints some connection
	/// failures that way, even under -G) throws <see cref="PerforceException"/>, as the library's typed reader does.
	/// </summary>
	internal static async Task<List<PerforceRawRecord>> RunAsync(IPerforceConnection connection, string command, IReadOnlyList<string> arguments, IReadOnlyList<string>? fileArguments, byte[]? inputData, CancellationToken cancellationToken)
	{
		List<PerforceRawRecord> records = [];
		await using IPerforceOutput output = connection.Command(command, arguments, fileArguments, inputData, null, false);
		for (; ; )
		{
			ReadOnlyMemory<byte> data = output.Data;
			if (data.Length > 0 && data.Span[0] != '{')
			{
				throw new PerforceException("Unexpected response from p4 {0}: {1}", command, System.Text.Encoding.UTF8.GetString(data.Span[..Math.Min(data.Length, 512)]).Trim());
			}
			int position = 0;
			for (; ; )
			{
				int start = position;
				List<KeyValuePair<Utf8String, PerforceValue>> rows = [];
				if (!PerforceOutput.ParseRecord(data, ref position, rows))
				{
					position = start;
					break;
				}
				// Keys may point into the output buffer, which the next read reuses; values are already copies.
				records.Add(new PerforceRawRecord(rows.Select(row => new KeyValuePair<Utf8String, PerforceValue>(row.Key.Clone(), row.Value)).ToList()));
			}
			output.Discard(position);

			int pending = output.Data.Length;
			if (!await output.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				break;
			}
			if (output.Data.Length == pending && position == 0)
			{
				// Nothing parsed and nothing new arrived: a truncated record that will never complete.
				break;
			}
		}
		if (output.Data.Length > 0)
		{
			throw new PerforceException("p4 {0} ended in the middle of a record.", command);
		}
		return records;
	}
}
