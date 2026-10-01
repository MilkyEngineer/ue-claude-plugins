// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;

namespace AgentKit.Vcs.Tests;

/// <summary>A unique directory under the system temp folder, deleted on dispose.</summary>
internal class TempDirectory : IDisposable
{
	public TempDirectory()
	{
		// Resolve the long, canonical form so paths compare equal to what git and the file system report.
		string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uak-vcs-tests", Guid.NewGuid().ToString("N")[..12]);
		System.IO.Directory.CreateDirectory(path);
		Path = new DirectoryInfo(path).FullName;
	}

	public string Path { get; }

	public DirectoryInfo Directory => new(Path);

	/// <summary>Writes a file (creating its directories) and returns its absolute path.</summary>
	public string Write(string relativePath, string content = "content\n")
	{
		string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
		File.WriteAllText(fullPath, content);
		return fullPath;
	}

	public string Combine(string relativePath) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));

	public virtual void Dispose()
	{
		try
		{
			// git marks object files read-only.
			foreach (string file in System.IO.Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}
			System.IO.Directory.Delete(Path, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}

/// <summary>A real git repository in a temp directory, made with the git command line.</summary>
internal sealed class TempGitRepository : TempDirectory
{
	public TempGitRepository()
	{
		Git("init", "-q", "-b", "main");
		Git("config", "user.name", "UAK Tests");
		Git("config", "user.email", "uak-tests@example.invalid");
		Git("config", "commit.gpgsign", "false");
		Git("config", "core.autocrlf", "false");
	}

	/// <summary>Runs git in the repository and returns its trimmed standard output; fails the test on a non-zero exit.</summary>
	public string Git(params string[] arguments)
	{
		ProcessStartInfo startInfo = new("git")
		{
			WorkingDirectory = Path,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		using Process process = Process.Start(startInfo)!;
		string output = process.StandardOutput.ReadToEnd();
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {error}");
		}
		return output.Trim();
	}

	/// <summary>Stages everything and commits.</summary>
	public string Commit(string message = "commit")
	{
		Git("add", "-A");
		Git("commit", "-q", "-m", message);
		return Git("rev-parse", "HEAD");
	}
}
