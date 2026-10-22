using System.Runtime.InteropServices;

using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin : ISoundProvider
	{
		// the native mixer produces 48 kHz stereo, but modern BizHawk's sound pipeline
		// (SoundOutputProvider, the A/V writers) is hardwired to 44.1 kHz, so resample
		// here; 44100/48000 reduces to exactly 147/160
		private const double RESAMPLE_STEP = 160.0 / 147.0;

		// native 48 kHz staging buffer, with room for one carry frame at the front
		private short[] _rawBuf = new short[1024 * 2];
		// resampled 44.1 kHz output buffer (returned oversized, consumers must use nsamp)
		private short[] _sampleBuf = new short[1024 * 2];
		private int _nsamps;

		// last input frame, kept so interpolation stays phase-continuous across chunks
		private short _carryL, _carryR;
		// fractional read position relative to the carry frame, in [0, RESAMPLE_STEP)
		private double _resamplePos;

		private void ProcessSound()
		{
			var sz = 0;
			var data = _core.Dolphin_GetAudio(ref sz);
			_nsamps = 0;
			// sometimes 0 is returned, particularly on the first frame of the IPL boot
			if (sz <= 0) return;

			if (sz + 2 > _rawBuf.Length) _rawBuf = new short[sz + 2];
			_rawBuf[0] = _carryL;
			_rawBuf[1] = _carryR;
			Marshal.Copy(data, _rawBuf, 2, sz);

			var inFrames = sz / 2 + 1; // carry frame plus the new samples
			var maxOut = (int)((inFrames - 1 - _resamplePos) / RESAMPLE_STEP) + 2;
			if (maxOut * 2 > _sampleBuf.Length) _sampleBuf = new short[maxOut * 2];

			var pos = _resamplePos;
			var outFrames = 0;
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
				pos += RESAMPLE_STEP;
			}

			_resamplePos = pos - (inFrames - 1);
			_carryL = _rawBuf[(inFrames - 1) * 2];
			_carryR = _rawBuf[(inFrames - 1) * 2 + 1];
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
