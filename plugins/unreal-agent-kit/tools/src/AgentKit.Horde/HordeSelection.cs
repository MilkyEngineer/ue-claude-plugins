// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Horde;

/// <summary>A Perforce stream in the workspace's chain: the client's stream, then its parents.</summary>
/// <param name="Name">The stream, such as "//Project/Dev".</param>
/// <param name="Type">Its type: mainline, development, release, virtual, task...</param>
public sealed record PerforceStreamLink(string Name, string Type)
{
	/// <summary>Whether it is a virtual stream, whose files are its parent's.</summary>
	public bool IsVirtual => Type.Equals("virtual", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Chooses the Horde stream and template for a preflight (DESIGN.md, "Horde").</summary>
public static class HordeSelection
{
	/// <summary>
	/// The Horde stream for a Perforce stream, matched as Horde's dashboard does (Preflight.tsx): a stream whose name is the
	/// Perforce stream (ignoring case); else one whose id is the name without "//", with "/" as "-", in lower case; else one
	/// named the stream plus "-VS". Null when none matches.
	/// </summary>
	public static HordeStream? Match(IReadOnlyList<HordeStream> streams, string perforceStream)
	{
		string id = perforceStream.TrimStart('/').Replace('/', '-').ToLowerInvariant();
		return streams.FirstOrDefault(stream => stream.Name.Equals(perforceStream, StringComparison.OrdinalIgnoreCase))
			?? streams.FirstOrDefault(stream => stream.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
			?? streams.FirstOrDefault(stream => stream.Name.Equals(perforceStream + "-VS", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// The Horde stream for the workspace: the first stream of the chain (client stream first) that Horde knows. The walk goes up
	/// through virtual streams and stops at the first stream that isn't virtual: a shelf holds that stream's depot paths, so a
	/// stream further up would not see the shelved files. Throws <see cref="UakUsageException"/> naming the streams tried.
	/// </summary>
	public static (HordeStream Stream, string PerforceStream) FromChain(IReadOnlyList<HordeStream> streams, IReadOnlyList<PerforceStreamLink> chain)
	{
		foreach (PerforceStreamLink link in chain)
		{
			HordeStream? match = Match(streams, link.Name);
			if (match is not null)
			{
				return (match, link.Name);
			}
			if (!link.IsVirtual)
			{
				break;
			}
		}
		string tried = string.Join(", ", chain.TakeWhile((link, index) => index == 0 || chain[index - 1].IsVirtual).Select(link => link.Name));
		throw new UakUsageException($"No Horde stream builds {tried} (or you can't see it). Pass -stream=<Horde stream id>; uak horde streams lists them.");
	}

	/// <summary>
	/// The template: <paramref name="requested"/> (an id or a name, ignoring case) when given, which must allow preflights;
	/// else the stream's default preflight template; else its only template that allows preflights and that the user can run.
	/// Throws <see cref="UakUsageException"/> listing the candidates otherwise.
	/// </summary>
	public static HordeTemplate ChooseTemplate(HordeStream stream, string? requested)
	{
		List<HordeTemplate> candidates = stream.Templates.Where(template => template.AllowPreflights).ToList();
		string list = candidates.Count == 0 ? "none" : string.Join(", ", candidates.Select(template => $"{template.Id} ({template.Name})"));
		if (requested is not null)
		{
			HordeTemplate? template = stream.Templates.FirstOrDefault(template => template.Id.Equals(requested, StringComparison.OrdinalIgnoreCase))
				?? stream.Templates.FirstOrDefault(template => template.Name.Equals(requested, StringComparison.OrdinalIgnoreCase))
				?? throw new UakUsageException($"Stream {stream.Id} has no template '{requested}'. Templates that allow preflights: {list}.");
			return template.AllowPreflights ? template : throw new UakUsageException($"Template {template.Id} ({template.Name}) does not allow preflights in stream {stream.Id}. Templates that do: {list}.");
		}
		if (stream.DefaultPreflightTemplate is not null)
		{
			HordeTemplate? fallback = stream.Templates.FirstOrDefault(template => template.Id.Equals(stream.DefaultPreflightTemplate, StringComparison.OrdinalIgnoreCase));
			if (fallback is not null)
			{
				return fallback;
			}
		}
		List<HordeTemplate> runnable = candidates.Where(template => template.CanRun).ToList();
		return runnable.Count == 1
			? runnable[0]
			: throw new UakUsageException($"Stream {stream.Id} has {(runnable.Count == 0 ? "no" : "several")} preflight templates you can run, and no default. Pass -template=<id or name>. Templates that allow preflights: {list}.");
	}
}
