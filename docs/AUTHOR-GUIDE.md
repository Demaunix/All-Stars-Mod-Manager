# Author guide: making a mod that can say what it needs

This guide is for people who build loader mods for Sonic & SEGA All-Stars Racing and want players (and the manager, and the checker) to know what the mod needs and what it cannot be combined with. The precise rules are in [`PACKAGE-FORMAT.md`](PACKAGE-FORMAT.md); this is the practical version.

**You do not have to use any of this.** A mod that is only a folder with `mod.ini`, `plan.json` and a runtime DLL works exactly as before, with or without the manager. The description file is an optional extra that lets tools give players straight answers instead of "unknown". Players can always copy your folder into `mods\` by hand.

## 1. What a mod folder contains

```
My Mod\                    any folder name; remember it decides start order (section 6)
  mod.ini                  required
  plan.json                required
  runtime\MyMod.dll        required, 32-bit (x86), exports SsasrModInitialize and SsasrModRollback
  payloads\...             files named by plan.json
  allstars-mod.json        optional: the description
```

A complete, tiny, synthetic example that passes the checker is in [`../examples/minimal-mod`](../examples/minimal-mod); a second one that uses requirements and load order is in [`../examples/minimal-mod-addon`](../examples/minimal-mod-addon). Neither contains game data, and their runtime does nothing (its C source is in `examples/runtime-src`).

## 2. The required files

`mod.ini`:

```ini
[mod]
schema=1
id=minimal-example
name=Minimal Example
version=1.0.0
plan=plan.json
plan_sha256=<sha-256 of plan.json, lowercase>
runtime=runtime/example-runtime.dll
runtime_sha256=<sha-256 of the DLL, lowercase>
```

`plan.json` (the smallest valid plan changes nothing):

```json
{"schema":1,"archive_groups":[]}
```

Compute the two hashes with `tools\Update-ModHashes.ps1 -ModFolder "My Mod"`: it recomputes both SHA-256 values and rewrites the two `*_sha256` lines of `mod.ini` (and nothing else). Run it again whenever you rebuild the DLL or edit the plan — the loader refuses a mod whose pins are stale, and **it then starts the game with no mods at all**, not just without yours.

Things the loader is strict about (and the checker mirrors): hashes are lowercase; paths are relative with no `..`; no folder in the chain is a link/junction; the `id` is unique among all mods; plan files are plain JSON (no comments, no BOM). Details: PACKAGE-FORMAT.md sections 2 and 3.

## 3. The description file (optional)

`allstars-mod.json`, in the same folder as `mod.ini`. Start with this and add only what is true:

```json
{
  "manifest_schema": "allstars-mod/1",
  "id": "minimal-example",
  "name": "Minimal Example",
  "version": "1.0.0",
  "summary": "A synthetic mod that does nothing; used to show the file layout."
}
```

Rules to remember:

* **Plain JSON as the plan is plain JSON**: no comments, no trailing commas, no byte-order mark (Windows PowerShell 5 and Notepad can add one — save as "UTF-8" *without BOM*), numbers are whole and unsigned (write a decimal limit as text: `"min": "-1.5"`), no `😀`-style surrogate escapes (type the character directly).
* `id` must equal the `id` in `mod.ini`. IDs: use lower-case letters, digits and hyphens, short, and never reuse one for a different mod; the loader accepts any text but compares it exactly.
* `version` should equal the `version` in `mod.ini`. Use the version grammar of section 10 of the format document (`1.2.3`, `1.2.3-beta.2`).
* If a rule is wrong in a way the tools can detect (a range they cannot read, a field of the wrong type), the tools **do not skip it**: they say they could not confirm your mod. A single typo can therefore stop the manager from switching your mod on. Run the checker (section 8) before you publish.

## 4. Saying what you need and what you cannot share the game with

All of these go in `allstars-mod.json`. Every one is optional.

**Another mod must be present** (and in a version range):

```json
"requires": [ { "id": "base-pack", "versions": ">=1.0.0 <2.0.0" } ]
```

**Do not combine with** (a mod, or only some versions of it):

```json
"conflicts_with": [ { "id": "old-base-pack", "versions": "*", "reason": "the same characters are in both" } ]
```

**Version ranges** are `>=`, `<=`, `>`, `<`, `=` (or a bare version), separated by spaces or commas, all must hold. They are strict: `1.x`, `^1.0`, `~1`, `>=1 || <0.5` are *invalid*, not "close enough". Pre-releases sort below their release (`1.0.0-rc.1 < 1.0.0`). `*` means any version.

**Tested together** — only if you really did test, and say where:

```json
"composes_with": [ { "id": "base-pack", "versions": "1.0.0", "evidence": "offline fixture test, see tests/report.txt" } ]
```

This lowers the "native code interactions are unknown" note for that pair; it is treated as *offline* evidence and is never shown as "works in the game". Two mods that both run their own native DLL are reported by the checker as **insufficient information** unless every pair is recorded like this — that is the honest state, not an error.

**Sole use of something** (`capabilities_exclusive`) and **which game code you patch** (`touches.native_patches` with `va` and `length`) let tools detect that two mods fight over the same thing. If you patch game code, list every range as `{ "id": "...", "va": "0x545434", "length": 8 }`; a placeholder instead of an address is shown as "could not be confirmed".

**Extra files outside your folder** (`loose_assets`) must be listed with a hash and a size, under `Resource/`; the manager will only create a file that is absent and never overwrites one.

## 5. Saying what you need from the loader

If your runtime depends on a feature of the loader, say which one instead of hoping:

```json
"must_understand": ["loader-requirements/1"],
"loader": {
  "host_abi": 1,
  "capabilities": ["xpac-member-merge", "runtime-init-before-game-crt"]
}
```

* `host_abi` is the host interface version your runtime was built against (`SsasrHostApiV1` has version 1).
* `capabilities` are names from the loader profile of the build you tested with (`AllStarsModCheck --list-loaders`). A player whose loader is not known to provide one gets a definite "needs a different loader"; a player whose loader build is not recognised at all gets "could not be confirmed" (and the manager will not switch the mod on). That is deliberate: it is better to say so than to start a game that will fail.
* `host_sha256` / `proxy_sha256` are exact build pins (lists of SHA-256 values of `ChaoGarage.dll` / `dinput8.dll`) for the rare case where only specific builds are acceptable. Prefer capabilities; pins make every later loader build look incompatible.
* `"target": { "executable_sha256": "<sha-256 of the game .exe you tested with>" }` records the game build. A different build is a warning, not a block.

`must_understand` is for any restricting feature that older tools might not know. Tools that do not know a listed name refuse to say "compatible" instead of ignoring the rule.

There is no "newest version" rule. A mod states what it needs; an older mod whose needs are met keeps working, and the tools do not know (and do not need to know) what is newest anywhere.

## 6. Start order

The loader starts runtimes in **folder-name order**: compared character by character, case-sensitive (`0…9`, then `A…Z`, then `a…z`), with the mod whose ID is `metal-sonic` always first. It has no priority setting. If your mod must start after another one (for example because it patches code that the other one installs), say so:

```json
"load_order": { "after": ["all-stars-roster-pack"] }
```

and give your folder a name that sorts later (`Egg Hangar` sorts after `All-Stars Roster Pack`; `0 Egg Hangar` does not). The checker compares your declaration with the real folder names and tells the player which folder to rename if it is wrong. Neither the manager nor the checker renames anything for you or for the player.

## 7. Publishing

* Zip the **folder** (so a player can unzip into `mods\`): `YourMod.zip` containing `My Mod\mod.ini`, … or a `mods\My Mod\…` tree if you also ship extra files.
* **Do not put `ChaoGarage.dll` or `dinput8.dll` in your mod package.** The loader is a separate download that players install once (its releases page: https://github.com/Demaunix/ChaoGarage/releases); a mod package holds only its own folder under `mods\` (plus the extra files its description lists). Link the loader's download page from your mod's page (ready-made requirements text: the loader repository's `docs/MOD-AUTHORS.md`) and say which loader build(s) you tested with; say what you did and did not test in the game. If your mod changes an archive member that another mod also changes, ChaoGarage 1.2.0 can combine the two when your plan says how (`variants` made for the other mod's exact content, or `optional`; see `docs/PACKAGE-FORMAT.md` section 3.1 and the loader's `docs/MOD-AUTHORS.md`); then require `xpac-member-variants` and/or `xpac-member-optional`, and check the pair with `--add` on a game folder that has the other mod. If your mod needs ChaoGarage 1.1.0 or later, require `startup-requirements-check` in your `loader` block: the 1.1.0 loader, the manager and the checker then agree, and the loader repository's `examples/mod-runtime-check` shows how your runtime can also stop with a clear message under an older loader.
* Ordinary download-and-replace updates are fine: the manager adds a new version by hand (`Add mod…`), the old one can stay installed (switched off) and a player can go back.
* Do not put game files in your package unless you have the right to. The tools never need them: plans pin **hashes** of the original files, not the files.
* Never tell players to delete your description file to get past a warning; fix the file instead. Players who do not want the tools can still copy your folder by hand.

## 8. Check before you publish

```
AllStarsModCheck "path\to\My Mod" --loader chaogarage-1.2.0
AllStarsModCheck "path\to\My Mod" --loader chaogarage-1.1.0
AllStarsModCheck "path\to\My Mod" --loader ssasr-host-1
AllStarsModCheck "path\to\game folder" --add "path\to\My Mod"
```

The first checks your package alone against a loader profile you name (the result says the loader was *assumed*). The second checks it together with what a player has installed, without installing anything. Exit code 0 means every declared rule is met; 1 means something could not be confirmed (always the case for two native mods without recorded pair tests); 2 is a definite problem. `--json` gives the same result for scripts. See [`COMPATIBILITY-CHECKER.md`](COMPATIBILITY-CHECKER.md).

## 9. Checklist

- [ ] `mod.ini` pins match the plan and the DLL (`tools\Update-ModHashes.ps1`).
- [ ] The DLL is 32-bit, exports `SsasrModInitialize` and `SsasrModRollback`.
- [ ] The folder name sorts where it must; `load_order` says where, if it matters.
- [ ] Description is plain JSON without a byte-order mark; `id` and `version` match `mod.ini`.
- [ ] Ranges use only the grammar in PACKAGE-FORMAT.md section 10.
- [ ] `requires`, `conflicts_with`, `loader` list real needs, not hopes.
- [ ] `composes_with` only for pairs you tested, with evidence.
- [ ] The checker gives no "could not be confirmed" that you did not intend.
- [ ] Your page says what was and was not tested in the game.
