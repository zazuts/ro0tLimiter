using System;
using System.Windows.Media;
using NetDivert.Core;

namespace NetDivert.UI
{
    public sealed class AppRowViewModel : ObservableObject
    {
        private static readonly string[] RuleProperties =
        {
            nameof(LimitDownEnabled), nameof(LimitDownValue), nameof(LimitUpEnabled), nameof(LimitUpValue),
            nameof(BlockIn), nameof(BlockOut), nameof(HasRule), nameof(DecidedOnline),
            nameof(IsActiveRule), nameof(IsTunnel),
        };

        private readonly Action<AppRule> _pushRule;
        private AppSnapshot _snap;
        private AppRule _rule;
        private long _prevIn;
        private long _prevOut;
        private double _downRate;
        private double _upRate;
        private ImageSource? _icon;

        public AppRowViewModel(AppSnapshot snap, Action<AppRule> pushRule, bool seenByNetwork = true)
        {
            _snap = snap;
            _rule = snap.Rule.Clone();
            _pushRule = pushRule;
            SeenByNetwork = seenByNetwork;
            _prevIn = snap.BytesIn;
            _prevOut = snap.BytesOut;
        }

        /// <summary>True once the engine has seen traffic or a connection for this executable.</summary>
        public bool SeenByNetwork { get; private set; }

        /// <summary>Running, and it has never used the network.</summary>
        public bool IsOffline => !SeenByNetwork && _snap.Pids.Length > 0;

        /// <summary>Used the network, then stayed quiet for 30 seconds. It leaves the Online tab.</summary>
        public bool IsInactive => SeenByNetwork && _band == Band.Away;

        /// <summary>On the Online tab: either active now, or quiet for less than 30 seconds.</summary>
        public bool IsOnline => SeenByNetwork && _band != Band.Away;

        /// <summary>Quiet for at least 5 seconds and less than 30. Shown on the lower Online list.</summary>
        public bool IsCooling => _band == Band.Lower;

        /// <summary>Started, touched the network, and exited within a few seconds.</summary>
        public bool BriefExit => _briefExit;

        /// <summary>Hidden after a forced stop until the process is gone and starts again.</summary>
        public bool HeldDismissed => _held;

        public bool IsTunnel => _rule.Adapters.Exists(a => a.Exclusive);

        public bool EngineRuled { get; private set; }

        public bool IsActiveRule => EngineRuled || HasRule;

        public bool IsFavorite { get; private set; }
        public bool IsHidden { get; private set; }
        public bool IsPinned { get; private set; }
        public string FavoriteMenuText => IsFavorite ? Loc.Get("menu.fav.remove") : Loc.Get("menu.fav.add");
        public string HiddenMenuText => IsHidden ? Loc.Get("menu.show") : Loc.Get("menu.hide");
        public string PinMenuText => IsPinned ? Loc.Get("menu.unpin") : Loc.Get("menu.pin");

