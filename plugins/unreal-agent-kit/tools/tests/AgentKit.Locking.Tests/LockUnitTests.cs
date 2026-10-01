// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;
using AgentKit.Tests;

namespace AgentKit.Locking.Tests;

[TestClass]
public sealed class LockUnitTests
{
	public TestContext TestContext { get; set; } = null!;

	[TestMethod]
	public void TicketFileNamesRoundTripAndSortByPriorityThenArrival()
	{
		ProcessIdentity process = new(1234, new DateTime(2026, 10, 1, 6, 5, 0, DateTimeKind.Utc));
		string high = LockQueue.TicketFileName(LockPriority.High, 900, process);
		string normalEarly = LockQueue.TicketFileName(LockPriority.Normal, 100, process);
		string normalLate = LockQueue.TicketFileName(LockPriority.Normal, 200, new ProcessIdentity(99, null));
		Assert.AreEqual($"0-0000000000000000900-1234-{process.StartTimeUtc!.Value.Ticks}.ticket", high);

		Assert.IsTrue(LockQueue.TryParseFileName(high, out LockPriority priority, out long sequence, out ProcessIdentity parsed));
		Assert.AreEqual(LockPriority.High, priority);
		Assert.AreEqual(900, sequence);
		Assert.AreEqual(process, parsed);
		Assert.IsTrue(LockQueue.TryParseFileName(normalLate, out _, out _, out ProcessIdentity unknownStart));
		Assert.IsNull(unknownStart.StartTimeUtc);

		string[] names = [normalLate, high, normalEarly];
		Array.Sort(names, StringComparer.Ordinal);
		CollectionAssert.AreEqual(new[] { high, normalEarly, normalLate }, names);

		Assert.IsFalse(LockQueue.TryParseFileName("holder.json", out _, out _, out _));
		Assert.IsFalse(LockQueue.TryParseFileName("2-0000000000000000900-1-0.ticket", out _, out _, out _));
		Assert.IsFalse(LockQueue.TryParseFileName("1-900-1-0.ticket", out _, out _, out _));
	}

	[TestMethod]
	public void NameAndPriorityDefaultsComeFromTheEnvironment()
	{
		string? oldName = Environment.GetEnvironmentVariable(EditorLock.NameVariable);
		string? oldPriority = Environment.GetEnvironmentVariable(EditorLock.PriorityVariable);
		try
		{
			Environment.SetEnvironmentVariable(EditorLock.NameVariable, null);
			Environment.SetEnvironmentVariable(EditorLock.PriorityVariable, null);
			Assert.AreEqual("build", EditorLock.ResolveName("build"));
			Assert.IsFalse(string.IsNullOrEmpty(EditorLock.ResolveName(null)));
			Assert.AreEqual(LockPriority.Normal, EditorLock.ResolvePriority(null));
			Assert.AreEqual(LockPriority.High, EditorLock.ResolvePriority(LockPriority.High));

			Environment.SetEnvironmentVariable(EditorLock.NameVariable, "W3-Verify");
			Environment.SetEnvironmentVariable(EditorLock.PriorityVariable, "high");
			Assert.AreEqual("W3-Verify/build", EditorLock.ResolveName("build"));
			Assert.AreEqual("W3-Verify", EditorLock.ResolveName(null));
			Assert.AreEqual(LockPriority.High, EditorLock.ResolvePriority(null));
			Assert.AreEqual(LockPriority.Normal, EditorLock.ResolvePriority(LockPriority.Normal), "An explicit priority wins.");

			Environment.SetEnvironmentVariable(EditorLock.PriorityVariable, "urgent");
			Assert.AreEqual(LockPriority.Normal, EditorLock.ResolvePriority(null), "An unknown value is ignored.");
		}
		finally
		{
			Environment.SetEnvironmentVariable(EditorLock.NameVariable, oldName);
			Environment.SetEnvironmentVariable(EditorLock.PriorityVariable, oldPriority);
		}
	}

	[TestMethod]
	public void CommandArgumentsSplitOptionsFromTheCommand()
	{
		UakArguments parsed = new(["-Name=x", "-priority=high", "--", "cmd", "-name=y", "a b", "--"]);
		Assert.AreEqual("x", parsed.GetString("name"));
		Assert.AreEqual(LockPriority.High, CommandArguments.GetPriority(parsed));
		CollectionAssert.AreEqual(new[] { "cmd", "-name=y", "a b", "--" }, CommandArguments.GetCommand(parsed).ToArray());
		parsed.ThrowIfUnknown();

		UakArguments bare = new(["git", "status"]);
		CollectionAssert.AreEqual(new[] { "git", "status" }, CommandArguments.GetCommand(bare).ToArray());

		Assert.ThrowsExactly<UakUsageException>(() => CommandArguments.GetPriority(new UakArguments(["-priority=Urgent"])));
		UakArguments stray = new(["stray", "--", "cmd"]);
		Assert.ThrowsExactly<UakUsageException>(() => CommandArguments.GetCommand(stray));
	}

	[TestMethod]
	public void CommandsAreFoundByTheirNames()
	{
		Assert.AreEqual("lock run", new LockRunCommand().Name);
		Assert.AreEqual("lock status", new LockStatusCommand().Name);
		Assert.IsFalse(((IUakCommand)new LockRunCommand()).RequiresEngine);
		Assert.IsFalse(((IUakCommand)new LockStatusCommand()).RequiresEngine);
	}

	[TestMethod]
	public async Task LockRunHoldsTheLockAroundTheCommandAndPassesItsExitCode()
	{
		using TempState state = new();
		string output = state.PathOf("args.json");
		Task<int> run = new LockRunCommand().RunAsync(state.Context,
			["-name=Runner", "-priority=High", "--", Child.ExecutablePath, "sleep", "1500", "3"], TestContext.CancellationToken);
		Wait.Until(() => EditorLock.GetStatus(state.Context).Holder?.Name == "Runner", TimeSpan.FromSeconds(30), "lock run to hold the lock");
		EditorLockStatus status = EditorLock.GetStatus(state.Context);
		Assert.AreEqual(LockPriority.High, status.Holder!.Priority);
		StringAssert.Contains(status.Holder.Command, "sleep 1500 3");
		Assert.AreEqual(3, await run);
		Assert.IsNull(EditorLock.GetStatus(state.Context).Holder);
	}

	[TestMethod]
	public async Task LockRunRejectsBadUsage()
	{
		using TempState state = new();
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new LockRunCommand().RunAsync(state.Context, ["-name=x"], TestContext.CancellationToken));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new LockRunCommand().RunAsync(state.Context, ["-bogus=1", "--", "x"], TestContext.CancellationToken));
		await Assert.ThrowsExactlyAsync<UakUsageException>(() => new LockStatusCommand().RunAsync(state.Context, ["extra"], TestContext.CancellationToken));
	}

	[TestMethod]
	public void StatusFormatsAnEmptyLock()
	{
		using TempState state = new();
		string text = LockStatusCommand.Format(EditorLock.GetStatus(state.Context), DateTime.UtcNow);
		StringAssert.Contains(text, "Held by   nobody");
		StringAssert.Contains(text, "Queue     empty");
		Assert.AreEqual("1h 02m", LockStatusCommand.FormatSpan(TimeSpan.FromMinutes(62.5)));
		Assert.AreEqual("3m 05s", LockStatusCommand.FormatSpan(TimeSpan.FromSeconds(185)));
		Assert.AreEqual("0s", LockStatusCommand.FormatSpan(TimeSpan.FromSeconds(-5)));
	}
}
