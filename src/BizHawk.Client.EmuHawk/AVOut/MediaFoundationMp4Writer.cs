using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

using BizHawk.Client.Common;
using BizHawk.Emulation.Common;

namespace BizHawk.Client.EmuHawk
{
	/// <summary>
	/// Experimental Windows Media Foundation H.264/AAC MP4 writer with configurable video/audio
	/// bitrates (see <see cref="CodecToken"/>). Splits output when the emulated resolution changes.
	/// All Media Foundation COM calls are confined to a dedicated MTA worker thread.
	/// </summary>
	[VideoWriter("mfmp4", "Native MP4 writer (Media Foundation)", "Encodes H.264 video and AAC audio into MP4 using Windows Media Foundation, with configurable video and audio bitrates. Splits on resolution change.")]
	public sealed class MediaFoundationMp4Writer : IVideoWriter
	{
		/// <summary>Default video bitrate (8 Mbps), used when no valid persisted value is available.</summary>
		public const int DefaultVideoBitrateKbps = 8_000;

		/// <summary>Default audio bitrate, used when no valid persisted value is available.</summary>
		public const int DefaultAudioBitrateKbps = 192;

		/// <summary>
		/// Lowest video bitrate accepted by this writer. Media Foundation's H.264 encoder only
		/// requires MF_MT_AVG_BITRATE to be greater than zero; this floor is a practical sanity
		/// bound, below which H.264 output at emulator resolutions/frame rates is unusable.
		/// </summary>
		public const int MinVideoBitrateKbps = 100;

		/// <summary>
		/// Highest video bitrate accepted by this writer (50 Mbps). As with
		/// <see cref="MinVideoBitrateKbps"/>, this is a practical writer-imposed bound rather than
		/// a Media Foundation limit; Media Foundation may still reject extreme bitrate/resolution
		/// combinations, which are reported as explicit recording failures when a segment is opened.
		/// </summary>
		public const int MaxVideoBitrateKbps = 50_000;

		private static readonly int[] SupportedAudioBitrateKbpsValues = [ 96, 128, 160, 192 ];

		/// <summary>
		/// Audio bitrates supported by the Media Foundation AAC encoder for mono/stereo input, in kbps.
		/// These correspond to the documented MF_MT_AUDIO_AVG_BYTES_PER_SECOND values
		/// 12000/16000/20000/24000 bytes per second; any other value is rejected by the encoder.
		/// </summary>
		public static readonly IReadOnlyList<int> SupportedAudioBitratesKbps = SupportedAudioBitrateKbpsValues;

		public static bool IsSupportedVideoBitrateKbps(int videoBitrateKbps)
			=> videoBitrateKbps >= MinVideoBitrateKbps && videoBitrateKbps <= MaxVideoBitrateKbps;

		public static bool IsSupportedAudioBitrateKbps(int audioBitrateKbps)
			=> Array.IndexOf(SupportedAudioBitrateKbpsValues, audioBitrateKbps) >= 0;

		private const long TicksPerSecond = 10_000_000;
		private const int QueueCapacity = 8;
		private const int ProducerBackpressureTimeoutMs = 10_000;
		private const int StartupTimeoutMs = 15_000;
		private const int CloseTimeoutMs = 30_000;

		/// <summary>
		/// Immutable video/audio bitrate settings for one recording, created either by the settings
		/// dialog (<see cref="MediaFoundationMp4Writer.AcquireVideoCodecToken"/>) or from persisted
		/// config values without UI (<see cref="LoadFromConfig(Config)"/>). Bitrates are stored in
		/// kbps (1 kbps = 1000 bps) and converted to Media Foundation units when a segment's output
		/// media types are built.
		/// </summary>
		public sealed class CodecToken : IDisposable
		{
			public int VideoBitrateKbps { get; }

			public int AudioBitrateKbps { get; }

			/// <summary>Video bitrate in bits per second, for MF_MT_AVG_BITRATE.</summary>
			internal uint VideoBitrateBps => checked((uint) VideoBitrateKbps * 1000u);

			/// <summary>Audio bitrate in bytes per second, for MF_MT_AUDIO_AVG_BYTES_PER_SECOND.</summary>
			internal uint AudioAvgBytesPerSecond => checked((uint) AudioBitrateKbps * 1000u / 8u);

