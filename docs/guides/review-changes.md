# Review and remove edits

Open **Home → Review** to inspect the recorded changes for the current project before building.
This is the edit history/intent, not a list of everything currently installed in the game.

## Find the edit

1. Load the suit you mean to inspect and check the project name.
2. Open Review and use the search/category controls to narrow the list.
3. In detailed-list mode, choose **All statuses**, **applied**, **staged**, or **Other**.
4. Select a row to read its complete details. **Copy selected detail** is useful for a bug report.

The detailed table shows category, target, recorded status and optionally the recorded time.
An **applied** status is not a successful in-game test; **staged** does not mean installed.
Always run **Check mod** and build after editing.

## Remove an unwanted change

1. Back up the project before undoing a large set of changes.
2. Select the exact edit and read its target/details.
3. Choose **Remove selected edit**, read the confirmation and continue if it is the intended change.
4. Let the saved stage rebuild. If replay fails, read the first named donor/part/material error.
5. Reopen the relevant category, inspect the result in 3D and check/build the mod again.

Removing a recorded edit can rebuild more than the one visible component. An unrelated older
missing donor can therefore stop the operation. Use [Update or repair a suit](update-repair-suit.md)
instead of repeatedly removing more edits to make the error disappear.

## Choose your preferred view

In **Settings → General**, the Review options let you:

- Enable **Use detailed review list**, or use compact cards instead.
- Enable **Group review by category**.
- Show or hide **review timestamps**.

These change how the history is displayed, not the mod's behavior or packaging order.
