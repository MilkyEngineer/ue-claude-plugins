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
	public void SurelyAliveNeedsBothStartTimesKnown()
	{
		DateTime start = ProcessIdentity.Current.StartTimeUtc!.Value;
		Assert.IsTrue(new ProcessIdentity(Environment.ProcessId, start).IsSurelyAlive());
		Assert.IsFalse(new ProcessIdentity(Environment.ProcessId, null).IsSurelyAlive(), "An unknown recorded start never matches.");
		Assert.IsFalse(new ProcessIdentity(Environment.ProcessId, start.AddMinutes(-5)).IsSurelyAlive());
		Assert.IsFalse(new ProcessIdentity(int.MaxValue - 1, start).IsSurelyAlive());
		if (OperatingSystem.IsWindows() && ProcessIdentity.TryGet(4) is { StartTimeUtc: null } system)
		{
			// The System process: it runs, but its start time can't be read, as for a protected process that reuses an ID.
			Assert.IsTrue(new ProcessIdentity(system.Pid, start).IsAlive());
			Assert.IsFalse(new ProcessIdentity(system.Pid, start).IsSurelyAlive());
		}
	}

	[TestMethod]
	public void InvalidIdsAreNotRunning()
	{
		Assert.IsNull(ProcessIdentity.TryGet(0));
		Assert.IsNull(ProcessIdentity.TryGet(-1));
		Assert.IsFalse(new ProcessIdentity(int.MaxValue - 1, null).IsAlive());
	}

	[TestMethod]
	[DataRow("12 (UnrealBuildTool) Z 1 12 12", true)]
	[DataRow("12 (a) b) Z (c) Z 1 12", true)]
	[DataRow("12 (dotnet) S 1 12 12", false)]
	[DataRow("12 (Z) R 1 12 12", false)]
	[DataRow("12 (cut", false)]
	[DataRow("12 (end)", false)]
	public void AZombieIsReadFromTheStateAfterTheName(string stat, bool zombie)
	{
		Assert.AreEqual(zombie, ProcessIdentity.IsZombieStat(stat));
	}

	[TestMethod]
	public void AWiderToleranceTakesAStartTimeTheDefaultDoesNot()
	{
		DateTime start = ProcessIdentity.Current.StartTimeUtc!.Value.AddSeconds(30);
		ProcessIdentity stepped = new(Environment.ProcessId, start);

		Assert.IsFalse(stepped.IsAlive());
		Assert.IsTrue(stepped.IsAlive(TimeSpan.FromSeconds(60)));
		Assert.IsFalse(stepped.IsAlive(TimeSpan.FromSeconds(10)));
		Assert.IsTrue(ProcessIdentity.StartTimesMatch(start, start.AddSeconds(59), TimeSpan.FromSeconds(60)));
		Assert.IsFalse(ProcessIdentity.StartTimesMatch(start, start.AddSeconds(61), TimeSpan.FromSeconds(60)));
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
