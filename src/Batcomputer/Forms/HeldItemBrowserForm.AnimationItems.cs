namespace Batcomputer;

public sealed partial class HeldItemsForm
{
    private sealed record AnimationPropChoice(string Id, string Label) { public override string ToString() => Label; }
    private Control LazyAnimationPage(Func<AnimationSpawnedItemService.Inspection> loader)
    {
        var page = new Panel { Dock = DockStyle.Fill, Padding = new(12) };
        var message = new Label { Dock = DockStyle.Top, Height = 90, ForeColor = Theme.OnDarkMuted,
            Text = "Inspect temporary items spawned by animation notifications.\nScan only when needed; the native-items and extra-props editors do not wait for this scan.\nSaved animation-item changes remain intact until you edit or restore them." };
        var scan = ItemWorkshopUi.Button("Scan animation items", true); scan.Dock = DockStyle.Top;
        page.Controls.Add(scan); page.Controls.Add(message);
        scan.Click += async (_, _) => {
            scan.Enabled = false; scan.Text = "Reading animation items…";
            try {
                var inspection = await Task.Run(loader);
                if (IsDisposed || page.IsDisposed) return;
                page.Controls.Clear(); page.Padding = Padding.Empty; page.Controls.Add(AnimationPage(inspection));
                scan.Dispose(); message.Dispose();
            } catch (Exception ex) {
                if (!IsDisposed && !page.IsDisposed) { message.Text = "Animation scan failed. Saved edits have not changed.\n" + ex.Message; scan.Text = "Retry scan"; scan.Enabled = true; }
            }
        };
        return page;
    }
    private Control AnimationPage(AnimationSpawnedItemService.Inspection inspection)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Absolute, 66)); root.RowStyles.Add(new(SizeType.Absolute, 38)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, Text = "Temporary items spawned by animation events—not ordinary ability items.\nReuse an Extra prop's baked model and materials, choose a cooked static mesh, or hide an exact spawn. Timing, sockets and attacks stay native." });
        var search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Search animation, mesh or attachment slot…" }; Theme.StyleDarkInput(search); root.Controls.Add(search, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(1000, 470), SplitterDistance = 600, Panel1MinSize = 300, Panel2MinSize = 300 };
        root.Controls.Add(split, 0, 2);
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false };
        list.Columns.Add("Animation", 270); list.Columns.Add("Attachment", 110); list.Columns.Add("Appearance", 170); Theme.StyleListView(list); ItemWorkshopUi.FitColumns(list, .48f, .22f, .30f); split.Panel1.Controls.Add(list);
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(12), ColumnCount = 1, RowCount = 8, BackColor = Theme.PanelBg };
        foreach (var height in new[] { 38, 48, 38, 42, 38, 42, 76 }) details.RowStyles.Add(new(SizeType.Absolute, height)); details.RowStyles.Add(new(SizeType.Percent, 100)); split.Panel2.Controls.Add(details);
        var selected = new Label { Dock = DockStyle.Fill, ForeColor = Theme.OnDark, Font = Theme.Heading }; details.Controls.Add(selected);
        details.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Theme.OnDarkMuted, Text = "Ctrl / Shift selects several spawns. Apply to both hands and every required phase." }, 0, 1);
        var props = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.PanelBg, ForeColor = Theme.OnDark }; details.Controls.Add(props, 0, 2);
        var useProp = ItemWorkshopUi.Button("Use extra prop", true); useProp.Dock = DockStyle.Fill; details.Controls.Add(useProp, 0, 3);
        var mesh = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Advanced: /Game/.../SM_Mesh" }; Theme.StyleDarkInput(mesh); details.Controls.Add(mesh, 0, 4);
        var useMesh = ItemWorkshopUi.Button("Use cooked mesh"); useMesh.Dock = DockStyle.Fill; details.Controls.Add(useMesh, 0, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        var hide = ItemWorkshopUi.Button("Hide selected"); var reset = ItemWorkshopUi.Button("Restore original"); var report = ItemWorkshopUi.Button("Scan report"); actions.Controls.AddRange([hide, reset, report]); details.Controls.Add(actions, 0, 6);
        var info = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical }; Theme.StyleDarkInput(info); details.Controls.Add(info, 0, 7);
        var bindings = inspection.Items.ToList();
        foreach (var stale in AnimationResult.Where(e => !bindings.Any(b => b.Identity.Key == e.Key))) bindings.Add(new(stale.Clone(), "Not found—restore edit"));
        AnimationSpawnedItemService.Binding[] Selected() => list.SelectedItems.Cast<ListViewItem>().Select(i => (AnimationSpawnedItemService.Binding)i.Tag!).ToArray();
        void Select() {
            var values = Selected(); selected.Text = values.Length == 0 ? "Choose animation items" : $"{values.Length} spawn(s) selected";
            hide.Enabled = useProp.Enabled = useMesh.Enabled = values.Length > 0 && values.All(b => inspection.Items.Any(i => i.Identity.Key == b.Identity.Key)); reset.Enabled = values.Length > 0;
            info.Text = string.Join("\r\n\r\n", values.Select(b => $"{b.Identity.AnimationPackage}\r\n{b.Slot} · export {b.Identity.ExportIndex}\r\nOriginal: {b.Identity.OriginalMeshPackage}"));
        }
        void Refresh() {
            var keys = Selected().Select(b => b.Identity.Key).ToHashSet(); list.BeginUpdate(); list.Items.Clear();
            foreach (var b in bindings.Where(b => (b.Identity.AnimationPackage + b.Identity.OriginalMeshPackage + b.Slot).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))) {
                var edit = AnimationResult.FirstOrDefault(e => e.Key == b.Identity.Key);
                var appearance = edit?.Hide == true ? "Hidden" : edit?.HeldItemId is { Length: > 0 } id ? Result.FirstOrDefault(i => i.Id == id)?.Name ?? "Missing extra prop" : edit is not null ? "Custom mesh" : "Original";
                var row = new ListViewItem([UnrealPathUtil.AssetName(b.Identity.AnimationPackage), AttachmentLabel(b.Slot), appearance]) { Tag = b }; list.Items.Add(row); row.Selected = keys.Contains(b.Identity.Key);
            }
            list.EndUpdate(); Select();
        }
        void PropChoices() {
            var id = (props.SelectedItem as AnimationPropChoice)?.Id; props.Items.Clear();
            foreach (var item in Result) props.Items.Add(new AnimationPropChoice(item.Id, item.Name));
            props.SelectedIndex = props.Items.Count == 0 ? -1 : Math.Max(0, Enumerable.Range(0, props.Items.Count).FirstOrDefault(i => ((AnimationPropChoice)props.Items[i]!).Id == id));
        }
        void Apply(bool hidden, string propId, string package) {
            var edits = Selected().Select(b => { var e = b.Identity.Clone(); e.Hide = hidden; e.HeldItemId = propId; e.ReplacementMeshPackage = package; return e; }).ToArray();
            var candidate = AnimationResult.Where(e => !edits.Any(x => x.Key == e.Key)).Concat(edits).ToList();
            var errors = AnimationSpawnedItemService.Validate(new() { HeldItems = Result, AnimationSpawnedItems = candidate });
            if (errors.Count > 0) { Dialog.Warn(this, "Animation items", string.Join("\n", errors)); return; }
            AnimationResult = candidate; Refresh();
        }
        useProp.Click += (_, _) => { if (props.SelectedItem is AnimationPropChoice choice) Apply(false, choice.Id, ""); else Dialog.Info(this, "Add an extra prop", "Import and assign your model in Extra props first. Then reuse that prop here."); };
        useMesh.Click += (_, _) => Apply(false, "", mesh.Text.Trim()); hide.Click += (_, _) => Apply(true, "", "");
        reset.Click += (_, _) => { var keys = Selected().Select(b => b.Identity.Key).ToHashSet(); AnimationResult.RemoveAll(e => keys.Contains(e.Key)); Refresh(); };
        report.Click += (_, _) => Dialog.Info(this, "Animation item discovery", "Scans explicit animation references and direct static-mesh spawn notifies. Code-spawned, inherited and skeletal items are not guessed.\n\n" + string.Join("\n", inspection.Warnings));
        list.SelectedIndexChanged += (_, _) => Select(); search.TextChanged += (_, _) => Refresh(); props.DropDown += (_, _) => PropChoices();
        PropChoices(); Refresh(); return root;
    }
}
