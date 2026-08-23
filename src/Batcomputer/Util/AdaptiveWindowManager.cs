namespace Batcomputer;

/// <summary>
/// Applies one resize policy to every app-owned WinForms window, including the small forms built
/// inline by individual workspace tabs. Fixed-layout dialogs retain their original canvas as a
/// scrollable fallback, while already-responsive windows keep their normal dock/anchor behavior.
/// </summary>
internal static class AdaptiveWindowManager
{
    internal readonly record struct WindowFit(Rectangle Bounds, Size MinimumSize);

    private sealed record PreparedWindow(FormBorderStyle OriginalBorderStyle);

    private sealed record WindowProfile(
        Size PreferredMinimumLogical,
        Size OriginalClientLogical,
        bool NeedsScrollFallback,
        bool OriginalAutoScroll,
        Size OriginalAutoScrollMinimumLogical);

    private static readonly Dictionary<Form, PreparedWindow> PreparedWindows = new();
    private static readonly Dictionary<Form, WindowProfile> Profiles = new();
    private static readonly HashSet<Form> UpdatingScrollFallback = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        Application.Idle += (_, _) => ConfigureOpenWindows();
    }

    private static void ConfigureOpenWindows()
    {
        foreach (var form in Application.OpenForms.Cast<Form>().ToArray())
        {
            Configure(form);
        }
    }

    internal static void Prepare(Form form)
    {
        if (form.IsDisposed || PreparedWindows.ContainsKey(form) || Profiles.ContainsKey(form) ||
            !IsApplicationWindow(form))
        {
            return;
        }

        var originalBorder = form.FormBorderStyle;
        PreparedWindows.Add(form, new PreparedWindow(originalBorder));
        form.Disposed += (_, _) =>
        {
            PreparedWindows.Remove(form);
            Profiles.Remove(form);
            UpdatingScrollFallback.Remove(form);
        };

        form.FormBorderStyle = ResizableBorderStyleForTest(originalBorder);
        if (IsResizableBorderForTest(form.FormBorderStyle))
        {
            form.SizeGripStyle = SizeGripStyle.Show;
            if (form.ControlBox)
            {
                form.MaximizeBox = true;
            }
        }
    }

    internal static void Configure(Form form)
    {
        if (form.IsDisposed || Profiles.ContainsKey(form) || !IsApplicationWindow(form))
        {
            return;
        }

        Prepare(form);
        if (!PreparedWindows.TryGetValue(form, out var prepared))
        {
            return;
        }

        var dpi = Math.Max(96, form.DeviceDpi);
        var originalClientSize = form.ClientSize;
        var needsScrollFallback =
            !IsResizableBorderForTest(prepared.OriginalBorderStyle) || form.MinimumSize.IsEmpty;
        var preferredMinimum = needsScrollFallback || form.MinimumSize.IsEmpty
            ? CompactMinimumFor(form)
            : form.MinimumSize;
        var profile = new WindowProfile(
            ToLogical(preferredMinimum, dpi),
            ToLogical(originalClientSize, dpi),
            needsScrollFallback,
            form.AutoScroll,
            form.AutoScrollMinSize.IsEmpty ? Size.Empty : ToLogical(form.AutoScrollMinSize, dpi));
        Profiles.Add(form, profile);

        FitToWorkingArea(form, profile);
        form.ClientSizeChanged += (_, _) => UpdateScrollFallback(form, profile);
        form.DpiChanged += (_, _) => QueueFitToWorkingArea(form, profile);
    }

    private static bool IsApplicationWindow(Form form)
    {
        var type = form.GetType();
        return type == typeof(Form) || type.Assembly == typeof(MainForm).Assembly;
    }

    private static Size CompactMinimumFor(Form form)
    {
        var dpi = Math.Max(96, form.DeviceDpi);
        var fallback = new Size(
            Math.Max(1, 360 * dpi / 96),
            Math.Max(1, 240 * dpi / 96));
        return new Size(
            Math.Max(1, Math.Min(form.Width, fallback.Width)),
            Math.Max(1, Math.Min(form.Height, fallback.Height)));
    }

    private static void QueueFitToWorkingArea(Form form, WindowProfile profile)
    {
        if (form.IsDisposed || !form.IsHandleCreated)
        {
            return;
        }

        try
        {
            form.BeginInvoke(() => FitToWorkingArea(form, profile));
        }
        catch (InvalidOperationException)
        {
            // The form closed between the DPI event and this queued layout pass.
        }
    }

    private static void FitToWorkingArea(Form form, WindowProfile profile)
    {
        if (form.IsDisposed)
        {
            return;
        }

        var dpi = Math.Max(96, form.DeviceDpi);
        var screen = form.Owner is { IsDisposed: false } owner
            ? Screen.FromControl(owner)
            : form.IsHandleCreated
                ? Screen.FromControl(form)
                : Screen.FromRectangle(form.WindowState == FormWindowState.Normal
                    ? form.Bounds
                    : form.RestoreBounds);
        var edgeGap = Math.Max(8, 12 * dpi / 96);
        var requestedMinimum = FromLogical(profile.PreferredMinimumLogical, dpi);
        var fit = ConstrainWindowBoundsForTest(
            form.Bounds,
            screen.WorkingArea,
            requestedMinimum,
            edgeGap);

        form.MinimumSize = fit.MinimumSize;
        if (form.WindowState == FormWindowState.Normal)
        {
            form.Bounds = fit.Bounds;
        }
        UpdateScrollFallback(form, profile);
    }

    private static void UpdateScrollFallback(Form form, WindowProfile profile)
    {
        if (form.IsDisposed || !UpdatingScrollFallback.Add(form))
        {
            return;
        }

        try
        {
            var dpi = Math.Max(96, form.DeviceDpi);
            var canvas = FromLogical(profile.OriginalClientLogical, dpi);
            var requestedMinimum = FromLogical(profile.PreferredMinimumLogical, dpi);
            var minimumWasScreenClamped =
                form.MinimumSize.Width < requestedMinimum.Width ||
                form.MinimumSize.Height < requestedMinimum.Height;
            var compact = form.ClientSize.Width < canvas.Width || form.ClientSize.Height < canvas.Height;
            var useFallbackCanvas = compact && (profile.NeedsScrollFallback || minimumWasScreenClamped);
            var originalMinimum = profile.OriginalAutoScrollMinimumLogical.IsEmpty
                ? Size.Empty
                : FromLogical(profile.OriginalAutoScrollMinimumLogical, dpi);
            var desiredMinimum = useFallbackCanvas
                ? new Size(
                    Math.Max(originalMinimum.Width, canvas.Width),
                    Math.Max(originalMinimum.Height, canvas.Height))
                : originalMinimum;
            var desiredAutoScroll = profile.OriginalAutoScroll || useFallbackCanvas;

            if (form.AutoScrollMinSize != desiredMinimum)
            {
                form.AutoScrollMinSize = desiredMinimum;
            }
            if (form.AutoScroll != desiredAutoScroll)
            {
                form.AutoScroll = desiredAutoScroll;
            }
        }
        finally
        {
            UpdatingScrollFallback.Remove(form);
        }
    }

    private static Size ToLogical(Size physical, int dpi) => new(
        Math.Max(1, (int)Math.Ceiling(physical.Width * 96d / Math.Max(96, dpi))),
        Math.Max(1, (int)Math.Ceiling(physical.Height * 96d / Math.Max(96, dpi))));

    private static Size FromLogical(Size logical, int dpi) => new(
        Math.Max(1, (int)Math.Round(logical.Width * Math.Max(96, dpi) / 96d)),
        Math.Max(1, (int)Math.Round(logical.Height * Math.Max(96, dpi) / 96d)));

    internal static FormBorderStyle ResizableBorderStyleForTest(FormBorderStyle borderStyle) => borderStyle switch
    {
        FormBorderStyle.FixedToolWindow => FormBorderStyle.SizableToolWindow,
        FormBorderStyle.Fixed3D or
        FormBorderStyle.FixedDialog or
        FormBorderStyle.FixedSingle => FormBorderStyle.Sizable,
        _ => borderStyle,
    };

    internal static bool IsResizableBorderForTest(FormBorderStyle borderStyle) =>
        borderStyle is FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow;

    internal static WindowFit ConstrainWindowBoundsForTest(
        Rectangle currentBounds,
        Rectangle workingArea,
        Size requestedMinimum,
        int edgeGap)
    {
        edgeGap = Math.Max(0, edgeGap);
        var horizontalGap = Math.Min(edgeGap, Math.Max(0, (workingArea.Width - 1) / 2));
        var verticalGap = Math.Min(edgeGap, Math.Max(0, (workingArea.Height - 1) / 2));
        var usable = new Rectangle(
            workingArea.Left + horizontalGap,
            workingArea.Top + verticalGap,
            Math.Max(1, workingArea.Width - horizontalGap * 2),
            Math.Max(1, workingArea.Height - verticalGap * 2));
        var minimum = new Size(
            Math.Clamp(Math.Max(1, requestedMinimum.Width), 1, usable.Width),
            Math.Clamp(Math.Max(1, requestedMinimum.Height), 1, usable.Height));
        var width = Math.Clamp(Math.Max(1, currentBounds.Width), minimum.Width, usable.Width);
        var height = Math.Clamp(Math.Max(1, currentBounds.Height), minimum.Height, usable.Height);
        var left = Math.Clamp(currentBounds.Left, usable.Left, usable.Right - width);
        var top = Math.Clamp(currentBounds.Top, usable.Top, usable.Bottom - height);
        return new WindowFit(new Rectangle(left, top, width, height), minimum);
    }
}
