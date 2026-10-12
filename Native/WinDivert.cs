using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetDivert.Native
{
    public enum WinDivertLayer : int
    {
        Network = 0,
        NetworkForward = 1,
        Flow = 2,
        Socket = 3,
        Reflect = 4,
    }

    public enum WinDivertEvent : byte
    {
        NetworkPacket = 0,
        FlowEstablished = 1,
        FlowDeleted = 2,
        SocketBind = 3,
        SocketConnect = 4,
        SocketListen = 5,
        SocketAccept = 6,
        SocketClose = 7,
    }

    [Flags]
    public enum WinDivertFlags : ulong
    {
        None = 0,
        Sniff = 0x0001,
        Drop = 0x0002,
        RecvOnly = 0x0004,
        SendOnly = 0x0008,
        NoInstall = 0x0010,
        Fragments = 0x0020,
    }

    public enum WinDivertParam : int
    {
        QueueLength = 0,
        QueueTime = 1,
        QueueSize = 2,
        VersionMajor = 3,
        VersionMinor = 4,
    }

    /// <summary>WINDIVERT_ADDRESS (80 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 80)]
    public unsafe struct WinDivertAddress
    {
        public long Timestamp;
        public uint Bits;
        public uint Reserved2;
        public fixed byte Data[64];

        public WinDivertLayer Layer => (WinDivertLayer)(Bits & 0xFF);
        public WinDivertEvent Event => (WinDivertEvent)((Bits >> 8) & 0xFF);
        public bool Sniffed => (Bits & (1u << 16)) != 0;
        public bool Outbound => (Bits & (1u << 17)) != 0;
        public bool Loopback => (Bits & (1u << 18)) != 0;
        public bool Impostor => (Bits & (1u << 19)) != 0;
        public bool IPv6 => (Bits & (1u << 20)) != 0;

        public uint InterfaceIndex
        {
            get
            {
                fixed (byte* p = Data)
                    return *(uint*)p;
            }
        }
    }

    /// <summary>WINDIVERT_DATA_FLOW / WINDIVERT_DATA_SOCKET (identical layouts).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WinDivertDataFlow
    {
        public ulong EndpointId;
        public ulong ParentEndpointId;
        public uint ProcessId;
        public fixed uint LocalAddr[4];
        public fixed uint RemoteAddr[4];
        public ushort LocalPort;
        public ushort RemotePort;
        public byte Protocol;
    }

    internal static unsafe class WinDivertNative
    {
        private const string Dll = "WinDivert.dll";

        public static readonly IntPtr InvalidHandle = new IntPtr(-1);

        public const int ShutdownRecv = 1;
        public const int ShutdownSend = 2;
        public const int ShutdownBoth = 3;

        public const int BatchMax = 0xFF;

        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
        public static extern IntPtr WinDivertOpen(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags);

        [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
        public static extern bool WinDivertRecv(IntPtr handle, void* packet, uint packetLen, out uint recvLen, WinDivertAddress* addr);

        [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
        public static extern bool WinDivertRecvEx(IntPtr handle, void* packet, uint packetLen, out uint recvLen, ulong flags,
            WinDivertAddress* addr, uint* addrLen, IntPtr overlapped);

        [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
        public static extern bool WinDivertSend(IntPtr handle, void* packet, uint packetLen, out uint sendLen, WinDivertAddress* addr);

        [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
        public static extern bool WinDivertShutdown(IntPtr handle, int how);

        [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
        public static extern bool WinDivertClose(IntPtr handle);

        [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
        public static extern bool WinDivertSetParam(IntPtr handle, WinDivertParam param, ulong value);

        [DllImport(Dll, SetLastError = true, ExactSpelling = true)]
        public static extern bool WinDivertGetParam(IntPtr handle, WinDivertParam param, out ulong value);
    }

    /// <summary>Thin owner of a WinDivert handle.</summary>
    public sealed unsafe class WinDivertHandle : IDisposable
    {
        private IntPtr _handle;

        public IntPtr Raw => _handle;

        private WinDivertHandle(IntPtr handle) => _handle = handle;

        public static WinDivertHandle Open(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags)
        {
            IntPtr h = WinDivertNative.WinDivertOpen(filter, layer, priority, flags);
            if (h == WinDivertNative.InvalidHandle)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Falha ao abrir o filtro de rede: {DescribeOpenError(err)} (código {err})");
            }
            return new WinDivertHandle(h);
        }

        public static string DescribeOpenError(int err) => err switch
        {
            2 => "o arquivo do filtro não foi encontrado ao lado do programa",
            5 => "acesso negado - execute como Administrador",
            87 => "parâmetro/filtro inválido",
            577 => "assinatura do driver não pôde ser verificada",
            654 => "outra versão incompatível do filtro já está carregada",
            1060 => "o serviço do driver foi marcado para exclusão; reinicie o Windows",
            1275 => "o driver foi bloqueado (antivírus ou política do sistema)",
            1753 => "Base Filtering Engine (BFE) desativado",
            _ => new Win32Exception(err).Message,
        };

        public void SetParam(WinDivertParam param, ulong value) =>
            WinDivertNative.WinDivertSetParam(_handle, param, value);

        public bool Send(byte* packet, int length, WinDivertAddress* addr) =>
            WinDivertNative.WinDivertSend(_handle, packet, (uint)length, out _, addr);

        public void Shutdown()
        {
            if (_handle != IntPtr.Zero)
                WinDivertNative.WinDivertShutdown(_handle, WinDivertNative.ShutdownBoth);
        }

        public void Dispose()
        {
            IntPtr h = _handle;
            _handle = IntPtr.Zero;
            if (h != IntPtr.Zero)
                WinDivertNative.WinDivertClose(h);
        }
    }
}