			/// <exception cref="ArgumentOutOfRangeException">a bitrate is zero, negative, or otherwise unsupported</exception>
			public CodecToken(int videoBitrateKbps, int audioBitrateKbps)
			{
				if (!IsSupportedVideoBitrateKbps(videoBitrateKbps))
				{
					throw new ArgumentOutOfRangeException(nameof(videoBitrateKbps), videoBitrateKbps, $"Video bitrate must be between {MinVideoBitrateKbps} and {MaxVideoBitrateKbps} kbps.");
				}

				if (!IsSupportedAudioBitrateKbps(audioBitrateKbps))
				{
					throw new ArgumentOutOfRangeException(nameof(audioBitrateKbps), audioBitrateKbps, $"Audio bitrate must be one of {string.Join(", ", SupportedAudioBitrateKbpsValues)} kbps (the values supported by the Media Foundation AAC encoder for mono/stereo audio).");
				}

				VideoBitrateKbps = videoBitrateKbps;
				AudioBitrateKbps = audioBitrateKbps;
			}

			/// <summary>
			/// Builds a token from persisted config values. Each value is validated independently:
			/// a stale or hand-edited value that is zero, negative, or otherwise unsupported falls
			/// back to that field's default instead of preventing recording (notably in the
			/// unattended <see cref="MediaFoundationMp4Writer.SetDefaultVideoCodecToken(Config)"/>
			/// path, where there is no dialog in which the value could be corrected). The settings
			/// dialog validates strictly instead and rejects unsupported values.
			/// </summary>
			public static CodecToken LoadFromConfig(Config config)
			{
				if (config == null) throw new ArgumentNullException(nameof(config));
				return new CodecToken(
					IsSupportedVideoBitrateKbps(config.MfMp4VideoBitrateKbps) ? config.MfMp4VideoBitrateKbps : DefaultVideoBitrateKbps,
					IsSupportedAudioBitrateKbps(config.MfMp4AudioBitrateKbps) ? config.MfMp4AudioBitrateKbps : DefaultAudioBitrateKbps);
			}

			public void Dispose() { }
		}

		private enum JobKind { Video, Audio, SegmentBoundary }

		private sealed class Job
		{
			internal JobKind Kind { get; }
			internal int[] Pixels { get; }
			internal short[] Samples { get; }
			internal int Width { get; }
			internal int Height { get; }

			private Job(JobKind kind, int[] pixels, short[] samples, int width, int height)
			{
				Kind = kind;
				Pixels = pixels;
				Samples = samples;
				Width = width;
				Height = height;
			}

			internal static Job ForVideo(int[] pixels, int width, int height) => new Job(JobKind.Video, pixels, null, width, height);
			internal static Job ForAudio(short[] samples) => new Job(JobKind.Audio, null, samples, 0, 0);
			internal static Job ForSegmentBoundary(int width, int height) => new Job(JobKind.SegmentBoundary, null, null, width, height);
		}

		private sealed class SegmentContext
		{
			internal IMFSinkWriter Writer;
			internal IMFMediaType VideoOutputType;
			internal IMFMediaType VideoInputType;
			internal IMFMediaType AudioOutputType;
			internal IMFMediaType AudioInputType;
			internal int VideoStream;
			internal int AudioStream;
			internal int Width;
			internal int Height;
			internal long VideoFramesWritten;
			internal long AudioSampleFramesWritten;
		}

		private readonly IDialogParent _dialogParent;
		private readonly object _producerLock = new object();

		/// <summary>
		/// Backing store for the frame copies queued to the worker. <see cref="ArrayPool{T}.Shared"/>
		/// ignores arrays above one million elements (roughly 720p of pixels), so it would allocate a
		/// fresh array per 1080p frame; this pool covers up to 4K. Buckets are allocated on demand, so
		/// the cap costs nothing until the matching frame size is actually recorded.
		/// </summary>
		private readonly ArrayPool<int> _framePool = ArrayPool<int>.Create(3840 * 2160, QueueCapacity);

		private CodecToken _codecToken = new CodecToken(DefaultVideoBitrateKbps, DefaultAudioBitrateKbps);

