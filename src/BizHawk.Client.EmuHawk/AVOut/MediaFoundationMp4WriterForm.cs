using System.Windows.Forms;
using BizHawk.Client.Common;

namespace BizHawk.Client.EmuHawk
{
	/// <summary>
	/// Settings dialog for <see cref="MediaFoundationMp4Writer"/>: independently editable
	/// H.264 video and AAC audio bitrates, both shown in kbps and persisted to <see cref="Config"/>.
	/// </summary>
	public partial class MediaFoundationMp4WriterForm : Form
	{
		private MediaFoundationMp4WriterForm() => InitializeComponent();

		/// <summary>
		/// Show a configuration dialog (modal) for <see cref="MediaFoundationMp4Writer"/>,
		/// initialized from <paramref name="config"/>'s persisted bitrates. On OK the chosen
		/// values are written back to <paramref name="config"/> and returned as a codec token.
		/// </summary>
		/// <returns>the chosen codec token, or null if the user canceled</returns>
		public static MediaFoundationMp4Writer.CodecToken DoTokenForm(IWin32Window parent, Config config)
		{
			// LoadFromConfig sanitizes stale/hand-edited persisted values, so the numeric
			// controls are always initialized with supported values.
			var initial = MediaFoundationMp4Writer.CodecToken.LoadFromConfig(config);
			var supportedAudio = MediaFoundationMp4Writer.SupportedAudioBitratesKbps;

			using var dlg = new MediaFoundationMp4WriterForm();
			dlg.numericVideoBitrate.Minimum = MediaFoundationMp4Writer.MinVideoBitrateKbps;
			dlg.numericVideoBitrate.Maximum = MediaFoundationMp4Writer.MaxVideoBitrateKbps;
			dlg.numericVideoBitrate.Value = initial.VideoBitrateKbps;
			dlg.labelVideoRange.Text = $"{MediaFoundationMp4Writer.MinVideoBitrateKbps}-{MediaFoundationMp4Writer.MaxVideoBitrateKbps} kbps";
			dlg.numericAudioBitrate.Minimum = supportedAudio[0];
			dlg.numericAudioBitrate.Maximum = supportedAudio[supportedAudio.Count - 1];
			dlg.numericAudioBitrate.Value = initial.AudioBitrateKbps;
			dlg.labelAudioRange.Text = $"{string.Join(", ", supportedAudio)} kbps";

			if (dlg.ShowDialog(parent).IsOk())
			{
				config.MfMp4VideoBitrateKbps = (int) dlg.numericVideoBitrate.Value;
				config.MfMp4AudioBitrateKbps = (int) dlg.numericAudioBitrate.Value;

				return MediaFoundationMp4Writer.CodecToken.LoadFromConfig(config);
			}

			return null;
		}

		private void ButtonOK_Click(object sender, EventArgs e)
		{
			// The NumericUpDown ranges already keep typed/spun values in bounds, but validate
			// explicitly anyway (independently for each bitrate) so unsupported values -- in
			// particular audio bitrates the Media Foundation AAC encoder cannot use -- are
			// rejected with an explanation instead of failing when recording starts.
			var videoBitrateKbps = (int) numericVideoBitrate.Value;
			if (!MediaFoundationMp4Writer.IsSupportedVideoBitrateKbps(videoBitrateKbps))
			{
				MessageBox.Show(
					this,
					$"Video bitrate must be between {MediaFoundationMp4Writer.MinVideoBitrateKbps} and {MediaFoundationMp4Writer.MaxVideoBitrateKbps} kbps.",
					Text,
					MessageBoxButtons.OK,
					MessageBoxIcon.Warning);
				DialogResult = DialogResult.None;
				return;
			}

			var audioBitrateKbps = (int) numericAudioBitrate.Value;
			if (!MediaFoundationMp4Writer.IsSupportedAudioBitrateKbps(audioBitrateKbps))
			{
				MessageBox.Show(
					this,
					$"Audio bitrate must be one of {string.Join(", ", MediaFoundationMp4Writer.SupportedAudioBitratesKbps)} kbps; those are the only values supported by the Media Foundation AAC encoder for mono/stereo audio.",
					Text,
					MessageBoxButtons.OK,
					MessageBoxIcon.Warning);
				DialogResult = DialogResult.None;
			}
		}
	}
}
