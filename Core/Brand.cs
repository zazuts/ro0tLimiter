using System;
using System.IO;

namespace NetDivert.Core
{
    /// <summary>User-visible names and folders. The capture library is an implementation detail.</summary>
    public static class Brand
    {
        public const string Product = "ro0tLimiter";
        public const string ServiceName = "ro0tLimiter";
        public const string OldServiceName = "NetDivert";
        public const string PipeName = "ro0tLimiter.Control";
        public const string ContactEmail = "ro0t@hotmail.it";
        public const string DonationUrl = "https://www.paypal.com/donate/?hosted_button_id=QLPEHGUTPCLXJ";
        public const string RepositoryUrl = "https://github.com/zazuts/ro0tLimiter";

        public static string DataDir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Product);

        public static string UserDir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Product);

        public static string LogDir => Path.Combine(DataDir, "logs");
        public static string ListCacheDir => Path.Combine(DataDir, "lists");

        public static void MigrateLegacy()
        {
            TryCopy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetDivert", "rules.json"),
                Path.Combine(DataDir, "rules.json"));
            TryCopy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetDivert", "rules.json"),
                Path.Combine(DataDir, "rules.json"));
            TryCopy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetDivert", "ui.json"),
                Path.Combine(UserDir, "ui.json"));
        }

        private static void TryCopy(string from, string to)
        {
            try
            {
                if (!File.Exists(from) || File.Exists(to))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
            }
            catch
            {
                // Migration is best effort.
            }
        }
    }
}
