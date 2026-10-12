using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using NetDivert.Service;

namespace NetDivert
{
    public static class Program
    {
        public const string ShowEventName = "ro0tLimiter.UI.Show";

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
            {
                ServiceBase.Run(new NetDivertService());
                return 0;
            }

            bool install = args.Contains("--install-service", StringComparer.OrdinalIgnoreCase);
            if (install || args.Contains("--uninstall-service", StringComparer.OrdinalIgnoreCase))
            {
                string log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ro0tLimiter-service.log");
                try
                {
                    if (install)
                        ServiceManager.Install();
                    else
                        ServiceManager.Uninstall();
                    System.IO.File.WriteAllText(log, $"OK: {ServiceManager.GetState()}");
                    return 0;
                }
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText(log, "ERRO: " + ex);
                    return 1;
                }
            }

            using var mutex = new Mutex(true, "ro0tLimiter.UI.SingleInstance", out bool first);
            if (!first)
            {
                // Another UI is already running (possibly hidden in the tray): ask it to show itself.
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
                    using (show)
                        show.Set();
                return 0;
            }

            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
