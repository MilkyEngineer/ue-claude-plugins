// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text;

namespace AgentKit.Core.Tests;

[TestClass]
public sealed class StateFilesTests
{
	private string _directory = null!;

	public TestContext TestContext { get; set; } = null!;

	[TestInitialize]
	public void Initialize()
	{
		_directory = Path.Combine(Path.GetTempPath(), "UakStateFilesTest", Guid.NewGuid().ToString("N")[..12]);
	}

	[TestCleanup]
	public void Cleanup()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}
	}

	public sealed class Sample
	{
		public string Name { get; set; } = "";

		public DateTime? When { get; set; }

		public DayOfWeek Day { get; set; }

		public int? Missing { get; set; }
	}

	[TestMethod]
	public void JsonRoundTripsAsUtf8WithoutBomAndUtcTimes()
	{
		string path = Path.Combine(_directory, "sub", "sample.json");
		DateTime when = new(2026, 10, 1, 6, 5, 4, 321, DateTimeKind.Utc);
		StateFiles.WriteJson(path, new Sample { Name = "é ü", When = when, Day = DayOfWeek.Friday });

		byte[] bytes = File.ReadAllBytes(path);
		Assert.AreNotEqual(0xEF, bytes[0], "No BOM.");
		string text = Encoding.UTF8.GetString(bytes);
		StringAssert.Contains(text, "\"When\": \"2026-10-01T06:05:04.321Z\"");
		StringAssert.Contains(text, "\"Day\": \"Friday\"");
		StringAssert.Contains(text, "\"Missing\": null");

		Sample? read = StateFiles.ReadJson<Sample>(path);
		Assert.IsNotNull(read);
		Assert.AreEqual("é ü", read.Name);
		Assert.AreEqual(when, read.When);
		Assert.AreEqual(DateTimeKind.Utc, read.When!.Value.Kind);
		Assert.AreEqual(DayOfWeek.Friday, read.Day);
		Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"), "No temporary file is left.");
	}

	[TestMethod]
	public void TimesReadBackAsUtcWithOrWithoutAZone()
	{
		DateTime expected = new(2026, 10, 1, 6, 5, 4, 321, DateTimeKind.Utc);
		foreach (string text in new[] { "2026-10-01T06:05:04.321Z", "2026-10-01T06:05:04.321", "2026-10-01T08:05:04.321+02:00" })
		{
			DateTime parsed = UakJson.ParseTime(text);
			Assert.AreEqual(expected, parsed, text);
			Assert.AreEqual(DateTimeKind.Utc, parsed.Kind, text);
			Sample? sample = UakJson.Deserialize<Sample>($"{{\"When\": \"{text}\"}}");
			Assert.AreEqual(expected, sample.When, text);
			Assert.AreEqual(DateTimeKind.Utc, sample.When!.Value.Kind, text);
		}
	}

	[TestMethod]
	public void MissingOrBrokenFilesReadAsNull()
	{
		Directory.CreateDirectory(_directory);
		Assert.IsNull(StateFiles.ReadShared(Path.Combine(_directory, "none.json")));
		Assert.IsNull(StateFiles.ReadJson<Sample>(Path.Combine(_directory, "none.json")));
		string broken = Path.Combine(_directory, "broken.json");
		File.WriteAllText(broken, "{ \"Name\": ");
		Assert.IsNull(StateFiles.ReadJson<Sample>(broken));
		File.WriteAllText(broken, "");
		Assert.IsNull(StateFiles.ReadJson<Sample>(broken));
	}

	[TestMethod]
	public void ReadsAFileAnotherHandleHoldsWithDeleteOnClose()
	{
		// As a lock ticket is held: written, shared for reading and deleting only, deleted on close.
		Directory.CreateDirectory(_directory);
		string path = Path.Combine(_directory, "ticket");
		using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1, FileOptions.DeleteOnClose))
		{
			stream.Write(Encoding.UTF8.GetBytes("{\"Name\":\"held\"}"));
			stream.Flush();
			Assert.AreEqual("held", StateFiles.ReadJson<Sample>(path)?.Name);
		}
		Assert.IsFalse(File.Exists(path));
	}

	[TestMethod]
	public void ReplacesAFileWhileAReaderHasItOpen()
	{
		string path = Path.Combine(_directory, "record.json");
		StateFiles.WriteJson(path, new Sample { Name = "old" });
		// On Windows a rename cannot replace a file that is open, whatever its sharing: the writer retries until the reader
		// closes it (readers hold state files for milliseconds).
		FileStream reader = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		Task close = Task.Run(async () =>
		{
			await Task.Delay(500, TestContext.CancellationToken);
			await reader.DisposeAsync();
		}, TestContext.CancellationToken);
		StateFiles.WriteJson(path, new Sample { Name = "new" });
		close.Wait(TestContext.CancellationToken);
		Assert.AreEqual("new", StateFiles.ReadJson<Sample>(path)?.Name);
	}

	[TestMethod]
	public async Task ConcurrentWritersAndReadersNeverSeeAPartialFile()
	{
		string path = Path.Combine(_directory, "busy.json");
		StateFiles.WriteJson(path, new Sample { Name = new string('x', 5000) });
		using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));
		int bad = 0;
		Task writer = Task.Run(() =>
		{
			for (int index = 0; !stop.IsCancellationRequested; index++)
			{
				StateFiles.WriteJson(path, new Sample { Name = new string((char)('a' + index % 26), 5000) });
			}
		}, TestContext.CancellationToken);
		Task reader = Task.Run(() =>
		{
			while (!stop.IsCancellationRequested)
			{
				Sample? sample = StateFiles.ReadJson<Sample>(path);
				if (sample is not null && (sample.Name.Length != 5000 || sample.Name.Distinct().Count() != 1))
				{
					Interlocked.Increment(ref bad);
				}
			}
		}, TestContext.CancellationToken);
		await Task.WhenAll(writer, reader);
		Assert.AreEqual(0, bad);
	}

	[TestMethod]
	public void TryDeleteIgnoresMissingFiles()
	{
		Assert.IsTrue(StateFiles.TryDelete(Path.Combine(_directory, "never.json")));
	}
}
