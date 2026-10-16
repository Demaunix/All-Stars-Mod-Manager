# Mod package format 1 (normative)

This is the stable contract between mod authors, the All-Stars Mod Manager, the manager-free checker (`AllStarsModCheck`) and any future loader build. It describes what exists today and what each rule's status is. Words in capitals follow RFC 2119 (MUST, SHOULD, MAY).

**Status of this document:** format `allstars-mod/1`, tools version 1.2.0. What is *enforced by the game's loader* is stated separately from what only *these tools* check, in section 12. Nothing here claims that mods work in the game: the tools read files and compare declarations; they never run a mod.

* Author-oriented walk-through: [`AUTHOR-GUIDE.md`](AUTHOR-GUIDE.md)
* Using the checker and reading its output: [`COMPATIBILITY-CHECKER.md`](COMPATIBILITY-CHECKER.md)
* Notes for people writing loader code: [`LOADER-INTEGRATION.md`](LOADER-INTEGRATION.md)

## 1. Two layers

A mod is a **folder** placed directly inside the game's `mods` folder.

| Layer | Files | Who reads it |
|---|---|---|
| **Loader layer** (required) | `mod.ini`, `plan.json`, the runtime DLL, the payload files the plan names | the game's loader at every start; also these tools |
| **Description layer** (optional) | `allstars-mod.json` next to `mod.ini` | these tools; the loader from **ChaoGarage 1.1.0** on (only its format, `must_understand`, `loader` and `load_order`, see section 12); **loaders before 1.1.0 never read it** |

A folder without `mod.ini` is ignored by the loader. Players are free to copy mod folders by hand; nothing in this contract requires the manager, and a mod with no description file is simply a *legacy* mod: it is checked for everything the loader checks, and its compatibility with other mods stays *unknown* wherever it says nothing.

```
<game folder>\
  Sonic & SEGA All-Stars Racing.exe
  ChaoGarage.dll, dinput8.dll      the loader (host and bootstrap proxy)
  mods\
    <any folder name>\             one mod
      mod.ini                      required: pins the plan and the runtime
      plan.json                    required: what the mod replaces or adds
      runtime\<name>.dll           required: x86 DLL the loader loads
      payloads\...                 files named by the plan (hash and size pinned)
      *.ini                        the mod's own settings (free form)
      allstars-mod.json            optional description (this document, sections 5-11)
```

A disabled mod keeps its files but its manifest is renamed to `mod.ini.disabled-by-manager` (or any name starting with `mod.ini.disabled`); the loader ignores such a folder because `mod.ini` is absent.

## 2. File syntax

### 2.1 `mod.ini`

An INI file read by the loader with the Windows profile API, section `[mod]`:

| key | required | rule |
|---|---|---|
| `schema` | yes | exactly `1` |
| `id` | yes | 1 to 2046 characters; unique among all mod folders, compared exactly (case-sensitive) |
| `plan` | yes | relative path of the plan file (normally `plan.json`) |
| `plan_sha256` | yes | SHA-256 of the plan file, **64 lowercase** hexadecimal digits |
| `runtime` | yes | relative path of the runtime DLL |
| `runtime_sha256` | yes | SHA-256 of the runtime DLL, 64 lowercase hexadecimal digits |
| `name`, `version` | no | display name and version; the loader ignores them, these tools use them |

Paths MUST be relative, MUST NOT contain `..`, a drive letter or a link on the way, and are resolved inside the mod folder. Hash values MUST be ASCII and lowercase: the loader compares them as text against its own lowercase output, so an upper-case pin is rejected (the tools report this specifically).

### 2.2 JSON files: the loader's JSON subset

`plan.json` is read by the loader's own JSON reader. **`allstars-mod.json` MUST use the same subset**, so that any reader that understands the plan understands the description and so that this document, the checker and a native reader cannot disagree about what a file says:

