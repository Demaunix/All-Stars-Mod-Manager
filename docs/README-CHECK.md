# All-Stars Mod Check

A small offline program that checks whether the loader mods in a folder agree with each other and with your mod loader, for **Sonic & SEGA All-Stars Racing** (PC). You do **not** need the All-Stars Mod Manager to use it, and you do not need to install anything.

* Needs the **.NET 10 Runtime** (x64) from Microsoft (the Desktop runtime includes it).
* Reads files only: never runs or loads a mod DLL, never changes the game folder, settings or saves, no network, creates no files.
* Gives one of three answers — **declared compatible** (exit 0), **insufficient information** (exit 1) or **incompatible** (exit 2) — and lists exactly what it found and which of it the game's own loader enforces.
* It is a file check, not a test in the game.

## Use

Open a command prompt in this folder:

```
AllStarsModCheck "C:\Games\Sonic & SEGA All-Stars Racing"
AllStarsModCheck "D:\Downloads\Some Mod" --loader ssasr-host-1
AllStarsModCheck "C:\Games\Sonic & SEGA All-Stars Racing" --add "D:\Downloads\Some Mod"
AllStarsModCheck --help
```

A `.zip` has to be unpacked first. Full explanation of the options, the answers, the exit codes and the JSON output: `docs\COMPATIBILITY-CHECKER.md`.

## For mod authors

`docs\PACKAGE-FORMAT.md` is the stable format; `docs\AUTHOR-GUIDE.md` is the practical guide; `examples\` has two small synthetic packages that pass the checker (their runtime does nothing); `tools\Update-ModHashes.ps1` recomputes the two hashes in `mod.ini`.

## Source, license, versions

MIT license (`LICENSE`). Source and the manager: https://github.com/Demaunix/All-Stars-Mod-Manager. The mod loader (`ChaoGarage.dll`, `dinput8.dll`) is a separate download: https://github.com/Demaunix/ChaoGarage/releases. `BUILD-INFO.json` lists every file's hash and the source tree it was built from.