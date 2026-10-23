using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading;

using BizHawk.Client.Common;
using BizHawk.Common;
using BizHawk.Common.PathExtensions;
using BizHawk.Emulation.Common;

namespace BizHawk.Client.EmuHawk
{
	/// <summary>
	/// uses pipes to launch an external ffmpeg process and encode
	/// </summary>
	[VideoWriter("ffmpeg", "FFmpeg writer", "Uses an external FFMPEG process to encode video and audio.  Various formats supported.  Splits on resolution change.")]
	public class FFmpegWriter : IVideoWriter
	{
		private readonly IDialogParent _dialogParent;

		/// <summary>
		/// handle to external ffmpeg process
		/// </summary>
		private Process _ffmpeg;

		/// <summary>
		/// the commandline actually sent to ffmpeg; for informative purposes
		/// </summary>
		private string _commandline;

		/// <summary>
		/// current file segment (for multires)
		/// </summary>
		private int _segment;

		/// <summary>
		/// base filename before segment number is attached
		/// </summary>
		private string _baseName;

		/// <summary>
		/// recent lines in ffmpeg's stderr, for informative purposes
		/// </summary>
		private Queue<string> _stderr;

		/// <summary>
		/// guards <see cref="_stderr"/>, which is touched by both the stderr event thread and the main thread
		/// </summary>
		private readonly object _stderrLock = new();

		/// <summary>
		/// number of lines of stderr to buffer
		/// </summary>
		private const int Consolebuffer = 5;

		/// <summary>
		/// muxer handle for the current segment
		/// </summary>
		private NutMuxer _muxer;

		/// <summary>
		/// codec token in use
		/// </summary>
		private FFmpegWriterForm.FormatPreset _token;

		/// <summary>
		/// file extension actually used
		/// </summary>
		private string _ext;

		public FFmpegWriter(IDialogParent dialogParent) => _dialogParent = dialogParent;

		public void SetFrame(int frame)
		{
		}

		public void OpenFile(string baseName)
		{
			var (dir, fileNoExt, ext) = baseName.SplitPathToDirFileAndExt();
			_baseName = Path.Combine(dir!, fileNoExt);
			_ext = ext;
			_segment = 0;
			OpenFileSegment();
		}

		/// <summary>
		/// starts an ffmpeg process and sets up associated sockets
		/// </summary>
		private void OpenFileSegment()
		{
			try
			{
				_ffmpeg = OSTailoredCode.ConstructSubshell(
					FFmpegService.FFmpegPath,
					$"-y -f nut -i - {_token.Commandline} \"{_baseName}{(_segment == 0 ? string.Empty : $"_{_segment}")}{_ext}\"",
					checkStdout: false,
					checkStderr: true // ffmpeg sends informative display to stderr, and nothing to stdout
				);

				_commandline = $"ffmpeg {_ffmpeg.StartInfo.Arguments}";

				_ffmpeg.ErrorDataReceived += StderrHandler;

				_stderr = new Queue<string>(Consolebuffer);

				_ffmpeg.Start();
			}
			catch
			{
				_ffmpeg.Dispose();
				_ffmpeg = null;
				throw;
			}

			_ffmpeg.BeginErrorReadLine();

			StallDiag.SetExtraState(() =>
			{
				try
				{
					return $"ffmpeg: exited={_ffmpeg.HasExited} queued={_jobs.Count} pumpAlive={_pump is not null && _pump.IsAlive} pumpError={_pumpError?.Message ?? "none"}";
				}
				catch (Exception e)
				{
					return $"ffmpeg state unavailable: {e.Message}";
				}
			});
			_muxer = new NutMuxer(_width, _height, _fpsnum, _fpsden, _sampleRate, _channels, new WedgeGuardStream(_ffmpeg.StandardInput.BaseStream, this));

			_jobs = new BlockingCollection<Job>(boundedCapacity: 8);
			_pump = new Thread(PumpLoop) { IsBackground = true, Name = "FFmpegWriter pump" };
			_pump.Start();
			StallDiag.Note($"ffmpeg segment open done {_width}x{_height}");
		}

