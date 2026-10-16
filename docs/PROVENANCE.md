# Provenance

What the files in this repository are, where they came from, and what they are not.

## Code and documents

All source code, tests, scripts and documents in this repository were written for this project and are released under the MIT license (`LICENSE`, copyright "All-Stars Mod Manager contributors"). They were produced with AI assistance (Anthropic's Claude, used through Claude Code under the project owner's direction) and were reviewed with an independent AI code-review tool (OpenAI's Codex tooling) plus automated tests. No third-party source code was copied in; the project uses no NuGet packages. The only Microsoft-originated binary code in the release packages is the .NET SDK's standard Windows launcher stub inside the two `.exe` files (see `THIRD-PARTY-NOTICES.md`). Whether and how AI-assisted material can be copyrighted differs between countries; the owner has chosen to release it under MIT regardless.

The window design is inspired by community mod managers for other games (see `THIRD-PARTY-NOTICES.md`); no code or assets were taken from them.

## Facts about the game's mod loader

Statements about what the shipped mod loader enforces (section 12 of `docs/PACKAGE-FORMAT.md`) come from reading the loader's own source files, and, for the data rules (conflicts between mods), from a differential test that compiles the loader's discovery code unchanged and compares its verdict with this project's on 600 generated folders. That test needs the loader's source and a Microsoft C++ compiler and is **not** part of the public test suite; the public tests use synthetic folders only. The runtime-DLL check and the description-file rules are additional to the 1.0 loader. ChaoGarage 1.1.0 also enforces the description-file format, `loader` requirements and `load_order` at game start; its behaviour is tested in the ChaoGarage repository against the shared vector file `src/Tests/vectors/loader-requirements.json` (byte-identical to ChaoGarage's `tests/vectors/loader-requirements.json`).

The built-in loader profile (`ssasr-host-1`) records the SHA-256 of one known `ChaoGarage.dll` / `dinput8.dll` pair and a capability list derived by reading the host source; the loader was not run to produce that list. Each hash is tied to source by a separate verification in which both files were rebuilt from their retained source with the Microsoft C++ tools (MSVC 14.42.34433 x86, Windows SDK 10.0.22621.0): the rebuilt files matched the distributed ones byte for byte except six bytes of embedded timestamps, the bootstrap proxy only when compiled in its original source-folder context. That is evidence about the files' origin, not proof of behaviour in the game, and it does not cover the game executable.

## Images and icon

`docs/images/*.png` are screenshots of the program on a synthetic practice folder created by the test suite: synthetic practice data, with no game files and no files from any mod. The only edit made to a screenshot is that the text of the practice folder's full path, which contains a local account name, is covered and replaced with "(path hidden)". The application icon is a blue disc with a white five-pointed star drawn as XAML geometry in `src/App/Theme/Graphics.xaml`; it is a plain geometric shape and not derived from any logo.

## What is deliberately not here

No game files or assets, no files from any console, no Steam SDK files, no screenshots of the game, no private test data (the project's private test inputs and the loader-parity oracle are kept out of this tree), and no account, credential or local-path information.