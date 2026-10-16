# Third-party notices

The files in this repository are original work released under the MIT license (see `LICENSE`), with the exceptions and references below. Nothing listed here is copied into the repository or into the release packages unless it says so.

## What the release packages contain

The packages contain this project's own compiled code, its documentation and two synthetic example mods — **and two launcher programs that are not this project's code**: `AllStarsModManager.exe` and `AllStarsModCheck.exe`. The .NET SDK builds every framework-dependent Windows program by copying Microsoft's "apphost" launcher template (here the `Microsoft.NETCore.App.Host.win-x64` pack, version 10.0.12) and writing the program's name and version resources into it; the launcher only finds the installed .NET runtime and starts the program's own `.dll`. In these packages each launcher differs from the unmodified template only in patched header fields and added resources (88 changed bytes within the template's length, plus 2,048 bytes of resources, in this build). The launcher code is Microsoft's, from the open-source .NET runtime repository (`src/native/corehost`), which is licensed under the MIT license:

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

(Upstream: https://github.com/dotnet/runtime — `LICENSE.TXT`; Microsoft's description of the launcher: https://learn.microsoft.com/en-us/dotnet/core/deploying/. The SDK that builds the launcher also ships a `ThirdPartyNotices.txt` for components of the .NET runtime; none of those components is distributed in these packages.) Everything else in the packages is this project's own work under the MIT license in `LICENSE`.

The packages do **not** contain the .NET runtime, any NuGet package, any font, any image from another project, any file from the game, any console or Steam file, or the mod loader (`ChaoGarage.dll`, `dinput8.dll`).
## What you need from others

| Item | Needed for | Provider and terms |
|---|---|---|
| .NET 10 Desktop Runtime (x64) | running the manager (`AllStarsModManager.exe`) | Microsoft, separate download, MIT-licensed runtime: https://dotnet.microsoft.com/ |
| .NET 10 Runtime (x64) | running the checker (`AllStarsModCheck.exe`) only; the Desktop runtime includes it | Microsoft, as above |
| .NET 10 SDK | building from source | Microsoft, as above |
| Segoe UI | the manager's text | a Windows system font, referenced by name and not distributed |
| The mod loader (`ChaoGarage.dll`, `dinput8.dll`) | mods to load in the game | a separate project and download; this repository only records the SHA-256 of known builds and a list of capabilities read from the loader's source |
| Microsoft C++ build tools | rebuilding the 1.5 KB example runtime DLL only (optional) | Microsoft; the DLL links no library and contains no Microsoft code beyond what the compiler generates for two empty functions |

The project uses no NuGet packages and no third-party source code (the launchers above are the one piece of Microsoft-originated binary code in the packages).

## Inspiration, not copying

The look and feel of the window (compact tabs, check-box table with details beneath, flat buttons, a fixed action bar) and the layout of this repository's README are inspired by two community mod managers for other games: **SA Mod Manager** (for Sonic Adventure games, by the X-Hax community) and **HedgeModManager** (for Sonic games on the Hedgehog Engine, by the Hedge Modding community). No code, text, icon, image or other asset from either project was copied. If you reuse ideas from them, follow their own licenses.

## Trademarks

*Sonic*, *SEGA* and *Sonic & SEGA All-Stars Racing* are trademarks or registered trademarks of SEGA Corporation. *Steam* is a trademark of Valve Corporation. *Windows*, *.NET* and *Visual Studio* are trademarks of Microsoft Corporation. This project is an unofficial fan tool, is not affiliated with or endorsed by any of them, and ships none of their files. The names are used only to say which game and loader the tools are for.

## Images

`docs/images/*.png` are screenshots of this program running on a synthetic practice folder created by the test suite (no game data or mod files; the local folder path in the picture is covered). The application icon is a plain geometric shape (a blue disc with a white five-pointed star) drawn in XAML by this project; it is not derived from any logo.