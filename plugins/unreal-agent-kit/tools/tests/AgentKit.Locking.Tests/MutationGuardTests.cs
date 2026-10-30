// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Locking;

namespace AgentKit.Tests;

[TestClass]
public sealed class MutationGuardTests
{
	[TestMethod]
	public async Task LockRunRefusesWhileAStoppedMutationRunLeftItsPatch()
	{
		using TempState State = new();
		string Source = State.PathOf("Foo.cpp");
		File.WriteAllText(Source, "original\n");
		MutationJournal Journal = MutationJournal.Begin(State.Context, "M", "p.diff", [Source]);
		File.WriteAllText(Source, "mutant\n");
		Journal.MarkApplied();
		// As if that uak had been killed.
		Journal.Record.Pid = int.MaxValue;
		StateFiles.WriteJson(Path.Combine(Journal.Directory, "journal.json"), Journal.Record);

		UakUsageException Refused = await Assert.ThrowsExactlyAsync<UakUsageException>(() => new LockRunCommand().RunAsync(State.Context, ["-name=Guarded", "--", "never-started"], CancellationToken.None));

		Assert.Contains("uak mutate -restore -name=M", Refused.Message);
		Assert.AreEqual("mutant\n", File.ReadAllText(Source));
	}
}
