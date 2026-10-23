# Rollback and safety

## What gets written, and where

| Location | Written by | Purpose |
|---|---|---|
| `<game>\mods\<folder>\mod.ini` ↔ `mod.ini.disabled-by-manager` | switching a mod on/off | the loader reads a folder only if `mod.ini` exists; renaming is the existing convention (no generic enable flag exists) |
| `<game>\mods\<new folder>\` | installing from the library | verified copy staged first, then one same-drive folder move |
| `<game>\Resource\Movie\...` (only `Resource\`, only when a mod declares required extra files) | install | create-if-absent; identical existing file adopted (not owned); different file blocks the install |
| `<game>\mods\<folder>\<settings>.ini` | "Save settings" | in-place replace after the old file is copied to backups |
| `<game>\mods\.allstars-manager\` | every change | `current.json` (journal of the change in progress), `history\<txn>\journal.json`, `stage\<txn>` (temporary copies), `backups\<txn>` (moved-aside content), `assets.json` (which extra files the manager created and who needs them), `lock` |
| `<manager>\data\` | the manager | `settings.json`, `profiles.json`, `library\`, `cache\`, `incoming\` (temporary unpacking), `manager.log` |

Nothing else is written: not saves, not base archives, not `ChaoGarage.dll`/`dinput8.dll`, not Steam, not the registry. Scans and previews write nothing at all (tested).

## The transaction

1. **Refuse early:** game running or unknown, unfinished earlier change, game folder changed since the preview, another manager holding the lock, any blocker in the intended set.
2. **Stage:** copy what will be installed into `stage\<txn>` with read-back hash checks, re-verify the staged copy.
3. **Journal:** write `current.json` (atomic replace) listing every step with its expected before/after state.
4. **Each step** is marked *Started* before and *Done* after it happens (journal saved each time) and re-checks its own preconditions (path in the allowed folders, no links on the way, the object still in the expected state).
   * *Replacing a file* (settings saves, the list of extra files) uses an atomic swap that keeps whatever was really there: if another program wrote to the file after the manager looked, their version is put straight back, nothing of theirs is lost, and the step counts as not done. If a second outside write lands between the two halves of the swap, the swap-back writes that newest version straight into the manager's backup folder (`*.swapback-…`, renamed `*.external-…` when the rename works) — never to a staging or temporary path that is cleaned up; only bytes proven by hash to be the manager's own are discarded. The name a replace step parks the old version under (`<file>.displaced-xxxxxx`) is written into the journal before the step starts; after a crash between the halves, the next restore moves exactly that recorded file (never deletes it, never touches look-alike files) into the same backup folder as `*.leftover`, and the journal checker refuses any parking name that is not that exact shape beside its own target. The settings editor also refuses to save if the file changed on disk after it was opened.
   * *Installs* land in their final state in one step: a package whose manifest is stored under a switched-off name is renamed to `mod.ini` inside the manager's staged copy (the library original is untouched).
5. **Post-check:** the real folder is re-scanned and re-validated exactly like the loader will see it; any mismatch rolls back.
6. **Commit:** journal marked *Committed*, filed under `history`, staging removed (backups stay).

**Failure, cancel or crash** at any point: completed steps are undone in reverse order. Undo moves things aside instead of deleting them: a folder installed by the change goes to `backups\<txn>\undo\…`, an added file likewise, a replaced file is restored from its verified backup after a copy of what is there is kept. Before undoing a step the manager checks that the object is still exactly what the step left: a renamed manifest must still hash to what was renamed, an installed folder must still have the same file list **and the same SHA-256 of every file**, an added file must still hash to what was added. If anything differs (someone added, removed or edited something, even by one byte), that step is **not** undone: recovery stops, reports it, leaves the file or folder exactly as it is, and keeps the journal so it can be retried after the situation is resolved. The only things ever deleted are the manager's own temporary staging copies.

**One manager at a time.** Apply, Recover and Undo all take the folder's lock *before* they read the journal or history, and keep it until they finish, so a second manager cannot be overwritten or acted on from a stale reading. Every write into the manager's own folder (journal, history, staging, backups) first proves it is inside that folder and reached without a link.

**The journal is not trusted blindly.** Before any write, a loaded journal (the unfinished-change record or a history record used by Undo) is validated: format version, change-id shape, that the staging/backup folders are exactly this change's own folders under `mods\.allstars-manager\`, and that every step is one the manager itself could have produced (a manifest rename inside one mod folder, an install/removal of one folder directly under `mods\`, an added or taken-back file that passes the extra-file policy under `Resource\`, a settings-file replace of a top-level `.ini`, with their backup/undo locations inside the change's backup folder). Anything else is refused and nothing is written.

After a crash the next start shows **Restore previous state**. Recovery is idempotent and safe to repeat. Leftover staging from a crash before any journal existed is just temporary data and is cleaned up.

## Evidence

`tests\results\core-tests.json` records, among others: failure **and** simulated crash injected at every journaled boundary of a rich apply (install + extra files + registry + disable + enable) with exact byte-for-byte baseline comparison and a successful retry each time; a second fault injected during every rollback step followed by recovery; cancellation at every step; the same matrix for settings saves and uninstall; edits by the user after a crash or after a finished change (added file, same-size edit, removed file — Undo and recovery refuse and preserve them); a tampered/corrupt journal or history record in 17 ways with no write outside the manager's folders; a file created by someone else between journaling and the step; junctions swapped in after the preview; game-running and unknown-status refusals for every kind of change; and the loader's own discovery code accepting the applied result.

## Doing it by hand

If you ever need to undo something without the manager: switched-on/off state is only a rename inside the mod folder; moved-aside content is under `mods\.allstars-manager\backups\<txn>\` with the same relative layout; `history\<txn>\journal.json` lists each step (`kind`, `from`, `path`, `undoPath`) in order.

## Rolling back the manager itself (1.3.0 to 1.2.0)

Version 1.3.0 changes only the compatibility rules (it knows ChaoGarage 1.2.0 and how that loader lets two mods share an archive member); the journal, backup, undo and storage code is unchanged. To go back, close the manager and run the 1.2.0 package; both read the same `data` folder and records. 1.2.0 does not recognise the ChaoGarage 1.2.0 loader files and reports mods that share an archive member as conflicting, even when that loader combines them.

## Rolling back the manager itself (1.2.0 to 1.1.0)

Version 1.2.0 adds the compatibility checks, the checker program and the stable package format; it does not change the journal, backup, undo or storage code (`Transactions/*`, `Storage/*` and `Library/*` are byte-identical to 1.1.0 apart from one added argument in the planner that carries the loader profiles). To go back, close the manager and run the previous package instead. The `data` folder (settings, profiles, library) and the game folder's `mods\.allstars-manager` records are shared by both versions, so an interrupted change or **Undo last change** can be finished by either one. The optional `data\loader-profiles.json` is ignored by 1.1.0. The one visible difference is stricter checking: 1.2.0 refuses to switch on a mod whose description declares something it cannot confirm (see PACKAGE-FORMAT.md section 9); 1.1.0 ignored such declarations.

Mods never depend on the manager: switching a mod on is a rename inside its folder, and you can do that, or copy a folder in or out of `mods\`, by hand at any time.