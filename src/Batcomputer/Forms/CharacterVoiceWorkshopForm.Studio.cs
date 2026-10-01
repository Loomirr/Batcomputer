using System.Text.RegularExpressions;

namespace Batcomputer;

internal sealed partial class CharacterVoiceWorkshopForm
{
    private readonly Label _summary = Caption("");
    private readonly Label _slotTitle = Caption("Select a voice slot");
    private readonly Label _recordingTitle = Caption("Import or choose a recording");
    private readonly Label _assignment = Caption("Choose a slot on the left and a recording on the right.");
    private readonly Label _draftState = Caption("No voice profile yet");
    private readonly Label _decoderState = Caption("");
    private readonly CheckBox _ownOnly = new() { Text = "This character only", Checked = true, AutoSize = true };
    private readonly ComboBox _assignmentFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private Button _assignButton = null!, _silenceButton = null!, _restoreButton = null!, _nativePlay = null!, _assignedPlay = null!, _importPlay = null!, _saveButton = null!;
    private readonly VoiceWaveformControl _wave = new() { Dock = DockStyle.Fill };
    private int _playRequest;
    private string _lastError = "";
    private CharacterVoiceLibraryService.Clip[] _clips = [];
    private readonly NumericUpDown _voiceGain = new() { Minimum=-12, Maximum=6, DecimalPlaces=1, Increment=.5m, Width=72, AccessibleName="Assigned voice volume in decibels" };

