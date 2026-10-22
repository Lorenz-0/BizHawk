using System.Runtime.InteropServices;

using BizHawk.BizInvoke;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	/// <summary>
	/// The complete native ABI exported by <c>dolphin-emu-nogui.dll</c> / <c>libdolphin-emu-nogui.so</c>.
	/// </summary>
	/// <remarks>
	/// These bindings are built from the <c>biz</c> branch of https://github.com/CasualPokePlayer/dolphin
	/// (Dolphin 5.0-16426), which is tracked by the <c>libdolphin</c> submodule.
	/// Every entry here has a matching export in the shipped binaries; do not add or remove
	/// members without rebuilding the native libraries to match.
	/// </remarks>
	public abstract class LibDolphin
	{
		private const CallingConvention CC = CallingConvention.Cdecl;

		[BizImport(CC, Compatibility = true)]
		public abstract int Dolphin_Main(int argc, string[] argv);

		[BizImport(CC)]
		public abstract void Dolphin_Shutdown();

		[BizImport(CC)]
		public abstract bool Dolphin_BootupSuccessful();

		[BizImport(CC)]
		public abstract void Dolphin_SetFrameBuffer(IntPtr fb);

		/// <returns><see langword="true"/> if the GPU presented no new frame during this step (i.e. it lagged)</returns>
		[BizImport(CC)]
		public abstract bool Dolphin_FrameStep(bool render, ref int width, ref int height);

		[Flags]
		public enum PadButtons : ushort
		{
			Left = 0x0001,
			Right = 0x0002,
			Down = 0x0004,
			Up = 0x0008,
			Z = 0x0010,
			R = 0x0020,
			L = 0x0040,
			// 0x0080 is PAD_USE_ORIGIN, which is not exposed to us
			A = 0x0100,
			B = 0x0200,
			X = 0x0400,
			Y = 0x0800,
			Start = 0x1000,
		}

		/// <remarks>matches <c>GCPadStatus</c> in the native library</remarks>
		[StructLayout(LayoutKind.Sequential)]
		public struct GCPadStatus
		{
			public PadButtons Buttons;
			public byte StickX;
			public byte StickY;
			public byte SubstickX;
			public byte SubstickY;
			public byte TriggerLeft;
			public byte TriggerRight;
			public byte AnalogA;
			public byte AnalogB;
			/// <remarks>marshalled as a single byte by the native side, so this must stay <see langword="bool"/> (1 byte), not <see cref="int"/></remarks>
			public bool IsConnected;
		}

		[UnmanagedFunctionPointer(CC)]
		public delegate void GCPadCallback(IntPtr padStatus, int controllerID);

		[BizImport(CC)]
		public abstract void Dolphin_SetGCPadCallback(GCPadCallback cb);

		[Flags]
		public enum WiimoteButtons : ushort
		{
			Left = 0x0001,
			Right = 0x0002,
			Down = 0x0004,
			Up = 0x0008,
			Z = 0x0010,
			R = 0x0020,
			L = 0x0040,
			A = 0x0100,
			B = 0x0200,
			X = 0x0400,
			Y = 0x0800,
			Start = 0x1000,
		}

		/// <summary>which chunk of a Wiimote report the native library wants us to fill in</summary>
		public enum WiimoteInputReq
		{
			CORE_BUTTONS = 0,
			CORE_ACCEL = 1,
			CORE_IR_BASIC = 2,
			CORE_IR_EXTENDED = 3,
			CORE_IR_FULL = 4,
			/// <summary>the report is complete; this is where we advance our own per-frame bookkeeping</summary>
			END_INPUT = 0xFF,
		}

		[Flags]
		public enum CoreButtons : ushort
		{
			Left = 0x0001,
			Right = 0x0002,
			Down = 0x0004,
			Up = 0x0008,
			Plus = 0x0010,
			// 3 bits not relevant here
			Two = 0x0100,
			One = 0x0200,
			B = 0x0400,
			A = 0x0800,
			Minus = 0x1000,
			// 2 bits not relevant here
			Home = 0x8000,
		}

		/// <summary>the subset of <see cref="CoreButtons"/> we drive, so that unrelated report bits are preserved</summary>
		public const CoreButtons CoreButtonsMask =
			CoreButtons.Left | CoreButtons.Right | CoreButtons.Down | CoreButtons.Up |
			CoreButtons.Plus | CoreButtons.Two | CoreButtons.One | CoreButtons.B |
			CoreButtons.A | CoreButtons.Minus | CoreButtons.Home;

		[StructLayout(LayoutKind.Sequential)]
		public struct CoreAccel
		{
			public ushort X;
			public ushort Y;
			public ushort Z;
		}

#pragma warning disable RCS1157 // packed multi-bit bitfield, not a set of single-bit flags
		[Flags]
		public enum CoreIRBasicHi : byte
		{
			X2 = 0x03,
			Y2 = 0x0C,
			X1 = 0x30,
			Y1 = 0xC0,
		}
#pragma warning restore RCS1157

		[StructLayout(LayoutKind.Sequential)]
		public struct CoreIRBasic
		{
			public byte X1;
			public byte Y1;
			public CoreIRBasicHi Hi;
			public byte X2;
			public byte Y2;
		}

#pragma warning disable RCS1157 // packed multi-bit bitfield, not a set of single-bit flags
		[Flags]
		public enum CoreIRExtendedSzHi : byte
		{
			Sz = 0x0F,
			X = 0x30,
			Y = 0xC0,
		}
#pragma warning restore RCS1157

		[StructLayout(LayoutKind.Sequential)]
		public struct CoreIRExtended
		{
			public byte X;
			public byte Y;
			public CoreIRExtendedSzHi SzHi;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct CoreIRFull
		{
			// can't inherit a struct in a struct, and a class is managed, so the fields of CoreIRExtended are repeated here
			public byte X;
			public byte Y;
			public CoreIRExtendedSzHi SzHi;
			/// <remarks>min/max have 7 bits of granularity</remarks>
			public byte XMin;
			public byte YMin;
			public byte XMax;
			public byte YMax;
			public byte ZeroPadding;
			public byte Intensity;
		}

		[UnmanagedFunctionPointer(CC)]
		public delegate void WiiPadCallback(IntPtr p, WiimoteInputReq which, int controllerID);

		[BizImport(CC)]
		public abstract void Dolphin_SetWiiPadCallback(WiiPadCallback cb);

		/// <returns>a pointer to interleaved stereo s16 samples; <paramref name="sz"/> receives the size in bytes</returns>
		[BizImport(CC)]
		public abstract IntPtr Dolphin_GetAudio(ref int sz);

		[BizImport(CC)]
		public abstract int Dolphin_StateSize(bool compressed);

		[BizImport(CC)]
		public abstract void Dolphin_SaveState(byte[] buf, int sz, bool compressed);

		[BizImport(CC)]
		public abstract void Dolphin_LoadState(byte[] buf, int sz, bool compressed);

		public enum MEMPTR_IDS
		{
			RAM = 0,
			EXRAM = 1,
			L1Cache = 2,
			FakeVMEM = 3,
		}

		[BizImport(CC)]
		public abstract bool Dolphin_GetMemPtr(MEMPTR_IDS which, ref IntPtr ptr, ref int sz);

		[BizImport(CC)]
		public abstract byte Dolphin_ReadU8(uint addr);

		[BizImport(CC)]
		public abstract ushort Dolphin_ReadU16(uint addr, bool bigEndian);

		[BizImport(CC)]
		public abstract uint Dolphin_ReadU32(uint addr, bool bigEndian);

		[BizImport(CC)]
		public abstract void Dolphin_ReadBulkU8(uint startAddr, int numAddrs, byte[] buf);

		[BizImport(CC)]
		public abstract void Dolphin_ReadBulkU16(uint startAddr, int numAddrs, ushort[] buf, bool bigEndian);

		[BizImport(CC)]
		public abstract void Dolphin_ReadBulkU32(uint startAddr, int numAddrs, uint[] buf, bool bigEndian);

		[BizImport(CC)]
		public abstract void Dolphin_WriteU8(uint addr, byte val);

		[BizImport(CC)]
		public abstract void Dolphin_WriteU16(uint addr, ushort val, bool bigEndian);

		[BizImport(CC)]
		public abstract void Dolphin_WriteU32(uint addr, uint val, bool bigEndian);

		[BizImport(CC)]
		public abstract int Dolphin_GetVSyncNumerator();

		[BizImport(CC)]
		public abstract int Dolphin_GetVSyncDenominator();

		[UnmanagedFunctionPointer(CC)]
		public delegate bool MPlusConfigCallback(int n);

		[UnmanagedFunctionPointer(CC)]
		public delegate Dolphin.DolphinWiiPadSettings.WiimoteExtensions ExtensionConfigCallback(int n);

		[BizImport(CC)]
		public abstract void Dolphin_SetConfigCallbacks(MPlusConfigCallback mplus, ExtensionConfigCallback extension);

		[BizImport(CC)]
		public abstract ulong Dolphin_GetTicks();

		[BizImport(CC)]
		public abstract void Dolphin_SpecialInputs(bool swapDisc, bool reset);

		/// <summary>
		/// Hashes a disc medium using Dolphin's own code, which understands every container format it can boot.
		/// </summary>
		/// <param name="quick">CRC32 of the disc header when <see langword="true"/>, else the full MD5</param>
		/// <param name="doInit">whether the native library still needs its one-time disc subsystem init</param>
		[BizImport(CC, Compatibility = true)]
		public abstract void Dolphin_HashMedium(string path, byte[] hash, bool quick, bool doInit);
	}
}
