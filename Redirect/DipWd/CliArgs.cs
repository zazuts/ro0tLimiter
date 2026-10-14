namespace DipWd;

sealed class CliArgs
{
    public bool ListOnly { get; private set; }
    public bool CaptureOnly { get; private set; }
    public string ProcessRaw { get; private set; } = "";
    public string SocksIp { get; private set; } = "";
    public int SocksPort { get; private set; }
    public string Protocol { get; private set; } = "all";
    public bool Verbose { get; private set; }
    public bool Log { get; private set; }
    public bool NoChromeAssist { get; private set; }

    public IReadOnlyList<string> ProcessPatterns => Wildcard.Split(ProcessRaw);

    public static CliArgs Parse(string[] argv)
    {
        var a = new CliArgs();
        if (argv.Length == 0)
        {
            PrintUsage();
            Environment.Exit(1);
        }

        if (argv.Length == 1 && argv[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            a.ListOnly = true;
            return a;
        }

        for (var i = 0; i < argv.Length; i++)
        {
            var t = argv[i];
            string Next()
            {
                if (i + 1 >= argv.Length)
                    throw new ArgumentException("Falta valor depois de " + t);
                return argv[++i];
            }

            if (t.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                a.ListOnly = true;
                continue;
            }

            switch (t.ToLowerInvariant())
            {
                case "--adap":
                case "-a":
                    _ = Next(); // ignored (compat CLI)
                    break;
                case "--process":
                case "--proccess":
                case "-proc":
                    a.ProcessRaw = Next();
                    break;
                case "-ip":
                case "--ip":
                    a.SocksIp = Next();
                    break;
                case "-p":
                case "--port":
                    if (!int.TryParse(Next(), out var port) || port is < 1 or > 65535)
                        throw new ArgumentException("Porta SOCKS invalida.");
                    a.SocksPort = port;
                    break;
                case "--protocol":
                case "-prot":
                    a.Protocol = Next().ToLowerInvariant();
                    if (a.Protocol is not ("tcp" or "udp" or "all"))
                        throw new ArgumentException("--protocol deve ser tcp, udp ou all.");
                    break;
                case "--verbose":
                case "-v":
                    a.Verbose = true;
                    break;
                case "--log":
                    a.Log = true;
                    break;
                case "--no-chrome-assist":
                    a.NoChromeAssist = true;
                    break;
                case "--capture":
                    a.CaptureOnly = true;
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException("Opcao desconhecida: " + t);
            }
        }

        if (a.ListOnly) return a;

        if (a.ProcessPatterns.Count == 0)
            throw new ArgumentException("--process e obrigatorio (ex.: \"l2.*;chrom*.*\").");
        if (string.IsNullOrWhiteSpace(a.SocksIp))
            throw new ArgumentException("-ip e obrigatorio (ex.: 127.0.0.1).");
        if (a.SocksPort == 0)
            throw new ArgumentException("-p e obrigatorio (ex.: 1080).");
        return a;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            dip-wd — proxy estilo Proxifier (WinDivert divert → SOCKS)

            dip-wd.exe list
            dip-wd.exe --process "l2.*;chrom*.*" -ip 127.0.0.1 -p 1080 [-prot all] [-v] [--log]
            dip-wd.exe --process "*" -ip 127.0.0.1 -p 1080 --log
            dip-wd.exe --capture --process "chrom*.*" -ip 127.0.0.1 -p 1080 -v

              --process / -proc      nomes ; wildcards * ?  |  "*" = quase todos (exceto SO/kitty)
              -ip / --ip             SOCKS IP (KiTTY, ex. 127.0.0.1)
              -p / --port            SOCKS porta (ex. 1080)
              --protocol / -prot     tcp | udp | all  (default all; so "udp" dropa UDP)
              --verbose / -v         detalhes no console
              --log                  log completo na pasta do exe
              --capture              so dump WinDivert (sem proxy SOCKS)
              --no-chrome-assist     nao aplica politica SOCKS no Chrome/Edge

            L2/apps: WinDivert forge SYN-ACK → SOCKS.
            Chrome/Edge: politica HKCU SOCKS → KiTTY (reinicie o browser uma vez).
            Requer admin + WinDivert.dll / WinDivert64.sys ao lado do exe.
            """);
    }
}
