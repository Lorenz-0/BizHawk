using System.IO;

using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin : IStatable
	{
		private byte[] _stateBuf = [ ];

		/// <remarks>
		/// must be false for TAStudio's state managers to capture any savestate history at all
		/// (both managers skip capture for AvoidRewind cores, which would make every seek restart
		/// from the beginning of the movie). Dolphin states are large but workable, the same
		/// tradeoff PPSSPP makes; the main form's rewind buffer remains bounded by
		/// <c>Config.Rewind.BufferSize</c> and can be disabled via <c>Config.Rewind.Enabled</c>
		/// without affecting TAStudio seeking.
		/// </remarks>
		public bool AvoidRewind => false;

		public void SaveStateBinary(BinaryWriter writer)
		{
			var compressed = _settings.UseCompressedStates;
			var sz = _core.Dolphin_StateSize(compressed);
			if (sz > _stateBuf.Length) _stateBuf = new byte[sz];
			_core.Dolphin_SaveState(_stateBuf, sz, compressed);

			writer.Write(compressed);
			writer.Write(sz);
			writer.Write(_stateBuf, 0, sz);

			// other variables
			writer.Write(IsLagFrame);
			writer.Write(LagCount);
			writer.Write(Frame);

			if (_multiDisc) writer.Write(_didSwapDisc);
		}

		public void LoadStateBinary(BinaryReader reader)
		{
			var compressed = reader.ReadBoolean();
			var len = reader.ReadInt32();
			if (len > _stateBuf.Length) _stateBuf = new byte[len];

			_ = reader.Read(_stateBuf, 0, len);
			_core.Dolphin_LoadState(_stateBuf, len, compressed);
			ResetSoundFifo();
			RefreshVsync();

			// other variables; must mirror SaveStateBinary exactly
			IsLagFrame = reader.ReadBoolean();
			LagCount = reader.ReadInt32();
			Frame = reader.ReadInt32();

			if (_multiDisc) _didSwapDisc = reader.ReadBoolean();
		}
	}
}
