# The compatibility checker (`AllStarsModCheck`)

A small console program that tells you, in plain language, whether the mods in a folder agree with each other and with your mod loader. It is **separate from the manager**: you can run it without opening or installing the manager, offline, on a game folder, a mods folder, or a single mod you have just downloaded.

* It **reads files only**. It never runs or loads a mod DLL, never writes to the game folder, settings or saves, never contacts a network, and creates no files (not even a cache).
* The manager uses the same rules (the same code): a mod set you check by hand gets the same answer the manager would give for the same set.
* It is a *file* check. It is not a test in the game and it cannot see problems between two native mods that nobody declared.

## Run it

Needs the .NET 10 runtime (the plain "Runtime", not necessarily the Desktop one). In the folder with `AllStarsModCheck.exe`:

```
AllStarsModCheck "C:\Games\Sonic & SEGA All-Stars Racing"
AllStarsModCheck "D:\Downloads\Some Mod"  --loader ssasr-host-1
AllStarsModCheck "C:\Games\Sonic & SEGA All-Stars Racing" --add "D:\Downloads\Some Mod"
AllStarsModCheck "C:\Games\Sonic & SEGA All-Stars Racing" --json > report.json
```

| input | what it checks |
|---|---|
| a **game folder** (it holds the game's `.exe`) | the mods the loader would start from its `mods` folder, the real loader files (by hash), the plans against your real game files |
| a **mods folder**, or a folder of mod folders | the same mods, but no loader and no game files: say which loader to assume with `--loader` |
| **one mod folder** (it holds `mod.ini`) | that package alone |

Options: `--add <folder>` (repeatable) checks a mod that is not installed yet as if it were added; `--as game\|mods\|package` overrides detection; `--loader <profile>` assumes a named loader build when no game folder is given (`--list-loaders` shows them; the output says the loader was *assumed*); `--loader-profiles <file>` adds loader builds you describe yourself; `--quick` skips hashing mod files and game files; `--deep` additionally rebuilds each changed archive the way the loader does (slow, reads whole archives); `--json`; `--ascii` (no arrows or typographic dashes); `--help`; `--version`.

By default the checker hashes every mod file and the game files each plan replaces, which can take a minute for large mods (a progress line goes to the error stream, never into the result). `--quick` still checks the manifest, plan and runtime pins and everything declared.

A `.zip` must be unpacked first; the checker will say so. (A path ending in a backslash inside quotes, `"C:\Games\Some Mod\"`, is accepted although Windows hands it over with a stray quote.)

## The three answers

| result | exit code | meaning |
|---|---|---|
| **DECLARED COMPATIBLE** | 0 | Every rule the mods declare is met, the file facts are fine, the loader is recognised (or was named). This is **not** a game test. |
| **INSUFFICIENT INFORMATION** | 1 | No definite problem was found, but something could not be confirmed: an unrecognised or missing loader, two native mods nobody tested together, a rule the tool cannot read, a mod built for another game build, … Each is listed under "Could not be confirmed". |
| **INCOMPATIBLE** | 2 | At least one definite problem — for example the game's loader would refuse the set, or a requirement is not met. Listed under "Definite problems". Unconfirmed items are still listed beside it. |
| (input error) | 3 | The folder or a file you named could not be read. |
| (usage error) | 64 | The command line was wrong. |
| (unexpected error) | 70 | A bug; nothing was changed. Please report it with the folder layout that triggered it. |

"Definite" is a statement about the files and declarations; "could not be confirmed" is a statement about missing or unreadable information. They are never mixed: a rule that cannot be read is never reported as met.

Every finding says whether **the game's own loader enforces** the check (it would refuse the whole mod set) or whether it is a **rule applied by these tools** (declared by a mod, or a static check); which of those rules ChaoGarage 1.1.0 and later also enforce at game start is listed in `PACKAGE-FORMAT.md`, section 12. Mods that are in the folder but are not loader mods (no `mod.ini`: another tool's folders, research notes) and switched-off mods are listed under "Left alone". The checker does not examine, convert or change them — a different tool's `mod.ini` *inside* the mods folder, however, makes the real loader reject everything, and is reported as such.

## What is checked

