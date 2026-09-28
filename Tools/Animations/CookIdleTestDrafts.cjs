// Disposable UE 5.6 cook probe. No game installation or saved suit is touched.
// node CookIdleTestDrafts.cjs <validated native-rig FBX> <draft folder> <output folder>
const fs = require('fs'), path = require('path'), os = require('os'), cp = require('child_process');
const [sourceFbx, draftFolder, outputFolder] = process.argv.slice(2).map(value => path.resolve(value));
if (!fs.existsSync(sourceFbx)) throw new Error('Missing native-rig FBX.');
const rig = JSON.parse(fs.readFileSync(path.join(draftFolder, 'NativeBodyRigReference.json'), 'utf8'));
const byName = new Map(rig.map((bone, index) => [bone.name, index]));
if (byName.size !== rig.length || rig.length < 40) throw new Error('Incomplete native rig reference.');
const donor_bones = rig.map((bone, index) => ({ ...bone,
  parent:bone.parent == null ? -1 : byName.get(bone.parent) }));
if (donor_bones.some((bone, index) => bone.parent >= index || bone.parent === undefined))
  throw new Error('Native rig parent ordering is invalid.');
const drafts = ['IdleBreathingTest', 'IdleLookAroundTest', 'IdleWaveTest'].map(name => path.join(draftFolder, name + '.json'));
drafts.forEach(file => { if (!fs.existsSync(file)) throw new Error('Missing draft: ' + file); });
const projectRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'BCAnimCook-'));
fs.mkdirSync(path.join(projectRoot, 'Config'));
fs.mkdirSync(outputFolder, { recursive:true });
fs.writeFileSync(path.join(projectRoot, 'AnimCook.uproject'), JSON.stringify({ FileVersion:3, EngineAssociation:'5.6',
  Plugins:[{ Name:'PythonScriptPlugin', Enabled:true }, { Name:'EditorScriptingUtilities', Enabled:true },
    { Name:'MeshModelingToolset', Enabled:true }] }));
const ddc = path.join(projectRoot, 'DDC'); fs.mkdirSync(ddc);
fs.writeFileSync(path.join(projectRoot, 'Config', 'DefaultEngine.ini'),
  '[/Script/Engine.RendererSettings]\nr.RayTracing=False\nr.AllowStaticLighting=False\n' +
  '[ConsoleVariables]\nInterchange.FeatureFlags.Import.FBX=False\n' +
  '[SkinnedLocalDDC]\nRoot=(Type=KeyLength, Length=120, Inner=AsyncPut)\n' +
  'AsyncPut=(Type=AsyncPut, Inner=Local)\n' +
  `Local=(Type=FileSystem, ReadOnly=false, Clean=false, Flush=false, DeleteUnused=false, Path="${ddc.replace(/\\/g, '/')}")\n`);
fs.writeFileSync(path.join(projectRoot, 'Config', 'DefaultGame.ini'),
  '[/Script/UnrealEd.ProjectPackagingSettings]\nbUseIoStore=False\nbUsePakFile=False\nbUseZenStore=False\nbCookAll=False\nbSkipEditorContent=True\n');
const fbx = path.join(projectRoot, 'source.fbx'); fs.copyFileSync(sourceFbx, fbx);
fs.writeFileSync(path.join(projectRoot, 'import.json'), JSON.stringify({ source:fbx, scale:1,
  package:'/Game/Mods/AnimationCreatorIdleTest/SK_AnimationCreatorTest', donor_bones, drafts }));
fs.copyFileSync(path.resolve(__dirname, '../SkinnedMesh/import_mesh.py'), path.join(projectRoot, 'import_mesh.py'));
fs.copyFileSync(path.resolve(__dirname, 'author_animation_drafts.py'), path.join(projectRoot, 'author_animation_drafts.py'));
const engine = process.env.UNREAL_ENGINE_ROOT || 'C:/Program Files/Epic Games/UE_5.6';
const editor = path.join(engine, 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe');
if (!fs.existsSync(editor)) throw new Error('UE 5.6 commandlet is not installed: ' + editor);
const project = path.join(projectRoot, 'AnimCook.uproject');
fs.writeFileSync(path.join(outputFolder, 'cook-workspace.txt'), projectRoot + '\n');
function run(phase, args) {
  return new Promise((resolve, reject) => {
    console.log(`${phase}: starting isolated UE 5.6 commandlet`);
    const log = path.join(projectRoot, phase + '.log');
    const child = cp.spawn(editor, [project, '-unattended', '-nop4', '-nosplash', '-NullRHI', '-NoSound', '-NoCrashDialog', '-DDC=SkinnedLocalDDC',
      '-NoZenAutoLaunch', ...args, '-abslog=' + log], { cwd:projectRoot, windowsHide:true, stdio:'ignore' });
    child.on('error', reject);
    child.on('exit', code => {
      if (fs.existsSync(log)) fs.copyFileSync(log, path.join(outputFolder, phase + '.log'));
      if (code !== 0) {
        const lines = fs.existsSync(log) ? fs.readFileSync(log, 'utf8').split(/\r?\n/) : [];
        const errors = lines.filter(line => /Error:|Fatal|Traceback|RuntimeError|Exception/.test(line)).slice(-12);
        reject(new Error(`${phase} failed (${code})\n${errors.join('\n')}\nLog: ${path.join(outputFolder, phase + '.log')}`));
      } else { console.log(`${phase}: complete`); resolve(); }
    });
  });
}
(async () => {
  await run('author', ['-run=pythonscript', '-script=' + path.join(projectRoot, 'author_animation_drafts.py')]);
  const report = path.join(projectRoot, 'animations-result.json');
  if (!fs.existsSync(report)) throw new Error('Unreal did not write an AnimSequence report.');
  fs.copyFileSync(report, path.join(outputFolder, 'animations-result.json'));
  await run('cook', ['-run=Cook', '-TargetPlatform=Windows', '-CookDir=' + path.join(projectRoot, 'Content/Mods'),
    '-NoDefaultMaps', '-NoAlwaysCookMaps', '-SkipEditorContent', '-SkipZenStore']);
  const cooked = path.join(projectRoot, 'Saved/Cooked/Windows/AnimCook/Content/Mods/AnimationCreatorIdleTest');
  if (!fs.existsSync(cooked)) throw new Error('Cooked package tree is missing: ' + cooked);
  for (const file of fs.readdirSync(path.join(cooked, 'Animations'))) {
    if (!/^A_Idle.*Test\.(uasset|uexp|ubulk)$/.test(file)) continue;
    const destination = path.join(outputFolder, 'Content/Mods/AnimationCreatorIdleTest/Animations');
    fs.mkdirSync(destination, { recursive:true });
    fs.copyFileSync(path.join(cooked, 'Animations', file), path.join(destination, file));
  }
  console.log('Cooked test AnimSequence files: ' + path.join(outputFolder, 'Content'));
})().catch(error => { console.error(error); process.exitCode = 1; });
