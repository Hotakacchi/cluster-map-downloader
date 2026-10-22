using System.Diagnostics;
using System.Text;
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
    private readonly ProgressBar progressBar;
    private readonly Label statusLabel;
    private readonly bool autoClose;
    private string? lastWorldDir;
    private readonly List<string> lastPlainBundles = new();
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
            RowCount = 7,
            Padding = new Padding(10),
        };
        // 0..5 auto rows (URL / path / buttons / bar / status / footer-ish), 6 is the log.
        layoutRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layoutRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layoutRoot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 0: URL
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 1: output dir
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 2: buttons
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 3: progress bar
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 4: status
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // row 5: log (fills)
        layoutRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // row 6: footer note

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
            }
        };
        layoutRoot.Controls.Add(browse, 2, 1);

        var buttonRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Height = 40 };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        buttonRow.Controls.Add(downloadButton, 0, 0);
        buttonRow.Controls.Add(cancelButton, 1, 0);
        buttonRow.Controls.Add(openFolderButton, 2, 0);
        buttonRow.Controls.Add(convertButton, 3, 0);
        layoutRoot.Controls.Add(buttonRow, 0, 2);
        layoutRoot.SetColumnSpan(buttonRow, 3);

        layoutRoot.Controls.Add(progressBar, 0, 3);
        layoutRoot.SetColumnSpan(progressBar, 3);
        layoutRoot.Controls.Add(statusLabel, 0, 4);
        layoutRoot.SetColumnSpan(statusLabel, 3);
        layoutRoot.Controls.Add(logBox, 0, 5);
        layoutRoot.SetColumnSpan(logBox, 3);

        var footer = new Label
        {
            Text = "Public worlds only | no accounts | encrypted files are saved as-is | unofficial, not affiliated with Cluster",
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
        };
        layoutRoot.Controls.Add(footer, 0, 6);
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
        if (result.WorldId is { } worldId && worldId.Length > 0)
        {
            lastWorldDir = Path.Combine(Path.GetFullPath(outputBox.Text), worldId);
            openFolderButton.Enabled = Directory.Exists(lastWorldDir);
        }

        switch (result.Outcome)
        {
            case WorldDownloadOutcome.Success:
                statusLabel.Text = $"Done - {result.Files.Count} file(s) saved.";
                OnDownloadProducedBundles(result);
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
