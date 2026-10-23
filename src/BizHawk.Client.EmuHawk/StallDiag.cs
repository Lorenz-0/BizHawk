using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BizHawk.Client.EmuHawk
{
	/// <summary>
	/// append-only breadcrumb log that bypasses the UI entirely, plus a stall detector.
	/// message boxes and VS attaches both need a responsive UI thread, which is exactly what a freeze
	/// takes away; so every interesting thread writes its current stage straight to disk (autoflushed,
	/// survives a task-kill), and when the main loop stops beating, a healthy watchdog thread dumps
	/// process/thread state and can offer a JIT debugger attach for real stacks.
	/// log file: stall-diag.log next to the executable; set BIZHAWK_STALL_ATTACH=1 to get the
	/// debugger-attach prompt on stall detection
	/// </summary>
	public static class StallDiag
	{
		private static readonly object LogLock = new();
		private static StreamWriter _log;
		private static int _lastBeat = unchecked(Environment.TickCount);
		private static int _stallReported;
		private static Func<string> _extraState;

		public static void Init()
		{
			lock (LogLock)
			{
				if (_log is not null) return;
				try
				{
					var dir = Path.GetDirectoryName(typeof(StallDiag).Assembly.Location);
					var path = Path.Combine(dir, "stall-diag.log");
					_log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
				}
				catch
				{
					return; // no log, no diagnostics, but never break the client over this
				}

				_lastBeat = unchecked(Environment.TickCount);
				new Thread(WatchLoop) { IsBackground = true, Name = "StallDiag watchdog" }.Start();
			}
		}

		/// <summary>registers extra state (e.g. the ffmpeg process) to include in a stall report</summary>
		public static void SetExtraState(Func<string> provider) => _extraState = provider;

		/// <summary>breadcrumb from the calling thread</summary>
		public static void Note(string stage)
		{
			var log = _log;
			if (log is null) return;
			lock (LogLock)
			{
				log.WriteLine($"{DateTime.UtcNow:O} tid={Thread.CurrentThread.ManagedThreadId} {stage}");
			}
		}

		/// <summary>main-loop heartbeat; also re-arms the stall detector</summary>
		public static void Beat()
		{
			if (_log is null) return;
			Volatile.Write(ref _lastBeat, unchecked(Environment.TickCount));
			Volatile.Write(ref _stallReported, 0);
		}

		private static void WatchLoop()
		{
			while (true)
			{
				Thread.Sleep(2000);
				if (_log is null) continue;
				var delta = unchecked(Environment.TickCount - Volatile.Read(ref _lastBeat));
				if (delta <= 6000) continue;
				if (Interlocked.Exchange(ref _stallReported, 1) == 1) continue;
				Report(delta);
			}
		}

		private static void Report(int deltaMs)
		{
			lock (LogLock)
			{
				_log.WriteLine($"{DateTime.UtcNow:O} tid={Thread.CurrentThread.ManagedThreadId} STALL DETECTED: main loop silent for {deltaMs} ms");
				try
				{
					_log.WriteLine(_extraState?.Invoke() ?? "no extra state registered");
				}
				catch (Exception e)
				{
					_log.WriteLine($"extra state failed: {e.Message}");
				}

				try
				{
					using var proc = Process.GetCurrentProcess();
					foreach (ProcessThread th in proc.Threads)
					{
						_log.WriteLine($"thread id={th.Id} state={th.ThreadState} wait={th.WaitReason} cpu={th.TotalProcessorTime}");
					}
				}
				catch (Exception e)
				{
					_log.WriteLine($"thread dump failed: {e.Message}");
				}
			}

			// offer a JIT debugger attach from this healthy thread so real (incl. native) stacks become
			// available even though the UI thread is frozen; opt-in via env var
			if (Environment.GetEnvironmentVariable("BIZHAWK_STALL_ATTACH") == "1")
			{
				try
				{
					Debugger.Launch();
				}
				catch
				{
					// no JIT debugger configured; the log still has everything
				}
			}
		}
	}
}
