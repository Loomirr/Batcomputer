using System.ComponentModel;

#nullable enable

namespace Batcomputer;

partial class MainForm
{
    private IContainer? components = null;
    private TableLayoutPanel _mainRootLayout = null!;
    private Panel _mainWorkspaceHost = null!;
    private Panel _mainLogGroupBox = null!;
    private TableLayoutPanel _designerWorkspacePreview = null!;
    private Label _designerTitleLabel = null!;
    private Label _designerSubtitleLabel = null!;
    private Label _designerWorkspaceLabel = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            DisposeTextureThumbnailCache();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new Container();
        _mainRootLayout = new TableLayoutPanel();
        _mainWorkspaceHost = new Panel();
        _mainLogGroupBox = new Panel();
        _designerWorkspacePreview = new TableLayoutPanel();
        _designerTitleLabel = new Label();
        _designerSubtitleLabel = new Label();
        _designerWorkspaceLabel = new Label();

        SuspendLayout();

        // _mainRootLayout
        _mainRootLayout.BackColor = Color.FromArgb(26, 29, 34);
        _mainRootLayout.ColumnCount = 1;
        _mainRootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _mainRootLayout.Controls.Add(_mainWorkspaceHost, 0, 0);
        _mainRootLayout.Controls.Add(_mainLogGroupBox, 0, 1);
        _mainRootLayout.Dock = DockStyle.Fill;
        _mainRootLayout.Location = new Point(0, 0);
        _mainRootLayout.Margin = new Padding(0);
        _mainRootLayout.Name = "_mainRootLayout";
        _mainRootLayout.Padding = new Padding(0);
        _mainRootLayout.RowCount = 2;
        _mainRootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _mainRootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180F));
        _mainRootLayout.Size = new Size(1280, 800);
        _mainRootLayout.TabIndex = 0;

        // _mainWorkspaceHost
        _mainWorkspaceHost.BackColor = Color.FromArgb(26, 29, 34);
        _mainWorkspaceHost.Controls.Add(_designerWorkspacePreview);
        _mainWorkspaceHost.Dock = DockStyle.Fill;
        _mainWorkspaceHost.Location = new Point(0, 0);
        _mainWorkspaceHost.Margin = new Padding(0);
        _mainWorkspaceHost.Name = "_mainWorkspaceHost";
        _mainWorkspaceHost.Padding = new Padding(6);
        _mainWorkspaceHost.Size = new Size(1280, 620);
        _mainWorkspaceHost.TabIndex = 0;

        // _designerWorkspacePreview
        _designerWorkspacePreview.BackColor = Color.FromArgb(43, 47, 54);
        _designerWorkspacePreview.ColumnCount = 1;
        _designerWorkspacePreview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _designerWorkspacePreview.Controls.Add(_designerTitleLabel, 0, 0);
        _designerWorkspacePreview.Controls.Add(_designerSubtitleLabel, 0, 1);
        _designerWorkspacePreview.Controls.Add(_designerWorkspaceLabel, 0, 2);
        _designerWorkspacePreview.Dock = DockStyle.Fill;
        _designerWorkspacePreview.Location = new Point(6, 6);
        _designerWorkspacePreview.Name = "_designerWorkspacePreview";
        _designerWorkspacePreview.Padding = new Padding(18);
        _designerWorkspacePreview.RowCount = 3;
        _designerWorkspacePreview.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        _designerWorkspacePreview.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        _designerWorkspacePreview.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _designerWorkspacePreview.Size = new Size(1268, 608);
        _designerWorkspacePreview.TabIndex = 0;

        // _designerTitleLabel
        _designerTitleLabel.AutoSize = true;
        _designerTitleLabel.Dock = DockStyle.Fill;
        _designerTitleLabel.Font = AppFonts.Condensed(16F, FontStyle.Bold);
        _designerTitleLabel.ForeColor = Color.FromArgb(240, 194, 48);
        _designerTitleLabel.Location = new Point(21, 18);
        _designerTitleLabel.Name = "_designerTitleLabel";
        _designerTitleLabel.Size = new Size(1226, 38);
        _designerTitleLabel.TabIndex = 0;
        _designerTitleLabel.Text = "Batcomputer";
        _designerTitleLabel.TextAlign = ContentAlignment.MiddleLeft;

        // _designerSubtitleLabel
        _designerSubtitleLabel.AutoSize = true;
        _designerSubtitleLabel.Dock = DockStyle.Fill;
        _designerSubtitleLabel.ForeColor = Color.FromArgb(158, 166, 178);
        _designerSubtitleLabel.Location = new Point(21, 56);
        _designerSubtitleLabel.Name = "_designerSubtitleLabel";
        _designerSubtitleLabel.Size = new Size(1226, 28);
        _designerSubtitleLabel.TabIndex = 1;
        _designerSubtitleLabel.Text = "Designer shell only. Runtime inserts the Toybox builder, Advanced fallback, and log textbox into these host panels.";
        _designerSubtitleLabel.TextAlign = ContentAlignment.MiddleLeft;

        // _designerWorkspaceLabel
        _designerWorkspaceLabel.BorderStyle = BorderStyle.FixedSingle;
        _designerWorkspaceLabel.Dock = DockStyle.Fill;
        _designerWorkspaceLabel.Font = AppFonts.Condensed(10F, FontStyle.Bold);
        _designerWorkspaceLabel.ForeColor = Color.FromArgb(236, 238, 242);
        _designerWorkspaceLabel.Location = new Point(21, 84);
        _designerWorkspaceLabel.Name = "_designerWorkspaceLabel";
        _designerWorkspaceLabel.Padding = new Padding(24);
        _designerWorkspaceLabel.Size = new Size(1226, 503);
        _designerWorkspaceLabel.TabIndex = 2;
        _designerWorkspaceLabel.Text = "Runtime workspace host\r\n\r\nEdit form size, colors, docking, row heights, and placeholder shell here. Runtime-only dynamic controls remain in MainForm.cs so package/build logic is untouched.";
        _designerWorkspaceLabel.TextAlign = ContentAlignment.MiddleCenter;

        // _mainLogGroupBox
        _mainLogGroupBox.BackColor = Color.FromArgb(26, 29, 34);
        _mainLogGroupBox.Dock = DockStyle.Fill;
        _mainLogGroupBox.ForeColor = Color.FromArgb(158, 166, 178);
        _mainLogGroupBox.Location = new Point(3, 623);
        _mainLogGroupBox.Name = "_mainLogGroupBox";
        _mainLogGroupBox.Padding = Padding.Empty;
        _mainLogGroupBox.Size = new Size(1274, 174);
        _mainLogGroupBox.TabIndex = 1;

        // MainForm
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(26, 29, 34);
        ClientSize = new Size(1280, 800);
        Controls.Add(_mainRootLayout);
        ForeColor = Color.FromArgb(236, 238, 242);
        MinimumSize = new Size(960, 640);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Batcomputer — Suit Builder";

        _mainRootLayout.ResumeLayout(false);
        _mainWorkspaceHost.ResumeLayout(false);
        _designerWorkspacePreview.ResumeLayout(false);
        _designerWorkspacePreview.PerformLayout();
        _mainLogGroupBox.ResumeLayout(false);
        ResumeLayout(false);
    }
}
