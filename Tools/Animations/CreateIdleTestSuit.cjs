// Create a disposable, build-only Batman suit using three authored idle clips.
// This never copies files into the game installation.
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '../..');
const workspace = path.join(root, 'artifacts/animation-cook-test/SuitWorkspace');
const build = path.join(root, 'artifacts/animation-creator-build');
const gameContent = path.join(root, 'artifacts/GameRefresh_20260916/Extracts/DeveloperResearch_20260916_163330/LEGOBatmanLotDK/Content');
const sourceSuit = path.join(root, 'artifacts/FaceVoice_20260920/Workspace/Generated/NativeSuitGuiProjects/FaceAnimationTest.native-suit-project.json');
const sourceMod = path.join(root, 'artifacts/FaceVoice_20260920/Workspace/Generated/NativeSuitModProjects/FaceAnimationTest.native-suit-mod-project.json');
const mapping = path.join(root, 'artifacts/icon-test-bin/Data/Mappings/5.6.1-1308853+++Dinner+mainline-Dinner.usmap');
const modId = 'AnimationCreatorIdleTest';
const generated = path.join(workspace, 'Generated');
const suitPath = path.join(generated, 'NativeSuitGuiProjects', `${modId}.native-suit-project.json`);
const modPath = path.join(generated, 'NativeSuitModProjects', `${modId}.native-suit-mod-project.json`);
const settingsPath = path.join(build, 'Batcomputer.settings.json');

for (const file of [sourceSuit, sourceMod, mapping, path.join(gameContent, 'Characters/LEGOfig/SKEL_LEGOfig.uasset')]) {
  if (!fs.existsSync(file)) throw new Error('Missing test prerequisite: ' + file);
}
for (const file of [suitPath, modPath, settingsPath]) {
  if (fs.existsSync(file)) throw new Error('Refusing to overwrite an existing file: ' + file);
}

const suit = JSON.parse(fs.readFileSync(sourceSuit, 'utf8'));
suit.slotId = modId;
suit.displayName = 'Batman — Animation Creator Idle Test';
suit.description = 'Disposable Batman donor suit with three authored idle motions. Test only.';
suit.pawnTag = `Pawns.Playable.Batman.${modId}`;
suit.packageBaseName = modId + '_P';
suit.targetPackages = {
  playable: `/Game/Mods/${modId}/Characters/BP_${modId}_Playable`,
  cutscene: `/Game/Mods/${modId}/Characters/BP_${modId}_Cutscene`,
  dcmd: `/Game/Mods/${modId}/Characters/DA_DCMD_${modId}_Playable`
};
suit.customCharacter = null;
suit.partGrafts = [];
suit.changes = [];
suit.useCustomArchetype = true;
suit.animationOverrides = [];
suit.animationSlotOverrides = [];
suit.locomotionOverrides = [
  ['A_Idle_Batman', 'A_IdleBreathingTest'],
  ['A_Idle2_Batman', 'A_IdleLookAroundTest'],
  ['A_Idle3_Batman', 'A_IdleWaveTest']
].map(([donor, replacement]) => ({
  donorSequence: donor,
  donorSequencePackage: `/Game/Animation/LEGOfig/Batman/Movement/${donor}`,
  replacementSequence: replacement,
  replacementPackage: `/Game/Mods/${modId}/Animations/${replacement}`
}));

const mod = JSON.parse(fs.readFileSync(sourceMod, 'utf8'));
mod.modId = modId;
mod.displayName = suit.displayName;
mod.description = suit.description;
mod.packageBaseName = modId + '_P';
mod.contentRoot = `/Game/Mods/${modId}`;
mod.stringTablePackage = `/Game/Mods/${modId}/Localization/ST_${modId}.ST_${modId}`;
mod.previousModIds = [];
mod.suits = [{
  suitProjectPath: `Generated\\NativeSuitGuiProjects\\${modId}.native-suit-project.json`,
  suitId: modId,
  enabled: true,
  menuOrder: 940
}];

const settings = {
  ProjectRoot: workspace,
  ExtractedContentRoot: gameContent,
  UsmapPath: mapping,
  UnrealEngineRoot: 'C:/Program Files/Epic Games/UE_5.6',
  GamePaksRoot: 'C:/Program Files (x86)/Steam/steamapps/common/LEGO Batman - Legacy of the Dark Knight/LEGOBatmanLotDK/Content/Paks'
};
for (const file of [suitPath, modPath]) fs.mkdirSync(path.dirname(file), { recursive: true });
fs.writeFileSync(suitPath, JSON.stringify(suit, null, 2));
fs.writeFileSync(modPath, JSON.stringify(mod, null, 2));
fs.writeFileSync(settingsPath, JSON.stringify(settings, null, 2));
console.log('suit=' + suitPath);
console.log('mod=' + modPath);
console.log('settings=' + settingsPath);
