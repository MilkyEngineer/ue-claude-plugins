// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentKit.Runs;

/// <summary>
/// Keeps the machine awake while a detached run goes (DESIGN.md, "Detached runs"). A laptop that sleeps, or enters Modern
/// Standby, under a long build or a GPU-heavy editor run can lose the run, or stop the machine. On Windows this asks for the
/// system and the display to stay on (<c>SetThreadExecutionState</c>), from a thread of its own: Windows ties the request to
/// the thread that made it, and an async continuation may move between pool threads. Windows drops the request when that
/// thread ends, so it also ends with the process. Elsewhere it does nothing yet.
/// </summary>
public sealed class KeepAwake : IDisposable
{
	private readonly Thread? _thread;
	private readonly ManualResetEventSlim _release = new(false);

	/// <summary>Whether the request was granted: false when it was not asked for, refused, or not supported on this platform.</summary>
	public bool IsActive { get; }

	private KeepAwake(bool keepAwake)
	{
		if (!keepAwake || !OperatingSystem.IsWindows())
		{
			// TODO(unix): untested platforms; caffeinate (Mac) or systemd-inhibit (Linux) would do this.
			return;
		}
		(_thread, IsActive) = StartOnWindows(_release);
	}

	/// <summary>Starts the request's thread, which asks, then holds the request until <paramref name="release"/>. Returns it, and whether it was granted.</summary>
	[SupportedOSPlatform("windows")]
	private static (Thread Thread, bool Granted) StartOnWindows(ManualResetEventSlim release)
	{
		using ManualResetEventSlim requested = new(false);
		bool granted = false;
		Thread thread = new(() =>
		{
			granted = WindowsPower.Request(true);
			requested.Set();
			release.Wait();
			if (granted)
			{
				WindowsPower.Request(false);
			}
		})
		{
			IsBackground = true,
			Name = "uak keep-awake",
		};
		thread.Start();
		requested.Wait();
		return (thread, granted);
	}

	/// <summary>Starts keeping the machine awake, when <paramref name="keepAwake"/> is true. Dispose to stop.</summary>
	public static KeepAwake Begin(bool keepAwake)
	{
		return new KeepAwake(keepAwake);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		_release.Set();
		_thread?.Join();
		_release.Dispose();
	}

	[SupportedOSPlatform("windows")]
	private static class WindowsPower
	{
		private const uint ES_CONTINUOUS = 0x80000000;
		private const uint ES_SYSTEM_REQUIRED = 0x00000001;
		private const uint ES_DISPLAY_REQUIRED = 0x00000002;

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern uint SetThreadExecutionState(uint esFlags);

		/// <summary>
		/// Asks for the system and the display to stay on until <c>Request(false)</c> on the same thread, or the thread's end.
		/// The display too: on a Modern Standby machine, the display turning off is what starts standby.
		/// </summary>
		public static bool Request(bool on)
		{
			return SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS) != 0;
		}
	}
}
