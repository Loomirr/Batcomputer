using Newtonsoft.Json.Linq;
using System.Text;

namespace Batcomputer;

internal static class CharacterVoiceRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        var definition = CustomCharacterProjectService.CreateRecipe(null, "Example hero", "ExampleHero", "ExampleHero");
        var suit = CustomCharacterProjectService.CreateRecipe(definition, "Example suit", "ExampleHero", "Blue", definition.SlotId);
        var baseVoice = new CharacterVoiceLibraryService.Profile { CharacterId = definition.SlotId };
        var suitVoice = new CharacterVoiceLibraryService.Profile { CharacterId = suit.SlotId };
        yield return (CharacterVoiceBuildService.BuildIdentity(definition, baseVoice, null) == CharacterVoiceBuildService.BuildIdentity(suit, baseVoice, definition) &&
            CharacterVoiceBuildService.BuildIdentity(suit, suitVoice, null) != CharacterVoiceBuildService.BuildIdentity(definition, baseVoice, null) &&
            Reject(() => CharacterVoiceBuildService.BuildIdentity(suit, baseVoice, null)),
            "inherited voice profiles reuse their character's private actor, media and routing identity; explicit suit voice profiles remain isolated and missing definitions fail closed");
        var wem=new byte[102];"RIFF"u8.CopyTo(wem);BitConverter.GetBytes(94).CopyTo(wem,4);"WAVEfmt "u8.CopyTo(wem.AsSpan(8));
        BitConverter.GetBytes(66).CopyTo(wem,16);BitConverter.GetBytes((ushort)65535).CopyTo(wem,20);BitConverter.GetBytes((ushort)1).CopyTo(wem,22);
        BitConverter.GetBytes(48000).CopyTo(wem,24);BitConverter.GetBytes(6).CopyTo(wem,64);BitConverter.GetBytes(0x20cef588).CopyTo(wem,80);wem[84]=8;wem[85]=11;
        "data"u8.CopyTo(wem.AsSpan(86));BitConverter.GetBytes(8).CopyTo(wem,90);BitConverter.GetBytes((ushort)4).CopyTo(wem,94);wem[96]=1;wem[100]=42;
        var normalized=PrivateWemSetupService.Normalize(wem);var alternate=wem.ToArray();alternate[96]=2;var other=PrivateWemSetupService.Normalize(alternate);
        var differentAudio=wem.ToArray();differentAudio[100]=43;
        yield return (!normalized.AsSpan(80,4).SequenceEqual(other.AsSpan(80,4)) &&
            normalized.AsSpan(80,4).SequenceEqual(PrivateWemSetupService.Normalize(differentAudio).AsSpan(80,4)),
            "private Vorbis media separates different decoder setups while identical setups share a stable identity");
        yield return (normalized.SequenceEqual(PrivateWemSetupService.Normalize(normalized)) &&
            Enumerable.Range(0,wem.Length).Where(i=>i<80||i>=84).All(i=>wem[i]==normalized[i]) && wem[80]==0x88,
            "private WEM normalization is idempotent and changes only four setup-identity bytes; input and audio packets stay untouched");
        var malformed=wem.ToArray();BitConverter.GetBytes(200).CopyTo(malformed,64);
        var pcm=wem.ToArray();BitConverter.GetBytes((ushort)1).CopyTo(pcm,20);
        yield return (Reject(()=>PrivateWemSetupService.Normalize(malformed))&&Reject(()=>PrivateWemSetupService.Normalize(wem[..^1]))&&
            PrivateWemSetupService.Normalize(pcm).SequenceEqual(pcm),"private audio rejects invalid setup offsets and truncated RIFFs, without rewriting other codecs");
        var data=JArray.Parse("""[{"Sequence":{"InnerSequence":[{"Character":"ExampleHero","WemName":"Greeting_A"},{"Character":"Default","WemName":"Effort_A"},{"Character":"Partner","WemName":"Reply_A"}]}}]""");
        var lines=CharacterVoiceCatalogService.ParseLines(data,"/Game/Wub/DialogueEvents/Test","DX_CHR_EnterGame","ExampleHero");
        yield return (lines.Length==3&&lines[0].OwnSpeaker&&lines[1].OwnSpeaker&&!lines[2].OwnSpeaker&&lines.Select(l=>l.Id).Distinct().Count()==3,
            "voice discovery preserves native branches, stable line identities and read-only partner dialogue");
        yield return (CharacterVoiceCatalogService.ParseLines(data,"/Game/Other/Test","DX_CHR_EnterGame","ExampleHero")[0].Id!=lines[0].Id,
            "voice replacement identities distinguish equal media names in different events");
        var paths=new[]{"Project/Plugins/Wub_Loc_enUS/Content/Wub/Platforms/Windows/Media/Greeting_A.wem","Project/Plugins/Wub_Loc_frFR/Content/Wub/Platforms/Windows/Media/Greeting_A.wem","Project/Content/Wub/Platforms/Windows/Media/Effort_A.wem"};
        yield return (CharacterVoiceCatalogService.FindMedia(paths,"Greeting_A","enUS")==paths[0]&&CharacterVoiceCatalogService.FindMedia(paths,"Greeting_A","deDE") is null&&CharacterVoiceCatalogService.FindMedia(paths,"Effort_A","deDE")==paths[2],
            "voice preview selects the requested language or shared vocals without borrowing another language");
        var privateName="BCV_"+new string('a',64);
        var sharedMount="LEGOBatmanLotDK/Content/Wub/Platforms/Windows/Media/"+privateName+".wem";
        var localizedPaths=new[]{"LEGOBatmanLotDK/Plugins/Wub_Loc_enUS/Content/Wub/Platforms/Windows/Media/Greeting_A.wem",
            "LEGOBatmanLotDK/Plugins/Wub_Loc_frFR/Content/Wub/Platforms/Windows/Media/Greeting_A.wem",
            "LEGOBatmanLotDK/Plugins/Other/Content/Wub/Platforms/Windows/Media/Greeting_A.wem",
            "LEGOBatmanLotDK/Content/Wub/Platforms/Windows/Media/Effort_A.wem"};
        var speechMounts=CharacterVoiceBuildService.LocalizedMediaMounts(localizedPaths,"Greeting_A",privateName);
        yield return (speechMounts.Length==2&&speechMounts.All(p=>p.EndsWith('/'+privateName+".wem"))&&
            CharacterVoiceBuildService.LocalizedMediaMounts(localizedPaths,"Effort_A",privateName).Length==0&&
            Reject(()=>CharacterVoiceBuildService.LocalizedMediaMounts(localizedPaths,"Greeting_A","../native")),
            "custom speech retains only observed localization lookup roots using unique private filenames; shared grunts and unrelated plugins are unchanged");
        var localizedResult=new CharacterVoiceBuildService.Result("Actor",[],["shared.wem","speech.wem"],"report",[sharedMount,speechMounts[0]]);
        var mixed=CharacterVoiceBuildService.Combine([localizedResult,new("Legacy",[],[privateName+".wem"],"old-report")])!;
        yield return (CharacterVoiceBuildService.MountPaths(mixed).SequenceEqual(new[]{sharedMount,speechMounts[0],sharedMount})&&
            Reject(()=>CharacterVoiceBuildService.MountPaths(localizedResult with {MediaMountPaths=[sharedMount]}))&&
            Reject(()=>CharacterVoiceBuildService.MountPaths(localizedResult with {MediaMountPaths=[sharedMount,"../native.wem"]})),
            "combined voice builds retain localized mount identities, support old shared-media receipts and reject mismatched or escaping media paths");
        byte[] Wav(){using var stream=new MemoryStream();using(var w=new BinaryWriter(stream,Encoding.ASCII,true)){w.Write("RIFF"u8);w.Write(40);w.Write("WAVEfmt "u8);w.Write(16);w.Write((ushort)1);w.Write((ushort)1);w.Write(22050);w.Write(44100);w.Write((ushort)2);w.Write((ushort)16);w.Write("data"u8);w.Write(4);w.Write((short)100);w.Write((short)-100);}return stream.ToArray();}
        var wav=Wav();var format=CharacterVoiceLibraryService.InspectWav(wav);
        var boosted=CharacterVoiceLevelService.Apply(wav,3);
        yield return (CharacterVoiceLevelService.Apply(wav,0).SequenceEqual(wav)&&boosted.Take(44).SequenceEqual(wav.Take(44))&&boosted.Length==wav.Length&&
            BitConverter.ToInt16(boosted,44)==141&&BitConverter.ToInt16(boosted,46)==-141&&BitConverter.ToInt16(wav,44)==100,
            "voice level boosts a copy without changing source samples, PCM headers or duration; zero gain preserves exact bytes");
        var loud=wav.ToArray();System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(loud.AsSpan(44),short.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(loud.AsSpan(46),short.MinValue);
        var limited=CharacterVoiceLevelService.Apply(loud,6);
        yield return (CharacterVoiceLevelService.HasClippedSamples(loud)&&!CharacterVoiceLevelService.HasClippedSamples(limited)&&
            BitConverter.ToInt16(CharacterVoiceLevelService.Apply(wav,-6),44)==50,
            "voice encoding checks actual PCM saturation and can add reconstruction headroom without changing duration");
        yield return (BitConverter.ToInt16(limited,44) is >29000 and <32767&&BitConverter.ToInt16(limited,46) is < -29000 and > -32768&&
            Reject(()=>CharacterVoiceLevelService.Apply(wav,double.NaN))&&Reject(()=>CharacterVoiceLevelService.Apply(wav,7)),
            "voice boost limits near-full-scale positive/negative samples and rejects unsafe gain values");
        var peaks=VoiceWaveformControl.Peaks(wav);
        yield return (peaks.Length==200&&Math.Abs(peaks.Max()-100/32768f)<0.00001f&&CharacterVoiceWorkshopForm.CategoryTitle("VOX_Jump")=="Jump grunts",
            "voice studio uses actual decoded PCM for waveforms and readable category labels");
        yield return (format.Rate==22050&&Math.Abs(format.Seconds-2.0/22050)<1e-9,"voice import accepts complete mono 16-bit PCM and measures sample duration");
        var bad=(byte[])wav.Clone();bad[20]=3;
        yield return (Reject(()=>CharacterVoiceLibraryService.InspectWav(bad))&&Reject(()=>CharacterVoiceLibraryService.InspectWav(wav[..^1]))&&Reject(()=>CharacterVoiceLibraryService.InspectWav(new byte[100])),
            "voice import rejects unsupported codecs, truncated recordings and invalid headers");
        var folder=Directory.CreateTempSubdirectory("BatcomputerVoiceChecks-").FullName;
        string scratch;
        using(var codec=new CharacterVoiceBuildService.CodecWorkspace(Path.Combine(folder,"Runtime"),folder))
        {
            scratch=codec.Folder;
            File.WriteAllBytes(codec.Input,wav);
            yield return (codec.Input.Length<240&&codec.Encoded.Length<240&&codec.Decoded.Length<240&&
                FileSystemPathUtil.IsWithinDirectory(codec.Folder,Path.Combine(folder,"Runtime","VoiceCodec"))&&File.ReadAllBytes(codec.Input).SequenceEqual(wav),
                "voice encoding uses a unique short runtime workspace instead of deeply nested project input and output paths");
        }
        yield return (!Directory.Exists(scratch),"voice codec scratch files are removed after success without deleting the runtime root");
        var longRoot=Path.Combine(folder,new string('x',180));
        using(var fallback=new CharacterVoiceBuildService.CodecWorkspace(longRoot,folder))
            yield return (FileSystemPathUtil.IsWithinDirectory(fallback.Folder,folder)&&!FileSystemPathUtil.IsWithinDirectory(fallback.Folder,longRoot)&&
                fallback.Decoded.Length<240&&!Directory.Exists(longRoot),
                "deep portable installs fall back to a short temporary codec workspace without moving project data");
        yield return (Reject(()=>new CharacterVoiceBuildService.CodecWorkspace(longRoot,longRoot)),
            "voice encoding rejects scratch roots that are too long before launching legacy tools");
        string failedScratch="";
        try { using var failed=new CharacterVoiceBuildService.CodecWorkspace(Path.Combine(folder,"Runtime"),folder);failedScratch=failed.Folder;File.WriteAllBytes(failed.Input,wav);throw new OperationCanceledException(); }
        catch(OperationCanceledException) { }
        yield return (!Directory.Exists(failedScratch),"voice codec scratch is cleaned on exceptions and cancellation without changing imported recordings");
        var library=new CharacterVoiceLibraryService(folder);var source=Path.Combine(folder,"Sample.wav");File.WriteAllBytes(source,wav);
        var clip=library.Import(source,"Effort","Example caption");var duplicate=library.Import(source);
        yield return (clip.Id==duplicate.Id&&library.Clips().Count==1&&File.ReadAllBytes(source).AsSpan().SequenceEqual(wav)&&File.ReadAllBytes(library.ClipPath(clip.Id)).AsSpan().SequenceEqual(wav),
            "voice imports are content-addressed, deduplicated and preserve source audio");
        var plainLibrary=new CharacterVoiceLibraryService(Path.Combine(folder,"Plain"));plainLibrary.Import(source);
        var enriched=plainLibrary.Import(source,"Jump","Catalog caption");
        var retained=plainLibrary.Import(source,"Other","Different caption");
        yield return (enriched.Category=="Jump"&&enriched.Transcript=="Catalog caption"&&retained==enriched&&plainLibrary.Clips().Count==1,
            "catalog reimports enrich plain imports without duplicating audio or replacing existing metadata");
        var profile=new CharacterVoiceLibraryService.Profile{CharacterId="example_character",Name="Example voice",Donor="/Game/Characters/Example",NativeVoice="ExampleHero",Assignments=[new(lines[0].Id,lines[0].EventPackage,lines[0].Media,clip.Id)]};
        profile.GainDb=3;library.Save(profile);var restored=library.Profiles("example_character").Single();
        yield return (restored.GainDb==3&&restored.Assignments.Count==1&&restored.ProposedVoiceActor==profile.ProposedVoiceActor&&library.Profiles("different_character").Count==0,
            "voice drafts persist private per-character identities and exact event/line replacements without editing suits or game assets");
        var ownerProject=new NativeSuitProject{SlotId="example_character",VoiceProfileId=profile.Id};
        var childProject=new NativeSuitProject{SlotId="example_child",VoiceProfileId=profile.Id,CustomCharacter=new(){IsDefinition=false,DefinitionSlotId="example_character"}};
        var otherProject=new NativeSuitProject{SlotId="unrelated_character",VoiceProfileId=profile.Id};
        yield return (CharacterVoiceBuildService.Selected(ownerProject,folder).Id==profile.Id&&CharacterVoiceBuildService.Selected(childProject,folder).Id==profile.Id&&Reject(()=>CharacterVoiceBuildService.Selected(otherProject,folder)),
            "voice builds select only this suit's profile or its explicit character parent, never another character's recordings");
        var untouched=CharacterVoiceBuildService.StageAsync(new NativeSuitProject(),folder,Path.Combine(folder,"MustNotExist"),_=>{},CancellationToken.None).GetAwaiter().GetResult();
        yield return (untouched is null&&!Directory.Exists(Path.Combine(folder,"MustNotExist")),
            "suits with no explicit voice selection skip voice cooking without changing staging or requiring encoders");
        var sourceMedia=Path.Combine(folder,"source.wem");File.WriteAllBytes(sourceMedia,[1,2,3]);
        var sourceReport=Path.Combine(folder,"source-report.json");File.WriteAllText(sourceReport,"{\"checked\":true}");
        var build=new CharacterVoiceBuildService.Result("PrivateActor",["/Game/Mods/Example/Voice/Event"],[sourceMedia],sourceReport);
        var preserved=CharacterVoiceBuildService.PreserveForAggregate(build,Path.Combine(folder,"aggregate"));
        File.Delete(sourceMedia);File.Delete(sourceReport);
        yield return (File.ReadAllBytes(preserved.MediaFiles.Single()).SequenceEqual(new byte[]{1,2,3})&&File.ReadAllText(preserved.ReportPath)=="{\"checked\":true}"&&preserved.Actor==build.Actor,
            "combined mod builds retain voice media and audit evidence before disposable suit stages are cleaned");
        var combined=CharacterVoiceBuildService.Combine([preserved,preserved]);
        yield return (CharacterVoiceBuildService.Combine([]) is null&&combined!.Packages.Length==1&&combined.MediaFiles.Length==2&&combined.ReportPath==preserved.ReportPath,
            "combined mods gather every selected character voice in one packaging pass without changing profiles");
        var sharedSource=Path.Combine(folder,"shared",privateName+".wem");var speechSource=Path.Combine(folder,"localized",privateName+".wem");
        Directory.CreateDirectory(Path.GetDirectoryName(sharedSource)!);Directory.CreateDirectory(Path.GetDirectoryName(speechSource)!);
        File.WriteAllBytes(sharedSource,[1,2,3]);File.WriteAllBytes(speechSource,[1,2,3]);
        var retainedSpeech=CharacterVoiceBuildService.PreserveForAggregate(new("Actor",[],[sharedSource,speechSource],preserved.ReportPath,[sharedMount,speechMounts[0]]),Path.Combine(folder,"aggregate-localized"));
        yield return (retainedSpeech.MediaFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count()==2&&
            retainedSpeech.MediaFiles.All(f=>Path.GetFileName(f)==privateName+".wem"&&File.ReadAllBytes(f).SequenceEqual(new byte[]{1,2,3}))&&
            CharacterVoiceBuildService.MountPaths(retainedSpeech).SequenceEqual(new[]{sharedMount,speechMounts[0]}),
            "aggregate staging keeps same-named shared and localized recordings at distinct source paths so UnrealPak cannot collapse language mounts");
        profile.Assignments.Add(profile.Assignments[0]);
        yield return (Reject(()=>library.Save(profile))&&Reject(()=>library.ClipPath("../outside"))&&Reject(()=>library.ClipPath("C:/outside")),
            "voice draft validation rejects duplicate targets and paths outside the audio cache");
        profile.Assignments.RemoveAt(1);
        yield return (restored.Schema==1&&!restored.Assignments[0].Silent,
            "existing version-one voice assignments remain recording replacements");
        profile.Assignments.Add(new(lines[1].Id,lines[1].EventPackage,lines[1].Media,"",Silent:true));library.Save(profile);
        restored=library.Profiles("example_character").Single();
        yield return (restored.Schema==2&&restored.Assignments[1].Silent&&restored.Assignments[1].ClipId==""&&restored.Assignments[0].ClipId==clip.Id,
            "explicit silence persists independently from original audio and recording replacements");
        profile.Assignments[1]=profile.Assignments[1] with {ClipId=clip.Id};
        yield return (Reject(()=>library.Save(profile)),"silenced lines cannot also contain a replacement recording");
        profile.Assignments.RemoveAt(1);library.Save(profile);
        yield return (library.Profiles("example_character").Single().Assignments.Count==1,
            "restoring a silenced line removes only its override and preserves other assignments");
        var batch = new CharacterVoiceLibraryService.Profile { CharacterId="batch_example", Donor=profile.Donor, NativeVoice=profile.NativeVoice };
        var edit = CharacterVoiceAssignmentService.Edit.Recording;
        var selection = new[] { lines[0], lines[1], lines[0], lines[2] };
        yield return (CharacterVoiceAssignmentService.Apply(batch, selection, edit, clip.Id)==2 && batch.Assignments.Count==2 &&
            batch.Assignments.All(a=>a.ClipId==clip.Id&&!a.Silent) && batch.Assignments.All(a=>a.LineId!=lines[2].Id) &&
            CharacterVoiceAssignmentService.Apply(batch, selection, edit, clip.Id)==0,
            "bulk voice assignment uses one recording for distinct owned slots, ignores partners and is idempotent");
        var retainedAssignment = batch.Assignments[0];
        yield return (CharacterVoiceAssignmentService.Apply(batch, [lines[1],lines[2]], CharacterVoiceAssignmentService.Edit.Silence)==1 &&
            batch.Assignments[0]==retainedAssignment && batch.Assignments[1].Silent && batch.Assignments[1].ClipId=="",
            "bulk silence preserves unrelated assignments and cannot silence another speaker");
        library.Save(batch);var batchReload=library.Profiles("batch_example").Single();
        yield return (batchReload.Schema==2&&batchReload.Assignments.SequenceEqual(batch.Assignments)&&
            CharacterVoiceAssignmentService.Apply(batchReload, selection, CharacterVoiceAssignmentService.Edit.Original)==2&&batchReload.Assignments.Count==0,
            "mixed bulk voice edits round-trip through drafts and bulk restore removes only selected overrides");
        var beforeInvalid=batch.Assignments.ToArray();
        yield return (Reject(()=>CharacterVoiceAssignmentService.Apply(batch, selection, edit, "../bad")) &&
            Reject(()=>CharacterVoiceAssignmentService.Apply(batch, selection, (CharacterVoiceAssignmentService.Edit)99)) &&
            Reject(()=>CharacterVoiceAssignmentService.Apply(batch, [lines[0],lines[1] with { Id="invalid" }], edit, clip.Id)) &&
            batch.Assignments.SequenceEqual(beforeInvalid),
            "invalid batch voice edits fail before mutating any draft assignments");
        File.WriteAllText(Path.Combine(folder,"catalog.json"),"""[{"file":"../outside.wav"}]""");
        yield return (Reject(()=>library.ImportFolder(folder,CancellationToken.None)),"voice catalog imports reject recordings outside the selected source folder");
        yield return (CheckLayout(folder),
            "voice workshop keeps its footer, draft actions and recording panels within the minimum window height");
        yield return (CheckBatchEditor(folder, lines),
            "voice studio multi-select edits preserve selection, use one imported clip and do not auto-select a new target when filters hide edited slots");
    }
    private static bool CheckBatchEditor(string folder, CharacterVoiceCatalogService.Line[] lines)
    {
        bool passed=false;
        var thread=new Thread(()=>{
            try {
                using var form=new CharacterVoiceWorkshopForm(new NativeSuitProject { SlotId="bulk_ui", DisplayName="Example character" },folder);
                const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                object Field(string name)=>typeof(CharacterVoiceWorkshopForm).GetField(name,flags)!.GetValue(form)!;
                void Call(string name,params object[] args)=>typeof(CharacterVoiceWorkshopForm).GetMethod(name,flags)!.Invoke(form,args);
                var native=(ListView)Field("_native");var imports=(ListView)Field("_imports");
                _=native.Handle;_=imports.Handle;
                var profile=new CharacterVoiceLibraryService.Profile { CharacterId="bulk_ui",Donor="/Game/Example",NativeVoice="ExampleHero" };
                typeof(CharacterVoiceWorkshopForm).GetField("_profile",flags)!.SetValue(form,profile);
                typeof(CharacterVoiceWorkshopForm).GetField("_snapshot",flags)!.SetValue(form,new CharacterVoiceCatalogService.Snapshot(profile.Donor,profile.NativeVoice,lines,[]));
                Call("FilterImports");Call("FilterNative",true);Call("SelectVisibleSlots");
                bool selection=native.MultiSelect&&!imports.MultiSelect&&native.SelectedItems.Count==2&&imports.SelectedItems.Count==1;
                Call("Silence");bool silence=profile.Assignments.Count==2&&profile.Assignments.All(a=>a.Silent)&&native.SelectedItems.Count==2;
                Call("Assign");bool assignment=profile.Assignments.Count==2&&profile.Assignments.All(a=>!a.Silent)&&profile.Assignments.Select(a=>a.ClipId).Distinct().Count()==1;
                ((CheckBox)Field("_ownOnly")).Checked=false;Call("SelectVisibleSlots");Call("Restore");
                bool partners=native.SelectedItems.Count==3&&profile.Assignments.Count==0;
                foreach(ListViewItem row in native.Items)
                    if(row.Tag is CharacterVoiceCatalogService.Line { OwnSpeaker:false })row.Selected=false;
                ((ComboBox)Field("_assignmentFilter")).SelectedIndex=1;Call("Silence");
                passed=selection&&silence&&assignment&&partners&&profile.Assignments.Count==2&&native.Items.Count==1&&native.SelectedItems.Count==0;
            } catch { passed=false; }
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return thread.Join(TimeSpan.FromSeconds(10))&&passed;
    }
    private static bool CheckLayout(string folder)
    {
        var passed = false;
        // Control creation installs a Windows Forms synchronization context. Keep it
        // off the regression runner, whose async service checks have no message loop.
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new CharacterVoiceWorkshopForm(new NativeSuitProject { SlotId = "layout_example", DisplayName = "Example character" }, folder);
                form.ClientSize = new(1000, 740);
                form.CreateControl();
                form.PerformLayout();
                var root = form.Controls.OfType<TableLayoutPanel>().Single();
                root.PerformLayout();
                passed = root.Controls.Cast<Control>().All(c => c.Bottom <= root.ClientSize.Height && c.Top >= 0);
            }
            catch { passed = false; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(10)) && passed;
    }
    private static bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
}
