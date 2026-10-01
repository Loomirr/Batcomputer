namespace Batcomputer;

/// <summary>Small PCM preview display. Shows decoded samples, never invented waveforms.</summary>
internal sealed class VoiceWaveformControl : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private float[] _peaks = [];
    private double _seconds;
    private string _label = "Audio preview";
    internal VoiceWaveformControl()
    {
        DoubleBuffered = true; BackColor = Theme.SlateDark; ForeColor = Theme.Materials;
        _timer.Tick += (_, _) => { if (_clock.Elapsed.TotalSeconds >= _seconds) { _timer.Stop(); _clock.Stop(); } Invalidate(); };
    }
    internal void Play(byte[] bytes, string label)
    {
        var format = CharacterVoiceLibraryService.InspectWav(bytes);
        _seconds = format.Seconds; _label = label; _peaks = Peaks(bytes);
        _clock.Restart(); _timer.Start(); Invalidate();
    }
    internal static float[] Peaks(byte[] bytes)
    {
        CharacterVoiceLibraryService.InspectWav(bytes);
        var peaks = new float[200];
        for (int at = 12; at + 8 <= bytes.Length;)
        {
            int size = BitConverter.ToInt32(bytes, at + 4);
            if (bytes.AsSpan(at, 4).SequenceEqual("data"u8))
            {
                int samples = size / 2;
                for (int i = 0; i < samples; i++)
                {
                    int bin = (int)((long)i * peaks.Length / Math.Max(1, samples));
                    peaks[bin] = Math.Max(peaks[bin], Math.Abs((int)BitConverter.ToInt16(bytes, at + 8 + 2 * i)) / 32768f);
                }
                break;
            }
            at += 8 + size + (size & 1);
        }
        return peaks;
    }
    internal void Stop() { _timer.Stop(); _clock.Reset(); if (!IsDisposed) Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var text = new Rectangle(6, 2, Math.Max(1, Width - 12), 22);
        TextRenderer.DrawText(e.Graphics, _label, Font, text, Theme.OnDarkMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        int center = Math.Max(25, (Height + 12) / 2), span = Math.Max(1, Height / 2 - 24);
        using var dim = new Pen(Theme.SlateLight); using var bright = new Pen(ForeColor);
        e.Graphics.DrawLine(dim, 6, center, Math.Max(6, Width - 6), center);
        for (int x = 6; x < Width - 6 && _peaks.Length > 0; x += 2)
        {
            var peak = _peaks[Math.Clamp((x - 6) * _peaks.Length / Math.Max(1, Width - 12), 0, _peaks.Length - 1)];
            float height = Math.Max(1, peak * span); e.Graphics.DrawLine(bright, x, center - height, x, center + height);
        }
        if (_seconds > 0)
        {
            double time = Math.Min(_seconds, _clock.Elapsed.TotalSeconds);
            using var cursor = new Pen(Theme.Gold, 2);
            float pos = 6 + (float)(time / _seconds * Math.Max(1, Width - 12)); e.Graphics.DrawLine(cursor, pos, 26, pos, Math.Max(26, Height - 20));
            TextRenderer.DrawText(e.Graphics, $"{time:0.00} / {_seconds:0.00}s", Font, new Rectangle(6, Height - 20, Width - 12, 20), Theme.OnDarkMuted);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
}
