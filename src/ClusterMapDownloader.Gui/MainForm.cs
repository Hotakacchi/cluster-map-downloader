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

        Text = "cluster-map-downloader — クラスタ公開ワールド取得ツール(非公式・研究用)";
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

        downloadButton = new Button { Text = "ダウンロード", Dock = DockStyle.Fill };
        cancelButton = new Button { Text = "中止", Enabled = false, Dock = DockStyle.Fill };
        openFolderButton = new Button
        {
            Text = "保存フォルダを開く",
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
            Text = " 待機中 …(ワールドURL を貼って「ダウンロード」)",
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
            Text = "ワールドURL:",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 6, 0),
        };
        layoutRoot.Controls.Add(urlLabel, 0, 0);
        layoutRoot.Controls.Add(urlBox, 1, 0);
        layoutRoot.SetColumnSpan(urlBox, 2);

        var outputLabel = new Label
        {
            Text = "保存先:",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 6, 0),
        };
        layoutRoot.Controls.Add(outputLabel, 0, 1);
        layoutRoot.Controls.Add(outputBox, 1, 1);

        var browse = new Button { Text = "参照…", Dock = DockStyle.Fill };
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
            Text = "公開ワールド専用。匿名 Firebase ゲストサインアップ(無料クライアント新規導入と同じ 1 回分;" +
                   "トークンはメモリのみ・保存/表示なし)。単一ワールド、列挙なし、AES 暗号化ファイルはそのまま保存(復号なし)。",
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
            MessageBox.Show(
                this,
                "ワールドURL を入力してください。例:\n  https://cluster.mu/w/<WORLD_ID>",
                "URL が必要です",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        cts = new CancellationTokenSource();
        SetBusy(true);

        var resolver = GuestEntryResolverFactory.Create(api: new ClusterApiOptions());
        if (resolver is null)
        {
            SetBusy(false);
            MessageBox.Show(
                this,
                "公開 Firebase Web API キーを取得できるローカル資料が見つかりませんでした。\n\n" +
                "次のいずれかを用意してください:\n" +
                " 1. キーを直接指定(--firebase-api-key / Firebase web API key)\n" +
                " 2. 解凍済み cluster クライアントパッケージのディレクトリ\n" +
                "    (*_Data/resources.assets を含むフォルダ)\n" +
                " 3. IL2CPP ダンプ(stringliteral.json)のあるディレクトリ\n\n" +
                "インストール済みの cluster クライアントは自動探索されます。",
                "キーの自動取得に失敗",
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
            statusLabel.Text = " 中止しました。";
        }
        catch (Exception ex)
        {
            // Any unexpected GUI-side exception must never silently kill the run:
            // surface it in the log, the status line, and a persistent crash file.
            var detail = ex.ToString();
            statusLabel.Text = $" エラー:{SecretMasker.Mask(ex.Message)}";
            try
            {
                Directory.CreateDirectory(outputBox.Text);
                await File.WriteAllTextAsync(
                    Path.Combine(outputBox.Text, "gui-crash.log"), detail, cts.Token).ConfigureAwait(true);
            }
            catch
            {
                // best effort
            }

            MessageBox.Show(this, SecretMasker.Mask(detail), "予期しないエラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                statusLabel.Text = $" 完了:{result.Files.Count} 個のファイルを保存しました。";
                if (autoClose)
                {
                    BeginInvoke(new Action(Close));
                }

                break;

            case WorldDownloadOutcome.NotImplemented:
                statusLabel.Text = " 停止:既定境界(未実装)。";
                MessageBox.Show(this, result.FailureMessage, "既定では未実装", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;

            case WorldDownloadOutcome.RequiresNonPublicAccess:
                statusLabel.Text = " 停止:cluster がゲストフローを拒否しました。";
                MessageBox.Show(
                    this,
                    "ゲストフローは cluster に拒否されました(理由はログにあります)。\n" +
                    "このツールは、cluster に拒否されたアクセスを迂回しません。\n\n" +
                    SecretMasker.Mask(result.Delivery?.Reason ?? result.FailureMessage ?? string.Empty),
                    "ゲストフロー拒否",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Stop);
                break;

            case WorldDownloadOutcome.NotPublic:
                statusLabel.Text = " 停止:非公開ワールド。";
                MessageBox.Show(this, result.FailureMessage, "非公開ワールド", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;

            case WorldDownloadOutcome.Usage:
                statusLabel.Text = " 停止:入力エラー。";
                MessageBox.Show(this, result.FailureMessage, "入力エラー", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;

            default:
                statusLabel.Text = " エラー。";
                MessageBox.Show(this, result.FailureMessage, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
