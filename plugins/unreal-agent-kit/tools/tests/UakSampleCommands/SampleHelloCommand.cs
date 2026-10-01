// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace UakSampleCommands;

/// <summary>A project command: prints a greeting and its arguments, so tests can see it was found and run.</summary>
public sealed class SampleHelloCommand : IUakCommand
{
	/// <inheritdoc/>
	public string Name => "sample hello";

	/// <inheritdoc/>
	public string Summary => "Says hello (a sample project command).";

	/// <inheritdoc/>
	public string Usage => "uak sample hello [words...]";

	/// <inheritdoc/>
	public bool RequiresEngine => false;

	/// <inheritdoc/>
	public Task<int> RunAsync(UakContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		Console.Out.WriteLine("hello from the sample: " + string.Join(' ', arguments));
		return Task.FromResult(arguments.Count > 0 && arguments[0] == "fail" ? UakExitCodes.Failure : UakExitCodes.Success);
	}
}
