using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Sieve.Models;
using Sieve.Services;

namespace Sieve.UI
{
    public sealed partial class SieveDialog : Dialog
    {
        private readonly PluginScanner _scanner = new PluginScanner();
        private readonly WebView _webView = new WebView();
        private readonly List<PluginCandidate> _candidates = new List<PluginCandidate>();
        private readonly object _sync = new object();
        private SieveSettings _settings;
        private TcpListener _server;
        private CancellationTokenSource _serverCancel;
        private string _baseUrl = string.Empty;
        private string _screen = "home";
        private string _query = string.Empty;
        private bool _showOnlyDuplicates;
        private bool _isScanning;
        private ScanProgressState _scanProgress = new ScanProgressState();
        private CancellationTokenSource _scanCancel;
        private DocumentAnalysisResult _documentResult;
        private string _editingPresetName = string.Empty;
        private bool _presetEditorOpen;
        private string _detailKey = string.Empty;
        private string _detailReturnScreen = "manual";
        private string _launchLabel = "Manual";
        private string _layoutMode = string.Empty;

        public SieveDialog()
        {
            Title = "Sieve";
            ClientSize = new Size(920, 320);
            MinimumSize = new Size(860, 300);
            Resizable = true;
            Padding = 0;

            _settings = SettingsStore.Load();
            NormalizeSettings();
            var unsupportedRestoreMessages = PluginGate.RestoreUnsupportedDisabled(_settings);
            if (unsupportedRestoreMessages.Count > 0)
                _settings.LastScanReport = "Restored old unsupported RHP entries where possible:" + Environment.NewLine + string.Join(Environment.NewLine, unsupportedRestoreMessages);
            _candidates.AddRange(_settings.LastScan ?? new List<PluginCandidate>());
            RemoveUnsupportedCachedCandidates();
            CanonicalizeDuplicateGroups(_candidates);
            PersistCandidates();

            Closed += (_, _) => StopServer();
            Content = _webView;
            StartServer();
            _webView.Url = new Uri(_baseUrl);
        }

        private void StartServer()
        {
            _serverCancel = new CancellationTokenSource();
            _server = new TcpListener(IPAddress.Loopback, 0);
            _server.Start();
            var port = ((IPEndPoint)_server.LocalEndpoint).Port;
            _baseUrl = $"http://127.0.0.1:{port}/";
            _ = Task.Run(() => ServerLoop(_serverCancel.Token));
        }

        private void StopServer()
        {
            try
            {
                _serverCancel?.Cancel();
                _scanCancel?.Cancel();
                _server?.Stop();
            }
            catch
            {
                // Nothing useful to report during dialog shutdown.
            }
        }