* **Loader layer** (same as the loader): manifest fields and pins, plan format and hashes, payload hashes and sizes, original-file hashes against your game files, links in the mods folder, unique IDs, shared archive members / whole files / shader caches.
* **Runtime DLLs**: read as bytes only — must be an x86 DLL exporting `SsasrModInitialize` and `SsasrModRollback`. They are never loaded.
* **Declared rules**: `requires`, `conflicts_with`, `capabilities_exclusive`, overlapping native patch ranges, loose-asset collisions, `load_order` against the real start order, `loader` needs against the real loader hashes and profile, `target` game build.
* **Packages to add** (`--add`) are examined individually even when their folders have the same name (they are told apart by position); a folder given twice counts once; two packages that would be installed under the same folder name, or one named like an installed folder, are reported as a definite problem (a folder holds one mod).
* **Words-only conflicts** a mod declares (a `conflicts` entry whose `with` is not a mod id) are listed as "could not be confirmed": the checker cannot compare free text with your mods, and it does not pretend that nothing was said.
* **Fail closed**: a description the tool cannot read (not plain JSON, a newer format, an unknown required feature, a field of the wrong type, a version range outside the grammar) is reported as *could not be confirmed*, never as "no rules". The format is in [`PACKAGE-FORMAT.md`](PACKAGE-FORMAT.md).
* **Loader recognition** is by SHA-256 of `ChaoGarage.dll` and `dinput8.dll` against the profile table. A version number in a file name or a description does not count.

## What is not checked

Behaviour in the game; interactions between native DLLs that no one declared; whether a file is authentic (a hash only shows it is the file the manifest names); other injectors or plug-ins in the game folder; newer versions of anything (the checker is offline and does not know what exists elsewhere).

## Output

Text for people; `--json` for scripts. JSON schema `allstars-check-report/1`:

```json
{
  "schema": "allstars-check-report/1",
  "tool": "AllStarsModCheck 1.3.0",
  "verdict": "declared-compatible | insufficient-information | incompatible",
  "exit_code": 0,
  "input_kind": "game-folder | mods-folder | package",
  "summary": "…",
  "executed_mod_code": false,
  "modified_anything": false,
  "file_hashes": "full | full-with-archive-rebuild | skipped",
  "loader": { "knowledge": "verified | assumed | unrecognized | absent | not-inspected", "profile": "…", "profile_source": "built-in | user-supplied", "host_sha256": "…", "proxy_sha256": "…", "game_executable_sha256": "…" },
  "start_order": ["folder names in the order the loader starts them"],
  "packages": [ { "folder": "…", "state": "active | would-be-added | switched-off | unmanaged", "id": "…", "name": "…", "version": "…", "description_file": "…", "runtime": "…", "note": "…" } ],
  "findings": [ { "impact": "blocker | unknown | note", "code": "…", "title": "…", "detail": "…", "enforced_by": "loader | tools", "packages": ["folder names"] } ],
  "not_checked": ["…"]
}
```

Reports contain folder names but no absolute paths, user names or times. One rule applies to every report field and every error line: paths under the checked folders are shown relative; the account name (and the profile folder name, if different) becomes `<user>`, also inside a path or a web address; any other absolute Windows path becomes `<path>` (drive-letter paths with `\` or `/`, UNC paths whatever letters the server and share use, the `\\?\` and `\\.\` forms, `file:` addresses; a quoted path runs to its closing quote even with spaces or apostrophes in it — for single quotes, to the last closing quote on the line); every file the checker reads lies under a checked folder, so operating-system messages about those files are made relative exactly, and the pattern rules are a fallback for any other text; other web addresses such as a mod's `https://` homepage are kept unchanged. A usage error never repeats what was typed after an unknown option, only a plain option name. A text built to be too slow to check (only adversarially constructed text; checking is otherwise linear) is replaced as a whole by `<text withheld: too long to check for private paths>`. Text a mod author wrote is free prose: an unquoted path with spaces in it can leave its later words visible, so this is a filter for machine details, not an anonymiser for arbitrary text. Two runs on the same input give identical text. Finding `code`s are stable identifiers (for example `duplicate-id`, `shared-archive-member`, `shared-archive-member-two-versions`, `shared-archive-member-unconfirmed`, `shared-member-combined` (a note), `missing-requirement`, `load-order`, `loader-requirement`, `loader-requirement-unconfirmed`, `unreadable-rule`, `target-build`, `native-unknown`, `loader-unrecognized`, `sidecar-unreadable`, `sidecar-schema`, `runtime-exports-missing`, `manifest-foreign`, `payload-hash`, …).

## Exit codes in scripts

```
AllStarsModCheck "%GAME%" --quick
if errorlevel 2 goto problem     rem definite problem
if errorlevel 1 goto unsure      rem could not be confirmed
```

(`errorlevel N` means "N or higher", so test the larger code first.)

## Loader builds the checker knows

`AllStarsModCheck --list-loaders` prints the built-in table (one build today: `ssasr-host-1`, with the hashes and the capability list). A build that is not in the table is reported as unrecognised. If you *know* your build and want mods that declare loader needs to be evaluated against it, describe it in a profile file (same format, schema `allstars-loader-profiles/1`) and pass `--loader-profiles file.json`; the manager reads the same file from its `data\loader-profiles.json`. Such an entry is your statement, not evidence: the text output and the JSON (`profile_source`) say when the loader was described by a profile file you supplied.
