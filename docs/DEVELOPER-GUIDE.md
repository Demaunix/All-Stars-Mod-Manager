# Developer guide: how the code is organised and how to extend it

Everything that decides anything lives in one library, `src/Core` (`AllStarsModManager.Core.dll`). The manager window (`src/App`) and the checker (`src/Check`) are thin shells over it, so they cannot disagree.

```
src/Core
  Packages/    PackageInspector (reads one mod folder), SidecarReader (allstars-mod.json), VersionSpec, PeInspector,
               PackageVerifier (hashes), Model
  Formats/     LoaderIni (mod.ini as the loader reads it), StrictJson (the loader's JSON subset)
  Validation/  Conflicts (ConflictAnalyzer: every cross-mod rule), ActiveSetValidator (the one set-level check), LoaderVerdict
  Game/        GameFolderScanner, LoaderProfiles + LoaderEvidence, Launching (Steam launch, manager only)
  Compat/      CheckRunner / CheckCli / CheckOutput / CheckReport (the manager-free checker)
  Transactions/  Planner, TransactionEngine, Journal: journaled apply, undo and recovery (see ROLLBACK-AND-SAFETY.md)
  Library/     Importer, SafeZip (folder and ZIP import)
src/App        WPF window (MainViewModel, MainWindow, dialogs, theme)
src/Check      AllStarsModCheck.exe: Main only
src/Tests      the test runner and all tests (Private\ holds inputs that are not public; see BUILDING.md)
```

## One implementation, three entry points

`PackageInspector.Inspect(folder)` → `PackageVerifier.Verify(...)` → `ActiveSetValidator.Validate(candidates, game?, cache, loaderEvidence, options)` → `ConflictAnalyzer.Analyze(candidates, loaderEvidence)`. The manager calls this chain with its intended set (what you ticked); the checker calls it with whatever is on disk (or `--add`ed). The test `checker: a set built by hand and the same set chosen in the manager get the same answer` pins that.

## Three kinds of statement

* `Issue` (per package) and `Conflict` (between packages) carry a **severity** (does the manager refuse to apply?) and, for conflicts, a **certainty**: `Definite` (a fact about the files or declarations) or `Unknown` (could not be confirmed: unreadable, unsupported, missing evidence). `IssueInfo.IsUnknown` gives the same for issues. The checker maps blocker+definite → "blocker", anything unknown → "unknown", the rest → "note".
* `LoaderEnforced` says the game's own loader performs the same check; everything else is applied by these tools only.
* **Fail closed:** whenever a reader meets something it cannot interpret in a field that could make a combination unsafe, it adds a `SidecarProblem` (or an `Invalid` relation); it never drops the entry. Display-only fields are skipped silently.

## Adding to the format

1. **A new description field**: add it to `Sidecar` (`Packages/Model.cs`) and read it in `SidecarReader.Parse` — strictly if it can restrict a combination (record a `SidecarProblem` with `Safety: true` when it cannot be read; `Blocking: false` only for footprint declarations), leniently if it is display text. Add the rule to `ConflictAnalyzer` (`Validation/Conflicts.cs`), a test per outcome in `Tests/CompatRuleTests.cs`, and the field to `docs/PACKAGE-FORMAT.md`. If the field restricts, authors must list a feature name in `must_understand`: add the name to `SidecarReader.SupportedFeatures`.
2. **A new loader build**: add a record to `src/Core/Game/loader-profiles.json` (hash pair, interface versions, capabilities, and how you obtained them in `evidence`). Capability names are append-only.
3. **A new manifest/plan version or plan format** (needs the loader to understand it first): `PackageInspector` (`ReadManifestFields`, `ParsePlan`, `ParseGroup`), `ConflictAnalyzer.Pair`, `PackageVerifier`; extend the generator in the differential test if you have the loader source.
4. **A new kind of extra file or destination**: `ActiveSetValidator` (`LooseAssetPolicy`) is the only place that decides which game-relative targets a package may add; `TransactionEngine` independently refuses any step outside `mods\` and `Resource\`.
5. **A new archive type for import**: `Library/SafeZip.cs` is the only extractor; a new one needs the same guarantees (names, sizes, links, checksums) and tests.
6. **A different game or loader**: the loader facts (`GameFolderInfo.ExeName`, profiles, launch app id) are constants; the conflict semantics are specific to this loader and must be re-derived.

## Honest unknown states already built in

*Unsupported archive*, *no manifest*, *foreign manifest* (another loader's `[Main]` mod.ini), *unsupported plan entry*, *schema other than 1*, *research note*, *not checked yet*, *native interactions unknown*, *declared but unchecked incompatibility*, *loader build not recognised*, *description unreadable / newer format / unknown required feature*, *version range invalid / unknown*. Each has user-facing wording and a stable code that the tests assert.
