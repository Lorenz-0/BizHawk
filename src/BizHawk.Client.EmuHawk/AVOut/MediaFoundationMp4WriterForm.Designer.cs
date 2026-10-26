namespace BizHawk.Client.EmuHawk
{
	partial class MediaFoundationMp4WriterForm
	{
		/// <summary>
		/// Required designer variable.
		/// </summary>
		private System.ComponentModel.IContainer components = null;

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
		protected override void Dispose(bool disposing)
		{
			if (disposing && (components != null))
			{
				components.Dispose();
			}
			base.Dispose(disposing);
		}

		#region Windows Form Designer generated code

		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
		{
			this.buttonOK = new System.Windows.Forms.Button();
			this.buttonCancel = new System.Windows.Forms.Button();
			this.labelVideoBitrate = new BizHawk.WinForms.Controls.LocLabelEx();
			this.numericVideoBitrate = new System.Windows.Forms.NumericUpDown();
			this.labelVideoRange = new BizHawk.WinForms.Controls.LocLabelEx();
			this.labelAudioBitrate = new BizHawk.WinForms.Controls.LocLabelEx();
			this.numericAudioBitrate = new System.Windows.Forms.NumericUpDown();
			this.labelAudioRange = new BizHawk.WinForms.Controls.LocLabelEx();
			this.labelNote = new BizHawk.WinForms.Controls.LocLabelEx();
			((System.ComponentModel.ISupportInitialize)(this.numericVideoBitrate)).BeginInit();
			((System.ComponentModel.ISupportInitialize)(this.numericAudioBitrate)).BeginInit();
			this.SuspendLayout();
			//
			// buttonOK
			//
			this.buttonOK.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
			this.buttonOK.DialogResult = System.Windows.Forms.DialogResult.OK;
			this.buttonOK.Location = new System.Drawing.Point(178, 132);
			this.buttonOK.Name = "buttonOK";
			this.buttonOK.Size = new System.Drawing.Size(75, 23);
			this.buttonOK.TabIndex = 2;
			this.buttonOK.Text = "OK";
			this.buttonOK.UseVisualStyleBackColor = true;
			this.buttonOK.Click += new System.EventHandler(this.ButtonOK_Click);
			//
			// buttonCancel
			//
			this.buttonCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
			this.buttonCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
			this.buttonCancel.Location = new System.Drawing.Point(259, 132);
			this.buttonCancel.Name = "buttonCancel";
			this.buttonCancel.Size = new System.Drawing.Size(75, 23);
			this.buttonCancel.TabIndex = 3;
			this.buttonCancel.Text = "Cancel";
			this.buttonCancel.UseVisualStyleBackColor = true;
			//
			// labelVideoBitrate
			//
			this.labelVideoBitrate.Location = new System.Drawing.Point(12, 9);
			this.labelVideoBitrate.Name = "labelVideoBitrate";
			this.labelVideoBitrate.Text = "Video bitrate (kbps):";
			//
			// numericVideoBitrate
			//
			this.numericVideoBitrate.Increment = new decimal(new int[] {
			100,
			0,
			0,
			0});
			this.numericVideoBitrate.Location = new System.Drawing.Point(12, 25);
			this.numericVideoBitrate.Maximum = new decimal(new int[] {
			50000,
			0,
			0,
			0});
			this.numericVideoBitrate.Minimum = new decimal(new int[] {
			100,
			0,
			0,
			0});
			this.numericVideoBitrate.Name = "numericVideoBitrate";
			this.numericVideoBitrate.Size = new System.Drawing.Size(120, 20);
			this.numericVideoBitrate.TabIndex = 0;
			this.numericVideoBitrate.ThousandsSeparator = true;
			this.numericVideoBitrate.Value = new decimal(new int[] {
			8000,
			0,
			0,
			0});
			//
			// labelVideoRange
			//
			this.labelVideoRange.Location = new System.Drawing.Point(138, 27);
			this.labelVideoRange.Name = "labelVideoRange";
			this.labelVideoRange.Text = "100-50000 kbps";
			//
			// labelAudioBitrate
			//
			this.labelAudioBitrate.Location = new System.Drawing.Point(12, 52);
			this.labelAudioBitrate.Name = "labelAudioBitrate";
			this.labelAudioBitrate.Text = "Audio bitrate (kbps):";
			//
			// numericAudioBitrate
			//
			this.numericAudioBitrate.Increment = new decimal(new int[] {
			32,
			0,
			0,
			0});
			this.numericAudioBitrate.Location = new System.Drawing.Point(12, 68);
			this.numericAudioBitrate.Maximum = new decimal(new int[] {
			192,
			0,
			0,
			0});
			this.numericAudioBitrate.Minimum = new decimal(new int[] {
			96,
			0,
			0,
			0});
			this.numericAudioBitrate.Name = "numericAudioBitrate";
			this.numericAudioBitrate.Size = new System.Drawing.Size(120, 20);
			this.numericAudioBitrate.TabIndex = 1;
			this.numericAudioBitrate.Value = new decimal(new int[] {
			192,
			0,
			0,
			0});
			//
			// labelAudioRange
			//
			this.labelAudioRange.Location = new System.Drawing.Point(138, 70);
			this.labelAudioRange.Name = "labelAudioRange";
			this.labelAudioRange.Text = "96, 128, 160, 192 kbps";
			//
			// labelNote
			//
			this.labelNote.Location = new System.Drawing.Point(12, 98);
			this.labelNote.Name = "labelNote";
			this.labelNote.Text = "Higher bitrates give better quality and larger files.";
			//
			// MediaFoundationMp4WriterForm
			//
			this.AcceptButton = this.buttonOK;
			this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
			this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			this.CancelButton = this.buttonCancel;
			this.ClientSize = new System.Drawing.Size(346, 167);
			this.Controls.Add(this.labelNote);
			this.Controls.Add(this.labelAudioRange);
			this.Controls.Add(this.numericAudioBitrate);
			this.Controls.Add(this.labelAudioBitrate);
			this.Controls.Add(this.labelVideoRange);
			this.Controls.Add(this.numericVideoBitrate);
			this.Controls.Add(this.labelVideoBitrate);
			this.Controls.Add(this.buttonCancel);
			this.Controls.Add(this.buttonOK);
			this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
			this.MaximizeBox = false;
			this.MinimizeBox = false;
			this.Name = "MediaFoundationMp4WriterForm";
			this.ShowIcon = false;
			this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
			this.Text = "Native MP4 Writer Options";
			((System.ComponentModel.ISupportInitialize)(this.numericVideoBitrate)).EndInit();
			((System.ComponentModel.ISupportInitialize)(this.numericAudioBitrate)).EndInit();
			this.ResumeLayout(false);
			this.PerformLayout();

		}

		#endregion

		private System.Windows.Forms.Button buttonOK;
		private System.Windows.Forms.Button buttonCancel;
		private BizHawk.WinForms.Controls.LocLabelEx labelVideoBitrate;
		private System.Windows.Forms.NumericUpDown numericVideoBitrate;
		private BizHawk.WinForms.Controls.LocLabelEx labelVideoRange;
		private BizHawk.WinForms.Controls.LocLabelEx labelAudioBitrate;
		private System.Windows.Forms.NumericUpDown numericAudioBitrate;
		private BizHawk.WinForms.Controls.LocLabelEx labelAudioRange;
		private BizHawk.WinForms.Controls.LocLabelEx labelNote;
	}
}
