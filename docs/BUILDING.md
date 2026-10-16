# Building and testing

## Requirements

* Windows 10/11 x64.
* The **.NET 10 SDK** (10.0.x) including the Windows Desktop targeting pack (the standard SDK installer has it). No NuGet packages are used and no network access is needed.
* PowerShell 5.1 or 7 for the scripts.
* Optional: Microsoft C++ build tools (x86) only to rebuild the example runtime DLL.

## Build

```
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

This compiles everything from the sources in this tree (nothing pre-built is used or compared against) and writes:

* `dist\AllStarsModManager\` — the manager, `AllStarsModCheck.exe`, docs and examples;
* `dist\AllStarsModCheck\` — the checker alone;
* `BUILD-INFO.json` in each folder and `dist\SOURCE-MANIFEST.json`, which bind the output to the exact source files: the manifest lists `sha256` and path of every source file; `BUILD-INFO.json` carries the SHA-256 of that list (`source_tree_sha256`) and of every output file.

The script keeps the SDK's caches under `work\` inside the tree. Output does not depend on the folder the tree is in (the compiler's source paths are mapped in `src\Directory.Build.props`): building a copy of the same tree elsewhere gives byte-identical `AllStarsModManager.Core.dll`, and the script fails if the manager and checker builds disagree about it.

To check a tree against a build: recompute the manifest rule stated in `BUILD-INFO.json` over your files and compare with `source_tree_sha256`; then compare the file hashes in `BUILD-INFO.json` with the files you have.

## Package (ZIPs)

```
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```

creates `dist-zip\AllStarsModManager-<version>.zip` and `AllStarsModCheck-<version>.zip` with fixed entry order and time stamps, plus `dist-zip\PACKAGE-INFO.json` (archive hash and every entry's hash).

## Tests

```
dotnet build src\Tests\AllStarsModManager.Tests.csproj -c Release
src\Tests\bin\Release\net10.0-windows\AllStarsModManager.Tests.exe all        # core, formats, rules, checker, transactions, import
src\Tests\bin\Release\net10.0-windows\AllStarsModManager.Tests.exe ui         # drives the packaged manager through UI Automation on a practice folder
src\Tests\bin\Release\net10.0-windows\AllStarsModManager.Tests.exe theme      # palette contrast and theme-usage checks
src\Tests\bin\Release\net10.0-windows\AllStarsModManager.Tests.exe settings-inproc current
src\Tests\bin\Release\net10.0-windows\AllStarsModManager.Tests.exe packagecheck <manager zip> <dist\AllStarsModManager>
src\Tests\bin\Release\net10.0-windows\AllStarsModManager.Tests.exe checkpackage <checker zip> <dist\AllStarsModCheck>
src\Tests\bin\Release\net10.0-windows\AllStarsModManager.Tests.exe all compat  # only tests whose name or area contains "compat"
```

* The `ui` mode needs the build in `dist\` (run `tools\build.ps1` first) and an interactive desktop session. It starts the packaged manager with `--fixture` (a practice copy), never the game or Steam.
* All tests use throw-away folders under `work\` with **synthetic** mods (generated files and hashes; no game data and no files from any real mod). Set `ALLSTARS_TEST_WORK` to a short folder (for example `C:\t`) if the tree sits in a deep path: the game loader's own cache paths hit Windows' 260-character limit in very deep folders, which only matters for the loader-parity test described below.
* Results are written to `tests\results\*.json`. A check that cannot run in a given checkout is reported as **skipped** with the reason and is never counted as passed.

### Tests that are not in the public tree

The maintainer's tree also contains a differential test that compiles the loader's own discovery code (needs the loader's source and the Microsoft C++ compiler) and compares its verdict with this program's on 600 generated game folders, plus checks against real mod packages. Those inputs are private and live in `src\Tests\Private\` and `private\`, which are **not exported**. The public test suite skips them and says so; everything else uses synthetic fixtures only.

## Rebuilding the example DLL (optional)

`examples\runtime-src\build-runtime.cmd <output folder>` builds the 1.5 KB example runtime with the Microsoft C++ tools. Afterwards run `tools\Update-ModHashes.ps1 -ModFolder examples\minimal-mod` (and the add-on) to re-pin it.
