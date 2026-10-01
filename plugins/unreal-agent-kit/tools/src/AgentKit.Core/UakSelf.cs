// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Reflection;

namespace AgentKit.Core;

/// <summary>How to start this same `uak` again, for example to relaunch it detached (`uak runs _wrap`).</summary>
public static class UakSelf
{
	/// <summary>
	/// The program that runs this uak, and the arguments that come before uak's own: the apphost alone (uak.exe, or a
	/// self-contained publish), or dotnet followed by uak.dll when it runs as `dotnet uak.dll`.
	/// </summary>
	public static (string FileName, IReadOnlyList<string> PrefixArguments) GetCommand() =>
		GetCommand(Environment.ProcessPath, Assembly.GetEntryAssembly()?.Location);

	/// <summary>
	/// <see cref="GetCommand()"/> for a given process path and entry assembly path, for tests. The process is the dotnet muxer
	/// when its file name is "dotnet"; then the entry assembly follows it.
	/// </summary>
	public static (string FileName, IReadOnlyList<string> PrefixArguments) GetCommand(string? processPath, string? entryAssemblyPath)
	{
		if (string.IsNullOrEmpty(processPath))
		{
			throw new InvalidOperationException("The path of the running uak is unknown.");
		}
		bool IsMuxer = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
		if (IsMuxer)
		{
			if (string.IsNullOrEmpty(entryAssemblyPath))
			{
				throw new InvalidOperationException("uak runs under dotnet, but its assembly path is unknown.");
			}
			return (processPath, [entryAssemblyPath]);
		}
		return (processPath, []);
	}

	/// <summary>
	/// An invocation of this uak with the given arguments. Global options (-project=, -engine=, -vcs=) are not added: pass
	/// them first in <paramref name="arguments"/> when the child must resolve the same context.
	/// </summary>
	public static ProcessInvocation GetInvocation(IEnumerable<string> arguments, string? workingDirectory = null)
	{
		(string FileName, IReadOnlyList<string> Prefix) = GetCommand();
		return new ProcessInvocation(FileName, [.. Prefix, .. arguments], workingDirectory);
	}

	/// <summary>The global options that make a child uak resolve the same context: -project=, -engine= and -vcs=, where known.</summary>
	public static IReadOnlyList<string> GetGlobalOptions(UakContext context)
	{
		List<string> Options = [];
		if (context.ProjectFile is not null)
		{
			Options.Add("-project=" + context.ProjectFile.FullName);
		}
		if (context.EngineRoot is not null)
		{
			Options.Add("-engine=" + context.EngineRoot.FullName);
		}
		if (context.RequestedVersionControl is not null)
		{
			Options.Add("-vcs=" + context.RequestedVersionControl);
		}
		return Options;
	}
}
