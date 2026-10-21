namespace ClusterMapDownloader;

/// <summary>Simple progress reporting used by the download pipeline.</summary>
public interface IProgressNotifier
{
    /// <summary>Informational/stage message.</summary>
    void Info(string message);

    /// <summary>Successful stage completion.</summary>
    void Success(string message);

    /// <summary>Non-fatal warning.</summary>
    void Warn(string message);

    /// <summary>Fatal/default: ends the run with an error outcome.</summary>
    void Error(string message);
}

/// <summary>Drops all notifications (useful for tests/benchmarks).</summary>
public sealed class NullProgressNotifier : IProgressNotifier
{
    public static NullProgressNotifier Instance { get; } = new();
    public void Info(string message) { }
    public void Success(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}
