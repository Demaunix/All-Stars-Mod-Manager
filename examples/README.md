# Examples

Two small, synthetic mod packages for learning the format and trying the checker. They contain no game data. Their runtime DLL (`runtime/example-runtime.dll`, source in `runtime-src`) is 32-bit, exports the two entry points the loader looks for, and does nothing.

| folder | shows |
|---|---|
| `minimal-mod` | the smallest valid package: `mod.ini`, an empty plan, a runtime, and a short `allstars-mod.json` |
| `minimal-mod-addon` | a second mod that `requires` the first, must start after it (`load_order`), needs loader capabilities (`loader`), and places one harmless file through a `copy` plan entry |

Try them (from the program folder):

```
AllStarsModCheck examples\minimal-mod --loader ssasr-host-1
AllStarsModCheck examples\minimal-mod-addon --loader ssasr-host-1         rem: definite problem, it needs minimal-example
AllStarsModCheck examples --loader ssasr-host-1                            rem: insufficient information (two native runtimes, no recorded pair test)
```

The first is *declared compatible*. The second is *incompatible* because its required mod is not there. The third shows what "insufficient information" means for two native mods: nothing clashes, but nobody recorded testing them together. Folder names matter: `minimal-mod` sorts before `minimal-mod-addon`, which is what the add-on's `load_order` asks for; rename the add-on folder to `a-addon` and the checker reports that it would start too early.

To use an example as a template, copy the folder, change the `id` in both files, edit the plan, rebuild the runtime, and run `tools\Update-ModHashes.ps1 -ModFolder <your folder>`; the loader refuses a mod whose pins do not match its files.

Rebuilding the example DLL needs the Microsoft C++ build tools (`runtime-src\build-runtime.cmd <output folder>`); the checked-in copy was built that way with `/Brepro` and is pinned by the hashes in the two `mod.ini` files.