		/// <summary>
		/// Token captured by <see cref="OpenFile(string)"/> and read only by the worker thread, so
		/// changing the writer's token mid-recording cannot affect later segments of that recording.
		/// </summary>
		private CodecToken _activeCodecToken;
		private BlockingCollection<Job> _queue;
		private Thread _worker;
		private ManualResetEventSlim _workerReady;
		private Exception _startupFailure;
		private Exception _failure;
		private bool _closing;
		private bool _closeTimedOut;

		private string _directory;
		private string _fileStem;
		private string _extension = ".mp4";
		private int _width;
		private int _height;
		private int _fpsNumerator;
		private int _fpsDenominator;
		private int _sampleRate;
		private int _channels;
		private int _bitsPerSample;

		public MediaFoundationMp4Writer(IDialogParent dialogParent) => _dialogParent = dialogParent;

		public bool UsesAudio => true;
		public bool UsesVideo => true;

		/// <summary>
		/// Sets the codec token used by the next recording. The value is captured by
		/// <see cref="OpenFile(string)"/>; calling this while a recording is open takes effect only
		/// after that recording is closed.
		/// </summary>
		public void SetVideoCodecToken(IDisposable token)
		{
			if (!(token is CodecToken nativeToken))
			{
				throw new ArgumentException($"{nameof(MediaFoundationMp4Writer)} only accepts its own codec tokens.", nameof(token));
			}
			_codecToken = nativeToken;
		}

		public void SetDefaultVideoCodecToken(Config config)
			=> _codecToken = CodecToken.LoadFromConfig(config);

		public IDisposable AcquireVideoCodecToken(Config config)
		{
			// Without a dialog parent there is no UI to show; fall back to the persisted
			// (sanitized) settings rather than failing, matching SetDefaultVideoCodecToken.
			return _dialogParent == null
				? (IDisposable) CodecToken.LoadFromConfig(config)
				: MediaFoundationMp4WriterForm.DoTokenForm(_dialogParent.AsWinFormsHandle(), config);
		}

		public string DesiredExtension() => "mp4";

		public void SetMovieParameters(int fpsNum, int fpsDen)
		{
			if (fpsNum <= 0) throw new ArgumentOutOfRangeException(nameof(fpsNum));
			if (fpsDen <= 0) throw new ArgumentOutOfRangeException(nameof(fpsDen));
			lock (_producerLock)
			{
				EnsureNotOpen("movie parameters");
				_fpsNumerator = fpsNum;
				_fpsDenominator = fpsDen;
			}
		}

		public void SetVideoParameters(int width, int height)
		{
			if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
			if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

			lock (_producerLock)
			{
				if (_width == width && _height == height) return;
				_width = width;
				_height = height;

				// Preserve FFmpegWriter's resolution-change behavior: finish the current file
				// and start a numbered segment, in queue order, on the Media Foundation thread.
				if (_queue != null && !_closing)
				{
					EnqueueLocked(Job.ForSegmentBoundary(width, height));
				}
			}
		}

		public void SetAudioParameters(int sampleRate, int channels, int bits)
		{
			if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
			if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
			if (bits != 16) throw new ArgumentOutOfRangeException(nameof(bits), "Sampling depth must be 16 bits.");
			lock (_producerLock)
			{
				EnsureNotOpen("audio parameters");
				_sampleRate = sampleRate;
				_channels = channels;
				_bitsPerSample = bits;
			}
		}

		public void SetMetaData(string gameName, string authors, ulong lengthMs, ulong rerecords)
		{
			// Sink Writer metadata is not needed for the first recording/hang reproduction pass.
		}

		public void SetFrame(int frame)
		{
			// The existing FFmpeg writer does not use this value either; timestamps are
			// generated from the emitted video frames and audio sample count.
		}

