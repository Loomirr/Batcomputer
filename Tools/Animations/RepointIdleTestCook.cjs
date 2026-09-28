// Repoint disposable cooked AnimSequences from the validated temporary rig to
// the game's shared LEGOfig skeleton. Does not modify the original cook.
const fs = require('fs'), path = require('path'), cp = require('child_process');
const root = path.resolve(__dirname, '../..');
const source = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3]);
const tool = path.join(root, 'artifacts/animation-creator-build/Batcomputer.dll');
if (!fs.existsSync(tool)) throw new Error('Build the Batcomputer CLI first.');
const native = '/Game/Characters/LEGOfig/SKEL_LEGOfig';
const temporary = '/Game/Mods/AnimationCreatorIdleTest/SK_AnimationCreatorTest_Skeleton';
fs.mkdirSync(output, { recursive:true });
for (const name of ['A_IdleBreathingTest', 'A_IdleLookAroundTest', 'A_IdleWaveTest']) {
  const target = path.join(output, name + '.uasset');
  for (const ext of ['.uasset', '.uexp']) {
    const src = path.join(source, name + ext);
    if (!fs.existsSync(src)) throw new Error('Incomplete cooked sequence: ' + src);
    fs.copyFileSync(src, path.join(output, name + ext));
  }
  for (const [from, to] of [[temporary, native], ['SK_AnimationCreatorTest_Skeleton', 'SKEL_LEGOfig']]) {
    const result = cp.spawnSync('dotnet', [tool, '--repath-namemap', target, from, to], { encoding:'utf8', windowsHide:true });
    if (result.status !== 0) throw new Error(String(result.error || '') + '\n' + result.stdout + '\n' + result.stderr);
    console.log(result.stdout.trim());
  }
  const probe = cp.spawnSync('dotnet', [tool, '--probe-refs', target], { encoding:'utf8', windowsHide:true });
  if (probe.status !== 0 || !probe.stdout.includes(native + '.SKEL_LEGOfig') || probe.stdout.includes(temporary))
    throw new Error('Native-skeleton import validation failed for ' + name + '\n' + probe.stdout + probe.stderr);
  console.log(name + ': native skeleton import verified');
}
