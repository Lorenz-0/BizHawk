using System;

namespace BizHawk.Emulation.Common
{
	/// <summary>
	/// Describes an analog stick as a set of virtual directional controls for UI editors.
	/// These are metadata only: they are not controller inputs and are not serialized into movie logs.
	/// </summary>
	/// <param name="Name">A stable, player-qualified label used to name its virtual directions.</param>
	/// <param name="XAxisName">The real X-axis input name.</param>
	/// <param name="YAxisName">The real Y-axis input name.</param>
	/// <param name="XMinimumIsLeft">Whether the lower X range means left.</param>
	/// <param name="YMinimumIsUp">Whether the lower Y range means up.</param>
	public sealed record VirtualAnalogStick(
		string Name,
		string XAxisName,
		string YAxisName,
		bool XMinimumIsLeft = true,
		bool YMinimumIsUp = true)
	{
		public string DirectionName(VirtualAnalogStickDirection direction) => $"{Name} {direction}";

		public string AxisNameFor(VirtualAnalogStickDirection direction)
			=> direction is VirtualAnalogStickDirection.Left or VirtualAnalogStickDirection.Right ? XAxisName : YAxisName;

		public bool IsDirectionPressed(VirtualAnalogStickDirection direction, int value, int neutral)
		{
			bool lowMeansThisDirection = direction switch
			{
				VirtualAnalogStickDirection.Up => YMinimumIsUp,
				VirtualAnalogStickDirection.Down => !YMinimumIsUp,
				VirtualAnalogStickDirection.Left => XMinimumIsLeft,
				VirtualAnalogStickDirection.Right => !XMinimumIsLeft,
				_ => throw new ArgumentOutOfRangeException(nameof(direction)),
			};

			return lowMeansThisDirection ? value < neutral : value > neutral;
		}

		public int ValueForDirection(VirtualAnalogStickDirection direction, AxisSpec axisSpec)
		{
			bool lowMeansThisDirection = direction switch
			{
				VirtualAnalogStickDirection.Up => YMinimumIsUp,
				VirtualAnalogStickDirection.Down => !YMinimumIsUp,
				VirtualAnalogStickDirection.Left => XMinimumIsLeft,
				VirtualAnalogStickDirection.Right => !XMinimumIsLeft,
				_ => throw new ArgumentOutOfRangeException(nameof(direction)),
			};

			return lowMeansThisDirection ? axisSpec.Min : axisSpec.Max;
		}
	}

	public enum VirtualAnalogStickDirection
	{
		Up,
		Down,
		Left,
		Right,
	}
}
