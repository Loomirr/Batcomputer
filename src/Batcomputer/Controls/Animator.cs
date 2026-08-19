namespace Batcomputer;

/// <summary>
/// One shared, transient tween driver for the whole app. A single timer ticks only while at least
/// one tween is live and stops itself the instant the list empties, so the idle cost is exactly
/// zero - no per-control timers, no background loops. Each tween interpolates 0->1 over a short
/// duration and hands the eased value to a callback (which repaints its own small region).
///
/// The contract that keeps this cheap: animations are SHORT (~120-200ms) and repaint only the
/// control that changed. Do not use this for anything that loops forever or invalidates a large
/// surface every frame.
/// </summary>
internal static class Animator
{
    private sealed class Tween
    {
        public object Key = null!;          // owner+name, so a re-triggered tween replaces itself
        public long StartTicks;
        public double DurationMs;
        public double From;
        public double To;
        public Func<double, double> Ease = Easing.OutCubic;
        public Action<double> OnFrame = null!;
        public Action? OnDone;
    }

    private const int FrameIntervalMs = 16; // ~60fps
    private static readonly List<Tween> Active = new();
    private static readonly System.Windows.Forms.Timer Timer = new() { Interval = FrameIntervalMs };
    private static bool _wired;

    /// <summary>
    /// Master switch (Settings → General). When off, every tween resolves to its end value in one
    /// frame - the UI still lands in the right state, just without the motion.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Starts (or restarts) a tween keyed by <paramref name="owner"/> + <paramref name="name"/>.
    /// Re-triggering the same key - e.g. hovering a control that is still fading in - replaces the
    /// running tween from its current value, so there is never more than one per key and no snap.
    /// </summary>
    public static void Start(Control owner, string name, double from, double to, int durationMs,
        Action<double> onFrame, Func<double, double>? ease = null, Action? onDone = null)
    {
        if (!Enabled)
        {
            // Animations off: land on the final value at once, no tween, no timer.
            Cancel(owner, name);
            onFrame(to);
            onDone?.Invoke();
            return;
        }

        EnsureWired();

        var key = (owner, name);
        var existing = Active.FirstOrDefault(t => Equals(t.Key, key));
        var start = from;
        if (existing is not null)
        {
            // Continue from wherever the in-flight tween had reached, so a reversal is smooth.
            start = existing.From + (existing.To - existing.From) * existing.Ease(Progress(existing));
            Active.Remove(existing);
        }

        Active.Add(new Tween
        {
            Key = key,
            StartTicks = Environment.TickCount64,
            DurationMs = Math.Max(1, durationMs),
            From = start,
            To = to,
            Ease = ease ?? Easing.OutCubic,
            OnFrame = onFrame,
            OnDone = onDone,
        });

        // Drop any tween whose owner has gone away, so a disposed control can't be repainted.
        Active.RemoveAll(t => ((ValueTuple<Control, string>)t.Key).Item1.IsDisposed);

        if (!Timer.Enabled)
        {
            Timer.Start();
        }
    }

    /// <summary>Stops a tween without firing its final frame - used when a control is torn down.</summary>
    public static void Cancel(Control owner, string name)
    {
        Active.RemoveAll(t => Equals(t.Key, (owner, name)));
        if (Active.Count == 0)
        {
            Timer.Stop();
        }
    }

    private static double Progress(Tween t) =>
        Math.Clamp((Environment.TickCount64 - t.StartTicks) / t.DurationMs, 0, 1);

    private static void EnsureWired()
    {
        if (_wired)
        {
            return;
        }
        _wired = true;
        Timer.Tick += (_, _) =>
        {
            for (var i = Active.Count - 1; i >= 0; i--)
            {
                var t = Active[i];
                var owner = ((ValueTuple<Control, string>)t.Key).Item1;
                if (owner.IsDisposed)
                {
                    Active.RemoveAt(i);
                    continue;
                }

                var p = Progress(t);
                var value = t.From + (t.To - t.From) * t.Ease(p);
                try
                {
                    t.OnFrame(value);
                }
                catch
                {
                    // A frame callback that throws (a control mid-teardown) should not kill the timer
                    // for every other animation - just drop this tween.
                    Active.RemoveAt(i);
                    continue;
                }

                if (p >= 1)
                {
                    Active.RemoveAt(i);
                    t.OnDone?.Invoke();
                }
            }

            if (Active.Count == 0)
            {
                Timer.Stop();
            }
        };
    }
}

/// <summary>Easing curves. Ease-out cubic is the default: fast start, gentle settle - what makes a
/// short tween read as "smooth" rather than "linear/mechanical".</summary>
internal static class Easing
{
    public static double OutCubic(double t)
    {
        var u = 1 - t;
        return 1 - u * u * u;
    }

    public static double InOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    public static double Linear(double t) => t;
}
