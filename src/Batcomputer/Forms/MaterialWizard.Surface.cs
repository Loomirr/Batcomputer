namespace Batcomputer;

public partial class MaterialWizard
{
    private bool _surfaceControlsReading;
    private async Task ReadSurfaceControlsAsync()
    {
        if (_surfaceControlsReading) return;
        if (_lastTemplateInfo is not { Status: "ok" } template) { _status.Text = "Read a base material first."; return; }
        _surfaceControlsReading = true;
        _status.Text = "Reading native surface / UV controls…";
        try
        {
            var controls = await Task.Run(() => MaterialSurfaceControlService.Read(template));
            if (IsDisposed || !ReferenceEquals(template, _lastTemplateInfo)) return;
            foreach (var control in controls)
            {
                if (_grid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["Kind"].Value?.ToString() == "Scalar" &&
                    string.Equals(row.Cells["Param"].Value?.ToString(), control.Name, StringComparison.OrdinalIgnoreCase))) continue;
                var index = _grid.Rows.Add("Scalar", control.Name, control.Value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture), "");
                var row = _grid.Rows[index];
                row.Cells["Current"].ToolTipText = "Inherited from the real native shader. A blank override preserves it.";
                row.Cells["Override"].ToolTipText = control.UvChannel
                    ? "Native UV selector: use a channel that exists on your mesh. This is not the preview-only Part UV setting."
                    : "A real native material scalar. Regenerate, assign, rebuild and check its effect in-game.";
                if (control.UvChannel)
                {
                    var cell = new DataGridViewComboBoxCell { DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
                    cell.Items.Add(""); foreach (var channel in Enumerable.Range(0, 8)) cell.Items.Add(channel.ToString());
                    cell.Value = ""; cell.ToolTipText = row.Cells["Override"].ToolTipText; row.Cells["Override"] = cell;
                }
            }
            var uvCount = controls.Count(control => control.UvChannel);
            _status.Text = $"{controls.Count} native surface controls loaded. " + (uvCount > 0
                ? $"{uvCount} native UV selector(s); use a UV set your mesh contains."
                : "No supported global UV-channel selector exposed; Part UV is preview-only.");
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "Could not read native surface controls: " + ex.Message; }
        finally { _surfaceControlsReading = false; }
    }
    private void ReduceCapeFuzz()
    {
        var rows = _grid.Rows.Cast<DataGridViewRow>().Where(row => row.Cells["Kind"].Value?.ToString() == "Scalar" &&
            MaterialSurfaceControlService.IsFuzzStrength(row.Cells["Param"].Value?.ToString() ?? "")).ToArray();
        if (rows.Length == 0) { _status.Text = "Load Surface / UV controls first. This shader may not expose cape fuzz controls."; return; }
        foreach (var row in rows) row.Cells["Override"].Value = "0";
        _status.Text = $"{rows.Length} native fuzz/hair strength override(s) set to zero. Generate and assign the MI; rebuild and check in-game. Blank = inherit.";
    }
}
