using System.IO;

using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin : IStatable
	{
		private byte[] _stateBuf = [ ];

		/// <remarks>
		/// a Dolphin savestate serializes the whole machine and runs to tens of megabytes, so buffering one per
		/// frame for rewind would be prohibitively expensive in both memory and time.
		/// users who really want rewind can still force it with <c>Config.Rewind.AllowSlowStates</c>.
		/// </remarks>
		public bool AvoidRewind => true;

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

			// other variables; must mirror SaveStateBinary exactly
			IsLagFrame = reader.ReadBoolean();
			LagCount = reader.ReadInt32();
			Frame = reader.ReadInt32();

			if (_multiDisc) _didSwapDisc = reader.ReadBoolean();
		}
	}
}
