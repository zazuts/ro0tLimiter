using System;
using System.Net;
using NetDivert.Core;

namespace NetDivert.UI
{
    public sealed class ConnectionRowViewModel : ObservableObject
    {
        private readonly IPAddress? _remoteIp;
        private ConnSnapshot _snap;
        private long _prevIn;
        private long _prevOut;
        private double _downRate;
        private double _upRate;
        private string _remoteHost = "";

        public ConnectionRowViewModel(ConnSnapshot snap)
        {
            _snap = snap;
            IPAddress.TryParse(snap.RemoteIp, out _remoteIp);
            Protocol = PacketParser.ProtocolName(snap.Protocol);
            Remote = Endpoint(snap.RemoteIp, snap.RemotePort);
            _prevIn = snap.BytesIn;
            _prevOut = snap.BytesOut;
        }

        public string Id => _snap.Id;
        public string Protocol { get; }
        public string Local => Endpoint(_snap.LocalIp, _snap.LocalPort);
        public string Remote { get; }
        public string Started => _snap.Started.ToString("HH:mm:ss");
        public string State => _snap.State;

        public string RemoteHost { get => _remoteHost; private set => Set(ref _remoteHost, value); }

        public double DownRate { get => _downRate; private set { if (Set(ref _downRate, value)) OnPropertyChanged(nameof(DownRateText)); } }
        public double UpRate { get => _upRate; private set { if (Set(ref _upRate, value)) OnPropertyChanged(nameof(UpRateText)); } }
        public string DownRateText => Format.Rate(DownRate);
        public string UpRateText => Format.Rate(UpRate);

        public long TotalIn => _snap.BytesIn;
        public long TotalOut => _snap.BytesOut;
        public string TotalInText => Format.Bytes(TotalIn);
        public string TotalOutText => Format.Bytes(TotalOut);

        private static string Endpoint(string ip, ushort port) => ip.Contains(':') ? $"[{ip}]:{port}" : $"{ip}:{port}";

        public void Update(ConnSnapshot snap, double seconds)
        {
            _snap = snap;
            DownRate = Math.Max(0, snap.BytesIn - _prevIn) / seconds;
            UpRate = Math.Max(0, snap.BytesOut - _prevOut) / seconds;
            _prevIn = snap.BytesIn;
            _prevOut = snap.BytesOut;
            OnPropertyChanged(nameof(TotalIn));
            OnPropertyChanged(nameof(TotalInText));
            OnPropertyChanged(nameof(TotalOut));
            OnPropertyChanged(nameof(TotalOutText));
            OnPropertyChanged(nameof(Local));
            OnPropertyChanged(nameof(State));

            if (RemoteHost.Length == 0 && _remoteIp != null && PolicyEngine.Current.Policy.ResolveAddresses)
                RemoteHost = DnsCache.Get(_remoteIp) ?? "";
        }
    }
}
