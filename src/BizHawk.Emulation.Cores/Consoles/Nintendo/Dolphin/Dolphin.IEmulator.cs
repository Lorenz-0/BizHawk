using System.Collections.Concurrent;
using System.Threading;

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

			public CoreRunner()
			{
				new Thread(() =>
				{
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
				_jobs.Add(j);
				if (!j.Done.Wait(timeoutMs))
				{
					throw new TimeoutException("libdolphin stopped responding inside Dolphin_FrameStep (unbounded native spin-wait on the CPU thread); the step thread remains wedged");
				}

				if (j.Error is not null) throw new Exception("Dolphin core call failed", j.Error);
			}
		}

		private readonly CoreRunner _runner = new();

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
				while (HostRunning) Thread.Sleep(1);
				_hostThread = null;
			}

			_core.Dolphin_SetConfigCallbacks(null, null);

			if (ReferenceEquals(_currentCore, this)) _currentCore = null;
		}
	}
}
