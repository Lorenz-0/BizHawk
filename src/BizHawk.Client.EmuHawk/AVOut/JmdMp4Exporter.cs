using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

using BizHawk.Client.Common;

namespace BizHawk.Client.EmuHawk
{
	/// <summary>
	/// Converts JMD captures produced by BizHawk into ordinary MP4 files after emulation has finished.
	/// </summary>
	internal static class JmdMp4Exporter
	{
		private const int AudioSampleRate = 44100;
		private const int MaxFrameBytes = 256 * 1024 * 1024;
		private static long _nextTempFileSuffix;

		public static void Run(MainForm parent, Config config)
		{
			try
			{
				int filterIndex = 1;
				var files = parent.DialogController.ShowFileMultiOpenDialog(
					parent,
					"BizHawk JMD captures (*.jmd)|*.jmd|All files (*.*)|*.*",
					ref filterIndex,
					Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
					windowTitle: "Select a BizHawk JMD capture to convert");
				if (files is null || files.Count == 0) return;

				var inputPath = files[0];
				var inspectProgress = new ConversionProgress();
				var inspectTask = Task.Run(() => Inspect(inputPath, inspectProgress));
				using (var progressForm = new ConversionProgressForm(inspectTask, inspectProgress, "Analyzing JMD timestamps and video dimensions..."))
				{
					parent.ShowDialogWithTempMute(progressForm);
				}
				var info = inspectTask.GetAwaiter().GetResult();

				using var writer = new FFmpegWriter(parent);
				using var codecToken = writer.AcquireVideoCodecToken(config);
				if (codecToken is null) return;
				writer.SetVideoCodecToken(codecToken);
				if (!string.Equals(writer.DesiredExtension(), "mp4", StringComparison.OrdinalIgnoreCase))
				{
					parent.DialogController.ShowMessageBox(
						parent,
						"Choose the MP4 preset in the FFmpeg format list, then try the conversion again.",
						"MP4 preset required");
					return;
				}

				var outputPath = parent.DialogController.ShowFileSaveDialog(
					parent,
					discardCWDChange: true,
					fileExt: "mp4",
					filterStr: "MP4 video (*.mp4)|*.mp4",
					initDir: Path.GetDirectoryName(inputPath) ?? string.Empty,
					initFileName: Path.GetFileNameWithoutExtension(inputPath) + ".mp4",
					muteOverwriteWarning: false);
				if (string.IsNullOrWhiteSpace(outputPath)) return;
				if (!string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
				{
					outputPath += ".mp4";
				}

				var tempPath = CreateTempOutputPath(outputPath);
				var progress = new ConversionProgress();
				var task = Task.Run(() => ConvertFile(inputPath, tempPath, outputPath, info, writer, progress));
				using (var progressForm = new ConversionProgressForm(task, progress, "Encoding MP4 from the JMD capture. This may take a while..."))
				{
					parent.ShowDialogWithTempMute(progressForm);
				}
				var sourceDeleted = task.GetAwaiter().GetResult();
				var completionMessage = sourceDeleted
					? $"MP4 export complete. The original JMD was deleted:\n{outputPath}"
					: $"MP4 export complete, but the original JMD could not be deleted:\n{outputPath}";
				parent.DialogController.ShowMessageBox(parent, completionMessage, "JMD export complete");
			}
			catch (Exception e)
			{
				parent.DialogController.ShowMessageBox(parent, $"JMD to MP4 conversion failed:\n\n{e.Message}", "JMD export failed");
			}
		}

		private static CaptureInfo Inspect(string path, ConversionProgress progress)
		{
			using var reader = new JmdReader(path);
			var info = new CaptureInfo();
			ulong firstVideoTimestamp = 0, lastVideoTimestamp = 0;
			long packetCount = 0;

			while (reader.TryReadNext(decode: false, out var packet))
			{
				if (packet.StreamType == 0 && packet.Subtype is 0 or 1)
				{
					if (packet.Width <= 0 || packet.Height <= 0)
					{
						throw new InvalidDataException("The JMD contains a video frame with invalid dimensions.");
					}
					if (info.FrameCount == 0)
					{
						info.Width = packet.Width;
						info.Height = packet.Height;
						firstVideoTimestamp = packet.Timestamp;
					}
					else if (packet.Width != info.Width || packet.Height != info.Height)
					{
						throw new InvalidDataException(
							"This first exporter version requires a constant video resolution. "
							+ "The capture changes dimensions, so it cannot be written as one MP4 yet.");
					}
					lastVideoTimestamp = packet.Timestamp;
					info.FrameCount++;
				}
				else if (packet.StreamType == 1 && packet.Subtype == 1)
				{
					info.SampleCount++;
				}

				if ((++packetCount & 0xFFFF) == 0)
				{
					progress.SetPercent((int)(reader.Position * 1000 / reader.Length));
				}
			}
			progress.SetPercent(1000);

			if (info.FrameCount == 0)
			{
				throw new InvalidDataException("The JMD contains no video frames.");
			}
			if (info.FrameCount > 1)
			{
				var elapsed = lastVideoTimestamp - firstVideoTimestamp;
				info.Fps = ApproximateRate((info.FrameCount - 1) * 1_000_000_000.0 / elapsed);
			}
			else
			{
				info.Fps = (60, 1);
			}

			// BizHawk's AV capture path writes JMD audio as stereo 44.1 kHz PCM.
			info.SampleRate = AudioSampleRate;
			return info;
		}

		private static (int Num, int Den) ApproximateRate(double rate)
		{
			if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 1 || rate > 1000)
			{
				throw new InvalidDataException("The JMD video frame rate is outside the supported range.");
			}

			var bestNum = (int)Math.Round(rate);
			var bestDen = 1;
			var bestError = Math.Abs(bestNum - rate);
			for (var den = 2; den <= 1001; den++)
			{
				var num = (int)Math.Round(rate * den);
				if (num <= 0) continue;
				var error = Math.Abs(num / (double)den - rate);
				if (error < bestError)
				{
					bestError = error;
					bestNum = num;
					bestDen = den;
				}
			}
			if (bestError > 0.001)
			{
				throw new InvalidDataException("The JMD frame timestamps do not describe a stable frame rate.");
			}
			var gcd = GreatestCommonDivisor(bestNum, bestDen);
			return (bestNum / gcd, bestDen / gcd);
		}

