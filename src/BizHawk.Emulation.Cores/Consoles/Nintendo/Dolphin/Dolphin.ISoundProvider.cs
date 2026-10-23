using System.Runtime.InteropServices;

using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin : ISoundProvider
	{
		private const int MaxFifoFrames = 4096;

		// libdolphin's AudioProvider (MainNoGUI.cpp) already resamples both 32 kHz (Wii boot / GC 32 kHz)
		// and 48 kHz game audio to 44.1 kHz via blip_buf; however, DSP DMA and 3.5 ms DTK streaming blocks
		// land asynchronously relative to VI field boundaries, so per-frame chunk sizes jitter around the
		// nominal 44100 * Den / Num count. Stage incoming 44.1 kHz stereo samples in a small FIFO so DMA
		// block jitter is absorbed losslessly without resampling warble, dropped samples, or zero-fill gaps.
		private short[] _fifo = new short[8192 * 2];
		private int _fifoReadFrames;
		private int _fifoFrames;
		private short _lastL, _lastR;

		// 44.1 kHz output buffer (returned oversized, consumers must use nsamp);
		// the fill happens on the core step thread, so GetSamplesSync swaps to a spare buffer and the
		// consumer never reads the array being filled
		private short[] _sampleBuf = new short[1024 * 2];
		private short[] _spareBuf = new short[1024 * 2];
		private readonly object _soundLock = new();
		private int _nsamps;

		// cumulative sample-frame budget and total emitted, for exact per-frame counts;
		// nsamp is always whole stereo frames, i.e. an even sample count
		private double _sampCarry;
		private long _emitted;

		private void ProcessSound()
		{
			lock (_soundLock) ProcessSoundLocked();
		}

		private void ResetSoundFifo()
		{
			lock (_soundLock)
			{
				_fifoReadFrames = 0;
				_fifoFrames = 0;
				_lastL = 0;
				_lastR = 0;
				_nsamps = 0;
			}
		}

		private void ProcessSoundLocked()
		{
			var sz = 0;
			var data = _core.Dolphin_GetAudio(ref sz);

			RefreshVsync();

			var inFrames = sz / 2;
			if (inFrames > 0)
			{
				var capacityFrames = _fifo.Length / 2;
				if (_fifoReadFrames + _fifoFrames + inFrames > capacityFrames)
				{
					if (_fifoReadFrames > 0 && _fifoFrames > 0)
					{
						Array.Copy(_fifo, _fifoReadFrames * 2, _fifo, 0, _fifoFrames * 2);
					}

					_fifoReadFrames = 0;
					if (_fifoFrames + inFrames > capacityFrames)
					{
						var newCapFrames = Math.Max(capacityFrames * 2, _fifoFrames + inFrames);
						var grown = new short[newCapFrames * 2];
						if (_fifoFrames > 0)
						{
							Array.Copy(_fifo, 0, grown, 0, _fifoFrames * 2);
						}

						_fifo = grown;
					}
				}

				Marshal.Copy(data, _fifo, (_fifoReadFrames + _fifoFrames) * 2, inFrames * 2);
				_fifoFrames += inFrames;

				var tailIdx = (_fifoReadFrames + _fifoFrames - 1) * 2;
				_lastL = _fifo[tailIdx];
				_lastR = _fifo[tailIdx + 1];

				if (_fifoFrames > MaxFifoFrames)
				{
					var drop = _fifoFrames - MaxFifoFrames;
					_fifoReadFrames += drop;
					_fifoFrames = MaxFifoFrames;
				}
			}

			// the frontend paces A/V dumping off the sample count (VideoStretcher drops or dups video
			// frames when a chunk deviates from the expected per-frame count), so emit exactly
			// rate * fpsDen / fpsNum stereo frames per frame on a cumulative carry
			_sampCarry += 44100.0 * _vsync.Den / _vsync.Num;
			var want = (int) ((long) Math.Round(_sampCarry) - _emitted);
			if (want < 0) want = 0;

			if (want * 2 > _sampleBuf.Length) _sampleBuf = new short[want * 2];

			var take = Math.Min(_fifoFrames, want);
			if (take > 0)
			{
				Array.Copy(_fifo, _fifoReadFrames * 2, _sampleBuf, 0, take * 2);
				_fifoReadFrames += take;
				_fifoFrames -= take;
				if (_fifoFrames == 0) _fifoReadFrames = 0;
			}

			// pad genuine underruns (IPL boot / GPU lag frames when the FIFO is empty) by decaying
			// smoothly toward silence rather than stepping abruptly to zero
			for (var i = take; i < want; i++)
			{
				_lastL = (short) (_lastL * 31 / 32);
				_lastR = (short) (_lastR * 31 / 32);
				_sampleBuf[i * 2] = _lastL;
				_sampleBuf[i * 2 + 1] = _lastR;
			}

			_emitted += want;
			_nsamps = want;
		}

		public bool CanProvideAsync => false;

		public SyncSoundMode SyncMode => SyncSoundMode.Sync;

		public void DiscardSamples()
		{
			lock (_soundLock)
			{
				_nsamps = 0;
			}
		}

		public void GetSamplesAsync(short[] samples)
		{
			throw new NotSupportedException("Async mode is not supported");
		}

		public void GetSamplesSync(out short[] samples, out int nsamp)
		{
			lock (_soundLock)
			{
				samples = _sampleBuf;
				(_sampleBuf, _spareBuf) = (_spareBuf, _sampleBuf);
				nsamp = _nsamps;
				_nsamps = 0;
			}
		}

		public void SetSyncMode(SyncSoundMode mode)
		{
			if (mode == SyncSoundMode.Async) throw new NotSupportedException("Async mode is not supported");
		}
	}
}
