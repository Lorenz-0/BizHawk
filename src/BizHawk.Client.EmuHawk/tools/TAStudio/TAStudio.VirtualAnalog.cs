using System;
using System.Collections.Generic;
using System.Linq;

using BizHawk.Emulation.Common;

namespace BizHawk.Client.EmuHawk
{
	public partial class TAStudio
	{
		private sealed record VirtualAnalogColumn(VirtualAnalogStick Stick, VirtualAnalogStickDirection Direction, string Glyph);

		private readonly Dictionary<string, VirtualAnalogColumn> _virtualAnalogColumns = new();

		private void RefreshVirtualAnalogColumns()
		{
			_virtualAnalogColumns.Clear();
			foreach (VirtualAnalogStick stick in ControllerType.VirtualAnalogSticks)
			{
				foreach (VirtualAnalogStickDirection direction in Enum.GetValues(typeof(VirtualAnalogStickDirection)).Cast<VirtualAnalogStickDirection>())
				{
					_virtualAnalogColumns.Add(stick.DirectionName(direction), new(stick, direction, DirectionGlyph(direction)));
				}
			}
		}

		private static string DirectionGlyph(VirtualAnalogStickDirection direction) => direction switch
		{
			VirtualAnalogStickDirection.Up => "↑",
			VirtualAnalogStickDirection.Down => "↓",
			VirtualAnalogStickDirection.Left => "←",
			VirtualAnalogStickDirection.Right => "→",
			_ => throw new ArgumentOutOfRangeException(nameof(direction)),
		};

		private bool TryGetVirtualAnalogColumn(string columnName, out VirtualAnalogColumn column)
			=> _virtualAnalogColumns.TryGetValue(columnName, out column);

		private bool IsVirtualAnalogDirectionPressed(int frame, VirtualAnalogColumn column)
		{
			string axisName = column.Stick.AxisNameFor(column.Direction);
			AxisSpec axisSpec = ControllerType.Axes[axisName];
			int value = frame < CurrentTasMovie.InputLogLength
				? CurrentTasMovie.GetAxisState(frame, axisName)
				: axisSpec.Neutral;
			return column.Stick.IsDirectionPressed(column.Direction, value, axisSpec.Neutral);
		}

		private void SetVirtualAnalogDirection(int frame, string columnName, bool pressed)
		{
			if (!TryGetVirtualAnalogColumn(columnName, out VirtualAnalogColumn column)) return;

			string axisName = column.Stick.AxisNameFor(column.Direction);
			AxisSpec axisSpec = ControllerType.Axes[axisName];
			if (pressed)
			{
				CurrentTasMovie.SetAxisState(frame, axisName, column.Stick.ValueForDirection(column.Direction, axisSpec));
			}
			else if (IsVirtualAnalogDirectionPressed(frame, column))
			{
				CurrentTasMovie.SetAxisState(frame, axisName, axisSpec.Neutral);
			}
		}

		private void SetVirtualAnalogColumnsOnRoll(InputRoll roll)
		{
			RefreshVirtualAnalogColumns();
			foreach (VirtualAnalogStick stick in ControllerType.VirtualAnalogSticks)
			{
				int yAxisIndex = roll.AllColumns.FindIndex(existing => existing.Name == stick.YAxisName);
				int insertIndex = yAxisIndex < 0 ? -1 : yAxisIndex + 1;
				foreach (VirtualAnalogStickDirection direction in Enum.GetValues(typeof(VirtualAnalogStickDirection)).Cast<VirtualAnalogStickDirection>())
				{
					string name = stick.DirectionName(direction);
					if (roll.AllColumns.Any(column => column.Name == name)) continue;

					var column = new RollColumn(name, widthUnscaled: 10, text: DirectionGlyph(direction));
					if (insertIndex < 0)
					{
						roll.AllColumns.Add(column);
					}
					else
				{
					roll.AllColumns.Insert(insertIndex++, column);
				}
				}
			}
		}
	}
}