		private static int GreatestCommonDivisor(int a, int b)
		{
			while (b != 0)
			{
				var remainder = a % b;
				a = b;
				b = remainder;
			}
			return a;
		}

		private static bool ConvertFile(string inputPath, string tempPath, string outputPath, CaptureInfo info, FFmpegWriter writer, ConversionProgress progress)
		{
			var exportSucceeded = false;
			try
			{
				writer.SetMovieParameters(info.Fps.Num, info.Fps.Den);
				writer.SetVideoParameters(info.Width, info.Height);
				writer.SetAudioParameters(info.SampleRate, 2, 16);
				writer.OpenFile(tempPath);

				using (var reader = new JmdReader(inputPath))
				{
					var audio = new short[8192]; // 4096 interleaved stereo sample pairs
					var audioCount = 0;
					long packetCount = 0;
					long framesWritten = 0;
					long samplesWritten = 0;
					while (reader.TryReadNext(decode: true, out var packet))
					{
						if (packet.StreamType == 0 && packet.Subtype is 0 or 1)
						{
							if (packet.Width != info.Width || packet.Height != info.Height)
							{
								throw new InvalidDataException("Video resolution changed during conversion.");
							}
							try
							{
								var pixels = DecodeVideo(packet);
								try
								{
									writer.AddFrame(new JmdVideoProvider(packet.Width, packet.Height, pixels));
								}
								finally
								{
									ArrayPool<int>.Shared.Return(pixels);
								}
							}
							finally
							{
								ArrayPool<byte>.Shared.Return(packet.Data);
							}
							framesWritten++;
						}
						else if (packet.StreamType == 1 && packet.Subtype == 1)
						{
							audio[audioCount++] = packet.Left;
							audio[audioCount++] = packet.Right;
							samplesWritten++;
							if (audioCount == audio.Length)
							{
								writer.AddSamples(audio);
								audio = new short[8192];
								audioCount = 0;
							}
						}

						if ((++packetCount & 0xFFFF) == 0)
						{
							progress.SetPercent((int)(reader.Position * 1000 / reader.Length));
						}
					}

					if (framesWritten != info.FrameCount || samplesWritten != info.SampleCount)
					{
						throw new InvalidDataException("The JMD capture changed between the scan and export passes.");
					}
					if (audioCount > 0)
					{
						var finalAudio = new short[audioCount];
						Array.Copy(audio, finalAudio, audioCount);
						writer.AddSamples(finalAudio);
					}
				}

				progress.SetPercent(1000);
				writer.CloseFile();
				if (writer.LastExitCode != 0)
				{
					var exitMessage = writer.LastExitCode is int exitCode
						? $"FFmpeg exited with code {exitCode}."
						: "FFmpeg did not exit cleanly.";
					throw new InvalidOperationException($"{exitMessage} The original JMD was kept.");
				}
				InstallOutput(tempPath, outputPath);
				exportSucceeded = true;
				try
				{
					File.Delete(inputPath);
					return !File.Exists(inputPath);
				}
				catch
				{
					return false;
				}
			}
			finally
			{
				if (!exportSucceeded)
				{
					try { writer.Dispose(); }
					catch { }
					TryDelete(tempPath);
				}
			}
		}

