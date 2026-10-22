using Microsoft.Web.WebView2.Core;

namespace ClusterMapDownloader.Gui;

/// <summary>
/// Non-modal in-app 3D viewer: renders a converted GLB scene model with
/// three.js (MIT, imported from a CDN by the bundled viewer page) inside a
/// WebView2 host. Local files are exposed through per-window virtual hosts
/// only (<c>cmdviewer.local</c> for the page, <c>cmdmodels.local</c> for the
/// folder that contains the model); nothing outside those folders is served.
/// WASD + Space fly controls live in the page (see viewer/viewer.js).
/// </summary>
public sealed class ViewerForm : Form
{
    private const string ViewerHost = "cmdviewer.local";
    private const string ModelsHost = "cmdmodels.local";

    private readonly string modelPath;
    private readonly string viewerRoot;
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 webView;
    private readonly ToolStripStatusLabel statusItem = new()
    {
        Spring = true,
        TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        Text = "Initialising viewer...",
    };

    public ViewerForm(string modelPath, string viewerRoot, string ownerTitle)
    {
        this.modelPath = Path.GetFullPath(modelPath);
        this.viewerRoot = Path.GetFullPath(viewerRoot);

        Text = $"3D Viewer - {Path.GetFileName(this.modelPath)} - {ownerTitle}";
        MinimumSize = new Size(720, 520);
        Size = new Size(1200, 800);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        webView = new Microsoft.Web.WebView2.WinForms.WebView2
        {
            Dock = DockStyle.Fill,
            TabIndex = 0,
        };
        webView.CoreWebView2InitializationCompleted += OnWebViewReady;

        var statusStrip = new StatusStrip { SizingGrip = false };
        statusStrip.Items.Add(statusItem);

        Controls.Add(webView);
        Controls.Add(statusStrip);
        Load += async (_, _) => await InitializeAsync();
    }

    /// <summary>
    /// True when the installed WebView2 Runtime version can be resolved.
    /// </summary>
    public static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task InitializeAsync()
    {
        if (!IsWebView2RuntimeAvailable())
        {
            ShowFatal(
                "The WebView2 Runtime is required for the in-app 3D viewer but is not installed.\n\n" +
                "Install it once from https://developer.microsoft.com/microsoft-edge/webview2/ " +
                "and open the viewer again.\n(The model file itself is untouched.)");
            return;
        }

        try
        {
            await webView.EnsureCoreWebView2Async().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowFatal("Could not start the WebView2 host.\n\n" + ex.Message);
        }
    }

    private void OnWebViewReady(object? sender, EventArgs e)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            ShowFatal("The WebView2 host did not initialise.");
            return;
        }

        // Minimal hardening for a local personal-use viewer page.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.AreHostObjectsAllowed = false;

        core.WebMessageReceived += OnWebMessage;

        // Serve the bundled three.js page and the model's folder over
        // dedicated virtual hosts (local disk only, cross-origin fetch of the
        // GLB allowed by the Allow resource kind).
        core.SetVirtualHostNameToFolderMapping(
            ViewerHost, viewerRoot, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping(
            ModelsHost, Path.GetDirectoryName(modelPath) ?? ".", CoreWebView2HostResourceAccessKind.Allow);

        AssertViewerPageExists();
        var name = $"{Path.GetFileName(Path.GetDirectoryName(modelPath))}\\{Path.GetFileName(modelPath)}";
        core.Navigate(
            $"https://{ViewerHost}/index.html?model={Uri.EscapeDataString(Path.GetFileName(modelPath))}" +
            $"&name={Uri.EscapeDataString(name)}");
    }

    private void AssertViewerPageExists()
    {
        if (!File.Exists(Path.Combine(viewerRoot, "index.html")))
        {
            ShowFatal(
                "The bundled viewer page was not found at:\n" + Path.Combine(viewerRoot, "index.html") +
                "\n\nRe-publish the GUI (bootstrap.ps1) or run it from the repository layout.");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement))
            {
                return;
            }

            var kind = kindElement.GetString();
            var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
            switch (kind)
            {
                case "status":
                    statusItem.Text = text ?? string.Empty;
                    break;
                case "loaded":
                    statusItem.Text = text;
                    Text = $"3D Viewer - {Path.GetFileName(modelPath)} - loaded";
                    break;
                case "error":
                    statusItem.Text = text;
                    statusItem.ForeColor = System.Drawing.Color.IndianRed;
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages: the page keeps reporting status.
        }
    }

    private void ShowFatal(string message)
    {
        statusItem.Text = "Viewer failed.";
        MessageBox.Show(this, message, "Viewer unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
