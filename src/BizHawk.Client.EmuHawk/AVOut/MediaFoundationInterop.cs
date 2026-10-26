using System;
using System.Runtime.InteropServices;

namespace BizHawk.Client.EmuHawk
{
	internal static class MfNative
	{
		internal const int S_OK = 0;
		internal const uint MfVersion = 0x00020070; // MF_SDK_VERSION=2, MF_API_VERSION=0x70
		internal const int MfStartupFull = 0;
		internal const uint CoInitMultithreaded = 0;

		[DllImport("ole32.dll", ExactSpelling = true)]
		internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

		[DllImport("ole32.dll", ExactSpelling = true)]
		internal static extern void CoUninitialize();

		[DllImport("mfplat.dll", ExactSpelling = true)]
		internal static extern int MFStartup(uint version, int flags);

		[DllImport("mfplat.dll", ExactSpelling = true)]
		internal static extern int MFShutdown();

		[DllImport("mfplat.dll", ExactSpelling = true)]
		internal static extern int MFCreateMediaType([MarshalAs(UnmanagedType.Interface)] out IMFMediaType mediaType);

		[DllImport("mfplat.dll", ExactSpelling = true)]
		internal static extern int MFCreateSample([MarshalAs(UnmanagedType.Interface)] out IMFSample sample);