		private static int[] DecodeVideo(JmdPacket packet)
		{
			var pixelCount = checked(packet.Width * packet.Height);
			var expectedLength = checked(pixelCount * 4);
			if (expectedLength > MaxFrameBytes)
			{
				throw new InvalidDataException("A JMD video frame is too large to export safely.");
			}

			var raw = ArrayPool<byte>.Shared.Rent(expectedLength);
			try
			{
				if (packet.Subtype == 0)
				{
					if (packet.DataLength != expectedLength + 4)
					{
						throw new InvalidDataException("An uncompressed JMD frame has an unexpected byte length.");
					}
					Buffer.BlockCopy(packet.Data, 4, raw, 0, expectedLength);
				}
				else
				{
					using var compressed = new MemoryStream(packet.Data, 4, packet.DataLength - 4, writable: false);
					using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
					ReadExactly(gzip, raw, 0, expectedLength);
					if (gzip.ReadByte() != -1)
					{
						throw new InvalidDataException("A compressed JMD frame expands beyond its declared dimensions.");
					}
				}

				var pixels = ArrayPool<int>.Shared.Rent(pixelCount);
				try
				{
					for (var i = 0; i < pixelCount; i++)
					{
						var offset = i * 4;
						var r = raw[offset];
						var g = raw[offset + 1];
						var b = raw[offset + 2];
						pixels[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
					}
					return pixels;
				}
				catch
				{
					ArrayPool<int>.Shared.Return(pixels);
					throw;
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(raw);
			}
		}

		private static string CreateTempOutputPath(string outputPath)
		{
			using var process = Process.GetCurrentProcess();
			var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
			var basename = Path.GetFileNameWithoutExtension(outputPath);
			while (true)
			{
				var suffix = System.Threading.Interlocked.Increment(ref _nextTempFileSuffix);
				var candidate = Path.Combine(directory, $"{basename}.converting-{process.Id}-{suffix}.mp4");
				try
				{
					using var reservation = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
					return candidate;
				}
				catch (IOException) when (File.Exists(candidate))
				{
					// A previous run or another conversion already owns this name. Try the next counter.
				}
			}
		}

		private static void InstallOutput(string tempPath, string outputPath)
		{
			if (File.Exists(outputPath))
			{
				File.Replace(tempPath, outputPath, null);
			}
			else
			{
				File.Move(tempPath, outputPath);
			}
		}

		private static void TryDelete(string path)
		{
			try { if (File.Exists(path)) File.Delete(path); }
			catch { }
		}

		private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
		{
			while (count > 0)
			{
				var read = stream.Read(buffer, offset, count);
				if (read == 0) throw new EndOfStreamException("Unexpected end of JMD data.");
				offset += read;
				count -= read;
			}
		}

		private sealed class CaptureInfo
		{
			public int Width;
			public int Height;
			public int SampleRate;
			public (int Num, int Den) Fps;
			public long FrameCount;
			public long SampleCount;
		}

		private sealed class ConversionProgress
		{
			private int _percent;
			public int Percent => System.Threading.Volatile.Read(ref _percent);
			public void SetPercent(int value) => System.Threading.Volatile.Write(ref _percent, Math.Max(0, Math.Min(1000, value)));
		}

		private sealed class ConversionProgressForm : Form
		{
			private readonly Task _task;
			private readonly ConversionProgress _progress;
			private readonly ProgressBar _bar;
			private readonly Timer _timer;

			public ConversionProgressForm(Task task, ConversionProgress progress, string message)
			{
				_task = task;
				_progress = progress;
				Text = "Exporting JMD to MP4";
				FormBorderStyle = FormBorderStyle.FixedDialog;
				ControlBox = false;
				StartPosition = FormStartPosition.CenterParent;
				ClientSize = new System.Drawing.Size(380, 96);

				var label = new Label
				{
					AutoSize = false,
					Left = 16,
					Top = 14,
					Width = 348,
					Height = 24,
					Text = message,
				};
				_bar = new ProgressBar
				{
					Left = 16,
					Top = 50,
					Width = 348,
					Height = 22,
					Minimum = 0,
					Maximum = 1000,
				};
				Controls.Add(label);
				Controls.Add(_bar);

				_timer = new Timer { Interval = 200 };
				_timer.Tick += (_, _) =>
				{
					_bar.Value = Math.Max(_bar.Minimum, Math.Min(_bar.Maximum, _progress.Percent));
					if (_task.IsCompleted)
					{
						_timer.Stop();
						DialogResult = DialogResult.OK;
						Close();
					}
				};
				Shown += (_, _) => _timer.Start();
				FormClosed += (_, _) => _timer.Dispose();
			}
		}

		private sealed class JmdVideoProvider : BizHawk.Emulation.Common.IVideoProvider
		{
			private readonly int[] _pixels;
			public int VirtualWidth => BufferWidth;
			public int VirtualHeight => BufferHeight;
			public int BufferWidth { get; }
			public int BufferHeight { get; }
			public int VsyncNumerator => 60;
			public int VsyncDenominator => 1;
			public int BackgroundColor => 0;

			public JmdVideoProvider(int width, int height, int[] pixels)
			{
				BufferWidth = width;
				BufferHeight = height;
				_pixels = pixels;
			}

			public int[] GetVideoBuffer() => _pixels;
		}

		private struct JmdPacket
		{
			public ushort StreamType;
			public byte Subtype;
			public ulong Timestamp;
			public int Width;
			public int Height;
			public short Left;
			public short Right;
			public byte[] Data;
			public int DataLength;
		}

		private sealed class JmdReader : IDisposable
		{
			private static readonly byte[] Magic =
			{
				0xFF, 0xFF, (byte)'J', (byte)'P', (byte)'C', (byte)'R', (byte)'R',
				(byte)'M', (byte)'U', (byte)'L', (byte)'T', (byte)'I', (byte)'D', (byte)'U', (byte)'M', (byte)'P',
			};

			private readonly FileStream _stream;
			private readonly Dictionary<ushort, ushort> _streamTypes = new();
			private ulong _timestamp;

			public long Position => _stream.Position;
			public long Length => _stream.Length;

			public JmdReader(string path)
			{
				_stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				try
				{
					var magic = new byte[Magic.Length];
					ReadExactly(_stream, magic, 0, magic.Length);
					for (var i = 0; i < Magic.Length; i++)
					{
						if (magic[i] != Magic[i]) throw new InvalidDataException("This is not a JPC-RR multidump/JMD file.");
					}

					var count = ReadBe16();
					if (count == 0 || count > 64) throw new InvalidDataException("The JMD stream table is invalid.");
					for (var i = 0; i < count; i++)
					{
						var channel = ReadBe16();
						var type = ReadBe16();
						var nameLength = ReadBe16();
						Skip(nameLength);
						if (channel == ushort.MaxValue || _streamTypes.ContainsKey(channel))
						{
							throw new InvalidDataException("The JMD stream table contains a duplicate or reserved channel.");
						}
						_streamTypes.Add(channel, type);
					}
					var videoStreams = 0;
					var audioStreams = 0;
					foreach (var type in _streamTypes.Values)
					{
						if (type == 0) videoStreams++;
						if (type == 1) audioStreams++;
					}
					if (videoStreams != 1 || audioStreams != 1)
					{
						throw new InvalidDataException("This exporter requires one BizHawk video stream and one PCM audio stream.");
					}
				}
				catch
				{
					_stream.Dispose();
					throw;
				}
			}

			public bool TryReadNext(bool decode, out JmdPacket packet)
			{
				packet = default;
				while (_stream.Position < _stream.Length)
				{
					var first = _stream.ReadByte();
					if (first < 0) return false;
					var second = _stream.ReadByte();
					if (second < 0) throw new EndOfStreamException("Truncated JMD packet header.");
					if (first == 0xFF && second == 0xFF)
					{
						for (var i = 0; i < 4; i++)
						{
							if (_stream.ReadByte() != 0xFF)
							{
								throw new InvalidDataException("This converter cannot read a multi-chapter JMD yet.");
							}
						}
						_timestamp = checked(_timestamp + uint.MaxValue);
						continue;
					}

					var channel = (ushort)((first << 8) | second);
					if (!_streamTypes.TryGetValue(channel, out var streamType))
					{
						throw new InvalidDataException("The JMD packet refers to an unknown stream.");
					}
					_timestamp = checked(_timestamp + ReadBe32());
					var subtype = (byte)ReadByteRequired();
					var payloadLength = ReadVar();
					if (payloadLength > int.MaxValue || payloadLength > (ulong)(_stream.Length - _stream.Position))
					{
						throw new InvalidDataException("A JMD packet has an invalid payload length.");
					}

					packet = new JmdPacket { StreamType = streamType, Subtype = subtype, Timestamp = _timestamp };
					if (streamType == 0 && subtype is 0 or 1)
					{
						if (payloadLength < 4) throw new InvalidDataException("A JMD video packet is missing its dimensions.");
					packet.Width = ReadBe16();
					packet.Height = ReadBe16();
					var remaining = (int)payloadLength - 4;
					if (decode)
					{
						if (remaining > MaxFrameBytes) throw new InvalidDataException("A compressed JMD frame is too large.");
						packet.Data = ArrayPool<byte>.Shared.Rent((int)payloadLength);
						packet.DataLength = (int)payloadLength;
						packet.Data[0] = (byte)(packet.Width >> 8);
						packet.Data[1] = (byte)packet.Width;
						packet.Data[2] = (byte)(packet.Height >> 8);
						packet.Data[3] = (byte)packet.Height;
						try
						{
							ReadExactly(_stream, packet.Data, 4, remaining);
						}
						catch
						{
							ArrayPool<byte>.Shared.Return(packet.Data);
							throw;
						}
					}
					else
					{
						Skip(remaining);
					}
				}
				else if (streamType == 1 && subtype == 1)
				{
					if (payloadLength != 4) throw new InvalidDataException("A BizHawk PCM sample packet must contain a stereo sample pair.");
					if (decode)
					{
						packet.Left = unchecked((short)((ReadByteRequired() << 8) | ReadByteRequired()));
						packet.Right = unchecked((short)((ReadByteRequired() << 8) | ReadByteRequired()));
					}
					else
					{
						Skip(4);
					}
				}
				else
				{
					Skip((long)payloadLength);
				}
				return true;
			}
				return false;
			}

			private ushort ReadBe16()
			{
				var a = ReadByteRequired();
				var b = ReadByteRequired();
				return (ushort)((a << 8) | b);
			}

			private uint ReadBe32()
			{
				return ((uint)ReadByteRequired() << 24)
					| ((uint)ReadByteRequired() << 16)
					| ((uint)ReadByteRequired() << 8)
					| (uint)ReadByteRequired();
			}

			private int ReadByteRequired()
			{
				var value = _stream.ReadByte();
				if (value < 0) throw new EndOfStreamException("Unexpected end of JMD data.");
				return value;
			}

			private ulong ReadVar()
			{
				ulong value = 0;
				for (var i = 0; i < 10; i++)
				{
					var next = (byte)ReadByteRequired();
					if (value > (ulong.MaxValue >> 7)) throw new InvalidDataException("A JMD packet length overflows.");
					value = (value << 7) | (uint)(next & 0x7F);
					// BizHawk's JMD writer puts the terminating bit on the final byte; zero is
					// the special one-byte value 0x00.
					if ((i == 0 && next == 0) || (next & 0x80) != 0) return value;
				}
				throw new InvalidDataException("A JMD variable-length value is too long.");
			}

			private void Skip(long count)
			{
				if (count < 0 || count > _stream.Length - _stream.Position)
				{
					throw new EndOfStreamException("A JMD packet extends beyond the end of the file.");
				}
				_stream.Position += count;
			}

			public void Dispose() => _stream.Dispose();
		}
	}
}
