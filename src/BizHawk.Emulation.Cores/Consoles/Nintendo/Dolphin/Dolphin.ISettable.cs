using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;

using Newtonsoft.Json;

using BizHawk.Common;
using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin : ISettable<Dolphin.DolphinSettings, Dolphin.DolphinSyncSettings>
	{
		private DolphinSettings _settings;
		private DolphinSyncSettings _syncSettings;

		public DolphinSettings GetSettings()
			=> _settings.Clone();

		public PutSettingsDirtyBits PutSettings(DolphinSettings o)
		{
			_settings = o;
			return PutSettingsDirtyBits.None;
		}

		public DolphinSyncSettings GetSyncSettings()
			=> _syncSettings.Clone();

		public PutSettingsDirtyBits PutSyncSettings(DolphinSyncSettings o)
		{
			var ret = DolphinSyncSettings.NeedsReboot(_syncSettings, o);
			_syncSettings = o;
			return ret ? PutSettingsDirtyBits.RebootCore : PutSettingsDirtyBits.None;
		}

		/// <remarks>
		/// Dolphin is configured through its <c>--config=Section.Category.Key=Value</c> CLI form,
		/// so each sync-settings group is flattened into that shape before the host thread boots.
		/// </remarks>
		private void ApplyNativeSettings(List<string> config)
		{
			var ss = _syncSettings.Clone();
			ss.MainSettings.EnableCustomRTC = DeterministicEmulation;
			ss.MainSettings.ApplyNativeSettings(config);
			ss.WiiPadSettings.ApplyNativeSettings(config);
			ss.GFXSettings.ApplyNativeSettings(config);
			ss.SYSCONFSettings.ApplyNativeSettings(config);
		}

		[CoreSettings]
		public class DolphinSettings
		{
			[DisplayName("Use Compressed States")]
			[Description("Compress savestates. Saves a lot of space (Dolphin's states are large) at some CPU cost.")]
			[DefaultValue(true)]
			public bool UseCompressedStates { get; set; }

			[DisplayName("GPU Lag Frames")]
			[Description("Mark a frame as a lag frame when the GPU presented no new frame. If off, only frames where the CPU didn't run are counted as lag.")]
			[DefaultValue(true)]
			public bool GPULagFrames { get; set; }

			public DolphinSettings()
				=> SettingsUtil.SetDefaultValues(this);

			public DolphinSettings Clone()
				=> (DolphinSettings) MemberwiseClone();
		}

		[CoreSettings]
		public class DolphinSyncSettings
		{
			[DisplayName("Apply Per-Game Settings")]
			[Description("Apply Dolphin's own per-game INI overrides (from its GameSettings database). These affect emulation, so this is a sync setting.")]
			[DefaultValue(true)]
			public bool ApplyPerGameSettings { get; set; }

			public DolphinMainSettings MainSettings { get; set; }

			public DolphinWiiPadSettings WiiPadSettings { get; set; }

			public DolphinGFXSettings GFXSettings { get; set; }

			public DolphinSYSCONFSettings SYSCONFSettings { get; set; }

			public DolphinSyncSettings()
			{
				SettingsUtil.SetDefaultValues(this);
				MainSettings = new();
				WiiPadSettings = new();
				GFXSettings = new();
				SYSCONFSettings = new();
			}

			public DolphinSyncSettings Clone()
				=> new()
				{
					ApplyPerGameSettings = ApplyPerGameSettings,
					MainSettings = MainSettings.Clone(),
					WiiPadSettings = WiiPadSettings.Clone(),
					GFXSettings = GFXSettings.Clone(),
					SYSCONFSettings = SYSCONFSettings.Clone(),
				};

			public static bool NeedsReboot(DolphinSyncSettings x, DolphinSyncSettings y)
				=> x.ApplyPerGameSettings != y.ApplyPerGameSettings
					|| !DeepEquality.DeepEquals(x.MainSettings, y.MainSettings)
					|| !DeepEquality.DeepEquals(x.WiiPadSettings, y.WiiPadSettings)
					|| !DeepEquality.DeepEquals(x.GFXSettings, y.GFXSettings)
					|| !DeepEquality.DeepEquals(x.SYSCONFSettings, y.SYSCONFSettings);
		}

		/// <summary>
		/// A group of settings which maps 1:1 onto a section of Dolphin's own config,
		/// driven entirely by the <see cref="DisplayNameAttribute"/>/<see cref="CategoryAttribute"/>/
		/// <see cref="DefaultValueAttribute"/>/<see cref="BrowsableAttribute"/> on each property.
		/// </summary>
		public abstract class DolphinNativeSettings
		{
			/// <value>the config section name, e.g. <c>Dolphin</c>, <c>Graphics</c> or <c>SYSCONF</c></value>
			[JsonIgnore] // nicely propagates to any derived class
			protected abstract string NativeName { get; }

			/// <remarks>
			/// the concrete derived type, used to enumerate its properties.
			/// An abstract member rather than <c>GetType()</c> so that each group names its own type explicitly.
			/// </remarks>
			[JsonIgnore]
			protected abstract Type NativeSettingsType { get; }

			private static bool IsBrowsable(PropertyInfo prop)
			{
				var brAttr = prop.GetCustomAttribute<BrowsableAttribute>();
				return brAttr?.Browsable ?? throw new InvalidOperationException($"Could not determine if setting {prop.Name} should be browsable!");
			}

			private static object GetDefaultValue(PropertyInfo prop)
			{
				var dvAttr = prop.GetCustomAttribute<DefaultValueAttribute>();
				return dvAttr?.Value ?? throw new InvalidOperationException($"Could not find default value for setting {prop.Name}!");
			}

			private static string GetCategoryName(PropertyInfo prop)
			{
				var cAttr = prop.GetCustomAttribute<CategoryAttribute>();
				return cAttr?.Category ?? throw new InvalidOperationException($"Could not find category name for setting {prop.Name}!");
			}

			/// <returns>the <see cref="DisplayAttribute"/> name if the property has a <see cref="DescribableEnumConverter"/>, else the raw enum value as an <see cref="int"/></returns>
			private static object MaybeGetDisplayName(object s, PropertyInfo prop)
			{
				if (prop.GetCustomAttribute<TypeConverterAttribute>() is null) return (int) s; // just return the raw value
				// must be DescribableEnumConverter, so use display names here
				var converter = new DescribableEnumConverter(s.GetType());
				return converter.ConvertTo(context: null, culture: null, value: s, destType: typeof(string));
			}

			private static readonly DateTime _unixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

			private static string ToNativeConfigString(object s, PropertyInfo prop)
			{
				var t = s.GetType();
				// NOTE: invariant culture throughout, as Dolphin's config parser is not locale-aware
				if (t == typeof(bool)) return ((bool) s).ToString();
				if (t == typeof(int)) return ((int) s).ToString(CultureInfo.InvariantCulture);
				if (t == typeof(float)) return ((float) s).ToString(CultureInfo.InvariantCulture);
				if (t == typeof(DateTime)) return ((uint) ((DateTime) s - _unixEpoch).TotalSeconds).ToString(CultureInfo.InvariantCulture);
				if (t == typeof(string)) return (string) s;

				if (t.IsEnum)
				{
					if (Enum.GetUnderlyingType(t) != typeof(int)) throw new InvalidOperationException($"Enums must be ints ({prop.Name} is a {t.Name})!");

					return MaybeGetDisplayName(s, prop) switch
					{
						string name => name,
						int i => i.ToString(CultureInfo.InvariantCulture),
						_ => throw new InvalidOperationException($"Could not determine native config string for setting {prop.Name}!"),
					};
				}

				throw new InvalidOperationException($"Could not determine native config string for setting {prop.Name}!");
			}

			public void ApplyNativeSettings(List<string> config)
			{
				foreach (var prop in NativeSettingsType.GetProperties())
				{
					// settings which aren't browsable can't be configured by the user,
					// so pin them to their defaults to keep emulation deterministic
					if (prop.CanWrite && !IsBrowsable(prop)) prop.SetValue(this, GetDefaultValue(prop));

					if (prop.CanRead)
					{
						config.Add($"--config={NativeName}.{GetCategoryName(prop)}.{prop.Name}={ToNativeConfigString(prop.GetValue(this), prop)}");
					}
				}
			}
		}

		[CoreSettings]
		public class DolphinMainSettings : DolphinNativeSettings
		{
			protected override string NativeName => "Dolphin";

			protected override Type NativeSettingsType => typeof(DolphinMainSettings);

			public DolphinMainSettings()
				=> SettingsUtil.SetDefaultValues(this);

			public DolphinMainSettings Clone()
				=> (DolphinMainSettings) MemberwiseClone();

			[DisplayName("Skip IPL")]
			[Description("Boot straight into the game instead of playing the GameCube IPL intro. Ignored for Wii. If off, the IPL ROM must be provided as firmware.")]
			[Category("Core")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool SkipIPL { get; set; }

			public enum CPUCores
			{
				Interpreter = 0,
				JIT64 = 1,
				// we don't support ARM
				CachedInterpreter = 5,
			}

			[DisplayName("CPU Core")]
			[Description("Interpreter is the slowest but most accurate; JIT64 is the default.")]
			[Category("Core")]
			[DefaultValue(CPUCores.JIT64)]
			[Browsable(true)]
			public CPUCores CPUCore { get; set; }

			[DisplayName("JIT Follow Branch")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool JITFollowBranch { get; set; }

			[DisplayName("Fast Mem")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool Fastmem { get; set; }

			[DisplayName("DSP HLE")]
			[Description("High-level emulation of the audio DSP. Turn off to use the LLE DSP, which needs the DSP COEF and DSP IROM firmware.")]
			[Category("Core")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool DSPHLE { get; set; }

			[DisplayName("Timing Variance")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(40)]
			[Browsable(false)]
			public int TimingVariance { get; set; }

			[DisplayName("CPU Thread")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool CPUThread { get; set; }

			[DisplayName("Sync On Skip Idle")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool SyncOnSkipIdle { get; set; }

			public enum GCLanguages
			{
				English = 0,
				German = 1,
				French = 2,
				Spanish = 3,
				Italian = 4,
				Dutch = 5,
				Unknown,
			}

			[DisplayName("Selected Language")]
			[Description("GameCube language. For Wii, use the SYSCONF Language setting instead.")]
			[Category("Core")]
			[DefaultValue(GCLanguages.English)]
			[Browsable(true)]
			public GCLanguages SelectedLanguage { get; set; }

			[DisplayName("Override Region Settings")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool OverrideRegionSettings { get; set; }

			[DisplayName("DPL2 Decoder")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool DPL2Decoder { get; set; }

			public enum DPL2Qualitys
			{
				Lowest = 0,
				Low = 1,
				High = 2,
				Highest = 3,
			}

			[DisplayName("DPL2 Quality")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(DPL2Qualitys.High)]
			[Browsable(true)]
			public DPL2Qualitys DPL2Quality { get; set; }

			public enum MemoryCardSizes
			{
				OneHundredTwentyEightMBit = -1,
				FourMBit = 0,
				EightMBit = 1,
				SixteenMBit = 2,
				ThiryTwoMBit = 3,
				SixtyFourMBit = 4,
			}

			[DisplayName("Memory Card Size")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(MemoryCardSizes.OneHundredTwentyEightMBit)]
			[Browsable(true)]
			public MemoryCardSizes MemoryCardSize { get; set; }

			public enum EXIDeviceType
			{
				Dummy = 0,
				MemoryCardFolder = 8,
				None = 0xFF,
			}

			[DisplayName("Slot A")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(EXIDeviceType.MemoryCardFolder)]
			[Browsable(true)]
			public EXIDeviceType SlotA { get; set; }

			[DisplayName("Slot B")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(EXIDeviceType.None)]
			[Browsable(true)]
			public EXIDeviceType SlotB { get; set; }

			public enum EXIDeviceTypeSP1
			{
				Dummy = 0,
				None = 0xFF,
			}

			[DisplayName("Serial Port 1")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(EXIDeviceTypeSP1.None)]
			[Browsable(true)]
			public EXIDeviceTypeSP1 SerialPort1 { get; set; }

			public enum SIDevices
			{
				None = 0,
				Controller = 6,
				// TODO: SteeringWheel = 8, DanceMat = 9, TaruKonga = 10
			}

			[DisplayName("SI Device 0")]
			[Description("What's plugged into serial interface slot 0 (player 1).")]
			[Category("Core")]
			[DefaultValue(SIDevices.Controller)]
			[Browsable(true)]
			public SIDevices SIDevice0 { get; set; }

			[DisplayName("SI Device 1")]
			[Description("What's plugged into serial interface slot 1 (player 2).")]
			[Category("Core")]
			[DefaultValue(SIDevices.None)]
			[Browsable(true)]
			public SIDevices SIDevice1 { get; set; }

			[DisplayName("SI Device 2")]
			[Description("What's plugged into serial interface slot 2 (player 3).")]
			[Category("Core")]
			[DefaultValue(SIDevices.None)]
			[Browsable(true)]
			public SIDevices SIDevice2 { get; set; }

			[DisplayName("SI Device 3")]
			[Description("What's plugged into serial interface slot 3 (player 4).")]
			[Category("Core")]
			[DefaultValue(SIDevices.None)]
			[Browsable(true)]
			public SIDevices SIDevice3 { get; set; }

			[DisplayName("Wii SD Card")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool WiiSDCard { get; set; }

			[DisplayName("Wii Keyboard")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool WiiKeyboard { get; set; }

			[DisplayName("Wiimote Enable Speaker")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool WiimoteEnableSpeaker { get; set; }

			[DisplayName("MMU")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool MMU { get; set; }

			[DisplayName("BB Dump Port")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(-1)]
			[Browsable(false)]
			public int BBDumpPort { get; set; }

			[DisplayName("Sync GPU")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(true)]
			[Browsable(false)]
			public bool SyncGPU { get; set; }

			[DisplayName("Sync Gpu Max Distance")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(200000)]
			[Browsable(false)]
			public int SyncGpuMaxDistance { get; set; }

			[DisplayName("Sync Gpu Min Distance")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(-200000)]
			[Browsable(false)]
			public int SyncGpuMixDistance { get; set; }

			[DisplayName("Sync Gpu Overclock")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(1.0f)]
			[Browsable(false)]
			public float SyncGpuOverclock { get; set; }

			[DisplayName("Fast Disc Speed")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool FastDiscSpeed { get; set; }

			[DisplayName("Low DCBZ Hack")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool LowDCBZHack { get; set; }

			[DisplayName("Float Exceptions")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool FloatExceptions { get; set; }

			[DisplayName("Div By Zero Exceptions")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool DivByZeroExceptions { get; set; }

			[DisplayName("FPRF")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool FPRF { get; set; }

			[DisplayName("Accurate NaNs")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool AccurateNaNs { get; set; }

			[DisplayName("Disable ICache")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool DisableICache { get; set; }

			[DisplayName("Emulation Speed")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(0.0f)]
			[Browsable(false)]
			public float EmulationSpeed { get; set; }

			[DisplayName("Overclock")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(1.0f)]
			[Browsable(false)]
			public float Overclock { get; set; }

			[DisplayName("Overclock Enable")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool OverclockEnable { get; set; }

			[DisplayName("RAM Override Enable")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool RAMOverrideEnable { get; set; }

			[DisplayName("MEM1 Size")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(0x01800000)]
			[Browsable(false)]
			public int MEM1Size { get; set; }

			[DisplayName("MEM2 Size")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(0x04000000)]
			[Browsable(false)]
			public int MEM2Size { get; set; }

			public enum GFXBackends
			{
				// OGL can't be used here (the software renderer needs it too), so it's omitted
				[Display(Name = "D3D")]
				D3D = 1,
				[Display(Name = "D3D12")]
				D3D12 = 2,
				[Display(Name = "Vulkan")]
				Vulkan = 3,
				[Display(Name = "Null")]
				Null = 5,
			}

			[JsonIgnore]
			private GFXBackends _gfxBackend;

			[DisplayName("GFX Backend")]
			[Description("Which host graphics API Dolphin renders with. D3D/D3D12 are Windows-only and fall back to Vulkan elsewhere.")]
			[Category("Core")]
			[DefaultValue(GFXBackends.D3D)]
			[TypeConverter(typeof(DescribableEnumConverter))]
			[Browsable(true)]
			public GFXBackends GFXBackend
			{
				get => _gfxBackend;
				set => _gfxBackend = OSTailoredCode.IsUnixHost && value is GFXBackends.D3D or GFXBackends.D3D12
					? GFXBackends.Vulkan
					: value;
			}

			public enum GPUDeterminismModes
			{
				[Display(Name = "fake-completion")]
				FakeCompletion = 2,
			}

			[DisplayName("GPU Determinism Mode")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(GPUDeterminismModes.FakeCompletion)]
			[TypeConverter(typeof(DescribableEnumConverter))]
			[Browsable(false)]
			public GPUDeterminismModes GPUDeterminismMode { get; set; }

			[DisplayName("Override Boot IOS")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(-1)]
			[Browsable(false)]
			public int OverrideBootIOS { get; set; }

			[DisplayName("Enable Custom RTC")]
			[Description("Forced true when recording a movie.")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool EnableCustomRTC { get; set; }

			[DisplayName("Custom RTC Value")]
			[Description("The emulated clock's starting time. Only used when Enable Custom RTC is on (or when recording a movie).")]
			[Category("Core")]
			[DefaultValue(typeof(DateTime), "2010-01-01")]
			[TypeConverter(typeof(BizDateTimeConverter))]
			[Browsable(true)]
			public DateTime CustomRTCValue { get; set; }

			public enum Regions
			{
				NTSC_J = 0,
				NTSC_U = 1,
				PAL = 2,
				Unknown = 3,
				NTSC_K = 4,
			}

			[DisplayName("Fallback Region")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(Regions.NTSC_U)]
			[Browsable(true)]
			public Regions FallbackRegion { get; set; }

			[DisplayName("Auto Disc Change")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool AutoDiscChange { get; set; }

			[DisplayName("Wii SD Card Allow Writes")]
			[Description("")]
			[Category("Core")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool WiiSDCardAllowWrites { get; set; }

			[DisplayName("DSP Thread")]
			[Description("")]
			[Category("DSP")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool DSPThread { get; set; }

			[DisplayName("Enable JIT")]
			[Description("")]
			[Category("DSP")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool EnableJIT { get; set; }

			public enum DSPBackends
			{
				[Display(Name = "No Audio Output")]
				NoAudioOutput = 0,
			}

			[DisplayName("Backend")]
			[Description("BizHawk pulls samples out of the core itself, so the native audio backend is always disabled.")]
			[Category("DSP")]
			[DefaultValue(DSPBackends.NoAudioOutput)]
			[TypeConverter(typeof(DescribableEnumConverter))]
			[Browsable(false)]
			public DSPBackends Backend { get; set; }

			[DisplayName("Use Panic Handlers")]
			[Description("")]
			[Category("Interface")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool UsePanicHandlers { get; set; }

			[DisplayName("Abort On Panic Alert")]
			[Description("")]
			[Category("Interface")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool AbortOnPanicAlert { get; set; }

			[DisplayName("On Screen Display Messages")]
			[Description("")]
			[Category("Interface")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool OnScreenDisplayMessages { get; set; }
		}

		[CoreSettings]
		public class DolphinWiiPadSettings
		{
			/// <remarks>
			/// custom rather than reflection-driven, as the keys share categories but are otherwise unique.
			/// NOTE: some of these settings can't be set through the CLI,
			/// so the native library calls back into <see cref="Dolphin._mpluscb"/>/<see cref="Dolphin._extensioncb"/> instead.
			/// </remarks>
			public void ApplyNativeSettings(List<string> config)
			{
				for (var i = 1; i <= 4; i++)
				{
					var active = i switch
					{
						1 => Wiimote1,
						2 => Wiimote2,
						3 => Wiimote3,
						4 => Wiimote4,
						_ => throw new InvalidOperationException($"Unknown Wiimote index: {i}"),
					};
					config.Add($"--config=Wiimote.Wiimote{i}.Source={(active ? 1 : 0).ToString(CultureInfo.InvariantCulture)}");
				}

				config.Add("--config=Wiimote.BalanceBoard.Source=0");
			}

			public DolphinWiiPadSettings()
				=> SettingsUtil.SetDefaultValues(this);

			public DolphinWiiPadSettings Clone()
				=> (DolphinWiiPadSettings) MemberwiseClone();

			public enum WiimoteExtensions
			{
				None = 0,
				Nunchuk = 1,
				// TODO: Classic Controller, Guitar, Drum Kit, Balance Board
			}

			[DisplayName("Wiimote Active")]
			[Description("")]
			[Category("Wiimote 1")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool Wiimote1 { get; set; }

			[DisplayName("Wiimote MotionPlus Attached")]
			[Description("")]
			[Category("Wiimote 1")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool Wiimote1MotionPlus { get; set; }

			[DisplayName("Wiimote Extension")]
			[Description("")]
			[Category("Wiimote 1")]
			[DefaultValue(WiimoteExtensions.Nunchuk)]
			[Browsable(true)]
			public WiimoteExtensions Wiimote1Extension { get; set; }

			[DisplayName("Wiimote Active")]
			[Description("")]
			[Category("Wiimote 2")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool Wiimote2 { get; set; }

			[DisplayName("Wiimote MotionPlus Attached")]
			[Description("")]
			[Category("Wiimote 2")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool Wiimote2MotionPlus { get; set; }

			[DisplayName("Wiimote Extension")]
			[Description("")]
			[Category("Wiimote 2")]
			[DefaultValue(WiimoteExtensions.None)]
			[Browsable(true)]
			public WiimoteExtensions Wiimote2Extension { get; set; }

			[DisplayName("Wiimote Active")]
			[Description("")]
			[Category("Wiimote 3")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool Wiimote3 { get; set; }

			[DisplayName("Wiimote MotionPlus Attached")]
			[Description("")]
			[Category("Wiimote 3")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool Wiimote3MotionPlus { get; set; }

			[DisplayName("Wiimote Extension")]
			[Description("")]
			[Category("Wiimote 3")]
			[DefaultValue(WiimoteExtensions.None)]
			[Browsable(true)]
			public WiimoteExtensions Wiimote3Extension { get; set; }

			[DisplayName("Wiimote Active")]
			[Description("")]
			[Category("Wiimote 4")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool Wiimote4 { get; set; }

			[DisplayName("Wiimote MotionPlus Attached")]
			[Description("")]
			[Category("Wiimote 4")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool Wiimote4MotionPlus { get; set; }

			[DisplayName("Wiimote Extension")]
			[Description("")]
			[Category("Wiimote 4")]
			[DefaultValue(WiimoteExtensions.None)]
			[Browsable(true)]
			public WiimoteExtensions Wiimote4Extension { get; set; }
		}

		[CoreSettings]
		public class DolphinGFXSettings : DolphinNativeSettings
		{
			protected override string NativeName => "Graphics";

			protected override Type NativeSettingsType => typeof(DolphinGFXSettings);

			public DolphinGFXSettings()
				=> SettingsUtil.SetDefaultValues(this);

			public DolphinGFXSettings Clone()
				=> (DolphinGFXSettings) MemberwiseClone();

			[DisplayName("VSync")]
			[Description("")]
			[Category("Hardware")]
			[DefaultValue(false)]
			[Browsable(false)]
			public bool VSync { get; set; }

			[DisplayName("Adapter")]
			[Description("Refer to Dolphin's adapter order for what these values mean.")]
			[Category("Hardware")]
			[DefaultValue(0)]
			[Browsable(true)]
			public int Adapter { get; set; }

			/// <remarks>lowercase first letter is deliberate: this must match Dolphin's own INI key exactly</remarks>
			[DisplayName("Widescreen Hack")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool wideScreenHack { get; set; }

			public enum AspectModes
			{
				Auto,
				AnalogWide,
				Analog,
			}

			[DisplayName("Aspect Ratio")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(AspectModes.Auto)]
			[Browsable(true)]
			public AspectModes AspectRatio { get; set; }

			[DisplayName("Crop")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool Crop { get; set; }

			public enum TextureCache
			{
				Safe = 0,
				Mid = 512,
				Fast = 128,
			}

			[DisplayName("Texture Cache Accuracy")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(TextureCache.Fast)]
			[Browsable(true)]
			public TextureCache SafeTextureCacheColorSamples { get; set; }

			[DisplayName("Enable GPU Texture Decoding")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool EnableGPUTextureDecoding { get; set; }

			[DisplayName("Enable Pixel Lighting")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool EnablePixelLighting { get; set; }

			[DisplayName("Fast Depth Calc")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool FastDepthCalc { get; set; }

			[DisplayName("Disable Fog")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool DisableFog { get; set; }

			[DisplayName("Backend Multithreading")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool BackendMultithreading { get; set; }

			[DisplayName("Save Texture Cache To State")]
			[Description("")]
			[Category("Settings")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool SaveTextureCacheToState { get; set; }

			[DisplayName("Force Filtering")]
			[Description("")]
			[Category("Enhancements")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool ForceFiltering { get; set; }

			[DisplayName("Force True Color")]
			[Description("")]
			[Category("Enhancements")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool ForceTrueColor { get; set; }

			[DisplayName("Disable Copy Filter")]
			[Description("")]
			[Category("Enhancements")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool DisableCopyFilter { get; set; }

			[DisplayName("Arbitrary Mipmap Detection")]
			[Description("")]
			[Category("Enhancements")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool ArbitraryMipmapDetection { get; set; }

			[DisplayName("EFB Access Enable")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool EFBAccessEnable { get; set; }

			[DisplayName("EFB Access Defer Invalidation")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool EFBAccessDeferInvalidation { get; set; }

			[DisplayName("Bounding Box Enable")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool BBoxEnable { get; set; }

			[DisplayName("Force Progressive")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool ForceProgressive { get; set; }

			[DisplayName("EFB To Texture Enable")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool EFBToTextureEnable { get; set; }

			[DisplayName("XFB To Texture Enable")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool XFBToTextureEnable { get; set; }

			[DisplayName("Disable Copy To VRAM")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool DisableCopyToVRAM { get; set; }

			[DisplayName("Defer EFB Copies")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool DeferEFBCopies { get; set; }

			[DisplayName("Immediate XFB Enable")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool ImmediateXFBEnable { get; set; }

			[DisplayName("Skip Duplicate XFBs")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool SkipDuplicateXFBs { get; set; }

			[DisplayName("Early XFB Output")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool EarlyXFBOutput { get; set; }

			[DisplayName("EFB Scaled Copy")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool EFBScaledCopy { get; set; }

			[DisplayName("EFB Emulate Format Changes")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool EFBEmulateFormatChanges { get; set; }

			[DisplayName("Vertex Rounding")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool VertexRounding { get; set; }

			[DisplayName("Fast Texture Sampling")]
			[Description("")]
			[Category("Hacks")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool FastTextureSampling { get; set; }
		}

		[CoreSettings]
		public class DolphinSYSCONFSettings : DolphinNativeSettings
		{
			protected override string NativeName => "SYSCONF";

			protected override Type NativeSettingsType => typeof(DolphinSYSCONFSettings);

			public DolphinSYSCONFSettings()
				=> SettingsUtil.SetDefaultValues(this);

			public DolphinSYSCONFSettings Clone()
				=> (DolphinSYSCONFSettings) MemberwiseClone();

			[DisplayName("Screensaver")]
			[Description("")]
			[Category("IPL")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool SSV { get; set; }

			public enum WiiLanguages
			{
				Japanese = 0,
				English = 1,
				German = 2,
				French = 3,
				Spanish = 4,
				Italian = 5,
				Dutch = 6,
				SimplifiedChinese = 7,
				TraditionalChinese = 8,
				Korean = 9,
				Unknown,
			}

			[DisplayName("Language")]
			[Description("Wii language.")]
			[Category("IPL")]
			[DefaultValue(WiiLanguages.English)]
			[Browsable(true)]
			public WiiLanguages LNG { get; set; }

			public enum WiiCountries
			{
				Europe = 0,
				Japan,
				USA,
				Australia,
				France,
				Germany,
				Italy,
				Korea,
				Netherlands,
				Russia,
				Spain,
				Taiwan,
				World,
				Unknown,
			}

			[DisplayName("Country")]
			[Description("")]
			[Category("IPL")]
			[DefaultValue(WiiCountries.USA)]
			[Browsable(true)]
			public WiiCountries SADR { get; set; }

			[DisplayName("Widescreen")]
			[Description("")]
			[Category("IPL")]
			[DefaultValue(false)]
			[Browsable(true)]
			public bool AR { get; set; }

			[DisplayName("Progressive Scan")]
			[Description("")]
			[Category("IPL")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool PGS { get; set; }

			[DisplayName("PAL60")]
			[Description("")]
			[Category("IPL")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool E60 { get; set; }

			public enum SoundModes
			{
				Mono = 0,
				Stereo = 1,
				Surround = 2,
			}

			[DisplayName("Sound Mode")]
			[Description("")]
			[Category("IPL")]
			[DefaultValue(SoundModes.Stereo)]
			[Browsable(true)]
			public SoundModes SND { get; set; }

			public enum SensorBarPositions
			{
				Bottom = 0,
				Top = 1,
			}

			[DisplayName("Sensor Bar Position")]
			[Description("")]
			[Category("BT")]
			[DefaultValue(SensorBarPositions.Top)]
			[Browsable(true)]
			public SensorBarPositions BAR { get; set; }

			[JsonIgnore]
			private int _spkv;

			[DisplayName("Wiimote Speaker Volume")]
			[Description("")]
			[Category("BT")]
			[DefaultValue(88)]
			[Browsable(true)]
			public int SPKV
			{
				get => _spkv;
				set => _spkv = Math.Min(Math.Max(0, value), 127);
			}

			[DisplayName("Wiimote Motor On")]
			[Description("")]
			[Category("BT")]
			[DefaultValue(true)]
			[Browsable(true)]
			public bool MOT { get; set; }
		}
	}
}