* UTF-8, **no byte-order mark**, at most 16 MiB (the tools also refuse a description larger than 4 MiB).
* White space between tokens is only space, tab, CR and LF.
* **No comments** and **no trailing commas**.
* **Numbers are unsigned whole numbers** written in plain decimal digits without a leading zero (`0`, `7`, `4294967295`). No sign, no fraction, no exponent. Where a number would not fit (a decimal limit, a negative minimum) write it as a string.
* Strings: the escapes `\" \\ \/ \b \f \n \r \t \uXXXX` only; control characters below U+0020 MUST be escaped; `\u` escapes in the range D800–DFFF (surrogates, including the usual pair for characters above U+FFFF) are **not** accepted — write such characters directly as UTF-8.
* Object member names MUST be unique within their object.
* Nesting at most 33 levels (a top-level value is level 0; level 32 is the last accepted).

The checker applies this subset with a port of the loader's parser (`StrictJson`) before it reads a description. A description outside the subset is **unreadable**; it is never "partly read" (see 9). A survey of every distinct description file that existed when 1.2.0 was written found that all of them already conform.

## 3. The plan (`plan.json`)

```json
{ "schema": 1, "archive_groups": [ <group>, ... ] }
```

`schema` MUST be `1`. An optional `"mod_id"` string, if present, MUST equal the manifest `id` (the tools warn otherwise; the loader ignores it). Each group has a `path` (a game-relative file under `Resource/` or `Scripts/`, or `ShaderCache/*.rtsc` for shader caches; each logical file may appear once per plan) and a `format`:

| `format` | other required fields | meaning |
|---|---|---|
| `xpac` | `base_sha256`; `operations[]` of `{key, kind: "replace"\|"add", base_stored_sha256 (replace only), payload, payload_sha256, stored_size, entry_flags (optional, 0)}`; `expected_composed_sha256`, `expected_composed_size`, `expected_member_count` | change or add members of a game archive. `key` is a number or 8 hexadecimal digits. Several mods may change **different** members of the same archive. |
| `file` | `base_sha256`, `payload`, `payload_sha256` | replace a whole file; only one mod may do so, and not together with an `xpac` group on the same file |
| `copy` | `payload`, `payload_sha256` | place a copy of a payload at the path |
| `rtsc_merge` | `payload`, `payload_sha256`, `key_size` | shader-cache seed; each cache file belongs to one mod |

`base_*` hashes pin the **original game files** the mod was made against; the loader refuses a mod whose base hash does not match the installed game file. Payload hashes are lowercase SHA-256. An unknown `format` is refused by the loader. These rules are mirrored by the tools and compared with the loader's own code in a private differential test (see the manager README, "Evidence").

## 4. The runtime DLL

`runtime` is loaded by the loader into the 32-bit game process and MUST:

