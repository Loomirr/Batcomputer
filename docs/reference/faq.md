# Frequently asked questions

## Do players need Batcomputer?

No. Batcomputer is an authoring tool. Players need Loomirr's LOTDK UE4SS and the finished mod
release.

## Can one mod contain several suits?

Yes. The mod is the release unit and can contain multiple enabled suits. Shared StringTable and
registry infrastructure are generated once for that mod.

## Can users install suit mods from different authors together?

Yes, provided every release has unique technical identities and uses the shared runtime installed
with Loomirr's LOTDK UE4SS. Third-party mods do not ship the shared core registry.

## Does this only work for Batman?

No. Use the target character family's PawnTag and an appropriate playable donor—for example
Nightwing or Gordon—while keeping every suit ID, PawnTag, and package path unique.

## Why are visual and gameplay donors separate?

A cutscene character may have the appearance you want without complete playable behavior. The
visual base supplies the assembly; the playable donor supplies gameplay-facing data.

## Can I make a custom face?

Yes, by cloning a compatible donor-backed face material and changing supported print layers. The
face mesh family still matters: standard LEGOface and SuperheroFace are not interchangeable.

## Can I import a model?

The beta supports verified OBJ static-mesh attachments. It does not cook arbitrary skeletal meshes
or transfer skeletons.

## Can I create Red Bricks?

Not in this beta. The Red Brick selector in the viewer is a read-only preview of base-game color
palettes for compatible playable characters.

## Why does a build require Unreal Engine?

The game discovers native custom assets through Asset Registry data. Batcomputer includes the small
writer project's source, but UE 5.6 performs that author-side registry build.
