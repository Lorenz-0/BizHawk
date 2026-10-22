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

		public int VsyncNumerator => _core.Dolphin_GetVSyncNumerator();

		public int VsyncDenominator => _core.Dolphin_GetVSyncDenominator();

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
