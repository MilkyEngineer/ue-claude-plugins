// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Runs.Tests;

[TestClass]
public sealed class KeepAwakeTests
{
	[TestMethod]
	public void NotAskedForIsInactive()
	{
		using KeepAwake awake = KeepAwake.Begin(false);
		Assert.IsFalse(awake.IsActive);
	}

	[TestMethod]
	public void GrantedOnWindowsAndReleasedOnDispose()
	{
		KeepAwake first = KeepAwake.Begin(true);
		// Requests are per thread, so two at once are independent.
		using (KeepAwake second = KeepAwake.Begin(true))
		{
			Assert.AreEqual(OperatingSystem.IsWindows(), first.IsActive, "Windows grants the request; other platforms do nothing yet.");
			Assert.AreEqual(OperatingSystem.IsWindows(), second.IsActive);
		}
		// Dispose returns once the request's thread has released it and ended.
		Task dispose = Task.Run(first.Dispose);
		Assert.IsTrue(dispose.Wait(TimeSpan.FromSeconds(10)), "Dispose returns promptly.");
	}
}
