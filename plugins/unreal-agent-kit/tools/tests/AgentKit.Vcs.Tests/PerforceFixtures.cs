// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Buffers.Binary;
using System.Text;
using EpicGames.Perforce;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Trace;

namespace AgentKit.Vcs.Tests;

/// <summary>Loads the recorded <c>-ztag</c> fixtures and encodes them as the <c>p4 -G</c> (Python marshal) stream p4 writes.</summary>
internal static class PerforceFixtures
{
	/// <summary>Fields p4 -G sends as marshal integers rather than strings.</summary>
	static readonly HashSet<string> s_integerFields = ["severity", "generic"];

	public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Perforce");

	/// <summary>Reads a fixture's records, replacing {root} with <paramref name="root"/> ('/' separators, as p4 prints on every platform they get normalised by the parser anyway).</summary>
	public static List<List<KeyValuePair<string, string>>> ReadRecords(string name, string root)
	{
		string text = File.ReadAllText(Path.Combine(Directory, name + ".ztag")).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("{root}", root, StringComparison.Ordinal);
		List<List<KeyValuePair<string, string>>> records = [];
		List<KeyValuePair<string, string>>? current = null;
		foreach (string line in text.Split('\n'))
		{
			if (line.StartsWith("... ", StringComparison.Ordinal))
			{
				current ??= [];
				string body = line[4..];
				int space = body.IndexOf(' ', StringComparison.Ordinal);
				current.Add(space < 0 ? KeyValuePair.Create(body, string.Empty) : KeyValuePair.Create(body[..space], body[(space + 1)..]));
			}
			else if (line.StartsWith('\t') && current is { Count: > 0 })
			{
				// A continuation of a multi-line value.
				KeyValuePair<string, string> last = current[^1];
				current[^1] = KeyValuePair.Create(last.Key, last.Value + "\n" + line);
			}
			else if (line.Length == 0 && current is not null)
			{
				records.Add(current);
				current = null;
			}
		}
		if (current is not null)
		{
			records.Add(current);
		}
		return records;
	}

	/// <summary>Encodes a fixture as p4 -G output.</summary>
	public static byte[] Encode(string name, string root)
	{
		using MemoryStream stream = new();
		foreach (List<KeyValuePair<string, string>> record in ReadRecords(name, root))
		{
			stream.WriteByte((byte)'{');
			foreach ((string key, string value) in record)
			{
				WriteString(stream, key);
				if (s_integerFields.Contains(key))
				{
					stream.WriteByte((byte)'i');
					byte[] number = new byte[4];
					BinaryPrimitives.WriteInt32LittleEndian(number, int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
					stream.Write(number);
				}
				else
				{
					WriteString(stream, value);
				}
			}
			stream.WriteByte((byte)'0');
		}
		return stream.ToArray();
	}

	static void WriteString(Stream stream, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		stream.WriteByte((byte)'s');
		byte[] length = new byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
		stream.Write(length);
		stream.Write(bytes);
	}
}

/// <summary>One command the fake connection received.</summary>
internal sealed record PerforceCall(string Command, IReadOnlyList<string> Arguments, IReadOnlyList<string> FileArguments);

/// <summary>
/// An <see cref="IPerforceConnection"/> that answers each command with a recorded fixture, parsed by EpicGames.Perforce's
/// real -G reader. Commands without a fixture throw, so a test notices an unexpected server call.
/// </summary>
internal sealed class FakePerforceConnection : IPerforceConnection
{
	readonly Dictionary<string, Func<PerforceCall, string>> _fixtures = new(StringComparer.Ordinal);
	readonly string _root;

	public FakePerforceConnection(string root, IPerforceSettings? settings = null)
	{
		_root = root;
		Settings = settings ?? new PerforceSettings("perforce.example.com:1666", "user") { ClientName = "user-ws" };
	}

	public IPerforceSettings Settings { get; }

	public ILogger Logger { get; } = NullLogger.Instance;

	public Tracer Tracer { get; } = TracerProvider.Default.GetTracer("AgentKit.Vcs.Tests");

	public List<PerforceCall> Calls { get; } = [];

	public bool Disposed { get; private set; }

	/// <summary>Answers <paramref name="command"/> with the named fixture, or with plain text for "text:...".</summary>
	public FakePerforceConnection On(string command, string fixture) => On(command, _ => fixture);

	/// <summary>Answers <paramref name="command"/> with a fixture chosen from the call.</summary>
	public FakePerforceConnection On(string command, Func<PerforceCall, string> fixture)
	{
		_fixtures[command] = fixture;
		return this;
	}

	public IPerforceOutput Command(string command, IReadOnlyList<string> arguments, IReadOnlyList<string>? fileArguments, byte[]? inputData, string? promptResponse, bool interceptIo)
	{
		PerforceCall call = new(command, [.. arguments], [.. fileArguments ?? []]);
		Calls.Add(call);
		if (!_fixtures.TryGetValue(command, out Func<PerforceCall, string>? fixture))
		{
			throw new InvalidOperationException($"Unexpected p4 {command} {string.Join(' ', arguments)}");
		}
		string name = fixture(call);
		// "text:..." answers with plain text, as p4 does for some connection failures even under -G.
		byte[] data = name.StartsWith("text:", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetBytes(name[5..]) : PerforceFixtures.Encode(name, _root);
		return PerforceOutput.FromData(data);
	}

	public PerforceRecord CreateRecord(List<KeyValuePair<string, object>> fields) => PerforceRecord.FromFields(fields, true);

	public void Dispose() => Disposed = true;
}
