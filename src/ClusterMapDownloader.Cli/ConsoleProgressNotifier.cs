namespace ClusterMapDownloader.Cli;

/// <summary>Console color wrapper around <see cref="IProgressNotifier"/>.</summary>
internal sealed class ConsoleProgressNotifier : IProgressNotifier
{
    public static ConsoleProgressNotifier Instance { get; } = new();

    public void Info(string message) => Write(ConsoleColor.DarkGray, "[i]  ", message);

    public void Success(string message) => Write(ConsoleColor.DarkGreen, "[ok] ", message);

    public void Warn(string message) => Write(ConsoleColor.DarkYellow, "[w]  ", message);

    public void Error(string message) => Write(ConsoleColor.DarkRed, "[!!] ", message);

    private static void Write(ConsoleColor color, string prefix, string message)
    {
        lock (Console.Out)
        {
            var previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Console.Out.Write(prefix);
                Console.ForegroundColor = previous;
            }
            catch
            {
                // Console colors are not available in all hosts; keep prefix only.
            }
            Console.Out.WriteLine(message);
        }
    }
}
