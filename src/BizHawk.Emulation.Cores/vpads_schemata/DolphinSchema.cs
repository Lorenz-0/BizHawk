using System.Collections.Generic;
using System.Drawing;

using BizHawk.Common;
using BizHawk.Emulation.Common;
using BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin;

namespace BizHawk.Emulation.Cores
{
	/// <summary>
	/// Virtual pad layouts for Dolphin.
	/// </summary>
	/// <remarks>
	/// Split into one class per system because <see cref="SchemaAttribute"/> is not <c>AllowMultiple</c>,
	/// and <c>VirtualpadsTool</c> resolves a schema by matching a single attribute against <c>Emulator.SystemId</c>.
	/// The pad builders are shared, as the GameCube controller is usable on both systems.
	/// </remarks>
	[Schema(VSystemID.Raw.GameCube)]
	public sealed class DolphinSchema : IVirtualPadSchema
	{
		public IEnumerable<PadSchema> GetPadSchemas(IEmulator core, Action<string> showMessageBox)
			=> DolphinPadSchemata.GetPadSchemas(core, wii: false);
	}

	[Schema(VSystemID.Raw.Wii)]
	public sealed class DolphinWiiSchema : IVirtualPadSchema
	{
		public IEnumerable<PadSchema> GetPadSchemas(IEmulator core, Action<string> showMessageBox)
			=> DolphinPadSchemata.GetPadSchemas(core, wii: true);
	}

	internal static class DolphinPadSchemata
	{
		public static IEnumerable<PadSchema> GetPadSchemas(IEmulator core, bool wii)
		{
			if (core is not Dolphin dolphin) yield break;

			var ss = dolphin.GetSyncSettings();

			var gcSlots = new[]
			{
				ss.MainSettings.SIDevice0,
				ss.MainSettings.SIDevice1,
				ss.MainSettings.SIDevice2,
				ss.MainSettings.SIDevice3,
			};
			for (var i = 0; i < 4; i++)
			{
				if (gcSlots[i] is Dolphin.DolphinMainSettings.SIDevices.Controller) yield return GameCubeController(i + 1);
			}

			if (wii)
			{
				for (var i = 0; i < (dolphin.Wiimotes?.Length ?? 0); i++)
				{
					if (dolphin.Wiimotes[i].IsActive) yield return WiimoteController(i + 5);
				}
			}

			// "Swap Disc" only exists when a 2-disc game was loaded
			yield return Console(core.ControllerDefinition.BoolButtons.Contains("Swap Disc"));
		}

		private static PadSchema GameCubeController(int controller)
		{
			static AxisSpec MakeSpec() => new(0.RangeTo(255), 128);

			static AxisSpec MakeTriggerSpec() => new(0.RangeTo(255), 0);

			return new PadSchema
			{
				Size = new Size(500, 290),
				DisplayName = $"GameCube Controller Player {controller}",
				Buttons =
				[
					ButtonSchema.Up(32, 50, controller),
					ButtonSchema.Down(32, 71, controller),
					ButtonSchema.Left(11, 62, controller),
					ButtonSchema.Right(53, 62, controller),
					new ButtonSchema(3, 32, controller, "L"),
					new ButtonSchema(191, 32, controller, "R"),
					new ButtonSchema(148, 62, controller, "B"),
					new ButtonSchema(169, 50, controller, "Y"),
					new ButtonSchema(190, 62, controller, "X"),
					new ButtonSchema(169, 71, controller, "A"),
					new ButtonSchema(112, 62, controller, "Start", "S"),
					new ButtonSchema(90, 62, controller, "Z"),

					new AnalogSchema(3, 120, $"P{controller} Main Stick X")
					{
						Spec = MakeSpec(),
						SecondarySpec = MakeSpec(),
					},

					new AnalogSchema(260, 120, $"P{controller} C Stick X")
					{
						Spec = MakeSpec(),
						SecondarySpec = MakeSpec(),
					},

					new SingleAxisSchema(240, 15, controller, "Analog L")
					{
						TargetSize = new Size(226, 40),
						MinValue = MakeTriggerSpec().Min,
						MaxValue = MakeTriggerSpec().Max,
					},

					new SingleAxisSchema(240, 72, controller, "Analog R")
					{
						TargetSize = new Size(226, 40),
						MinValue = MakeTriggerSpec().Min,
						MaxValue = MakeTriggerSpec().Max,
					},
				],
			};
		}

		private static PadSchema WiimoteController(int controller)
		{
			static AxisSpec MakeAccelSpec() => new(0.RangeTo(1023), 512);

			return new PadSchema
			{
				Size = new Size(360, 300),
				DisplayName = $"Wiimote Player {controller - 4}",
				Buttons =
				[
					ButtonSchema.Up(32, 50, controller),
					ButtonSchema.Down(32, 71, controller),
					ButtonSchema.Left(11, 62, controller),
					ButtonSchema.Right(53, 62, controller),
					new ButtonSchema(120, 20, controller, "Minus"),
					new ButtonSchema(150, 20, controller, "Plus"),
					new ButtonSchema(120, 50, controller, "One"),
					new ButtonSchema(150, 50, controller, "Two"),
					new ButtonSchema(120, 80, controller, "A"),
					new ButtonSchema(150, 80, controller, "B"),
					new ButtonSchema(185, 20, controller, "Home"),

					new AnalogSchema(3, 120, $"P{controller} Accelerometer X")
					{
						Spec = MakeAccelSpec(),
						SecondarySpec = MakeAccelSpec(),
					},

					new SingleAxisSchema(200, 120, controller, "Accelerometer Z", isVertical: true)
					{
						TargetSize = new Size(40, 100),
						MinValue = MakeAccelSpec().Min,
						MaxValue = MakeAccelSpec().Max,
					},

					new ButtonSchema(3, 250, controller, "IR Pointing"),

					// the Wiimote's IR camera reports 1024x768, so the ranges can't be taken from a target size
					new TargetedPairSchema(60, 250, $"P{controller} IR X", 1023, 767)
					{
						TargetSize = new Size(256, 192),
					},

					new SingleAxisSchema(320, 250, controller, "IR Z")
					{
						TargetSize = new Size(36, 100),
						MinValue = 0,
						MaxValue = 767,
					},
				],
			};
		}

		private static PadSchema Console(bool multiDisc)
		{
			var buttons = new List<PadSchemaControl> { new ButtonSchema(10, 10, "Reset") };
			if (multiDisc) buttons.Add(new ButtonSchema(10, 40, "Swap Disc"));

			return new ConsoleSchema
			{
				Size = new Size(150, 110),
				Buttons = buttons,
			};
		}
	}
}
