namespace DipWd;

sealed class AppLog : IDisposable
{
    readonly object _gate = new();
    readonly StreamWriter? _file;
    readonly bool _verbose;
    public string? FilePath { get; }

    public AppLog(bool verbose, bool toFile)
    {
        _verbose = verbose;
        if (!toFile) return;
        var dir = AppContext.BaseDirectory;
        FilePath = Path.Combine(dir, "dip-wd-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public void Info(string msg)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg;
        lock (_gate)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    public void Verbose(string msg)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg;
        lock (_gate)
        {
            if (_verbose) Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    public void Dispose() => _file?.Dispose();
}
