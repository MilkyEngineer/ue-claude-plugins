// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Text.Json;

namespace AgentKit.Core;

/// <summary>
/// Reading and writing the kit's state files (lock tickets, holder.json, run records), which several processes read while
/// others replace them. Every file is UTF-8 without a BOM; JSON uses <see cref="UakJson.Options"/>.
/// </summary>
public static class StateFiles
{
	/// <summary>How long a writer keeps retrying to replace a file that a reader holds open without delete sharing.</summary>
	private static readonly TimeSpan s_replaceTimeout = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Writes a file whole: to a temporary file beside it, then renamed over the old one, so a reader sees the old file or the
	/// new one and never a part. Creates the directory. Retries for a few seconds while a reader holds the old file open
	/// without delete sharing.
	/// </summary>
	public static void WriteText(string path, string text)
	{
		WriteBytes(path, UakJson.Utf8NoBom.GetBytes(text));
	}

	/// <summary>Writes a value as JSON (<see cref="UakJson.SerializeToUtf8"/>), whole (see <see cref="WriteText"/>).</summary>
	public static void WriteJson<T>(string path, T value)
	{
		WriteBytes(path, UakJson.SerializeToUtf8(value));
	}

	private static void WriteBytes(string path, byte[] bytes)
	{
		string fullPath = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		string temp = $"{fullPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
		File.WriteAllBytes(temp, bytes);
		DateTime deadline = DateTime.UtcNow + s_replaceTimeout;
		try
		{
			while (true)
			{
				try
				{
					File.Move(temp, fullPath, overwrite: true);
					return;
				}
				catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && DateTime.UtcNow < deadline)
				{
					Thread.Sleep(20);
				}
			}
		}
		finally
		{
			if (File.Exists(temp))
			{
				TryDelete(temp);
			}
		}
	}

	/// <summary>
	/// Reads a text file that another process may hold open, even for writing or with delete-on-close: it opens with read,
	/// write and delete sharing. A BOM, if any, is skipped. Null when the file is missing or cannot be read.
	/// </summary>
	public static string? ReadShared(string path)
	{
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader reader = new(stream, UakJson.Utf8NoBom, detectEncodingFromByteOrderMarks: true);
			return reader.ReadToEnd();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Reads a JSON file (see <see cref="ReadShared"/>). Null when it is missing, unreadable, or not valid JSON for T.</summary>
	public static T? ReadJson<T>(string path) where T : class
	{
		string? text = ReadShared(path);
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		try
		{
			return UakJson.Deserialize<T>(text);
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException)
		{
			return null;
		}
	}

	/// <summary>Deletes a file, ignoring a missing file and any failure. Returns whether it is gone.</summary>
	public static bool TryDelete(string path)
	{
		try
		{
			File.Delete(path);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return !File.Exists(path);
		}
	}
}
