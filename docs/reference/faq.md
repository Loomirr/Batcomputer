# Frequently asked questions

## Do players need Batcomputer?

No. Players need Loomirr's LOTDK UE4SS and the finished mod.

## Can one mod contain several suits?

Yes. One mod can contain multiple enabled suits. Its shared StringTable and registry files are
generated once.

## Can users install suit mods from different authors together?

Yes, provided every release has unique technical identities. Loomirr's LOTDK UE4SS supplies the
shared registry files, so individual suit mods do not include them.

## Does this only work for Batman?

No. Use the target character family's PawnTag and an appropriate playable donor—for example
Nightwing or Gordon—while keeping every suit ID, PawnTag, and package path unique.

## Why are visual and gameplay donors separate?

A cutscene character may have the appearance you want without complete playable behavior. The
visual base supplies the assembly; the playable donor supplies gameplay-facing data.

## Can I make a custom face?

Yes. Copy a compatible game face material and change its supported print layers. The
face mesh family still matters: standard LEGOface and SuperheroFace are not interchangeable.

## Can I import a model?

The beta supports verified OBJ static-mesh attachments. It does not cook arbitrary skeletal meshes
or transfer skeletons.

## Can I create Red Bricks?

Not in this beta. The Red Brick selector in the viewer is a read-only preview of base-game color
palettes for compatible playable characters.

## Why does a build require Unreal Engine?

The game discovers custom assets through Asset Registry data. Batcomputer includes the small writer
project, but UE 5.6 must build it on your computer.