        public void SetListFlags(bool favorite, bool hidden, bool pinned)
        {
            if (IsFavorite == favorite && IsHidden == hidden && IsPinned == pinned)
                return;
            IsFavorite = favorite;
            IsHidden = hidden;
            IsPinned = pinned;
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(IsHidden));
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(FavoriteMenuText));
            OnPropertyChanged(nameof(HiddenMenuText));
            OnPropertyChanged(nameof(PinMenuText));
        }

        public AppRule RuleClone() => _rule.Clone();

        public void ReplaceRule(AppRule rule)
        {
            _rule.CopyFrom(rule);
            _pushRule(_rule.Clone());
            NotifyRule();
        }

        public void ToggleAllow(bool outbound) => Change(() =>
        {
            if (outbound)
                _rule.AllowOut = !_rule.AllowOut;
            else
                _rule.AllowIn = !_rule.AllowIn;
        });

        /// <summary>Refreshes the PID list of an app the engine has not seen yet.</summary>
        public void UpdateRunning(int[] pids)
        {
            if (SeenByNetwork || string.Join(",", _snap.Pids) == string.Join(",", pids))
                return;
            _snap.Pids = pids;
            OnPropertyChanged(nameof(PidsText));
        }

        public string Key => _snap.Key;
        public string Name => _snap.Name;
        public string? ExePath => _snap.Path;
        public string Path => _snap.Path ?? _snap.Key;
        public ImageSource? Icon => _icon ??= IconCache.Get(_snap.Path);

        public History DownHistory { get; } = new(120);
        public History UpHistory { get; } = new(120);

        public string AdaptersText => _snap.Adapters;
        public string Tip => string.IsNullOrEmpty(HashText) ? Path : Path + "\nSHA-256 " + HashText;
        public bool Signed { get; private set; }
        public string HashText { get; private set; } = "";
        public bool HasHighlight => HighlightBrush != null;
        public System.Windows.Media.Brush? HighlightBrush { get; private set; }
        private string? _highlightHex;

        public int[] ProcessIds => _snap.Pids;
        public string PidsText => string.Join(", ", _snap.Pids);
        public int ConnectionCount => _snap.Connections;
        public long IdleMs => _snap.IdleMs;

        public double DownRate { get => _downRate; private set { if (Set(ref _downRate, value)) OnPropertyChanged(nameof(DownRateText)); } }
        public double UpRate { get => _upRate; private set { if (Set(ref _upRate, value)) OnPropertyChanged(nameof(UpRateText)); } }
        public string DownRateText => Format.Rate(DownRate);
        public string UpRateText => Format.Rate(UpRate);

        public long TotalIn => _snap.BytesIn;
        public long TotalOut => _snap.BytesOut;
        public string TotalInText => Format.Bytes(TotalIn);
        public string TotalOutText => Format.Bytes(TotalOut);

        public long Discarded => _snap.Discarded;
        public string DiscardedText => Discarded == 0 ? "" : Format.Bytes(Discarded);

        /// <summary>Active = traffic (delivered or discarded) in the last 30 s, or at least one open connection.</summary>
        public bool IsActive => _snap.IdleMs < 30_000 || _snap.Connections > 0;

        public bool HasRule => !_rule.IsDefault;

        public bool LimitDownEnabled
        {
            get => _rule.LimitDownEnabled;
            set => Change(() => _rule.LimitDownEnabled = value);
        }

        public double LimitDownValue
        {
            get => Format.KBpsToLimitUnit(_rule.LimitDownKBps);
            set => Change(() => _rule.LimitDownKBps = Format.LimitUnitToKBps(value));
        }

        public bool LimitUpEnabled
        {
            get => _rule.LimitUpEnabled;
            set => Change(() => _rule.LimitUpEnabled = value);
        }

        public double LimitUpValue
        {
            get => Format.KBpsToLimitUnit(_rule.LimitUpKBps);
            set => Change(() => _rule.LimitUpKBps = Format.LimitUnitToKBps(value));
        }

        public bool BlockIn
        {
            get => _rule.BlockIn;
            set => Change(() => _rule.BlockIn = value);
        }

        public bool BlockOut
        {
            get => _rule.BlockOut;
            set => Change(() => _rule.BlockOut = value);
        }

        private void Change(Action mutate)
        {
            mutate();
            _pushRule(_rule.Clone());
            NotifyRule();
        }

        private void NotifyRule()
        {
            foreach (string p in RuleProperties)
                OnPropertyChanged(p);
        }

        public void SetFileFacts(bool signed, string hash)
        {
            if (Signed == signed && HashText == hash)
                return;
            Signed = signed;
            HashText = hash;
            OnPropertyChanged(nameof(Tip));
        }

        public void ApplyHighlight(string? hex)
        {
            if (_highlightHex == hex)
                return;
            _highlightHex = hex;
            HighlightBrush = hex == null ? null : Theme.Brush(hex);
            OnPropertyChanged(nameof(HighlightBrush));
            OnPropertyChanged(nameof(HasHighlight));
        }

        public void ClearRule() => Change(() =>
        {
            _rule.BlockIn = _rule.BlockOut = false;
            _rule.LimitDownEnabled = _rule.LimitUpEnabled = false;
            _rule.AllowOut = _rule.AllowIn = false;
            _rule.DecidedOnline = false;
            _rule.Adapters.Clear();
        });

        public bool DecidedOnline => _rule.DecidedOnline;

        public void DecideOnline(bool allow) => Change(() =>
        {
            _rule.DecidedOnline = true;
            if (!allow)
                _rule.BlockIn = _rule.BlockOut = true;
        });

        public void RefreshUnits()
        {
            OnPropertyChanged(nameof(DownRateText));
            OnPropertyChanged(nameof(UpRateText));
            OnPropertyChanged(nameof(LimitDownValue));
            OnPropertyChanged(nameof(LimitUpValue));
        }

        public void Update(AppSnapshot snap, double seconds)
        {
            var old = _snap;
            _snap = snap;
            if (!SeenByNetwork)
            {
                // First time the engine reports this app: skip the fake rate spike from its backlog.
                SeenByNetwork = true;
                _networkAt = Environment.TickCount64;
                _prevIn = snap.BytesIn;
                _prevOut = snap.BytesOut;
            }

            DownRate = Math.Max(0, snap.BytesIn - _prevIn) / seconds;
            UpRate = Math.Max(0, snap.BytesOut - _prevOut) / seconds;
            _prevIn = snap.BytesIn;
            _prevOut = snap.BytesOut;
            DownHistory.Push(DownRate);
            UpHistory.Push(UpRate);

            if (old.BytesIn != snap.BytesIn)
            {
                OnPropertyChanged(nameof(TotalIn));
                OnPropertyChanged(nameof(TotalInText));
            }
            if (old.BytesOut != snap.BytesOut)
            {
                OnPropertyChanged(nameof(TotalOut));
                OnPropertyChanged(nameof(TotalOutText));
            }
            if (old.Discarded != snap.Discarded)
            {
                OnPropertyChanged(nameof(Discarded));
                OnPropertyChanged(nameof(DiscardedText));
            }
            if (old.Connections != snap.Connections)
                OnPropertyChanged(nameof(ConnectionCount));
            if (string.Join(",", old.Pids) != string.Join(",", snap.Pids))
                OnPropertyChanged(nameof(PidsText));
            if (old.Adapters != snap.Adapters)
                OnPropertyChanged(nameof(AdaptersText));
            if (EngineRuled != snap.Ruled)
            {
                EngineRuled = snap.Ruled;
                OnPropertyChanged(nameof(EngineRuled));
                OnPropertyChanged(nameof(IsActiveRule));
            }

            if (!snap.Rule.SameAs(_rule))
            {
                _rule = snap.Rule.Clone();
                NotifyRule();
            }
            TrackOnlineBand();
        }

        private enum Band { Top, Lower, Away }

        private Band _band = Band.Top;
        private long _resumeAt;
        private long _networkAt;
        private bool _briefExit;
        private bool _held;
        private bool _heldSawStop;

        public void HoldDismissed()
        {
            _held = true;
            _heldSawStop = false;
        }

        /// <summary>Called from the process scan, not from the packet path.</summary>
        public void NoteProcess(bool alive)
        {
            if (_held)
            {
                if (!alive)
                    _heldSawStop = true;
                else if (_heldSawStop)
                {
                    _held = false;
                    _heldSawStop = false;
                }
            }
            if (alive)
            {
                _briefExit = false;
                return;
            }
            if (!SeenByNetwork)
                return;
            long now = Environment.TickCount64;
            if (_networkAt == 0)
                _networkAt = now;
            if (now - _networkAt <= 20_000)
                _briefExit = true;
        }

        private void TrackOnlineBand()
        {
            bool flowing = _snap.IdleMs < 1500;
            if (flowing)
            {
                if (_band == Band.Top)
                {
                    _resumeAt = 0;
                    return;
                }
                long now = Environment.TickCount64;
                if (_resumeAt == 0)
                    _resumeAt = now;
                else if (now - _resumeAt >= 5000)
                {
                    _band = Band.Top;
                    _resumeAt = 0;
                }
                return;
            }
            _resumeAt = 0;
            if (_snap.IdleMs >= 30_000)
                _band = Band.Away;
            else if (_snap.IdleMs >= 5000 && _band == Band.Top)
                _band = Band.Lower;
        }
    }
}
