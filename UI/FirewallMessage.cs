using System.Text;
using NetDivert.Core;

namespace NetDivert.UI
{
    public static class FirewallMessage
    {
        public static string FormatApp(FirewallSyncResult result)
        {
            if (result.Error != null)
                return Loc.Get("fw.error") + "\n\n" + result.Error;
            var text = new StringBuilder();
            if (result.MissingPath)
                return Loc.Get("fw.nopath");
            if (result.NothingToBlock)
            {
                text.AppendLine(Loc.Get("fw.none"));
                text.Append(Loc.Get("fw.removed"));
                return text.ToString();
            }
            if (result.OutRules > 0)
                text.AppendLine(Loc.Get("fw.out"));
            if (result.InRules > 0)
                text.AppendLine(Loc.Get("fw.in"));
            text.AppendLine(Loc.Get("fw.removed"));
            text.Append(Loc.Get("fw.kept"));
            return text.ToString().Trim();
        }

        public static string FormatAll(FirewallSyncResult result)
        {
            if (result.Error != null)
                return Loc.Get("fw.error") + "\n\n" + result.Error;
            var text = new StringBuilder();
            text.AppendLine(string.Format(Loc.Get("fw.summary"), result.ProgramRules, result.PortRules, result.AddressRules, result.ExeRules));
            text.AppendLine();
            text.AppendLine(Loc.Get("fw.kept"));
            if (result.SkippedNames.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(Loc.Get("fw.skipExe"));
                foreach (var name in result.SkippedNames)
                    text.AppendLine("• " + name);
            }
            return text.ToString().Trim();
        }
    }
}