		public void OpenFile(string baseName)
		{
			if (string.IsNullOrWhiteSpace(baseName)) throw new ArgumentException("Output path is required.", nameof(baseName));

			lock (_producerLock)
			{
				if (_worker != null) throw new InvalidOperationException("A native MP4 recording is already open.");
				if (_width <= 0 || _height <= 0) throw new InvalidOperationException("Video parameters must be set before opening the file.");
				if (_fpsNumerator <= 0 || _fpsDenominator <= 0) throw new InvalidOperationException("Movie frame rate must be set before opening the file.");
				if (_sampleRate <= 0 || _channels <= 0 || _bitsPerSample != 16) throw new InvalidOperationException("Audio parameters must be set before opening the file.");

				var fullPath = Path.GetFullPath(baseName);
				_directory = Path.GetDirectoryName(fullPath);
				if (string.IsNullOrEmpty(_directory)) _directory = Environment.CurrentDirectory;
				_fileStem = Path.GetFileNameWithoutExtension(fullPath);
				_extension = ".mp4";

				_failure = null;
				_startupFailure = null;
				_closing = false;
				_activeCodecToken = _codecToken;
				_queue = new BlockingCollection<Job>(QueueCapacity);
				_workerReady = new ManualResetEventSlim(false);

				var initialWidth = _width;
				var initialHeight = _height;
				_worker = new Thread(() => WorkerMain(initialWidth, initialHeight))
				{
					IsBackground = true,
					Name = "Media Foundation MP4 writer"
				};
				_worker.SetApartmentState(ApartmentState.MTA);
				_worker.Start();
			}

			if (!_workerReady.Wait(StartupTimeoutMs))
			{
				var timeout = new TimeoutException($"Media Foundation did not open the MP4 output within {StartupTimeoutMs} ms.");
				SetFailure(timeout);
				lock (_producerLock)
				{
					if (_queue != null && !_closing)
					{
						_closing = true;
						_queue.CompleteAdding();
					}
				}
				throw new IOException("Could not start the native MP4 writer.", timeout);
			}

			if (_startupFailure != null)
			{
				var startupError = _startupFailure;
				if (_worker != null && _worker.Join(5_000)) ClearStoppedWorker();
				throw new IOException($"Windows Media Foundation could not start the MP4 recording: {startupError.Message}", startupError);
			}
		}

		public void AddFrame(IVideoProvider source)
		{
			if (source == null) throw new ArgumentNullException(nameof(source));
			lock (_producerLock)
			{
				EnsureOpen();
				if (source.BufferWidth != _width || source.BufferHeight != _height)
				{
					SetVideoParameters(source.BufferWidth, source.BufferHeight);
				}

				var count = checked(_width * _height);
				var sourcePixels = source.GetVideoBuffer();
				if (sourcePixels == null || sourcePixels.Length < count)
				{
					throw new InvalidOperationException("The video provider returned a buffer shorter than the configured frame dimensions.");
				}

				// IVideoProvider buffers are live emulator-owned memory. Rent a pooled buffer and
				// copy before returning to the emulation loop, rather than letting the worker race
				// the next frame; the worker returns the buffer once the frame has been encoded.
				var copy = _framePool.Rent(count);
				try
				{
					Array.Copy(sourcePixels, 0, copy, 0, count);
					EnqueueLocked(Job.ForVideo(copy, _width, _height));
				}
				catch
				{
					_framePool.Return(copy);
					throw;
				}
			}
		}

		public void AddSamples(short[] samples)
		{
			if (samples == null) throw new ArgumentNullException(nameof(samples));
			if (samples.Length == 0) return;

			lock (_producerLock)
			{
				EnsureOpen();
				if (samples.Length % _channels != 0)
				{
					throw new ArgumentException("PCM sample count must contain complete interleaved audio frames.", nameof(samples));
				}
				EnqueueLocked(Job.ForAudio((short[])samples.Clone()));
			}
		}

		public void CloseFile()
		{
			Thread worker;
			lock (_producerLock)
			{
				worker = _worker;
				if (worker == null) return;
				// A previous close already reported a wedge. Don't block again on a worker that is
				// still stuck (close runs on the UI thread); retry only once it has exited.
				if (_closeTimedOut && worker.IsAlive) return;
				if (!_closing)
				{
					_closing = true;
					_queue.CompleteAdding();
				}
			}

			if (!worker.Join(CloseTimeoutMs))
			{
				// COM calls are never forcibly aborted from the UI thread. Report the wedge
				// explicitly rather than making the client wait forever in CloseFile().
				lock (_producerLock) _closeTimedOut = true;
				throw new TimeoutException($"Media Foundation did not finish/close the MP4 within {CloseTimeoutMs} ms. The output may be incomplete.");
			}

			var failure = ReadFailure();
			ClearStoppedWorker();
			if (failure != null) throw new IOException($"Native MP4 recording failed; the output may be incomplete: {failure.Message}", failure);
		}

