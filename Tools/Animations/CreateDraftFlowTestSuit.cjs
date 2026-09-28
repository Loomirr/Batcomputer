// Disposable acceptance suit for the in-app draft cook. Does not install game files.
const fs = require('fs'), path = require('path');
const root = path.resolve(__dirname, '../..');
const workspace = path.join(root, 'artifacts/animation-draft-flow-test/Workspace');
const previous = path.join(root, 'artifacts/animation-cook-test/SuitWorkspace/Generated');
const modId = 'AnimationDraftFlowTest';
const sourceSuit = path.join(previous, 'NativeSuitGuiProjects/AnimationCreatorIdleTest.native-suit-project.json');
const sourceMod = path.join(previous, 'NativeSuitModProjects/AnimationCreatorIdleTest.native-suit-mod-project.json');
const suitPath = path.join(workspace, `Generated/NativeSuitGuiProjects/${modId}.native-suit-project.json`);
const modPath = path.join(workspace, `Generated/NativeSuitModProjects/${modId}.native-suit-mod-project.json`);
const library = JSON.parse(fs.readFileSync(path.join(workspace, 'Generated/AnimationLibrary/library.json'), 'utf8'));
const cooked = library.Entries.find(entry => entry.PackagePath?.startsWith('/Game/Mods/BatcomputerAnimations/Animations/A_IdleWaveTest_') && entry.IsAvailable && entry.CachedFiles?.length);
if (!cooked) throw new Error('Cook the IdleWaveTest draft through --cook-animation-draft first.');
for (const file of [suitPath, modPath]) if (fs.existsSync(file)) throw new Error('Refusing to overwrite: ' + file);
const clone = file => JSON.parse(fs.readFileSync(file, 'utf8').replaceAll('AnimationCreatorIdleTest', modId));
const suit = clone(sourceSuit), mod = clone(sourceMod);
suit.displayName = 'Batman — Draft Animation Flow Test';
suit.description = 'Disposable test suit. Its primary idle plays the animation cooked from a Batcomputer draft.';
suit.locomotionOverrides = [{
  donorSequence:'A_Idle_Batman',
  donorSequencePackage:'/Game/Animation/LEGOfig/Batman/Movement/A_Idle_Batman',
  replacementSequence:cooked.PackagePath.split('/').at(-1),
  replacementPackage:cooked.PackagePath
}];
suit.animationSlotOverrides = [];
suit.animationOverrides = [];
suit.useCustomArchetype = true;
mod.displayName = suit.displayName;
mod.description = suit.description;
fs.mkdirSync(path.dirname(suitPath), { recursive:true });
fs.mkdirSync(path.dirname(modPath), { recursive:true });
fs.writeFileSync(suitPath, JSON.stringify(suit, null, 2) + '\n');
fs.writeFileSync(modPath, JSON.stringify(mod, null, 2) + '\n');
console.log('suit=' + suitPath);
console.log('mod=' + modPath);
console.log('animation=' + cooked.PackagePath);
