// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Runs;

/// <summary>
/// A short critical section per run name, so that checking a name is free and recording the run that takes it is one step:
/// <c>&lt;State&gt;/Runs/&lt;Name&gt;.claim</c>, created create-new and deleted when closed. On Windows the file also goes when its
/// process dies; elsewhere a claim whose process has died is removed by the next claimer.
/// </summary>
internal sealed class RunClaim : IDisposable
{
	/// <summary>The claim file's extension.</summary>
	public const string Extension = ".claim";

	/// <summary>How long a claimer waits for another claim of the same name, which is held only for a few file operations.</summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

	private readonly FileStream _stream;

	private RunClaim(FileStream stream)
	{
		_stream = stream;
	}

	/// <summary>What a claim file holds: who claims the name.</summary>
	private sealed class ClaimInfo
	{
		public int Pid { get; set; }

		public DateTime? ProcessStart { get; set; }
	}

	/// <summary>Takes the claim on a name, waiting while another process holds it.</summary>
	/// <exception cref="RunStartException">Another process held the claim for longer than <paramref name="timeout"/>.</exception>
	public static RunClaim Acquire(RunRegistry registry, string name, TimeSpan? timeout = null)
	{
		ArgumentNullException.ThrowIfNull(registry);
		Directory.CreateDirectory(registry.Directory);
		string path = Path.Combine(registry.Directory, name + Extension);
		ProcessIdentity self = ProcessIdentity.Current;
		byte[] content = UakJson.SerializeToUtf8(new ClaimInfo { Pid = self.Pid, ProcessStart = self.StartTimeUtc });
		DateTime deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
		while (true)
		{
			FileStream stream;
			try
			{
				stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1, FileOptions.DeleteOnClose);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Taken (or, on Windows, being deleted). A claim left by a process that died is stale: remove it.
				// TODO(unix): two claimers that both find the same stale claim can race on removing it; it needs a crash first.
				ClaimInfo? holder = StateFiles.ReadJson<ClaimInfo>(path);
				if (holder is not null && !new ProcessIdentity(holder.Pid, holder.ProcessStart).IsAlive())
				{
					StateFiles.TryDelete(path);
					continue;
				}
				if (DateTime.UtcNow > deadline)
				{
					throw new RunStartException($"Run '{name}' is being recorded by another process (PID {holder?.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}): {path}.", exception);
				}
				Thread.Sleep(20);
				continue;
			}
			try
			{
				stream.Write(content);
				stream.Flush();
			}
			catch
			{
				stream.Dispose();
				throw;
			}
			return new RunClaim(stream);
		}
	}

	/// <summary>Releases the claim: the file is deleted.</summary>
	public void Dispose()
	{
		_stream.Dispose();
	}
}