		/// <summary>
		/// saves stderr lines from ffmpeg in a short queue
		/// </summary>
		private void StderrHandler(object p, DataReceivedEventArgs line)
		{
			if (!string.IsNullOrEmpty(line.Data))
			{
				lock (_stderrLock)
				{
					// a late event can race with segment teardown nulling out the queue
					if (_stderr is null) return;

					if (_stderr.Count == Consolebuffer)
					{
						_stderr.Dequeue();
					}

					_stderr.Enqueue($"{line.Data}\n");
				}
			}
		}

		/// <summary>
		/// how long a single pipe write may block before we conclude ffmpeg stopped consuming
		/// </summary>
		private const int PipeWriteTimeoutMs = 10000;

		private void KillWedgedFfmpeg()
		{
			try
			{
				_ffmpeg.Kill();
				_ffmpeg.WaitForExit(5000);
			}
			catch
			{
				// already gone or inaccessible; the throwing write still reports the wedge
			}
		}

		/// <summary>
		/// wraps the ffmpeg stdin pipe so a full pipe cannot block the calling thread forever:
		/// a write making no progress for <see cref="PipeWriteTimeoutMs"/> means ffmpeg wedged, so the
		/// process is killed and the write throws (surfaced as a "dumping died" dialog carrying ffmpeg's
		/// last stderr lines) instead of hanging the whole client silently.
		/// skipping the write instead is not an option: NUT is a length-prefixed byte stream, and dropped
		/// bytes would corrupt every later packet anyway
		/// </summary>
		private sealed class WedgeGuardStream : Stream
		{
			private readonly Stream _inner;
			private readonly FFmpegWriter _owner;
			private bool _wedged;

			public WedgeGuardStream(Stream inner, FFmpegWriter owner)
			{
				_inner = inner;
				_owner = owner;
			}

			public override bool CanRead => false;
			public override bool CanSeek => false;
			public override bool CanWrite => true;
			public override long Length => throw new NotSupportedException();
			public override long Position
			{
				get => throw new NotSupportedException();
				set => throw new NotSupportedException();
			}

			public override void Flush() => _inner.Flush();
			public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();

			public override void Write(byte[] buffer, int offset, int count)
			{
				if (_wedged) throw new IOException("ffmpeg pipe already wedged");

				StallDiag.Note($"pipe write enter {count}");
				var ar = _inner.BeginWrite(buffer, offset, count, null, null);
				if (!ar.AsyncWaitHandle.WaitOne(PipeWriteTimeoutMs))
				{
					StallDiag.Note("pipe write TIMEOUT, killing ffmpeg");
					_wedged = true;
					_owner.KillWedgedFfmpeg();
					throw new IOException($"ffmpeg did not consume a pipe write for {PipeWriteTimeoutMs} ms; process killed");
				}

				_inner.EndWrite(ar);
				StallDiag.Note("pipe write exit");
			}

			// the pipe's lifetime belongs to the ffmpeg process object, not to us
			protected override void Dispose(bool disposing)
			{
			}
		}

		/// <summary>
		/// one unit of work for the pump thread; exactly one of Video/Audio is set
		/// </summary>
		private sealed record Job(int[] Video, int VideoLen, short[] Audio);

		private BlockingCollection<Job> _jobs;
		private Thread _pump;
		private volatile Exception _pumpError;

