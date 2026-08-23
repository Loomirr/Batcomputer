using System.ComponentModel;

namespace Batcomputer;

/// <summary>
/// Base window that applies the app-wide resize and display-fit policy before its first paint.
/// Configuring at this boundary avoids recreating a visible native window when a fixed border is
/// upgraded to a sizable one.
/// </summary>
public abstract class AdaptiveForm : Form
{
    protected override void SetVisibleCore(bool value)
    {
        if (value && LicenseManager.UsageMode != LicenseUsageMode.Designtime)
        {
            AdaptiveWindowManager.Prepare(this);
        }

        base.SetVisibleCore(value);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (LicenseManager.UsageMode != LicenseUsageMode.Designtime)
        {
            // The handle now has its final monitor DPI and WinForms has applied AutoScale.
            AdaptiveWindowManager.Configure(this);
        }
    }
}

/// <summary>Concrete adaptive shell for small forms composed inline by workspace commands.</summary>
internal sealed class AdaptiveDialogForm : AdaptiveForm;
