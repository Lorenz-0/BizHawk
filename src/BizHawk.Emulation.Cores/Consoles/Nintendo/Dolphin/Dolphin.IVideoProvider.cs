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
		/// the native side reports 0/1 until the VI timing registers are first written (earliest boot frames),
		/// and the frontend divides by these values (A/V dumping, sound pacing), so hold the last known-good
		/// ratio instead of passing the zeros through
		/// </remarks>
		private (int Num, int Den) _vsync = (60, 1);

		private void RefreshVsync()
		{
			var num = _core.Dolphin_GetVSyncNumerator();
			var den = _core.Dolphin_GetVSyncDenominator();
			if (num != 0 && den != 0) _vsync = (num, den);
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
		/// the native library blits straight into this buffer (no callback into managed code),
		/// sized for the tallest interlaced field Dolphin can produce at the widest NTSC/PAL width
		/// </remarks>
		private readonly int[] _vbuf = new int[640 * 528 * 8];

		/// <remarks>assigned in the main ctor once the core has booted, and freed in <see cref="Dispose"/></remarks>
		private readonly GCHandle _vhandle;

		public int[] GetVideoBuffer() => _vbuf;
	}
}