		/// <summary>
		/// writes queued frames to the muxer (and thus the ffmpeg pipe) off the main thread, so a pipe
		/// that stops draining can never block the client UI; the bounded queue plus TryAdd timeout
		/// turn a wedged ffmpeg into an exception on the calling thread instead of an infinite hang
		/// </summary>
		private void PumpLoop()
		{
			try
			{
				foreach (var j in _jobs.GetConsumingEnumerable())
				{
					StallDiag.Note(j.Video is not null ? $"pump video {j.VideoLen}" : $"pump audio {j.Audio.Length}");
					if (j.Video is not null)
					{
						_muxer.WriteVideoFrame(j.Video.AsSpan(0, j.VideoLen));
						ArrayPool<int>.Shared.Return(j.Video);
					}
					else
					{
						_muxer.WriteAudioFrame(j.Audio);
					}
				}
			}
			catch (Exception e)
			{
				StallDiag.Note($"pump died: {e}");
				_pumpError = e;
				_jobs.CompleteAdding();
			}
		}

		private void Enqueue(Job j)
		{
			if (_pumpError is not null) throw new Exception("ffmpeg pump died", _pumpError);
			if (!_jobs.TryAdd(j, TimeSpan.FromSeconds(10)))
			{
				StallDiag.Note($"Enqueue TIMEOUT queued={_jobs.Count} exited={_ffmpeg.HasExited}");
				throw new TimeoutException($"ffmpeg not consuming: queued={_jobs.Count} exited={_ffmpeg.HasExited}\n{FfmpegGetError()}");
			}
		}

		/// <summary>
		/// finishes an ffmpeg process
		/// </summary>
		private void CloseFileSegment()
		{
			// let the pump finish everything it already accepted before the muxer writes EOR
			StallDiag.Note("CloseFileSegment enter, joining pump");
			_jobs?.CompleteAdding();
			_pump?.Join();
			StallDiag.Note("pump joined");
			_pump = null;
			_jobs = null;
			// pump failures already surfaced on the calling thread via Enqueue; don't rethrow here,
			// CloseFileSegment also runs during abort/teardown
			_pumpError = null;

			try
			{
				_muxer.Finish();
			}
			catch (IOException)
			{
				// pipe already dead (wedged ffmpeg was killed); nothing left to flush
			}
			// give ffmpeg a real EOF on stdin: EOR packets alone left it reading the pipe forever,
			// which is what burned the full exit timeout on the UI thread during segment splits
			try
			{
				_ffmpeg.StandardInput.Close();
				StallDiag.Note("stdin closed");
			}
			catch
			{
				// already closed or gone
			}

			// bounded, short waits: this runs on the UI thread during segment splits, so a wedged
			// process must be killed promptly rather than waited out
			StallDiag.Note("waiting for ffmpeg exit (2s)");
			if (!_ffmpeg.WaitForExit(2000))
			{
				StallDiag.Note("ffmpeg did not exit in 2s, killing");
				try
				{
					_ffmpeg.Kill();
				}
				catch
				{
					// already gone or inaccessible
				}

				_ffmpeg.WaitForExit(2000);
			}

			if (_ffmpeg.HasExited)
			{
				// Known MS bug: WaitForExit(time) waits for the process to exit but doesn't wait for event
				//               handling to finish; this call (process confirmed exited) flushes the events
				_ffmpeg.WaitForExit();
			}
			lock (_stderrLock)
			{
				_stderr = null;
			}

			_ffmpeg.Dispose();
			_ffmpeg = null;
			_commandline = null;
			_muxer = null;
		}


		public void CloseFile()
		{
			CloseFileSegment();
			_baseName = null;
		}

		/// <summary>
		/// returns a string containing the commandline sent to ffmpeg and recent console (stderr) output
		/// </summary>
		private string FfmpegGetError()
		{
			if (_ffmpeg.StartInfo.RedirectStandardError)
			{
				_ffmpeg.CancelErrorRead();
			}

			var s = new StringBuilder();
			s.Append(_commandline);
			s.Append('\n');
			lock (_stderrLock)
			{
				if (_stderr is not null)
				{
					while (_stderr.Count > 0)
					{
						var foo = _stderr.Dequeue();
						s.Append(foo);
					}
				}
			}

			return s.ToString();
		}

