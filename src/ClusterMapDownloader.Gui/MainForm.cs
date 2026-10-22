using System.Diagnostics;
using ClusterMapDownloader;
using ClusterMapDownloader.GuestEntry;

namespace ClusterMapDownloader.Gui;

/// <summary>
/// Minimal Japanese-language paste-a-URL GUI on top of the pipeline. It enters
/// the documented opt-in guest-entry flow automatically (that is the GUI's
/// purpose) while keeping every repo policy: masked tokens, in-memory-only
/// credentials, honest refusal reporting, no decryption.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TextBox urlBox;
    private readonly TextBox outputBox;
    private readonly TextBox logBox;
    private readonly Button downloadButton;
    private readonly Button cancelButton;
    private readonly Button openFolderButton;
    private readonly ProgressBar progressBar;
    private readonly Label statusLabel;
    private readonly bool autoClose;
    private string? lastWorldDir;
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
        cancelButton.Click += (_, _) => cts?.Cancel();

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

        var buttonRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Height = 40 };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        buttonRow.Controls.Add(downloadButton, 0, 0);
        buttonRow.Controls.Add(cancelButton, 1, 0);
        buttonRow.Controls.Add(openFolderButton, 2, 0);
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
        FormClosing += (_, _) => cts?.Cancel();
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
                statusLabel.Text = $"Done — {result.Files.Count} file(s) saved.";
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
