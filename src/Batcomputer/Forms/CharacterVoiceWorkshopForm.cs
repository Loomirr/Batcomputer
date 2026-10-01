using System.Media;

namespace Batcomputer;

internal sealed partial class CharacterVoiceWorkshopForm : AdaptiveForm
{
    private readonly NativeSuitProject _character;
    private readonly string _workspace;
    private readonly CharacterVoiceLibraryService _library;
    private readonly CancellationTokenSource _lifetime = new();
    private CharacterVoiceCatalogService.Snapshot? _snapshot;
    private CharacterVoiceLibraryService.Profile? _profile;
    private readonly ListView _native = List(multiSelect: true);
    private readonly ListView _imports = List();
    private readonly TextBox _nativeSearch = Search("Search native category, recording or speaker…");
    private readonly TextBox _importSearch = Search("Search imported names, categories or transcripts…");
    private readonly TextBox _name = Search("Voice profile name");
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly ComboBox _category = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Theme.OnDarkMuted };
    private readonly Label _identity = new() { AutoSize = true, ForeColor = Theme.OnDarkMuted };
    private SoundPlayer? _player;
    private MemoryStream? _playStream;
    private bool _syncingGain;
    private bool _dirty, _busy, _loadingProfiles, _decodingAudio, _filteringNative;

    internal CharacterVoiceWorkshopForm(NativeSuitProject character, string workspace)
    {
        _character = character; _workspace = workspace; _library = new(workspace);
        Text = "Batcomputer — Voice studio"; ClientSize = new(1280, 900); MinimumSize = new(1000, 740);
        BackColor = Theme.WindowBg; ForeColor = Theme.OnDark; Font = Theme.Body; StartPosition = FormStartPosition.CenterParent;
        BuildStudio();
        foreach(var input in new[]{_name,_nativeSearch,_importSearch}) Theme.StyleDarkInput(input);
        _nativeSearch.TextChanged += (_,_) => FilterNative(); _importSearch.TextChanged += (_,_) => FilterImports(); _category.SelectedIndexChanged += (_,_) => FilterNative();
        _native.SelectedIndexChanged += (_,_) => { if (!_filteringNative) { Stop(); Details(); } }; _imports.SelectedIndexChanged += (_,_) => { Stop(); Details(); };
        _native.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.A) { SelectVisibleSlots(); e.Handled = true; e.SuppressKeyPress = true; } };
        _imports.DoubleClick += (_,_) => PlayImport(); _native.DoubleClick += (_,_) => _ = PlayNative();
        _name.TextChanged += (_,_) => { if(_profile is not null && !_loadingProfiles){_profile.Name=_name.Text;_dirty=true;UpdateStudio();} };
        _profiles.SelectedIndexChanged += (_,_) => SelectProfile();
        Shown += async (_,_) => { Theme.UseDarkTitleBar(this); try { RefreshProfiles(); FilterImports(); await LoadNative(); } catch(Exception ex){Fail(ex);} };
        FormClosing += (_,e) => { if(!ConfirmDraft()){e.Cancel=true;return;} _lifetime.Cancel(); Stop(); };
    }
    private static ListView List(bool multiSelect = false){var list=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,HideSelection=false,MultiSelect=multiSelect};Theme.StyleListView(list);return list;}
    private static TextBox Search(string placeholder)=>new(){Dock=DockStyle.Fill,PlaceholderText=placeholder};
    private static Button Button(string text,Action click){var b=ItemWorkshopUi.Button(text);b.Margin=new(0,0,6,0);b.Click+=(_,_)=>click();return b;}
    private CharacterVoiceCatalogService.Line? SelectedLine=>_native.SelectedItems.Count==1?_native.SelectedItems[0].Tag as CharacterVoiceCatalogService.Line:null;
    private CharacterVoiceCatalogService.Line[] SelectedLines => _native.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<CharacterVoiceCatalogService.Line>().ToArray();
    private CharacterVoiceLibraryService.Clip? SelectedClip=>_imports.SelectedItems.Count==1?_imports.SelectedItems[0].Tag as CharacterVoiceLibraryService.Clip:null;
    private async Task LoadNative(){
        if(_busy)return;_busy=true;UpdateStudio();_status.Text="Reading donor voice cards and dialogue branches…";
        try{var snapshot=await Task.Run(()=>CharacterVoiceCatalogService.Inspect(_character,_lifetime.Token));if(IsDisposed)return;_snapshot=snapshot;
            _category.Items.Clear();_category.Items.Add(new CategoryChoice("", "All categories"));_category.Items.AddRange(snapshot.Lines.GroupBy(l=>l.Category).OrderBy(g=>CategoryTitle(g.Key)).Select(g=>(object)new CategoryChoice(g.Key,$"{CategoryTitle(g.Key)} ({g.Count()})")).ToArray());_category.SelectedIndex=0;
            FilterNative();Identity();_status.Text=$"{snapshot.Lines.Length} native line uses · {snapshot.VoiceActor} · {snapshot.Warnings.Length} discovery warning(s). Includes conditional and partner branches; not every story line.";
            if(snapshot.Warnings.Length>0)_details.Text=string.Join(Environment.NewLine,snapshot.Warnings);
        }catch(OperationCanceledException){}catch(Exception ex){Fail(ex);}finally{_busy=false;UpdateStudio();}
    }
    private void FilterNative(bool selectFirstIfEmpty = true){if(IsDisposed)return;var selected=SelectedLines.Select(l=>l.Id).ToHashSet();_filteringNative=true;_native.BeginUpdate();
        try { _native.Items.Clear();
        var category = (_category.SelectedItem as CategoryChoice)?.Key ?? "";
        foreach(var line in (_snapshot?.Lines??[]).Where(l=>(category.Length==0||l.Category==category)&&(!_ownOnly.Checked||l.OwnSpeaker)&&
            (CategoryTitle(l.Category)+" "+l.Category+" "+l.Media+" "+l.Speaker).Contains(_nativeSearch.Text,StringComparison.OrdinalIgnoreCase)).OrderBy(l=>CategoryTitle(l.Category)).ThenBy(l=>l.Media)){
            var assignment=_profile?.Assignments.FirstOrDefault(a=>a.LineId==line.Id);
            if ((_assignmentFilter.SelectedIndex==1 && assignment is not null)||(_assignmentFilter.SelectedIndex==2 && (assignment is null || assignment.Silent))||(_assignmentFilter.SelectedIndex==3 && assignment?.Silent!=true)) continue;
            var row=new ListViewItem([Readable(line.Media),!line.OwnSpeaker?"Partner · read-only":assignment is null?"Original":assignment.Silent?"● Silenced":"● Assigned"]){Tag=line,ToolTipText=CategoryTitle(line.Category)+"\n"+line.Media+"\nSpeaker: "+line.Speaker,ForeColor=assignment is null?Theme.OnDark:Theme.Materials};_native.Items.Add(row);if(selected.Contains(line.Id))row.Selected=true;
        }if(selectFirstIfEmpty&&_native.SelectedItems.Count==0&&_native.Items.Count>0)_native.Items[0].Selected=true;
        } finally { _native.EndUpdate(); _filteringNative=false; } FitColumns();Details();}
    private void SelectVisibleSlots(){if(_busy)return;_filteringNative=true;_native.BeginUpdate();try{foreach(ListViewItem row in _native.Items)row.Selected=true;}finally{_native.EndUpdate();_filteringNative=false;}Stop();Details();}
    private void FilterImports(){if(IsDisposed)return;var selected=SelectedClip?.Id;_imports.BeginUpdate();_imports.Items.Clear();
        _clips=_library.Clips().ToArray();
        foreach(var clip in _clips.Where(c=>(c.Name+" "+c.Category+" "+c.Transcript).Contains(_importSearch.Text,StringComparison.OrdinalIgnoreCase)).OrderBy(c=>c.Category).ThenBy(c=>c.Name)){
            var row=new ListViewItem([Readable(clip.Name),Readable(clip.Category),clip.Seconds.ToString("0.00")+" s"]){Tag=clip,ToolTipText=clip.Name+"\n"+clip.Transcript};_imports.Items.Add(row);if(clip.Id==selected)row.Selected=true;
        }_imports.EndUpdate();if(_imports.SelectedItems.Count==0&&_imports.Items.Count>0)_imports.Items[0].Selected=true;FitColumns();UpdateStudio();}
    private void Details(){var line=SelectedLine;var clip=SelectedClip;var lines=SelectedLines;_details.Text=(lines.Length>1?$"Selected: {lines.Length} slots · {lines.Count(l=>l.OwnSpeaker)} editable · {lines.Count(l=>!l.OwnSpeaker)} read-only partner slots\r\n"+string.Join("\r\n",lines.Select(l=>$"{l.Category} · {l.Media} · {l.Speaker}"))+"\r\n":line is null?"":$"Native: {line.Category} · {line.Media}\r\nSpeaker: {line.Speaker} · {line.EventPackage}\r\n")+
        (clip is null?"":$"Imported: {clip.Name} · {clip.Seconds:0.00}s · {clip.SampleRate} Hz\r\n{clip.Transcript}\r\n")+
        "Save retains your local profile. Use for builds selects it for this suit; Build mod cooks the assigned recordings into private assets. Native audio and other characters are unchanged.";UpdateStudio();}
    private void Identity(){_identity.Text=$"Original voice: {_snapshot?.VoiceActor??"loading"} · {(_profile is null?"Create a profile or assign your first recording":_profile.Name)} · {(AppSettings.Current.EffectiveVgmstreamExePath() is null?"Audio setup needed for originals":"Audio decoder ready")}";UpdateStudio();}
    private void RefreshProfiles(){_loadingProfiles=true;_profiles.Items.Clear();foreach(var p in _library.Profiles(_character.SlotId))_profiles.Items.Add(new ProfileChoice(p));if(_profile is not null&&!_profiles.Items.Cast<ProfileChoice>().Any(p=>p.Profile.Id==_profile.Id))_profiles.Items.Add(new ProfileChoice(Clone(_profile)));
        var index=_profile is null?(_profiles.Items.Count>0?0:-1):_profiles.Items.Cast<ProfileChoice>().ToList().FindIndex(p=>p.Profile.Id==_profile.Id);_profiles.SelectedIndex=index;
        if(index>=0 && _profiles.Items[index] is ProfileChoice chosen)_profile=Clone(chosen.Profile);_name.Text=_profile?.Name??"";_loadingProfiles=false;Identity();}
    private static CharacterVoiceLibraryService.Profile Clone(CharacterVoiceLibraryService.Profile profile)=>System.Text.Json.JsonSerializer.Deserialize<CharacterVoiceLibraryService.Profile>(System.Text.Json.JsonSerializer.Serialize(profile),CharacterVoiceLibraryService.Json)!;
    private sealed record ProfileChoice(CharacterVoiceLibraryService.Profile Profile){public override string ToString()=>Profile.Name+" · draft";}
    private void SelectProfile(){if(_loadingProfiles)return;var selected=(_profiles.SelectedItem as ProfileChoice)?.Profile;if(!ConfirmDraft()){_loadingProfiles=true;_profiles.SelectedIndex=_profiles.Items.Cast<ProfileChoice>().ToList().FindIndex(p=>p.Profile.Id==_profile?.Id);_loadingProfiles=false;return;}
        _profile=selected is null?null:Clone(_library.Profiles(_character.SlotId).FirstOrDefault(p=>p.Id==selected.Id)??selected);_loadingProfiles=true;_profiles.SelectedIndex=_profiles.Items.Cast<ProfileChoice>().ToList().FindIndex(p=>p.Profile.Id==_profile?.Id);_name.Text=_profile?.Name??"";_loadingProfiles=false;_dirty=false;FilterNative();Identity();}
    private void NewProfile(){if(_snapshot is null){_status.Text="Load the native voice first.";return;}if(!ConfirmDraft())return;
        _profile=new(){CharacterId=_character.SlotId,Name=_character.DisplayName+" voice",Donor=_snapshot.Donor,NativeVoice=_snapshot.VoiceActor};_dirty=true;RefreshProfiles();FilterNative();}
    private bool Compatible()=>_profile is not null&&_snapshot is not null&&_profile.Donor==_snapshot.Donor&&_profile.NativeVoice==_snapshot.VoiceActor;
    private void Assign(){if(SelectedClip is not {} clip){_status.Text="Choose one imported recording for the selected voice slots.";return;}EditSelected(CharacterVoiceAssignmentService.Edit.Recording,clip.Id);}
    private void Silence()=>EditSelected(CharacterVoiceAssignmentService.Edit.Silence);
    private void Restore()=>EditSelected(CharacterVoiceAssignmentService.Edit.Original);
    private void EditSelected(CharacterVoiceAssignmentService.Edit edit,string clipId=""){
        var lines=SelectedLines;if(_busy||_snapshot is null||!lines.Any(l=>l.OwnSpeaker))return;
        if(_profile is null){if(edit==CharacterVoiceAssignmentService.Edit.Original)return;_profile=new(){CharacterId=_character.SlotId,Name=_character.DisplayName+" voice",Donor=_snapshot.Donor,NativeVoice=_snapshot.VoiceActor};_dirty=true;RefreshProfiles();}
        if(!Compatible()){_status.Text="This profile belongs to a different donor. Create a new profile for the current character.";return;}
        Stop();var changed=CharacterVoiceAssignmentService.Apply(_profile!,lines,edit,clipId);_dirty|=changed>0;FilterNative(selectFirstIfEmpty:false);
        var action=edit==CharacterVoiceAssignmentService.Edit.Recording?"assigned the recording":edit==CharacterVoiceAssignmentService.Edit.Silence?"silenced":"restored to original";
        _status.Text=$"{changed} slot(s) {action} in the draft."+(lines.Any(l=>!l.OwnSpeaker)?$" {lines.Count(l=>!l.OwnSpeaker)} read-only partner slot(s) skipped.":"")+" Save draft to keep changes; Build mod applies them in-game.";UpdateStudio();}
    private void Save(){try{if(_profile is null){_status.Text="Create a voice profile first.";return;}_library.Save(_profile);_dirty=false;RefreshProfiles();_status.Text="Voice draft saved locally. Use for builds, then Build mod to apply it in-game.";}catch(Exception ex){Fail(ex);}}
    private void ChooseEncoder(){using var picker=new OpenFileDialog{Title="Choose wav2wem for private voice builds",Filter="wav2wem encoder|wav2wem.exe"};if(picker.ShowDialog(this)!=DialogResult.OK)return;
        var previous=AppSettings.Current.VoiceEncoderExePath;try{AppSettings.Current.VoiceEncoderExePath=picker.FileName;AppSettings.Current.Save();_status.Text="Voice encoder configured. Original game audio will not be replaced.";}catch(Exception ex){AppSettings.Current.VoiceEncoderExePath=previous;Fail(ex);}}
    private void UseForBuilds(){if(_busy||_profile is null||!Compatible()||_profile.Assignments.Count==0){_status.Text="Choose a compatible profile with at least one assigned or silenced line.";return;}
        if(!File.Exists(AppSettings.Current.VoiceEncoderExePath))ChooseEncoder();if(!File.Exists(AppSettings.Current.VoiceEncoderExePath))return;
        try{_library.Save(_profile);_dirty=false;RefreshProfiles();}catch(Exception ex){Fail(ex);return;}
        _character.VoiceProfileId=_profile.Id;UpdateStudio();_status.Text="Profile selected for this suit. Build mod to cook it; installing remains a separate action.";}
    private void UseNativeVoice(){if(_busy)return;_character.VoiceProfileId="";UpdateStudio();_status.Text="Future builds will use native voices. Your imported recordings and draft are kept.";}
    private bool ConfirmDraft(){if(!_dirty)return true;var choice=MessageBox.Show(this,"Save this voice draft before continuing? Imported recordings remain in your library.","Unsaved voice draft",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);if(choice==DialogResult.Cancel)return false;if(choice==DialogResult.Yes){Save();return !_dirty;}_dirty=false;return true;}
    private async Task ImportFiles(){if(_busy)return;using var picker=new OpenFileDialog{Filter="PCM WAV recordings|*.wav",Multiselect=true};if(picker.ShowDialog(this)!=DialogResult.OK)return;await Import(()=>{int n=0;var errors=new List<string>();foreach(var file in picker.FileNames){_lifetime.Token.ThrowIfCancellationRequested();try{_library.Import(file);n++;}catch(Exception ex){errors.Add(Path.GetFileName(file)+": "+ex.Message);}}return(n,errors.ToArray());});}
    private async Task ImportFolder(){if(_busy)return;using var picker=new FolderBrowserDialog{Description="Select a voice folder. An optional catalog.json supplies names, categories and transcripts.",UseDescriptionForTitle=true};if(picker.ShowDialog(this)!=DialogResult.OK)return;await Import(()=>_library.ImportFolder(picker.SelectedPath,_lifetime.Token));}
    private async Task Import(Func<(int Imported,string[] Errors)> work){_busy=true;UpdateStudio();_status.Text="Copying recordings into your workspace library…";try{var result=await Task.Run(work);if(IsDisposed)return;FilterImports();_status.Text=$"Imported/available: {result.Imported}; errors: {result.Errors.Length}. Source recordings were not changed.";if(result.Errors.Length>0)_details.Text=string.Join(Environment.NewLine,result.Errors);}catch(OperationCanceledException){}catch(Exception ex){Fail(ex);}finally{_busy=false;UpdateStudio();}}
    private void Stop(){_playRequest++;if((_player is not null||_decodingAudio)&&!IsDisposed)_status.Text="Playback stopped.";_decodingAudio=false;_player?.Stop();_player?.Dispose();_player=null;_playStream?.Dispose();_playStream=null;_wave.Stop();}
    private void ChooseDecoder(){using var picker=new OpenFileDialog{Title="Select your vgmstream-cli executable for native recording previews",Filter="vgmstream executable|vgmstream-cli.exe;vgmstream.exe|Executable|*.exe"};if(picker.ShowDialog(this)!=DialogResult.OK)return;
        var previous=AppSettings.Current.VgmstreamExePath;try{AppSettings.Current.VgmstreamExePath=picker.FileName;AppSettings.Current.Save();Identity();_status.Text="Native audio decoder configured. Imported PCM WAVs do not require decoding.";}catch(Exception ex){AppSettings.Current.VgmstreamExePath=previous;Fail(ex);}}
    private void Play(string path,string label,double gainDb=0){try{Stop();var bytes=CharacterVoiceLevelService.Apply(File.ReadAllBytes(path),gainDb);_playStream=new(bytes,false);_player=new(_playStream);_player.Load();_player.Play();_wave.Play(bytes,label);_status.Text="Playing: "+label+(gainDb==0?"":$" · {gainDb:+0.0;-0.0} dB");}catch(Exception ex){Stop();Fail(ex);}}
    private void PlayImport(){if(SelectedClip is {} clip)Play(_library.ClipPath(clip.Id),clip.Name);}
    private void PlayAssigned(){if(SelectedLine is {} line&&_profile?.Assignments.FirstOrDefault(a=>a.LineId==line.Id) is {} a){if(a.Silent){Stop();_status.Text="This line is silenced in the draft.";return;}Play(_library.ClipPath(a.ClipId),_clips.FirstOrDefault(c=>c.Id==a.ClipId)?.Name??"Assigned recording",_profile.GainDb);}}
    private async Task PlayNative(){if(_busy||SelectedLine is not {} line)return;
        if(AppSettings.Current.EffectiveVgmstreamExePath() is null){_status.Text="Original recordings need vgmstream. Use Audio setup to choose its executable; your imported WAVs play without it.";return;}
        _busy=true;Stop();_decodingAudio=true;var request=_playRequest;UpdateStudio();_status.Text="Decoding original recording…";var language=(_language.SelectedItem as LanguageChoice)?.Code??"enUS";
        try{var path=await Task.Run(()=>CharacterVoicePreviewService.NativeAsync(_workspace,line.Media,language,_lifetime.Token));if(!IsDisposed&&!_lifetime.IsCancellationRequested&&request==_playRequest)Play(path,line.Media);}catch(OperationCanceledException){}catch(Exception ex){Fail(ex);}finally{_decodingAudio=false;_busy=false;UpdateStudio();}}
    private void Fail(Exception ex){if(!IsDisposed){_lastError=ex.Message;_status.Text=ex.Message;_details.Text=ex.Message;}}
    protected override void Dispose(bool disposing){if(disposing){_lifetime.Cancel();Stop();_details.Dispose();}base.Dispose(disposing);}
}
