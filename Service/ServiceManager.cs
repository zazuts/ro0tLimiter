using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using NetDivert.Core;

namespace NetDivert.Service
{
    public enum ServiceState
    {
        NotInstalled,
        Stopped,
        Starting,
        Running,
        Stopping,
    }

    /// <summary>Installs/removes the NetDivert service. The binaries are copied to Program Files so a
    /// LocalSystem service never runs an executable from a user-writable folder.</summary>
    public static class ServiceManager
    {
        public static string InstallDir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Brand.Product);

        public static ServiceState GetState()
        {
            try
            {
                using var sc = new ServiceController(NetDivertService.Name);
                return sc.Status switch
                {
                    ServiceControllerStatus.Running => ServiceState.Running,
                    ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
                    ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ServiceState.Stopping,
                    _ => ServiceState.Stopped,
                };
            }
            catch (InvalidOperationException)
            {
                return ServiceState.NotInstalled;
            }
        }

        public static bool IsInstalled => GetState() != ServiceState.NotInstalled;

        public static void Install()
        {
            if (IsInstalled)
                StopService();

            RemoveLegacyService();
            CopyBinaries();
            string exe = Path.Combine(InstallDir, Brand.Product + ".exe");

            Sc($"create {NetDivertService.Name} binPath= \"\\\"{exe}\\\" --service\" start= auto depend= BFE DisplayName= \"{Brand.Product}\"",
                allowFailure: IsInstalled);
            Sc($"config {NetDivertService.Name} binPath= \"\\\"{exe}\\\" --service\" start= auto depend= BFE");
            Sc($"description {NetDivertService.Name} \"Aplica os limites de banda e o firewall do {Brand.Product} desde a inicialização do Windows.\"");
            Sc($"failure {NetDivertService.Name} reset= 86400 actions= restart/2000/restart/5000/restart/30000");

            StartService();
        }

        public static void Uninstall()
        {
            int mode = 0;
            try { mode = PolicyStore.Load().UninstallMode; } catch { }
            if (IsInstalled)
            {
                StopService();
                Sc($"delete {NetDivertService.Name}");
            }
            RemoveLegacyService();
            TryDeleteDirectory(InstallDir);
            if (mode == 1)
            {
                TryDeleteDirectory(Brand.DataDir);
                TryDeleteDirectory(Brand.UserDir);
            }
            else
            {
                TryDeleteDirectory(Brand.LogDir);
                TryDeleteDirectory(Brand.ListCacheDir);
            }
        }

        private static void RemoveLegacyService()
        {
            try
            {
                using var sc = new ServiceController(Brand.OldServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch
            {
                // The old service may already be gone.
            }
            Sc($"delete {Brand.OldServiceName}", allowFailure: true);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // A running copy of the program keeps its own folder locked.
            }
        }

        public static void StartService()
        {
            using var sc = new ServiceController(NetDivertService.Name);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
        }

        public static void StopService()
        {
            using var sc = new ServiceController(NetDivertService.Name);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }

        private static void CopyBinaries()
        {
            string source = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase))
                return;

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    continue;
                string dest = Path.Combine(InstallDir, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                try
                {
                    File.Copy(file, dest, overwrite: true);
                }
                catch (IOException) when (File.Exists(dest) && name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
                {
                    // The WinDivert driver stays loaded (and locked) while any handle is open; same version is fine.
                }
            }
        }

        private static void Sc(string args, bool allowFailure = false)
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 && !allowFailure)
                throw new InvalidOperationException($"sc.exe {args.Split(' ').First()} falhou ({p.ExitCode}): {output.Trim()}");
        }
    }
}
