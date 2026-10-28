using System.Runtime.InteropServices;

using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin : IVideoProvider
	{
		private int _width = 640;
		private int _height = 480;

		public int VirtualWidth => _width;

		public int VirtualHeight => _height;

		public int BufferWidth => _width;

		public int BufferHeight => _height;

		/// <remarks>
		/// the native side reports 0/1 until the VI timing registers are first written, and
		/// <c>VideoInterface::Preset()</c> initializes <c>m_Clock = 1</c> (54 MHz) alongside 27 MHz interlaced
		/// line counts (reporting 120000/1001 ~ 119.88 Hz before the game configures VI); normalize that 2x
		/// preset ratio and hold the last valid PAL/NTSC refresh ratio so A/V dumping and sound pacing never
		/// latch onto 0 or 119.88 Hz
		/// </remarks>
		private (int Num, int Den) _vsync = (60000, 1001);

		private static int Gcd(int a, int b)
		{
			while (b != 0)
			{
				(a, b) = (b, a % b);
			}

			return a;
		}

		private void RefreshVsync()
		{
			var num = _core.Dolphin_GetVSyncNumerator();
			var den = _core.Dolphin_GetVSyncDenominator();
			if (num <= 0 || den <= 0) return;

			var fps = (double) num / den;
			if (fps is > 90.0 and < 130.0)
			{
				if ((num & 1) == 0)
				{
					num >>= 1;
				}
				else
				{
					if (den > int.MaxValue / 2) return;
					den <<= 1;
				}

				fps *= 0.5;
			}

			if (fps is < 20.0 or > 75.0) return;

			var gcd = Gcd(num, den);
			if (gcd > 1)
			{
				num /= gcd;
				den /= gcd;
			}

			_vsync = (num, den);
		}

		public int VsyncNumerator
		{
			get
			{
				RefreshVsync();
				return _vsync.Num;
			}
		}

		public int VsyncDenominator
		{
			get
			{
				RefreshVsync();
				return _vsync.Den;
			}
		}

		public int BackgroundColor => 0;

		/// <remarks>
		/// the native library blits straight into this buffer (no callback into managed code) and
		/// does not check its capacity. It is sized for the tallest interlaced field Dolphin can
		/// produce at the widest NTSC/PAL width. For standard retail VI output (XFB up to 720x576
		/// at 4:3 or 16:9), 2x internal resolution tops out at 2048x1152 (720x576 doubled, widened
		/// to 16:9) = 2,359,296 ints, 87% of this buffer; 3x would need at least
		/// 640*528*9 = 3,041,280 ints, so <see cref="DolphinGFXSettings.InternalResolution"/> is
		/// capped at 2x. Out-of-spec VI dimensions or timing (e.g. from cheats, corrupted state,
		/// or dolphin-native-overrides.txt) are not bounded by this buffer at any scale; a native
		/// capacity check is future work.
		/// </remarks>
		private readonly int[] _vbuf = new int[640 * 528 * 8];

		/// <remarks>assigned in the main ctor once the core has booted, and freed in <see cref="Dispose"/></remarks>
		private readonly GCHandle _vhandle;

		public int[] GetVideoBuffer() => _vbuf;
	}
}
