using System.Linq;

using BizHawk.Common;
using BizHawk.Emulation.Common;

namespace BizHawk.Tests.Emulation.Common
{
	[TestClass]
	public class VirtualAnalogStickTests
	{
		[TestMethod]
		public void DirectionsUseNeutralAndConfiguredPolarity()
		{
			var stick = new VirtualAnalogStick("P1 Main Stick", "P1 Main Stick X", "P1 Main Stick Y");

			Assert.IsFalse(stick.IsDirectionPressed(VirtualAnalogStickDirection.Up, 0, 128));
			Assert.IsFalse(stick.IsDirectionPressed(VirtualAnalogStickDirection.Up, 127, 128));
			Assert.IsFalse(stick.IsDirectionPressed(VirtualAnalogStickDirection.Up, 128, 128));
			Assert.IsTrue(stick.IsDirectionPressed(VirtualAnalogStickDirection.Up, 129, 128));
			Assert.IsTrue(stick.IsDirectionPressed(VirtualAnalogStickDirection.Down, 127, 128));
			Assert.IsTrue(stick.IsDirectionPressed(VirtualAnalogStickDirection.Left, 0, 128));
			Assert.IsTrue(stick.IsDirectionPressed(VirtualAnalogStickDirection.Right, 255, 128));
			var axisSpec = new AxisSpec(0.RangeTo(255), 128);
			Assert.AreEqual(255, stick.ValueForDirection(VirtualAnalogStickDirection.Up, axisSpec));
			Assert.AreEqual(0, stick.ValueForDirection(VirtualAnalogStickDirection.Down, axisSpec));
			Assert.AreEqual(255, stick.ValueForDirection(VirtualAnalogStickDirection.Right, axisSpec));
			var pspAxisSpec = new AxisSpec((-32768).RangeTo(32767), 0);
			Assert.AreEqual(32767, stick.ValueForDirection(VirtualAnalogStickDirection.Up, pspAxisSpec));
			Assert.AreEqual(-32768, stick.ValueForDirection(VirtualAnalogStickDirection.Down, pspAxisSpec));

			var reversedStick = stick with { XMinimumIsLeft = false };
			Assert.IsFalse(reversedStick.IsDirectionPressed(VirtualAnalogStickDirection.Up, 127, 128));
			Assert.IsTrue(reversedStick.IsDirectionPressed(VirtualAnalogStickDirection.Up, 129, 128));
			Assert.IsTrue(reversedStick.IsDirectionPressed(VirtualAnalogStickDirection.Left, 129, 128));
			Assert.AreEqual(255, reversedStick.ValueForDirection(VirtualAnalogStickDirection.Up, axisSpec));
			Assert.AreEqual(0, reversedStick.ValueForDirection(VirtualAnalogStickDirection.Down, axisSpec));
		}

		[TestMethod]
		public void MetadataDoesNotBecomeControllerInputAndSurvivesCopies()
		{
			var stick = new VirtualAnalogStick("P1 Main Stick", "P1 Main Stick X", "P1 Main Stick Y");
			var definition = new ControllerDefinition("Dolphin Controls");
			definition.AddAxis("P1 Main Stick X", 0.RangeTo(255), 128);
			definition.AddAxis("P1 Main Stick Y", 0.RangeTo(255), 128);
			definition.VirtualAnalogSticks.Add(stick);
			definition.MakeImmutable();

			Assert.AreEqual(0, definition.BoolButtons.Count);
			Assert.AreEqual(2, definition.Axes.Count);
			Assert.IsFalse(definition.ControlsOrdered.SelectMany(static group => group).Any(control => control.Name == stick.DirectionName(VirtualAnalogStickDirection.Up)));
			Assert.AreEqual(stick, new ControllerDefinition(definition).VirtualAnalogSticks.Single());
		}

		[TestMethod]
		public void ControllerDefinitionMergerRemapsVirtualStickAxes()
		{
			static ControllerDefinition MakeDefinition()
			{
				var def = new ControllerDefinition("Stick");
				def.AddAxis("1 Main Stick X", 0.RangeTo(255), 128);
				def.AddAxis("1 Main Stick Y", 0.RangeTo(255), 128);
				def.VirtualAnalogSticks.Add(new("1 Main Stick", "1 Main Stick X", "1 Main Stick Y"));
				return def.MakeImmutable();
			}

			ControllerDefinition merged = ControllerDefinitionMerger.GetMerged("Merged", [MakeDefinition(), MakeDefinition()], out _);

			CollectionAssert.AreEqual(
				new[] { "P2 Main Stick", "P4 Main Stick" },
				merged.VirtualAnalogSticks.Select(static stick => stick.Name).ToArray());
			Assert.AreEqual("P2 Main Stick X", merged.VirtualAnalogSticks[0].XAxisName);
			Assert.AreEqual("P4 Main Stick Y", merged.VirtualAnalogSticks[1].YAxisName);
		}
	}
}
