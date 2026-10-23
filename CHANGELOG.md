# Changelog

## 1.3.0

**Understands ChaoGarage 1.2.0, where two mods may share an archive member.** Made for the Egg Hangar 1.2.0 release on GameBanana: with the current Steam Multiplayer, Metal Sonic and All-Stars Roster Pack mods installed, 1.2.0 of these tools reported "INCOMPATIBLE" although the game starts every mod.

* New: the loader profile `chaogarage-1.2.0` (ChaoGarage 1.2.0, https://github.com/Demaunix/ChaoGarage/releases/tag/v1.2.0): both tools recognise that pair by its two SHA-256 values and know its capabilities, including the new `xpac-member-variants` and `xpac-member-optional`.
* New: plan operations may carry `variants` (a version of the member made for another mod's exact content) and `optional` (gives way to another mod's change). The conflict check now combines such members exactly as the loader does: a fitting variant of either mod first; then an optional change gives way (the later mod's first, in folder-name order); otherwise the mods still cannot be used together. Two mods that each offer a different version made for the other's content are refused, and a third mod combines with a variant-made combination only through a variant made for that combined content. Combined members are listed in a note, like the loader's own log.
* With ChaoGarage 1.1.0 or older the rule is unchanged (those loaders ignore the new fields). When the loader is not recognised, a pair only 1.2.0 can combine is reported as "could not be confirmed".
* Plans are read as the 1.2.0 loader reads them: a wrong type in `optional` or `variants` makes the plan invalid, and every variant's file is checked like any other payload file, even when it would not be used.
* Tests: one test for each way a shared member is resolved, run through the rules and through the checker; the shared loader test cases now come from ChaoGarage 1.2.0. Checked on a real game folder with ChaoGarage 1.2.0 and nine mods: the members the checker reports as combined are exactly the 41 the loader logged as combined at game start. Not tested: the manager's window (no window code changed).

## 1.2.0

**Mods stay usable without the manager, and compatibility now has a documented, checkable format.**

* New: **`AllStarsModCheck`**, a separate offline console program that checks a game folder, a mods folder or one mod with exactly the manager's rules; text or JSON; exit codes 0 (declared compatible), 1 (insufficient information), 2 (definite problem), 3 (input error), 64 (usage). It reads files only and never loads a mod DLL.
* New: a **stable, documented package contract** (`docs/PACKAGE-FORMAT.md`) built on the existing `mod.ini` / `plan.json` and the optional `allstars-mod.json`: explicit `manifest_schema`, `must_understand`, `loader` requirements (host interface version, capabilities, exact build pins), `load_order`, `target` game build, `homepage`; author guide and two synthetic example packages.
* New: **loader recognition by hash** against a profile table (`--list-loaders`); optional user-described builds in `data\loader-profiles.json`. A version label is never evidence.
* New: **static runtime check**: the runtime DLL must be an x86 DLL exporting `SsasrModInitialize` and `SsasrModRollback` (read as bytes, never loaded).
* Changed (stricter, fail closed): version ranges are parsed completely (no partial acceptance, no integer overflow, correct pre-release ordering); descriptions must be plain JSON in the loader's JSON subset; a requirement, conflict, order or loader rule that cannot be read is reported as *could not be confirmed* and, in the manager, blocks switching the mod on — it is never read as "no rule". A description for a different mod ID that carries rules is a blocker. Unreadable *footprint* declarations (native patch ranges) are warnings.
* Changed: the Core library is built from source with every release; it is no longer pinned to the 1.0.0 binary. Builds do not depend on the folder they are made in.
* Unchanged: the journal, rollback, changed-file guards, settings, profiles and library formats (a 1.2.0 data folder can be opened by 1.1.0 and the other way round; `loader-profiles.json` is ignored by 1.1.0).
* New: checker output is shareable: no absolute paths, no account name (every report field and error line is scrubbed by one rule: drive-letter, UNC — any letters — and device paths and `file:` addresses become `<path>`, while web addresses such as a mod's `https://` homepage are kept; a usage error never repeats what was typed after an unknown option); unreadable (locked) files are findings, not crashes; `--add` examines every added package even when their folders share a name and reports folder-name collisions.
* New in the manager: a header chip lists folders in `mods\` that are not loader mods (never examined or changed); user-described loader builds in `data\loader-profiles.json` are labelled as such.
* Policy: mod packages should not bundle `ChaoGarage.dll` / `dinput8.dll`; the loader is a separate download (author guide).
* New: the loader profile `chaogarage-1.1.0` (ChaoGarage 1.1.0, released separately at https://github.com/Demaunix/ChaoGarage): both tools recognise that pair by its two SHA-256 values and know its capabilities. The documentation now says which description rules ChaoGarage 1.1.0 itself enforces at game start (format, `must_understand`, `loader`, `load_order`) and which only these tools apply; the loader and the checker are tested against one shared set of cases (`src/Tests/vectors/loader-requirements.json`).
* Not included: no automatic updater, no installer, no loader installation or update, no gameplay changes.

## 1.1.0

Visual redesign (neutral gray window, tabs, table with details, themed dialogs, keyboard handling) on the unchanged 1.0.0 core.

## 1.0.0

First release: journaled apply/undo of loader mods, import from folders and ZIPs, conflict analysis matched to the loader's source.