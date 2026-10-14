namespace DirectIp.CleanCore;

public static class DipPaths
{
    public static string ProgramDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DirectIp");

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DirectIp");

    public static string ProxyAppsLedger => Path.Combine(ProgramDataDir, "proxy-apps.jsonl");
    public static string CreatedKittySessions => Path.Combine(ProgramDataDir, "created-kitty-sessions.json");
    public static string SettingsJson => Path.Combine(AppDataDir, "settings.json");
    public static string HooksDir => Path.Combine(ProgramDataDir, "hooks");

    public static void EnsureProgramData()
    {
        Directory.CreateDirectory(ProgramDataDir);
    }
}
