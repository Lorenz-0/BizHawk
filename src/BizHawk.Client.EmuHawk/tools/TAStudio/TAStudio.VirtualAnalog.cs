using System;
using System.Collections.Generic;
using System.Linq;

using BizHawk.Client.Common;
using BizHawk.Emulation.Common;

namespace BizHawk.Client.EmuHawk
{
	public partial class TAStudio
	{
		private sealed record class VirtualAnalogColumn(VirtualAnalogStick Stick, VirtualAnalogStickDirection Direction, string Glyph);

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

		private static string DirectionHeaderText(VirtualAnalogStick stick, VirtualAnalogStickDirection direction)
		{
			string stickName = stick.Name.StartsWith("P1 ", StringComparison.Ordinal) ? stick.Name[3..] : stick.Name;
			return $"{stickName} {direction}";
		}

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
				// Use each core's full range: PSP is signed, while GameCube uses 0..255.
				int axisValue = column.Direction switch
				{
					VirtualAnalogStickDirection.Up => axisSpec.Max,
					VirtualAnalogStickDirection.Down => axisSpec.Min,
					_ => column.Stick.ValueForDirection(column.Direction, axisSpec),
				};
				CurrentTasMovie.SetAxisState(frame, axisName, axisValue);
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
					string headerText = DirectionHeaderText(stick, direction);
					int headerWidth = UIHelper.UnscaleX(roll.GetCellWidthForText(headerText));
					RollColumn? existingColumn = roll.AllColumns.FirstOrDefault(column => column.Name == name);
					if (existingColumn is not null)
					{
						existingColumn.Text = headerText;
						existingColumn.CenterCellText = true;
						existingColumn.VerticalWidth = Math.Max(existingColumn.VerticalWidth, headerWidth);
						existingColumn.HorizontalHeight = Math.Max(existingColumn.HorizontalHeight, headerWidth);
						continue;
					}

					var column = new RollColumn(name, headerWidth, headerWidth, headerText)
					{
						CenterCellText = true,
					};
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
