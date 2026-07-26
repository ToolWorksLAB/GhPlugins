using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Eto.Forms;
using Sieve.Models;
using Sieve.Services;

namespace Sieve.UI
{
    public sealed partial class SieveDialog
    {
        private const string CurrentIconExtractionVersion = "named-resources-v2";

        private void NormalizeSettings()
        {
            _settings.CustomPaths ??= new List<string>();
            _settings.DisabledScanPaths ??= new List<string>();
            _settings.Presets ??= new List<SievePreset>();
            _settings.LastScan ??= new List<PluginCandidate>();
            _settings.DisabledPaths ??= new List<string>();
            _settings.PinnedPluginPaths ??= new List<string>();
            _settings.LastScanChanges ??= new List<ScanChange>();
            _settings.LaunchHistory ??= new List<LaunchRecord>();
            _settings.PluginIconCache ??= new List<PluginIconCacheEntry>();
            _settings.PluginViewMode = string.Equals(_settings.PluginViewMode, "list", StringComparison.OrdinalIgnoreCase) ? "list" : "grid";
            _settings.DisabledScanPaths = _settings.DisabledScanPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _settings.PluginIconCache = _settings.PluginIconCache
                .Where(entry => entry != null && IsSafePluginIcon(entry.IconDataUrl))
                .GroupBy(entry => FirstNonEmpty(entry.PluginPath, entry.LibraryName), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(entry => entry.CapturedUtc, StringComparer.Ordinal).First())
                .Take(256)
                .ToList();
            if (!string.Equals(_settings.IconExtractionVersion, CurrentIconExtractionVersion, StringComparison.Ordinal))
            {
                foreach (var candidate in _settings.LastScan)
                    candidate.IconDataUrl = string.Empty;
                _settings.IconExtractionVersion = CurrentIconExtractionVersion;
            }
            foreach (var preset in _settings.Presets)
            {
                preset.PluginPaths ??= new List<string>();
                preset.ProjectFolders ??= new List<string>();
            }
        }

