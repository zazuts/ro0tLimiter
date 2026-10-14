using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using DipWd;

namespace NetDivert.Core
{
    public sealed class RedirectSettings
    {
        public string SocksIp { get; set; } = "127.0.0.1";
        public int SocksPort { get; set; } = 1080;
        public string Protocol { get; set; } = "all";
        public bool ChromeAssist { get; set; }
        public bool LogToFile { get; set; }
        public bool StartWithApp { get; set; }
        public bool Running { get; set; }
        public List<string> Patterns { get; set; } = new();
    }

    public static class RedirectStore
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        public static string FilePath => Path.Combine(Brand.DataDir, "redirect.json");

        public static RedirectSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<RedirectSettings>(File.ReadAllText(FilePath)) ?? new RedirectSettings();
            }
            catch
            {
                // Fall back to defaults.
            }
            return new RedirectSettings();
        }

        public static void Save(RedirectSettings settings)
        {
            Directory.CreateDirectory(Brand.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
        }
    }

    /// <summary>Runs the embedded redirector. The limiter keeps the higher WinDivert priority.</summary>
    public sealed class RedirectController : IDisposable
    {
        private CancellationTokenSource? _cancel;
        private Thread? _thread;
        private readonly object _gate = new();

        public bool IsRunning { get; private set; }
        public string? Error { get; private set; }

        public void Start(RedirectSettings settings)
        {
            lock (_gate)
            {
                StopCore();
                if (settings.Patterns.Count == 0)
                    throw new InvalidOperationException("pattern");
                var args = new List<string>
                {
                    "--process", string.Join(";", settings.Patterns),
                    "-ip", settings.SocksIp.Trim(),
                    "-p", settings.SocksPort.ToString(),
                    "--protocol", settings.Protocol,
                };
                if (!settings.ChromeAssist)
                    args.Add("--no-chrome-assist");
                if (settings.LogToFile)
                    args.Add("--log");
                var cli = CliArgs.Parse(args.ToArray());
                var log = new AppLog(verbose: false, settings.LogToFile);
                _cancel = new CancellationTokenSource();
                var token = _cancel.Token;
                Error = null;
                IsRunning = true;
                _thread = new Thread(() =>
                {
                    try
                    {
                        ProxySession.Run(cli, log, token);
                    }
                    catch (Exception ex)
                    {
                        Error = ex.Message;
                    }
                    finally
                    {
                        log.Dispose();
                        IsRunning = false;
                    }
                })
                {
                    IsBackground = true,
                    Name = "ro0tLimiter redirect",
                };
                _thread.Start();
            }
        }

        public void Stop()
        {
            lock (_gate)
                StopCore();
        }

        private void StopCore()
        {
            _cancel?.Cancel();
            _thread?.Join(TimeSpan.FromSeconds(3));
            _cancel?.Dispose();
            _cancel = null;
            _thread = null;
            IsRunning = false;
        }

        public void Dispose() => Stop();
    }
}
