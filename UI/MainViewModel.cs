using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using NetDivert.Core;
using NetDivert.Native;
using NetDivert.Service;

namespace NetDivert.UI
{
    public enum AppTab
    {
        All,
        Offline,
        Inactive,
        Favorites,
        Hidden,
        Online,
        ActiveRules,
        Tunnel,
    }

    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private readonly UiSettings _settings = UiSettings.Current;
        private readonly RedirectSettings _redirect = RedirectStore.Load();
        private readonly RedirectController _redirector = new();
        private SettingsWindow? _settingsWindow;
        private readonly Dictionary<string, AppRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ConnectionRowViewModel> _connRows = new();
        private readonly HashSet<AppRowViewModel> _visible = new();
        private readonly HashSet<AppRowViewModel> _cooling = new();
        private readonly HashSet<string> _favorites;
        private readonly HashSet<string> _hidden;
        private readonly HashSet<string> _dismissed;
        private readonly HashSet<string> _pinned;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ListCollectionView _appsView;
        private readonly ListCollectionView _listenerView;
        private readonly ListCollectionView _coolingView;

        private IEngineHost? _host;
        private bool _busy;
        private bool _running;
        private bool _hasBaseline;
        private long _prevIn;
        private long _prevOut;
        private long _prevPackets;
        private AppRule _global = new();
        private AppRowViewModel? _selectedApp;
        private string _searchText = "";
        private string _statusText = "Iniciando…";
        private string _totalDown = "";
        private string _totalUp = "";
        private string _statsText = "";
        private string _graphTitle = "Tráfego total";
        private string _trayText = Brand.Product;
        private AppTab _tab;
        private int _nAll, _nOffline, _nInactive, _nClearable, _nFavorites, _nHidden, _nOnline, _nActive, _nTunnel;
        private long _lastScan;
        private int _scanning;