		/// <summary>
		/// Requests finalization and never throws. Close failures are reported by
		/// <see cref="CloseFile()"/>, which callers invoke where an error can be shown. After a close
		/// timeout this does not wait again while the worker is still running; the worker finishes on
		/// its own and releases its Media Foundation resources when it exits.
		/// </summary>
		public void Dispose()
		{
			try
			{
				CloseFile();
			}
			catch
			{
				// Finalization errors are reported by CloseFile; Dispose is the last-resort callers
				// use during teardown, where throwing would leave the caller half-reset.
			}
		}

		private void EnsureNotOpen(string parameter)
		{
			if (_worker != null) throw new InvalidOperationException($"Cannot change {parameter} while a native MP4 recording is open.");
		}

		private void EnsureOpen()
		{
			if (_queue == null || _worker == null || _closing) throw new InvalidOperationException("No native MP4 recording is open.");
			var failure = ReadFailure();
			if (failure != null) throw new IOException("Native MP4 writer worker has failed.", failure);
		}

		private void EnqueueLocked(Job job)
		{
			var queue = _queue;
			if (queue == null) throw new InvalidOperationException("No native MP4 recording is open.");
			var timer = Stopwatch.StartNew();
			while (true)
			{
				var failure = ReadFailure();
				if (failure != null) throw new IOException("Native MP4 writer failed while recording.", failure);
				if (_closing || queue.IsAddingCompleted) throw new IOException("Native MP4 writer is closing and no longer accepts samples.");

				try
				{
					if (queue.TryAdd(job, 250)) return;
				}
				catch (InvalidOperationException ex) when (queue.IsAddingCompleted)
				{
					throw new IOException("Native MP4 writer stopped accepting samples.", ReadFailure() ?? ex);
				}

				if (timer.ElapsedMilliseconds >= ProducerBackpressureTimeoutMs)
				{
					var timeout = new TimeoutException($"Media Foundation fell behind recording input for {ProducerBackpressureTimeoutMs} ms. Recording was stopped rather than blocking the emulator indefinitely.");
					SetFailure(timeout);
					throw new IOException("Native MP4 writer queue remained full; recording failed explicitly.", timeout);
				}
			}
		}

		private void WorkerMain(int initialWidth, int initialHeight)
		{
			var comInitialized = false;
			var mfStarted = false;
			SegmentContext segment = null;
			var segmentNumber = 0;

			try
			{
				MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, MfNative.CoInitMultithreaded), "CoInitializeEx(COINIT_MULTITHREADED)");
				comInitialized = true;
				MfNative.Check(MfNative.MFStartup(MfNative.MfVersion, MfNative.MfStartupFull), "MFStartup");
				mfStarted = true;

				segment = OpenSegment(GetSegmentPath(segmentNumber), initialWidth, initialHeight);
				_workerReady.Set();

