namespace Batcomputer;

/// <summary>
/// Read-only right-side inspector for the Research browser. It is intentionally separate from the
/// editable suit InspectorControl so research clicks can never change the current suit.
/// </summary>
public partial class CharacterResearchInspectorControl : UserControl
{
    private string _packagePath = "";

    public CharacterResearchInspectorControl()
    {
        InitializeComponent();
        BackColor = Theme.CardBg;
        Padding = new Padding(6);
        _layout.BackColor = Theme.CardBg;
        _titleLabel.ForeColor = Theme.Gold;
        _infoLabel.ForeColor = Theme.OnDarkMuted;
        _details.BackColor = Theme.SlateDark;
        _details.ForeColor = Theme.OnDark;
        _details.BorderStyle = BorderStyle.None;
        _details.ReadOnly = true;
        _details.DetectUrls = true;
        Theme.StyleSmallDarkButton(_copyPathButton);
    }

    public Button CopyPathButton => _copyPathButton;

    public string SelectedPackagePath => _packagePath;

    public void ShowLoading(CharacterResearchService.ResearchAssetRecord record)
    {
        _packagePath = record.PackagePath;
        _titleLabel.Text = "RESEARCH INSPECTOR";
        _infoLabel.Text = record.AssetName + "\r\n" + record.PackagePath;
        _details.Text = "Reading this asset with UAssetAPI…";
        _copyPathButton.Enabled = true;
    }

    public void ShowInspection(CharacterResearchService.ResearchAssetInspection inspection)
    {
        _packagePath = inspection.Record.PackagePath;
        _titleLabel.Text = inspection.Succeeded ? "RESEARCH INSPECTOR" : "RESEARCH INSPECTOR · PARSE ERROR";
        _infoLabel.Text = inspection.Record.AssetName + "\r\n" + inspection.Record.PackagePath;
        _copyPathButton.Enabled = true;

        var lines = new List<string>();
        lines.AddRange(inspection.SummaryLines);
        lines.Add("");
        lines.Add("Exports");
        lines.AddRange(inspection.ExportLines.Count == 0 ? new[] { "(none)" } : inspection.ExportLines);
        lines.Add("");
        lines.Add("Imports");
        lines.AddRange(inspection.ImportLines.Count == 0 ? new[] { "(none)" } : inspection.ImportLines);
        lines.Add("");
        lines.Add("Interesting references (heuristic)");
        lines.AddRange(inspection.InterestingReferences.Count == 0
            ? new[] { "(none detected)" }
            : inspection.InterestingReferences);
        lines.Add("");
        lines.Add("Note: " + inspection.Note);
        _details.Text = string.Join(Environment.NewLine, lines);
        _details.SelectionStart = 0;
        _details.SelectionLength = 0;
    }

    public void ClearInspection(string message = "Select an extracted character asset to inspect it.")
    {
        _packagePath = "";
        _titleLabel.Text = "RESEARCH INSPECTOR";
        _infoLabel.Text = "Read-only · no suit or package changes";
        _details.Text = message;
        _copyPathButton.Enabled = false;
    }
}
