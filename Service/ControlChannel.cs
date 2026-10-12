using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetDivert.Core;

namespace NetDivert.Service
{
    public sealed class ControlRequest
    {
        public string Cmd { get; set; } = "";
        public AppRule? Rule { get; set; }
        public string? ConnectionsFor { get; set; }
    }

    public sealed class ControlResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public EngineSnapshot? Snapshot { get; set; }
    }

    /// <summary>Line-delimited JSON over a named pipe that only Administrators and SYSTEM can open.</summary>
    public sealed class ControlServer : IDisposable
    {
        public const string PipeName = Brand.PipeName;

        private readonly IEngineHost _host;
        private readonly CancellationTokenSource _cts = new();

        public ControlServer(IEngineHost host) => _host = host;

        public void Start() => Task.Run(() => AcceptLoop(_cts.Token));

        private static PipeSecurity CreateSecurity()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            var security = CreateSecurity();
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var connected = pipe;
                    pipe = null;
                    _ = Task.Run(() => Serve(connected, ct));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    pipe?.Dispose();
                }
            }
        }

        private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true })
            {
                try
                {
                    while (!ct.IsCancellationRequested && pipe.IsConnected)
                    {
                        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line == null)
                            break;
                        var response = Handle(line);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Client went away.
                }
            }
        }

        private ControlResponse Handle(string line)
        {
            try
            {
                var req = JsonSerializer.Deserialize<ControlRequest>(line) ?? new ControlRequest();
                switch (req.Cmd)
                {
                    case "snapshot":
                        return new ControlResponse { Ok = true, Snapshot = _host.GetSnapshot(req.ConnectionsFor) };
                    case "setRule" when req.Rule != null:
                        _host.SetRule(req.Rule);
                        break;
                    case "setGlobal" when req.Rule != null:
                        _host.SetGlobal(req.Rule);
                        break;
                    case "start":
                        _host.Start();
                        break;
                    case "stop":
                        _host.Stop();
                        break;
                    default:
                        return new ControlResponse { Error = "comando desconhecido: " + req.Cmd };
                }
                return new ControlResponse { Ok = true };
            }
            catch (Exception ex)
            {
                return new ControlResponse { Error = ex.Message };
            }
        }

        public void Dispose() => _cts.Cancel();
    }

    /// <summary>UI-side proxy for the engine running inside the Windows service.</summary>
    public sealed class RemoteEngineHost : IEngineHost
    {
        private readonly object _lock = new();
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public bool IsService => true;

        public static bool TryConnect(out RemoteEngineHost? host)
        {
            var h = new RemoteEngineHost();
            try
            {
                h.Send(new ControlRequest { Cmd = "snapshot" });
                host = h;
                return true;
            }
            catch
            {
                h.Dispose();
                host = null;
                return false;
            }
        }

        private ControlResponse Send(ControlRequest request)
        {
            lock (_lock)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (_pipe == null || !_pipe.IsConnected)
                        {
                            Close();
                            _pipe = new NamedPipeClientStream(".", ControlServer.PipeName, PipeDirection.InOut);
                            _pipe.Connect(1500);
                            _reader = new StreamReader(_pipe, new UTF8Encoding(false));
                            _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
                        }
                        _writer!.WriteLine(JsonSerializer.Serialize(request));
                        string line = _reader!.ReadLine() ?? throw new IOException("o serviço fechou a conexão");
                        var response = JsonSerializer.Deserialize<ControlResponse>(line) ?? throw new IOException("resposta inválida");
                        if (!response.Ok)
                            throw new InvalidOperationException(response.Error ?? "erro no serviço");
                        return response;
                    }
                    catch (IOException) when (attempt == 0)
                    {
                        Close();
                    }
                    catch (TimeoutException) when (attempt == 0)
                    {
                        Close();
                    }
                }
            }
        }

        public EngineSnapshot GetSnapshot(string? connectionsForKey) =>
            Send(new ControlRequest { Cmd = "snapshot", ConnectionsFor = connectionsForKey }).Snapshot ?? new EngineSnapshot();

        public void SetRule(AppRule rule) => Send(new ControlRequest { Cmd = "setRule", Rule = rule });
        public void SetGlobal(AppRule rule) => Send(new ControlRequest { Cmd = "setGlobal", Rule = rule });
        public void Start() => Send(new ControlRequest { Cmd = "start" });
        public void Stop() => Send(new ControlRequest { Cmd = "stop" });

        private void Close()
        {
            // The writer flushes on dispose, so it has to go first: disposing the reader closes the pipe.
            try { _writer?.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }
            try { _reader?.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }
            try { _pipe?.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }
            _reader = null;
            _writer = null;
            _pipe = null;
        }

        public void Dispose()
        {
            lock (_lock)
                Close();
        }
    }
}
