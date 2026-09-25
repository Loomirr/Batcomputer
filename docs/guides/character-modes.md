# Character game modes

Independent characters can be configured for **Normal**, **Mayhem**, or **Both**. This is separate
from their visual base, gameplay donor and display name.

## Set the mode

1. Open **Characters** and load the character's default definition.
2. Choose **Character identity & modes**.
3. Set **Game modes** to Normal, Mayhem or Both and choose **Save details**.
4. Keep the default definition and its enabled child suits together in the mod.
5. Run **Check mod**, build and install the **whole release bundle**.
6. Fully restart the game and test in each selected mode.

Child suits inherit their parent's mode policy. The editor's **Mayhem Mode theme** only changes
Batcomputer's colors; it does not set this policy.

## Required framework

Mayhem/Both needs a compatible **LOTDKExpanded** installation with integrated **character modes
API 1**. An older framework that loads ordinary suits is not necessarily enough. Batcomputer
checks the installed capability information and matching runtime before allowing those builds.

Install the framework's complete matching release, not a loose DLL from another build.
The old standalone Mode Access helper is superseded by integrated support; follow the framework's
migration instructions and do not run duplicate mode handlers. Batcomputer's application updater
does not update your game-side framework.

Native Joker/Harley gameplay donors also require their DLC to be installed. Refresh the full
extraction and part index after installing DLC or updating mappings.

## Suits work, but my new character is missing

Check these in order:

1. Verify the new **character definition**, not just an extra suit, is enabled in the built mod.
2. Check its chosen mode and test in that mode after a cold restart.
3. Confirm the mod's registry plugin, tag configuration and pak/ucas/utoc trio all came from the same build.
4. Confirm the installed framework loaded successfully and supports the selected mode.
5. Check for a duplicate character/pawn-tag family, including conflicts with native NPC families.
6. Remove duplicate older installations of this same mod before testing the replacement.

For a character displayed as **Poison Ivy** or **Mr Freeze**, keep that display name but use a
unique [pawn-tag family](character-identity.md). A readable pak alone cannot rule out a runtime
identity conflict. Don't remake the whole character or reset your game save just to try another tag.

Mode availability is not story casting, a new voice set or a promise that every mission/co-op
context supports the selected donor. Test the actual gameplay contexts you plan to advertise.
