using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Nintendo.Dolphin
{
	public partial class Dolphin : IInputPollable
	{
		public int LagCount { get; set; }

		public bool IsLagFrame
		{
			get => _lagged;
			set => _lagged = value;
		}

		public IInputCallbackSystem InputCallbacks { get; } = new InputCallbackSystem();

		/// <remarks>read on the host thread, written on the emulation thread</remarks>
		private volatile bool _lagged;

		private IController _controller = NullController.Instance;

		private readonly LibDolphin.GCPadCallback _gcpadcb;

		/// <remarks>
		/// "sticky" disc swap: the swap only fires on the press, so holding the button down
		/// won't spam disc swaps once per polled frame
		/// </remarks>
		private volatile bool _didSwapDisc;
		private volatile bool _doSwapDisc;
		private volatile bool _doReset;

		private unsafe void GCPadCallback(IntPtr padStatus, int controllerID)
		{
			IsLagFrame = false;
			InputCallbacks.Call();

			var p = (LibDolphin.GCPadStatus*) padStatus;
			var n = controllerID + 1;

			p->Buttons = 0;
			p->Buttons |= (LibDolphin.PadButtons) 0x0080; // PAD_USE_ORIGIN
			if (_controller.IsPressed($"P{n} Left")) p->Buttons |= LibDolphin.PadButtons.Left;
			if (_controller.IsPressed($"P{n} Right")) p->Buttons |= LibDolphin.PadButtons.Right;
			if (_controller.IsPressed($"P{n} Down")) p->Buttons |= LibDolphin.PadButtons.Down;
			if (_controller.IsPressed($"P{n} Up")) p->Buttons |= LibDolphin.PadButtons.Up;
			if (_controller.IsPressed($"P{n} Z")) p->Buttons |= LibDolphin.PadButtons.Z;
			if (_controller.IsPressed($"P{n} R")) p->Buttons |= LibDolphin.PadButtons.R;
			if (_controller.IsPressed($"P{n} L")) p->Buttons |= LibDolphin.PadButtons.L;
			if (_controller.IsPressed($"P{n} A")) p->Buttons |= LibDolphin.PadButtons.A;
			if (_controller.IsPressed($"P{n} B")) p->Buttons |= LibDolphin.PadButtons.B;
			if (_controller.IsPressed($"P{n} X")) p->Buttons |= LibDolphin.PadButtons.X;
			if (_controller.IsPressed($"P{n} Y")) p->Buttons |= LibDolphin.PadButtons.Y;
			if (_controller.IsPressed($"P{n} Start")) p->Buttons |= LibDolphin.PadButtons.Start;

			p->StickX = (byte) _controller.AxisValue($"P{n} Main Stick X");
			p->StickY = (byte) _controller.AxisValue($"P{n} Main Stick Y");
			p->SubstickX = (byte) _controller.AxisValue($"P{n} C Stick X");
			p->SubstickY = (byte) _controller.AxisValue($"P{n} C Stick Y");

			if ((p->Buttons & LibDolphin.PadButtons.L) != 0)
			{
				p->TriggerLeft = 0xFF;
			}
			else
			{
				p->TriggerLeft = (byte) _controller.AxisValue($"P{n} Analog L");
				// the digital button latches at the top of the analog range
				if (p->TriggerLeft == 0xFF) p->Buttons |= LibDolphin.PadButtons.L;
			}

			if ((p->Buttons & LibDolphin.PadButtons.R) != 0)
			{
				p->TriggerRight = 0xFF;
			}
			else
			{
				p->TriggerRight = (byte) _controller.AxisValue($"P{n} Analog R");
				if (p->TriggerRight == 0xFF) p->Buttons |= LibDolphin.PadButtons.R;
			}

			p->AnalogA = (byte) (((p->Buttons & LibDolphin.PadButtons.A) != 0) ? 0xFF : 0);
			p->AnalogB = (byte) (((p->Buttons & LibDolphin.PadButtons.B) != 0) ? 0xFF : 0);

			p->IsConnected = true;
		}

		private readonly LibDolphin.WiiPadCallback _wiipadcb;

		private unsafe void WiiPadCallback(IntPtr rpt, LibDolphin.WiimoteInputReq which, int controllerID)
		{
			var n = controllerID + 5;

			switch (which)
			{
				case LibDolphin.WiimoteInputReq.CORE_BUTTONS:
				{
					var p = *(LibDolphin.CoreButtons*) rpt;
					p &= ~LibDolphin.CoreButtonsMask;

					if (_controller.IsPressed($"P{n} Left")) p |= LibDolphin.CoreButtons.Left;
					if (_controller.IsPressed($"P{n} Right")) p |= LibDolphin.CoreButtons.Right;
					if (_controller.IsPressed($"P{n} Down")) p |= LibDolphin.CoreButtons.Down;
					if (_controller.IsPressed($"P{n} Up")) p |= LibDolphin.CoreButtons.Up;
					if (_controller.IsPressed($"P{n} Plus")) p |= LibDolphin.CoreButtons.Plus;
					if (_controller.IsPressed($"P{n} Two")) p |= LibDolphin.CoreButtons.Two;
					if (_controller.IsPressed($"P{n} One")) p |= LibDolphin.CoreButtons.One;
					if (_controller.IsPressed($"P{n} B")) p |= LibDolphin.CoreButtons.B;
					if (_controller.IsPressed($"P{n} A")) p |= LibDolphin.CoreButtons.A;
					if (_controller.IsPressed($"P{n} Minus")) p |= LibDolphin.CoreButtons.Minus;
					if (_controller.IsPressed($"P{n} Home")) p |= LibDolphin.CoreButtons.Home;

					*(LibDolphin.CoreButtons*) rpt = p;
					break;
				}
				case LibDolphin.WiimoteInputReq.CORE_ACCEL:
				{
					var p = (LibDolphin.CoreAccel*) rpt;
					p->X = (ushort) _controller.AxisValue($"P{n} Accelerometer X");
					p->Y = (ushort) _controller.AxisValue($"P{n} Accelerometer Y");
					p->Z = (ushort) _controller.AxisValue($"P{n} Accelerometer Z");
					break;
				}
				case LibDolphin.WiimoteInputReq.CORE_IR_BASIC:
				{
					if (!_controller.IsPressed($"P{n} IR Pointing")) break;

					var p = (LibDolphin.CoreIRBasic*) rpt;
					var c = GetIRCoords(n);

					p->X1 = (byte) (c.X1 & 0xFF);
					p->Y1 = (byte) (c.Y1 & 0xFF);
					p->Hi &= ~(LibDolphin.CoreIRBasicHi.X1 | LibDolphin.CoreIRBasicHi.Y1);
					p->Hi |= (LibDolphin.CoreIRBasicHi) ((c.X1 >> 8) << 4) & LibDolphin.CoreIRBasicHi.X1;
					p->Hi |= (LibDolphin.CoreIRBasicHi) ((c.Y1 >> 8) << 6) & LibDolphin.CoreIRBasicHi.Y1;

					p->X2 = (byte) (c.X2 & 0xFF);
					p->Y2 = (byte) (c.Y2 & 0xFF);
					p->Hi &= ~(LibDolphin.CoreIRBasicHi.X2 | LibDolphin.CoreIRBasicHi.Y2);
					p->Hi |= (LibDolphin.CoreIRBasicHi) (c.X2 >> 8) & LibDolphin.CoreIRBasicHi.X2;
					p->Hi |= (LibDolphin.CoreIRBasicHi) ((c.Y2 >> 8) << 2) & LibDolphin.CoreIRBasicHi.Y2;
					break;
				}
				case LibDolphin.WiimoteInputReq.CORE_IR_EXTENDED:
				{
					if (!_controller.IsPressed($"P{n} IR Pointing")) break;

					var p = (LibDolphin.CoreIRExtended*) rpt;
					var c = GetIRCoords(n);
					var sz = _controller.AxisValue($"P{n} IR Z") / 48;

					FillExtendedPoint(&p[0], c.X1, c.Y1, sz);
					FillExtendedPoint(&p[1], c.X2, c.Y2, sz);
					break;
				}
				case LibDolphin.WiimoteInputReq.CORE_IR_FULL:
				{
					if (!_controller.IsPressed($"P{n} IR Pointing")) break;

					var p = (LibDolphin.CoreIRFull*) rpt;
					var c = GetIRCoords(n);
					var sz = _controller.AxisValue($"P{n} IR Z") / 48;
					const double TAU = 6.2831853071795862; // Math.Tau isn't available on net48

					FillFullPoint(&p[0], c.X1, c.Y1, sz, TAU);
					FillFullPoint(&p[1], c.X2, c.Y2, sz, TAU);
					break;
				}
				case LibDolphin.WiimoteInputReq.END_INPUT:
				{
					// the whole report is assembled; this is the once-per-frame hook for Wii
					IsLagFrame = false;
					InputCallbacks.Call();
					break;
				}
				default:
					throw new InvalidOperationException($"Unknown Wiimote input request: {which}");
			}
		}

		private static unsafe void FillExtendedPoint(LibDolphin.CoreIRExtended* p, int x, int y, int sz)
		{
			p->X = (byte) (x & 0xFF);
			p->Y = (byte) (y & 0xFF);
			p->SzHi = 0;
			p->SzHi |= (LibDolphin.CoreIRExtendedSzHi) sz & LibDolphin.CoreIRExtendedSzHi.Sz;
			p->SzHi |= (LibDolphin.CoreIRExtendedSzHi) ((x >> 8) << 4) & LibDolphin.CoreIRExtendedSzHi.X;
			p->SzHi |= (LibDolphin.CoreIRExtendedSzHi) ((y >> 8) << 6) & LibDolphin.CoreIRExtendedSzHi.Y;
		}

		private static unsafe void FillFullPoint(LibDolphin.CoreIRFull* p, int x, int y, int sz, double tau)
		{
			FillExtendedPoint((LibDolphin.CoreIRExtended*) p, x, y, sz);

			p->XMin = (byte) (Math.Max(x - sz, 0) & 0x7F);
			p->YMin = (byte) (Math.Max(y - sz, 0) & 0x7F);
			p->XMax = (byte) (Math.Min(x + sz, 1023) & 0x7F);
			p->YMax = (byte) (Math.Min(y + sz, 767) & 0x7F);

			p->ZeroPadding = 0;
			p->Intensity = (byte) Math.Round((p->XMax - p->XMin) * (p->YMax - p->YMin) / 8 / 8 * tau / 8);
		}

		private struct IRCoords
		{
			public int X1;
			public int Y1;
			public int X2;
			public int Y2;
		}

		/// <remarks>
		/// the Wiimote reports two IR dots; we only expose one point plus a "size" axis,
		/// so the two dots are synthesised symmetrically about it.
		/// An out-of-range dot is reported as 0xFFFF, which is how a real Wiimote signals "not seen".
		/// </remarks>
		private IRCoords GetIRCoords(int n)
		{
			var x = _controller.AxisValue($"P{n} IR X");
			var y = _controller.AxisValue($"P{n} IR Y");
			var z = _controller.AxisValue($"P{n} IR Z");

			IRCoords ret = default;
			ret.X1 = x - (z / 2);
			ret.X2 = x + (z / 2);
			ret.Y1 = ret.Y2 = y;

			if (ret.X1 < 0 || ret.X1 > 1023 || ret.Y1 < 0 || ret.Y1 > 767) ret.X1 = ret.Y1 = 0xFFFF;
			if (ret.X2 < 0 || ret.X2 > 1023 || ret.Y2 < 0 || ret.Y2 > 767) ret.X2 = ret.Y2 = 0xFFFF;

			return ret;
		}
	}
}
