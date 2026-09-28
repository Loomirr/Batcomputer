// Lay out the disposable animation-draft test mod for a manual game acceptance check.
// This writes an artifact ZIP input only; it never touches the installed game.
const fs = require('fs'), path = require('path'), crypto = require('crypto');
const root = path.resolve(__dirname, '../..');
const id = 'AnimationDraftFlowTest';
const build = path.join(root, `artifacts/animation-draft-flow-test/Workspace/Generated/NativeSuitModBuilds/${id}`);
const output = path.join(root, 'artifacts/animation-draft-flow-test/InstallLayout');
const game = path.join(output, 'LEGO Batman - Legacy of the Dark Knight/LEGOBatmanLotDK');
if (fs.existsSync(output)) throw new Error('Refusing to overwrite an existing test install layout: ' + output);
function copy(source, destination) {
  if (!fs.existsSync(source)) throw new Error('Missing build file: ' + source);
  fs.mkdirSync(path.dirname(destination), { recursive:true });
  fs.copyFileSync(source, destination, fs.constants.COPYFILE_EXCL);
}
for (const extension of ['.pak', '.ucas', '.utoc']) copy(
  path.join(build, id + '_P' + extension),
  path.join(game, 'Content/Paks/~mods/Expanded', id + '_P' + extension));
copy(path.join(build, 'mod.json'), path.join(game, `Binaries/Win64/ue4ss/LOTDKExpanded/Mods/${id}/mod.json`));
copy(path.join(build, 'LooseFiles/LEGOBatmanLotDK/Config/Tags', id + 'Tags.ini'),
  path.join(game, 'Config/Tags', id + 'Tags.ini'));
for (const name of [id + 'Registry.uplugin', 'AssetRegistry.bin']) copy(
  path.join(build, 'Engine/Plugins/Mods', id + 'Registry', name),
  path.join(game, 'Binaries/Win64/ue4ss/LOTDKExpanded/RegistryPlugins', id + 'Registry', name));
const instructions = [
  'BATCOMPUTER ANIMATION-DRAFT FLOW TEST — DISPOSABLE',
  '',
  'This mod was built from the animation editor JSON through Batcomputer’s new automated Unreal 5.6 cook and library import.',
  'It does not replace any base-game package. The primary Batman idle is replaced with IdleWaveTest.',
  '',
  'Install: close the game, then merge the LEGO Batman - Legacy of the Dark Knight folder in this archive into your game folder.',
  'If a prior AnimationCreatorIdleTest mod is installed, remove that old test mod first to avoid confusing the results.',
  'Start the game, select Batman — Draft Animation Flow Test, and stand still until the primary idle plays.',
  'Expected: a brief right-arm lift/wave, then return to rest. Other animation slots should remain native.',
  'After testing, remove only the AnimationDraftFlowTest files/folders shown in this archive.',
  '',
  'This is a local test build, not a release and not an automatic game installation.'
].join('\r\n') + '\r\n';
fs.writeFileSync(path.join(output, 'TEST-INSTRUCTIONS.txt'), instructions);
const files = [];
function walk(folder) {
  for (const entry of fs.readdirSync(folder, { withFileTypes:true })) {
    const absolute = path.join(folder, entry.name);
    if (entry.isDirectory()) walk(absolute);
    else files.push(absolute);
  }
}
walk(output);
const sums = files.filter(file => !file.endsWith('SHA256SUMS.txt')).map(file => {
  const relative = path.relative(output, file).replaceAll('\\', '/');
  const digest = crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  return digest + '  ' + relative;
});
fs.writeFileSync(path.join(output, 'SHA256SUMS.txt'), sums.join('\n') + '\n');
console.log('layout=' + output);
console.log('files=' + files.length);
