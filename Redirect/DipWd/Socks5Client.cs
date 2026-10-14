using System.Net;
using System.Net.Sockets;

namespace DipWd;

static class Socks5Client
{
    public static async Task<TcpClient> ConnectClientAsync(
        string socksHost, int socksPort, IPAddress destIp, int destPort, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(socksHost, socksPort, ct);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
            var buf = new byte[2];
            await ReadExactAsync(stream, buf, ct);
            if (buf[0] != 0x05 || buf[1] != 0x00)
                throw new IOException("SOCKS5 greeting rejeitado (auth=" + buf[1] + ")");

            var req = new byte[10];
            req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x01;
            Buffer.BlockCopy(destIp.GetAddressBytes(), 0, req, 4, 4);
            req[8] = (byte)(destPort >> 8);
            req[9] = (byte)(destPort & 0xFF);
            await stream.WriteAsync(req, ct);

            var resp = new byte[4];
            await ReadExactAsync(stream, resp, ct);
            if (resp[0] != 0x05 || resp[1] != 0x00)
                throw new IOException("SOCKS5 CONNECT falhou: reply=" + resp[1]);
            await DrainBindAddrAsync(stream, resp[3], ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    static async Task DrainBindAddrAsync(NetworkStream stream, byte atyp, CancellationToken ct)
    {
        if (atyp == 0x01)
        {
            var rest = new byte[4 + 2];
            await ReadExactAsync(stream, rest, ct);
        }
        else if (atyp == 0x03)
        {
            var dlen = new byte[1];
            await ReadExactAsync(stream, dlen, ct);
            var rest = new byte[dlen[0] + 2];
            await ReadExactAsync(stream, rest, ct);
        }
        else if (atyp == 0x04)
        {
            var rest = new byte[16 + 2];
            await ReadExactAsync(stream, rest, ct);
        }
        else throw new IOException("SOCKS5 atyp invalido: " + atyp);
    }

    static async Task ReadExactAsync(NetworkStream stream, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(off, buf.Length - off), ct);
            if (n == 0) throw new EndOfStreamException("SOCKS5 fechou a conexao.");
            off += n;
        }
    }
}