        private List<ScanChange> CalculateScanChanges(IEnumerable<PluginCandidate> previous, IEnumerable<PluginCandidate> current)
        {
            var before = (previous ?? Enumerable.Empty<PluginCandidate>()).ToList();
            var after = (current ?? Enumerable.Empty<PluginCandidate>()).ToList();
            var beforePaths = new HashSet<string>(before.Select(item => item.OriginalPath), StringComparer.OrdinalIgnoreCase);
            var afterPaths = new HashSet<string>(after.Select(item => item.OriginalPath), StringComparer.OrdinalIgnoreCase);
            var changes = new List<ScanChange>();

            foreach (var candidate in after.Where(item => !beforePaths.Contains(item.OriginalPath)))
            {
                var matchingPrevious = before.Where(item => string.Equals(GetPluginKey(item), GetPluginKey(candidate), StringComparison.OrdinalIgnoreCase)).ToList();
                changes.Add(new ScanChange
                {
                    Kind = matchingPrevious.Count == 0 ? "Added" : "Updated",
                    PluginName = candidate.Name,
                    Detail = matchingPrevious.Count == 0
                        ? CompactPath(candidate.OriginalPath)
                        : $"{FirstNonEmpty(matchingPrevious[0].Version, "unknown")} to {FirstNonEmpty(candidate.Version, "unknown")}" 
                });
            }

            foreach (var candidate in before.Where(item => !afterPaths.Contains(item.OriginalPath)))
            {
                if (after.Any(item => string.Equals(GetPluginKey(item), GetPluginKey(candidate), StringComparison.OrdinalIgnoreCase)))
                    continue;

                changes.Add(new ScanChange
                {
                    Kind = "Removed",
                    PluginName = candidate.Name,
                    Detail = CompactPath(candidate.OriginalPath)
                });
            }

            return changes.OrderBy(item => item.Kind).ThenBy(item => item.PluginName, StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        }

        private bool IsPinned(PluginGroup group)
        {
            return group.Variants.Any(candidate => _settings.PinnedPluginPaths.Contains(candidate.OriginalPath, StringComparer.OrdinalIgnoreCase));
        }

        private void TogglePin(string key)
        {
            var group = BuildGroups().FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (group == null)
                return;

            var allPaths = group.Variants.Select(item => item.OriginalPath).ToList();
            if (IsPinned(group))
                _settings.PinnedPluginPaths.RemoveAll(path => allPaths.Contains(path, StringComparer.OrdinalIgnoreCase));
            else
            {
                var release = BuildReleases(group).FirstOrDefault(item => item.Load) ?? BuildReleases(group).FirstOrDefault();
                if (release != null)
                    _settings.PinnedPluginPaths.AddRange(release.Variants.Select(item => item.OriginalPath));
            }

            _settings.PinnedPluginPaths = _settings.PinnedPluginPaths
                .Where(PluginPolicy.IsManageablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            SettingsStore.Save(_settings);
        }

        private void BeginPresetEdit(string name)
        {
            var preset = _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                return;

            _editingPresetName = preset.Name;
            _presetEditorOpen = true;
            _query = string.Empty;
            var selectedPaths = new HashSet<string>(preset.PluginPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in _candidates)
                candidate.Load = selectedPaths.Contains(candidate.OriginalPath);
            PersistCandidates();
            _screen = "manual";
        }

        private void DuplicatePreset(string name)
        {
            var source = _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return;

            var copy = new SievePreset
            {
                Name = NextPresetName(source.Name + " copy"),
                Icon = source.Icon,
                Description = source.Description,
                PluginPaths = source.PluginPaths.ToList(),
                ProjectFolders = source.ProjectFolders.ToList()
            };
            _settings.Presets.Add(copy);
            _editingPresetName = copy.Name;
            SettingsStore.Save(_settings);
        }

        private void MovePreset(string name, int direction)
        {
            var index = _settings.Presets.FindIndex(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            var target = index + direction;
            if (index < 0 || target < 0 || target >= _settings.Presets.Count)
                return;

            var preset = _settings.Presets[index];
            _settings.Presets.RemoveAt(index);
            _settings.Presets.Insert(target, preset);
            SettingsStore.Save(_settings);
        }

        private void ReorderPresets(string serializedOrder)
        {
            var names = (serializedOrder ?? string.Empty)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .ToList();
            if (names.Count != _settings.Presets.Count || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                return;

            var byName = _settings.Presets.ToDictionary(preset => preset.Name, StringComparer.OrdinalIgnoreCase);
            if (names.Any(name => !byName.ContainsKey(name)))
                return;

            _settings.Presets = names.Select(name => byName[name]).ToList();
            SettingsStore.Save(_settings);
        }

        private void ImportPresetFromServer()
        {
            var wait = new System.Threading.ManualResetEventSlim(false);
            string selected = null;
            Application.Instance.AsyncInvoke(() =>
            {
                using var dialog = new OpenFileDialog { Title = "Import Sieve preset" };
                dialog.Filters.Add(new FileFilter("Sieve preset", ".txt"));
                if (dialog.ShowDialog(this) == DialogResult.Ok)
                    selected = dialog.FileName;
                wait.Set();
            });
            wait.Wait();

            if (string.IsNullOrWhiteSpace(selected) || !File.Exists(selected))
                return;

            try
            {
                var imported = ParsePresetExport(File.ReadAllText(selected));
                if (string.IsNullOrWhiteSpace(imported.Name))
                {
                    _settings.LastScanReport = "Preset import failed: no preset name was found.";
                    return;
                }

                var mapped = new List<string>();
                var missing = 0;
                foreach (var sourcePath in imported.PluginPaths)
                {
                    var candidate = _candidates.FirstOrDefault(item => string.Equals(item.OriginalPath, sourcePath, StringComparison.OrdinalIgnoreCase)) ??
                        _candidates.FirstOrDefault(item => string.Equals(Path.GetFileName(item.OriginalPath), Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase));
                    if (candidate == null)
                        missing++;
                    else
                        mapped.Add(candidate.OriginalPath);
                }

                var preset = new SievePreset
                {
                    Name = NextPresetName(imported.Name),
                    Icon = NormalizePresetIcon(imported.Icon),
                    Description = imported.Description,
                    PluginPaths = mapped.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    ProjectFolders = imported.ProjectFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                };
                _settings.Presets.Add(preset);
                _settings.LastScanReport = $"Imported preset {preset.Name}: {preset.PluginPaths.Count} plugins mapped, {missing} unavailable.";
                SettingsStore.Save(_settings);
                _editingPresetName = preset.Name;
            }
            catch (Exception ex)
            {
                _settings.LastScanReport = "Preset import failed: " + ex.Message;
            }
        }

        private void AssociatePresetFolder(string name)
        {
            var preset = _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                return;

            var wait = new System.Threading.ManualResetEventSlim(false);
            string selected = null;
            Application.Instance.AsyncInvoke(() =>
            {
                using var dialog = new SelectFolderDialog { Title = "Associate a project folder" };
                if (dialog.ShowDialog(this) == DialogResult.Ok)
                    selected = dialog.Directory;
                wait.Set();
            });
            wait.Wait();

            if (!string.IsNullOrWhiteSpace(selected) && Directory.Exists(selected) && !preset.ProjectFolders.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                preset.ProjectFolders.Add(selected);
                preset.ProjectFolders = preset.ProjectFolders.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
                SettingsStore.Save(_settings);
            }
        }

        private SievePreset FindRecommendedPreset()
        {
            if (string.IsNullOrWhiteSpace(_documentResult?.SourcePath))
                return null;

            var documentPath = Path.GetFullPath(_documentResult.SourcePath);
            return _settings.Presets
                .SelectMany(preset => preset.ProjectFolders.Select(folder => new { preset, folder }))
                .Where(item => !string.IsNullOrWhiteSpace(item.folder))
                .OrderByDescending(item => item.folder.Length)
                .FirstOrDefault(item => documentPath.StartsWith(item.folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                ?.preset;
        }

        private void AssociateDocumentWithPreset(string name)
        {
            var preset = _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null || string.IsNullOrWhiteSpace(_documentResult?.SourcePath))
                return;

            var folder = Path.GetDirectoryName(_documentResult.SourcePath);
            if (!string.IsNullOrWhiteSpace(folder) && !preset.ProjectFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                preset.ProjectFolders.Add(folder);
                SettingsStore.Save(_settings);
            }
        }

        private List<PreflightItem> BuildPreflightItems()
        {
            var items = new List<PreflightItem>();
            var active = _candidates.Where(item => item.Load).ToList();
            if (active.Count == 0)
                items.Add(new PreflightItem("Check", "No managed plugins are selected", "Grasshopper will open with native components only."));
            else
                items.Add(new PreflightItem("Ready", $"{active.Count} managed files selected", "One variant is selected for each plugin group."));

            var pinned = _candidates.Where(item => _settings.PinnedPluginPaths.Contains(item.OriginalPath, StringComparer.OrdinalIgnoreCase)).ToList();
            if (pinned.Count > 0)
                items.Add(new PreflightItem("Pinned", $"{pinned.Count} always-on files", string.Join(", ", pinned.Take(4).Select(item => item.Name))));

            foreach (var group in BuildGroups().Where(item => item.Load && BuildReleases(item).Count > 1))
                items.Add(new PreflightItem("Resolved", group.Name, $"{BuildReleases(group).Count} releases found; one release will load with its companion files."));

            if (_documentResult != null)
            {
                foreach (var match in _documentResult.Matches.Where(item => item.Status == "Missing"))
                    items.Add(new PreflightItem("Missing", match.Requirement.Name, match.Note));
                foreach (var match in _documentResult.Matches.Where(item => item.Status == "Closest"))
                    items.Add(new PreflightItem("Closest", match.Requirement.Name, match.Note));
            }

            return items;
        }

        private string NextPresetName(string suggested)
        {
            var name = string.IsNullOrWhiteSpace(suggested) ? "Preset" : suggested.Trim();
            if (!_settings.Presets.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;

            for (var number = 2; ; number++)
            {
                var candidate = name + " " + number;
                if (!_settings.Presets.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                    return candidate;
            }
        }

        private static ImportedPreset ParsePresetExport(string text)
        {
            var preset = new ImportedPreset();
            var inPlugins = false;
            var inFolders = false;
            foreach (var line in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
            {
                if (line.StartsWith("Name: ", StringComparison.OrdinalIgnoreCase))
                    preset.Name = line.Substring(6).Trim();
                else if (line.StartsWith("Icon: ", StringComparison.OrdinalIgnoreCase))
                    preset.Icon = line.Substring(6).Trim();
                else if (line.StartsWith("Description: ", StringComparison.OrdinalIgnoreCase))
                    preset.Description = line.Substring(13).Trim();
                else if (line.Trim().Equals("Plugins:", StringComparison.OrdinalIgnoreCase))
                {
                    inPlugins = true;
                    inFolders = false;
                }
                else if (line.Trim().Equals("Project folders:", StringComparison.OrdinalIgnoreCase))
                {
                    inFolders = true;
                    inPlugins = false;
                }
                else if (inFolders && line.StartsWith("  ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(line.Trim()))
                    preset.ProjectFolders.Add(line.Trim());
                else if (inPlugins && line.StartsWith("  ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(line.Trim()))
                    preset.PluginPaths.Add(line.Trim());
            }
            return preset;
        }

        private string RenderPreflight()
        {
            var items = BuildPreflightItems();
            var rows = string.Join("", items.Select(item => $@"
<tr class='{Attr(item.Status.ToLowerInvariant())}'><td><strong>{H(item.Status)}</strong></td><td>{H(item.Title)}</td><td>{H(item.Detail)}</td></tr>"));
            var active = _candidates.Count(item => item.Load);
            var groups = BuildGroups().Count(item => item.Load);

            return $@"
<div class='manual'>
  <header class='manual-head'>
    <div>
      <a class='back' href='/manual'>Back</a>
      <h1>Preflight</h1>
      <div class='manual-meta'>{groups} plugin groups / {active} files / {_launchLabel}</div>
    </div>
    <div class='manual-actions'><a href='/manual'>Adjust</a><a class='fill' href='/preflight-launch'>Launch</a></div>
  </header>
  <section class='preflight-summary'>
    <div><span>Selection</span><strong>{active}</strong><em>managed files</em></div>
    <div><span>Pinned</span><strong>{_settings.PinnedPluginPaths.Count}</strong><em>always on</em></div>
    <div><span>Scan changes</span><strong>{_settings.LastScanChanges.Count}</strong><em>since last scan</em></div>
  </section>
  <div class='table-wrap preflight-table'><table><thead><tr><th>Status</th><th>Item</th><th>Detail</th></tr></thead><tbody>{rows}</tbody></table></div>
  <details class='review'><summary>Recent launch timing</summary>{RenderLaunchHistoryTable()}</details>
</div>";
        }

        private string RenderHistory()
        {
            return $@"
<div class='manual'>
  <header class='manual-head'><div><a class='back' href='/home'>Back</a><h1>Launch history</h1><div class='manual-meta'>Canvas-ready measurements are approximate and stay on this computer.</div></div></header>
  <div class='table-wrap document-table'>{RenderLaunchHistoryTable()}</div>
</div>";
        }

        private string RenderLaunchHistoryTable()
        {
            var records = _settings.LaunchHistory ?? new List<LaunchRecord>();
            var rows = records.Count == 0
                ? "<tr><td colspan='5' class='empty-row'>No launch measurements yet.</td></tr>"
                : string.Join("", records.Select(record => $@"<tr><td>{H(record.Label)}</td><td>{record.PluginCount}</td><td>{H(record.Status)}</td><td>{(record.CanvasReadyMilliseconds > 0 ? (record.CanvasReadyMilliseconds / 1000d).ToString("0.0") + " s" : "-")}</td><td>{H(FormatLocalTime(record.StartedUtc))}</td></tr>"));
            return $"<table><thead><tr><th>Launch</th><th>Files</th><th>Status</th><th>Canvas ready</th><th>Started</th></tr></thead><tbody>{rows}</tbody></table>";
        }

        private string RenderPluginDetails()
        {
            var group = BuildGroups().FirstOrDefault(item => string.Equals(item.Key, _detailKey, StringComparison.OrdinalIgnoreCase));
            if (group == null)
                return "<div class='manual'><header class='manual-head'><div><a class='back' href='/details-back'>Back</a><h1>Plugin not found</h1></div></header></div>";

            var releases = BuildReleases(group);
            var activeRelease = releases.FirstOrDefault(release => release.Load);
            var presets = _settings.Presets.Where(preset => preset.PluginPaths.Intersect(group.Variants.Select(item => item.OriginalPath), StringComparer.OrdinalIgnoreCase).Any()).ToList();
            var projectFolders = string.Join("", presets.SelectMany(item => item.ProjectFolders).Distinct(StringComparer.OrdinalIgnoreCase).Select(folder => $"<li>{H(folder)}</li>"));
            var releaseButtons = string.Join("", releases.Select(release =>
            {
                var kinds = string.Join(" + ", release.Variants.Select(item => DetailKindLabel(item.Kind)).Distinct(StringComparer.OrdinalIgnoreCase));
                return $@"<a class='release-choice {(release.Load ? "active" : "")}' href='/release?key={Url(Encode(group.Key))}&amp;release={Url(Encode(release.Key))}'>
  <span>{(release.Load ? "Selected" : "Available")}</span>
  <strong>{H(release.Version)}</strong>
  <em>{release.Variants.Count} {(release.Variants.Count == 1 ? "file" : "files")} / {H(kinds)}</em>
</a>";
            }));
            var fileSections = string.Join("", group.Variants
                .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => KindOrder(item.Key))
                .Select(kindGroup => RenderPluginFileCategory(group, releases, kindGroup.Key, kindGroup.ToList())));
            var pinText = IsPinned(group) ? "Unpin" : "Pin always on";
            var categories = group.Variants.Select(item => item.Category)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var familyKinds = string.Join(" + ", group.Variants.Select(item => DetailKindLabel(item.Kind)).Distinct(StringComparer.OrdinalIgnoreCase));

            return $@"
<div class='manual plugin-details-page'>
  <header class='manual-head'>
    <div><a class='back' href='/details-back'>Back</a><h1>{H(group.Name)}</h1><div class='manual-meta'>{group.Variants.Count} files / {releases.Count} {(releases.Count == 1 ? "release" : "releases")} / {H(familyKinds)}</div></div>
    <div class='manual-actions'><a href='/pin?key={Url(Encode(group.Key))}&amp;return=details'>{pinText}</a><a class='fill' href='/toggle?key={Url(Encode(group.Key))}'>{(group.Load ? "Disable family" : "Enable family")}</a></div>
  </header>
  <section class='plugin-detail-head'><span class='mono-icon plugin-logo'>{PluginFamilyIconMarkup(group)}</span><div><strong>{H(activeRelease == null ? "No release selected" : "Version " + activeRelease.Version)}</strong><span>{H(categories.Count == 0 ? "No toolbar category reported" : string.Join(", ", categories))}</span></div></section>
  <section class='release-panel'><header><span>Installed versions</span><strong>Choose one release to load</strong></header><div class='release-choices'>{releaseButtons}</div></section>
  <section class='plugin-file-categories'>{fileSections}</section>
  <section class='detail-grid'><div><span>Used by presets</span><strong>{(presets.Count == 0 ? "None" : H(string.Join(", ", presets.Select(item => item.Name))))}</strong></div><div><span>Project folders</span><strong>{(string.IsNullOrWhiteSpace(projectFolders) ? "None linked" : "Linked through its presets")}</strong></div></section>
  {(string.IsNullOrWhiteSpace(projectFolders) ? string.Empty : $"<details class='review'><summary>Linked folders</summary><ul class='folder-list'>{projectFolders}</ul></details>")}
</div>";
        }

        private string RenderPluginFileCategory(PluginGroup group, IReadOnlyList<PluginRelease> releases, string kind, List<PluginCandidate> candidates)
        {
            var rows = string.Join("", candidates
                .OrderBy(item => item.ComponentName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => ParseVersion(item.Version))
                .ThenBy(item => item.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .Select(candidate =>
                {
                    var release = releases.First(item => item.Variants.Contains(candidate));
                    var displayName = candidate.Kind == "GHUSER"
                        ? FirstNonEmpty(candidate.ComponentName, Path.GetFileNameWithoutExtension(candidate.OriginalPath))
                        : Path.GetFileName(candidate.OriginalPath);
                    var category = candidate.Kind == "GHUSER"
                        ? FirstNonEmpty(candidate.SubCategory, candidate.Category, "Uncategorized")
                        : FirstNonEmpty(candidate.Category, "Plugin file");
                    var action = release.Load
                        ? "<span class='file-selected'>Selected</span>"
                        : $"<a href='/release?key={Url(Encode(group.Key))}&amp;release={Url(Encode(release.Key))}'>Use version</a>";
                    return $@"<tr class='{(candidate.Load ? "active-file" : "")}'>
  <td><span class='file-status'>{(candidate.Load ? "Active" : "Blocked")}</span></td>
  <td><strong>{H(displayName)}</strong><small>{H(category)}</small></td>
  <td>{H(GetCandidateVersion(candidate, release))}</td>
  <td class='path-cell' title='{Attr(candidate.OriginalPath)}'>{H(candidate.OriginalPath)}</td>
  <td>{action}</td>
</tr>";
                }));

            return $@"
<section class='file-category'>
  <header><div><span>{H(kind)}</span><h2>{H(DetailKindLabel(kind))}</h2></div><strong>{candidates.Count}</strong></header>
  <div class='detail-file-table'><table><thead><tr><th>Status</th><th>File</th><th>Version</th><th>Path</th><th></th></tr></thead><tbody>{rows}</tbody></table></div>
</section>";
        }

        private static string DetailKindLabel(string kind)
        {
            return string.Equals(kind, "GHA", StringComparison.OrdinalIgnoreCase) ? "Grasshopper assemblies"
                : string.Equals(kind, "GHPY", StringComparison.OrdinalIgnoreCase) ? "Python components"
                : string.Equals(kind, "GHUSER", StringComparison.OrdinalIgnoreCase) ? "User objects"
                : FirstNonEmpty(kind, "Other files");
        }

        private string RenderScanChanges()
        {
            if (_settings.LastScanChanges.Count == 0)
                return "<div class='muted'>No recorded changes. Refresh scan to compare the current folders with the previous scan.</div>";

            return "<div class='scan-changes'>" + string.Join("", _settings.LastScanChanges.Select(change =>
                $"<div><strong>{H(change.Kind)}</strong><span>{H(change.PluginName)}</span><small>{H(change.Detail)}</small></div>")) + "</div>";
        }

        private static string FormatLocalTime(string value)
        {
            return DateTime.TryParse(value, out var time) ? time.ToLocalTime().ToString("g") : string.Empty;
        }

        private static string FeatureCss()
        {
            return @"
.pin-dot{display:inline-grid;place-items:center;min-width:28px;height:24px;border:1.5px solid #111;border-radius:999px;background:#fff;font-size:10px;font-weight:900}.pin-dot.pinned{background:#111;color:#fff}.plugin-link:hover strong{text-decoration:underline}.preflight-summary{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;margin:14px 24px}.preflight-summary>div{min-height:96px;border:1.5px solid #111;border-radius:12px;padding:12px;background:#fff;display:flex;flex-direction:column;justify-content:space-between}.preflight-summary span,.detail-grid span,.document-recommendation span{font-size:10px;text-transform:uppercase;letter-spacing:.08em;color:#555}.preflight-summary strong{font-size:30px;line-height:1}.preflight-summary em{font-style:normal;font-size:11px;color:#666}.preflight-table{max-height:360px}.preflight-table tr.missing td:first-child{color:#b51d16}.preflight-table tr.closest td:first-child,.preflight-table tr.check td:first-child{color:#966d00}.preflight-table tr.ready td:first-child,.preflight-table tr.pinned td:first-child,.preflight-table tr.resolved td:first-child{color:#147468}.document-recommendation{margin:14px 24px;border:1.5px solid #111;border-radius:14px;padding:11px 12px;background:#fff;display:flex;align-items:center;gap:10px}.document-recommendation strong{display:inline-flex;align-items:center;gap:6px;flex:1;font-size:14px}.document-recommendation a,.association-list a{border:1.5px solid #111;border-radius:999px;padding:6px 10px;font-size:11px;font-weight:800}.association-list{display:flex;gap:6px;flex-wrap:wrap;margin-top:10px}.association-list a{display:inline-flex;gap:5px;align-items:center;background:#fff}.plugin-detail-head{margin:14px 24px;display:flex;gap:10px;align-items:center}.plugin-detail-head .mono-icon{width:46px;height:46px;border-radius:12px}.plugin-detail-head div{display:flex;flex-direction:column;gap:3px}.plugin-detail-head strong{font-size:15px}.plugin-detail-head span{font-size:12px;color:#666}.detail-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;margin:14px 24px}.detail-grid>div{border-top:1.5px solid #111;padding:9px 0;display:flex;flex-direction:column;gap:5px}.detail-grid strong{font-size:13px;line-height:1.35}.folder-list{margin:8px 0 0;padding-left:18px;font-size:12px;line-height:1.7}.scan-changes{display:grid;gap:5px;margin-top:12px}.scan-changes>div{display:grid;grid-template-columns:64px minmax(120px,1fr) minmax(160px,2fr);gap:8px;border-top:1px solid #d8d8d3;padding:6px 0;font-size:11px}.scan-changes strong{text-transform:uppercase;letter-spacing:.06em}.scan-changes small{color:#666;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}@media(max-width:700px){.preflight-summary,.detail-grid{grid-template-columns:1fr}.document-recommendation{align-items:flex-start;flex-wrap:wrap}.scan-changes>div{grid-template-columns:56px 1fr}.scan-changes small{grid-column:2}.preset-pill{max-width:100%;overflow-x:auto}}";
        }

        private static string PluginDetailCss()
        {
            return @"
.plugin-details-page{background-color:#f8f8f6;background-image:radial-gradient(rgba(0,0,0,.16) .7px,transparent .8px);background-size:9px 9px}.plugin-details-page .manual-head{background-color:rgba(248,248,246,.97)}.release-panel{margin:12px 24px 18px;padding-top:10px;border-top:1.5px solid #111}.release-panel>header{display:flex;align-items:baseline;justify-content:space-between;gap:12px;margin-bottom:8px}.release-panel>header span,.file-category>header span{color:#666;font-size:9px;font-weight:800;letter-spacing:.1em;text-transform:uppercase}.release-panel>header strong{font-size:12px}.release-choices{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:8px}.release-choice{display:flex;min-height:86px;flex-direction:column;justify-content:space-between;padding:9px;border:1.5px solid #111;border-radius:8px;background:#fff;box-shadow:2px 2px 0 #111;transition:transform .12s ease,box-shadow .12s ease}.release-choice:hover{transform:translate(1px,1px);box-shadow:1px 1px 0 #111}.release-choice.active{background:#111;color:#fff}.release-choice span{font-size:8px;letter-spacing:.09em;text-transform:uppercase;opacity:.7}.release-choice strong{font-size:17px}.release-choice em{font-size:9px;font-style:normal;line-height:1.3;opacity:.68}.plugin-file-categories{margin:0 24px}.file-category{padding:12px 0 16px;border-top:1.5px solid #111}.file-category>header{display:flex;align-items:center;justify-content:space-between;margin-bottom:8px}.file-category>header div{display:flex;align-items:baseline;gap:9px}.file-category h2{margin:0;font-size:16px}.file-category>header>strong{display:grid;min-width:25px;height:25px;place-items:center;border:1.5px solid #111;border-radius:50%;font-size:10px}.detail-file-table{overflow:auto;border:1.5px solid #111;border-radius:8px;background:#fff}.detail-file-table table{min-width:850px}.detail-file-table th{padding:7px 9px;font-size:9px}.detail-file-table td{padding:7px 9px;font-size:10px}.detail-file-table td:nth-child(2){min-width:155px}.detail-file-table td:nth-child(2) strong,.detail-file-table td:nth-child(2) small{display:block}.detail-file-table td:nth-child(2) small{margin-top:2px;color:#777;font-size:9px}.detail-file-table .path-cell{max-width:460px}.detail-file-table tr.active-file{background:#ededeb}.file-status{font-size:9px;font-weight:800;text-transform:uppercase}.active-file .file-status{color:#147468}.detail-file-table td:last-child{text-align:right}.detail-file-table td:last-child a,.file-selected{display:inline-block;padding:4px 7px;border:1px solid #111;border-radius:999px;font-size:9px;font-weight:800;white-space:nowrap}.detail-file-table td:last-child a:hover{background:#111;color:#fff}.file-selected{border-color:transparent;color:#147468}@media(max-width:900px){.release-choices{grid-template-columns:repeat(2,minmax(0,1fr))}}";
        }

        private sealed class ImportedPreset
        {
            public string Name { get; set; } = string.Empty;
            public string Icon { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public List<string> PluginPaths { get; } = new List<string>();
            public List<string> ProjectFolders { get; } = new List<string>();
        }

        private sealed class PreflightItem
        {
            public PreflightItem(string status, string title, string detail)
            {
                Status = status;
                Title = title;
                Detail = detail;
            }

            public string Status { get; }
            public string Title { get; }
            public string Detail { get; }
        }
    }
}
