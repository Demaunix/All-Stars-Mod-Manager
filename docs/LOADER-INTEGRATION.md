# Notes for loader implementers

This page is for people changing the mod loader (`ChaoGarage.dll` and its bootstrap proxy) who want the loader, a mod's runtime, or another tool to use the same compatibility data as the manager and the checker. **ChaoGarage 1.1.0** (https://github.com/Demaunix/ChaoGarage) implements the loader side described here: before any mod code is loaded it reads each mod's `allstars-mod.json` for its format, `must_understand`, `loader` requirements and `load_order`, refuses mods it cannot satisfy, and explains every refusal in one message per start. Loader builds **before** 1.1.0 never read `allstars-mod.json` and show no compatibility message of their own. No loader knows which version of anything is the newest. ChaoGarage's `tests/vectors/loader-requirements.json` is the shared set of cases; this repository's copy (`src/Tests/vectors/`) is byte-identical and is run through the checker by `LoaderVectorTests`, which also lists where the loader and the tools intentionally differ (rules the loader does not read).

## 1. What already exists in the contract

| Piece | Where it is defined | Notes for a native reader |
|---|---|---|
| Mod requirements about the loader | `allstars-mod.json` → `loader` (`host_abi`, `mod_ini_schema`, `plan_schema`, `capabilities`, `host_sha256`, `proxy_sha256`) | format 1, see PACKAGE-FORMAT.md section 7 |
| Features a reader must know | `must_understand` | an unknown name means "do not claim compatible" |
| What a loader provides | loader profile: `{id, host_abi, mod_ini_schema, plan_schema, capabilities[]}` keyed by the hashes of the two loader files | table `allstars-loader-profiles/1`; a loader build can carry its own record in code |
| Game build the mod was tested with | `target.executable_sha256` | descriptive |
| Order constraints | `load_order.after/before` | checked against folder-name order, `metal-sonic` first |

A mod that does **not** declare a requirement says nothing; a loader must not invent one. A mod that declares one it can state exactly (an ABI number, a capability name) lets a loader or the mod's own runtime decide without guessing.

## 2. Syntax to read

`allstars-mod.json` is written in exactly the JSON subset the loader's own `json.h` reads for `plan.json` (PACKAGE-FORMAT.md 2.2): UTF-8 without BOM; whitespace = space, tab, CR, LF; no comments; no trailing commas; **unsigned decimal whole numbers only, no leading zeros**; the string escapes `\" \\ \/ \b \f \n \r \t \uXXXX` with surrogate code points (D800–DFFF) rejected; object names unique; depth ≤ 33 levels. A reader built on `json.h` therefore reads every conforming description without a new parser. Content that the shipped `json.h` cannot represent (negative or fractional numbers, escaped surrogate pairs) is by definition not allowed in the description, and the managed reader refuses it for the same reason — the two cannot disagree.

Optional text fields are display text. Numeric fields that matter to compatibility are `host_abi`, `mod_ini_schema`, `plan_schema` (`uint32`), `va`, `length` (`uint32`) and `bytes` (`uint32`).

## 3. Fail-closed rules a native reader should share

Copy the *outcomes*, not the code:

1. A description that is present but cannot be parsed in the subset, is larger than 4 MiB, or has `manifest_schema` other than `allstars-mod/1[.n]` → **unknown**. Never "no requirements".
2. `must_understand` with a name the reader does not know → unknown.
3. A `loader` object whose members have the wrong type, a `capabilities` entry that is not a short lowercase name, or a hash that is not 64 hex digits → unknown (blocking class).
4. Loader recognised only by hash pair. An unrecognised pair cannot answer capability or ABI questions.
5. `host_abi` is an exact version, not a minimum; a future format may add a range member and list it in `must_understand`.
6. Hash pins compare with the real file hashes whether or not the build is recognised.

The decision table the managed tools use is in PACKAGE-FORMAT.md sections 6, 7 and 9, and the reference implementation is `src/Core/Validation/Conflicts.cs` (`LoaderRequirementRules`).

## 4. How ChaoGarage 1.1.0 uses the contract

* **A new mod meets a loader that lacks what it needs.** ChaoGarage 1.1.0 and later compare the mod's `loader` block and `must_understand` with what they provide and refuse the mod before loading any mod code, with the message "needs a newer ChaoGarage loader" and the loader's download page. A loader released **before** 1.1.0 cannot do this: only code newer than the running loader can notice it is too old. A mod's own runtime can: ChaoGarage exports `ChaoGarageGetLoaderInfo` from 1.1.0 on (absent before), and its `examples/mod-runtime-check/chaogarage_require.h` shows a three-line check that names the mod and the download page.
* **An older mod meets a newer loader that no longer provides what it declared.** Refused only on a declaration: a lower `host_abi`/`mod_ini_schema`/`plan_schema`, a capability that only earlier builds provided (1.1.0 no longer provides `bootstrap-abi-1`), or `host_sha256`/`proxy_sha256` pins to other builds. The message says "made for a different ChaoGarage loader build" and shows the mod's `homepage` when it is an `https://` address. A legacy mod with no description is never refused for its age; 1.1.0 keeps no list of known-incompatible mods.
* **An older but compatible mod with the newer loader keeps working.** Requirements are capabilities and exact interface versions, never "newest wins", so such a mod passes untouched.
* **The two loader files.** `ChaoGarage.dll` and `dinput8.dll` 1.1.0 refuse a partner from another release with a message; the tools recognise a pair only by both SHA-256 values (profiles `chaogarage-1.1.0`, `chaogarage-1.2.0`), so a mixed pair is *unrecognised* here as well.
* **Two mods change the same archive member (1.2.0).** ChaoGarage 1.2.0 combines them when a plan operation carries a fitting `variants` entry or `optional`; the tools mirror its order (PACKAGE-FORMAT.md section 3.1) when the recognised loader has `xpac-member-variants` / `xpac-member-optional`, refuse as before when it lacks them, and report *could not be confirmed* when the loader is not recognised. A loader that changes this order must use new capability names.
* **Newest versions cannot be known offline.** Neither the tools nor the loader contact a network; messages only say that what is on disk does not meet what a file declares.
## 5. What a profile must record honestly

`evidence` should say how the capability list was obtained (read from source, observed in a test) and for which hashes. A profile entry written by hand for a build you did not verify is your own statement; the tools label such an entry `user-supplied` and never treat it as evidence. Capability names are append-only.

## 6. Contract changes that would need a new major

A rule that makes a combination *acceptable* which a major-1 reader would reject, a different meaning for an existing field, or syntax outside the subset above. Everything else can be added as an optional member in a minor and, if it restricts, named in `must_understand`.
