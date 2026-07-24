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
    }

    /// <summary>Clears the diagnostics surface.</summary>
    public void ClearLog() => _logText.Clear();

    /// <summary>The full log text (for Copy all / Save report actions).</summary>
    public string LogText => _logText.Text;
}
