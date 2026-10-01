// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Diagnostics;

namespace AgentKit.Core.Tests;

[TestClass]
public sealed class ProcessIdentityTests
{
	/// <summary>A short-lived child process of the platform's shell.</summary>
	private static Process StartShortProcess()
	{
		ProcessStartInfo startInfo = OperatingSystem.IsWindows()
			? new ProcessStartInfo("cmd.exe", "/c exit 0")
			: new ProcessStartInfo("/bin/sh", "-c true");
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;
		return Process.Start(startInfo)!;
	}

	[TestMethod]
	public void CurrentProcessIsAliveWithItsStartTime()
	{
		ProcessIdentity current = ProcessIdentity.Current;
		Assert.AreEqual(Environment.ProcessId, current.Pid);
		Assert.IsNotNull(current.StartTimeUtc);
		Assert.AreEqual(DateTimeKind.Utc, current.StartTimeUtc.Value.Kind);
		Assert.IsLessThan(TimeSpan.FromMinutes(1), current.StartTimeUtc.Value - DateTime.UtcNow);
		Assert.IsTrue(current.IsAlive());
		Assert.AreEqual(current, ProcessIdentity.TryGet(Environment.ProcessId));
	}

	[TestMethod]
	public void ExitedProcessIsNotAlive()
	{
		ProcessIdentity identity;
		using (Process process = StartShortProcess())
		{
			identity = ProcessIdentity.TryGet(process.Id) ?? new ProcessIdentity(process.Id, null);
			process.WaitForExit();
		}
		Assert.IsFalse(identity.IsAlive());
	}

	[TestMethod]
	public void ReusedIdIsNotTheSameProcess()
	{
		// This process's ID with another start time stands for a process that has since reused the ID.
		DateTime start = ProcessIdentity.Current.StartTimeUtc!.Value;
		Assert.IsFalse(new ProcessIdentity(Environment.ProcessId, start.AddMinutes(-5)).IsAlive());
		Assert.IsTrue(new ProcessIdentity(Environment.ProcessId, start.AddMilliseconds(400)).IsAlive(), "Within the tolerance.");
		Assert.IsTrue(new ProcessIdentity(Environment.ProcessId, null).IsAlive(), "An unknown start matches any.");
	}

	[TestMethod]
	public void InvalidIdsAreNotRunning()
	{
		Assert.IsNull(ProcessIdentity.TryGet(0));
		Assert.IsNull(ProcessIdentity.TryGet(-1));
		Assert.IsFalse(new ProcessIdentity(int.MaxValue - 1, null).IsAlive());
	}

	[TestMethod]
	public void StartTimesMatchWithinTheTolerance()
	{
		DateTime time = new(2026, 10, 1, 6, 5, 0, DateTimeKind.Utc);
		Assert.IsTrue(ProcessIdentity.StartTimesMatch(time, time));
		Assert.IsTrue(ProcessIdentity.StartTimesMatch(time, time.AddMilliseconds(-999)));
		Assert.IsFalse(ProcessIdentity.StartTimesMatch(time, time.AddSeconds(2)));
		Assert.IsTrue(ProcessIdentity.StartTimesMatch(null, time));
		Assert.IsTrue(ProcessIdentity.StartTimesMatch(time, null));
		StringAssert.Contains(new ProcessIdentity(42, time).ToString(), "PID 42 (started 2026-10-01T06:05:00");
	}
}