				foreach (var job in _queue.GetConsumingEnumerable())
				{
					if (ReadFailure() != null)
					{
						// The job was dequeued before the failure check, so return its buffer here;
						// the switch below never runs for it.
						if (job.Kind == JobKind.Video) _framePool.Return(job.Pixels);
						break;
					}
					switch (job.Kind)
					{
						case JobKind.Video:
							try
							{
								if (job.Width != segment.Width || job.Height != segment.Height)
								{
									throw new InvalidOperationException("A queued video frame does not match the active MP4 segment dimensions.");
								}
								WriteVideo(segment, job.Pixels);
							}
							finally
							{
								_framePool.Return(job.Pixels);
							}
							break;
						case JobKind.Audio:
							WriteAudio(segment, job.Samples);
							break;
						case JobKind.SegmentBoundary:
							var oldSegment = segment;
							segment = null;
							CloseSegment(oldSegment);
							segmentNumber++;
							segment = OpenSegment(GetSegmentPath(segmentNumber), job.Width, job.Height);
							break;
						default:
							throw new InvalidOperationException("Unknown native MP4 writer job.");
					}
				}
			}
			catch (Exception ex)
			{
				SetFailure(ex);
				if (_workerReady != null && !_workerReady.IsSet)
				{
					_startupFailure = ex;
					_workerReady.Set();
				}
			}
			finally
			{
				if (!_workerReady.IsSet)
				{
					_startupFailure = ReadFailure() ?? new InvalidOperationException("Media Foundation worker exited before startup completed.");
					_workerReady.Set();
				}

				if (segment != null)
				{
					try { CloseSegment(segment); }
					catch (Exception ex) { SetFailure(ex); }
				}

				if (mfStarted)
				{
					try { MfNative.Check(MfNative.MFShutdown(), "MFShutdown"); }
					catch (Exception ex) { SetFailure(ex); }
				}
				if (comInitialized) MfNative.CoUninitialize();
			}
		}

		private string GetSegmentPath(int segmentNumber)
			=> Path.Combine(_directory, _fileStem + (segmentNumber == 0 ? string.Empty : "_" + segmentNumber) + _extension);

		private SegmentContext OpenSegment(string path, int width, int height)
		{
			IMFSinkWriter writer = null;
			IMFMediaType videoOutput = null;
			IMFMediaType videoInput = null;
			IMFMediaType audioOutput = null;
			IMFMediaType audioInput = null;
			try
			{
				MfNative.Check(MfNative.MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer), "MFCreateSinkWriterFromURL");
				videoOutput = CreateVideoOutputType(width, height, _activeCodecToken.VideoBitrateBps);
				videoInput = CreateVideoInputType(width, height);
				audioOutput = CreateAudioOutputType(_activeCodecToken.AudioAvgBytesPerSecond);
				audioInput = CreateAudioInputType();

				MfNative.Check(writer.AddStream(videoOutput, out var videoStream), "IMFSinkWriter.AddStream(video)");
				MfNative.Check(writer.AddStream(audioOutput, out var audioStream), "IMFSinkWriter.AddStream(audio)");
				MfNative.Check(writer.SetInputMediaType(videoStream, videoInput, null), "IMFSinkWriter.SetInputMediaType(video)");
				MfNative.Check(writer.SetInputMediaType(audioStream, audioInput, null), "IMFSinkWriter.SetInputMediaType(audio)");
				MfNative.Check(writer.BeginWriting(), "IMFSinkWriter.BeginWriting");

				var context = new SegmentContext
				{
					Writer = writer,
					VideoOutputType = videoOutput,
					VideoInputType = videoInput,
					AudioOutputType = audioOutput,
					AudioInputType = audioInput,
					VideoStream = videoStream,
					AudioStream = audioStream,
					Width = width,
					Height = height,
				};
				writer = null;
				videoOutput = null;
				videoInput = null;
				audioOutput = null;
				audioInput = null;
				return context;
			}
			catch
			{
				MfNative.Release(audioInput);
				MfNative.Release(audioOutput);
				MfNative.Release(videoInput);
				MfNative.Release(videoOutput);
				MfNative.Release(writer);
				throw;
			}
		}

		private void CloseSegment(SegmentContext segment)
		{
			Exception failure = null;
			try
			{
				if (segment.Writer != null) MfNative.Check(segment.Writer.Finalize_(), "IMFSinkWriter.Finalize");
			}
			catch (Exception ex)
			{
				failure = ex;
			}
			finally
			{
				MfNative.Release(segment.AudioInputType);
				MfNative.Release(segment.AudioOutputType);
				MfNative.Release(segment.VideoInputType);
				MfNative.Release(segment.VideoOutputType);
				MfNative.Release(segment.Writer);
			}
			if (failure != null) throw failure;
		}

		private void WriteVideo(SegmentContext segment, int[] pixels)
		{
			var count = checked(segment.Width * segment.Height);
			var start = VideoTicks(segment.VideoFramesWritten);
			var end = VideoTicks(segment.VideoFramesWritten + 1);
			// BizHawk's int pixel buffer is laid out as BGRA bytes on little-endian Windows;
			// MFVideoFormat_RGB32 consumes B,G,R,X bytes, so a direct int copy is appropriate.
			WriteSample(segment.Writer, segment.VideoStream, checked(count * sizeof(int)), start, end - start, "video frame",
				destination => Marshal.Copy(pixels, 0, destination, count));
			segment.VideoFramesWritten++;
		}

		private void WriteAudio(SegmentContext segment, short[] samples)
		{
			var sampleFrames = samples.Length / _channels;
			if (sampleFrames == 0) return;
			var start = AudioTicks(segment.AudioSampleFramesWritten);
			var end = AudioTicks(segment.AudioSampleFramesWritten + sampleFrames);
			WriteSample(segment.Writer, segment.AudioStream, checked(samples.Length * sizeof(short)), start, end - start, "audio samples",
				destination => Marshal.Copy(samples, 0, destination, samples.Length));
			segment.AudioSampleFramesWritten += sampleFrames;
		}

		private void WriteSample(
			IMFSinkWriter writer,
			int stream,
			int byteLength,
			long time,
			long duration,
			string description,
			Action<IntPtr> copyToBuffer)
		{
			IMFSample sample = null;
			IMFMediaBuffer buffer = null;
			var locked = false;
			try
			{
				MfNative.Check(MfNative.MFCreateSample(out sample), "MFCreateSample(" + description + ")");
				MfNative.Check(MfNative.MFCreateMemoryBuffer(checked((uint)byteLength), out buffer), "MFCreateMemoryBuffer(" + description + ")");
				MfNative.Check(buffer.Lock(out var destination, out var maxLength, out _), "IMFMediaBuffer.Lock(" + description + ")");
				locked = true;
				if (maxLength < byteLength) throw new InvalidOperationException("Media Foundation returned a short sample buffer.");
				copyToBuffer(destination);
				MfNative.Check(buffer.Unlock(), "IMFMediaBuffer.Unlock(" + description + ")");
				locked = false;
				MfNative.Check(buffer.SetCurrentLength(checked((uint)byteLength)), "IMFMediaBuffer.SetCurrentLength(" + description + ")");
				MfNative.Check(sample.AddBuffer(buffer), "IMFSample.AddBuffer(" + description + ")");
				MfNative.Check(sample.SetSampleTime(time), "IMFSample.SetSampleTime(" + description + ")");
				MfNative.Check(sample.SetSampleDuration(duration), "IMFSample.SetSampleDuration(" + description + ")");
				MfNative.Check(writer.WriteSample(stream, sample), "IMFSinkWriter.WriteSample(" + description + ")");
			}
			finally
			{
				if (locked && buffer != null)
				{
					try { buffer.Unlock(); } catch { }
				}
				MfNative.Release(buffer);
				MfNative.Release(sample);
			}
		}

		private IMFMediaType CreateVideoOutputType(int width, int height, uint bitrate)
		{
			var type = CreateMediaType("video output");
			try
			{
				SetGuid(type, MfGuids.MtMajorType, MfGuids.MediaTypeVideo);
				SetGuid(type, MfGuids.MtSubtype, MfGuids.VideoFormatH264);
				SetUInt32(type, MfGuids.MtAvgBitrate, bitrate);
				SetUInt32(type, MfGuids.MtMpeg2Profile, 66); // H.264 Baseline profile
				SetUInt32(type, MfGuids.MtInterlaceMode, 2); // progressive
				SetUInt64(type, MfGuids.MtFrameSize, PackPair(width, height));
				SetUInt64(type, MfGuids.MtFrameRate, PackPair(_fpsNumerator, _fpsDenominator));
				SetUInt64(type, MfGuids.MtPixelAspectRatio, PackPair(1, 1));
				return type;
			}
			catch { MfNative.Release(type); throw; }
		}

		private IMFMediaType CreateVideoInputType(int width, int height)
		{
			var type = CreateMediaType("video input");
			try
			{
				SetGuid(type, MfGuids.MtMajorType, MfGuids.MediaTypeVideo);
				SetGuid(type, MfGuids.MtSubtype, MfGuids.VideoFormatRgb32);
				SetUInt32(type, MfGuids.MtInterlaceMode, 2);
				SetUInt64(type, MfGuids.MtFrameSize, PackPair(width, height));
				SetUInt64(type, MfGuids.MtFrameRate, PackPair(_fpsNumerator, _fpsDenominator));
				SetUInt64(type, MfGuids.MtPixelAspectRatio, PackPair(1, 1));
				SetUInt32(type, MfGuids.MtDefaultStride, unchecked((uint)(width * 4)));
				return type;
			}
			catch { MfNative.Release(type); throw; }
		}

		private IMFMediaType CreateAudioOutputType(uint avgBytesPerSecond)
		{
			// The supported audio bitrates (and therefore the codec token and settings dialog)
			// cover the Media Foundation AAC encoder's mono/stereo values. The encoder also
			// accepts 6-channel input, but only at six times those byte rates, which this writer
			// does not offer; fail explicitly here instead of surfacing a bare
			// MF_E_INVALIDMEDIATYPE from stream setup for other channel counts.
			if (_channels != 1 && _channels != 2)
			{
				throw new InvalidOperationException($"Native MP4 writer only supports mono/stereo AAC audio bitrates ({string.Join(", ", SupportedAudioBitrateKbpsValues)} kbps); {_channels}-channel audio is unsupported.");
			}

			var type = CreateMediaType("audio output");
			try
			{
				SetGuid(type, MfGuids.MtMajorType, MfGuids.MediaTypeAudio);
				SetGuid(type, MfGuids.MtSubtype, MfGuids.AudioFormatAac);
				SetUInt32(type, MfGuids.MtAudioNumChannels, checked((uint)_channels));
				SetUInt32(type, MfGuids.MtAudioSamplesPerSecond, checked((uint)_sampleRate));
				SetUInt32(type, MfGuids.MtAudioBitsPerSample, checked((uint)_bitsPerSample));
				SetUInt32(type, MfGuids.MtAudioAvgBytesPerSecond, avgBytesPerSecond);
				SetUInt32(type, MfGuids.MtAacPayloadType, 0); // raw AAC access units in MP4
				SetUInt32(type, MfGuids.MtAacAudioProfileLevelIndication, 0x29); // AAC-LC, level 2
				return type;
			}
			catch { MfNative.Release(type); throw; }
		}

		private IMFMediaType CreateAudioInputType()
		{
			var type = CreateMediaType("audio input");
			try
			{
				SetGuid(type, MfGuids.MtMajorType, MfGuids.MediaTypeAudio);
				SetGuid(type, MfGuids.MtSubtype, MfGuids.AudioFormatPcm);
				SetUInt32(type, MfGuids.MtAudioNumChannels, checked((uint)_channels));
				SetUInt32(type, MfGuids.MtAudioSamplesPerSecond, checked((uint)_sampleRate));
				SetUInt32(type, MfGuids.MtAudioBitsPerSample, checked((uint)_bitsPerSample));
				SetUInt32(type, MfGuids.MtAudioBlockAlignment, checked((uint)(_channels * _bitsPerSample / 8)));
				SetUInt32(type, MfGuids.MtAudioAvgBytesPerSecond, checked((uint)(_sampleRate * _channels * _bitsPerSample / 8)));
				return type;
			}
			catch { MfNative.Release(type); throw; }
		}

		private static IMFMediaType CreateMediaType(string label)
		{
			MfNative.Check(MfNative.MFCreateMediaType(out var type), "MFCreateMediaType(" + label + ")");
			return type;
		}

		private static void SetGuid(IMFAttributes attributes, Guid key, Guid value)
			=> MfNative.Check(attributes.SetGUID(ref key, ref value), "IMFAttributes.SetGUID(" + key + ")");

		private static void SetUInt32(IMFAttributes attributes, Guid key, uint value)
			=> MfNative.Check(attributes.SetUINT32(ref key, value), "IMFAttributes.SetUINT32(" + key + ")");

		private static void SetUInt64(IMFAttributes attributes, Guid key, ulong value)
			=> MfNative.Check(attributes.SetUINT64(ref key, value), "IMFAttributes.SetUINT64(" + key + ")");

		private static ulong PackPair(int high, int low)
			=> ((ulong)(uint)high << 32) | (uint)low;

		private long VideoTicks(long frame)
			=> checked(frame * TicksPerSecond * _fpsDenominator / _fpsNumerator);

		private long AudioTicks(long sampleFrame)
			=> checked(sampleFrame * TicksPerSecond / _sampleRate);

		private void ClearStoppedWorker()
		{
			lock (_producerLock)
			{
				if (_worker != null && _worker.IsAlive) return;
				_worker = null;
				if (_queue != null)
				{
					_queue.Dispose();
					_queue = null;
				}
				if (_workerReady != null)
				{
					_workerReady.Dispose();
					_workerReady = null;
				}
				_activeCodecToken = null;
				_closing = false;
				_closeTimedOut = false;
			}
		}

		private Exception ReadFailure() => Interlocked.CompareExchange(ref _failure, null, null);
		private void SetFailure(Exception error) => Interlocked.CompareExchange(ref _failure, error, null);
	}
}