		/// <exception cref="Exception">FFmpeg call failed</exception>
		public void AddFrame(IVideoProvider source)
		{
			if (source.BufferWidth != _width || source.BufferHeight != _height)
			{
				SetVideoParameters(source.BufferWidth, source.BufferHeight);
			}

			if (_ffmpeg.HasExited)
			{
				throw new Exception($"unexpected ffmpeg death:\n{FfmpegGetError()}");
			}

			var video = source.GetVideoBuffer();
			var buf = ArrayPool<int>.Shared.Rent(_width * _height);
			Array.Copy(video, 0, buf, 0, _width * _height);
			Enqueue(new Job(buf, _width * _height, null));
		}

		public IDisposable AcquireVideoCodecToken(Config config)
		{
			if (!FFmpegService.QueryServiceAvailable())
			{
				using var form = new FFmpegDownloaderForm();
				_dialogParent.ShowDialogWithTempMute(form);
				if (!FFmpegService.QueryServiceAvailable()) return null;
			}
			return FFmpegWriterForm.DoFFmpegWriterDlg(_dialogParent.AsWinFormsHandle(), config);
		}

		/// <exception cref="ArgumentException"><paramref name="token"/> does not inherit <see cref="FFmpegWriterForm.FormatPreset"/></exception>
		public void SetVideoCodecToken(IDisposable token)
		{
			if (token is FFmpegWriterForm.FormatPreset preset)
			{
				_token = preset;
			}
			else
			{
				throw new ArgumentException(message: $"{nameof(FFmpegWriter)} can only take its own codec tokens!", paramName: nameof(token));
			}
		}

		/// <summary>
		/// video params
		/// </summary>
		private int _fpsnum, _fpsden, _width, _height, _sampleRate, _channels;

		public void SetMovieParameters(int fpsNum, int fpsDen)
		{
			_fpsnum = fpsNum;
			_fpsden = fpsDen;
		}

		public void SetVideoParameters(int width, int height)
		{
			_width = width;
			_height = height;

			/* ffmpeg theoretically supports variable resolution videos, but in practice that's not handled very well.
			 * so we start a new segment.
			 */
			if (_ffmpeg != null)
			{
				CloseFileSegment();
				_segment++;
				OpenFileSegment();
			}
		}


		public void SetMetaData(string gameName, string authors, ulong lengthMS, ulong rerecords)
		{
			// can be implemented with ffmpeg "-metadata" parameter???
			// nyi
		}

		public void Dispose()
		{
			if (_ffmpeg != null)
			{
				CloseFile();
			}
		}

		/// <exception cref="Exception">FFmpeg call failed</exception>
		public void AddSamples(short[] samples)
		{
			if (_ffmpeg.HasExited)
			{
				throw new Exception($"unexpected ffmpeg death:\n{FfmpegGetError()}");
			}

			if (samples.Length == 0)
			{
				// has special meaning for the muxer, so don't pass on
				return;
			}

			var buf = new short[samples.Length];
			Array.Copy(samples, buf, samples.Length);
			Enqueue(new Job(null, 0, buf));
		}

		/// <exception cref="ArgumentOutOfRangeException"><paramref name="bits"/> is not <c>16</c></exception>
		public void SetAudioParameters(int sampleRate, int channels, int bits)
		{
			if (bits != 16)
			{
				throw new ArgumentOutOfRangeException(nameof(bits), "Sampling depth must be 16 bits!");
			}

			this._sampleRate = sampleRate;
			this._channels = channels;
		}

		public string DesiredExtension()
		{
			// this needs to interface with the codec token
			return _token.Extension;
		}

		public void SetDefaultVideoCodecToken(Config config)
		{
			_token = FFmpegWriterForm.FormatPreset.GetDefaultPreset(config);
		}

		public bool UsesAudio => true;

		public bool UsesVideo => true;
	}
}
