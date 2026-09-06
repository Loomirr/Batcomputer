using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace Batcomputer;

/// <summary>
/// The first designer-editable, self-contained view extracted from the
/// monolithic MainForm. Owns the diagnostics/log surface only - pure presentation, no project
/// mutation. MainForm keeps its <c>AppendLog</c> entry point and delegates here, so every
/// existing caller is behavior-identical (the migration adapter pattern from the plan §14/§17).
/// </summary>
public partial class DiagnosticsControl : UserControl
{
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmLineScroll = 0x00B6;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private int _logDrainPosted;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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
        Disposed += (_, _) => _pendingLog.Clear();
    }

    /// <summary>
    /// Appends a timestamped message. One "[HH:mm:ss] line" per newline-separated segment,
    /// blank lines skipped. Worker callbacks are queued to the owning UI thread; they never
    /// read the TextBox or synchronously wait for the UI (which may be waiting for that worker).
    /// </summary>
    public void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || IsDisposed || Disposing)
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
        _pendingLog.Enqueue(builder.ToString());
        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            DrainLogOnUiThread();
        }
        else
        {
            PostLogDrain();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PostLogDrain();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Interlocked.Exchange(ref _logDrainPosted, 0);
        base.OnHandleDestroyed(e);
    }

    private void PostLogDrain()
    {
        // InvokeRequired alone returns false before a handle exists. Keep early worker messages
        // queued instead of accidentally creating the control's first handle on a worker thread.
        if (IsDisposed || Disposing || !IsHandleCreated || _pendingLog.IsEmpty ||
            Interlocked.CompareExchange(ref _logDrainPosted, 1, 0) != 0) return;
        try { BeginInvoke((Action)DrainLogOnUiThread); }
        catch (InvalidOperationException)
        {
            // The handle/message loop can disappear between the checks and BeginInvoke during
            // shutdown or recreation. A recreated handle schedules the retained queue again.
            Interlocked.Exchange(ref _logDrainPosted, 0);
        }
    }

    private void DrainLogOnUiThread()
    {
        Interlocked.Exchange(ref _logDrainPosted, 0);
        if (IsDisposed || Disposing || _logText.IsDisposed)
        {
            _pendingLog.Clear();
            return;
        }
        var builder = new StringBuilder();
        while (_pendingLog.TryDequeue(out var text)) builder.Append(text);
        if (builder.Length == 0) return;

        var selectionStart = _logText.SelectionStart;
        var selectionLength = _logText.SelectionLength;
        var firstVisibleLine = FirstVisibleLine();
        var wasFollowingTail = IsFollowingTail(firstVisibleLine);

        _logText.AppendText(builder.ToString());
        if (wasFollowingTail)
        {
            _logText.SelectionStart = _logText.TextLength;
            _logText.SelectionLength = 0;
            _logText.ScrollToCaret();
        }
        else
        {
            // Appending to a TextBox moves its caret and viewport to the end. Put both back when
            // the user has scrolled upward so live diagnostics do not fight older-log reading.
            _logText.SelectionStart = Math.Min(selectionStart, _logText.TextLength);
            _logText.SelectionLength = Math.Min(selectionLength, _logText.TextLength - _logText.SelectionStart);
            var currentFirstVisibleLine = FirstVisibleLine();
            SendMessage(
                _logText.Handle,
                EmLineScroll,
                IntPtr.Zero,
                new IntPtr(firstVisibleLine - currentFirstVisibleLine));
        }
    }

    private int FirstVisibleLine() =>
        _logText.IsHandleCreated
            ? SendMessage(_logText.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32()
            : 0;

    private bool IsFollowingTail(int firstVisibleLine)
    {
        if (_logText.TextLength == 0)
        {
            return true;
        }

        var lastVisibleCharacter = _logText.GetCharIndexFromPosition(
            new Point(Math.Max(0, _logText.ClientSize.Width - 2), Math.Max(0, _logText.ClientSize.Height - 2)));
        var lastVisibleLine = _logText.GetLineFromCharIndex(lastVisibleCharacter);
        var lastTextLine = _logText.GetLineFromCharIndex(Math.Max(0, _logText.TextLength - 1));
        return firstVisibleLine == 0 && lastTextLine == 0 || lastVisibleLine >= lastTextLine - 1;
    }

    /// <summary>Clears the diagnostics surface.</summary>
    public void ClearLog()
    {
        _pendingLog.Clear();
        _logText.Clear();
    }

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
