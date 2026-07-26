# Sieve

Sieve is a Rhino 8 utility plugin for launching Grasshopper with a controlled set of loadable plugins.

## Current workflow

1. Run the Rhino command `Sieve`.
2. The minimal native Eto home screen opens immediately.
3. Use `Basics` to launch Grasshopper with known external plugins blocked.
4. Use `All Plugins` to enable every managed plugin group and launch Grasshopper.
5. Use `+` to start a new preset.
6. Use `Manual` for the compact plugin table and scan controls.
7. Press `Scan` only when you want to refresh plugin discovery.
8. Save the selection as a named preset if it is a repeatable workflow. Presets can include an icon, a short description, and can be exported as a `.txt` summary.

Sieve blocks unselected candidates by renaming the loadable file with a `.sieve-disabled` suffix before Grasshopper starts. On Rhino shutdown, Sieve tries to restore disabled files automatically. If Rhino crashes or is force-closed, reopen Rhino, run `Sieve`, and press `Restore`.

## Important constraint

Grasshopper cannot unload assemblies that are already initialized in the current Rhino process. Sieve should be used before opening Grasshopper in a fresh Rhino session. Once Grasshopper has loaded a plugin, disabling that plugin affects the next Grasshopper startup, not the already-running session.

## Scanned plugin types

Sieve currently detects:

- `.gha`
- `.ghpy`
- `.ghuser`

Sieve intentionally does not manage `.rhp`, `.dll`, or Rhino install-folder component files by default. `.rhp` files are Rhino-side plugins and many native `.gha` files live inside protected Rhino install folders, so renaming them can cause access-denied errors and can affect Rhino/Grasshopper itself. `.dll` files are usually dependencies, not direct Grasshopper load targets.

## Duplicate handling

Sieve groups files by plugin identity so users see one compact table row per plugin instead of many repeated file rows. If several copies of the same plugin are found, Sieve selects one preferred active copy and marks the other copies as blocked. The preferred copy is chosen by:

1. Higher version number when available.
2. Shorter/stabler path as a final tie-breaker.

Each duplicate card exposes its variants so the user can pick a different active version.

## User objects

`.ghuser` files are grouped by Grasshopper toolbar category instead of being listed one component at a time. When Sieve can read the serialized user object metadata, it uses `Category` as the plugin row name and shows subcategories/components in the details. If the metadata is not readable, Sieve falls back to the package or folder name.

## Grasshopper document analysis

Use `Open GH file` on the home screen to analyze a `.gh` or `.ghx` file. You can choose a file or drag and drop it onto the document page. Sieve scans the document archive for component/plugin signatures, including serialized data inside cluster chunks when GH_IO can normalize the archive. It then matches required plugins against the installed scan:

- Exact version is preferred.
- If the exact version is not installed, Sieve selects the closest installed version.
- Dependency assembly names such as `Something.Gh.CommonSdk` are mapped back to the closest loadable `.gha` in the same plugin family.
- Missing requirements are listed before launch.
- `Load set` applies the matched plugin set.
- `Load + launch` applies the matched plugin set and opens Grasshopper.

## UI note

Sieve uses an Eto `WebView` for the minimal black-and-white HTML interface. The UI is served from a tiny local loopback server inside the plugin, so buttons are normal browser links instead of custom WebView callback events. This keeps the modern HTML UI while avoiding the unreliable custom-scheme navigation bridge.

## UX ideas worth adding next

- A startup profile prompt: show a compact preset launcher automatically when Rhino opens.
- Conflict groups: detect plugins that share dependency DLLs and warn before partial disabling.
- Health checks: record Grasshopper startup time and last load errors per preset.
- Dry-run mode: show exactly which files will be renamed before applying.
- Real plugin icons where the plugin assembly exposes an icon resource.
- Author/vendor extraction from assembly metadata and Yak package metadata.
