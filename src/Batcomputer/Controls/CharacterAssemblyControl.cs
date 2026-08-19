namespace Batcomputer;

/// <summary>
/// The main character-assembly workspace (Your Character + Toybox +
/// Inspector). Per plan §17 it HOSTS the existing runtime Toybox workspace unchanged, so all the
/// current drag/drop, tile, and inspector behavior is preserved verbatim. The workspace internals
/// are decomposed in later phases (virtualized Toybox, typed drag/drop) after the controller/
/// session boundary is in place.
/// </summary>
public partial class CharacterAssemblyControl : UserControl
{
    public CharacterAssemblyControl()
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
