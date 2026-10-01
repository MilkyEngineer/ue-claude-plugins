// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Vcs;

/// <summary>Parses <c>git status --porcelain=v2 -z</c> output.</summary>
internal static class GitStatusParser
{
	/// <summary>
	/// Parses the output into statuses with absolute paths under <paramref name="rootDirectory"/>. Header lines ("# ...") are skipped.
	/// Porcelain paths are always relative to the repository root, whatever the working directory.
	/// </summary>
	public static List<VcsFileStatus> Parse(string output, string rootDirectory)
	{
		List<VcsFileStatus> result = [];
		string[] entries = output.Split('\0');
		for (int index = 0; index < entries.Length; index++)
		{
			string entry = entries[index];
			if (entry.Length < 2)
			{
				continue;
			}
			switch (entry[0])
			{
				case '1':
				{
					// 1 XY sub mH mI mW hH hI <path>
					string[] fields = entry.Split(' ', 9);
					if (fields.Length == 9)
					{
						result.Add(new VcsFileStatus(ToFullPath(rootDirectory, fields[8]), ClassifyOrdinary(fields[1]), null, fields[1]));
					}
					break;
				}
				case '2':
				{
					// 2 XY sub mH mI mW hH hI X<score> <path>, then the original path as the next NUL-separated field.
					string[] fields = entry.Split(' ', 10);
					string? original = index + 1 < entries.Length ? entries[++index] : null;
					if (fields.Length == 10)
					{
						VcsFileState state = fields[8].StartsWith('C') ? VcsFileState.Copied : VcsFileState.Renamed;
						if (fields[1][1] == 'D')
						{
							state = VcsFileState.Deleted;
						}
						result.Add(new VcsFileStatus(ToFullPath(rootDirectory, fields[9]), state, original is null ? null : ToFullPath(rootDirectory, original), fields[1]));
					}
					break;
				}
				case 'u':
				{
					// u XY sub m1 m2 m3 mW h1 h2 h3 <path>
					string[] fields = entry.Split(' ', 11);
					if (fields.Length == 11)
					{
						result.Add(new VcsFileStatus(ToFullPath(rootDirectory, fields[10]), VcsFileState.Conflicted, null, fields[1]));
					}
					break;
				}
				case '?':
					result.Add(new VcsFileStatus(ToFullPath(rootDirectory, entry[2..]), VcsFileState.Untracked, null, "??"));
					break;
				case '!':
					result.Add(new VcsFileStatus(ToFullPath(rootDirectory, entry[2..]), VcsFileState.Ignored, null, "!!"));
					break;
				default:
					// "# branch.*" headers and anything newer that we do not understand.
					break;
			}
		}
		return result;
	}

	/// <summary>
	/// Classifies an ordinary entry's XY code. A deletion in the work tree wins, since the file is gone; otherwise the staged
	/// change (X) wins over the work-tree change (Y), so a staged new file that was then edited is still Added.
	/// </summary>
	internal static VcsFileState ClassifyOrdinary(string xy)
	{
		if (xy.Length != 2)
		{
			return VcsFileState.Modified;
		}
		if (xy[1] == 'D')
		{
			return VcsFileState.Deleted;
		}
		return Classify(xy[0] != '.' ? xy[0] : xy[1]);
	}

	static VcsFileState Classify(char code) => code switch
	{
		'A' => VcsFileState.Added,
		'D' => VcsFileState.Deleted,
		'R' => VcsFileState.Renamed,
		'C' => VcsFileState.Copied,
		'T' => VcsFileState.TypeChanged,
		'U' => VcsFileState.Conflicted,
		_ => VcsFileState.Modified,
	};

	/// <summary>Turns a repository-relative path with '/' separators into an absolute native path.</summary>
	internal static string ToFullPath(string rootDirectory, string relativePath)
		=> Path.GetFullPath(Path.Combine(rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
