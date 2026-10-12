using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using NetDivert.Core;

namespace NetDivert.Service
{
    /// <summary>Hosts the engine as a LocalSystem service so rules are enforced from boot, before any logon.</summary>
    public sealed class NetDivertService : ServiceBase
    {
        public const string Name = Brand.ServiceName;

        private LocalEngineHost? _host;
        private ControlServer? _server;
        private Thread? _starter;
        private volatile bool _stopping;

        public NetDivertService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            _host = new LocalEngineHost();
            _server = new ControlServer(_host);
            _server.Start();

            // Early in boot the filtering engine may not be ready yet: keep retrying without blocking the SCM.
            _starter = new Thread(() =>
            {
                PolicyEngine.Reload();
                if (!PolicyEngine.Current.Policy.FiltersOnStart)
                    return;
                for (int attempt = 0; !_stopping; attempt++)
                {
                    try
                    {
                        _host.Start();
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 0)
                            EventLog.WriteEntry($"Falha ao iniciar o motor, tentando novamente: {ex.Message}", EventLogEntryType.Warning);
                        Thread.Sleep(2000);
                    }
                }
            })
            { IsBackground = true, Name = "Engine starter" };
            _starter.Start();
        }

        protected override void OnStop() => Shutdown();

        protected override void OnShutdown() => Shutdown();

        private void Shutdown()
        {
            _stopping = true;
            _server?.Dispose();
            _host?.Dispose();
            _server = null;
            _host = null;
        }
    }
}
