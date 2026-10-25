using System.Runtime.InteropServices;

using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin : ISoundProvider
	{
		// input frames consumed per output frame. the native library exposes no sample-rate
		// export, and the rate is not even constant (Wii boot screens mix at 32 kHz, games at
		// 48 kHz), so a fixed 48->44.1 step pitch-shifted the boot audio; estimate the input rate
		// from the samples per frame we actually receive times the vsync rate, and derive the step
		private double _resampleStep = 48000.0 / 44100.0;
		private double _rateEst;

		// native interleaved-stereo staging buffer, with room for one carry frame at the front
		private short[] _rawBuf = new short[1024 * 2];
		// resampled 44.1 kHz output buffer (returned oversized, consumers must use nsamp);
		// the fill happens on the core step thread, so GetSamplesSync swaps to a spare buffer and the
		// consumer never reads the array being filled
		private short[] _sampleBuf = new short[1024 * 2];
		private short[] _spareBuf = new short[1024 * 2];
		private readonly object _soundLock = new();
		private int _nsamps;

		// last input frame, kept so interpolation stays phase-continuous across chunks
		private short _carryL, _carryR;
		// fractional read position relative to the carry frame, in [0, RESAMPLE_STEP)
		private double _resamplePos;

		// cumulative 44.1 kHz sample-frame budget and scheduled total;
		// nsamp is always whole stereo frames, i.e. an even sample count
		private double _sampCarry;
		private int _emitted;

		// Small non-blocking staging FIFO absorbs per-frame sample-count jitter without injecting silence.
		private short[] _audioFifo = new short[2048 * 2];
		private int _audioFifoStart;
		private int _audioFifoCount;

		private void ProcessSound()
		{
			lock (_soundLock) ProcessSoundLocked();
		}

		private void EnqueueAudio(short[] samples, int frames)
		{
			int sampleCount = frames * 2;
			int requiredCapacity = _audioFifoCount + sampleCount;
			if (requiredCapacity > _audioFifo.Length)
			{
				var larger = new short[Math.Max(requiredCapacity, _audioFifo.Length * 2)];
				int firstPart = Math.Min(_audioFifoCount, _audioFifo.Length - _audioFifoStart);
				Array.Copy(_audioFifo, _audioFifoStart, larger, 0, firstPart);
				Array.Copy(_audioFifo, 0, larger, firstPart, _audioFifoCount - firstPart);
				_audioFifo = larger;
				_audioFifoStart = 0;
			}

			int writeAt = (_audioFifoStart + _audioFifoCount) % _audioFifo.Length;
			int firstWrite = Math.Min(sampleCount, _audioFifo.Length - writeAt);
			Array.Copy(samples, 0, _audioFifo, writeAt, firstWrite);
			Array.Copy(samples, firstWrite, _audioFifo, 0, sampleCount - firstWrite);
			_audioFifoCount += sampleCount;
		}

		private int DequeueAudio(short[] destination, int maxFrames)
		{
			int sampleCount = Math.Min(_audioFifoCount, maxFrames * 2) & ~1;
			int firstPart = Math.Min(sampleCount, _audioFifo.Length - _audioFifoStart);
			Array.Copy(_audioFifo, _audioFifoStart, destination, 0, firstPart);
			Array.Copy(_audioFifo, 0, destination, firstPart, sampleCount - firstPart);
			_audioFifoStart = (_audioFifoStart + sampleCount) % _audioFifo.Length;
			_audioFifoCount -= sampleCount;
			if (_audioFifoCount == 0) _audioFifoStart = 0;
			return sampleCount / 2;
		}

		private void ProcessSoundLocked()
		{
			var sz = 0;
			var data = _core.Dolphin_GetAudio(ref sz);

			RefreshVsync();

			var outFrames = 0;
			// sometimes 0 is returned, particularly on the first frame of the IPL boot
			if (sz > 0)
			{
				// rate estimate: samples this frame times frames per second; zero chunks (GPU lag)
				// and insane values are ignored so they cannot poison the average
				var inst = (sz / 2.0) * (_vsync.Num / (double)_vsync.Den);
				if (inst >= 4000 && inst <= 192000)
				{
					_rateEst = _rateEst == 0 ? inst : _rateEst + (inst - _rateEst) * 0.1;
					_resampleStep = _rateEst / 44100.0;
				}

				if (sz + 2 > _rawBuf.Length) _rawBuf = new short[sz + 2];
				_rawBuf[0] = _carryL;
				_rawBuf[1] = _carryR;
				Marshal.Copy(data, _rawBuf, 2, sz);

				var inFrames = sz / 2 + 1; // carry frame plus the new samples
				var maxOut = (int)((inFrames - 1 - _resamplePos) / _resampleStep) + 2;
				if (maxOut * 2 > _sampleBuf.Length) _sampleBuf = new short[maxOut * 2];

				var pos = _resamplePos;
				while (pos < inFrames - 1)
				{
					var i0 = (int)pos;
					var frac = pos - i0;
					for (var ch = 0; ch < 2; ch++)
					{
						var v = _rawBuf[i0 * 2 + ch] * (1.0 - frac) + _rawBuf[(i0 + 1) * 2 + ch] * frac;
						_sampleBuf[outFrames * 2 + ch] = (short)Math.Round(v);
					}

					outFrames++;
					pos += _resampleStep;
				}

				_resamplePos = pos - (inFrames - 1);
				_carryL = _rawBuf[(inFrames - 1) * 2];
				_carryR = _rawBuf[(inFrames - 1) * 2 + 1];
			}

			// Buffer real resampled audio across frame boundaries. Padding each short native chunk
			// with silence creates audible gaps; the FIFO lets later surplus absorb small count jitter.
			if (outFrames > 0) EnqueueAudio(_sampleBuf, outFrames);

			_sampCarry += 44100.0 * _vsync.Den / _vsync.Num;
			var want = (int)((long)Math.Round(_sampCarry) - _emitted);
			if (want < 0) want = 0;
			var availableFrames = _audioFifoCount / 2;
			var framesToReturn = Math.Min(want, availableFrames);
			if (framesToReturn * 2 > _sampleBuf.Length) _sampleBuf = new short[framesToReturn * 2];
			outFrames = DequeueAudio(_sampleBuf, framesToReturn);

			// Advance the output clock by the scheduled count, not by the possibly-short read.
			_emitted += want;
			_nsamps = outFrames;
		}

		public bool CanProvideAsync => false;

		public SyncSoundMode SyncMode => SyncSoundMode.Sync;

		public void DiscardSamples()
		{
			lock (_soundLock)
			{
				_nsamps = 0;
				_audioFifoStart = 0;
				_audioFifoCount = 0;
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
