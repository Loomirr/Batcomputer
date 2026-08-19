namespace Batcomputer;

/// <summary>
/// The Toybox workspace region: search/type toolbar, asset tile browser and selection line.
/// Hosts the runtime-built workspace rather than owning its controls, so the tile grid stays
/// in one place (VirtualTilePanel).
/// </summary>
public partial class ToyboxControl : UserControl
{
    public ToyboxControl()
    {
        InitializeComponent();
    }

    /// <summary>Docks the runtime-built Toybox workspace inside this shell.</summary>
    public void HostContent(Control content)
    {
        content.Dock = DockStyle.Fill;
        Controls.Add(content);
    }
}
