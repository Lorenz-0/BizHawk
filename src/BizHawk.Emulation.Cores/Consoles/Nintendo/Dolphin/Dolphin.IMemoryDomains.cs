using System.Collections.Generic;

using BizHawk.Common;
using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin
	{
		private readonly List<MemoryDomain> _memoryDomains = [ ];

		private MemoryDomainList MemoryDomains { get; set; }

		private void CreateMemoryDomain(LibDolphin.MEMPTR_IDS which, string name)
		{
			var data = IntPtr.Zero;
			var length = 0;

			if (!_core.Dolphin_GetMemPtr(which, ref data, ref length))
			{
				throw new InvalidOperationException($"{nameof(LibDolphin.Dolphin_GetMemPtr)}({which}) failed!");
			}

			// EXRAM is absent on GameCube, and Fake VMEM only exists once a Wii game has mapped it,
			// so a null/empty result is expected rather than an error
			if (data != IntPtr.Zero && length > 0)
			{
				_memoryDomains.Add(new MemoryDomainIntPtr(name, MemoryDomain.Endian.Big, data, length, writable: true, wordSize: 4));
			}
		}

		private void InitMemoryDomains()
		{
			CreateMemoryDomain(LibDolphin.MEMPTR_IDS.RAM, "RAM");
			CreateMemoryDomain(LibDolphin.MEMPTR_IDS.EXRAM, "ExRAM");
			CreateMemoryDomain(LibDolphin.MEMPTR_IDS.L1Cache, "L1 Cache");
			CreateMemoryDomain(LibDolphin.MEMPTR_IDS.FakeVMEM, "Fake VMEM");

			_memoryDomains.Add(new DolphinMMU(_core));

			MemoryDomains = new(_memoryDomains);
			_serviceProvider.Register<IMemoryDomains>(MemoryDomains);
		}

		/// <summary>
		/// Dolphin's full 32-bit effective address space, resolved through the emulated MMU
		/// rather than pointing at a single host buffer.
		/// </summary>
		private sealed class DolphinMMU : MemoryDomain
		{
			private const string ERR_MSG_BUFFER_WRONG_SIZE = "Invalid length of values array";
			private const string ERR_MSG_UNALIGNED = "The API contract doesn't define what to do for unaligned reads and writes!";

			private readonly LibDolphin _core;

			public DolphinMMU(LibDolphin core)
			{
				Name = "System Bus";
				Size = 1L << 32;
				WordSize = 4;
				EndianType = Endian.Big;
				Writable = true;
				_core = core;
			}

			public override byte PeekByte(long addr)
				=> _core.Dolphin_ReadU8((uint) addr);

			public override ushort PeekUshort(long addr, bool bigEndian)
				=> _core.Dolphin_ReadU16((uint) addr, bigEndian);

			public override uint PeekUint(long addr, bool bigEndian)
				=> _core.Dolphin_ReadU32((uint) addr, bigEndian);

			public override void PokeByte(long addr, byte val)
				=> _core.Dolphin_WriteU8((uint) addr, val);

			public override void PokeUshort(long addr, ushort val, bool bigEndian)
				=> _core.Dolphin_WriteU16((uint) addr, val, bigEndian);

			public override void PokeUint(long addr, uint val, bool bigEndian)
				=> _core.Dolphin_WriteU32((uint) addr, val, bigEndian);

			public override void BulkPeekByte(Range<long> addresses, byte[] values)
			{
				if (addresses is null) throw new ArgumentNullException(paramName: nameof(addresses));
				if (values is null) throw new ArgumentNullException(paramName: nameof(values));
				if ((long) addresses.Count() != values.Length) throw new InvalidOperationException(ERR_MSG_BUFFER_WRONG_SIZE);

				_core.Dolphin_ReadBulkU8((uint) addresses.Start, values.Length, values);
			}

			public override void BulkPeekUshort(Range<long> addresses, bool bigEndian, ushort[] values)
			{
				if (addresses is null) throw new ArgumentNullException(paramName: nameof(addresses));
				if (values is null) throw new ArgumentNullException(paramName: nameof(values));

				var start = addresses.Start;
				var end = addresses.EndInclusive + 1;
				if ((start & 0b1) is not 0 || (end & 0b1) is not 0) throw new InvalidOperationException(ERR_MSG_UNALIGNED);
				if (values.LongLength * sizeof(ushort) != end - start) throw new InvalidOperationException(ERR_MSG_BUFFER_WRONG_SIZE); // a longer array could be valid, but nothing needs that so don't support it for now

				_core.Dolphin_ReadBulkU16((uint) start, values.Length, values, bigEndian);
			}

			public override void BulkPeekUint(Range<long> addresses, bool bigEndian, uint[] values)
			{
				if (addresses is null) throw new ArgumentNullException(paramName: nameof(addresses));
				if (values is null) throw new ArgumentNullException(paramName: nameof(values));

				var start = addresses.Start;
				var end = addresses.EndInclusive + 1;
				if ((start & 0b11) is not 0 || (end & 0b11) is not 0) throw new InvalidOperationException(ERR_MSG_UNALIGNED);
				if (values.LongLength * sizeof(uint) != end - start) throw new InvalidOperationException(ERR_MSG_BUFFER_WRONG_SIZE); // `!=` not `<`, per BulkPeekUshort

				_core.Dolphin_ReadBulkU32((uint) start, values.Length, values, bigEndian);
			}
		}
	}
}
