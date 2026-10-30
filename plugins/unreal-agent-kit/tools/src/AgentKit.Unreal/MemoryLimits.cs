// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

namespace AgentKit.Unreal;

/// <summary>
/// UBT's limit on parallel actions by memory, for <c>uak compile -dependents</c>. UBT applies it only when no
/// -MaxParallelActions is given (UE 5.8, Engine/Source/Programs/UnrealBuildTool/System/Utils.cs,
/// GetMaxActionsToExecuteInParallel: "If non-zero then this is the number of actions that can be executed in parallel"),
/// so an agent's UAK_MAX_PARALLEL_ACTIONS turns it off, and a header with hundreds of dependents can then start more
/// compiles than the free memory holds. uak applies the same limit to the value it passes: at most one action per
/// <see cref="MemoryPerActionBytes"/> of free memory, and at least one.
/// </summary>
public static class MemoryLimits
{
	/// <summary>
	/// UBT's default memory per action, 1.5 GiB (UnrealBuildAcceleratorConfig.MemoryPerActionBytes, BuildConfiguration.xml's
	/// ParallelExecutor category). A target's larger MemoryPerActionGB raises it in UBT; uak can't read that, so a target
	/// that sets it is limited by UBT's default here.
	/// </summary>
	public const long MemoryPerActionBytes = 3L * 512 * 1024 * 1024;

	/// <summary>
	/// The free physical memory as UBT measures it (EpicGames.Core's SystemUtils.GetFreeMemoryBytes): the GC's total
	/// available memory minus its memory load; on Mac, the total, as UBT uses there. Null when it can't be read. A
	/// process that has not collected yet has no GC memory figures, so this collects once first.
	/// </summary>
	public static long? GetFreeMemoryBytes()
	{
		GCMemoryInfo Info = GC.GetGCMemoryInfo();
		if (Info.TotalAvailableMemoryBytes == 0)
		{
			GC.Collect();
			Info = GC.GetGCMemoryInfo();
		}
		if (Info.TotalAvailableMemoryBytes == 0)
		{
			return null;
		}
		return OperatingSystem.IsMacOS() ? Info.TotalAvailableMemoryBytes : Info.TotalAvailableMemoryBytes - Info.MemoryLoadBytes;
	}

	/// <summary>
	/// The -MaxParallelActions to pass: <paramref name="requested"/> limited to what <paramref name="freeBytes"/> affords,
	/// as UBT limits its own default (Convert.ToInt32(free / MemoryPerActionBytes), at least 1). With nothing requested, UBT's
	/// own default applies its memory limit itself, so this passes nothing either.
	/// </summary>
	public static int? Limit(int? requested, long? freeBytes)
	{
		if (requested is not int Requested || freeBytes is not long Free || Free < 0)
		{
			return requested;
		}
		long Afforded = Math.Max(1, Free / MemoryPerActionBytes);
		return (int)Math.Min(Requested, Afforded);
	}
}
