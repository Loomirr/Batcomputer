namespace Batcomputer;

/// <summary>
/// The workflow/category rail region. Hosts the existing runtime-built
/// category rail unchanged for now. Becomes designer-authored once the
/// controller/session boundary lets navigation state move out of MainForm.
/// </summary>
public partial class WorkflowRailControl : UserControl
{
    public WorkflowRailControl()
    {
        InitializeComponent();
    }

    /// <summary>Docks the runtime-built rail content inside this shell.</summary>
    public void HostContent(Control content)
    {
        content.Dock = DockStyle.Fill;
        Controls.Add(content);
    }
}
