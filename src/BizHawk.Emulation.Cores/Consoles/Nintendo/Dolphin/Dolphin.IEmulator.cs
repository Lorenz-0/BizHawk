using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

using BizHawk.Common;
using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin : IEmulator
	{
		private readonly BasicServiceProvider _serviceProvider;

		/// <summary>
		/// libdolphin's Dolphin_FrameStep spin-waits (while (Core::IsFrameStepping()) / while (CPU::IsCPUActive()),
		/// MainNoGUI.cpp) with NO timeout, on the calling thread; when the emulated CPU thread never goes
		/// inactive (it throttles on the GPU FIFO/present path), that wait freezes the caller forever -
		/// which is exactly the whole-client hang seen while recording. running the step on a dedicated
		/// thread turns a wedge into a catchable TimeoutException instead.
		/// </summary>
		private const int CoreCallTimeoutMs = 10000;

		private volatile bool _coreWedged;

		private sealed class CoreRunner
		{
			private sealed class Job
			{
				public Action Work;
				public Exception Error;
				public readonly ManualResetEventSlim Done = new();
			}

			private readonly BlockingCollection<Job> _jobs = new();

			private Job _lastJob;
			private int _osThreadId;

			/// <summary>OS thread id of the step thread (Windows only; 0 elsewhere), so diagnostics can identify it in thread dumps</summary>
			public int OsThreadId => _osThreadId;

			public CoreRunner()
			{
				new Thread(() =>
				{
					if (!OSTailoredCode.IsUnixHost) _osThreadId = unchecked((int) GetCurrentThreadId());
					foreach (var j in _jobs.GetConsumingEnumerable())
					{
						try
						{
							j.Work();
						}
						catch (Exception e)
						{
							j.Error = e;
						}
						finally
						{
							j.Done.Set();
						}
					}
				})
				{ IsBackground = true, Name = "Dolphin core step" }.Start();
			}

			public void Run(Action work, int timeoutMs)
			{
				var j = new Job { Work = work };
				_lastJob = j;
				_jobs.Add(j);
				if (!j.Done.Wait(timeoutMs))
				{
					throw new TimeoutException("libdolphin stopped responding inside Dolphin_FrameStep (unbounded native spin-wait on the CPU thread); the step thread remains wedged");
				}

				if (j.Error is not null) throw new Exception("Dolphin core call failed", j.Error);
			}

			/// <summary>
			/// Hard-suspends the step thread when it is wedged inside libdolphin's spin-waits: their exit
			/// condition (the native s_frame_step flag) is only ever cleared by the NEXT core boot, so
			/// without this the thread would burn a CPU core forever - and after a forced core teardown it
			/// could even escape the second spin into freed native audio state. On Windows the spin bodies
			/// are pure atomic reads (TRY_CALLBACK is a no-op there), so suspending inside them is safe;
			/// the thread is never resumed.
			/// </summary>
			public void TrySuspendWedged()
			{
				if (OSTailoredCode.IsUnixHost) return; // on Linux the spins take a native job-queue lock, and the wedge has only been observed on Windows
				var j = _lastJob;
				if (j is null || j.Done.IsSet || _osThreadId == 0) return; // the step finished on its own; nothing to freeze
				var hThread = OpenThread(THREAD_SUSPEND_RESUME, bInheritHandle: false, unchecked((uint) _osThreadId));
				if (hThread == IntPtr.Zero) return;
				try
				{
					// SuspendThread reports (DWORD)-1 on failure; if the suspend didn't take, the step
					// thread simply keeps spinning, which is exactly the pre-existing behavior
					if (SuspendThread(hThread) == uint.MaxValue) return;
				}
				finally
				{
					CloseHandle(hThread);
				}
			}
		}

		private readonly CoreRunner _runner = new();

		/// <summary>OS thread id of the thread hosting Dolphin_Main (Windows only; 0 elsewhere), so diagnostics can identify it in thread dumps</summary>
		public int NativeHostThreadId => _hostThreadId;

		/// <summary>OS thread id of the core step thread (Windows only; 0 elsewhere)</summary>
		public int RunnerThreadId => _runner.OsThreadId;

		/// <summary>
		/// Last resort once a core call has timed out: tear the native core down from the outside so the
		/// game can be reloaded in this same process. Dolphin_Shutdown only sets flags and never blocks;
		/// the thread hosting Dolphin_Main notices within ~100 ms (PlatformHeadless::MainLoop) and runs
		/// Core::Stop on itself, which requests a CPU power-down. Whether that completes is exactly the
		/// open question at a wedge: an emulated CPU thread executing JIT blocks acknowledges PowerDown at
		/// the next block boundary (teardown completes, reload works), while one blocked inside a
		/// driver/fence wait never does (the bounded wait expires; the process must be restarted).
		/// The return value tells the frontend which of those two messages is true.
		/// </summary>
		/// <returns>whether the native core fully tore down (host thread exited) within the timeout</returns>
		public bool TryKillWedgedCore(int timeoutMs)
		{
			// freeze the spinning step thread first: its spin exit condition is never cleared by a
			// shutdown (only by the next boot), and if the CPU thread DOES acknowledge PowerDown
			// mid-wedge, the step thread could otherwise escape into freed native state afterwards
			_runner.TrySuspendWedged();

			if (HostRunning)
			{
				try
				{
					_core.Dolphin_Shutdown();
				}
				catch
				{
					// if the native library itself is gone there is nothing left to try
				}
			}

			var deadline = Environment.TickCount + timeoutMs;
			while (HostRunning && Environment.TickCount - deadline < 0) Thread.Sleep(50);
			return !HostRunning;
		}

		private const uint THREAD_SUSPEND_RESUME = 0x0002;

		[DllImport("kernel32.dll")]
		private static extern uint GetCurrentThreadId();

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

		[DllImport("kernel32.dll")]
		private static extern uint SuspendThread(IntPtr hThread);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CloseHandle(IntPtr hObject);

		public IEmulatorServiceProvider ServiceProvider => _serviceProvider;

		public ControllerDefinition ControllerDefinition { get; }

		public int Frame { get; set; }

		public string SystemId => IsWii ? VSystemID.Raw.Wii : VSystemID.Raw.GameCube;

		public bool DeterministicEmulation { get; }

		public bool FrameAdvance(IController controller, bool render, bool renderSound = true)
		{
			Interlocked.Exchange(ref _controller, controller);
			IsLagFrame = true;

			// the actual swap is deferred until the input callback occurs,
			// so it stays in step with what the emulated disc drive is doing
			var swapDisc = _controller.IsPressed("Swap Disc");
			if (swapDisc && !_didSwapDisc)
			{
				_doSwapDisc = true;
			}
			else if (!swapDisc && _didSwapDisc)
			{
				_didSwapDisc = false;
			}

			_doReset = _controller.IsPressed("Reset");

			if (_coreWedged)
			{
				throw new InvalidOperationException("the Dolphin core is wedged in native code; reload the core");
			}

			try
			{
				_runner.Run(() =>
				{
					// special inputs used to be fired from the native input callback, i.e. re-entering
					// libdolphin from inside the emulated CPU thread (a native-lock deadlock candidate);
					// fire them here instead, on the step thread and outside native code
					if (_doSwapDisc || _doReset)
					{
						_core.Dolphin_SpecialInputs(_doSwapDisc, _doReset);
						_didSwapDisc |= _doSwapDisc;
						_doSwapDisc = false;
						_doReset = false;
					}

					var gpuLagged = _core.Dolphin_FrameStep(render, ref _width, ref _height);

					// by default we lag whenever the GPU presented no new frame;
					// otherwise every frame is a non-lag frame once the input callback has run
					if (_settings.GPULagFrames) IsLagFrame = gpuLagged;

					// drain unconditionally: the native mixer accumulates into its buffer every FrameStep,
					// so skipping the drain (e.g. the future-frame loop steps with renderSound: false) would
					// pile samples up and hand the frontend one giant chunk later
					ProcessSound();
				}, CoreCallTimeoutMs);
			}
			catch (TimeoutException)
			{
				_coreWedged = true;
				throw;
			}

			Frame++;
			if (IsLagFrame) LagCount++;

			return true;
		}

		public void ResetCounters()
		{
			Frame = 0;
			LagCount = 0;
			IsLagFrame = false;
		}

		private bool _disposed;

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			if (_vhandle.IsAllocated) _vhandle.Free();

			_core.Dolphin_SetFrameBuffer(IntPtr.Zero);
			_core.Dolphin_SetGCPadCallback(null);
			_core.Dolphin_SetWiiPadCallback(null);

			if (HostRunning)
			{
				_core.Dolphin_Shutdown();
				// bounded: a wedged core may never finish tearing down, and hanging here would freeze the
				// client inside a reload attempt; past the deadline the host thread (a background thread)
				// is abandoned and the boot that follows will fail on the half-alive native core instead
				var deadline = Environment.TickCount + 10000;
				while (HostRunning && Environment.TickCount - deadline < 0) Thread.Sleep(10);
				_hostThread = null;
			}

			_core.Dolphin_SetConfigCallbacks(null, null);

			if (ReferenceEquals(_currentCore, this)) _currentCore = null;
		}
	}
}