        private async Task ServerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _server.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClient(client), token);
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                        await Task.Delay(100, token);
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
                    var request = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(request))
                        return;

                    var parts = request.Split(' ');
                    var method = parts.Length > 0 ? parts[0] : "GET";
                    var target = parts.Length > 1 ? parts[1] : "/";
                    var headers = ReadHeaders(reader);
                    var body = ReadBody(stream, headers);

                    if (method == "GET" && string.Equals(target.Split('?')[0], "/brand-logo.png", StringComparison.OrdinalIgnoreCase))
                    {
                        var logo = ReadBrandLogo();
                        if (logo.Length > 0)
                        {
                            WriteResponse(stream, logo, "image/png", cache: true);
                            return;
                        }
                    }

                    if (method == "GET" && string.Equals(target.Split('?')[0], "/preset-icon.png", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetParts = target.Split(new[] { '?' }, 2);
                        var iconQuery = ParseQuery(targetParts.Length > 1 ? targetParts[1] : string.Empty);
                        var iconName = iconQuery.TryGetValue("name", out var requestedIcon) ? WebUtility.UrlDecode(requestedIcon) : string.Empty;
                        var iconBytes = ReadPresetIcon(iconName);
                        if (iconBytes.Length > 0)
                        {
                            WriteResponse(stream, iconBytes, "image/png", cache: true);
                            return;
                        }
                    }

                    if (method == "GET" && string.Equals(target.Split('?')[0], "/scan-status", StringComparison.OrdinalIgnoreCase))
                    {
                        var status = Encoding.UTF8.GetBytes(GetScanStatusJson());
                        WriteResponse(stream, status, "application/json; charset=utf-8", cache: false);
                        return;
                    }

                    var response = HandleRoute(method, target, body);
                    var bytes = Encoding.UTF8.GetBytes(response);
                    WriteResponse(stream, bytes, "text/html; charset=utf-8", cache: false);
                }
                catch
                {
                    // Keep the tiny local server alive if one request fails.
                }
            }
        }

        private static void WriteResponse(Stream stream, byte[] bytes, string contentType, bool cache)
        {
            var cacheControl = cache ? "public, max-age=3600" : "no-store";
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nCache-Control: {cacheControl}\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static byte[] ReadBrandLogo()
        {
            return ReadEmbeddedResource("Sieve.BrandLogo.png");
        }

        private static byte[] ReadPresetIcon(string iconName)
        {
            if (string.IsNullOrWhiteSpace(iconName) || iconName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                return Array.Empty<byte>();
            return ReadEmbeddedResource("Sieve.PresetIcons." + iconName + ".png");
        }

        private static byte[] ReadEmbeddedResource(string resourceName)
        {
            try
            {
                using var source = typeof(SieveDialog).Assembly.GetManifestResourceStream(resourceName);
                if (source == null)
                    return Array.Empty<byte>();
                using var output = new MemoryStream();
                source.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static Dictionary<string, string> ReadHeaders(StreamReader reader)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    headers[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }
            return headers;
        }

        private static byte[] ReadBody(Stream stream, Dictionary<string, string> headers)
        {
            if (!headers.TryGetValue("Content-Length", out var lengthText) || !int.TryParse(lengthText, out var length) || length <= 0)
                return Array.Empty<byte>();

            var body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(body, offset, length - offset);
                if (read <= 0)
                    break;
                offset += read;
            }
            return body;
        }

        private string HandleRoute(string method, string target, byte[] body)
        {
            var split = target.Split(new[] { '?' }, 2);
            var path = WebUtility.UrlDecode(split[0] ?? "/") ?? "/";
            var query = ParseQuery(split.Length > 1 ? split[1] : string.Empty);

            lock (_sync)
            {
                if (method == "POST" && path == "/document-upload")
                {
                    var name = query.TryGetValue("name", out var uploadName) ? WebUtility.UrlDecode(uploadName) : "Dropped Grasshopper file";
                    AnalyzeDocument(name, body);
                    _screen = "document";
                    return RenderHtml();
                }

                switch (path)
                {
                    case "/":
                    case "/home":
                        _screen = "home";
                        _presetEditorOpen = false;
                        _editingPresetName = string.Empty;
                        break;
                    case "/manual":
                        _screen = "manual";
                        _presetEditorOpen = false;
                        _editingPresetName = string.Empty;
                        break;
                    case "/preflight":
                        _screen = "preflight";
                        break;
                    case "/preflight-launch":
                        LaunchFromServer();
                        return LaunchingPage();
                    case "/history":
                        _screen = "history";
                        break;
                    case "/details":
                        if (query.TryGetValue("key", out var detailKey))
                            _detailKey = Decode(detailKey);
                        _detailReturnScreen = query.TryGetValue("from", out var detailFrom) && detailFrom == "preset"
                            ? "preset"
                            : "manual";
                        _screen = "details";
                        break;
                    case "/details-back":
                        _screen = "manual";
                        _presetEditorOpen = _detailReturnScreen == "preset";
                        break;
                    case "/pin":
                        if (query.TryGetValue("key", out var pinKey))
                            TogglePin(Decode(pinKey));
                        _screen = query.TryGetValue("return", out var pinReturn) && pinReturn == "details"
                            ? "details"
                            : "manual";
                        break;
                    case "/document":
                        _screen = "document";
                        break;
                    case "/choose-document":
                        ChooseDocumentFromServer();
                        _screen = "document";
                        break;
                    case "/document-load":
                        ApplyDocumentLoadSet(query.TryGetValue("launch", out var documentLaunch) && documentLaunch == "1");
                        if (documentLaunch == "1")
                            return LaunchingPage();
                        _screen = "manual";
                        break;
                    case "/document-preflight":
                        ApplyDocumentLoadSet(false);
                        _screen = "preflight";
                        break;
                    case "/new-preset":
                        _screen = "manual";
                        _editingPresetName = string.Empty;
                        _presetEditorOpen = true;
                        _query = string.Empty;
                        SetAll(false, false);
                        break;
                    case "/basics":
                        LaunchBasicsFromServer();
                        return LaunchingPage();
                    case "/all":
                        LaunchAllFromServer();
                        return LaunchingPage();
                    case "/enable-all":
                        SetEveryManagedPlugin(true);
                        _screen = "manual";
                        break;
                    case "/scan":
                    case "/scan-settings":
                        _screen = "scan-settings";
                        break;
                    case "/scan-start":
                        StartBackgroundScan();
                        _screen = "scan-progress";
                        break;
                    case "/scan-progress":
                        _screen = "scan-progress";
                        break;
                    case "/scan-cancel":
                        _scanCancel?.Cancel();
                        _scanProgress.Message = "Cancelling scan...";
                        _screen = "scan-progress";
                        break;
                    case "/scan-finished":
                        _screen = "manual";
                        break;
                    case "/scan-path-toggle":
                        if (query.TryGetValue("path", out var scanPath))
                            SetScanPathEnabled(Decode(scanPath), query.TryGetValue("enabled", out var enabledPath) && enabledPath == "1");
                        _screen = "scan-settings";
                        break;
                    case "/restore":
                        Restore(false);
                        break;
                    case "/set-all":
                        SetAll(query.TryGetValue("load", out var load) && load == "1", false);
                        break;
                    case "/toggle":
                        if (query.TryGetValue("key", out var toggleKey))
                            ToggleGroup(Decode(toggleKey));
                        break;
                    case "/variant":
                        if (query.TryGetValue("path", out var variantPath))
                            SelectVariant(Decode(variantPath));
                        break;
                    case "/release":
                        if (query.TryGetValue("key", out var releasePluginKey) &&
                            query.TryGetValue("release", out var releaseKey))
                            SelectRelease(Decode(releasePluginKey), Decode(releaseKey));
                        _screen = "details";
                        break;
                    case "/filter":
                        if (query.TryGetValue("dupes", out var dupes))
                            _showOnlyDuplicates = dupes == "1";
                        if (query.TryGetValue("q", out var q))
                            _query = WebUtility.UrlDecode(q) ?? string.Empty;
                        _screen = "manual";
                        break;
                    case "/view":
                        _settings.PluginViewMode = query.TryGetValue("mode", out var viewMode) && viewMode == "list" ? "list" : "grid";
                        SettingsStore.Save(_settings);
                        _screen = "manual";
                        break;
                    case "/save-preset":
                        if (query.TryGetValue("name", out var presetName))
                        {
                            query.TryGetValue("icon", out var presetIcon);
                            query.TryGetValue("description", out var presetDescription);
                            SavePreset(
                                WebUtility.UrlDecode(presetName),
                                WebUtility.UrlDecode(presetIcon ?? string.Empty),
                                WebUtility.UrlDecode(presetDescription ?? string.Empty),
                                false);
                        }
                        _presetEditorOpen = false;
                        _screen = "home";
                        break;
                    case "/edit-preset":
                        if (query.TryGetValue("name", out var editName))
                            BeginPresetEdit(WebUtility.UrlDecode(editName));
                        break;
                    case "/duplicate-preset":
                        if (query.TryGetValue("name", out var duplicateName))
                        {
                            DuplicatePreset(WebUtility.UrlDecode(duplicateName));
                            BeginPresetEdit(_editingPresetName);
                        }
                        break;
                    case "/move-preset":
                        if (query.TryGetValue("name", out var moveName) && query.TryGetValue("direction", out var direction))
                            MovePreset(WebUtility.UrlDecode(moveName), direction == "up" ? -1 : 1);
                        _screen = "manual";
                        break;
                    case "/reorder-presets":
                        if (query.TryGetValue("order", out var presetOrder))
                            ReorderPresets(Decode(presetOrder));
                        _screen = "home";
                        break;
                    case "/import-preset":
                        ImportPresetFromServer();
                        if (!string.IsNullOrWhiteSpace(_editingPresetName))
                            BeginPresetEdit(_editingPresetName);
                        break;
                    case "/associate-preset":
                        if (query.TryGetValue("name", out var associateName))
                            AssociatePresetFolder(WebUtility.UrlDecode(associateName));
                        _screen = "manual";
                        break;
                    case "/associate-document-preset":
                        if (query.TryGetValue("name", out var documentPresetName))
                            AssociateDocumentWithPreset(WebUtility.UrlDecode(documentPresetName));
                        _screen = "document";
                        break;
                    case "/apply-preset":
                        if (query.TryGetValue("name", out var applyName))
                        {
                            var launch = query.TryGetValue("launch", out var launchValue) && launchValue == "1";
                            ApplyPreset(WebUtility.UrlDecode(applyName), launch, false);
                            if (launch)
                                return LaunchingPage();
                        }
                        break;
                    case "/delete-preset":
                        if (query.TryGetValue("name", out var deleteName))
                            DeletePreset(WebUtility.UrlDecode(deleteName));
                        break;
                    case "/export-preset":
                        if (query.TryGetValue("name", out var exportName))
                            ExportPreset(WebUtility.UrlDecode(exportName));
                        break;
                    case "/add-path":
                        AddPathFromServer();
                        _screen = query.TryGetValue("return", out var addPathReturn) && addPathReturn == "scan-settings"
                            ? "scan-settings"
                            : "manual";
                        break;
                    case "/remove-path":
                        if (query.TryGetValue("path", out var removePath))
                            RemovePath(Decode(removePath));
                        if (query.TryGetValue("return", out var removePathReturn) && removePathReturn == "scan-settings")
                            _screen = "scan-settings";
                        break;
                    case "/launch":
                        LaunchFromServer();
                        return LaunchingPage();
                }

                return RenderHtml();
            }
        }

        private void ScanNow()
        {
            if (_isScanning)
                return;

            _isScanning = true;
            try
            {
                var previous = _candidates.ToList();
                var results = _scanner.ScanRoots(GetEnabledScanRoots(), null, CancellationToken.None).ToList();
                CanonicalizeDuplicateGroups(results);
                _candidates.Clear();
                _candidates.AddRange(results);
                RemoveUnsupportedCachedCandidates();
                _settings.LastScan = _candidates.ToList();
                _settings.LastScanChanges = CalculateScanChanges(previous, _candidates);
                _settings.LastScanUtc = DateTime.UtcNow.ToString("O");
                _settings.LastScanReport = BuildScanReport(results);
                SettingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                _settings.LastScanReport = "Scan failed: " + ex.Message;
            }
            finally
            {
                _isScanning = false;
            }
        }

        private void StartBackgroundScan()
        {
            if (_isScanning)
                return;

            var roots = GetEnabledScanRoots();
            if (roots.Count == 0)
            {
                _scanProgress = new ScanProgressState
                {
                    Phase = "No folders selected",
                    Message = "Enable at least one existing scan folder.",
                    Percent = 0,
                    IsComplete = true,
                    HasError = true
                };
                return;
            }

            _scanCancel?.Dispose();
            _scanCancel = new CancellationTokenSource();
            var token = _scanCancel.Token;
            var previous = _candidates.ToList();
            _isScanning = true;
            _scanProgress = new ScanProgressState
            {
                Phase = "Starting",
                Message = $"Preparing {roots.Count} scan folders",
                Percent = 1,
                IsRunning = true,
                RecentMessages = roots.Take(4).Select(path => "Queued: " + path).ToList()
            };

            _ = Task.Run(() =>
            {
                try
                {
                    var results = _scanner.ScanRoots(roots, UpdateScanProgress, token).ToList();
                    token.ThrowIfCancellationRequested();
                    CanonicalizeDuplicateGroups(results);

                    lock (_sync)
                    {
                        _candidates.Clear();
                        _candidates.AddRange(results);
                        RemoveUnsupportedCachedCandidates();
                        _settings.LastScan = _candidates.ToList();
                        _settings.LastScanChanges = CalculateScanChanges(previous, _candidates);
                        _settings.LastScanUtc = DateTime.UtcNow.ToString("O");
                        _settings.LastScanReport = BuildScanReport(results);
                        SettingsStore.Save(_settings);
                        _scanProgress = new ScanProgressState
                        {
                            Phase = "Complete",
                            Message = $"Found {results.Count} files in {BuildGroups().Count} plugin families",
                            Percent = 100,
                            FilesDiscovered = results.Count,
                            FilesProcessed = results.Count,
                            TotalFiles = results.Count,
                            IsComplete = true,
                            RecentMessages = _scanProgress.RecentMessages
                                .Concat(new[] { "Scan complete. Results saved to scan history." })
                                .TakeLast(8)
                                .ToList()
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_sync)
                    {
                        _scanProgress.IsRunning = false;
                        _scanProgress.IsComplete = true;
                        _scanProgress.IsCancelled = true;
                        _scanProgress.Phase = "Cancelled";
                        _scanProgress.Message = "The previous scan cache was kept unchanged.";
                    }
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        _settings.LastScanReport = "Scan failed: " + ex.Message;
                        SettingsStore.Save(_settings);
                        _scanProgress.IsRunning = false;
                        _scanProgress.IsComplete = true;
                        _scanProgress.HasError = true;
                        _scanProgress.Phase = "Scan failed";
                        _scanProgress.Message = ex.Message;
                    }
                }
                finally
                {
                    lock (_sync)
                        _isScanning = false;
                }
            });
        }

        private void UpdateScanProgress(ScanProgressState progress)
        {
            lock (_sync)
            {
                var recent = _scanProgress.RecentMessages ?? new List<string>();
                if (!string.IsNullOrWhiteSpace(progress.CurrentPath) &&
                    !string.Equals(progress.CurrentPath, _scanProgress.CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    recent = recent.Concat(new[] { progress.Phase + ": " + progress.CurrentPath })
                        .TakeLast(8)
                        .ToList();
                }
                progress.RecentMessages = recent;
                _scanProgress = progress;
            }
        }

        private string GetScanStatusJson()
        {
            lock (_sync)
            {
                return JsonSerializer.Serialize(new
                {
                    phase = _scanProgress.Phase,
                    message = _scanProgress.Message,
                    currentPath = _scanProgress.CurrentPath,
                    percent = _scanProgress.Percent,
                    filesDiscovered = _scanProgress.FilesDiscovered,
                    filesProcessed = _scanProgress.FilesProcessed,
                    totalFiles = _scanProgress.TotalFiles,
                    isRunning = _scanProgress.IsRunning,
                    isComplete = _scanProgress.IsComplete,
                    isCancelled = _scanProgress.IsCancelled,
                    hasError = _scanProgress.HasError,
                    recentMessages = _scanProgress.RecentMessages ?? new List<string>()
                });
            }
        }

        private List<string> GetEnabledScanRoots()
        {
            return _scanner.GetDefaultRootOptions()
                .Concat(_settings.CustomPaths)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(Directory.Exists)
                .Where(path => !_settings.DisabledScanPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void SetScanPathEnabled(string path, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (enabled)
                _settings.DisabledScanPaths.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            else if (!_settings.DisabledScanPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                _settings.DisabledScanPaths.Add(path);

            _settings.DisabledScanPaths = _settings.DisabledScanPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();
            SettingsStore.Save(_settings);
        }

        private void LaunchBasicsFromServer()
        {
            if (_candidates.Count == 0)
                return;

            foreach (var candidate in _candidates)
                candidate.Load = false;

            _launchLabel = "Basics";
            PersistCandidates();
            LaunchFromServer();
        }

        private void LaunchAllFromServer()
        {
            if (_candidates.Count == 0)
                ScanNow();

            SetEveryManagedPlugin(true);
            _launchLabel = "All plugins";
            LaunchFromServer();
        }

        private void LaunchFromServer()
        {
            var candidates = _candidates.ToList();
            Application.Instance.AsyncInvoke(() =>
            {
                var errors = PluginGate.ApplySelection(candidates);
                if (errors.Count > 0)
                    MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Some plugins could not be toggled", MessageBoxButtons.OK, MessageBoxType.Warning);

                var launchError = PluginGate.QueueGrasshopperLaunch(_launchLabel, candidates.Count(candidate => candidate.Load));
                if (!string.IsNullOrWhiteSpace(launchError))
                {
                    MessageBox.Show(this, launchError, "Grasshopper could not start", MessageBoxButtons.OK, MessageBoxType.Warning);
                    return;
                }

                Close();
            });
        }

        private void AddPathFromServer()
        {
            var wait = new ManualResetEventSlim(false);
            string selected = null;
            Application.Instance.AsyncInvoke(() =>
            {
                using var dialog = new SelectFolderDialog { Title = "Add a plugin search folder" };
                if (dialog.ShowDialog(this) == DialogResult.Ok)
                    selected = dialog.Directory;
                wait.Set();
            });

            wait.Wait();
            if (!string.IsNullOrWhiteSpace(selected) && !_settings.CustomPaths.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                _settings.CustomPaths.Add(selected);
                _settings.DisabledScanPaths.RemoveAll(path => string.Equals(path, selected, StringComparison.OrdinalIgnoreCase));
                SettingsStore.Save(_settings);
            }
        }

        private void ChooseDocumentFromServer()
        {
            var wait = new ManualResetEventSlim(false);
            string selected = null;
            Application.Instance.AsyncInvoke(() =>
            {
                using var dialog = new OpenFileDialog { Title = "Open Grasshopper document" };
                dialog.Filters.Add(new FileFilter("Grasshopper documents", ".gh", ".ghx"));
                if (dialog.ShowDialog(this) == DialogResult.Ok)
                    selected = dialog.FileName;
                wait.Set();
            });

            wait.Wait();
            if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected))
                AnalyzeDocument(Path.GetFileName(selected), File.ReadAllBytes(selected), selected);
        }

        private void AnalyzeDocument(string fileName, byte[] bytes, string sourcePath = "")
        {
            if (_candidates.Count == 0)
                ScanNow();

            var analyzer = new GrasshopperDocumentAnalyzer();
            _documentResult = analyzer.Analyze(fileName, bytes, _candidates);
            _documentResult.SourcePath = sourcePath ?? string.Empty;
        }

        private void ApplyDocumentLoadSet(bool launch)
        {
            if (_documentResult == null)
                return;

            foreach (var candidate in _candidates)
                candidate.Load = false;

            foreach (var candidate in _candidates.Where(candidate => _settings.PinnedPluginPaths.Contains(candidate.OriginalPath, StringComparer.OrdinalIgnoreCase)))
                candidate.Load = true;

            foreach (var match in _documentResult.Matches.Where(match => match.Candidate != null))
            {
                if (match.Candidate.Kind == "GHUSER")
                {
                    foreach (var candidate in _candidates.Where(candidate => candidate.Kind == "GHUSER" && string.Equals(candidate.Category, match.Candidate.Category, StringComparison.OrdinalIgnoreCase)))
                        candidate.Load = true;
                    continue;
                }

                var group = BuildGroups().FirstOrDefault(item => item.Variants.Any(candidate => string.Equals(candidate.OriginalPath, match.Candidate.OriginalPath, StringComparison.OrdinalIgnoreCase)));
                if (group != null)
                {
                    foreach (var candidate in group.Variants)
                        candidate.Load = false;
                }

                match.Candidate.Load = true;
            }

            PersistCandidates();
            _launchLabel = "Document / " + _documentResult.FileName;
            if (launch)
                LaunchFromServer();
        }

        private string RenderHtml()
        {
            ResizeForCurrentScreen();
            var body = _screen == "manual" && _presetEditorOpen ? RenderPresetEditor()
                : _screen == "manual" ? RenderManual()
                : _screen == "scan-settings" ? RenderScanSettings()
                : _screen == "scan-progress" ? RenderScanProgress()
                : _screen == "document" ? RenderDocument()
                : _screen == "preflight" ? RenderPreflight()
                : _screen == "history" ? RenderHistory()
                : _screen == "details" ? RenderPluginDetails()
                : RenderHome();
            return $@"<!doctype html>
<html>
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width, initial-scale=1'>
  <style>{Css()}{IconCss()}{PluginIconCss()}{FeatureCss()}{PluginDetailCss()}{ScanWorkflowCss()}{ScanWorkflowLayerCss()}{HomeCss()}{PresetMenuCss()}{ManualRedesignCss()}{GridCardCss()}{DenseLayoutCss()}{StartupLayoutCss()}{PresetEditorCss()}{PresetEditorLayerCss()}</style>
</head>
<body>
  {body}
  <script>
    function go(path) {{ window.location.href = path; }}
    function savePreset() {{
      const name = document.getElementById('presetName');
      const icon = document.getElementById('presetIcon');
      const description = document.getElementById('presetDescription');
      const valueOf = function(element) {{
        if (!element) return '';
        return typeof element.value === 'string' ? element.value : (element.textContent || '').trim();
      }};
      go('/save-preset?name=' + encodeURIComponent(valueOf(name)) +
        '&icon=' + encodeURIComponent(icon ? icon.value : '') +
        '&description=' + encodeURIComponent(valueOf(description)));
    }}
    function choosePresetIcon(button) {{
      const input = document.getElementById('presetIcon');
      if (!input || !button) return;
      input.value = button.dataset.icon || '{DefaultPresetIcon}';
      document.querySelectorAll('.icon-choice').forEach(function(choice) {{ choice.classList.remove('selected'); }});
      button.classList.add('selected');
      const preview = document.getElementById('presetIconPreview');
      const icon = button.querySelector('.preset-icon');
      if (preview && icon) preview.innerHTML = icon.outerHTML;
      const popover = document.getElementById('editorIconPopover');
      if (popover) popover.classList.add('hidden');
      if (preview) preview.setAttribute('aria-expanded', 'false');
    }}
    function togglePresetIconPicker(event) {{
      if (event) event.stopPropagation();
      const popover = document.getElementById('editorIconPopover');
      const trigger = document.getElementById('presetIconPreview');
      if (!popover || !trigger) return;
      const hidden = popover.classList.toggle('hidden');
      trigger.setAttribute('aria-expanded', hidden ? 'false' : 'true');
      const tile = trigger.closest('.preset-live-tile');
      if (tile) tile.classList.toggle('icon-open', !hidden);
    }}
    function filterPresetPlugins(value) {{
      const query = (value || '').trim().toLowerCase();
      const cards = document.querySelectorAll('.preset-plugin-card');
      let visible = 0;
      cards.forEach(function(card) {{
        const matches = !query || (card.dataset.search || '').indexOf(query) !== -1;
        card.classList.toggle('filtered-out', !matches);
        if (matches) visible += 1;
      }});
      const result = document.getElementById('presetSearchCount');
      if (result) result.textContent = query ? visible + ' shown' : '';
    }}
    function setPresetPlugins(enabled) {{
      fetch('/set-all?load=' + (enabled ? '1' : '0'), {{ cache: 'no-store' }}).then(function(response) {{
        if (!response.ok) throw new Error('Selection failed');
        document.querySelectorAll('.preset-plugin-card').forEach(function(card) {{
          card.classList.toggle('enabled', enabled);
          card.classList.toggle('blocked', !enabled);
          card.setAttribute('aria-pressed', enabled ? 'true' : 'false');
          const status = card.querySelector('.preset-plugin-status');
          if (status) status.textContent = enabled ? '[enabled]' : '[disabled]';
        }});
        updatePresetEditorCount();
      }});
    }}
    function updatePresetEditorCount() {{
      const selected = document.querySelectorAll('.preset-plugin-card.enabled').length;
      document.querySelectorAll('[data-preset-count]').forEach(function(element) {{
        element.textContent = selected + (selected === 1 ? ' plugin' : ' plugins');
      }});
    }}
    function applySearch() {{
      const value = document.getElementById('searchBox').value || '';
      go('/filter?dupes={(_showOnlyDuplicates ? "1" : "0")}&q=' + encodeURIComponent(value));
    }}
    function wireScanProgress() {{
      const page = document.getElementById('scanProgressPage');
      if (!page) return;
      const poll = function() {{
        fetch('/scan-status', {{ cache: 'no-store' }}).then(function(response) {{
          if (!response.ok) throw new Error('Status unavailable');
          return response.json();
        }}).then(function(status) {{
          const percent = Math.max(0, Math.min(100, status.percent || 0));
          document.getElementById('scanPhase').textContent = status.phase || 'Scanning';
          document.getElementById('scanMessage').textContent = status.message || '';
          document.getElementById('scanPercent').textContent = percent + '%';
          document.getElementById('scanProgressBar').style.width = percent + '%';
          document.querySelector('.scan-progress-track').setAttribute('aria-valuenow', percent);
          document.getElementById('scanCounter').textContent = status.totalFiles > 0
            ? (status.filesProcessed || 0) + ' / ' + status.totalFiles + ' files'
            : (status.filesDiscovered || 0) + ' files discovered';
          document.getElementById('scanCurrentPath').textContent = status.currentPath || status.message || 'Preparing scan...';
          document.getElementById('scanDiscovered').textContent = (status.filesDiscovered || 0) + ' discovered';
          page.classList.toggle('scan-error', !!status.hasError);
          page.classList.toggle('scan-cancelled', !!status.isCancelled);
          const list = document.getElementById('scanActivityList');
          list.innerHTML = '';
          (status.recentMessages || []).forEach(function(message) {{
            const item = document.createElement('li');
            item.textContent = message;
            list.appendChild(item);
          }});
          if (!list.children.length) {{
            const item = document.createElement('li');
            item.textContent = 'Waiting for scanner activity...';
            list.appendChild(item);
          }}
          if (status.isComplete) {{
            document.getElementById('scanProgressAction').innerHTML = status.hasError || status.isCancelled
              ? `<a class='scan-progress-primary' href='/scan-settings'>Back to settings</a>`
              : `<a class='scan-progress-primary' href='/scan-finished'>View plugins</a>`;
          }} else {{
            setTimeout(poll, 220);
          }}
        }}).catch(function() {{
          setTimeout(poll, 600);
        }});
      }};
      poll();
    }}
    wireScanProgress();
    function uploadDocument(file) {{
      const target = '/document-upload?name=' + encodeURIComponent(file.name || 'Dropped Grasshopper file');
      fetch(target, {{ method: 'POST', body: file }}).then(function() {{ window.location.href = '/document'; }});
    }}
    function wireDropZone() {{
      const zone = document.getElementById('dropZone');
      if (!zone) return;
      zone.addEventListener('dragover', function(event) {{ event.preventDefault(); zone.classList.add('over'); }});
      zone.addEventListener('dragleave', function() {{ zone.classList.remove('over'); }});
      zone.addEventListener('drop', function(event) {{
        event.preventDefault();
        zone.classList.remove('over');
        if (event.dataTransfer.files.length > 0) uploadDocument(event.dataTransfer.files[0]);
      }});
      const input = document.getElementById('fileInput');
      if (input) input.addEventListener('change', function() {{ if (input.files.length > 0) uploadDocument(input.files[0]); }});
    }}
    wireDropZone();
    function wirePresetReorder() {{
      const strip = document.querySelector('.home-scroll');
      if (!strip) return;
      let dragging = null;
      let suppressClick = false;
      strip.querySelectorAll('.preset-home[data-preset]').forEach(function(tile) {{
        tile.addEventListener('dragstart', function(event) {{
          if (event.target.closest('.preset-menu')) {{ event.preventDefault(); return; }}
          dragging = tile;
          suppressClick = true;
          tile.classList.add('dragging');
          event.dataTransfer.effectAllowed = 'move';
          event.dataTransfer.setData('text/plain', tile.dataset.preset || 'preset');
        }});
        tile.addEventListener('dragend', function() {{
          tile.classList.remove('dragging');
          dragging = null;
          const names = Array.from(strip.querySelectorAll('.preset-home[data-preset]')).map(function(item) {{ return item.dataset.preset || ''; }});
          const bytes = new TextEncoder().encode(names.join('\n'));
          let binary = '';
          bytes.forEach(function(value) {{ binary += String.fromCharCode(value); }});
          fetch('/reorder-presets?order=' + encodeURIComponent(btoa(binary)), {{ cache: 'no-store' }});
          setTimeout(function() {{ suppressClick = false; }}, 0);
        }});
      }});
      strip.addEventListener('dragover', function(event) {{
        if (!dragging) return;
        event.preventDefault();
        const bounds = strip.getBoundingClientRect();
        if (event.clientX < bounds.left + 36) strip.scrollLeft -= 18;
        if (event.clientX > bounds.right - 36) strip.scrollLeft += 18;
        const target = event.target.closest('.preset-home[data-preset]');
        if (!target || target === dragging) return;
        const box = target.getBoundingClientRect();
        const after = event.clientX > box.left + box.width / 2;
        strip.insertBefore(dragging, after ? target.nextSibling : target);
      }});
      strip.addEventListener('drop', function(event) {{ event.preventDefault(); }});
      strip.addEventListener('click', function(event) {{
        if (!suppressClick) return;
        event.preventDefault();
        event.stopPropagation();
      }});
    }}
    wirePresetReorder();
    function togglePlugin(link) {{
      if (!link || link.dataset.busy === '1') return;
      link.dataset.busy = '1';
      link.classList.add('pending');
      fetch(link.href, {{ cache: 'no-store' }}).then(function(response) {{
        if (!response.ok) throw new Error('Toggle failed');
        const enabled = link.getAttribute('aria-pressed') !== 'true';
        const container = link.closest('.plugin-card,tr');
        if (link.matches('.card-load,.load-dot')) link.textContent = enabled ? 'ON' : 'OFF';
        link.setAttribute('aria-pressed', enabled ? 'true' : 'false');
        if (container) {{
          container.classList.toggle('enabled', enabled);
          container.classList.toggle('blocked', !enabled);
          const status = container.querySelector('.preset-plugin-status');
          if (status) status.textContent = enabled ? '[enabled]' : '[disabled]';
        }}
        updatePresetEditorCount();
        link.classList.remove('pending');
        delete link.dataset.busy;
      }}).catch(function() {{
        link.classList.remove('pending');
        link.classList.add('toggle-error');
        delete link.dataset.busy;
        setTimeout(function() {{ link.classList.remove('toggle-error'); }}, 900);
      }});
    }}
    document.addEventListener('click', function(event) {{
      const link = event.target.closest('a,button');
      if (link && link.matches('.card-load,.load-dot,.preset-plugin-card')) {{
        event.preventDefault();
        if (link._sieveToggleTimer) clearTimeout(link._sieveToggleTimer);
        link._sieveToggleTimer = setTimeout(function() {{
          link._sieveToggleTimer = 0;
          togglePlugin(link);
        }}, 230);
        return;
      }}
      if (link) link.classList.add('pressed');
    }});
    document.addEventListener('dblclick', function(event) {{
      const target = event.target.closest('[data-details]');
      if (!target || !target.dataset.details) return;
      const toggle = event.target.closest('.card-load,.load-dot,.preset-plugin-card');
      if (toggle && toggle._sieveToggleTimer) {{
        clearTimeout(toggle._sieveToggleTimer);
        toggle._sieveToggleTimer = 0;
      }}
      event.preventDefault();
      event.stopPropagation();
      go(target.dataset.details);
    }});
    document.addEventListener('click', function(event) {{
      const popover = document.getElementById('editorIconPopover');
      const trigger = document.getElementById('presetIconPreview');
      if (popover && trigger && !popover.contains(event.target) && !trigger.contains(event.target)) {{
        popover.classList.add('hidden');
        trigger.setAttribute('aria-expanded', 'false');
        const tile = trigger.closest('.preset-live-tile');
        if (tile) tile.classList.remove('icon-open');
      }}
    }});
    document.querySelectorAll('.preset-inline-edit').forEach(function(element) {{
      element.addEventListener('keydown', function(event) {{
        if (event.key === 'Enter') {{ event.preventDefault(); element.blur(); }}
      }});
      element.addEventListener('blur', function() {{
        if (!(element.textContent || '').trim()) element.textContent = element.dataset.fallback || '';
      }});
    }});
  </script>
</body>
</html>";
        }

        private void ResizeForCurrentScreen()
        {
            var mode = _screen == "home" ? "home" : "workspace";
            if (string.Equals(_layoutMode, mode, StringComparison.Ordinal))
                return;

            _layoutMode = mode;
            var size = mode == "home" ? new Size(920, 320) : new Size(1180, 610);
            var minimum = mode == "home" ? new Size(860, 300) : new Size(940, 500);
            Application.Instance.AsyncInvoke(() =>
            {
                MinimumSize = minimum;
                ClientSize = size;
            });
        }

        private string RenderHome()
        {
            var groups = BuildGroups();
            var enabled = groups.Count(group => group.Load);
            var presetButtons = _settings.Presets.Count == 0
                ? "<a class='home-tile disabled' href='/new-preset'><span>Preset</span><strong>No preset</strong><em>Create one with +</em><span class='tile-hover'>No saved plugin set yet.</span></a>"
                : string.Join("", _settings.Presets.Select(preset => $@"<div class='home-tile preset-home' draggable='true' data-preset='{Attr(preset.Name)}'>
  <a class='home-tile-main' href='/apply-preset?name={Url(preset.Name)}&launch=1'><span>Preset</span>{PresetIconMarkup(PresetIcon(preset))}<strong>{H(preset.Name)}</strong><em>{H(FirstNonEmpty(preset.Description, preset.PluginPaths.Count + " plugins"))}</em><span class='tile-hover'>{H(HomeHoverText(preset.PluginPaths.Select(path => _candidates.FirstOrDefault(candidate => string.Equals(candidate.OriginalPath, path, StringComparison.OrdinalIgnoreCase))?.Name ?? Path.GetFileNameWithoutExtension(path)), "No managed plugins"))}</span></a>
  <details class='preset-menu'><summary title='Preset actions' aria-label='Preset actions'>&#8942;</summary><div><a href='/edit-preset?name={Url(preset.Name)}'>Edit</a><a href='/export-preset?name={Url(preset.Name)}'>Export</a><a href='/delete-preset?name={Url(preset.Name)}'>Delete</a></div></details>
</div>"));
            var allPluginNames = groups.Select(group => group.Name);
            var activePluginNames = groups.Where(group => group.Load).Select(group => group.Name);

            return $@"
<div class='home'>
  <div class='tone'></div>
  <header class='home-head'>
    <div class='home-meta'>{groups.Count} unique / {enabled} active / {ScanAge()}</div>
  </header>
  <main class='home-main'>
    <div class='home-grid home-scroll'>
      <a class='home-tile fill' href='/basics'><span>01</span><strong>Basics</strong><em>Native Grasshopper only</em><span class='tile-hover'>Grasshopper native components only.</span></a>
      <a class='home-tile' href='/all'><span>All</span><strong>All Plugins</strong><em>Enable every managed plugin</em><span class='tile-hover'>{H(HomeHoverText(allPluginNames, "No scanned plugins"))}</span></a>
      {presetButtons}
      <a class='home-tile plus' href='/new-preset'><span>02</span><strong>+</strong><em>Make preset</em><span class='tile-hover'>{H(HomeHoverText(activePluginNames, "Start with an empty selection"))}</span></a>
      <a class='home-tile document-home' href='/document'><span>GH</span><strong>Open File</strong><em>Analyze required plugins</em><span class='tile-hover'>Reads the plugin requirements from a Grasshopper document.</span></a>
    </div>
    <a class='manual-selection' href='/manual'><span><strong>Manual selection</strong><em>Choose plugins individually</em></span><b aria-hidden='true'>&#8594;</b></a>
  </main>
  <footer class='home-foot'>
    <a href='{(_isScanning ? "/scan-progress" : "/scan")}'>{(_isScanning ? "View scan" : "Refresh scan")}</a>
    <a href='/history'>Launch history</a>
    <a href='/import-preset'>Import preset</a>
    <a href='/restore'>Restore disabled files</a>
  </footer>
</div>";
        }

        private static string HomeHoverText(IEnumerable<string> pluginNames, string fallback)
        {
            var names = (pluginNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0)
                return fallback;

            var visible = names.Take(6).ToList();
            var suffix = names.Count > visible.Count ? $" +{names.Count - visible.Count} more" : string.Empty;
            return string.Join(" / ", visible) + suffix;
        }

        private string RenderDocument()
        {
            var result = _documentResult;
            var recommendedPreset = FindRecommendedPreset();
            var rows = result == null
                ? "<tr><td colspan='5' class='empty-row'>Drop a Grasshopper file or choose one from disk.</td></tr>"
                : string.Join("", result.Matches.Select(RenderDocumentMatch));
            var notes = result == null || result.Notes.Count == 0
                ? string.Empty
                : $"<details class='review' open><summary>Notes</summary><pre>{H(string.Join(Environment.NewLine, result.Notes))}</pre></details>";
            var title = result == null ? "Document" : $"Document / {H(result.FileName)}";

            var recommendation = recommendedPreset == null
                ? string.Empty
                : $"<section class='document-recommendation'><span>Project preset</span><strong>{PresetIconMarkup(PresetIcon(recommendedPreset))}{H(recommendedPreset.Name)}</strong><a href='/apply-preset?name={Url(recommendedPreset.Name)}&launch=1'>Use + launch</a></section>";
            var association = result == null || string.IsNullOrWhiteSpace(result.SourcePath) || _settings.Presets.Count == 0
                ? string.Empty
                : $"<details class='review'><summary>Associate this document folder</summary><div class='association-list'>{string.Join("", _settings.Presets.Select(preset => $"<a href='/associate-document-preset?name={Url(preset.Name)}'>{PresetIconMarkup(PresetIcon(preset))}{H(preset.Name)}</a>"))}</div></details>";

            return $@"
<div class='manual'>
  <header class='manual-head'>
    <div>
      <a class='back' href='/home'>Back</a>
      <h1>{title}</h1>
      <div class='manual-meta'>Scan a .gh or .ghx file, including component signatures inside clusters.</div>
    </div>
    <div class='manual-actions'>
      <a href='/choose-document'>Choose file</a>
      <a class='fill' href='/document-load'>Load set</a>
      <a href='/document-preflight'>Preflight</a>
      <a class='fill' href='/document-load?launch=1'>Load + launch</a>
    </div>
  </header>
  <section id='dropZone' class='drop-zone'>
    <strong>Drop Grasshopper file here</strong>
    <span>.gh / .ghx document analysis</span>
    <input id='fileInput' type='file' accept='.gh,.ghx'>
  </section>
  <div class='table-wrap document-table'>
    <table><thead><tr><th>Status</th><th>Required</th><th>Requested version</th><th>Using</th><th>Note</th></tr></thead><tbody>{rows}</tbody></table>
  </div>
  {recommendation}
  {association}
  {notes}
</div>";
        }

        private string RenderDocumentMatch(DocumentPluginMatch match)
        {
            var candidate = match.Candidate;
            var usingText = candidate == null ? "" : $"{candidate.Name} / {candidate.Version} / {CompactPath(candidate.OriginalPath)}";
            return $@"
<tr class='{(match.Status == "Missing" ? "blocked" : "enabled")}'>
  <td>{H(match.Status)}</td>
  <td><strong>{H(match.Requirement.Name)}</strong></td>
  <td>{H(match.Requirement.Version)}</td>
  <td class='path-cell'>{H(usingText)}</td>
  <td>{H(match.Note)}</td>
</tr>";
        }

        private string RenderScanSettings()
        {
            var defaults = _scanner.GetDefaultRootOptions();
            var custom = _settings.CustomPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var enabledCount = GetEnabledScanRoots().Count;
            var defaultRows = string.Join("", defaults.Select(path => RenderScanPathRow(path, true)));
            var customRows = custom.Count == 0
                ? "<div class='scan-path-empty'>No custom folders. Add only locations that contain Grasshopper plugins.</div>"
                : string.Join("", custom.Select(path => RenderScanPathRow(path, false)));
            var startClass = enabledCount == 0 && !_isScanning ? "scan-start disabled" : "scan-start";
            var startHref = _isScanning ? "/scan-progress" : enabledCount == 0 ? "/scan-settings" : "/scan-start";
            var startText = _isScanning
                ? "View live scan"
                : $"Scan {enabledCount} {(enabledCount == 1 ? "folder" : "folders")}";

            return $@"
<div class='scan-workflow'>
  <header class='scan-workflow-head'>
    <div><a class='back' href='/manual'>Back</a><h1>Scan settings</h1><span>Choose exactly where Sieve looks for Grasshopper plugins.</span></div>
    <a class='{startClass}' href='{startHref}'>{startText}</a>
  </header>
  <main class='scan-settings-main'>
    <section class='scan-root-section'>
      <header><div><span>Detected</span><h2>Default folders</h2></div><em>{defaults.Count} locations</em></header>
      <div class='scan-path-list'>{defaultRows}</div>
    </section>
    <section class='scan-root-section'>
      <header><div><span>Optional</span><h2>Custom folders</h2></div><a href='/add-path?return=scan-settings'>Add folder</a></header>
      <div class='scan-path-list'>{customRows}</div>
    </section>
    <footer class='scan-settings-foot'><span>Last completed scan</span><strong>{H(ScanAge())}</strong><em>{_candidates.Count} cached files</em></footer>
  </main>
</div>";
        }

        private string RenderScanPathRow(string path, bool isDefault)
        {
            var exists = Directory.Exists(path);
            var enabled = exists && !_settings.DisabledScanPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
            var toggle = exists
                ? $"<a class='scan-path-toggle {(enabled ? "enabled" : "")}' role='switch' aria-checked='{(enabled ? "true" : "false")}' href='/scan-path-toggle?path={Url(Encode(path))}&amp;enabled={(enabled ? "0" : "1")}'><span></span><b>{(enabled ? "On" : "Off")}</b></a>"
                : "<span class='scan-path-missing'>Not found</span>";
            var remove = isDefault
                ? string.Empty
                : $"<a class='scan-path-remove' href='/remove-path?path={Url(Encode(path))}&amp;return=scan-settings'>Remove</a>";

            return $@"
<article class='scan-path-row {(!exists ? "missing" : "")}'>
  <div><span>{(isDefault ? "Default" : "Custom")}</span><strong title='{Attr(path)}'>{H(path)}</strong><em>{(exists ? "Available" : "Folder is not currently available")}</em></div>
  <nav>{remove}{toggle}</nav>
</article>";
        }

        private string RenderScanProgress()
        {
            var progress = _scanProgress;
            var recent = progress.RecentMessages == null || progress.RecentMessages.Count == 0
                ? "<li>Waiting for scanner activity...</li>"
                : string.Join("", progress.RecentMessages.Select(message => $"<li>{H(message)}</li>"));
            var action = progress.IsComplete
                ? progress.HasError || progress.IsCancelled
                    ? "<a id='scanDoneAction' class='scan-progress-primary' href='/scan-settings'>Back to settings</a>"
                    : "<a id='scanDoneAction' class='scan-progress-primary' href='/scan-finished'>View plugins</a>"
                : "<a id='scanCancelAction' class='scan-progress-secondary' href='/scan-cancel'>Cancel</a>";

            return $@"
<div class='scan-workflow scan-progress-page' id='scanProgressPage'>
  <header class='scan-workflow-head'>
    <div><a class='back' href='/scan-settings'>Settings</a><h1 id='scanPhase'>{H(progress.Phase)}</h1><span id='scanMessage'>{H(progress.Message)}</span></div>
    <div id='scanProgressAction'>{action}</div>
  </header>
  <main class='scan-progress-main'>
    <section class='scan-progress-meter'>
      <div><strong id='scanPercent'>{progress.Percent}%</strong><span id='scanCounter'>{progress.FilesProcessed} / {progress.TotalFiles} files</span></div>
      <div class='scan-progress-track' role='progressbar' aria-valuemin='0' aria-valuemax='100' aria-valuenow='{progress.Percent}'><span id='scanProgressBar' style='width:{progress.Percent}%'></span></div>
    </section>
    <section class='scan-current'>
      <span>Current location</span>
      <strong id='scanCurrentPath'>{H(FirstNonEmpty(progress.CurrentPath, "Preparing scan..."))}</strong>
    </section>
    <section class='scan-activity'>
      <header><span>Live activity</span><strong id='scanDiscovered'>{progress.FilesDiscovered} discovered</strong></header>
      <ol id='scanActivityList'>{recent}</ol>
    </section>
  </main>
</div>";
        }

        private string RenderManual()
        {
            var groups = BuildGroups();
            var visibleGroups = GetVisibleGroups().ToList();
            var enabled = groups.Count(group => group.Load);
            var duplicateGroups = groups.Count(group => BuildReleases(group).Count > 1);
            var rows = visibleGroups.Count == 0
                ? "<tr><td colspan='8' class='empty-row'>No plugins in this view. Scan, change filters, or add a path.</td></tr>"
                : string.Join("", visibleGroups.Select(RenderGroupRow));
            var pluginView = _settings.PluginViewMode == "list"
                ? $"<div class='table-wrap plugin-list-view'><table><thead><tr><th>Load</th><th>Pin</th><th>Plugin</th><th>Version</th><th>Type</th><th>Copies</th><th>Active path</th><th>Variant</th></tr></thead><tbody>{rows}</tbody></table></div>"
                : RenderPluginGrid(visibleGroups);

            return $@"
<div class='manual'>
  <header class='manual-head'>
    <div class='manual-heading'>
      <a class='back' href='/home'>Back</a>
      <div class='manual-title-row'>
        <div><h1>Manual selection</h1><div class='manual-meta'>{enabled} active / {groups.Count} unique / {_candidates.Count} files / {duplicateGroups} duplicate groups / {ScanAge()}</div></div>
      </div>
    </div>
    <div class='manual-actions'>
      <a class='fill' href='{(_isScanning ? "/scan-progress" : "/scan")}'>{(_isScanning ? "View scan" : "Scan")}</a>
    </div>
  </header>
  <section class='controls'>
    <form class='search' onsubmit='applySearch(); return false'><input id='searchBox' type='search' value='{Attr(_query)}' placeholder='Search plugin, version, path'><button type='submit'>Search</button></form>
    <a class='{(_showOnlyDuplicates ? "active" : "")}' href='/filter?dupes={(_showOnlyDuplicates ? "0" : "1")}&q={Url(_query)}'>Duplicates</a>
    <a href='/add-path'>Add path</a>
    <div class='view-switch' aria-label='Plugin view'><a class='{(_settings.PluginViewMode == "grid" ? "active" : "")}' href='/view?mode=grid' title='Grid view'>Grid</a><a class='{(_settings.PluginViewMode == "list" ? "active" : "")}' href='/view?mode=list' title='List view'>List</a></div>
  </section>
  {pluginView}
  <details class='review'>
    <summary>Scan review</summary>
    <pre>{H(_settings.LastScanReport)}</pre>
    {RenderScanChanges()}
    <div class='paths'>{RenderPaths()}</div>
  </details>
  <a class='manual-launch' href='/launch'>Launch selected</a>
</div>";
        }

        private string RenderPresetEditor()
        {
            var groups = BuildGroups();
            var editingPreset = _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, _editingPresetName, StringComparison.OrdinalIgnoreCase));
            var presetIcon = PresetIcon(editingPreset ?? new SievePreset());
            var presetName = editingPreset?.Name ?? NextPresetName("New preset");
            var presetDescription = editingPreset?.Description ?? string.Empty;
            var enabled = groups.Count(group => group.Load);
            var cards = groups.Count == 0
                ? "<div class='plugin-grid-empty'>No plugins are available. Return to Manual Selection and run a scan.</div>"
                : string.Join("", groups.Select(RenderPresetGroupCard));

            return $@"
<div class='preset-editor'>
  <header class='preset-editor-head'>
    <div class='preset-editor-heading'>
      <a class='preset-editor-back' href='/home' title='Back' aria-label='Back'>&#8592;</a>
      <div><h1>{(editingPreset == null ? "Create preset" : "Edit preset")}</h1><span>Preset editor</span></div>
    </div>
    <div class='preset-editor-actions'><span data-preset-count>{enabled} {(enabled == 1 ? "plugin" : "plugins")}</span><button type='button' onclick='savePreset()'>{(editingPreset == null ? "Save preset" : "Update preset")}</button></div>
  </header>
  <section class='preset-editor-identity'>
    <input id='presetIcon' type='hidden' value='{Attr(presetIcon)}'>
    <article class='preset-live-tile'>
      <span>Preset</span>
      <div class='preset-live-icon-control'>
        <button id='presetIconPreview' class='preset-live-icon' type='button' title='Choose preset icon' aria-label='Choose preset icon' aria-expanded='false' onclick='togglePresetIconPicker(event)'>{PresetIconMarkup(presetIcon)}</button>
        <div id='editorIconPopover' class='editor-icon-popover hidden' role='dialog' aria-label='Choose preset icon'>{RenderIconOptions(presetIcon)}</div>
      </div>
      <strong id='presetName' class='preset-inline-edit' contenteditable='true' spellcheck='false' data-fallback='{Attr(presetName)}'>{H(presetName)}</strong>
      <em id='presetDescription' class='preset-inline-edit' contenteditable='true' spellcheck='false' data-placeholder='Description'>{H(presetDescription)}</em>
      <em data-preset-count>{enabled} {(enabled == 1 ? "plugin" : "plugins")}</em>
    </article>
  </section>
  <section class='preset-editor-tools'>
    <input type='search' placeholder='Search plugins' oninput='filterPresetPlugins(this.value)'>
    <button type='button' onclick='setPresetPlugins(true)'>Select all</button>
    <button type='button' onclick='setPresetPlugins(false)'>Clear</button>
    <span id='presetSearchCount' aria-live='polite'></span>
    <span data-preset-count>{enabled} {(enabled == 1 ? "plugin" : "plugins")}</span>
  </section>
  <section class='preset-plugin-grid'>{cards}</section>
</div>";
        }

        private string RenderPresetGroupCard(PluginGroup group)
        {
            var version = GroupVersionSummary(group);
            var search = string.Join(" ", new[] { group.Name, version }.Concat(group.Variants.SelectMany(candidate => new[]
            {
                candidate.Name,
                candidate.ComponentName,
                candidate.Kind,
                candidate.Version,
                candidate.Category,
                candidate.SubCategory,
                candidate.OriginalPath
            }))).ToLowerInvariant();
            var enabled = group.Load;

            return $@"
<a class='plugin-card preset-plugin-card {(enabled ? "enabled" : "blocked")}' role='button' aria-pressed='{(enabled ? "true" : "false")}' href='/toggle?key={Url(Encode(group.Key))}' data-search='{Attr(search)}' data-details='/details?key={Url(Encode(group.Key))}&amp;from=preset' title='{Attr(group.Name + " / " + version + " / double-click for files and versions")}'>
  <span class='mono-icon plugin-logo'>{PluginFamilyIconMarkup(group)}</span>
  <strong>{H(group.Name)}</strong>
  <span class='preset-plugin-status'>{(enabled ? "[enabled]" : "[disabled]")}</span>
</a>";
        }

        private string RenderPluginGrid(List<PluginGroup> groups)
        {
            if (groups.Count == 0)
                return "<div class='plugin-grid-empty'>No plugins in this view. Scan, change filters, or add a path.</div>";

            return "<section class='plugin-grid'>" + string.Join("", groups.Select(RenderGroupCard)) + "</section>";
        }

        private string RenderGroupCard(PluginGroup group)
        {
            var selected = group.Variants.FirstOrDefault(candidate => candidate.Load) ?? GetPreferredVariant(group.Variants);
            var kind = string.Join(" / ", group.Variants.Select(candidate => candidate.Kind).Distinct(StringComparer.OrdinalIgnoreCase));
            var version = GroupVersionSummary(group);
            var pinText = IsPinned(group) ? "Pinned" : "Pin";

            return $@"
<article class='plugin-card {(group.Load ? "enabled" : "blocked")}' data-details='/details?key={Url(Encode(group.Key))}&amp;from=manual' title='{Attr(group.Name + " / " + version + " / double-click for files and versions")}'>
  <header>
    <a class='plugin-card-identity' href='/details?key={Url(Encode(group.Key))}'><span class='mono-icon plugin-logo'>{PluginFamilyIconMarkup(group)}</span><span><strong>{H(group.Name)}</strong><small>{H(version)} / {H(kind)}</small></span></a>
    <a class='card-load' role='button' aria-pressed='{(group.Load ? "true" : "false")}' href='/toggle?key={Url(Encode(group.Key))}'>{(group.Load ? "ON" : "OFF")}</a>
  </header>
  <div class='plugin-card-meta'><span>{group.Variants.Count} {(group.Variants.Count == 1 ? "file" : "files")} / {BuildReleases(group).Count} {(BuildReleases(group).Count == 1 ? "release" : "releases")}</span><a class='{(IsPinned(group) ? "pinned" : "")}' href='/pin?key={Url(Encode(group.Key))}'>{pinText}</a></div>
  <footer><span title='{Attr(selected.OriginalPath)}'>{H(CompactPath(selected.OriginalPath))}</span><a href='/details?key={Url(Encode(group.Key))}'>Details</a></footer>
</article>";
        }

        private string RenderGroupRow(PluginGroup group)
        {
            var selected = group.Variants.FirstOrDefault(candidate => candidate.Load) ?? GetPreferredVariant(group.Variants);
            var kind = string.Join(" / ", group.Variants.Select(candidate => candidate.Kind).Distinct(StringComparer.OrdinalIgnoreCase));
            var version = GroupVersionSummary(group);
            var variants = group.Variants.Count == 1
                ? "<span class='muted'>single</span>"
                : $"<details class='variants'><summary>{VariantSummary(group)}</summary>{string.Join("", group.Variants.Select(RenderVariant))}</details>";

            return $@"
<tr class='{(group.Load ? "enabled" : "blocked")}' data-details='/details?key={Url(Encode(group.Key))}&amp;from=manual' title='Double-click for files and versions'>
  <td><a class='load-dot' role='button' aria-pressed='{(group.Load ? "true" : "false")}' href='/toggle?key={Url(Encode(group.Key))}'>{(group.Load ? "ON" : "OFF")}</a></td>
  <td><a class='pin-dot {(IsPinned(group) ? "pinned" : "")}' href='/pin?key={Url(Encode(group.Key))}'>{(IsPinned(group) ? "PIN" : "+")}</a></td>
  <td><a class='plugin-cell plugin-link' href='/details?key={Url(Encode(group.Key))}'><span class='mono-icon plugin-logo'>{PluginFamilyIconMarkup(group)}</span><strong>{H(group.Name)}</strong></a></td>
  <td>{H(version)}</td>
  <td>{H(kind)}</td>
  <td>{group.Variants.Count}</td>
  <td class='path-cell'>{H(CompactPath(selected.OriginalPath))}</td>
  <td>{variants}</td>
</tr>";
        }

        private string RenderVariant(PluginCandidate candidate)
        {
            var selected = candidate.Load ? "selected" : "";
            var label = candidate.Kind == "GHUSER"
                ? FirstNonEmpty(candidate.SubCategory, "Uncategorized")
                : string.IsNullOrWhiteSpace(candidate.Version) ? candidate.Kind : $"{candidate.Kind} / {candidate.Version}";
            var detail = candidate.Kind == "GHUSER"
                ? $"{FirstNonEmpty(candidate.ComponentName, Path.GetFileNameWithoutExtension(candidate.OriginalPath))}  |  {CompactPath(candidate.OriginalPath)}"
                : CompactPath(candidate.OriginalPath);
            if (candidate.Kind == "GHUSER")
                return $"<span class='variant {selected}'><span>{H(label)}</span><small>{H(detail)}</small></span>";

            return $"<a class='variant {selected}' href='/variant?path={Url(Encode(candidate.OriginalPath))}'><span>{H(label)}</span><small>{H(detail)}</small></a>";
        }

        private string RenderPresetPills()
        {
            if (_settings.Presets.Count == 0)
                return "<span class='muted'>No presets yet</span>";

            return string.Join("", _settings.Presets.Select(preset => $@"
<span class='preset-pill'><a href='/apply-preset?name={Url(preset.Name)}'>{PresetIconMarkup(PresetIcon(preset))}{H(preset.Name)}</a><a href='/apply-preset?name={Url(preset.Name)}&launch=1'>Launch</a><a href='/edit-preset?name={Url(preset.Name)}'>Edit</a><a href='/duplicate-preset?name={Url(preset.Name)}'>Copy</a><a href='/associate-preset?name={Url(preset.Name)}'>Folder</a><a href='/move-preset?name={Url(preset.Name)}&direction=up'>Up</a><a href='/move-preset?name={Url(preset.Name)}&direction=down'>Down</a><a href='/export-preset?name={Url(preset.Name)}'>Export</a><a href='/delete-preset?name={Url(preset.Name)}'>Delete</a></span>"));
        }

        private static string RenderIconOptions(string selectedIcon)
        {
            var sieveIcons = new[]
            {
                new[] { "orbit", "Orbit" }, new[] { "sprout", "Sprout" }, new[] { "spark", "Spark" }, new[] { "wave", "Wave" },
                new[] { "grid", "Grid" }, new[] { "bridge", "Bridge" }, new[] { "knot", "Knot" }, new[] { "prism", "Prism" },
                new[] { "comet", "Comet" }, new[] { "radar", "Radar" }, new[] { "sun", "Sun" }, new[] { "moon", "Moon" },
                new[] { "flag", "Flag" }, new[] { "bolt", "Bolt" }, new[] { "cube", "Cube" }, new[] { "code", "Code" },
                new[] { "mask", "Mask" }, new[] { "beacon", "Beacon" }, new[] { "loop", "Loop" }, new[] { "pebble", "Pebble" }
            };

            var streamline = string.Join("", GetStreamlineIconNames().Select(name =>
            {
                var key = "streamline:" + name;
                var label = StreamlineIconLabel(name);
                return $"<button type='button' class='icon-choice {(string.Equals(key, selectedIcon, StringComparison.OrdinalIgnoreCase) ? "selected" : string.Empty)}' data-icon='{Attr(key)}' title='{Attr(label)}' aria-label='{Attr(label)}' onclick='choosePresetIcon(this)'>{PresetIconMarkup(key)}<small>{H(label)}</small></button>";
            }));
            var sieve = string.Join("", sieveIcons.Select(icon =>
                $"<button type='button' class='icon-choice {(icon[0] == selectedIcon ? "selected" : string.Empty)}' data-icon='{icon[0]}' title='{icon[1]}' aria-label='{icon[1]}' onclick='choosePresetIcon(this)'>{PresetIconMarkup(icon[0])}<small>{icon[1]}</small></button>"));

            return $"<section class='icon-section'><strong>Streamline</strong><div class='icon-choice-grid'>{streamline}</div></section><section class='icon-section'><strong>Sieve</strong><div class='icon-choice-grid'>{sieve}</div></section>";
        }

        private static string PresetIcon(SievePreset preset)
        {
            return NormalizePresetIcon(preset?.Icon);
        }

        private const string DefaultPresetIcon = "streamline:Coffee-Mug--Streamline-Core";

        private static readonly HashSet<string> PresetIconKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "orbit", "sprout", "spark", "wave", "grid", "bridge", "knot", "prism", "comet", "radar",
            "sun", "moon", "flag", "bolt", "cube", "code", "mask", "beacon", "loop", "pebble"
        };

        private static string NormalizePresetIcon(string icon)
        {
            icon = (icon ?? string.Empty).Trim();
            if (icon.StartsWith("streamline:", StringComparison.OrdinalIgnoreCase))
            {
                var requested = icon.Substring("streamline:".Length);
                var exact = GetStreamlineIconNames().FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(exact))
                    return "streamline:" + exact;
            }

            var sieveKey = icon.ToLowerInvariant();
            return PresetIconKeys.Contains(sieveKey) ? sieveKey : DefaultPresetIcon;
        }

        private static List<string> GetStreamlineIconNames()
        {
            const string prefix = "Sieve.PresetIcons.";
            const string suffix = ".png";
            return typeof(SieveDialog).Assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(name => name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string StreamlineIconLabel(string name)
        {
            return (name ?? string.Empty)
                .Replace("--Streamline-Core", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace('-', ' ')
                .Trim();
        }

        private static string PresetIconMarkup(string icon)
        {
            var key = NormalizePresetIcon(icon);
            if (key.StartsWith("streamline:", StringComparison.Ordinal))
            {
                var name = key.Substring("streamline:".Length);
                return $"<span class='preset-icon streamline-icon' aria-hidden='true'><img src='/preset-icon.png?name={Url(name)}' alt=''></span>";
            }

            var drawing = key switch
            {
                "orbit" => "<ellipse cx='12' cy='12' rx='9' ry='4.5'/><ellipse cx='12' cy='12' rx='4.5' ry='9'/><circle class='fill' cx='12' cy='12' r='2.5'/>",
                "sprout" => "<path class='fill' d='M12 20v-7'/><path class='fill' d='M12 13C6 13 5 8 5 5c4 0 7 2 7 8Z'/><path d='M12 16c0-5 3-8 7-8 0 4-2 7-7 8Z'/>",
                "spark" => "<path class='fill' d='m12 2 2.1 7.9L22 12l-7.9 2.1L12 22l-2.1-7.9L2 12l7.9-2.1Z'/><circle cx='19' cy='5' r='1.2'/>",
                "wave" => "<path d='M2 14c3-7 6 7 10 0s7 7 10 0'/><path class='fill' d='M3 7h5v3H3zM16 14h5v3h-5z'/>",
                "grid" => "<rect class='fill' x='3' y='3' width='7' height='7' rx='1'/><rect x='14' y='3' width='7' height='7' rx='1'/><rect x='3' y='14' width='7' height='7' rx='1'/><rect class='fill' x='14' y='14' width='7' height='7' rx='1'/>",
                "bridge" => "<path d='M3 19h18M5 19v-4a7 7 0 0 1 14 0v4M9 19v-4a3 3 0 0 1 6 0v4'/><path class='fill' d='M3 20h18v2H3z'/>",
                "knot" => "<path d='M8 8a5 5 0 1 1 0 8l8-8a5 5 0 1 1 0 8L8 8Z'/><circle class='fill' cx='12' cy='12' r='2'/>",
                "prism" => "<path class='fill' d='m12 3 8 16H4z'/><path d='m12 3 4 8-4 8-4-8z'/>",
                "comet" => "<path class='fill' d='M4 17c4-1 5-6 10-9l2 2c-3 5-8 6-12 7Z'/><circle cx='17.5' cy='6.5' r='3.5'/><path d='m5 20 3-1M3 14l3-.2'/>",
                "radar" => "<path d='M4 20A11 11 0 0 1 20 4M8 20a7 7 0 0 1 12-8M12 20a3 3 0 0 1 3-3'/><path class='fill' d='m13 11 8-8-5 10Z'/>",
                "sun" => "<circle class='fill' cx='12' cy='12' r='4'/><path d='M12 2v3M12 19v3M2 12h3M19 12h3M5 5l2 2M17 17l2 2M19 5l-2 2M7 17l-2 2'/>",
                "moon" => "<path class='fill' d='M18 16.5A8 8 0 0 1 7.5 6 8 8 0 1 0 18 16.5Z'/><path d='m18 4 .8 1.7L21 6.5l-2.2.8L18 9l-.8-1.7-2.2-.8 2.2-.8Z'/>",
                "flag" => "<path d='M5 22V3'/><path class='fill' d='M6 4h13l-3 4 3 4H6z'/><circle cx='5' cy='3' r='1.5'/>",
                "bolt" => "<path class='fill' d='m14 2-9 12h6l-1 8 9-12h-6z'/>",
                "cube" => "<path class='fill' d='m12 2 8 4.5v9L12 20l-8-4.5v-9z'/><path d='m4 6.5 8 4.5 8-4.5M12 11v9'/>",
                "code" => "<path d='m8 5-5 7 5 7M16 5l5 7-5 7M14 3l-4 18'/><circle class='fill' cx='12' cy='12' r='1.5'/>",
                "mask" => "<path class='fill' d='M4 6c5-3 11-3 16 0v7c-3 5-13 5-16 0Z'/><circle cx='9' cy='11' r='1.4'/><circle cx='15' cy='11' r='1.4'/><path d='M9 15c2 1.2 4 1.2 6 0'/>",
                "beacon" => "<path class='fill' d='M9 10h6l2 11H7z'/><path d='M10 10V6a2 2 0 0 1 4 0v4M3 8l3 2M21 8l-3 2M5 3l2 4M19 3l-2 4'/>",
                "loop" => "<path d='M7 8a5 5 0 0 0 0 8c5 0 5-8 10-8a5 5 0 0 1 0 8'/><circle class='fill' cx='7' cy='8' r='2'/><circle class='fill' cx='17' cy='16' r='2'/>",
                "pebble" => "<circle class='fill' cx='7' cy='15' r='4'/><circle cx='14' cy='8' r='4'/><circle class='fill' cx='18' cy='16' r='3'/>",
                _ => "<path class='fill' d='m12 2 2.1 7.9L22 12l-7.9 2.1L12 22l-2.1-7.9L2 12l7.9-2.1Z'/><circle cx='19' cy='5' r='1.2'/>"
            };

            return $"<span class='preset-icon icon-{Attr(key)}' aria-hidden='true'><svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'>{drawing}</svg></span>";
        }

        private string PluginFamilyIconMarkup(PluginGroup group)
        {
            var variantPaths = new HashSet<string>(
                group.Variants.Select(candidate => PluginCandidate.NormalizeOriginalPath(candidate.OriginalPath)),
                StringComparer.OrdinalIgnoreCase);
            var cached = _settings.PluginIconCache
                .Where(entry => IsSafePluginIcon(entry.IconDataUrl))
                .OrderByDescending(entry => entry.CapturedUtc, StringComparer.Ordinal)
                .FirstOrDefault(entry =>
                    !string.IsNullOrWhiteSpace(entry.PluginPath) &&
                    variantPaths.Contains(PluginCandidate.NormalizeOriginalPath(entry.PluginPath)));

            if (cached == null)
            {
                var family = group.Key.StartsWith("family:", StringComparison.OrdinalIgnoreCase)
                    ? group.Key.Substring("family:".Length)
                    : NormalizePluginFamily(group.Name);
                cached = _settings.PluginIconCache
                    .Where(entry => IsSafePluginIcon(entry.IconDataUrl))
                    .OrderByDescending(entry => entry.CapturedUtc, StringComparer.Ordinal)
                    .FirstOrDefault(entry =>
                        !string.IsNullOrWhiteSpace(entry.LibraryName) &&
                        string.Equals(NormalizePluginFamily(entry.LibraryName), family, StringComparison.Ordinal));
            }

            var icon = cached?.IconDataUrl;
            if (!IsSafePluginIcon(icon))
            {
                icon = group.Variants
                    .Where(candidate =>
                        string.Equals(candidate.Kind, "GHA", StringComparison.OrdinalIgnoreCase) &&
                        IsSafePluginIcon(candidate.IconDataUrl))
                    .OrderByDescending(candidate => candidate.Load)
                    .ThenByDescending(candidate => ParseVersion(candidate.Version))
                    .Select(candidate => candidate.IconDataUrl)
                    .FirstOrDefault();
            }

            if (IsSafePluginIcon(icon))
                return $"<img src='{Attr(icon)}' alt=''>";

            return H(Initials(group.Name));
        }

        private static bool IsSafePluginIcon(string value)
        {
            const string prefix = "data:image/png;base64,";
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || value.Length > 180000)
                return false;

            for (var index = prefix.Length; index < value.Length; index++)
            {
                var character = value[index];
                if (!(character >= 'A' && character <= 'Z') && !(character >= 'a' && character <= 'z') &&
                    !(character >= '0' && character <= '9') && character != '+' && character != '/' && character != '=')
                    return false;
            }

            return true;
        }

        private string RenderPaths()
        {
            if (_settings.CustomPaths.Count == 0)
                return "<div class='muted'>Default paths only.</div>";

            return string.Join("", _settings.CustomPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => $"<div class='path-line'><span>{H(path)}</span><a href='/remove-path?path={Url(Encode(path))}'>Remove</a></div>"));
        }

        private string LaunchingPage()
        {
            return "<!doctype html><html><body style='font-family:Inter,Arial,sans-serif;background:#f7f7f4;color:#111;padding:32px'><h1>Launching Grasshopper...</h1></body></html>";
        }

        private void SetAll(bool load, bool persist = true)
        {
            foreach (var group in GetVisibleGroups())
                SetGroupLoad(group, load);
            if (persist)
                PersistCandidates();
        }

        private void SetEveryManagedPlugin(bool load)
        {
            RemoveUnsupportedCachedCandidates();
            foreach (var group in BuildGroups())
                SetGroupLoad(group, load);
            PersistCandidates();
        }

        private void ToggleGroup(string key)
        {
            var group = BuildGroups().FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (group == null)
                return;
            SetGroupLoad(group, !group.Load);
            PersistCandidates();
        }

        private void SelectVariant(string originalPath)
        {
            var group = BuildGroups().FirstOrDefault(item => item.Variants.Any(candidate => string.Equals(candidate.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase)));
            if (group == null)
                return;
            var release = BuildReleases(group).FirstOrDefault(item =>
                item.Variants.Any(candidate => string.Equals(candidate.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase)));
            if (release != null)
                SelectRelease(group.Key, release.Key);
        }

        private void SelectRelease(string pluginKey, string releaseKey)
        {
            var group = BuildGroups().FirstOrDefault(item => string.Equals(item.Key, pluginKey, StringComparison.OrdinalIgnoreCase));
            var release = group == null
                ? null
                : BuildReleases(group).FirstOrDefault(item => string.Equals(item.Key, releaseKey, StringComparison.OrdinalIgnoreCase));
            if (group == null || release == null)
                return;

            var wasPinned = group.Variants.Any(candidate =>
                _settings.PinnedPluginPaths.Contains(candidate.OriginalPath, StringComparer.OrdinalIgnoreCase));
            foreach (var candidate in group.Variants)
                candidate.Load = release.Variants.Contains(candidate);
            if (wasPinned)
            {
                var familyPaths = group.Variants.Select(candidate => candidate.OriginalPath).ToList();
                _settings.PinnedPluginPaths.RemoveAll(path => familyPaths.Contains(path, StringComparer.OrdinalIgnoreCase));
                _settings.PinnedPluginPaths.AddRange(release.Variants.Select(candidate => candidate.OriginalPath));
            }
            PersistCandidates();
        }

        private static void SetGroupLoad(PluginGroup group, bool load)
        {
            foreach (var candidate in group.Variants)
                candidate.Load = false;
            if (load)
            {
                var release = BuildReleases(group)
                    .OrderByDescending(item => ParseVersion(item.Version))
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (release != null)
                {
                    foreach (var candidate in release.Variants)
                        candidate.Load = true;
                }
            }
        }

        private void SavePreset(string name, string icon, string description, bool persist)
        {
            name = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            var preset = !string.IsNullOrWhiteSpace(_editingPresetName)
                ? _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, _editingPresetName, StringComparison.OrdinalIgnoreCase))
                : _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
            {
                preset = new SievePreset { Name = name };
                _settings.Presets.Add(preset);
            }
            else if (!string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase) &&
                _settings.Presets.Any(item => !ReferenceEquals(item, preset) && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.LastScanReport = "Preset name already exists: " + name;
                return;
            }

            preset.Name = name;
            preset.Icon = NormalizePresetIcon(icon);
            preset.Description = (description ?? string.Empty).Trim();
            preset.PluginPaths = _candidates.Where(candidate => candidate.Load)
                .Select(candidate => candidate.OriginalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            SettingsStore.Save(_settings);
            _editingPresetName = string.Empty;
        }

        private void ExportPreset(string name)
        {
            var preset = _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                return;

            var wait = new ManualResetEventSlim(false);
            Application.Instance.AsyncInvoke(() =>
            {
                try
                {
                    using var dialog = new SaveFileDialog
                    {
                        Title = "Export Sieve preset",
                        FileName = SafeFileName(preset.Name) + ".txt"
                    };
                    dialog.Filters.Add(new FileFilter("Text files", ".txt"));
                    if (dialog.ShowDialog(this) == DialogResult.Ok && !string.IsNullOrWhiteSpace(dialog.FileName))
                        File.WriteAllText(dialog.FileName, BuildPresetExport(preset), Encoding.UTF8);
                }
                finally
                {
                    wait.Set();
                }
            });
            wait.Wait();
        }

        private void ApplyPreset(string name, bool launch, bool render)
        {
            var preset = _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                return;

            var allowed = new HashSet<string>(preset.PluginPaths, StringComparer.OrdinalIgnoreCase);
            allowed.UnionWith(_settings.PinnedPluginPaths);
            foreach (var candidate in _candidates)
                candidate.Load = allowed.Contains(candidate.OriginalPath);

            PersistCandidates();
            _launchLabel = "Preset / " + preset.Name;
            if (launch)
                LaunchFromServer();
        }

        private void DeletePreset(string name)
        {
            var preset = _settings.Presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                return;
            _settings.Presets.Remove(preset);
            if (string.Equals(_editingPresetName, preset.Name, StringComparison.OrdinalIgnoreCase))
                _editingPresetName = string.Empty;
            SettingsStore.Save(_settings);
        }

        private void RemovePath(string path)
        {
            _settings.CustomPaths.Remove(path);
            _settings.DisabledScanPaths.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            SettingsStore.Save(_settings);
        }

        private void Restore(bool render)
        {
            var errors = PluginGate.RestoreAllDisabled();
            if (errors.Count > 0)
                _settings.LastScanReport = string.Join(Environment.NewLine, errors);

            foreach (var candidate in _candidates)
            {
                candidate.Load = true;
                candidate.IsDisabled = false;
                candidate.CurrentPath = candidate.OriginalPath;
            }

            CanonicalizeDuplicateGroups(_candidates);
            PersistCandidates();
        }

        private List<PluginGroup> GetVisibleGroups()
        {
            IEnumerable<PluginGroup> groups = BuildGroups();
            if (_showOnlyDuplicates)
                groups = groups.Where(group => BuildReleases(group).Count > 1);

            if (!string.IsNullOrWhiteSpace(_query))
            {
                groups = groups.Where(group =>
                    group.Name.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    group.Variants.Any(candidate => candidate.Version.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        candidate.Name.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        candidate.ComponentName.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        candidate.Kind.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        candidate.Category.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        candidate.SubCategory.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        candidate.OriginalPath.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            return groups.ToList();
        }

        private List<PluginGroup> BuildGroups()
        {
            return _candidates.GroupBy(GetPluginKey, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var variants = group.OrderByDescending(candidate => candidate.Load)
                        .ThenByDescending(candidate => ParseVersion(candidate.Version))
                        .ThenBy(candidate => candidate.OriginalPath, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return new PluginGroup
                    {
                        Key = group.Key,
                        Name = GetPluginFamilyDisplayName(variants),
                        Variants = variants
                    };
                })
                .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void PersistCandidates()
        {
            _settings.LastScan = _candidates
                .Where(candidate => PluginPolicy.IsManageablePath(candidate.OriginalPath))
                .ToList();
            _settings.DisabledPaths = _settings.DisabledPaths
                .Where(PluginPolicy.IsManageablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _settings.PinnedPluginPaths = _settings.PinnedPluginPaths
                .Where(PluginPolicy.IsManageablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var preset in _settings.Presets)
            {
                preset.PluginPaths = preset.PluginPaths
                    .Where(PluginPolicy.IsManageablePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            SettingsStore.Save(_settings);
        }

        private void RemoveUnsupportedCachedCandidates()
        {
            _candidates.RemoveAll(candidate =>
                !PluginPolicy.IsManageablePath(candidate.OriginalPath));
        }

        private string BuildScanReport(List<PluginCandidate> candidates)
        {
            var groups = candidates.GroupBy(GetPluginKey, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var variants = group.ToList();
                    return new PluginGroup
                    {
                        Key = group.Key,
                        Name = GetPluginFamilyDisplayName(variants),
                        Variants = variants
                    };
                })
                .ToList();
            var duplicateGroups = groups.Where(group => BuildReleases(group).Count > 1).ToList();
            var userObjectCategoryCount = candidates.Where(candidate => candidate.Kind == "GHUSER")
                .GroupBy(candidate => FirstNonEmpty(candidate.Category, candidate.Name), StringComparer.OrdinalIgnoreCase)
                .Count();
            var roots = GetEnabledScanRoots();

            var lines = new List<string>
            {
                $"Scanned {roots.Count} folders.",
                $"Found {candidates.Count} loadable files grouped into {groups.Count} plugin families.",
                $"Grouped .ghuser files into {userObjectCategoryCount} Grasshopper toolbar categories.",
                $"{duplicateGroups.Count} plugin families have multiple installed releases; Sieve keeps one release active for each.",
                "",
                "Folders:"
            };
            lines.AddRange(roots.Select(path => "  - " + path));
            if (duplicateGroups.Count > 0)
            {
                lines.Add("");
                lines.Add("Duplicate groups:");
                lines.AddRange(duplicateGroups.Take(20).Select(group => $"  - {group.Name}: {BuildReleases(group).Count} releases / {group.Variants.Count} files"));
            }
            return string.Join(Environment.NewLine, lines);
        }

        private string ScanAge()
        {
            return DateTime.TryParse(_settings.LastScanUtc, out var lastScan)
                ? lastScan.ToLocalTime().ToString("g")
                : "no scan";
        }

        private static void CanonicalizeDuplicateGroups(List<PluginCandidate> candidates)
        {
            foreach (var group in candidates.GroupBy(GetPluginKey, StringComparer.OrdinalIgnoreCase))
            {
                var pluginGroup = new PluginGroup
                {
                    Key = group.Key,
                    Name = GetPluginFamilyDisplayName(group.ToList()),
                    Variants = group.ToList()
                };
                var loadedReleases = BuildReleases(pluginGroup)
                    .Where(release => release.Variants.Any(candidate => candidate.Load))
                    .ToList();
                if (loadedReleases.Count <= 1)
                    continue;

                var preferred = loadedReleases
                    .OrderByDescending(release => ParseVersion(release.Version))
                    .ThenBy(release => release.Key, StringComparer.OrdinalIgnoreCase)
                    .First();
                foreach (var candidate in pluginGroup.Variants.Where(candidate => !preferred.Variants.Contains(candidate)))
                    candidate.Load = false;
            }
        }

        private static PluginCandidate GetPreferredVariant(IEnumerable<PluginCandidate> candidates)
        {
            return candidates.OrderByDescending(candidate => ParseVersion(candidate.Version))
                .ThenBy(candidate => candidate.OriginalPath.Length)
                .ThenBy(candidate => candidate.OriginalPath, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        private static string GetPluginKey(PluginCandidate candidate)
        {
            var family = GetPluginFamilySource(candidate);
            var normalized = NormalizePluginFamily(family);
            return string.IsNullOrWhiteSpace(normalized)
                ? "path:" + candidate.OriginalPath.ToLowerInvariant()
                : "family:" + normalized;
        }

        private static string GetPluginFamilySource(PluginCandidate candidate)
        {
            if (TryGetPackageIdentity(candidate.OriginalPath, out var packageName, out _, out _))
                return packageName;

            if (candidate.Kind == "GHUSER" || candidate.Kind == "GHPY")
            {
                var libraryFolder = GetLibrariesFamilyFolder(candidate.OriginalPath);
                if (!string.IsNullOrWhiteSpace(libraryFolder))
                    return libraryFolder;
            }

            if (candidate.Kind == "GHUSER")
                return FirstNonEmpty(candidate.Category, candidate.Name, "User Objects");

            return FirstNonEmpty(candidate.Name, Path.GetFileNameWithoutExtension(candidate.OriginalPath));
        }

        private static string GetPluginFamilyDisplayName(IReadOnlyList<PluginCandidate> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (TryGetPackageIdentity(candidate.OriginalPath, out var packageName, out _, out _) &&
                    !string.IsNullOrWhiteSpace(packageName))
                    return packageName;
            }

            var assemblyName = candidates
                .Where(candidate => candidate.Kind == "GHA")
                .Select(candidate => candidate.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            if (!string.IsNullOrWhiteSpace(assemblyName))
                return assemblyName;

            var category = candidates
                .Where(candidate => candidate.Kind == "GHUSER")
                .Select(candidate => candidate.Category)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(category))
                return category;

            return CleanFamilyDisplay(GetPluginFamilySource(candidates[0]));
        }

        private static string NormalizePluginFamily(string value)
        {
            var source = (value ?? string.Empty).Trim().TrimStart('.', '_');
            var builder = new StringBuilder(source.Length);
            foreach (var character in source)
            {
                if (char.IsLetterOrDigit(character))
                    builder.Append(char.ToLowerInvariant(character));
            }

            var normalized = builder.ToString();
            while (normalized.Length > 0 && char.IsDigit(normalized[normalized.Length - 1]))
                normalized = normalized.Substring(0, normalized.Length - 1);

            foreach (var suffix in new[] { "grasshopper", "components", "component", "commonsdk", "plugin", "core", "sdk", "gha", "gh" })
            {
                if (normalized.Length > suffix.Length + 2 && normalized.EndsWith(suffix, StringComparison.Ordinal))
                    normalized = normalized.Substring(0, normalized.Length - suffix.Length);
            }

            while (normalized.Length > 0 && char.IsDigit(normalized[normalized.Length - 1]))
                normalized = normalized.Substring(0, normalized.Length - 1);
            return normalized;
        }

        private static string CleanFamilyDisplay(string value)
        {
            value = (value ?? string.Empty).Trim().TrimStart('.', '_');
            var end = value.Length;
            while (end > 0 && (char.IsDigit(value[end - 1]) || value[end - 1] == '.' || value[end - 1] == '-' || value[end - 1] == '_'))
                end--;
            return end > 0 ? value.Substring(0, end).Trim() : value;
        }

        private static string GetLibrariesFamilyFolder(string path)
        {
            var parts = SplitPath(path);
            var librariesIndex = parts.FindIndex(part => string.Equals(part, "Libraries", StringComparison.OrdinalIgnoreCase));
            if (librariesIndex < 0 || librariesIndex + 1 >= parts.Count - 1)
                return string.Empty;

            var folder = parts[librariesIndex + 1];
            return string.Equals(folder, "UserObjects", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(folder, "ghuser", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : folder;
        }

        private static bool TryGetPackageIdentity(string path, out string packageName, out string packageVersion, out string releaseRoot)
        {
            packageName = string.Empty;
            packageVersion = string.Empty;
            releaseRoot = string.Empty;
            var parts = SplitPath(path);
            var packagesIndex = parts.FindIndex(part => string.Equals(part, "packages", StringComparison.OrdinalIgnoreCase));
            if (packagesIndex < 0 || packagesIndex + 3 >= parts.Count)
                return false;

            packageName = parts[packagesIndex + 2];
            packageVersion = parts[packagesIndex + 3];
            releaseRoot = string.Join("/", parts.Take(packagesIndex + 4)).ToLowerInvariant();
            return !string.IsNullOrWhiteSpace(packageName);
        }

        private static List<string> SplitPath(string path)
        {
            return (path ?? string.Empty)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        private static List<PluginRelease> BuildReleases(PluginGroup group)
        {
            return group.Variants
                .GroupBy(GetReleaseContainerKey, StringComparer.OrdinalIgnoreCase)
                .Select(release =>
                {
                    var variants = release.OrderBy(candidate => KindOrder(candidate.Kind))
                        .ThenBy(candidate => candidate.ComponentName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(candidate => candidate.OriginalPath, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    return new PluginRelease
                    {
                        Key = release.Key,
                        Version = GetReleaseVersion(variants),
                        Variants = variants
                    };
                })
                .OrderByDescending(release => ParseVersion(release.Version))
                .ThenBy(release => release.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string GetReleaseContainerKey(PluginCandidate candidate)
        {
            if (TryGetPackageIdentity(candidate.OriginalPath, out var packageName, out var packageVersion, out var releaseRoot))
                return $"package:{NormalizePluginFamily(packageName)}:{packageVersion}:{releaseRoot}";

            var parts = SplitPath(candidate.OriginalPath);
            var librariesIndex = parts.FindIndex(part => string.Equals(part, "Libraries", StringComparison.OrdinalIgnoreCase));
            if (librariesIndex >= 0 && librariesIndex + 1 < parts.Count - 1)
                return "library:" + string.Join("/", parts.Take(librariesIndex + 2)).ToLowerInvariant();

            return "folder:" + (Path.GetDirectoryName(candidate.OriginalPath) ?? candidate.OriginalPath).ToLowerInvariant();
        }

        private static string GetReleaseVersion(IReadOnlyList<PluginCandidate> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (TryGetPackageIdentity(candidate.OriginalPath, out _, out var packageVersion, out _) &&
                    !string.IsNullOrWhiteSpace(packageVersion))
                    return packageVersion;
            }

            var assemblyVersion = candidates
                .Where(candidate => candidate.Kind == "GHA")
                .Select(candidate => candidate.Version)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(assemblyVersion))
                return assemblyVersion;

            var anyVersion = candidates.Select(candidate => candidate.Version)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return string.IsNullOrWhiteSpace(anyVersion) ? "Unversioned" : anyVersion;
        }

        private static string GetCandidateVersion(PluginCandidate candidate, PluginRelease release)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Version))
                return candidate.Version;
            if (TryGetPackageIdentity(candidate.OriginalPath, out _, out var packageVersion, out _) &&
                !string.IsNullOrWhiteSpace(packageVersion))
                return packageVersion;
            return release?.Version ?? "Unversioned";
        }

        private static int KindOrder(string kind)
        {
            return string.Equals(kind, "GHA", StringComparison.OrdinalIgnoreCase) ? 0
                : string.Equals(kind, "GHPY", StringComparison.OrdinalIgnoreCase) ? 1
                : string.Equals(kind, "GHUSER", StringComparison.OrdinalIgnoreCase) ? 2
                : 3;
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new Version(0, 0);
            var clean = new string(value.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray()).Trim('.');
            return Version.TryParse(clean, out var version) ? version : new Version(0, 0);
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query))
                return result;
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                var pieces = part.Split(new[] { '=' }, 2);
                if (pieces.Length == 2)
                    result[pieces[0]] = pieces[1];
            }
            return result;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(WebUtility.UrlDecode(value) ?? string.Empty));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string H(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private static string Attr(string value)
        {
            return H(value).Replace("'", "&#39;");
        }

        private static string Url(string value)
        {
            return WebUtility.UrlEncode(value ?? string.Empty);
        }

        private static string CompactPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
            if (parts.Count <= 4)
                return path;
            return string.Join(Path.DirectorySeparatorChar.ToString(), new[] { parts[0], "..." }.Concat(parts.Skip(parts.Count - 3)));
        }

        private static string Initials(string name)
        {
            var letters = new string((name ?? "S").Where(char.IsLetterOrDigit).Take(2).ToArray());
            return string.IsNullOrWhiteSpace(letters) ? "GH" : letters.ToUpperInvariant();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static string VariantSummary(PluginGroup group)
        {
            if (group.Variants.Select(candidate => candidate.Kind).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                return $"{group.Variants.Count} files / {BuildReleases(group).Count} releases";

            if (!group.IsUserObjectGroup)
                return $"{group.Variants.Count} copies";

            var subCategories = group.Variants.Select(candidate => candidate.SubCategory)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

            return subCategories.Count == 0
                ? $"{group.Variants.Count} components"
                : $"{group.Variants.Count} components / {string.Join(", ", subCategories)}";
        }

        private static string GroupVersionSummary(PluginGroup group)
        {
            if (group.IsUserObjectGroup)
                return $"{group.Variants.Count} components";

            var releases = BuildReleases(group);
            if (releases.Count > 1)
                return $"{releases.Count} versions";
            var version = releases.FirstOrDefault()?.Version;
            return string.IsNullOrWhiteSpace(version) || version == "Unversioned" ? "No version" : version;
        }

        private string BuildPresetExport(SievePreset preset)
        {
            var lines = new List<string>
            {
                "Sieve preset",
                "============",
                "Name: " + preset.Name,
                "Icon: " + PresetIcon(preset),
                "Description: " + FirstNonEmpty(preset.Description, ""),
                "Plugin count: " + preset.PluginPaths.Count,
                "Exported: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                "",
                "Project folders:"
            };

            lines.AddRange(preset.ProjectFolders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => "  " + path));
            lines.Add("");
            lines.Add("Plugins:");

            foreach (var path in preset.PluginPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = _candidates.FirstOrDefault(item => string.Equals(item.OriginalPath, path, StringComparison.OrdinalIgnoreCase));
                var label = candidate == null
                    ? Path.GetFileNameWithoutExtension(path)
                    : $"{candidate.Name} / {candidate.Version} / {candidate.Kind}";
                lines.Add("- " + label);
                lines.Add("  " + path);
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((name ?? "Sieve preset").Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(clean) ? "Sieve preset" : clean;
        }

        private static string Css()
        {
            return @"
*{box-sizing:border-box}html,body{margin:0;width:100%;height:100%;font-family:Inter,-apple-system,BlinkMacSystemFont,'Segoe UI',Arial,sans-serif;background:#f7f7f4;color:#090909}a{text-decoration:none;color:inherit}button,input,select{font:inherit}.home,.manual{min-height:100vh;position:relative;background:#f7f7f4;overflow:auto}.tone{position:absolute;inset:0;background:radial-gradient(#111 1px,transparent 1px);background-size:9px 9px;opacity:.055;pointer-events:none;animation:toneDrift 18s linear infinite}.home-head,.home-foot,.manual-head{position:relative;display:flex;align-items:flex-start;justify-content:space-between;gap:18px;padding:24px 30px}.wordmark{font-size:22px;font-weight:900;letter-spacing:0;border:2px solid #111;border-radius:999px;padding:7px 16px;background:#f7f7f4}.home-meta,.manual-meta{font-size:11px;text-transform:uppercase;letter-spacing:.08em;color:#575757;margin-top:10px}.home-main{position:relative;padding:28px 30px 20px}.home-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px;align-items:stretch}.home-tile{min-height:196px;border:2px solid #111;border-radius:20px;background:#f7f7f4;padding:14px;display:flex;flex-direction:column;justify-content:space-between;box-shadow:6px 6px 0 #111;transition:transform .16s ease,box-shadow .16s ease,background .16s ease;animation:tileIn .28s ease both}.home-tile:nth-child(2){animation-delay:.03s}.home-tile:nth-child(3){animation-delay:.06s}.home-tile:nth-child(4){animation-delay:.09s}.home-tile:nth-child(5){animation-delay:.12s}.home-tile:nth-child(6){animation-delay:.15s}.home-tile:hover{transform:translate(3px,3px);box-shadow:3px 3px 0 #111}.home-tile:active,.home-tile.pressed{transform:translate(6px,6px);box-shadow:0 0 0 #111}.home-tile.fill{background:#111;color:#fff}.home-tile.plus strong{font-size:64px;line-height:.8;font-weight:300}.home-tile.disabled{opacity:.55}.home-tile span{font-size:11px;letter-spacing:.12em;text-transform:uppercase}.home-tile b{font-size:24px;font-weight:900;line-height:1}.home-tile strong{font-size:27px;line-height:1;font-weight:900;letter-spacing:0}.home-tile em{font-style:normal;font-size:12px;line-height:1.35;color:inherit;opacity:.72}.document-home{background:repeating-linear-gradient(-45deg,#f7f7f4 0,#f7f7f4 9px,#ededeb 9px,#ededeb 10px)}.home-foot{padding-top:8px;justify-content:flex-start}.home-foot a,.back,.manual-actions a,.controls a,.preset-strip button,.preset-strip a,.search button,.path-line a{border:1.5px solid #111;border-radius:999px;background:#f7f7f4;padding:6px 10px;font-size:11px;font-weight:800;transition:transform .12s ease,background .12s ease,color .12s ease}.home-foot a:hover,.back:hover,.manual-actions a:hover,.controls a:hover,.preset-strip button:hover,.preset-strip a:hover,.search button:hover,.path-line a:hover{transform:translateY(-1px)}.manual{padding-bottom:24px}.manual-head{border-bottom:2px solid #111;background:linear-gradient(90deg,rgba(0,0,0,.04) 25%,transparent 25%,transparent 50%,rgba(0,0,0,.04) 50%,rgba(0,0,0,.04) 75%,transparent 75%);background-size:18px 18px}.manual h1{font-size:34px;line-height:1;margin:12px 0 0;font-weight:950;letter-spacing:0}.manual-actions{display:flex;gap:8px;flex-wrap:wrap;justify-content:flex-end}.manual-actions a.fill,.search button,.preset-strip button{background:#111;color:#fff}.preset-strip,.controls{margin:14px 24px;display:flex;gap:8px;align-items:center;flex-wrap:wrap}.preset-strip input,.preset-strip select,.search input{height:32px;border:1.5px solid #111;border-radius:999px;background:#fff;padding:0 11px;outline:none}.preset-strip select{width:54px;text-align:center}.preset-strip input{width:160px}.preset-strip .preset-description{width:260px}.preset-pill{display:inline-flex;gap:5px;align-items:center;border:1px solid #111;border-radius:999px;padding:3px;background:#fff}.preset-pill a{border:0;border-radius:999px;padding:5px 8px;font-size:11px;display:inline-flex;gap:5px;align-items:center}.preset-pill b{font-size:13px}.controls{border-top:1.5px solid #111;border-bottom:1.5px solid #111;padding:10px 0}.search{display:flex;gap:8px;flex:1;min-width:280px}.search input{width:100%}.controls a.active{background:#111;color:#fff}.table-wrap{margin:0 24px;border:2px solid #111;border-radius:18px;overflow:auto;background:#fff;max-height:430px}table{width:100%;border-collapse:collapse;font-size:12px;line-height:1.2}th{position:sticky;top:0;z-index:1;text-align:left;background:#111;color:#fff;font-size:11px;text-transform:uppercase;letter-spacing:.08em;padding:9px 10px;white-space:nowrap}td{padding:8px 10px;border-bottom:1px solid #d8d8d3;vertical-align:middle;white-space:nowrap}tr{transition:background .12s ease}tr:hover{background:#f1f1ee}tr.blocked{color:#777;background:#fafaf7}.plugin-cell{display:flex;align-items:center;gap:8px;min-width:190px}.mono-icon{width:26px;height:26px;border:1.5px solid #111;border-radius:8px;display:grid;place-items:center;font-size:10px;font-weight:900;background:#f7f7f4;color:#111}.load-dot{display:inline-grid;place-items:center;min-width:42px;height:24px;border:1.5px solid #111;border-radius:999px;font-size:10px;font-weight:900;background:#fff;transition:transform .12s ease}.load-dot:hover{transform:scale(1.04)}.enabled .load-dot{background:#111;color:#fff}.path-cell{max-width:330px;overflow:hidden;text-overflow:ellipsis;color:#444}.muted{color:#777;font-size:11px}.variants summary{cursor:pointer;font-weight:800}.variant{display:block;margin-top:6px;border:1px solid #111;border-radius:10px;padding:6px 8px;background:#fff;max-width:310px}.variant.selected{background:#111;color:#fff}.variant span{display:block;font-weight:900}.variant small{display:block;overflow:hidden;text-overflow:ellipsis;color:inherit;opacity:.72}.drop-zone{margin:14px 24px;border:2px dashed #111;border-radius:24px;min-height:150px;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:8px;background:#fff;transition:transform .12s ease,box-shadow .12s ease}.drop-zone.over{transform:translate(4px,4px);box-shadow:6px 6px 0 #111}.drop-zone strong{font-size:26px;font-weight:950}.drop-zone span{font-size:12px;text-transform:uppercase;letter-spacing:.08em;color:#575757}.drop-zone input{border:1.5px solid #111;border-radius:999px;background:#f7f7f4;padding:8px 12px}.document-table{max-height:360px}.review{margin:14px 24px;border:1.5px solid #111;border-radius:18px;padding:12px;background:#f7f7f4}.review summary{cursor:pointer;font-weight:900}.review pre{white-space:pre-wrap;max-height:150px;overflow:auto;font-size:11px;line-height:1.4;color:#333}.path-line{display:flex;justify-content:space-between;gap:12px;border-top:1px solid #d8d8d3;padding:8px 0;font-size:12px}.empty-row{text-align:center;color:#777;padding:28px}@keyframes tileIn{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:translateY(0)}}@keyframes toneDrift{from{background-position:0 0}to{background-position:54px 54px}}@media(max-width:980px){.home-grid{grid-template-columns:repeat(2,1fr)}.home-tile{min-height:168px}.manual-head{display:block}.manual-actions{justify-content:flex-start;margin-top:14px}.table-wrap{max-height:none}}";
        }

        private static string IconCss()
        {
            return @"
.home-tile .preset-icon{width:42px;height:42px}.preset-strip input,.search input{height:32px;border:1.5px solid #111;border-radius:999px;background:#fff;padding:0 11px;outline:none}.icon-picker{display:flex;align-items:center;gap:4px;max-width:318px;overflow-x:auto;padding:3px;border:1.5px solid #111;border-radius:16px;background:#fff;scrollbar-width:thin}.icon-choice{position:relative;flex:0 0 29px;width:29px;height:29px;padding:3px;border:1px solid transparent;border-radius:9px;background:transparent;color:#111;cursor:pointer;transition:transform .12s ease,background .12s ease,border-color .12s ease}.icon-choice:hover{transform:translateY(-2px);border-color:#111;background:#f0f0ec}.icon-choice.selected{background:#111;color:#fff;box-shadow:2px 2px 0 #999}.icon-choice small{position:absolute;left:50%;bottom:calc(100% + 7px);z-index:3;transform:translateX(-50%);display:none;padding:4px 6px;border:1px solid #111;border-radius:5px;background:#111;color:#fff;font-size:9px;white-space:nowrap;letter-spacing:.03em}.icon-choice:hover small{display:block}.preset-icon{display:inline-flex;width:18px;height:18px;align-items:center;justify-content:center;color:#111;--accent:#72cfc1}.preset-icon svg{display:block;width:100%;height:100%;overflow:visible}.preset-icon .fill{fill:var(--accent);stroke:currentColor}.icon-orbit,.icon-radar,.icon-beacon{--accent:#75baff}.icon-sprout,.icon-wave,.icon-bridge,.icon-loop{--accent:#72cfc1}.icon-spark,.icon-sun,.icon-bolt,.icon-prism,.icon-comet{--accent:#e7dc63}.icon-grid,.icon-knot,.icon-moon,.icon-flag,.icon-cube{--accent:#ff8a70}.icon-code,.icon-mask,.icon-pebble{--accent:#c7a0ee}.icon-choice.selected .preset-icon{color:#fff}.preset-pill .preset-icon{width:15px;height:15px}";
        }

        private static string PluginIconCss()
        {
            return ".plugin-logo{overflow:hidden;padding:0}.plugin-logo img{display:block;width:100%;height:100%;object-fit:contain}";
        }

        private static string ScanWorkflowCss()
        {
            return @"
.scan-workflow{min-height:100vh;background-color:#f8f8f6;background-image:radial-gradient(rgba(0,0,0,.16) .7px,transparent .8px);background-size:9px 9px}.scan-workflow-head{position:sticky;top:0;z-index:20;display:flex;min-height:82px;align-items:center;justify-content:space-between;gap:20px;padding:13px 24px;border-bottom:1.5px solid #111;background:rgba(248,248,246,.97)}.scan-workflow-head>div:first-child{min-width:0}.scan-workflow-head h1{margin:7px 0 3px;font-size:25px;line-height:1}.scan-workflow-head>div:first-child>span{display:block;overflow:hidden;color:#666;font-size:10px;text-overflow:ellipsis;white-space:nowrap}.scan-start,.scan-progress-primary,.scan-progress-secondary{display:inline-grid;height:35px;place-items:center;padding:0 14px;border:1.5px solid #111;border-radius:999px;background:#111;color:#fff;font-size:10px;font-weight:900;white-space:nowrap}.scan-start.disabled{background:#ddd;color:#777;pointer-events:none}.scan-progress-secondary{background:#fff;color:#111}.scan-settings-main,.scan-progress-main{width:min(960px,calc(100% - 48px));margin:0 auto;padding:18px 0 28px}.scan-root-section+ .scan-root-section{margin-top:22px}.scan-root-section>header{display:flex;align-items:flex-end;justify-content:space-between;gap:16px;margin-bottom:8px;padding:0 2px}.scan-root-section>header div>span,.scan-settings-foot span,.scan-current>span,.scan-activity>header span{display:block;color:#666;font-size:8px;font-weight:800;letter-spacing:.1em;text-transform:uppercase}.scan-root-section h2{margin:3px 0 0;font-size:16px}.scan-root-section>header em{color:#666;font-size:10px;font-style:normal}.scan-root-section>header>a{padding:5px 9px;border:1.5px solid #111;border-radius:999px;background:#fff;font-size:9px;font-weight:900}.scan-path-list{overflow:hidden;border:1.5px solid #111;border-radius:8px;background:#fff}.scan-path-row{display:flex;min-height:61px;align-items:center;justify-content:space-between;gap:16px;padding:9px 11px;border-bottom:1px solid #ddd}.scan-path-row:last-child{border-bottom:0}.scan-path-row.missing{background:#f0f0ed;color:#777}.scan-path-row>div{display:grid;min-width:0;grid-template-columns:58px minmax(0,1fr);gap:2px 8px;align-items:center}.scan-path-row>div>span{grid-row:1/3;color:#777;font-size:8px;font-weight:800;text-transform:uppercase}.scan-path-row>div>strong{overflow:hidden;font-size:11px;text-overflow:ellipsis;white-space:nowrap}.scan-path-row>div>em{color:#777;font-size:9px;font-style:normal}.scan-path-row nav{display:flex;align-items:center;gap:7px}.scan-path-remove{font-size:9px;font-weight:800}.scan-path-remove:hover{text-decoration:underline}.scan-path-toggle{display:flex;width:62px;height:28px;align-items:center;justify-content:space-between;padding:3px 7px 3px 3px;border:1.5px solid #111;border-radius:999px;background:#fff;font-size:8px;text-transform:uppercase}.scan-path-toggle span{width:20px;height:20px;border:1px solid #111;border-radius:50%;background:#fff;transition:transform .14s ease}.scan-path-toggle b{font-size:8px}.scan-path-toggle.enabled{padding-right:3px;padding-left:7px;background:#111;color:#fff}.scan-path-toggle.enabled span{order:2;border-color:#fff}.scan-path-missing{font-size:9px;font-weight:800;text-transform:uppercase}.scan-path-empty{padding:22px;color:#777;font-size:11px;text-align:center}.scan-settings-foot{display:flex;align-items:baseline;gap:10px;margin-top:17px;padding-top:9px;border-top:1px solid #111}.scan-settings-foot strong{font-size:11px}.scan-settings-foot em{margin-left:auto;color:#666;font-size:10px;font-style:normal}.scan-progress-main{padding-top:25px}.scan-progress-meter>div:first-child{display:flex;align-items:flex-end;justify-content:space-between}.scan-progress-meter strong{font-size:38px;line-height:1}.scan-progress-meter span{color:#666;font-size:10px}.scan-progress-track{height:18px;margin-top:10px;overflow:hidden;border:1.5px solid #111;border-radius:999px;background:#fff}.scan-progress-track span{display:block;height:100%;background:#111;transition:width .18s linear}.scan-current{margin-top:18px;padding:11px 0;border-top:1px solid #111;border-bottom:1px solid #111}.scan-current strong{display:block;overflow:hidden;margin-top:5px;font-family:Consolas,monospace;font-size:11px;text-overflow:ellipsis;white-space:nowrap}.scan-activity{margin-top:18px}.scan-activity>header{display:flex;align-items:center;justify-content:space-between}.scan-activity>header strong{font-size:10px}.scan-activity ol{height:190px;margin:8px 0 0;padding:8px 8px 8px 30px;overflow:auto;border:1.5px solid #111;border-radius:8px;background:#fff;font-family:Consolas,monospace;font-size:10px;line-height:1.7}.scan-activity li{overflow:hidden;padding-left:4px;border-bottom:1px solid #eee;text-overflow:ellipsis;white-space:nowrap}.scan-error .scan-progress-track span{background:#b52b22}.scan-cancelled .scan-progress-track span{background:#777}@media(max-width:720px){.scan-workflow-head{align-items:flex-start}.scan-settings-main,.scan-progress-main{width:calc(100% - 28px)}.scan-path-row>div{grid-template-columns:1fr}.scan-path-row>div>span{grid-row:auto}.scan-path-row>div>em{display:none}}";
        }

        private static string ScanWorkflowLayerCss()
        {
            return ".scan-root-section+.scan-root-section{margin-top:22px}";
        }

        private static string HomeCss()
        {
            return @"
.home-head{padding:16px 24px}.home-main{padding:14px 24px 12px}.home-foot{padding:4px 24px 14px}.logo-wordmark{width:42px;height:42px;padding:0;border:0;border-radius:0;background:transparent;overflow:hidden}.logo-wordmark img{display:block;width:100%;height:100%;object-fit:contain}.home-grid{gap:10px}.home-tile{position:relative;min-height:158px;border-radius:16px;padding:12px;overflow:hidden}.home-tile strong{font-size:24px}.home-tile.plus strong{font-size:54px}.home-tile .tile-hover{position:absolute;left:10px;right:10px;bottom:10px;z-index:2;display:block;max-height:52px;overflow:hidden;transform:translateY(8px);padding:7px 8px;border:1.5px solid #111;border-radius:8px;background:#111;color:#fff;font-size:10px;line-height:1.35;letter-spacing:.02em;text-transform:none;opacity:0;transition:opacity .14s ease,transform .14s ease;pointer-events:none}.home-tile.fill .tile-hover{background:#f7f7f4;color:#111}.home-tile:hover .tile-hover{opacity:1;transform:translateY(0)}@media(max-width:980px){.home-head{padding:14px 18px}.home-main{padding:12px 18px}.home-foot{padding:4px 18px 12px}.home-tile{min-height:138px}.home-tile strong{font-size:21px}}";
        }

        private static string PresetMenuCss()
        {
            return @"
.home-tile-main{display:flex;min-width:0;height:100%;flex-direction:column;justify-content:space-between}.preset-menu{position:absolute;top:8px;right:8px;z-index:5}.preset-menu summary{display:grid;width:26px;height:26px;place-items:center;border:1.5px solid #111;border-radius:8px;background:#f7f7f4;color:#111;cursor:pointer;font-size:19px;font-weight:900;line-height:1;list-style:none}.preset-menu summary::-webkit-details-marker{display:none}.preset-menu[open] summary{background:#111;color:#fff}.preset-menu>div{position:absolute;top:32px;right:0;display:flex;min-width:88px;flex-direction:column;gap:2px;padding:4px;border:1.5px solid #111;border-radius:9px;background:#fff;box-shadow:3px 3px 0 #111}.preset-menu>div a{padding:6px 7px;border-radius:5px;font-size:11px;font-weight:800}.preset-menu>div a:hover{background:#111;color:#fff}.preset-menu>div a:last-child:hover{background:#ba2f24}.preset-home .tile-hover{right:42px}";
        }

        private static string ManualRedesignCss()
        {
            return @"
.manual{padding-bottom:72px}.manual-head{align-items:center;padding:18px 26px}.manual-heading{min-width:0}.manual-title-row{display:flex;align-items:center;gap:12px;margin-top:10px}.manual-title-row h1{margin:0;font-size:32px}.manual-title-row .manual-meta{margin-top:6px}.preset-icon-control{position:relative;z-index:20}.preset-icon-trigger{display:grid;width:48px;height:48px;place-items:center;padding:8px;border:1.5px solid #111;border-radius:8px;background:#fff;cursor:pointer;transition:transform .12s ease,box-shadow .12s ease}.preset-icon-trigger:hover,.preset-icon-control:focus-within .preset-icon-trigger{transform:translate(-2px,-2px);box-shadow:3px 3px 0 #111}.preset-icon-trigger .preset-icon{width:29px;height:29px}.preset-icon-popover{position:absolute;top:100%;left:0;display:none;width:380px;max-height:350px;overflow:auto;padding:10px;border:1.5px solid #111;border-radius:8px;background:#fff;box-shadow:5px 5px 0 #111}.preset-icon-control:hover .preset-icon-popover,.preset-icon-control:focus-within .preset-icon-popover{display:block}.icon-section+.icon-section{margin-top:12px;padding-top:10px;border-top:1px solid #d8d8d3}.icon-section>strong{display:block;margin-bottom:7px;font-size:10px;text-transform:uppercase;letter-spacing:.08em}.icon-choice-grid{display:grid;grid-template-columns:repeat(9,32px);gap:6px}.preset-icon-popover .icon-choice{width:32px;height:32px;flex:initial;padding:6px;border-radius:7px}.preset-icon-popover .icon-choice small{display:none!important}.preset-icon-popover .preset-icon{width:19px;height:19px}.preset-icon img{display:block;width:100%;height:100%;object-fit:contain}.icon-choice.selected .streamline-icon img{filter:invert(1)}.preset-composer{display:grid;grid-template-columns:minmax(170px,.8fr) minmax(260px,1.5fr) auto auto;gap:10px;align-items:center;margin:14px 26px}.preset-composer input{height:38px;min-width:0;padding:0 13px;border:1.5px solid #111;border-radius:999px;background:#fff;outline:none}.preset-composer button,.preset-composer>a{display:inline-grid;height:38px;place-items:center;padding:0 14px;border:1.5px solid #111;border-radius:999px;background:#fff;font-size:11px;font-weight:900;white-space:nowrap}.preset-composer button{background:#111;color:#fff;cursor:pointer}.controls{margin:14px 26px}.view-switch{display:inline-flex;padding:2px;border:1.5px solid #111;border-radius:999px;background:#fff}.view-switch a{border:0!important;padding:5px 9px!important}.view-switch a.active{background:#111;color:#fff}.plugin-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:10px;margin:0 26px}.plugin-card{min-width:0;border:1.5px solid #111;border-radius:8px;background:#fff;overflow:hidden;transition:transform .12s ease,box-shadow .12s ease}.plugin-card:hover{transform:translate(-2px,-2px);box-shadow:4px 4px 0 #111}.plugin-card.blocked{color:#696969;background:#fafaf7}.plugin-card>header{display:flex;align-items:flex-start;justify-content:space-between;gap:8px;padding:11px;border-bottom:1px solid #d8d8d3}.plugin-card-identity{display:flex;min-width:0;align-items:center;gap:8px}.plugin-card-identity>span:last-child{display:flex;min-width:0;flex-direction:column;gap:3px}.plugin-card-identity strong{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:13px}.plugin-card-identity small{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:10px;color:#777}.card-load{display:grid;min-width:38px;height:24px;place-items:center;border:1.5px solid #111;border-radius:999px;background:#fff;font-size:9px;font-weight:900}.plugin-card.enabled .card-load{background:#111;color:#fff}.plugin-card-meta{display:flex;align-items:center;justify-content:space-between;padding:8px 11px;font-size:10px}.plugin-card-meta a{padding:4px 7px;border:1px solid #111;border-radius:999px;font-weight:800}.plugin-card-meta a.pinned{background:#111;color:#fff}.plugin-card>footer{display:flex;align-items:center;gap:8px;padding:8px 11px;border-top:1px solid #e2e2de;font-size:10px}.plugin-card>footer span{min-width:0;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#666}.plugin-card>footer a{font-weight:900}.plugin-grid-empty{margin:0 26px;padding:34px;border:1.5px dashed #111;border-radius:8px;text-align:center;color:#777}.manual-launch{position:fixed;right:24px;bottom:18px;z-index:15;padding:9px 14px;border:1.5px solid #111;border-radius:999px;background:#111;color:#fff;font-size:11px;font-weight:900;box-shadow:3px 3px 0 #999}.plugin-list-view{margin:0 26px}@media(max-width:1050px){.plugin-grid{grid-template-columns:repeat(3,minmax(0,1fr))}.icon-choice-grid{grid-template-columns:repeat(8,32px)}}@media(max-width:760px){.preset-composer{grid-template-columns:1fr 1fr}.plugin-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.preset-icon-popover{width:330px}.icon-choice-grid{grid-template-columns:repeat(7,32px)}}";
        }

        private static string GridCardCss()
        {
            return @"
.plugin-card{aspect-ratio:1/1;display:flex;flex-direction:column}.plugin-card>header{flex:0 0 auto}.plugin-card-meta{flex:1;align-items:flex-start}.plugin-card>footer{margin-top:auto}.plugin-card.enabled{background:#111;color:#fff}.plugin-card.enabled:hover{box-shadow:4px 4px 0 #777}.plugin-card.enabled>header{border-bottom-color:#444}.plugin-card.enabled .plugin-card-identity small,.plugin-card.enabled>footer span{color:#d7d7d2}.plugin-card.enabled .mono-icon{border-color:#fff;background:#fff;color:#111}.plugin-card.enabled .card-load{border-color:#fff;background:#fff;color:#111}.plugin-card.enabled .plugin-card-meta a{border-color:#fff;color:#fff}.plugin-card.enabled .plugin-card-meta a.pinned{background:#fff;color:#111}.plugin-card.enabled>footer{border-top-color:#444}.plugin-card.enabled>footer a{color:#fff}";
        }

        private static string DenseLayoutCss()
        {
            return @"
.home-main{padding:12px 20px}.home-grid{grid-template-columns:repeat(6,minmax(0,1fr));gap:8px}.home-tile{min-height:132px;padding:10px;border-radius:10px;box-shadow:3px 3px 0 #111}.home-tile:hover{transform:translate(2px,2px);box-shadow:1px 1px 0 #111}.home-tile strong{font-size:17px}.home-tile em{font-size:10px}.home-tile.plus strong{font-size:38px}.home-tile .preset-icon{width:30px;height:30px}.plugin-grid{grid-template-columns:repeat(10,minmax(0,1fr));gap:6px;margin:0 18px}.plugin-card{position:relative;border-radius:7px}.plugin-card:hover{transform:translate(-1px,-1px);box-shadow:2px 2px 0 #111}.plugin-card>header{display:block;padding:7px;border-bottom:0}.plugin-card-identity{height:70px;flex-direction:column;justify-content:center;gap:5px;text-align:center}.plugin-card-identity>span:last-child{width:100%}.plugin-card-identity strong{display:-webkit-box;overflow:hidden;white-space:normal;font-size:10px;line-height:1.15;-webkit-box-orient:vertical;-webkit-line-clamp:2}.plugin-card-identity small{display:none}.plugin-card .mono-icon{width:28px;height:28px;flex:0 0 28px;border-radius:6px}.card-load{position:absolute;top:5px;right:5px;min-width:28px;height:17px;border-width:1px;font-size:7px}.plugin-card-meta{min-height:0;align-items:flex-end;justify-content:flex-end;padding:4px 6px 6px}.plugin-card-meta>span{display:none}.plugin-card-meta a{padding:3px 5px;font-size:7px}.plugin-card>footer{display:none}.card-load.pending,.load-dot.pending{opacity:.45;pointer-events:none}.card-load.toggle-error,.load-dot.toggle-error{background:#ba2f24!important;color:#fff!important}@media(max-width:1050px){.home-grid{grid-template-columns:repeat(3,minmax(0,1fr))}.plugin-grid{grid-template-columns:repeat(8,minmax(0,1fr))}}@media(max-width:820px){.plugin-grid{grid-template-columns:repeat(6,minmax(0,1fr))}}@media(max-width:620px){.home-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.plugin-grid{grid-template-columns:repeat(4,minmax(0,1fr))}}";
        }

        private static string StartupLayoutCss()
        {
            return @"
.home{display:flex;min-height:100vh;flex-direction:column}.home-head{min-height:42px;justify-content:flex-end;padding:12px 24px 6px}.home-meta{margin-top:0}.home-main{padding:8px 24px 0}.home-grid{display:flex;width:800px;max-width:calc(100vw - 48px);justify-content:flex-start;gap:10px;margin:0 auto;padding:0 0 8px;overflow-x:auto;overflow-y:hidden;scrollbar-width:thin;scroll-snap-type:x proximity}.home-tile{width:152px;height:152px;min-width:152px;min-height:0;flex:0 0 152px;aspect-ratio:1/1;padding:10px;border-radius:9px;scroll-snap-align:start}.home-tile strong{font-size:16px}.home-tile.plus strong{font-size:36px}.home-tile .preset-icon{width:28px;height:28px}.preset-home{cursor:grab;user-select:none}.preset-home.dragging{cursor:grabbing;opacity:.45;transform:scale(.97);box-shadow:none}.manual-selection{display:flex;width:800px;max-width:100%;align-items:center;justify-content:space-between;margin:5px auto 0;padding:8px 2px;color:#111}.manual-selection span{display:flex;align-items:baseline;gap:10px}.manual-selection strong{font-size:13px}.manual-selection em{font-size:10px;font-style:normal;color:#666}.manual-selection b{font-size:19px;font-weight:400;transition:transform .12s ease}.manual-selection:hover strong{text-decoration:underline;text-underline-offset:3px}.manual-selection:hover b{transform:translateX(4px)}.home-foot{justify-content:center;margin-top:0;padding:2px 24px 10px}@media(max-width:840px){.home-grid{width:446px}.home-tile{width:142px;height:142px;min-width:142px;flex-basis:142px}.manual-selection{width:446px}}";
        }

        private static string PresetEditorCss()
        {
            return @"
.preset-editor{min-height:100vh;background-color:#f8f8f6;background-image:radial-gradient(rgba(0,0,0,.2) .7px,transparent .8px);background-size:9px 9px}.preset-editor .hidden,.preset-plugin-card.filtered-out{display:none!important}.preset-editor-head{position:sticky;top:0;z-index:30;display:flex;min-height:58px;align-items:center;justify-content:space-between;padding:9px 20px;border-bottom:1.5px solid #111;background:rgba(248,248,246,.97)}.preset-editor-heading,.preset-editor-actions{display:flex;align-items:center;gap:11px}.preset-editor-heading h1{margin:0;font-size:24px;line-height:1}.preset-editor-heading div>span{display:block;margin-top:3px;color:#666;font-size:8px;letter-spacing:.1em;text-transform:uppercase}.preset-editor-back{display:grid;width:32px;height:32px;place-items:center;border:1.5px solid #111;border-radius:50%;background:#fff;font-size:16px;transition:background .12s ease,color .12s ease}.preset-editor-back:hover{background:#111;color:#fff}.preset-editor-actions>span,.preset-editor-tools>span{color:#666;font-size:9px;font-weight:800;letter-spacing:.05em;text-transform:uppercase}.preset-editor-actions button{height:34px;padding:0 14px;border:1.5px solid #111;border-radius:999px;background:#111;color:#fff;font-size:10px;font-weight:900;cursor:pointer}.preset-editor-identity{position:relative;z-index:26;display:flex;min-height:188px;align-items:center;justify-content:center;padding:12px 20px;border-bottom:1.5px solid #111;background:rgba(248,248,246,.82)}.preset-live-tile{position:relative;z-index:31;display:flex;width:152px;height:152px;flex-direction:column;justify-content:space-between;padding:10px;overflow:visible;border:1.5px solid #111;border-radius:9px;background:#f8f8f6;box-shadow:3px 3px 0 #111;transition:box-shadow .14s ease}.preset-live-tile:hover{box-shadow:4px 4px 0 #111}.preset-live-tile>span{font-size:9px;letter-spacing:.12em;text-transform:uppercase}.preset-live-tile>strong{overflow:hidden;max-width:100%;border-bottom:1px dashed transparent;font-size:16px;line-height:1.05;text-overflow:ellipsis;white-space:nowrap;cursor:text;outline:none}.preset-live-tile>em{overflow:hidden;max-width:100%;border-bottom:1px dashed transparent;color:#666;font-size:10px;font-style:normal;line-height:1.25;text-overflow:ellipsis;white-space:nowrap;outline:none}.preset-live-tile [contenteditable=true]:hover{border-bottom-color:#888}.preset-live-tile [contenteditable=true]:focus{border-bottom-color:#111;text-overflow:clip}.preset-live-tile [contenteditable=true]:empty:before{content:attr(data-placeholder);color:#999}.preset-live-icon-control{position:relative;width:34px;height:34px;margin-left:-3px}.preset-live-icon{display:grid;width:34px;height:34px;place-items:center;padding:3px;border:1px solid transparent;border-radius:6px;background:transparent;cursor:pointer}.preset-live-icon:hover,.preset-live-icon[aria-expanded=true]{border-color:#111;background:#fff}.preset-live-icon .preset-icon{width:28px;height:28px}.editor-icon-popover{position:absolute;top:0;left:calc(100% + 14px);z-index:40;width:380px;max-height:350px;overflow:auto;padding:10px;border:1.5px solid #111;border-radius:9px;background:#fff;box-shadow:4px 4px 0 #111}.editor-icon-popover .icon-section+.icon-section{margin-top:12px;padding-top:10px;border-top:1px solid #d8d8d3}.editor-icon-popover .icon-section>strong{display:block;margin-bottom:7px;font-size:10px;text-transform:uppercase}.editor-icon-popover .icon-choice-grid{display:grid;grid-template-columns:repeat(9,32px);gap:6px}.editor-icon-popover .icon-choice{width:32px;height:32px;min-width:32px;padding:6px;border-radius:7px}.editor-icon-popover .icon-choice small{display:none}.editor-icon-popover .preset-icon{width:19px;height:19px}.preset-editor-tools{position:sticky;top:58px;z-index:25;display:flex;align-items:center;gap:8px;padding:8px 20px;border-bottom:1px solid #111;background:rgba(248,248,246,.97)}.preset-editor-tools input{min-width:0;height:34px;flex:1;padding:0 12px;border:1.5px solid #111;border-radius:999px;background:#fff;outline:none}.preset-editor-tools input:focus{box-shadow:0 0 0 2px #bbb}.preset-editor-tools button{height:30px;padding:0 10px;border:1.5px solid #111;border-radius:999px;background:#fff;font-size:9px;font-weight:900;cursor:pointer;transition:background .12s ease,color .12s ease}.preset-editor-tools button:hover{background:#111;color:#fff}.preset-editor-tools>span{min-width:70px;text-align:right}.preset-plugin-grid{display:grid;grid-template-columns:repeat(10,minmax(0,1fr));gap:8px;padding:13px 20px 24px}.preset-plugin-card{position:relative;display:flex!important;min-width:0;aspect-ratio:1;align-items:center;justify-content:center;gap:6px;padding:8px 6px 25px!important;border:1.5px solid #111;border-radius:8px;background:rgba(255,255,255,.88);color:#111;text-align:center;cursor:pointer;transition:transform .1s ease,box-shadow .1s ease,background .15s ease,color .15s ease}.preset-plugin-card:hover{transform:translate(-1px,-1px);box-shadow:2px 2px 0 #111}.preset-plugin-card.enabled{background:#111;color:#fff}.preset-plugin-card .mono-icon{width:31px;height:31px;flex:0 0 31px;border-radius:6px;background:#fff;color:#111}.preset-plugin-card>strong{width:100%;overflow:hidden;font-size:9px;line-height:1.15;text-overflow:ellipsis;white-space:nowrap}.preset-plugin-status{position:absolute;right:4px;bottom:7px;left:4px;overflow:hidden;color:#777;font-size:8px;font-weight:400;text-align:center;text-overflow:ellipsis;white-space:nowrap}.preset-plugin-card.enabled .preset-plugin-status{color:#d7d7d2}.preset-plugin-card.pending{opacity:.45;pointer-events:none}@media(max-width:1050px){.preset-plugin-grid{grid-template-columns:repeat(8,minmax(0,1fr))}}@media(max-width:820px){.preset-plugin-grid{grid-template-columns:repeat(6,minmax(0,1fr))}.editor-icon-popover{top:calc(100% + 8px);left:50%;width:330px;transform:translateX(-50%)}.editor-icon-popover .icon-choice-grid{grid-template-columns:repeat(7,32px)}}@media(max-width:620px){.preset-plugin-grid{grid-template-columns:repeat(4,minmax(0,1fr))}}";
        }

        private static string PresetEditorLayerCss()
        {
            return ".preset-editor-identity{z-index:auto}.preset-live-tile{z-index:auto}.preset-live-tile.icon-open{z-index:31}";
        }

        private sealed class PluginGroup
        {
            public string Key { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public List<PluginCandidate> Variants { get; set; } = new List<PluginCandidate>();
            public bool Load => Variants.Any(candidate => candidate.Load);
            public bool IsUserObjectGroup => Variants.Count > 0 && Variants.All(candidate => candidate.Kind == "GHUSER");
        }

        private sealed class PluginRelease
        {
            public string Key { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public List<PluginCandidate> Variants { get; set; } = new List<PluginCandidate>();
            public bool Load => Variants.Any(candidate => candidate.Load);
        }
    }
}