* be an **x86 (32-bit) DLL**;
* export, by exactly these undecorated, case-sensitive names, `SsasrModInitialize` and `SsasrModRollback` (the host interface version 1 signatures are in the loader's `mod_api.h`).

The tools check this **statically**: they read the DLL's headers and export tables as bytes (they never load, map or run it). A name only counts when a lookup by name would find it: the name table, the ordinal table and the function table are followed, and the entry must hold a code address inside the image. A runtime that is not an x86 DLL, that lacks either name, or whose entry has address 0, an ordinal outside the table or an address outside every section, is a definite problem that the loader would also hit. An export that is a *forwarder* (pointing into another module) is not followed and is reported as "could not be confirmed"; so is a table that cannot be read at all. (Not checked: that the address lies in executable memory or that the function does anything sensible.)

## 5. The description file `allstars-mod.json` (format `allstars-mod/1`)

The file is an object. **Every field is optional.** A mod without the file is a valid legacy mod. Recommended minimum for a mod that publishes any compatibility statement:

```json
{
  "manifest_schema": "allstars-mod/1",
  "id": "my-mod",
  "name": "My Mod",
  "version": "1.0.0"
}
```

| field | type | status | meaning |
|---|---|---|---|
| `manifest_schema` | string | SHOULD | exactly `allstars-mod/<major>` or `allstars-mod/<major>.<minor>`. Absent = legacy file, read as `allstars-mod/1`. A different major, or any other text, makes the whole file unreadable for compatibility purposes (section 9). |
| `must_understand` | list of strings | MAY | feature names a reader must know to apply this file correctly; a name the reader does not know makes the file unreadable (section 11). Known names: `loader-requirements/1`, `load-order/1`, `target-build/1`. |
| `id`, `name`, `version`, `summary`, `kind`, `redistribution` | strings | MAY | display text. `id` MUST equal the manifest `id` when present: a description for another mod that contains rules is a blocker, one that is purely descriptive is a warning. `version` SHOULD equal the manifest `version` (a difference is a warning). |
| `homepage` | string | MAY | an `https://` address of the mod's page, shown as text only; no tool opens or contacts it. Anything else is ignored. |
| `status` | object | MAY | `status.stage` is mapped to friendly wording; unknown stages are shown as "Status: …", never as "working". |
| `installable` | bool | MAY | `false` marks a research note; the manager will not switch it on. Not a bool = unreadable (blocking). |
| `requires` | list of relations | MAY | other mods that must be active (section 6.1). |
| `conflicts_with` | list of relations | MAY | other mods that must not be active together (6.2). |
| `conflicts` | list of `{with, effect}` | MAY | free-text conflicts; only an exact `<id>` or `<id> <op> <version>` in `with` is machine-readable. Anything else cannot be compared with the other mods, so it is reported as "could not be confirmed" (the checker lists the text; the manager shows it as a warning line). |
| `composes_with` | list of relations with `evidence` | MAY | pairs the author tested together (6.3). |
| `capabilities_exclusive` | list of strings | MAY | names of resources the mod claims alone (6.4). |
| `touches.native_patches` | list of `{id, va, length}` | MAY | address ranges of game code the runtime changes (6.5). |
| `loose_assets` | list of `{target, sha256, bytes, source_in_package, role}` | MAY | extra files that must exist outside the mod folder (6.6). |
| `package_completeness` | object | MAY | `mod_folder_alone_is_complete` (bool), `required_outside_mod_folder` (text). |
| `loader` | object | MAY | what the mod needs from the loader (section 7). |
| `target` | object | MAY | `executable_sha256`: game executable build(s) the mod was made and tested with (6.7). |
| `load_order` | object | MAY | `after` / `before` lists of mod IDs (section 8). |
| `persistence` | object | MAY | `statement`, `disable_note`: what stays in saves; shown before switching the mod off. |
| `config_schema` | list | MAY | typed settings for the manager's editor; `min`/`max` are numbers or strings such as `"-1.5"`. |
| `artifacts` | list of `{path, sha256, bytes}` | MAY | extra integrity listing (a mismatch is a warning). |
| `human_gameplay_requirements` | list (or one string) | MAY | things still to test in the game; shown as such. |

Unknown fields are **ignored** (forward compatibility); a new field that changes the meaning of a compatibility rule MUST be introduced either under a new major `manifest_schema` or named in `must_understand` (section 11).

All text is untrusted: control characters are stripped and lengths capped before display, and nothing in the file is executed, opened or used as an install destination except through the rules in 6.6.

## 6. Relations and declarations

A **relation** is an object `{ "id": "<mod id>", "versions": "<range>", "reason": "<text>" }`. `versions` defaults to `*`. For convenience a relation may also be written as just the id string, and a list of one may be written as the bare object or string (existing packages use the bare object). Anything else is unreadable: an entry without an id, a non-text `versions`, a range outside the grammar of section 10.

### 6.1 `requires`
The named mod MUST be in the active set and its version MUST satisfy the range.
* not active → **definite problem** (declared rule, not loader-enforced);
* active, version outside the range → definite problem;
* active but its version is missing/not comparable, or the range is the literal `UNKNOWN` → **could not be confirmed** (warning in the manager);
* the entry itself is unreadable → **could not be confirmed, blocking** in the manager (never "no requirement").

### 6.2 `conflicts_with`
If the named mod is active and its version is inside the range the combination is a definite problem. Outside the range: no finding. Mod active but its version cannot be compared with the range: **could not be confirmed, blocking** (a clash cannot be ruled out). Unreadable entry: could not be confirmed, blocking.

### 6.3 `composes_with`
Reassurance only: `{id, versions, evidence}` says the author checked the two together **offline** (never "in the game"). It lowers the "native interactions unknown" note for that pair; it can never make a combination pass a rule. An unreadable entry adds no reassurance.

### 6.4 `capabilities_exclusive`
Two active mods claiming the same name (case-insensitive) are a definite problem. Unreadable entries are "could not be confirmed" warnings.

### 6.5 `touches.native_patches`
`va` is a hexadecimal address string (`"0x545434"`) or a whole number, `length` a whole number above 0. Two active mods with overlapping ranges are a definite problem. An entry without a usable range (for example the placeholder text some packages use) cannot be compared; it is reported as "could not be confirmed" (warning), because overlap with other mods then cannot be ruled out.

### 6.6 `loose_assets`
Files the mod needs outside its own folder. `target` MUST be a relative path under `Resource/`, not a link, not an executable or script type (`.exe .dll .bat .cmd .ps1 .py .xpac .sbn …`); `sha256` lowercase; `bytes` a whole number up to 4294967295. The manager only creates a file when it is absent and never overwrites; a different existing file blocks the mod. An entry that cannot be read is blocking (a package must not look complete when a required file is not understood).

### 6.7 `target`
`executable_sha256` (string or list) names the game executable build(s) the mod was made for. It is **descriptive**: a different or uncomparable build is a "could not be confirmed" warning, not a block, because the mod's own code decides whether it works.

## 7. Loader requirements (`loader`)

```json
"loader": {
  "host_abi": 1,
  "mod_ini_schema": 1,
  "plan_schema": 1,
  "capabilities": ["xpac-member-merge"],
  "host_sha256": ["<64 lowercase hex>", "..."],
  "proxy_sha256": ["<64 lowercase hex>"]
}
```

All members are optional; an empty object still counts as a declaration. Meaning: the mod needs a loader whose host interface version is `host_abi` (exact match), that reads `mod.ini` version `mod_ini_schema` and plan version `plan_schema`, that provides every named **capability**, and (if the hash lists are present) whose `ChaoGarage.dll` / `dinput8.dll` is one of the listed builds.

Evaluation uses the real files, never a label:

1. The tools hash the installed `ChaoGarage.dll` and `dinput8.dll`. If **both** hashes equal a known **loader profile** (section 12.2) the build is *recognised* and the profile's interface versions and capability list answer the questions.
2. Hash pins (`host_sha256`, `proxy_sha256`) are compared directly with the real hashes whenever the loader was inspected, recognised or not. A pin that does not match is a definite problem.
3. A loader that is not recognised, is missing, or was not inspected cannot answer capability questions: the result is **could not be confirmed** — blocking in the manager when the build is unrecognised, a warning when the loader is missing or not inspected. Nothing is assumed to be met, and a version number a person typed somewhere is never used instead.

A mod that declares no `loader` block is unaffected by the loader's state (legacy behaviour).

## 8. Load order (`load_order`)

```json
"load_order": { "after": ["all-stars-roster-pack"], "before": [] }
```

The shipped loader has **no priority setting**. It starts mod runtimes in this order: folder names compared **ordinally** (character code by character code, case-sensitive; digits, then capital letters, then lower-case letters), except that the mod whose ID is exactly `metal-sonic` always starts first. The only way to change the order is to rename a mod folder.

`after` / `before` entries name mod IDs. The rule is checked against that real order for mods that are in the set; a named mod that is not in the set imposes nothing (pair it with `requires` if presence matters). A violated constraint is a definite problem whose text names both folders and says which has to sort later. Nothing can start before `metal-sonic`. Two descriptions that ask for opposite orders are reported as contradictory. The manager never renames folders on its own.

## 9. Unreadable, unsupported and future files: fail closed

A statement that could change a compatibility answer is **never** treated as absent because it could not be read:

| situation | result |
|---|---|
| description outside the JSON subset, too large, or duplicate member names; or the name `allstars-mod.json` is a folder, a link, or cannot be inspected or read (only "not found" means "no description") | whole file unreadable → *could not be confirmed, blocking* |
| `manifest_schema` of another major, or not an `allstars-mod/…` name | file not interpretable → none of its rules applied, *could not be confirmed, blocking* |
| `must_understand` names an unknown feature | same |
| a field that must hold a list/object/bool/text holds something else (**including an explicit `null`**: null is a value of the wrong type, never "not stated"), or a relation/loose asset/order entry is incomplete | *could not be confirmed, blocking* |
| a footprint declaration (native patch ranges, exclusive claims) cannot be read | *could not be confirmed, warning* |
| display-only field (summary, config_schema, artifacts, …) malformed | skipped; never changes a result |

"Blocking" means the **manager** will not switch the mod on and the **checker** cannot report a clean result. In the game, ChaoGarage 1.1.0 and later refuse such a mod at start (with a message) when the problem is in the description's syntax, format name, `must_understand`, `loader` or `load_order`; a malformed mod-to-mod rule (`requires`, `conflicts_with`, ...) is not read by the loader. Loaders before 1.1.0 do not read this file at all. The messages say to ask the author for a corrected file; they never suggest deleting a mod's safety information.

## 10. Versions and ranges

### 10.1 Versions
```
version  = [ "v" ] number { "." number } [ "-" prerelease ] [ "+" build ]
number   = digit+                       (any length; compared as numbers, no overflow)
prerelease = ident { "." ident }        ident = [0-9A-Za-z-]+   (no empty identifier)
build    = ident { "." ident }
```
1–16 numeric components, at most 80 characters, ASCII only (full-width digits, control characters and an upper-case `V` are not versions). Surrounding white space is ignored.

**Precedence** is Semantic Versioning 2.0.0 with one extension: missing numeric components count as 0 (`1.2` equals `1.2.0`). Numeric components and numeric pre-release identifiers compare as numbers; a numeric identifier sorts before an alphanumeric one; alphanumeric identifiers compare in ASCII order; with equal leading identifiers the longer pre-release sorts later; a release sorts after all its pre-releases; **build metadata is ignored**. So `1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-alpha.beta < 1.0.0-beta < 1.0.0-beta.2 < 1.0.0-beta.11 < 1.0.0-rc.1 < 1.0.0`, and `1.1.0-beta.3 < 1.1.0-beta.3.rp2`.

### 10.2 Ranges
```
range      = "*" | "" | "UNKNOWN" | condition { sep condition }
condition  = [ op [ blanks ] ] version
op         = "=" | "==" | ">=" | "<=" | ">" | "<"        (no op means "=")
sep        = blanks | ","                                  (all conditions must hold: AND)
```
At most 16 conditions and 200 characters. **The whole text must match**; anything else (`1.x`, `^1.0`, `~1`, `>=1.0 || <0.5`, `>=1.0 garbage`, an operator without a version) is *invalid* — never partly accepted. `*` or an empty range accepts any version, including a missing one. `UNKNOWN` (any case) means the author does not know and evaluates to *unknown*. A version that is missing or not a version evaluates a real range to *unknown*.

Pre-releases follow plain precedence: `1.0.0-rc.1` does **not** satisfy `>=1.0.0` but does satisfy `<2.0.0`; `2.0.0-rc.1` satisfies `<2.0.0` too (it is below `2.0.0`). Use an explicit lower bound such as `>=1.0.0 <2.0.0-0` if a limit should exclude the next release's pre-releases.

## 11. Evolution rules

* **Major** (`allstars-mod/2`): may change the meaning of any field. A reader that does not know the major treats the file as unreadable (section 9). Major 1 will not be changed incompatibly.
* **Minor** (`allstars-mod/1.1`, …): only *adds* optional fields. Readers of major 1 read every minor and ignore fields they do not know.
* A field added in a minor that **restricts** combinations MUST be listed by the author in `must_understand`, so that a reader that predates the field refuses to say "compatible" instead of silently ignoring it.
* Capability names (12.2) are append-only: once a build is listed with a capability, the name's meaning does not change.
* The loader layer (`mod.ini` schema 1, plan schema 1) is owned by the loader; the tools mirror it and follow the loader if it changes.

## 12. Who enforces what

### 12.1 Rules and their enforcers

| Rule | Game loader | Manager | Checker |
|---|---|---|---|
| `mod.ini` fields, schema 1, lowercase pins, path rules, no links | **yes** (whole mod set is refused on failure) | yes | yes |
| unique mod IDs | **yes** | yes | yes |
| plan format, payload sizes and hashes, base hashes of game files | **yes** | yes | yes (hashes unless `--quick`) |
| shared archive members, whole files, shader caches, different base archives | **yes** | yes | yes |
| runtime is an x86 DLL exporting `SsasrModInitialize` / `SsasrModRollback` | **yes** (when it loads the DLL) | static check | static check |
| any failure above | **all mods are disabled for that launch** | blocks Apply | definite problem |
| start order (folder name order, `metal-sonic` first) | **defines it** | shows it | shows it |
| description syntax and format name, `must_understand`, `loader` requirements, `load_order` | **ChaoGarage 1.1.0+: yes**, before any mod code is loaded (all mods are refused, with one message); before 1.1.0: no | enforces | reports |
| `requires`, `conflicts_with`, `capabilities_exclusive`, `native_patches` overlap, loose-asset collisions, `target` | **no** (no loader release reads these; 1.1.0 logs `target` only) | enforces (blocks or warns as above) | reports (definite / could not be confirmed) |
| two native runtimes working together | no one can check | note "unknown" | verdict *insufficient information* unless pairs are recorded |

For the rows that every loader enforces, "yes" in the loader column was compared with the loader's own source code and, for the data rules, with the loader's discovery code compiled unchanged and run on 600 generated folders (private differential test; see the README for what is and is not covered by public tests). The runtime-DLL and description checks are *additional* to anything the shipped loader does.

### 12.2 Loader profiles and capability names

A **loader profile** is a record keyed by the SHA-256 pair of `ChaoGarage.dll` + `dinput8.dll`: `{id, host_sha256, proxy_sha256, host_abi, mod_ini_schema, plan_schema, capabilities[], evidence}`. The tools ship a table (`allstars-loader-profiles/1`, embedded in `AllStarsModManager.Core.dll`; `AllStarsModCheck --list-loaders` prints it). A user may add builds in `data\loader-profiles.json` (manager) or with `--loader-profiles <file>` (checker); such entries are labelled user-supplied. A profile for a build you did not verify is your own statement, not evidence. The built-in profile records how its hashes were tied to source (a separate rebuild-and-compare, not a game test) in its `evidence` text.

Capabilities known for `ssasr-host-1` (derived by reading the host source, not by running it): `host-abi-1`, `bootstrap-abi-1`, `mod-ini-schema-1`, `plan-schema-1`, `plan-format-xpac`, `plan-format-file`, `plan-format-copy`, `plan-format-rtsc-merge`, `xpac-member-merge` (several mods change different members of one archive), `startup-order-folder-name`, `startup-order-metal-sonic-first`, `runtime-init-before-game-crt` (the runtime is initialised before the game's own startup, so no game function may be called yet), `runtime-rollback-on-failed-init`.

Capabilities of `chaogarage-1.1.0` (the host's own table, also reported by its `ChaoGarageGetLoaderInfo` export): the thirteen above except `bootstrap-abi-1` (1.1.0's two files use bootstrap interface 2), plus `bootstrap-abi-2` (the two files check that they come from one release), `loader-info-export`, `startup-requirements-check` (the description's format, `must_understand` and `loader` block are enforced before any mod code is loaded), `startup-load-order-check`, `startup-warning-dialog` (refusals are explained in one message per start) and `rtsc-merge-16384-records` (shader caches with up to 16,384 records). A mod that wants players on an older loader to be stopped by these tools can require `startup-requirements-check`; the loader's own repository also shows how a mod's runtime can check the loader it runs under.

## 13. What this contract does not give you

* **No authenticity.** A matching hash proves a file is the one the manifest names; it does not prove who made it, and an author who writes a lie in a description is not caught. There are no signatures in format 1.
* **No proof of gameplay compatibility.** Declared compatible means "every rule the mods declared is met and the checkable file facts are fine". Native DLLs can interact in ways nobody declared.
* **No update service.** Nothing here contacts a network. The tools cannot know the newest version of a mod or of the loader; they compare what is on disk with what the files declare. A loader that has already been distributed cannot gain new checks retroactively.
* **No guarantee for builds that are not in a profile table.** An unrecognised loader build is reported as such, never as "probably fine".