        public MainViewModel()
        {
            Format.Unit = _settings.Unit;
            _favorites = new HashSet<string>(_settings.Favorites, StringComparer.OrdinalIgnoreCase);
            _hidden = new HashSet<string>(_settings.Hidden, StringComparer.OrdinalIgnoreCase);
            _dismissed = new HashSet<string>(_settings.Dismissed, StringComparer.OrdinalIgnoreCase);
            _pinned = new HashSet<string>(_settings.Pinned, StringComparer.OrdinalIgnoreCase);
            Loc.Changed += () => OnPropertyChanged(string.Empty);
            _tab = Enum.IsDefined(typeof(AppTab), _settings.AppTab) ? (AppTab)_settings.AppTab : AppTab.All;

            _appsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Apps);
            _appsView.Filter = o => o is AppRowViewModel r && _visible.Contains(r);
            _appsView.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.Name), ListSortDirection.Ascending));
            _listenerView = (ListCollectionView)CollectionViewSource.GetDefaultView(Listeners);
            _listenerView.Filter = MatchListener;
            _coolingView = new ListCollectionView(Apps)
            {
                Filter = o => o is AppRowViewModel r && _cooling.Contains(r),
            };
            _coolingView.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.Name), ListSortDirection.Ascending));

            StartStopCommand = new RelayCommand(_ => ToggleEngine(), _ => _host != null && !_busy);
            ClearRuleCommand = new RelayCommand(p =>
            {
                if (p is not AppRowViewModel row)
                    return;
                row.ClearRule();
                if (_settings.AskedOnline.Remove(row.Key))
                    _settings.Save();
            });
            OpenFolderCommand = new RelayCommand(p =>
            {
                if (p is AppRowViewModel { ExePath: { } path })
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
            });
            CopyPathCommand = new RelayCommand(p =>
            {
                if (p is AppRowViewModel r)
                    System.Windows.Clipboard.SetText(r.Path);
            });
            InstallServiceCommand = new RelayCommand(_ => _ = InstallServiceAsync(), _ => !_busy);
            UninstallServiceCommand = new RelayCommand(_ => _ = UninstallServiceAsync(), _ => !_busy && ServiceManager.IsInstalled);
            ToggleFavoriteCommand = new RelayCommand(p => { if (p is AppRowViewModel r) Toggle(_favorites, r); });
            ToggleHiddenCommand = new RelayCommand(p => { if (p is AppRowViewModel r) Toggle(_hidden, r); });
            ClearInactiveCommand = new RelayCommand(_ => ClearInactive(), _ => _nClearable > 0);
            SettingsCommand = new RelayCommand(_ => OpenSettings());
            ContactCommand = new RelayCommand(_ => new ContactWindow { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog());
            AdapterRulesCommand = new RelayCommand(p => EditAdapterRules(p as AppRowViewModel));
            TogglePinCommand = new RelayCommand(p => { if (p is AppRowViewModel r) Toggle(_pinned, r); });
            AllowOutCommand = new RelayCommand(p => (p as AppRowViewModel)?.ToggleAllow(true));
            AllowInCommand = new RelayCommand(p => (p as AppRowViewModel)?.ToggleAllow(false));
            FirewallCommand = new RelayCommand(p => ReplicateFirewall(p as AppRowViewModel));
            CloseConnectionsCommand = new RelayCommand(p => CloseConnections(p as AppRowViewModel));
            ForceStopCommand = new RelayCommand(p => ForceStop(p as AppRowViewModel));

            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), App.Current.Dispatcher);
            foreach (var pattern in _redirect.Patterns)
                RedirectPatterns.Add(pattern);
            if (_redirect.StartWithApp && _redirect.Patterns.Count > 0)
            {
                try
                {
                    _redirector.Start(_redirect);
                    _redirect.Running = true;
                    SaveRedirect();
                }
                catch (Exception ex)
                {
                    _redirectError = ex.Message;
                    _redirect.Running = false;
                    SaveRedirect();
                }
            }
        }

        public ObservableCollection<AppRowViewModel> Apps { get; } = new();
        public ObservableCollection<ConnectionRowViewModel> Connections { get; } = new();
        public ListCollectionView CoolingApps => _coolingView;
        public bool ShowCoolingList => _tab == AppTab.Online;

        public History TotalDownHistory { get; } = new(120);
        public History TotalUpHistory { get; } = new(120);

        public ICommand StartStopCommand { get; }
        public ICommand ClearRuleCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand CopyPathCommand { get; }
        public ICommand InstallServiceCommand { get; }
        public ICommand UninstallServiceCommand { get; }
        public ICommand ToggleFavoriteCommand { get; }
        public ICommand ToggleHiddenCommand { get; }
        public ICommand ClearInactiveCommand { get; }
        public ICommand SettingsCommand { get; }
        public ICommand ContactCommand { get; }
        public ICommand AdapterRulesCommand { get; }
        public ICommand TogglePinCommand { get; }
        public ICommand AllowOutCommand { get; }
        public ICommand AllowInCommand { get; }
        public ICommand FirewallCommand { get; }
        public ICommand CloseConnectionsCommand { get; }
        public ICommand ForceStopCommand { get; }

        public ObservableCollection<ListenerSnapshot> Listeners { get; } = new();

        /// <summary>Raised after every refresh so the view can redraw the graph.</summary>
        public event Action? Refreshed;

        /// <summary>Raised when the display unit changes so the view can update column headers.</summary>
        public event Action? UnitChanged;

        public bool IsRunning => _running;
        public string StartStopText => _running ? Loc.Get("stop") : Loc.Get("start");
        public string WindowTitle => Loc.Get("window.title");
        public bool IsServiceMode => _host?.IsService == true;

        public string ModeText => _host == null ? "" : IsServiceMode ? Loc.Get("mode.service") : Loc.Get("mode.local");
        public string ModeTipKey => IsServiceMode ? "mode.service.tip" : "mode.local.tip";
        public string ModeHelpKey => IsServiceMode ? "mode.service.help" : "mode.local.help";

        public string ServiceStatusText => ServiceManager.GetState() switch
        {
            ServiceState.NotInstalled => Loc.Get("svc.missing"),
            ServiceState.Running => Loc.Get("svc.running"),
            ServiceState.Starting => Loc.Get("svc.starting"),
            ServiceState.Stopping => Loc.Get("svc.stopping"),
            _ => Loc.Get("svc.stopped"),
        };

        /// <summary>The service can be started/stopped outside the app (services.msc), so status is re-read on demand.</summary>
        public void RefreshServiceStatus()
        {
            OnPropertyChanged(nameof(ServiceStatusText));
            CommandManager.InvalidateRequerySuggested();
        }

        public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
        public string TotalDown { get => _totalDown; private set => Set(ref _totalDown, value); }
        public string TotalUp { get => _totalUp; private set => Set(ref _totalUp, value); }
        public string StatsText { get => _statsText; private set => Set(ref _statsText, value); }
        public string GraphTitle { get => _graphTitle; private set => Set(ref _graphTitle, value); }
        public string TrayText { get => _trayText; private set => Set(ref _trayText, value); }
        public bool AppsLoading => _appsLoading;
        public bool ConnectionsLoading => _connectionsLoading;

        public AppRowViewModel? SelectedApp
        {
            get => _selectedApp;
            set
            {
                if (!Set(ref _selectedApp, value))
                    return;
                GraphTitle = value == null ? Loc.Get("graph.total") : Loc.Get("graph.of") + value.Name;
                if (value != null && FollowsConnections)
                {
                    Connections.Clear();
                    _connRows.Clear();
                    _selectGen++;
                    SetFlag(ref _connectionsLoading, true, nameof(ConnectionsLoading));
                }
                else if (Connections.Count > 0 || _connectionsLoading)
                {
                    Connections.Clear();
                    _connRows.Clear();
                    SetFlag(ref _connectionsLoading, false, nameof(ConnectionsLoading));
                }
                if (value == null)
                    ShowAllListeners();
                Refreshed?.Invoke();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value))
                    QueueSearch();
            }
        }

        public bool TabIsAll { get => _tab == AppTab.All; set { if (value) SetTab(AppTab.All); } }
        public bool TabIsOffline { get => _tab == AppTab.Offline; set { if (value) SetTab(AppTab.Offline); } }
        public bool TabIsInactive { get => _tab == AppTab.Inactive; set { if (value) SetTab(AppTab.Inactive); } }
        public bool TabIsFavorites { get => _tab == AppTab.Favorites; set { if (value) SetTab(AppTab.Favorites); } }
        public bool TabIsHidden { get => _tab == AppTab.Hidden; set { if (value) SetTab(AppTab.Hidden); } }
        public bool TabIsOnline { get => _tab == AppTab.Online; set { if (value) SetTab(AppTab.Online); } }
        public bool TabIsActiveRules { get => _tab == AppTab.ActiveRules; set { if (value) SetTab(AppTab.ActiveRules); } }
        public bool TabIsTunnel { get => _tab == AppTab.Tunnel; set { if (value) SetTab(AppTab.Tunnel); } }

        public string TabAllText => $"{Loc.Get("tab.all")} ({_nAll})";
        public string TabOfflineText => $"{Loc.Get("tab.offline")} ({_nOffline})";
        public string TabInactiveText => $"{Loc.Get("tab.inactive")} ({_nInactive})";
        public string TabFavoritesText => $"{Loc.Get("tab.favorites")} ({_nFavorites})";
        public string TabHiddenText => $"{Loc.Get("tab.hidden")} ({_nHidden})";
        public string TabOnlineText => $"{Loc.Get("tab.online")} ({_nOnline})";
        public string TabActiveText => $"{Loc.Get("tab.active")} ({_nActive})";
        public string TabTunnelText => $"{Loc.Get("tab.tunnel")} ({_nTunnel})";
        public string ClearInactiveText => _nClearable == 0 ? Loc.Get("clear.inactive") : $"{Loc.Get("clear.inactive")} ({_nClearable})";

        private void SetTab(AppTab tab)
        {
            if (_tab == tab)
                return;
            _tab = tab;
            _settings.AppTab = (int)tab;
            _settings.Save();
            OnPropertyChanged(nameof(TabIsAll));
            OnPropertyChanged(nameof(TabIsOffline));
            OnPropertyChanged(nameof(TabIsInactive));
            OnPropertyChanged(nameof(TabIsFavorites));
            OnPropertyChanged(nameof(TabIsHidden));
            OnPropertyChanged(nameof(TabIsOnline));
            OnPropertyChanged(nameof(TabIsActiveRules));
            OnPropertyChanged(nameof(TabIsTunnel));
            OnPropertyChanged(nameof(ShowCoolingList));
            if (!FollowsConnections)
            {
                if (Connections.Count > 0)
                {
                    Connections.Clear();
                    _connRows.Clear();
                }
                SetFlag(ref _connectionsLoading, false, nameof(ConnectionsLoading));
            }
            if (_selectedApp == null || !InTab(_selectedApp, tab))
                ShowAllListeners();
            QueueFilter();
        }

        private void Toggle(HashSet<string> set, AppRowViewModel row)
        {
            if (!set.Add(row.Key))
                set.Remove(row.Key);
            SaveLists();
            RefreshFilter(force: true);
        }

        private void ClearInactive()
        {
            foreach (var row in Apps)
            {
                if (CanClear(row))
                    _dismissed.Add(row.Key);
            }
            SaveLists();
            RefreshFilter(force: true);
        }

        private bool FollowsConnections => _tab is not AppTab.Offline and not AppTab.Inactive;

        private bool CanClear(AppRowViewModel row) =>
            !_hidden.Contains(row.Key) && !_pinned.Contains(row.Key) && (row.IsInactive || IsMissingFile(row));

        private void SaveLists()
        {
            _settings.Favorites = _favorites.ToList();
            _settings.Hidden = _hidden.ToList();
            _settings.Dismissed = _dismissed.ToList();
            _settings.Pinned = _pinned.ToList();
            _settings.Save();
        }

        private static void ReplicateFirewall(AppRowViewModel? row)
        {
            if (row == null)
                return;
            var result = WindowsFirewall.ApplyApp(row.Name, row.ExePath, row.BlockIn, row.BlockOut);
            System.Windows.MessageBox.Show(FirewallMessage.FormatApp(result), Loc.Get("fw.title"),
                System.Windows.MessageBoxButton.OK,
                result.Error == null ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }

        private void EditAdapterRules(AppRowViewModel? row)
        {
            if (row == null)
                return;
            var draft = row.RuleClone();
            var window = new AdapterRulesWindow(draft) { Owner = System.Windows.Application.Current.MainWindow };
            if (window.ShowDialog() == true)
                row.ReplaceRule(draft);
        }


        public bool TrayHintShown
        {
            get => _settings.TrayHintShown;
            set
            {
                _settings.TrayHintShown = value;
                _settings.Save();
            }
        }

        // ------------------------------------------------------------------ units

        public string[] UnitOptions { get; } = { "Automático", "KB/s", "MB/s" };

        public int UnitIndex
        {
            get => (int)_settings.Unit;
            set
            {
                if ((int)_settings.Unit == value || value < 0)
                    return;
                _settings.Unit = (RateUnit)value;
                _settings.Save();
                Format.Unit = _settings.Unit;
                foreach (var row in Apps)
                    row.RefreshUnits();
                OnPropertyChanged(string.Empty);
                UnitChanged?.Invoke();
                Refreshed?.Invoke();
            }
        }

        public string LimitUnitLabel => Format.LimitUnitLabel;

        // ------------------------------------------------------------------ global limits

        public bool GlobalDownEnabled { get => _global.LimitDownEnabled; set => ChangeGlobal(() => _global.LimitDownEnabled = value); }
        public double GlobalDownValue { get => Format.KBpsToLimitUnit(_global.LimitDownKBps); set => ChangeGlobal(() => _global.LimitDownKBps = Format.LimitUnitToKBps(value)); }
        public bool GlobalUpEnabled { get => _global.LimitUpEnabled; set => ChangeGlobal(() => _global.LimitUpEnabled = value); }
        public double GlobalUpValue { get => Format.KBpsToLimitUnit(_global.LimitUpKBps); set => ChangeGlobal(() => _global.LimitUpKBps = Format.LimitUnitToKBps(value)); }

        private void ChangeGlobal(Action mutate)
        {
            mutate();
            Call(h => h.SetGlobal(_global.Clone()));
            NotifyGlobal();
        }

        private void NotifyGlobal()
        {
            OnPropertyChanged(nameof(GlobalDownEnabled));
            OnPropertyChanged(nameof(GlobalDownValue));
            OnPropertyChanged(nameof(GlobalUpEnabled));
            OnPropertyChanged(nameof(GlobalUpValue));
        }

        private void PushRule(AppRule rule) => Call(h => h.SetRule(rule));

        private void Call(Action<IEngineHost> action)
        {
            if (_host == null)
                return;
            try
            {
                action(_host);
            }
            catch (Exception ex)
            {
                StatusText = "Erro: " + ex.Message;
            }
        }

        // ------------------------------------------------------------------ host / engine lifecycle

        /// <summary>Uses the Windows service when installed; otherwise runs the engine in this process.</summary>
        public async Task InitializeAsync()
        {
            _busy = true;
            try
            {
                var state = ServiceManager.GetState();
                if (state != ServiceState.NotInstalled)
                {
                    StatusText = "Conectando ao serviço…";
                    var remote = await Task.Run(() =>
                    {
                        if (state != ServiceState.Running)
                            try { ServiceManager.StartService(); } catch { }
                        return ConnectToService(TimeSpan.FromSeconds(10));
                    });
                    if (remote != null)
                    {
                        SetHost(remote);
                        StatusText = "Conectado ao serviço";
                        return;
                    }
                    StatusText = "Serviço instalado mas não respondeu — usando o modo local";
                }
                var local = new LocalEngineHost();
                SetHost(local);
                StartLocal(local);
            }
            finally
            {
                _busy = false;
                _timer.Start();
                Tick();
            }
        }

        private static RemoteEngineHost? ConnectToService(TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (RemoteEngineHost.TryConnect(out var host))
                    return host;
                System.Threading.Thread.Sleep(500);
            }
            return null;
        }

        private void StartLocal(LocalEngineHost local)
        {
            try
            {
                if (!PolicyEngine.Current.Policy.FiltersOnStart)
                {
                    StatusText = Loc.Get("status.manual");
                    return;
                }
                local.Start();
                StatusText = Loc.Get("status.running");
            }
            catch (Exception ex)
            {
                StatusText = "Falha ao iniciar: " + ex.Message;
                System.Windows.MessageBox.Show(ex.Message, Brand.Product, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void SetHost(IEngineHost? host)
        {
            _host?.Dispose();
            _host = host;

            // Counters restart with a new host: drop rows so rates don't go negative.
            SelectedApp = null;
            Apps.Clear();
            _rows.Clear();
            _visible.Clear();
            _hasBaseline = false;
            OnPropertyChanged(nameof(IsServiceMode));
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(ModeTipKey));
            OnPropertyChanged(nameof(ModeHelpKey));
            OnPropertyChanged(nameof(ServiceStatusText));
        }

        public void ToggleEngine()
        {
            if (_host == null)
                return;
            try
            {
                if (_running)
                {
                    _host.Stop();
                    StatusText = Loc.Get("status.stopped");
                }
                else
                {
                    _host.Start();
                    StatusText = Loc.Get("status.running");
                }
            }
            catch (Exception ex)
            {
                StatusText = "Falha: " + ex.Message;
                System.Windows.MessageBox.Show(ex.Message, Brand.Product, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            Tick();
        }

        private async Task InstallServiceAsync()
        {
            var answer = System.Windows.MessageBox.Show(
                Brand.Product + " será copiado para:\n" + ServiceManager.InstallDir +
                "\n\ne registrado como serviço do Windows (conta SYSTEM, início automático). " +
                "As regras passam a valer desde o boot, antes de qualquer usuário fazer logon.\n\nContinuar?",
                "Instalar serviço", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.OK)
                return;

            _busy = true;
            StatusText = "Instalando o serviço…";
            SetHost(null); // stop the in-process engine: only one engine may divert traffic at a time
            try
            {
                var remote = await Task.Run(() =>
                {
                    ServiceManager.Install();
                    return ConnectToService(TimeSpan.FromSeconds(15));
                });
                if (remote == null)
                    throw new InvalidOperationException("o serviço foi instalado mas não respondeu");
                SetHost(remote);
                StatusText = "Serviço instalado e em execução";
            }
            catch (Exception ex)
            {
                StatusText = "Falha ao instalar o serviço: " + ex.Message;
                System.Windows.MessageBox.Show(ex.Message, "Instalar serviço", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                if (_host == null)
                {
                    var local = new LocalEngineHost();
                    SetHost(local);
                    StartLocal(local);
                }
            }
            finally
            {
                _busy = false;
                OnPropertyChanged(nameof(ServiceStatusText));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task UninstallServiceAsync()
        {
            var answer = System.Windows.MessageBox.Show(
                "Remover o serviço do Windows? O programa volta ao modo local (as regras só valem com ele aberto).",
                "Remover serviço", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.OK)
                return;

            _busy = true;
            StatusText = "Removendo o serviço…";
            SetHost(null);
            try
            {
                await Task.Run(ServiceManager.Uninstall);
                StatusText = "Serviço removido";
            }
            catch (Exception ex)
            {
                StatusText = "Falha ao remover o serviço: " + ex.Message;
            }
            finally
            {
                var local = new LocalEngineHost();
                SetHost(local);
                StartLocal(local);
                _busy = false;
                OnPropertyChanged(nameof(ServiceStatusText));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        // ------------------------------------------------------------------ periodic refresh

        private void Tick()
        {
            var host = _host;
            if (host == null || Interlocked.Exchange(ref _snapBusy, 1) == 1)
                return;
            string? key = FollowsConnections ? _selectedApp?.Key : null;
            int selectGen = _selectGen;
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Interlocked.Exchange(ref _snapBusy, 0);
                return;
            }
            Task.Run(() =>
            {
                EngineSnapshot? snap = null;
                string? error = null;
                try
                {
                    snap = host.GetSnapshot(key);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                if (dispatcher.HasShutdownStarted)
                {
                    Interlocked.Exchange(ref _snapBusy, 0);
                    return;
                }
                dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    try
                    {
                        if (!ReferenceEquals(_host, host))
                            return;
                        if (error != null)
                        {
                            StatusText = IsServiceMode ? "Serviço indisponível: " + error : "Erro: " + error;
                            return;
                        }
                        if (snap != null)
                            ApplySnapshot(snap, key, selectGen);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _snapBusy, 0);
                    }
                });
            });
        }

        private void ApplySnapshot(EngineSnapshot snap, string? requestedKey, int selectGen)
        {
            if (_host == null)
                return;

            double seconds = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
            _clock.Restart();

            if (_running != snap.Running)
            {
                _running = snap.Running;
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(StartStopText));
            }
            if (snap.Error != null)
                StatusText = "Erro: " + snap.Error;

            if (!snap.Global.SameAs(_global))
            {
                _global = snap.Global.Clone();
                NotifyGlobal();
            }

            foreach (var a in snap.Apps)
            {
                if (_rows.TryGetValue(a.Key, out var row))
                {
                    row.Update(a, seconds);
                }
                else
                {
                    row = new AppRowViewModel(a, PushRule);
                    _rows[a.Key] = row;
                    Apps.Add(row);
                }
            }

            double down = 0, up = 0, pps = 0;
            if (_hasBaseline)
            {
                down = Math.Max(0, snap.TotalIn - _prevIn) / seconds;
                up = Math.Max(0, snap.TotalOut - _prevOut) / seconds;
                pps = Math.Max(0, snap.Packets - _prevPackets) / seconds;
            }
            _hasBaseline = true;
            _prevIn = snap.TotalIn;
            _prevOut = snap.TotalOut;
            _prevPackets = snap.Packets;

            TotalDownHistory.Push(down);
            TotalUpHistory.Push(up);
            string d = Format.Rate(down) is { Length: > 0 } ds ? ds : "0 B/s";
            string u = Format.Rate(up) is { Length: > 0 } us ? us : "0 B/s";
            TotalDown = "↓ " + d;
            TotalUp = "↑ " + u;
            TrayText = _running ? $"{Brand.Product}  ↓ {d}  ↑ {u}" : $"{Brand.Product}";
            GraphTitle = _selectedApp == null ? Loc.Get("graph.total") : Loc.Get("graph.of") + _selectedApp.Name;
            if (snap.Listeners != null)
                RefreshListeners(snap.Listeners);
            StatsText = $"{pps:0} pacotes/s   •   bloqueados: {snap.Blocked:N0}   •   descartados pelo limitador: {snap.Dropped:N0}   •   em fila: {Format.Bytes(snap.Queued)}   •   total ↓ {Format.Bytes(snap.TotalIn)}  ↑ {Format.Bytes(snap.TotalOut)}";

            RestoreDismissedOnActivity();
            MaybeAskOnline();
            ScheduleProcessScan();
            RefreshFilter(force: false);
            OnPropertyChanged(nameof(RedirectStatus));
            OnPropertyChanged(nameof(RedirectButtonText));
            if (_redirectSeenRunning != _redirector.IsRunning)
            {
                _redirectSeenRunning = _redirector.IsRunning;
                OnPropertyChanged(nameof(RedirectIsRunning));
            }
            if (string.Equals(requestedKey, _selectedApp?.Key, StringComparison.OrdinalIgnoreCase))
            {
                RefreshConnections(snap.Connections, seconds);
                if (selectGen == _selectGen)
                    SetFlag(ref _connectionsLoading, false, nameof(ConnectionsLoading));
            }
            Refreshed?.Invoke();
            WriteDiagnostics(snap);
            ScheduleExistsScan();
        }

        private int _snapBusy;
        private int _selectGen;
        private int _filterTicket;
        private int _existsBusy;
        private long _existsAt;
        private long _searchAt;
        private bool _searchQueued;
        private bool _appsLoading;
        private bool _connectionsLoading;
        private bool _listenFiltered;
        private int[] _listenPids = Array.Empty<int>();
        private string? _listenPath;
        private readonly Dictionary<string, bool> _fileExists = new(StringComparer.OrdinalIgnoreCase);
        private SecurityPolicy _netPolicy = PolicyStore.Load();
        private long _netPolicyAt;
        private bool _asking;

        private void SetFlag(ref bool field, bool value, string property)
        {
            if (field == value)
                return;
            field = value;
            OnPropertyChanged(property);
        }

        private void QueueFilter()
        {
            int ticket = ++_filterTicket;
            SetFlag(ref _appsLoading, true, nameof(AppsLoading));
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null)
                return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (ticket != _filterTicket)
                    return;
                RefreshFilter(force: true);
                SetFlag(ref _appsLoading, false, nameof(AppsLoading));
            });
        }

        private void QueueSearch()
        {
            _searchAt = Environment.TickCount64;
            if (_searchQueued)
                return;
            _searchQueued = true;
            App.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyQueuedSearch));
        }

        private void ApplyQueuedSearch()
        {
            if (Environment.TickCount64 - _searchAt < 160)
            {
                App.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyQueuedSearch));
                return;
            }
            _searchQueued = false;
            RefreshFilter(force: true);
        }

        private void ScheduleExistsScan()
        {
            long now = Environment.TickCount64;
            if (now - _existsAt < 20000 || Interlocked.Exchange(ref _existsBusy, 1) == 1)
                return;
            _existsAt = now;
            var paths = new List<string>();
            foreach (var row in Apps)
            {
                if (!string.IsNullOrEmpty(row.ExePath))
                    paths.Add(row.ExePath!);
            }
            Task.Run(() =>
            {
                var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    if (map.ContainsKey(path))
                        continue;
                    try { map[path] = File.Exists(path); }
                    catch { map[path] = true; }
                }
                var dispatcher = App.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted)
                {
                    Interlocked.Exchange(ref _existsBusy, 0);
                    return;
                }
                dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _fileExists.Clear();
                    foreach (var kv in map)
                        _fileExists[kv.Key] = kv.Value;
                    Interlocked.Exchange(ref _existsBusy, 0);
                    RefreshFilter(force: false);
                });
            });
        }

        private void MaybeAskOnline()
        {
            long now = Environment.TickCount64;
            if (now - _netPolicyAt > 2000)
            {
                _netPolicyAt = now;
                try { _netPolicy = PolicyStore.Load(); } catch { }
            }
            if (_asking || _netPolicy.NetMode != 2)
                return;
            var row = Apps.FirstOrDefault(a => a.SeenByNetwork && !a.DecidedOnline && !_settings.AskedOnline.Contains(a.Key)
                && !a.BlockIn && !a.BlockOut && a.IdleMs < 4000 && !a.Key.StartsWith('?'));
            if (row == null)
                return;
            _asking = true;
            var target = row;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    bool? allow = AskOnline(target.Name);
                    if (allow.HasValue)
                    {
                        target.DecideOnline(allow.Value);
                        if (!_settings.AskedOnline.Contains(target.Key))
                            _settings.AskedOnline.Add(target.Key);
                        _settings.Save();
                    }
                }
                finally
                {
                    _asking = false;
                }
            }));
        }

        private static bool? AskOnline(string name)
        {
            bool? allow = null;
            var yes = new System.Windows.Controls.Button { Content = Loc.Get("ask.allow"), Width = 110, Margin = new System.Windows.Thickness(0, 14, 8, 0), IsDefault = true };
            var no = new System.Windows.Controls.Button { Content = Loc.Get("ask.deny"), Width = 110, Margin = new System.Windows.Thickness(0, 14, 0, 0) };
            var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = string.Format(Loc.Get("ask.online"), name),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["FgBrush"],
            });
            panel.Children.Add(buttons);
            var dialog = new System.Windows.Window
            {
                Title = Loc.Get("ask.title"),
                Width = 460,
                SizeToContent = System.Windows.SizeToContent.Height,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Topmost = true,
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PanelBrush"],
                Content = panel,
            };
            yes.Click += (_, _) => { allow = true; dialog.DialogResult = true; };
            no.Click += (_, _) => { allow = false; dialog.DialogResult = true; };
            dialog.ShowDialog();
            return allow;
        }

        private string? HighlightFor(AppRowViewModel row, bool pinned)
        {
            if (IsMissingFile(row))
                return _settings.ColorMissing;
            string? path = row.ExePath;
            if (row.HasRule)
                return _settings.ColorSpecial;
            if ((path != null && path.Contains("wsl", StringComparison.OrdinalIgnoreCase)) || row.Name.Contains("wsl", StringComparison.OrdinalIgnoreCase))
                return _settings.ColorWsl;
            if (pinned)
                return _settings.ColorPinned;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (path != null && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                return _settings.ColorSystem;
            if (row.Signed && PolicyEngine.Current.Policy.VerifySignature)
                return _settings.ColorSigned;
            if (row.ConnectionCount > 0 || row.DownRate > 0 || row.UpRate > 0)
                return _settings.ColorConnected;
            return null;
        }

        private bool IsMissingFile(AppRowViewModel row)
        {
            if (row.BriefExit)
                return true;
            string? path = row.ExePath;
            return path != null && _fileExists.TryGetValue(path, out bool exists) && !exists;
        }

        private bool InTab(AppRowViewModel row, AppTab tab)
        {
            bool hidden = _hidden.Contains(row.Key);
            bool favorite = _favorites.Contains(row.Key);
            bool dismissed = _dismissed.Contains(row.Key);
            return tab switch
            {
                AppTab.All => !hidden && !dismissed && (row.SeenByNetwork || row.IsOffline || favorite),
                AppTab.Offline => !hidden && row.IsOffline,
                AppTab.Inactive => !hidden && !dismissed && row.IsInactive,
                AppTab.Favorites => !hidden && favorite,
                AppTab.Hidden => hidden,
                AppTab.Online => !hidden && !dismissed && row.IsOnline,
                AppTab.ActiveRules => !hidden && (row.IsActiveRule || RedirectMatches(row)),
                AppTab.Tunnel => !hidden && (row.IsTunnel || RedirectMatches(row)),
                _ => false,
            };
        }

        private void RefreshFilter(bool force)
        {
            bool changed = force;
            bool coolChanged = force;
            string search = _searchText.Trim();
            int all = 0, offline = 0, inactive = 0, clearable = 0, favorites = 0, hidden = 0, online = 0, active = 0, tunnel = 0;
            foreach (var row in Apps)
            {
                bool isHidden = _hidden.Contains(row.Key);
                bool isFavorite = _favorites.Contains(row.Key);
                bool isPinned = _pinned.Contains(row.Key);
                row.SetListFlags(isFavorite, isHidden, isPinned);
                row.ApplyHighlight(HighlightFor(row, isPinned));
                if (InTab(row, AppTab.All)) all++;
                if (InTab(row, AppTab.Offline)) offline++;
                if (InTab(row, AppTab.Inactive)) inactive++;
                if (CanClear(row)) clearable++;
                if (InTab(row, AppTab.Favorites)) favorites++;
                if (InTab(row, AppTab.Hidden)) hidden++;
                if (InTab(row, AppTab.Online)) online++;
                if (InTab(row, AppTab.ActiveRules)) active++;
                if (InTab(row, AppTab.Tunnel)) tunnel++;

                bool matches = search.Length == 0
                    || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || row.Path.Contains(search, StringComparison.OrdinalIgnoreCase);
                bool inThisTab = InTab(row, _tab);
                bool cooling = _tab == AppTab.Online && row.IsCooling && inThisTab && matches;
                bool show = inThisTab && matches && !cooling;
                changed |= show ? _visible.Add(row) : _visible.Remove(row);
                coolChanged |= cooling ? _cooling.Add(row) : _cooling.Remove(row);
            }
            if (changed)
                _appsView.Refresh();
            if (coolChanged)
                _coolingView.Refresh();
            SetCount(ref _nAll, all, nameof(TabAllText));
            SetCount(ref _nOffline, offline, nameof(TabOfflineText));
            if (SetCount(ref _nInactive, inactive, nameof(TabInactiveText)))
                OnPropertyChanged(nameof(ClearInactiveText));
            if (SetCount(ref _nClearable, clearable, nameof(ClearInactiveText)))
                CommandManager.InvalidateRequerySuggested();
            SetCount(ref _nFavorites, favorites, nameof(TabFavoritesText));
            SetCount(ref _nHidden, hidden, nameof(TabHiddenText));
            SetCount(ref _nOnline, online, nameof(TabOnlineText));
            SetCount(ref _nActive, active, nameof(TabActiveText));
            SetCount(ref _nTunnel, tunnel, nameof(TabTunnelText));
        }

        private bool SetCount(ref int field, int value, string property)
        {
            if (field == value)
                return false;
            field = value;
            OnPropertyChanged(property);
            return true;
        }

        private void RestoreDismissedOnActivity()
        {
            bool restored = false;
            foreach (var row in Apps)
            {
                if (_dismissed.Contains(row.Key) && row.SeenByNetwork && row.IdleMs < 3000 && !row.HeldDismissed)
                {
                    _dismissed.Remove(row.Key);
                    restored = true;
                }
            }
            if (restored)
                SaveLists();
        }

        private void ScheduleProcessScan()
        {
            long now = Environment.TickCount64;
            if (now - _lastScan < 4000 || Interlocked.Exchange(ref _scanning, 1) == 1)
                return;
            _lastScan = now;
            Task.Run(() =>
            {
                RunningProcess[] found;
                try { found = ScanProcesses(); }
                catch { found = Array.Empty<RunningProcess>(); }
                var dispatcher = App.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted)
                {
                    Interlocked.Exchange(ref _scanning, 0);
                    return;
                }
                dispatcher.BeginInvoke(() =>
                {
                    try { MergeProcesses(found); }
                    finally { Interlocked.Exchange(ref _scanning, 0); }
                });
            });
        }

        private readonly record struct RunningProcess(string Key, string Name, string? Path, int[] Pids, bool Signed, string Hash);

        private static readonly Dictionary<string, string> ExeNames = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (bool Signed, string Hash)> FactCache = new(StringComparer.OrdinalIgnoreCase);

        private static (bool Signed, string Hash) FileFacts(string? path, bool signature, bool hash)
        {
            if (path == null || (!signature && !hash))
                return (false, "");
            string key = path + "|" + signature + hash;
            lock (FactCache)
            {
                if (FactCache.TryGetValue(key, out var cached))
                    return cached;
            }
            bool signed = false;
            string digest = "";
            if (signature)
            {
                try
                {
                    System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
                    signed = true;
                }
                catch { }
            }
            if (hash)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length is > 0 and < 80L * 1024 * 1024)
                    {
                        using var stream = File.OpenRead(path);
                        digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    }
                }
                catch { }
            }
            lock (FactCache)
                FactCache[key] = (signed, digest);
            return (signed, digest);
        }

        private static RunningProcess[] ScanProcesses()
        {
            var groups = new Dictionary<string, (string Name, string? Path, List<int> Pids)>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id <= 0)
                        continue;
                    string? path = Win32.GetProcessPath(process.Id);
                    string key = path ?? "?name:" + process.ProcessName;
                    string name = path != null ? DescribeExecutable(path) : process.ProcessName;
                    if (!groups.TryGetValue(key, out var group))
                        group = (name, path, new List<int>());
                    group.Pids.Add(process.Id);
                    groups[key] = group;
                }
                catch
                {
                    // The process can exit between enumeration and the query.
                }
                finally
                {
                    process.Dispose();
                }
            }
            var policy = PolicyEngine.Current.Policy;
            return groups.Select(kv =>
            {
                var facts = FileFacts(kv.Value.Path, policy.VerifySignature, policy.ComputeHash);
                return new RunningProcess(kv.Key, kv.Value.Name, kv.Value.Path, kv.Value.Pids.ToArray(), facts.Signed, facts.Hash);
            }).ToArray();
        }

        private static string DescribeExecutable(string path)
        {
            if (ExeNames.TryGetValue(path, out var cached))
                return cached;
            string name;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = string.IsNullOrWhiteSpace(info.FileDescription)
                    ? Path.GetFileNameWithoutExtension(path)
                    : info.FileDescription.Trim();
            }
            catch
            {
                name = Path.GetFileNameWithoutExtension(path);
            }
            ExeNames[path] = name;
            return name;
        }

        private void MergeProcesses(RunningProcess[] found)
        {
            if (_host == null)
                return;
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in found)
            {
                running.Add(item.Key);
                if (_rows.TryGetValue(item.Key, out var row))
                {
                    row.UpdateRunning(item.Pids);
                    row.SetFileFacts(item.Signed, item.Hash);
                    continue;
                }
                var snap = new AppSnapshot
                {
                    Key = item.Key,
                    Name = item.Name,
                    Path = item.Path,
                    Pids = item.Pids,
                    IdleMs = long.MaxValue,
                    Rule = new AppRule { Key = item.Key },
                };
                row = new AppRowViewModel(snap, PushRule, seenByNetwork: false);
                row.SetFileFacts(item.Signed, item.Hash);
                _rows[item.Key] = row;
                Apps.Add(row);
            }

            foreach (var row in Apps.ToList())
            {
                bool keep = row.SeenByNetwork || running.Contains(row.Key)
                    || _favorites.Contains(row.Key) || _hidden.Contains(row.Key);
                if (keep)
                    continue;
                if (SelectedApp == row)
                    SelectedApp = null;
                Apps.Remove(row);
                _rows.Remove(row.Key);
                _visible.Remove(row);
            }
            var runningKeys = running;
            foreach (var row in Apps)
                row.NoteProcess(runningKeys.Contains(row.Key));
            RefreshFilter(force: true);
        }

        private void RefreshListeners(List<ListenerSnapshot> found)
        {
            var ids = new HashSet<string>(found.Select(l => l.Id));
            foreach (var row in Listeners.Where(l => !ids.Contains(l.Id)).ToList())
                Listeners.Remove(row);
            var have = new HashSet<string>(Listeners.Select(l => l.Id));
            foreach (var item in found)
                if (have.Add(item.Id))
                    Listeners.Add(item);
        }

        public void ShowListeners(AppRowViewModel row)
        {
            if (_listenFiltered && SameListeners(row))
                return;
            _listenPids = (int[])row.ProcessIds.Clone();
            _listenPath = row.ExePath;
            _listenFiltered = true;
            _listenerView.Refresh();
        }

        public void ShowAllListeners()
        {
            if (!_listenFiltered)
                return;
            _listenFiltered = false;
            _listenPids = Array.Empty<int>();
            _listenPath = null;
            _listenerView.Refresh();
        }

        private bool SameListeners(AppRowViewModel row)
        {
            if (!string.Equals(_listenPath, row.ExePath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (_listenPids.Length != row.ProcessIds.Length)
                return false;
            for (int i = 0; i < _listenPids.Length; i++)
                if (_listenPids[i] != row.ProcessIds[i])
                    return false;
            return true;
        }

        private bool MatchListener(object item)
        {
            if (!_listenFiltered || item is not ListenerSnapshot listener)
                return true;
            if (!string.IsNullOrEmpty(_listenPath) && string.Equals(listener.Path, _listenPath, StringComparison.OrdinalIgnoreCase))
                return true;
            for (int i = 0; i < _listenPids.Length; i++)
                if (_listenPids[i] == listener.Pid)
                    return true;
            return false;
        }

        private void RefreshConnections(List<ConnSnapshot> conns, double seconds)
        {
            if (_selectedApp == null)
                return;
            var ids = new HashSet<string>(conns.Select(c => c.Id));
            foreach (var row in Connections.Where(r => !ids.Contains(r.Id)).ToList())
            {
                Connections.Remove(row);
                _connRows.Remove(row.Id);
            }
            foreach (var c in conns)
            {
                if (_connRows.TryGetValue(c.Id, out var row))
                {
                    row.Update(c, seconds);
                }
                else
                {
                    row = new ConnectionRowViewModel(c);
                    _connRows[c.Id] = row;
                    Connections.Add(row);
                    row.Update(c, seconds);
                }
            }
        }

        private static readonly string? LogPath = Environment.GetCommandLineArgs().Contains("--log")
            ? System.IO.Path.Combine(Brand.UserDir, "engine.log")
            : null;

        private void WriteDiagnostics(EngineSnapshot snap)
        {
            if (LogPath == null || !snap.Running)
                return;
            var sb = new System.Text.StringBuilder();
            sb.Append($"{DateTime.Now:HH:mm:ss} mode={(IsServiceMode ? "service" : "local")} pkts={snap.Packets} dropped={snap.Dropped} queued={snap.Queued}\n");
            foreach (var r in Apps.Where(r => r.DownRate > 0 || r.UpRate > 0 || r.HasRule))
                sb.Append($"  {r.Name} [{r.PidsText}] down={r.DownRate / 1024:0.0}KB/s up={r.UpRate / 1024:0.0}KB/s discarded={r.Discarded} active={r.IsActive} key={r.Key}\n");
            try { System.IO.File.AppendAllText(LogPath, sb.ToString()); } catch { }
        }

        public ObservableCollection<string> RedirectPatterns { get; } = new();
        public string RedirectIp { get => _redirect.SocksIp; set { _redirect.SocksIp = value; OnPropertyChanged(); SaveRedirect(); } }
        public int RedirectPort { get => _redirect.SocksPort; set { _redirect.SocksPort = value; OnPropertyChanged(); SaveRedirect(); } }
        public string RedirectProtocol { get => _redirect.Protocol; set { _redirect.Protocol = value; OnPropertyChanged(); SaveRedirect(); } }
        public bool RedirectChrome { get => _redirect.ChromeAssist; set { _redirect.ChromeAssist = value; OnPropertyChanged(); SaveRedirect(); } }
        public bool RedirectLog { get => _redirect.LogToFile; set { _redirect.LogToFile = value; OnPropertyChanged(); SaveRedirect(); } }
        public bool RedirectAutoStart { get => _redirect.StartWithApp; set { _redirect.StartWithApp = value; OnPropertyChanged(); SaveRedirect(); } }
        public bool RedirectIsRunning => _redirector.IsRunning;
        public string RedirectStatus => _redirectError ?? _redirector.Error ?? (_redirector.IsRunning ? Loc.Get("redir.on") : Loc.Get("redir.off"));
        public string RedirectButtonText => _redirector.IsRunning ? Loc.Get("redir.stop") : Loc.Get("redir.start");

        public void AddRedirectPattern(string pattern)
        {
            pattern = pattern.Trim();
            if (pattern.Length == 0 || RedirectPatterns.Contains(pattern))
                return;
            RedirectPatterns.Add(pattern);
            SaveRedirect();
            RefreshFilter(force: true);
        }

        public void RemoveRedirectPattern(string? pattern)
        {
            if (pattern == null)
                return;
            RedirectPatterns.Remove(pattern);
            SaveRedirect();
            RefreshFilter(force: true);
        }

        private string? _redirectError;
        private bool _redirectSeenRunning;

        public void ToggleRedirect()
        {
            _redirectError = null;
            if (_redirector.IsRunning)
            {
                _redirector.Stop();
                _redirect.Running = false;
            }
            else
            {
                try
                {
                    _redirector.Start(_redirect);
                    _redirect.Running = true;
                }
                catch (Exception ex)
                {
                    _redirectError = ex.Message;
                    _redirect.Running = false;
                }
            }
            SaveRedirect();
            OnPropertyChanged(nameof(RedirectStatus));
            OnPropertyChanged(nameof(RedirectButtonText));
            OnPropertyChanged(nameof(RedirectIsRunning));
        }

        public void StopRedirect()
        {
            _redirector.Stop();
            if (_redirect.Running)
            {
                _redirect.Running = false;
                SaveRedirect();
            }
            OnPropertyChanged(nameof(RedirectIsRunning));
        }

        private void SaveRedirect()
        {
            _redirect.Patterns = RedirectPatterns.ToList();
            try { RedirectStore.Save(_redirect); } catch { }
        }

        private bool RedirectMatches(AppRowViewModel row) =>
            _redirect.Patterns.Count > 0 && DipWd.Wildcard.MatchesAny(row.Path, _redirect.Patterns);

        private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "registry", "idle", "smss", "csrss", "wininit", "services", "lsass", "winlogon",
            "secure system", "memory compression",
        };

        private void CloseConnections(AppRowViewModel? row)
        {
            if (row == null)
                return;
            if (row.ProcessIds.Length == 0)
            {
                StatusText = string.Format(Loc.Get("menu.conns.none"), row.Name);
                return;
            }
            if (System.Windows.MessageBox.Show(string.Format(Loc.Get("menu.conns.ask"), row.Name), Loc.Get("app"),
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
                return;
            var pids = (int[])row.ProcessIds.Clone();
            string name = row.Name;
            Task.Run(() =>
            {
                int closed = TcpCloser.Close(pids);
                var dispatcher = App.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted)
                    return;
                dispatcher.BeginInvoke(() =>
                {
                    StatusText = closed == 0
                        ? string.Format(Loc.Get("menu.conns.none"), name)
                        : string.Format(Loc.Get("menu.conns.done"), closed, name);
                });
            });
        }

        private void ForceStop(AppRowViewModel? row)
        {
            if (row == null)
                return;
            if (row.ProcessIds.Length == 0)
            {
                StatusText = string.Format(Loc.Get("menu.kill.none"), row.Name);
                return;
            }
            if (IsProtectedProcess(row.Name) || IsProtectedProcess(row.ExePath))
            {
                System.Windows.MessageBox.Show(string.Format(Loc.Get("menu.kill.protected"), row.Name), Loc.Get("app"),
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            if (System.Windows.MessageBox.Show(string.Format(Loc.Get("menu.kill.ask"), row.Name), Loc.Get("app"),
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
                return;
            Win32.TryEnableDebugPrivilege();
            int stopped = 0, failed = 0;
            foreach (int pid in row.ProcessIds)
            {
                if (pid <= 4 || pid == Environment.ProcessId)
                {
                    failed++;
                    continue;
                }
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (IsProtectedProcess(process.ProcessName))
                    {
                        failed++;
                        continue;
                    }
                    process.Kill(entireProcessTree: true);
                    stopped++;
                }
                catch
                {
                    if (TerminatePid(pid))
                        stopped++;
                    else
                        failed++;
                }
            }
            StatusText = failed == 0
                ? string.Format(Loc.Get("menu.kill.done"), row.Name)
                : string.Format(Loc.Get("menu.kill.partial"), row.Name, stopped, failed);
            if (stopped > 0)
            {
                row.HoldDismissed();
                _dismissed.Add(row.Key);
                if (SelectedApp == row)
                    SelectedApp = null;
                SaveLists();
                RefreshFilter(force: true);
            }
        }

        private static bool IsProtectedProcess(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string file = System.IO.Path.GetFileNameWithoutExtension(name);
            return ProtectedProcesses.Contains(name) || ProtectedProcesses.Contains(file);
        }

        private static bool TerminatePid(int pid)
        {
            IntPtr handle = Win32.OpenProcess(Win32.PROCESS_TERMINATE, false, pid);
            if (handle == IntPtr.Zero)
                return false;
            try
            {
                return Win32.TerminateProcess(handle, 1);
            }
            finally
            {
                Win32.CloseHandle(handle);
            }
        }

        public string ExportBackupJson()
        {
            var rules = new RuleStore();
            rules.Load();
            return SettingsBackup.Export(_settings, PolicyStore.Load(), rules.Data, _redirect);
        }

        public void ImportBackup(string json)
        {
            var bundle = SettingsBackup.Import(json);
            if (bundle.Ui != null)
            {
                _settings.CopyFrom(bundle.Ui);
                _settings.Save();
                Format.Unit = _settings.Unit;
                Loc.SetLanguage(_settings.Language);
                Theme.Apply(_settings.Theme);
                ReplaceSet(_favorites, _settings.Favorites);
                ReplaceSet(_hidden, _settings.Hidden);
                ReplaceSet(_dismissed, _settings.Dismissed);
                ReplaceSet(_pinned, _settings.Pinned);
                RefreshFilter(force: true);
                OnPropertyChanged(string.Empty);
                UnitChanged?.Invoke();
            }
            if (bundle.Policy != null)
                PolicyStore.Save(bundle.Policy);
            if (bundle.Rules != null)
                ReplaceRules(bundle.Rules);
            if (bundle.Redirect != null)
                ReplaceRedirect(bundle.Redirect);
        }

        private static void ReplaceSet(HashSet<string> target, List<string>? values)
        {
            target.Clear();
            if (values == null)
                return;
            foreach (string value in values)
                target.Add(value);
        }

        private void ReplaceRules(RuleSet incoming)
        {
            if (_host == null)
                return;
            var current = new RuleStore();
            current.Load();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in incoming.Apps)
                keep.Add(rule.Key);
            foreach (var old in current.Data.Apps)
            {
                if (!keep.Contains(old.Key))
                    _host.SetRule(new AppRule { Key = old.Key });
            }
            foreach (var rule in incoming.Apps)
                _host.SetRule(rule);
            _host.SetGlobal(incoming.Global ?? new AppRule { Key = "*" });
        }

        private void ReplaceRedirect(RedirectSettings incoming)
        {
            bool wasRunning = _redirector.IsRunning;
            if (wasRunning)
                _redirector.Stop();
            _redirect.SocksIp = string.IsNullOrWhiteSpace(incoming.SocksIp) ? _redirect.SocksIp : incoming.SocksIp;
            _redirect.SocksPort = incoming.SocksPort > 0 ? incoming.SocksPort : _redirect.SocksPort;
            _redirect.Protocol = string.IsNullOrWhiteSpace(incoming.Protocol) ? _redirect.Protocol : incoming.Protocol;
            _redirect.ChromeAssist = incoming.ChromeAssist;
            _redirect.LogToFile = incoming.LogToFile;
            _redirect.StartWithApp = incoming.StartWithApp;
            RedirectPatterns.Clear();
            if (incoming.Patterns != null)
            {
                foreach (string pattern in incoming.Patterns)
                    if (!string.IsNullOrWhiteSpace(pattern))
                        RedirectPatterns.Add(pattern.Trim());
            }
            _redirect.Running = false;
            SaveRedirect();
            OnPropertyChanged(nameof(RedirectIp));
            OnPropertyChanged(nameof(RedirectPort));
            OnPropertyChanged(nameof(RedirectProtocol));
            OnPropertyChanged(nameof(RedirectChrome));
            OnPropertyChanged(nameof(RedirectLog));
            OnPropertyChanged(nameof(RedirectAutoStart));
            if (wasRunning && RedirectPatterns.Count > 0)
            {
                try { _redirector.Start(_redirect); _redirect.Running = true; }
                catch (Exception ex) { _redirectError = ex.Message; }
                SaveRedirect();
            }
            OnPropertyChanged(nameof(RedirectStatus));
            OnPropertyChanged(nameof(RedirectButtonText));
            OnPropertyChanged(nameof(RedirectIsRunning));
            RefreshFilter(force: true);
        }

        private void OpenSettings()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow { Owner = System.Windows.Application.Current.MainWindow };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        public void Dispose()
        {
            _timer.Stop();
            try
            {
                _redirector.Dispose();
                if (_redirect.Running)
                {
                    _redirect.Running = false;
                    SaveRedirect();
                }
            }
            catch { }
            try { _host?.Dispose(); }
            catch (Exception) { }
            _host = null;
        }
    }
}
