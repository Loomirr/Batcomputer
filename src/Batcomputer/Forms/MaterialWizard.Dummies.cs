namespace Batcomputer;

public sealed partial class MaterialWizard
{
    internal bool SetSelectedTextureDummy()
    {
        _grid.EndEdit();
        var row = _grid.CurrentRow;
        if (row is null || row.IsNewRow || !string.Equals(row.Cells["Kind"].Value?.ToString(), "Texture", StringComparison.OrdinalIgnoreCase))
        { _status.Text = "Select a texture parameter row before choosing Set dummy."; return false; }
        var parameter = row.Cells["Param"].Value?.ToString() ?? "";
        var choice = MaterialDummyTextureService.Resolve(parameter, _lastTemplateInfo);
        if (choice is null)
        {
            _status.Text = $"No verified dummy for {parameter} in this shader. Nothing changed; use Face helpers for face visibility.";
            row.Cells["Override"].ToolTipText = _status.Text; return false;
        }
        _faceHelperAuthoredRows.Remove(FaceHelperRowKey(row));
        row.Cells["Override"].Value = choice.ObjectPath;
        SetTextureRowWarning(row, null);
        row.Cells["Override"].ToolTipText = choice.Explanation + " Clear this override cell to inherit again.";
        row.Cells["Override"].Style.ForeColor = Theme.Info;
        _status.Text = $"{parameter} → {UnrealPathUtil.AssetName(choice.ObjectPath)}. {choice.Explanation}";
        return true;
    }
}
