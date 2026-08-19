namespace Batcomputer;

/// <summary>
/// The top command-bar region (suit identity, save state, package
/// name, Package/Install/Setup/New/Open). For now it HOSTS the existing runtime-built header
/// unchanged (the migration "host unchanged" step, plan §17) - a named, designer-editable frame
/// that shrinks MainForm's composition without touching any handler or shared field. Content is
/// authored into the designer in a later phase, once the controller/session boundary exists.
/// </summary>
public partial class CommandBarControl : UserControl
{
    public CommandBarControl()
    {
        InitializeComponent();
    }

    /// <summary>Docks the runtime-built command-bar content inside this shell.</summary>
    public void HostContent(Control content)
    {
        content.Dock = DockStyle.Fill;
        Controls.Add(content);
    }
}