    private static Label Caption(string text) => new() { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Theme.OnDarkMuted, TextAlign = ContentAlignment.MiddleLeft };
    private static TableLayoutPanel Stack(params float[] heights)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length, Margin = Padding.Empty };
        foreach (var h in heights) panel.RowStyles.Add(h < 0 ? new(SizeType.Percent, 100) : new(SizeType.Absolute, h));
        return panel;
    }
    private static FlowLayoutPanel Bar(params Control[] controls)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, Margin = Padding.Empty };
        bar.Controls.AddRange(controls); return bar;
    }
    private void BuildStudio()
    {
        var root = Stack(58, 66, 38, -1, 116, 44, 32);
        root.Padding = new(16); Controls.Add(root);
        var heading = Stack(30, 28);
        heading.Controls.Add(new Label { Text = "VOICE STUDIO  /  " + _character.DisplayName, Dock = DockStyle.Fill, Font = Theme.Heading, ForeColor = Theme.Gold }, 0, 0);
        heading.Controls.Add(Caption("Listen → assign or silence → save → use for builds. Build your mod to apply the selected profile."), 0, 1);
        root.Controls.Add(heading, 0, 0);

        var profiles = Stack(38, 28);
        _profiles.Width = 190; _name.Dock = DockStyle.None; _name.Width = 210;
        _profiles.AccessibleName = "Saved voice profiles"; _name.AccessibleName = "Voice profile name";
        profiles.Controls.Add(Bar(_profiles, Button("＋ New profile", NewProfile), _name, Button("Audio setup…", ChooseDecoder), Button("Encoder…", ChooseEncoder), Button("Refresh slots", () => _ = LoadNative())), 0, 0);
        _identity.AutoSize = false; _identity.Dock = DockStyle.Fill; _identity.AutoEllipsis = true;
        var identityRow=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty };
        identityRow.ColumnStyles.Add(new(SizeType.Percent,100));identityRow.ColumnStyles.Add(new(SizeType.Absolute,250));
        identityRow.Controls.Add(_identity,0,0);
        var levelLabel=Caption("Volume · dB");levelLabel.Dock=DockStyle.None;levelLabel.Size=new(110,25);
        _voiceGain.BackColor=Theme.CardBg;_voiceGain.ForeColor=Theme.OnDarkMuted;
        identityRow.Controls.Add(Bar(levelLabel,_voiceGain),1,0);
        _voiceGain.ValueChanged+=(_,_)=>{if(_loadingProfiles||_syncingGain||_profile is null||_profile.GainDb==(double)_voiceGain.Value)return;_profile.GainDb=(double)_voiceGain.Value;_dirty=true;UpdateStudio();};
        profiles.Controls.Add(identityRow, 0, 1); root.Controls.Add(profiles, 0, 1);
        root.Controls.Add(_summary, 0, 2);

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new(SizeType.Percent, 52)); columns.ColumnStyles.Add(new(SizeType.Percent, 48));
        root.Controls.Add(columns, 0, 3);
        var left = Stack(32, 36, 34, 40, -1, 34, 40); left.Padding = new(12); left.BackColor = Theme.CardBg; left.Margin = new(0, 0, 8, 0);
        left.Controls.Add(new Label { Text = "01  CHOOSE A VOICE SLOT", Dock = DockStyle.Fill, ForeColor = Theme.Gold }, 0, 0);
        left.Controls.Add(_nativeSearch, 0, 1);
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        filters.ColumnStyles.Add(new(SizeType.Percent, 58)); filters.ColumnStyles.Add(new(SizeType.Percent, 42));
        filters.Controls.Add(_category, 0, 0); filters.Controls.Add(_assignmentFilter, 1, 0); left.Controls.Add(filters, 0, 2);
        _category.Items.Add(new CategoryChoice("", "All categories")); _category.SelectedIndex = 0;
        _assignmentFilter.Items.AddRange(["All slots", "Using original", "Assigned in draft", "Silenced in draft"]); _assignmentFilter.SelectedIndex = 0;
        left.Controls.Add(Bar(_ownOnly, Button("Select all shown", SelectVisibleSlots)), 0, 3); left.Controls.Add(_native, 0, 4); left.Controls.Add(_slotTitle, 0, 5);
        _native.AccessibleName = "Voice slots · Ctrl or Shift to select several; Ctrl+A selects all shown";
        _nativePlay = Button("▶ Original", () => _ = PlayNative()); _assignedPlay = Button("▶ Assigned", PlayAssigned);
        _language.Items.AddRange([new LanguageChoice("enUS", "English"), new LanguageChoice("deDE", "German"), new LanguageChoice("esES", "Spanish"), new LanguageChoice("esMX", "Spanish (Latin America)"), new LanguageChoice("frFR", "French"), new LanguageChoice("itIT", "Italian"), new LanguageChoice("jaJP", "Japanese"), new LanguageChoice("plPL", "Polish"), new LanguageChoice("ptBR", "Portuguese (Brazil)")]);
        _language.Width = 130; _language.SelectedIndex = 0;
        left.Controls.Add(Bar(_nativePlay, _assignedPlay, _language), 0, 6);
        columns.Controls.Add(left, 0, 0);

        var right = Stack(32, 36, 40, -1, 34, 40); right.Padding = new(12); right.BackColor = Theme.CardBg;
        right.Controls.Add(new Label { Text = "02  CHOOSE YOUR RECORDING", Dock = DockStyle.Fill, ForeColor = Theme.Materials }, 0, 0);
        right.Controls.Add(_importSearch, 0, 1);
        right.Controls.Add(Bar(Button("＋ Import WAVs", () => _ = ImportFiles()), Button("Import folder", () => _ = ImportFolder())), 0, 2);
        right.Controls.Add(_imports, 0, 3); right.Controls.Add(_recordingTitle, 0, 4);
        _importPlay = Button("▶ Recording", PlayImport);
        right.Controls.Add(Bar(_importPlay, Button("■ Stop audio", Stop)), 0, 5); columns.Controls.Add(right, 1, 0);
        _native.Columns.Add("Voice slot / recording", 280); _native.Columns.Add("Draft state", 150);
        _imports.Columns.Add("Recording", 220); _imports.Columns.Add("Category", 140); _imports.Columns.Add("Length", 65);
        _native.Resize += (_, _) => FitColumns(); _imports.Resize += (_, _) => FitColumns();
        _native.ShowItemToolTips = true; _imports.ShowItemToolTips = true;

        var assignment = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new(10), BackColor = Theme.SlateDark, Margin = new(0, 8, 0, 0) };
        assignment.ColumnStyles.Add(new(SizeType.Percent, 72)); assignment.ColumnStyles.Add(new(SizeType.Percent, 28));
        var target = Stack(26, 28, 40); target.Controls.Add(new Label { Text = "03  EDIT SELECTED SLOTS · CTRL / SHIFT TO MULTI-SELECT", Dock = DockStyle.Fill, ForeColor = Theme.Gold }, 0, 0);
        target.Controls.Add(_assignment, 0, 1);
        _assignButton = Button("Use recording", Assign); Theme.StyleGoldButton(_assignButton);
        _restoreButton = Button("Use original", Restore);
        _silenceButton = Button("Silence selected", Silence);
        target.Controls.Add(Bar(_assignButton, _silenceButton, _restoreButton, Button("Details…", ShowTechnicalDetails)), 0, 2);
        assignment.Controls.Add(target, 0, 0); assignment.Controls.Add(_wave, 1, 0); root.Controls.Add(assignment, 0, 4);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        footer.RowStyles.Add(new(SizeType.Percent,100));
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 525));
        footer.Controls.Add(_draftState, 0, 0); _saveButton = Button("Save voice draft", Save); Theme.StyleGoldButton(_saveButton);
        footer.Controls.Add(Bar(_saveButton, Button("Use for builds", UseForBuilds), Button("Use native voice", UseNativeVoice), Button("Close", Close)), 1, 0); root.Controls.Add(footer, 0, 5);
        root.Controls.Add(_status, 0, 6);
        foreach (var combo in new[] { _category, _assignmentFilter, _profiles, _language }) Theme.StyleDarkCombo(combo);
        _assignmentFilter.SelectedIndexChanged += (_, _) => FilterNative(); _ownOnly.CheckedChanged += (_, _) => FilterNative();
        _language.SelectedIndexChanged += (_, _) => Stop();
        UpdateStudio();
    }
    private void FitColumns()
    {
        if (_native.Columns.Count == 2) { _native.Columns[1].Width = 135; _native.Columns[0].Width = Math.Max(150, _native.ClientSize.Width - 137); }
        if (_imports.Columns.Count == 3) { _imports.Columns[2].Width = 58; _imports.Columns[1].Width = 105; _imports.Columns[0].Width = Math.Max(140, _imports.ClientSize.Width - 165); }
    }
    private sealed record CategoryChoice(string Key, string Label) { public override string ToString() => Label; }
    private sealed record LanguageChoice(string Code, string Label) { public override string ToString() => Label; }
    internal static string CategoryTitle(string category) => category switch
    {
        "VOX_Jump" => "Jump grunts", "VOX_Attack" => "Attack grunts", "VOX_TakeHit" => "Hit reactions", "VOX_Death" => "Defeat / death",
        _ => Readable(category.Replace("DX_CHR_", "").Replace("VOX_", "").Replace("DX_", ""))
    };
    private static string Readable(string value) => Regex.Replace(value.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ").Trim();
    private void UpdateStudio()
    {
        if (_assignButton is null || IsDisposed) return;
        var line = SelectedLine; var clip = SelectedClip; var lines = SelectedLines;
        var editable = lines.Where(l => l.OwnSpeaker).ToArray();
        var assignment = line is null ? null : _profile?.Assignments.FirstOrDefault(a => a.LineId == line.Id);
        var assigned = assignment is null ? null : _clips.FirstOrDefault(c => c.Id == assignment.ClipId);
        _summary.Text = _snapshot is null ? "Reading the character's voice slots…" : $"{_native.Items.Count} visible slots  ·  {_clips.Length} recordings  ·  {_profile?.Assignments.Count(a => !a.Silent) ?? 0} assigned  ·  {_profile?.Assignments.Count(a => a.Silent) ?? 0} silenced in draft";
        _slotTitle.Text = lines.Length > 1 ? $"{lines.Length} selected · {editable.Length} editable · {lines.Length-editable.Length} read-only" : line is null ? (_native.Items.Count == 0 ? "No slots match these filters." : "Select a voice slot.") : CategoryTitle(line.Category) + " · " + (assignment is null ? "using original" : assignment.Silent ? "silenced in draft" : assigned?.Name ?? "assigned file missing");
        _recordingTitle.Text = clip is null ? (_clips.Length == 0 ? "Your library is empty. Import WAVs or a voice folder." : "Select a recording to preview or assign.") : $"{clip.Name} · {clip.Seconds:0.00}s · {clip.SampleRate:N0} Hz";
        _assignment.Text = lines.Length == 0 ? "Choose voice slots on the left (Ctrl / Shift selects several)." : editable.Length == 0 ? "Partner dialogue is read-only; it belongs to another speaker." : $"{editable.Length} editable slot(s)" + (clip is null ? " · choose a recording or silence them." : $"  ←  {clip.Name}") + (editable.Length < lines.Length ? " · partner slots skipped" : "");
        _assignButton.Enabled = !_busy && editable.Length > 0 && clip is not null && (_profile is null || Compatible());
        _silenceButton.Enabled = !_busy && editable.Any(l => _profile?.Assignments.FirstOrDefault(a => a.LineId == l.Id)?.Silent != true) && (_profile is null || Compatible());
        _restoreButton.Enabled = !_busy && editable.Any(l => _profile?.Assignments.Any(a => a.LineId == l.Id) == true) && Compatible();
        _nativePlay.Enabled = !_busy && line is not null;
        _assignedPlay.Enabled = !_busy && assigned is not null;
        _importPlay.Enabled = !_busy && clip is not null;
        _saveButton.Enabled = !_busy && _profile is not null && _dirty;
        _draftState.Text = (_dirty ? "● Unsaved draft" : "Saved locally") + (_profile is not null && _character.VoiceProfileId == _profile.Id ? " · selected for builds" : string.IsNullOrEmpty(_character.VoiceProfileId) ? " · builds use native voice" : " · another profile selected");
        _decoderState.Text = AppSettings.Current.EffectiveVgmstreamExePath() is null ? "Native decoder is not configured." : "Native decoder ready.";
        _name.Enabled = _profile is not null && !_busy;
        _voiceGain.Enabled = _profile is not null && !_busy;
        var level=_profile?.GainDb??0;
        var gain=(decimal)(double.IsFinite(level)?Math.Clamp(level,-12,6):0);
        _syncingGain=true;try{if(_voiceGain.Value!=gain)_voiceGain.Value=gain;}finally{_syncingGain=false;}
    }
    private void ShowTechnicalDetails()
    {
        Details();
        using var dialog = new AdaptiveDialogForm { Text = "Voice slot details", ClientSize = new(720, 470), MinimumSize = new(550, 350), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.WindowBg, ForeColor = Theme.OnDark, Font = Theme.Body, Padding = new(16) };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = _details.Text + "\r\n\r\n" + _identity.Text + "\r\nProfile identity: " + (_profile?.Id ?? "not created") + " (build creates a suit-local actor)\r\n" + _decoderState.Text + "\r\n" + string.Join("\r\n", _snapshot?.Warnings ?? []) + "\r\n" + _lastError };
        Theme.StyleDarkInput(text); dialog.Controls.Add(text); var close = Button("Close", dialog.Close); dialog.Controls.Add(DialogActionFooter.Create(close)); dialog.ShowDialog(this);
    }
}
