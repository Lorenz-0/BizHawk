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

		// native 48 kHz staging buffer, with room for one carry frame at the front
		private short[] _rawBuf = new short[1024 * 2];
		// resampled 44.1 kHz output buffer (returned oversized, consumers must use nsamp)
		private short[] _sampleBuf = new short[1024 * 2];
		private int _nsamps;

		// last input frame, kept so interpolation stays phase-continuous across chunks
		private short _carryL, _carryR;
		// fractional read position relative to the carry frame, in [0, RESAMPLE_STEP)
		private double _resamplePos;

		// cumulative sample-frame budget and total emitted, for exact per-frame counts;
		// nsamp is always whole stereo frames, i.e. an even sample count
		private double _sampCarry;
		private int _emitted;

		private void ProcessSound()
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

			// the frontend paces A/V dumping off the sample count (VideoStretcher drops or dups video
			// frames when a chunk deviates from the expected per-frame count), so emit exactly
			// rate * fpsDen / fpsNum stereo frames per frame on a cumulative carry: pad short chunks
			// with silence (the native mixer legitimately produces nothing on IPL boot / GPU lag
			// frames) and trim the rare surplus frame
			_sampCarry += 44100.0 * _vsync.Den / _vsync.Num;
			var want = (int)((long)Math.Round(_sampCarry) - _emitted);
			if (want < 0) want = 0;
			if (outFrames > want)
			{
				outFrames = want;
			}
			else if (outFrames < want)
			{
				if (want * 2 > _sampleBuf.Length) _sampleBuf = new short[want * 2];
				Array.Clear(_sampleBuf, outFrames * 2, (want - outFrames) * 2);
				outFrames = want;
			}

			_emitted += outFrames;
			_nsamps = outFrames;
		}

		public bool CanProvideAsync => false;

		public SyncSoundMode SyncMode => SyncSoundMode.Sync;

		public void DiscardSamples()
		{
			_nsamps = 0;
		}

		public void GetSamplesAsync(short[] samples)
		{
			throw new NotSupportedException("Async mode is not supported");
		}

		public void GetSamplesSync(out short[] samples, out int nsamp)
		{
			samples = _sampleBuf;
			nsamp = _nsamps;
			DiscardSamples();
		}

		public void SetSyncMode(SyncSoundMode mode)
		{
			if (mode == SyncSoundMode.Async) throw new NotSupportedException("Async mode is not supported");
		}
	}
}
