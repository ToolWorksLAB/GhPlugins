# Sieve – Grasshopper Plugin Environment Manager

Sieve is a Rhino/Grasshopper plugin that lets you scan, organize, and switch between curated sets of Grasshopper add‑ons. It provides a modal UI for creating environments, selecting which plugins should load, and applying those choices by toggling plugin files on disk.


## Download latest build
[![Download Latest Build](https://img.shields.io/badge/Download-Latest%20Build-2ea44f?style=for-the-badge)](../../releases/tag/latest-build)

After every push to `main`/`master` (or manual workflow run), GitHub Actions publishes `Sieve-latest.zip` to the **Latest Build** release.

## Features
- **Plugin discovery** – Scans default Grasshopper locations (Libraries, UserObjects, Yak packages) plus any user-specified paths to collect `.gha`, `.ghuser`, and `.ghpy` packages along with associated DLLs. Findings are cached for reuse between sessions.
- **Environment presets** – Lets you save named environments consisting of selected plugins, then reload them later. Environments are stored in `%APPDATA%/Sieve/Sieve_envs.json` as JSON.
- **Selective loading** – Enables or disables plugins by adding/removing a `.disabled` suffix from plugin files and Yak DLLs so only the chosen environment loads. A safety hook restores defaults on Rhino shutdown or plugin load.
- **UI workflows** – Provides dialogs to scan/select plugins (`Select Plugins`), pick a saved environment (`Select Environment`), and launch Grasshopper with the chosen set.
- **Reporting** – Can export scan results to JSON reports that summarize discovered plugins for debugging or sharing.

## Project layout
- `GhPlugins/` – Core plugin code (compiled as `Sieve.rhp`)
  - `GhPluginsCommand.cs` – Entry command that opens the main modal dialog.
  - `GhPluginsPlugin.cs` – Plugin lifecycle hooks that restore disabled plugins on load/shutdown.
  - `Models/` – Data classes for plugin metadata (`PluginItem`) and environment definitions (`ModeConfig`).
  - `services/` – Logic for scanning directories (`PluginScanner`), reading plugin metadata (`PluginReader`), saving reports (`ScanReport`), applying enable/disable toggles (`GhPluginBlocker`), and persisting environments (`ModeManager`).
  - `Info/` – Paths/utilities for caching scans and managing custom search paths.
  - `UI/` – Eto.Forms dialogs for selecting plugins and environments.
  - `Resources/` – Embedded assets such as the logo.
- `Sieve.sln` – Solution file targeting .NET 7.0 and .NET 4.8 (for Rhino 7/8 compatibility).

## Building
1. Install RhinoCommon / Grasshopper references (Rhino 8 paths are used in `Sieve.csproj`).
2. Open `Sieve.sln` in Visual Studio 2022 or run `dotnet build GhPlugins/Sieve.csproj -f net7.0` for a Rhino 8 build. Targeting `net48` produces a Rhino 7-compatible assembly.
3. The output plugin (`Sieve.rhp`) can be loaded into Rhino; it dynamically restores disabled plugins when Rhino exits.

## Usage
1. In Rhino, run the `Sieve` command.
2. Use **Select Plugins** to scan default and custom plugin directories, then choose which items belong in the environment.
3. Save the environment, then pick it via **Select Environment**.
4. Click **Launch Grasshopper** to start with the chosen set of plugins enabled; others are disabled on disk.
5. Closing Rhino (or reloading the plugin) restores everything to its prior state.

## Suggested improvements
- Validate unmerged comment markers in code and remove stale merge artifacts.
- Add automated tests around scanning and toggling logic to prevent regressions when Rhino directory layouts change.
- Surface clearer status/error messages in the UI when toggling files fails due to permissions.
- Provide a dry-run mode that reports which files would be enabled/disabled without touching disk state.
- Add CI steps to build both `net48` and `net7.0` targets and catch missing Rhino references early.
