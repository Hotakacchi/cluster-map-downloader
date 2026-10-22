using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClusterMapDownloader;
using ClusterMapDownloader.GuestEntry;

namespace ClusterMapDownloader.Gui;

/// <summary>
/// Minimal English-language paste-a-URL GUI on top of the pipeline: download
/// one public world (guest-entry flow), then optionally convert the downloaded
/// plain UnityFS bundles into ONE glTF/GLB scene model for three.js/Unity via
/// scripts/convert-bundle.ps1. It keeps every repo policy: masked tokens,
/// in-memory-only credentials, honest refusal reporting, no decryption,
/// personal-use-only conversion outputs.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TextBox urlBox;
    private readonly TextBox outputBox;
    private readonly TextBox logBox;
    private readonly Button downloadButton;
    private readonly Button cancelButton;
    private readonly Button openFolderButton;
    private readonly Button convertButton;
    private readonly Button refreshPastButton;
    private readonly ComboBox pastBox;
    private readonly ProgressBar progressBar;
    private readonly Label statusLabel;
    private readonly bool autoClose;
    private string? lastWorldDir;
    private readonly List<string> lastPlainBundles = new();
    private readonly Dictionary<string, string> pastWorlds = new(StringComparer.OrdinalIgnoreCase);
    private bool suppressPastSelection;
    private Process? convertProcess;
    private CancellationTokenSource? cts;

    public MainForm(string? presetUrl, string? presetOutput, bool autoCloseRequested)
    {
        autoClose = autoCloseRequested;

        Text = "cluster-map-downloader";
        MinimumSize = new Size(760, 480);
        Size = new Size(880, 640);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        urlBox = new TextBox { PlaceholderText = "https://cluster.mu/w/<WORLD_ID>", Dock = DockStyle.Fill };
        outputBox = new TextBox { Dock = DockStyle.Fill };

        logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
        };

        downloadButton = new Button { Text = "Download", Dock = DockStyle.Fill };
        cancelButton = new Button { Text = "Cancel", Enabled = false, Dock = DockStyle.Fill };
        openFolderButton = new Button
        {
            Text = "Open folder",
            Dock = DockStyle.Fill,
            Enabled = false,
        };
        convertButton = new Button
        {
            Text = "Convert to GLB",
            Dock = DockStyle.Fill,
            Enabled = false,
        };
        convertButton.Click += async (_, _) => await RunConversionAsync().ConfigureAwait(true);
        refreshPastButton = new Button { Text = "Refresh", Dock = DockStyle.Fill };
        refreshPastButton.Click += (_, _) => RefreshPastDownloads(null);
        pastBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        openFolderButton.Click += (_, _) =>
        {
            if (lastWorldDir is { } dir && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
        };

        progressBar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 40,
            Visible = false,
            Dock = DockStyle.Fill,
        };

        statusLabel = new Label
        {
            Text = "Paste a world URL and press Download.",
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
        };

        downloadButton.Click += async (_, _) => await RunDownloadAsync().ConfigureAwait(true);
        cancelButton.Click += (_, _) =>
        {
            if (convertProcess is { HasExited: false })
            {
                KillConversion();
            }
            else
            {
                cts?.Cancel();
            }
        };

        var layoutRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 8,
            Padding = new Padding(10),
        };
        // 0..6 auto rows (URL / path / past / buttons / bar / status / footer-ish), 6 is the log, 7 footer.
        layoutRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layoutRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layoutRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 0: URL
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 1: output dir
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 2: past downloads
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 3: buttons
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 4: progress bar
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 5: status
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // row 6: log (fills)
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 7: footer note

        var urlLabel = new Label
        {
            Text = "World URL:",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 6, 0),
        };
        layoutRoot.Controls.Add(urlLabel, 0, 0);
        layoutRoot.Controls.Add(urlBox, 1, 0);
        layoutRoot.SetColumnSpan(urlBox, 2);

        var outputLabel = new Label
        {
            Text = "Output:",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 6, 0),
        };
        layoutRoot.Controls.Add(outputLabel, 0, 1);
        layoutRoot.Controls.Add(outputBox, 1, 1);

        var browse = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { ShowNewFolderButton = true };
            if (Directory.Exists(outputBox.Text))
            {
                dialog.SelectedPath = outputBox.Text;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                outputBox.Text = dialog.SelectedPath;
                RefreshPastDownloads(null);
            }
        };
        layoutRoot.Controls.Add(browse, 2, 1);

        var pastLabel = new Label
        {
            Text = "Past downloads:",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 6, 0),
        };
        layoutRoot.Controls.Add(pastLabel, 0, 2);
        layoutRoot.Controls.Add(pastBox, 1, 2);
        layoutRoot.Controls.Add(refreshPastButton, 2, 2);
        pastBox.SelectedIndexChanged += (_, _) => OnPastSelectionChanged();
        outputBox.Leave += (_, _) => RefreshPastDownloads(null);

        var buttonRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Height = 40 };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        buttonRow.Controls.Add(downloadButton, 0, 0);
        buttonRow.Controls.Add(cancelButton, 1, 0);
        buttonRow.Controls.Add(openFolderButton, 2, 0);
        buttonRow.Controls.Add(convertButton, 3, 0);
        layoutRoot.Controls.Add(buttonRow, 0, 3);
        layoutRoot.SetColumnSpan(buttonRow, 3);

        layoutRoot.Controls.Add(progressBar, 0, 4);
        layoutRoot.SetColumnSpan(progressBar, 3);
        layoutRoot.Controls.Add(statusLabel, 0, 5);
        layoutRoot.SetColumnSpan(statusLabel, 3);
        layoutRoot.Controls.Add(logBox, 0, 6);
        layoutRoot.SetColumnSpan(logBox, 3);

        var footer = new Label
        {
            Text = "Public worlds only | no accounts | encrypted files are saved as-is | unofficial, not affiliated with Cluster",
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
        };
        layoutRoot.Controls.Add(footer, 0, 7);
        layoutRoot.SetColumnSpan(footer, 3);

        Controls.Add(layoutRoot);

        outputBox.Text = presetOutput is { Length: > 0 }
            ? presetOutput
            : Path.Combine(Environment.CurrentDirectory, "output");

        if (presetUrl is { Length: > 0 })
        {
            urlBox.Text = presetUrl;
            Shown += async (_, _) => await RunDownloadAsync().ConfigureAwait(true);
        }

        Shown += (_, _) => RefreshPastDownloads(null);

        AcceptButton = downloadButton;
        FormClosing += (_, _) =>
        {
            cts?.Cancel();
            if (convertProcess is { HasExited: false })
            {
                KillConversion();
            }
        };
    }

    private async Task RunDownloadAsync()
    {
        var worldUrl = urlBox.Text.Trim();
        if (worldUrl.Length == 0)
        {
            MessageBox.Show(this, "Enter a world URL.", "No URL",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        cts = new CancellationTokenSource();
        SetBusy(true);
        statusLabel.Text = "Working...";

        var resolver = GuestEntryResolverFactory.Create(api: new ClusterApiOptions());
        if (resolver is null)
        {
            SetBusy(false);
            MessageBox.Show(
                this,
                "Could not find a local cluster client to read the public key from.\n\n" +
                "Install/extract the cluster client, or run the CLI with --firebase-api-key.",
                "No client found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var options = new WorldDownloadOptions
        {
            WorldUrl = worldUrl,
            OutputDirectory = outputBox.Text,
            DeliveryResolver = resolver,
            FileProgress = new Progress<DownloadedFileProgress>(ReportProgress),
        };

        var notifier = new GuiProgressNotifier(this, logBox);
        try
        {
            var result = await WorldDownloader.RunAsync(options, notifier, cts.Token).ConfigureAwait(true);
            OnRunFinished(result);
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            // Any unexpected GUI-side exception must never silently kill the run:
            // surface a short message here and the detail in a persistent crash file.
            statusLabel.Text = "Error: " + SecretMasker.Mask(ex.Message);
            try
            {
                Directory.CreateDirectory(outputBox.Text);
                await File.WriteAllTextAsync(
                    Path.Combine(outputBox.Text, "gui-crash.log"), ex.ToString(), cts.Token).ConfigureAwait(true);
            }
            catch
            {
                // best effort
            }

            MessageBox.Show(
                this,
                SecretMasker.Mask(ex.Message) + "\n\n(Details: gui-crash.log)",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ReportProgress(DownloadedFileProgress progress)
    {
        if (progress.Bytes is { TotalBytes: > 0 } bytes)
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            var percent = (int)Math.Clamp(100.0 * bytes.DownloadedBytes / bytes.TotalBytes.Value, 0, 100);
            progressBar.Value = percent;
            statusLabel.Text = $" {progress.CurrentFile}/{progress.TotalFiles}: {progress.RelativeFileName}" +
                               $"  {FileDownloader.FormatBytes(bytes.DownloadedBytes)} / " +
                               $"{FileDownloader.FormatBytes(bytes.TotalBytes.Value)}";
        }
        else
        {
            progressBar.Style = ProgressBarStyle.Marquee;
            statusLabel.Text = $" {progress.CurrentFile}/{progress.TotalFiles}: {progress.RelativeFileName}";
        }
    }

    private void OnRunFinished(WorldDownloadResult result)
    {
        // The world directory name is the map name when available (Stage 2.5),
        // so derive it from the files that were actually written instead of
        // assuming "<output>/<worldId>".
        var worldDirFromResult = ResolveGivenWorldDir(result);
        if (worldDirFromResult is not null)
        {
            lastWorldDir = worldDirFromResult;
        }

        switch (result.Outcome)
        {
            case WorldDownloadOutcome.Success:
                statusLabel.Text = $"Done - {result.Files.Count} file(s) saved.";
                OnDownloadProducedBundles(result);
                RefreshPastDownloads(lastWorldDir); // auto-select the freshly downloaded world
                if (autoClose)
                {
                    BeginInvoke(new Action(Close));
                }

                break;

            case WorldDownloadOutcome.NotImplemented:
                statusLabel.Text = "Stopped: not implemented.";
                MessageBox.Show(this, result.FailureMessage, "Not implemented", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;

            case WorldDownloadOutcome.RequiresNonPublicAccess:
                statusLabel.Text = "Stopped: cluster refused this download.";
                MessageBox.Show(
                    this,
                    SecretMasker.Mask(result.Delivery?.Reason ?? result.FailureMessage ?? string.Empty),
                    "Refused",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);
                break;

            case WorldDownloadOutcome.NotPublic:
                statusLabel.Text = "Stopped: not a public world.";
                MessageBox.Show(this, result.FailureMessage, "Not a public world", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;

            case WorldDownloadOutcome.Usage:
                statusLabel.Text = "Stopped: invalid input.";
                MessageBox.Show(this, result.FailureMessage, "Invalid input", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;

            default:
                statusLabel.Text = "Error.";
                MessageBox.Show(this, result.FailureMessage, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                break;
        }
    }

    private void SetBusy(bool busy)
    {
        downloadButton.Enabled = !busy;
        convertButton.Enabled = !busy && lastPlainBundles.Count > 0;
        cancelButton.Enabled = busy;
        if (busy)
        {
            openFolderButton.Enabled = false;
            progressBar.Visible = true;
        }
        else
        {
            openFolderButton.Enabled = lastWorldDir is not null && Directory.Exists(lastWorldDir);
            progressBar.Visible = false;
        }
    }

    private void OnDownloadProducedBundles(WorldDownloadResult result)
    {
        // Remember the plain UnityFS bundles (.win) of this successful run; the
        // conversion button converts exactly these files, nothing else.
        lastPlainBundles.Clear();
        foreach (var file in result.Files)
        {
            if (file.FileFormat == "unityfs-plain" &&
                file.RelativeFileName.EndsWith(".win", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(file.AbsolutePath))
            {
                lastPlainBundles.Add(Path.GetFullPath(file.AbsolutePath));
            }
        }

        if (lastPlainBundles.Count > 0)
        {
            AppendLog(
                $"[cv] {lastPlainBundles.Count} plain bundle(s) ready for conversion (press \"Convert to GLB\").");
        }
        else
        {
            AppendLog("[cv] no plain UnityFS bundles in this download; conversion unavailable " +
                      "(encrypted files are never converted).");
        }
    }

    // ---- Past downloads picker --------------------------------------------

    /// <summary>
    /// Rescans the output directory for past downloads (world folders that
    /// contain bundles or a downloads manifest) and repopulates the picker.
    /// </summary>
    /// <param name="selectDir">Folder to auto-select (e.g. a fresh download),
    /// or null to keep/clear the current selection sensibly.</param>
    private void RefreshPastDownloads(string? selectDir)
    {
        string? keepDir = pastBox.SelectedItem is string previous &&
                          pastWorlds.TryGetValue(previous, out var previousDir)
            ? previousDir
            : null;

        string? selectedDisplay = null;
        suppressPastSelection = true;
        try
        {
            pastBox.Items.Clear();
            pastWorlds.Clear();

            var root = outputBox.Text;
            var candidates = new List<string>();
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var hasBundles = Directory.Exists(Path.Combine(dir, "bundles")) &&
                                     Directory.EnumerateFiles(Path.Combine(dir, "bundles"), "*.win")
                                         .Any();
                    var hasManifest = File.Exists(Path.Combine(dir, "downloads-manifest.json"));
                    if (hasBundles || hasManifest)
                    {
                        candidates.Add(dir);
                    }
                }
            }

            foreach (var dir in candidates.OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var name = ReadSnapshotName(dir) ?? Path.GetFileName(dir);
                var display = name;
                for (var suffix = 2; pastWorlds.ContainsKey(display); suffix++)
                {
                    display = $"{name} ({suffix})";
                }

                pastWorlds[display] = dir;
                pastBox.Items.Add(display);

                var wanted = selectDir ?? keepDir;
                if (wanted is not null &&
                    string.Equals(Path.GetFullPath(wanted), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                {
                    selectedDisplay = display;
                }
            }
        }
        finally
        {
            suppressPastSelection = false;
        }

        pastBox.SelectedItem = selectedDisplay; // fires OnPastSelectionChanged when set
    }

    private void OnPastSelectionChanged()
    {
        if (suppressPastSelection)
        {
            return;
        }

        pastWorlds.TryGetValue(pastBox.SelectedItem as string ?? string.Empty, out var dir);
        if (dir is null)
        {
            return;
        }

        lastWorldDir = dir;
        lastPlainBundles.Clear();

        var manifestFile = Path.Combine(dir, "downloads-manifest.json");
        var usedManifest = false;
        try
        {
            if (File.Exists(manifestFile))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestFile));
                if (document.RootElement.TryGetProperty("files", out var files) &&
                    files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in files.EnumerateArray())
                    {
                        var relative = file.TryGetProperty("relativeFileName", out var r) ? r.GetString() : null;
                        var format = file.TryGetProperty("fileFormat", out var f) ? f.GetString() : null;
                        if (relative is null || format != "unityfs-plain" ||
                            !relative.EndsWith(".win", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var bundlePath = Path.GetFullPath(Path.Combine(dir, relative));
                        if (File.Exists(bundlePath))
                        {
                            lastPlainBundles.Add(bundlePath);
                            usedManifest = true;
                        }
                    }
                }
            }
        }
        catch
        {
            // unreadable manifest: fall back to a plain glob below
        }

        if (!usedManifest)
        {
            // No manifest (or it does not list any file): offer every .win bundle;
            // the conversion script re-verifies the magic and honest-skips what is
            // not a plain UnityFS bundle.
            var bundleDir = Path.Combine(dir, "bundles");
            if (Directory.Exists(bundleDir))
            {
                foreach (var file in Directory.EnumerateFiles(bundleDir, "*.win").OrderBy(f => f))
                {
                    lastPlainBundles.Add(Path.GetFullPath(file));
                }
            }
        }

        openFolderButton.Enabled = Directory.Exists(dir);
        convertButton.Enabled = lastPlainBundles.Count > 0;
        AppendLog($"[cv] selected past download: {lastWorldDir}");
        if (lastPlainBundles.Count > 0)
        {
            AppendLog($"[cv] {lastPlainBundles.Count} plain bundle(s) ready (press \"Convert to GLB\").");
        }
        else
        {
            AppendLog("[cv] no plain UnityFS bundles in this folder; conversion unavailable " +
                      "(encrypted files are never converted).");
        }
    }

    private static string? ReadSnapshotName(string worldDir)
    {
        try
        {
            var snapshot = Path.Combine(worldDir, "world-metadata.json");
            if (!File.Exists(snapshot))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(snapshot));
            if (document.RootElement.TryGetProperty("metadata", out var metadata) &&
                metadata.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String)
            {
                var text = name.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Location of the just-downloaded world folder, derived from the files the
    /// pipeline actually wrote (the folder name may be the map name).
    /// </summary>
    private static string? ResolveGivenWorldDir(WorldDownloadResult result)
    {
        foreach (var file in result.Files)
        {
            if (string.Equals(file.RelativeFileName, "world-metadata.json", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(file.AbsolutePath));
                if (dir is { Length: > 0 } && Directory.Exists(dir))
                {
                    return dir;
                }
            }
        }

        foreach (var file in result.Files)
        {
            if (file.RelativeFileName.StartsWith("bundles/", StringComparison.OrdinalIgnoreCase))
            {
                var marker = Path.GetDirectoryName(Path.GetFullPath(file.AbsolutePath));
                var dir = marker is null ? null : Path.GetDirectoryName(marker);
                if (dir is { Length: > 0 } && Directory.Exists(dir))
                {
                    return dir;
                }
            }
        }

        if (result.WorldId is { Length: > 0 } worldId)
        {
            return Path.Combine(Path.GetFullPath(
                Directory.Exists(result.Options.OutputDirectory)
                    ? result.Options.OutputDirectory
                    : Environment.CurrentDirectory), worldId);
        }

        return null;
    }

    private sealed record ConversionContext(string PWshPath, string RepoRoot, string ScriptPath, string ToolExePath);

    private ConversionContext? ResolveConversionContext()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        var script = Path.Combine(repoRoot, "scripts", "convert-bundle.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show(
                this,
                "The conversion script was not found at:\n" + script +
                "\n\nRun the GUI from the repository layout (see README section 7).",
                "Script missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        var pwsh = FindPWsh();
        if (pwsh is null)
        {
            MessageBox.Show(
                this,
                "PowerShell 7 (pwsh.exe) is required for conversion but was not found.\n" +
                "Install it from https://aka.ms/powershell and try again.",
                "PowerShell 7 missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        var toolExe = Path.Combine(repoRoot, ".work", "third-party", "assetripper", "tool",
            "AssetRipper.GUI.Free.exe");
        return new ConversionContext(pwsh, repoRoot, script, toolExe);
    }

    private static string? FindPWsh()
    {
        var candidates = new[]
        {
            "pwsh",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe"),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-NoProfile -Command exit 0",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                if (probe is null)
                {
                    continue;
                }

                var exited = probe.WaitForExit(5000);
                if (!exited)
                {
                    try { probe.Kill(true); } catch { }
                }

                return candidate;
            }
            catch (Exception)
            {
                // try the next candidate
            }
        }

        return null;
    }

    private void KillConversion()
    {
        // Kill the whole pwsh process tree (incl. the AssetRipper child);
        // TaskKill keeps the cleanup honest even when the script's finally
        // blocks cannot run anymore.
        if (convertProcess is not { HasExited: false } process)
        {
            return;
        }

        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {process.Id}",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            _ = killer?.WaitForExit(3000);
        }
        catch
        {
            try { process.Kill(true); } catch { }
        }
    }

    private async Task RunConversionAsync()
    {
        if (lastPlainBundles.Count == 0)
        {
            MessageBox.Show(this, "Download a world first; conversion works on the" +
                " plain UnityFS bundles of a finished download.", "Nothing to convert",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (ResolveConversionContext() is not { } context)
        {
            return;
        }

        var fetchSwitch = string.Empty;
        if (!File.Exists(context.ToolExePath))
        {
            var choice = MessageBox.Show(
                this,
                "The conversion engine (AssetRipper, GPL-3.0 open source, ~80 MB on disk) " +
                "is not present yet.\n\n" +
                "Fetch it once now? It stays in .work (git-ignored, never committed).",
                "Fetch conversion tool?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (choice != DialogResult.Yes)
            {
                statusLabel.Text = "Conversion stopped: tool not fetched.";
                return;
            }

            fetchSwitch = " -FetchTool";
        }

        SetBusy(true);
        try
        {
            statusLabel.Text = $"Converting {lastPlainBundles.Count} bundle(s)...";
            AppendLog($"[cv] converting {lastPlainBundles.Count} bundle(s) " +
                      $"(AssetRipper engine, GPL-3.0; output: converted\\ under the world folder).");
            for (var i = 0; i < lastPlainBundles.Count; i++)
            {
                statusLabel.Text = $"Converting bundle {i + 1}/{lastPlainBundles.Count}...";
                var bundle = lastPlainBundles[i];
                AppendLog($"[cv] bundle {i + 1}/{lastPlainBundles.Count}: {Path.GetFileName(bundle)}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = context.PWshPath,
                    Arguments =
                        $"-NoProfile -ExecutionPolicy Bypass -File \"{context.ScriptPath}\"{fetchSwitch}" +
                        $" -Bundle \"{bundle}\"",
                    WorkingDirectory = context.RepoRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                Process? process = null;
                try
                {
                    process = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    AppendLog("[cv] could not start pwsh: " + SecretMasker.Mask(ex.Message));
                }

                if (process is null)
                {
                    statusLabel.Text = "Conversion failed to start.";
                    break;
                }

                convertProcess = process;
                var stdoutTask = PumpToLogAsync(process.StandardOutput);
                var stderrTask = PumpToLogAsync(process.StandardError);
                await process.WaitForExitAsync().ConfigureAwait(true);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(true);
                convertProcess = null;

                var code = process.ExitCode;
                if (code == 0)
                {
                    statusLabel.Text = $"Converted {i + 1}/{lastPlainBundles.Count}.";
                }
                else
                {
                    var reason = code switch
                    {
                        2 => "not plain UnityFS (honest skip, no decryption)",
                        3 => "conversion tool missing/usage error",
                        _ => "see log above",
                    };
                    statusLabel.Text = $"Bundle {i + 1}/{lastPlainBundles.Count}: {reason}.";
                    AppendLog($"[cv] exit code {code}: {reason}.");
                    if (code == 3)
                    {
                        break;
                    }
                }
            }

            if (convertProcess is null && lastPlainBundles.Count > 0)
            {
                statusLabel.Text = "Done - results in the converted folder (Open folder shows the world dir).";
            }
        }
        catch (Exception ex)
        {
            statusLabel.Text = "Conversion error.";
            AppendLog("[cv] " + SecretMasker.Mask(ex.Message));
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "gui-crash.log"), ex.ToString());
            }
            catch
            {
                // best effort
            }

            MessageBox.Show(
                this,
                SecretMasker.Mask(ex.Message) + "\n\n(Details: gui-crash.log)",
                "Conversion error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            convertProcess = null;
            SetBusy(false);
        }
    }

    private async Task PumpToLogAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(true) is { } line)
        {
            AppendLog(line);
        }
    }

    private void AppendLog(string line)
    {
        BeginInvoke(() => logBox.AppendText(line + Environment.NewLine));
    }

    private sealed class GuiProgressNotifier(Form owner, TextBox logBox) : IProgressNotifier
    {
        public void Info(string message) => Append("     " + message);

        public void Success(string message) => Append("[ok] " + message);

        public void Warn(string message) => Append("[!!] " + message);

        public void Error(string message) => Append("[!!] " + message);

        private void Append(string line)
        {
            owner.BeginInvoke(() => logBox.AppendText(line + Environment.NewLine));
        }
    }
}
