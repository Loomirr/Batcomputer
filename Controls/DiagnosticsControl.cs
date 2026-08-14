using System.Text;

namespace Batcomputer;

/// <summary>
/// The first designer-editable, self-contained view extracted from the
/// monolithic MainForm. Owns the diagnostics/log surface only - pure presentation, no project
/// mutation. MainForm keeps its <c>AppendLog</c> entry point and delegates here, so every
/// existing caller is behavior-identical (the migration adapter pattern from the plan §14/§17).
/// </summary>
public partial class DiagnosticsControl : UserControl
{
    public DiagnosticsControl()
    {
        InitializeComponent();
        // Theme colors are runtime tokens, applied here rather than in the Designer so the
        // static visual composition stays designer-editable.
        _logText.BackColor = Theme.SlateDark;
        _logText.ForeColor = Theme.Materials;
        _logText.Font = Theme.Mono; // redesign: monospaced log so timestamps/values align
        _logText.BorderStyle = BorderStyle.None;
        _logText.WordWrap = true;
        _logText.ScrollBars = ScrollBars.Vertical;
    }

    /// <summary>
    /// Appends a timestamped message. One "[HH:mm:ss] line" per newline-separated segment,
    /// blank lines skipped - identical to the original MainForm.AppendLog formatting.
    /// </summary>
    public void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            builder.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(line);
        }
        _logText.AppendText(builder.ToString());
        _logText.SelectionStart = _logText.TextLength;
        _logText.ScrollToCaret();
    }

    /// <summary>Clears the diagnostics surface.</summary>
    public void ClearLog() => _logText.Clear();

    /// <summary>The full log text (for Copy all / Save report actions).</summary>
    public string LogText => _logText.Text;

    /// <summary>Copies every currently retained diagnostics line to the Windows clipboard.</summary>
    public bool TryCopyLogToClipboard()
    {
        if (string.IsNullOrWhiteSpace(_logText.Text))
        {
            return false;
        }

        try
        {
            Clipboard.SetText(_logText.Text);
            return true;
        }
        catch (Exception)
        {
            // Clipboard can be temporarily held by another Windows app. Keep
            // the failure local to the caller instead of disrupting a build.
            return false;
        }
    }
}
