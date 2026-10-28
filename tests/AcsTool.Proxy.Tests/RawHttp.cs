using System.Net.Sockets;
using System.Text;

namespace AcsTool.Proxy.Tests;

/// <summary>Raw TCP client for chunked / pipelined cases HttpClient cannot express.</summary>
public static class RawHttp
{
    public static async Task<string> SendAsync(int port, byte[] request, int timeoutMs = 10000)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        await stream.WriteAsync(request);
        await stream.FlushAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
            {
                ms.Write(buf, 0, n);
            }
        }
        catch (IOException)
        {
            // reset after the response is acceptable
        }
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    public static byte[] Request(string method, string path, string host, byte[] body, bool chunked, bool close, params string[] extraHeaders)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\nHost: ").Append(host).Append("\r\n");
        foreach (var h in extraHeaders)
        {
            sb.Append(h).Append("\r\n");
        }
        if (close)
        {
            sb.Append("Connection: close\r\n");
        }
        if (chunked)
        {
            sb.Append("Transfer-Encoding: chunked\r\n\r\n");
        }
        else
        {
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
        }
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        using var ms = new MemoryStream();
        ms.Write(head);
        if (chunked)
        {
            // two chunks to make sure re-framing actually happens
            var half = body.Length / 2;
            foreach (var part in new[] { body[..half], body[half..] })
            {
                ms.Write(Encoding.ASCII.GetBytes($"{part.Length:x}\r\n"));
                ms.Write(part);
                ms.Write("\r\n"u8);
            }
            ms.Write("0\r\n\r\n"u8);
        }
        else
        {
            ms.Write(body);
        }
        return ms.ToArray();
    }
}
