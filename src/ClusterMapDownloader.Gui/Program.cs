using ClusterMapDownloader;

namespace ClusterMapDownloader.Gui;

internal static class Program
{
    /// <summary>
    /// GUI entry point with optional automation arguments:
    ///     cluster-map-downloader-gui.exe [&lt;world-url&gt;] [-o &lt;output-dir&gt;] [--auto-close]
    /// A preset URL starts the download automatically right after launch;
    /// --auto-close closes the window when the run finishes (CI/live checks).
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        string? urlArg = null;
        string? outputArg = null;
        var autoClose = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o":
                case "--output":
                    if (i + 1 < args.Length)
                    {
                        outputArg = args[++i];
                    }

                    break;
                case "--auto-close":
                    autoClose = true;
                    break;
                case "-h":
                case "--help":
                    Console.Out.WriteLine("""
                        cluster-map-downloader-gui.exe [url] [-o <dir>] [--auto-close]
                          url          pre-fill the URL box and start immediately
                          -o <dir>     output directory (default .\output)
                          --auto-close exit when the run finishes (automation/testing)
                        """);
                    return;
                default:
                    if (!args[i].StartsWith('-') && urlArg is null)
                    {
                        urlArg = args[i];
                    }

                    break;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(Environment.CurrentDirectory);
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "gui-crash.log"), e.Exception.ToString());
            }
            catch
            {
                // best effort
            }

            MessageBox.Show(
                SecretMasker.Mask(e.Exception.Message) + "\n\n(Details: gui-crash.log)",
                "Unexpected error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };
        Application.Run(new MainForm(urlArg, outputArg, autoClose));
    }
}
