using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using BizHawk.BizInvoke;
using BizHawk.Common;
using BizHawk.Common.BufferExtensions;
using BizHawk.Emulation.Common;
using BizHawk.Emulation.Cores.Properties;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	[PortedCore(
		name: CoreNames.Dolphin,
		author: "Dolphin Team; BizHawk port by CasualPokePlayer",
		portedVersion: "5.0-16426",
		portedUrl: "https://github.com/CasualPokePlayer/dolphin",
		singleInstance: true,
		isReleased: false)]
	public partial class Dolphin : IRomInfo
	{
		/// <remarks>
		/// resolved lazily so that merely reflecting over this type (which <see cref="CoreInventory"/> does at startup)
		/// can't throw a <see cref="TypeInitializationException"/> on a machine without the native library
		/// </remarks>
		private static readonly Lazy<LibDolphin> _coreLazy = new(static () => BizInvoker.GetInvoker<LibDolphin>(
			new DynamicLibraryImportResolver(
				OSTailoredCode.IsUnixHost ? "libdolphin-emu-nogui.so" : "dolphin-emu-nogui.dll",
				hasLimitedLifetime: false), // Dolphin's state is process-global and we are singleInstance, so never unload it
			CallingConventionAdapters.Native));

		private static LibDolphin _core => _coreLazy.Value;

		/// <summary>
		/// The running core, if any. Needed because <see cref="QuickHashDisc"/> is called by the frontend
		/// before a core exists, and the native disc subsystem must only be initialised once.
		/// </summary>
		private static Dolphin _currentCore;

		private Thread _hostThread;

		private int _hostThreadId;

		/// <summary>whether two discs were loaded, i.e. whether the "Swap Disc" button exists</summary>
		private readonly bool _multiDisc;

		private bool HostRunning => _hostThread?.IsAlive ?? false;

		private void ExecuteHostThread(string gamePath, string secondDiscPath)
		{
			if (!OSTailoredCode.IsUnixHost) _hostThreadId = unchecked((int) GetCurrentThreadId());

			var args = new List<string>
			{
				"BizHawk",
				$"--user={USER_DIR}",
				$"--exec={gamePath}",
				"--platform=headless",
			};

			if (secondDiscPath is not null) args.Add($"--exec={secondDiscPath}");

			ApplyNativeSettings(args);
			ApplyNativeOverrides(args);
			_core.Dolphin_Main(args.Count, args.ToArray());
		}

		/// <remarks>
		/// These directories are deliberately relative to the working directory: the prebuilt native library
		/// looks for the <c>Sys</c> folder (GC/Wii system files, font ROMs) there rather than under <c>--user</c>.
		/// Do not relocate them without rebuilding the native library to match.
		/// </remarks>
		private const string USER_DIR = "DolphinUserFolder";
		private const string SYS_DIR = "Sys";

		/// <summary>
		/// Power-user experimentation channel: extra native config overrides from an optional
		/// <c>dolphin-native-overrides.txt</c> in the working directory (same convention as
		/// <see cref="USER_DIR"/>), one <c>Section.Category.Key=Value</c> per line; <c>#</c> and
		/// <c>;</c> start comments. Applied after the settings-derived config, so overrides win.
		/// These affect emulation (e.g. <c>Dolphin.Core.GFXBackend=Vulkan</c> to test a different
		/// video backend) - use at your own risk.
		/// </summary>
		private static void ApplyNativeOverrides(List<string> args)
		{
			try
			{
				if (!File.Exists(NATIVE_OVERRIDES_FILE)) return;
				foreach (var line in File.ReadAllLines(NATIVE_OVERRIDES_FILE))
				{
					var trimmed = line.Trim();
					if (trimmed.Length == 0 || trimmed[0] is '#' or ';') continue;
					args.Add($"--config={trimmed}");
				}
			}
			catch
			{
				// an unreadable overrides file must never prevent boot
			}
		}

		private const string NATIVE_OVERRIDES_FILE = "dolphin-native-overrides.txt";

		private static void ExtractEmbeddedZip(Lazy<byte[]> resource, string destDir)
		{
			Directory.CreateDirectory(destDir);
			using var zip = new ZipArchive(Zstd.DecompressZstdStream(new MemoryStream(resource.Value)), ZipArchiveMode.Read, leaveOpen: false);
			zip.ExtractToDirectory(destDir);
		}

		[CoreConstructor(VSystemID.Raw.GameCube)]
		[CoreConstructor(VSystemID.Raw.Wii)]
		public Dolphin(CoreLoadParameters<DolphinSettings, DolphinSyncSettings> lp)
		{
			if (lp.Discs.Count > 2 || (lp.Discs.Count > 0 && lp.Roms.Count > 0))
			{
				throw new InvalidOperationException($"Expected at most 2 discs and no ROMs, or 1 ROM and no discs (got {lp.Discs.Count} disc(s) and {lp.Roms.Count} ROM(s))");
			}

			if (lp.Discs.Count == 0 && lp.Roms.Count == 0) throw new InvalidOperationException("No disc or ROM provided for emulation");

			// Dolphin's state is process-global, so there can only ever be one instance; tear down any survivor first
			_currentCore?.Dispose();
			_currentCore = this;

			var gamePath = lp.Discs.FirstOrDefault()?.DiscPath ?? lp.Roms[0].RomPath;
			_multiDisc = lp.Discs.Count == 2;

			_serviceProvider = new(this);

			_settings = lp.Settings ?? new();
			_syncSettings = lp.SyncSettings ?? new();

			IsWii = lp.Game.System == VSystemID.Raw.Wii;

			if (lp.Discs.Count > 0) RomDetails = $"{lp.Game.Name}\r\nCRC32:{lp.Game.Hash}";

			if (Directory.Exists(USER_DIR)) Directory.Delete(USER_DIR, recursive: true);
			Directory.CreateDirectory($"{USER_DIR}/GC/EUR");
			Directory.CreateDirectory($"{USER_DIR}/GC/USA");
			Directory.CreateDirectory($"{USER_DIR}/GC/JAP");

			// Dolphin's own per-game settings database (INI overrides); these affect emulation, hence they're gated on a sync setting
			if (_syncSettings.ApplyPerGameSettings) ExtractEmbeddedZip(Resources.DOLPHIN_GAME_SETTINGS, USER_DIR);

			// GC/Wii system files: DSP LLE ROMs, font ROMs, Wii shared2 contents
			if (!Directory.Exists(SYS_DIR)) ExtractEmbeddedZip(Resources.DOLPHIN_SYS_FOLDER, SYS_DIR);

			var ipl = lp.Comm.CoreFileProvider.GetFirmware(new("GC_Wii", "IPL"));
			if (!IsWii && !_syncSettings.MainSettings.SkipIPL)
			{
				if (ipl is null)
				{
					Dispose();
					throw new MissingFirmwareException("Couldn't find the GameCube IPL ROM. Either provide it, or enable \"Skip IPL\" in Dolphin's sync settings to boot straight into the game.");
				}
				WriteIplEverywhere(ipl);
			}
			else if (ipl is not null)
			{
				// even when skipping the IPL, Dolphin still reads the font ROMs out of it
				lp.Comm.Notify("Using user-provided IPL ROM for font files");
				WriteIplEverywhere(ipl);
			}

			if (!_syncSettings.MainSettings.DSPHLE)
			{
				var coef = lp.Comm.CoreFileProvider.GetFirmware(new("GC_Wii", "DSP_COEF"));
				if (coef is not null)
				{
					lp.Comm.Notify("Using user-provided DSP COEF");
					File.WriteAllBytes($"{USER_DIR}/GC/dsp_coef.bin", coef);
				}

				var irom = lp.Comm.CoreFileProvider.GetFirmware(new("GC_Wii", "DSP_IROM"));
				if (irom is not null)
				{
					lp.Comm.Notify("Using user-provided DSP IROM");
					File.WriteAllBytes($"{USER_DIR}/GC/dsp_rom.bin", irom);
				}
			}

			DeterministicEmulation = lp.DeterministicEmulationRequested || _syncSettings.MainSettings.EnableCustomRTC;
			ControllerDefinition = CreateControllerDefinition();

			// these have to be installed before the host thread starts,
			// else the core can poll input before the first frame advance (a race)
			_mpluscb = n => Wiimotes[n].HasMotionPlus;
			_extensioncb = n => Wiimotes[n].Extension;
			_core.Dolphin_SetConfigCallbacks(_mpluscb, _extensioncb);

			_gcpadcb = GCPadCallback;
			_core.Dolphin_SetGCPadCallback(_gcpadcb);

			if (IsWii)
			{
				_wiipadcb = WiiPadCallback;
				_core.Dolphin_SetWiiPadCallback(_wiipadcb);
			}

			_hostThread = new(() => ExecuteHostThread(gamePath, _multiDisc ? lp.Discs[1].DiscPath : null)) { IsBackground = true };
			_hostThread.Start();

			while (!_core.Dolphin_BootupSuccessful())
			{
				if (!HostRunning)
				{
					Dispose();
					throw new InvalidOperationException("Dolphin failed to boot up!");
				}

				Thread.Sleep(1);
			}

			_vhandle = GCHandle.Alloc(_vbuf, GCHandleType.Pinned);
			_core.Dolphin_SetFrameBuffer(_vhandle.AddrOfPinnedObject());

			InitMemoryDomains();

			ResetCounters();
		}

		private void WriteIplEverywhere(byte[] ipl)
		{
			// CHECKME: writing the same IPL to all three regions is probably wrong,
			// but the native library has no way to be told which region the user meant
			File.WriteAllBytes($"{USER_DIR}/GC/EUR/IPL.bin", ipl);
			File.WriteAllBytes($"{USER_DIR}/GC/USA/IPL.bin", ipl);
			File.WriteAllBytes($"{USER_DIR}/GC/JAP/IPL.bin", ipl);
		}

		public string RomDetails { get; private set; }

		private bool IsWii { get; }

		public class Wiimote
		{
			public bool IsActive { get; }

			public bool HasMotionPlus { get; }

			public DolphinWiiPadSettings.WiimoteExtensions Extension { get; }

			public Wiimote(bool active, bool motionPlus, DolphinWiiPadSettings.WiimoteExtensions extension)
			{
				IsActive = active;
				HasMotionPlus = motionPlus;
				Extension = extension;
			}
		}

		public Wiimote[] Wiimotes { get; private set; }

		private ControllerDefinition CreateControllerDefinition()
		{
			var ret = new ControllerDefinition($"{(IsWii ? "Wii" : "GameCube")} Controls");

			var gcSlots = new[]
			{
				_syncSettings.MainSettings.SIDevice0,
				_syncSettings.MainSettings.SIDevice1,
				_syncSettings.MainSettings.SIDevice2,
				_syncSettings.MainSettings.SIDevice3,
			};

			for (var i = 0; i < 4; i++)
			{
				if (gcSlots[i] is not DolphinMainSettings.SIDevices.Controller) continue;

				var n = i + 1;
				foreach (var b in Enum.GetValues(typeof(LibDolphin.PadButtons)).Cast<LibDolphin.PadButtons>())
				{
					ret.BoolButtons.Add($"P{n} {b}");
				}

				var stickSpec = new AxisSpec(0.RangeTo(255), 128);
				ret.AddAxis($"P{n} Main Stick X", stickSpec.Range, stickSpec.Neutral);
				ret.AddAxis($"P{n} Main Stick Y", stickSpec.Range, stickSpec.Neutral);
				ret.AddAxis($"P{n} C Stick X", stickSpec.Range, stickSpec.Neutral);
				ret.AddAxis($"P{n} C Stick Y", stickSpec.Range, stickSpec.Neutral);
				foreach (VirtualAnalogStick stick in DolphinStickDefinitions.GameCubeSticks(n))
				{
					ret.VirtualAnalogSticks.Add(stick);
				}
				ret.AddAxis($"P{n} Analog L", stickSpec.Range, stickSpec.Min);
				ret.AddAxis($"P{n} Analog R", stickSpec.Range, stickSpec.Min);
			}

			if (IsWii)
			{
				var wms = _syncSettings.WiiPadSettings;
				Wiimotes =
				[
					new(wms.Wiimote1, wms.Wiimote1MotionPlus, wms.Wiimote1Extension),
					new(wms.Wiimote2, wms.Wiimote2MotionPlus, wms.Wiimote2Extension),
					new(wms.Wiimote3, wms.Wiimote3MotionPlus, wms.Wiimote3Extension),
					new(wms.Wiimote4, wms.Wiimote4MotionPlus, wms.Wiimote4Extension),
				];

				for (var i = 0; i < 4; i++)
				{
					if (!Wiimotes[i].IsActive) continue;

					var n = i + 5;
					foreach (var b in Enum.GetValues(typeof(LibDolphin.CoreButtons)).Cast<LibDolphin.CoreButtons>())
					{
						ret.BoolButtons.Add($"P{n} {b}");
					}

					ret.AddXYZTriple($"P{n} Accelerometer {{0}}", 0.RangeTo(1023), 512);

					ret.BoolButtons.Add($"P{n} IR Pointing");
					ret.AddXYPair($"P{n} IR {{0}}", AxisPairOrientation.LeftAndDown, 0.RangeTo(1023), 512, 0.RangeTo(767), 384);
					ret.AddAxis($"P{n} IR Z", 0.RangeTo(767), 384);

					if (Wiimotes[i].HasMotionPlus)
					{
						// TODO: MotionPlus axes
					}

					switch (Wiimotes[i].Extension)
					{
						case DolphinWiiPadSettings.WiimoteExtensions.None:
							break;
						case DolphinWiiPadSettings.WiimoteExtensions.Nunchuk:
							// TODO: Nunchuk stick/buttons
							break;
						default:
							throw new InvalidOperationException($"Unknown Wiimote extension: {Wiimotes[i].Extension}");
					}
				}
			}

			if (_multiDisc) ret.BoolButtons.Add("Swap Disc");
			ret.BoolButtons.Add("Reset");

			return ret.MakeImmutable();
		}

		// these must be kept alive for as long as the host thread runs, so they're fields and not locals
		private readonly LibDolphin.MPlusConfigCallback _mpluscb;
		private readonly LibDolphin.ExtensionConfigCallback _extensioncb;

		/// <summary>
		/// CRC32 of a GC/Wii disc, computed by Dolphin itself so that every container format it can boot is understood.
		/// Called by the frontend during ROM loading, i.e. possibly before any core exists.
		/// </summary>
		public static string QuickHashDisc(string path)
		{
			var hash = new byte[4];
			_core.Dolphin_HashMedium(path, hash, quick: true, doInit: _currentCore is null);
			return hash.BytesToHexString();
		}
	}
}