		[DllImport("mfplat.dll", ExactSpelling = true)]
		internal static extern int MFCreateMemoryBuffer(uint maxLength, [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer buffer);

		[DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
		internal static extern int MFCreateSinkWriterFromURL(
			[MarshalAs(UnmanagedType.LPWStr)] string outputUrl,
			IntPtr byteStream,
			IntPtr attributes,
			[MarshalAs(UnmanagedType.Interface)] out IMFSinkWriter sinkWriter);

		internal static void Check(int hr, string operation)
		{
			if (hr < 0) throw new COMException($"{operation} failed (HRESULT 0x{hr:X8}).", hr);
		}

		internal static void Release(object value)
		{
			if (value != null && Marshal.IsComObject(value))
			{
				try { Marshal.ReleaseComObject(value); }
				catch { /* best-effort COM cleanup */ }
			}
		}
	}

	// IMFAttributes method order is part of the COM ABI. IMFMediaType inherits it.
	[ComImport]
	[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IMFAttributes
	{
		[PreserveSig] int GetItem(ref Guid key, IntPtr value);
		[PreserveSig] int GetItemType(ref Guid key, out int type);
		[PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
		[PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
		[PreserveSig] int GetUINT32(ref Guid key, out uint value);
		[PreserveSig] int GetUINT64(ref Guid key, out ulong value);
		[PreserveSig] int GetDouble(ref Guid key, out double value);
		[PreserveSig] int GetGUID(ref Guid key, out Guid value);
		[PreserveSig] int GetStringLength(ref Guid key, out uint length);
		[PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, out uint length);
		[PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
		[PreserveSig] int GetBlobSize(ref Guid key, out uint size);
		[PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint blobSize);
		[PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
		[PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, out IntPtr value);
		[PreserveSig] int SetItem(ref Guid key, IntPtr value);
		[PreserveSig] int DeleteItem(ref Guid key);
		[PreserveSig] int DeleteAllItems();
		[PreserveSig] int SetUINT32(ref Guid key, uint value);
		[PreserveSig] int SetUINT64(ref Guid key, ulong value);
		[PreserveSig] int SetDouble(ref Guid key, double value);
		[PreserveSig] int SetGUID(ref Guid key, ref Guid value);
		[PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
		[PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, uint size);
		[PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
		[PreserveSig] int LockStore();
		[PreserveSig] int UnlockStore();
		[PreserveSig] int GetCount(out uint count);
		[PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
		[PreserveSig] int CopyAllItems(IntPtr destination);
	}

	[ComImport]
	[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IMFMediaType : IMFAttributes { }

	[ComImport]
	[Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IMFMediaBuffer
	{
		[PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
		[PreserveSig] int Unlock();
		[PreserveSig] int GetCurrentLength(out uint currentLength);
		[PreserveSig] int SetCurrentLength(uint currentLength);
		[PreserveSig] int GetMaxLength(out uint maxLength);
	}

	// IMFAttributes slots are redeclared explicitly here so IMFSample methods land at
	// their native vtable slots on .NET Framework COM interop.
	[ComImport]
	[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IMFSample
	{
		[PreserveSig] int GetItem(ref Guid key, IntPtr value);
		[PreserveSig] int GetItemType(ref Guid key, out int type);
		[PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
		[PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
		[PreserveSig] int GetUINT32(ref Guid key, out uint value);
		[PreserveSig] int GetUINT64(ref Guid key, out ulong value);
		[PreserveSig] int GetDouble(ref Guid key, out double value);
		[PreserveSig] int GetGUID(ref Guid key, out Guid value);
		[PreserveSig] int GetStringLength(ref Guid key, out uint length);
		[PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, out uint length);
		[PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
		[PreserveSig] int GetBlobSize(ref Guid key, out uint size);
		[PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint blobSize);
		[PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
		[PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, out IntPtr value);
		[PreserveSig] int SetItem(ref Guid key, IntPtr value);
		[PreserveSig] int DeleteItem(ref Guid key);
		[PreserveSig] int DeleteAllItems();
		[PreserveSig] int SetUINT32(ref Guid key, uint value);
		[PreserveSig] int SetUINT64(ref Guid key, ulong value);
		[PreserveSig] int SetDouble(ref Guid key, double value);
		[PreserveSig] int SetGUID(ref Guid key, ref Guid value);
		[PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
		[PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, uint size);
		[PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
		[PreserveSig] int LockStore();
		[PreserveSig] int UnlockStore();
		[PreserveSig] int GetCount(out uint count);
		[PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
		[PreserveSig] int CopyAllItems(IntPtr destination);

		[PreserveSig] int GetSampleFlags(out uint flags);
		[PreserveSig] int SetSampleFlags(uint flags);
		[PreserveSig] int GetSampleTime(out long time);
		[PreserveSig] int SetSampleTime(long time);
		[PreserveSig] int GetSampleDuration(out long duration);
		[PreserveSig] int SetSampleDuration(long duration);
		[PreserveSig] int GetBufferCount(out uint count);
		[PreserveSig] int GetBufferByIndex(uint index, [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer buffer);
		[PreserveSig] int ConvertToContiguousBuffer([MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer buffer);
		[PreserveSig] int AddBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer buffer);
		[PreserveSig] int RemoveBufferByIndex(uint index);
		[PreserveSig] int RemoveAllBuffers();
		[PreserveSig] int GetTotalLength(out uint length);
		[PreserveSig] int CopyToBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer buffer);
	}

	[ComImport]
	[Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IMFSinkWriter
	{
		[PreserveSig] int AddStream([MarshalAs(UnmanagedType.Interface)] IMFMediaType targetMediaType, out int streamIndex);
		[PreserveSig] int SetInputMediaType(int streamIndex, [MarshalAs(UnmanagedType.Interface)] IMFMediaType inputMediaType, [MarshalAs(UnmanagedType.Interface)] IMFAttributes encodingParameters);
		[PreserveSig] int BeginWriting();
		[PreserveSig] int WriteSample(int streamIndex, [MarshalAs(UnmanagedType.Interface)] IMFSample sample);
		[PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
		[PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
		[PreserveSig] int NotifyEndOfSegment(int streamIndex);
		[PreserveSig] int Flush(int streamIndex);
		[PreserveSig] int Finalize_();
		[PreserveSig] int GetServiceForStream(int streamIndex, ref Guid service, ref Guid interfaceId, out IntPtr result);
		[PreserveSig] int GetStatistics(int streamIndex, IntPtr statistics);
	}

	internal static class MfGuids
	{
		internal static readonly Guid MediaTypeVideo = new Guid("73646976-0000-0010-8000-00AA00389B71");
		internal static readonly Guid MediaTypeAudio = new Guid("73647561-0000-0010-8000-00AA00389B71");
		internal static readonly Guid VideoFormatH264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
		internal static readonly Guid VideoFormatRgb32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
		internal static readonly Guid AudioFormatAac = new Guid("00001610-0000-0010-8000-00AA00389B71");
		internal static readonly Guid AudioFormatPcm = new Guid("00000001-0000-0010-8000-00AA00389B71");

		internal static readonly Guid MtMajorType = new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
		internal static readonly Guid MtSubtype = new Guid("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
		internal static readonly Guid MtAvgBitrate = new Guid("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
		internal static readonly Guid MtMpeg2Profile = new Guid("AD76A80B-2D5C-4E0B-B375-64E520137036");
		internal static readonly Guid MtInterlaceMode = new Guid("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
		internal static readonly Guid MtFrameSize = new Guid("1652C33D-D6B2-4012-B834-72030849A37D");
		internal static readonly Guid MtFrameRate = new Guid("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
		internal static readonly Guid MtPixelAspectRatio = new Guid("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
		internal static readonly Guid MtDefaultStride = new Guid("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
		internal static readonly Guid MtAudioNumChannels = new Guid("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
		internal static readonly Guid MtAudioSamplesPerSecond = new Guid("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
		internal static readonly Guid MtAudioBitsPerSample = new Guid("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
		internal static readonly Guid MtAudioBlockAlignment = new Guid("322DE230-9EEB-43BD-AB7A-FF412251541D");
		internal static readonly Guid MtAudioAvgBytesPerSecond = new Guid("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
		internal static readonly Guid MtAacPayloadType = new Guid("BFBABE79-7434-4D1C-94F0-72A3B9E17188");
		internal static readonly Guid MtAacAudioProfileLevelIndication = new Guid("7632F0E6-9538-4D61-ACDA-EA29C8C14456");
	}
}