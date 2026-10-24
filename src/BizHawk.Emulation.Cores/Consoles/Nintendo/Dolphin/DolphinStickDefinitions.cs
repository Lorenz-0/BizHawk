using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	/// <summary>Canonical names and virtual-direction metadata for Dolphin's GameCube sticks.</summary>
	internal static class DolphinStickDefinitions
	{
		public static VirtualAnalogStick[] GameCubeSticks(int controller)
			=>
		[
				new($"P{controller} Main Stick", $"P{controller} Main Stick X", $"P{controller} Main Stick Y", XMinimumIsLeft: true),
				new($"P{controller} C Stick", $"P{controller} C Stick X", $"P{controller} C Stick Y", XMinimumIsLeft: true),
		];
	}
}
