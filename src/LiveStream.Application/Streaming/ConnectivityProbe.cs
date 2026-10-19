using System.Diagnostics;
using System.Net.Sockets;

namespace LiveStream.Application.Streaming;

/// <summary>
/// Comprobación previa de un destino: resuelve el servidor y abre una conexión TCP a su puerto (1935 para RTMP,
/// 443 para RTMPS). No emite ni envía la clave; solo confirma que el servidor es alcanzable desde este equipo.
/// </summary>
public static class ConnectivityProbe
{
    public sealed record Result(bool Reachable, string Message, TimeSpan Latency);

    public static async Task<Result> TestAsync(string server, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0 ||
            uri.Scheme is not ("rtmp" or "rtmps"))
            return new Result(false, "La dirección no es una URL rtmp:// o rtmps:// válida.", TimeSpan.Zero);
        var port = uri.IsDefaultPort || uri.Port <= 0 ? (uri.Scheme == "rtmps" ? 443 : 1935) : uri.Port;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, port, cts.Token).ConfigureAwait(false);
            return new Result(true, $"{uri.Host}:{port} responde ({watch.ElapsedMilliseconds} ms).", watch.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Result(false, $"{uri.Host}:{port} no responde en {timeout.TotalSeconds:0} s (¿firewall o red?).", watch.Elapsed);
        }
        catch (SocketException ex)
        {
            return new Result(false, ex.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData => $"No existe el servidor «{uri.Host}» (DNS).",
                SocketError.ConnectionRefused => $"{uri.Host}:{port} rechaza la conexión.",
                _ => $"No se pudo conectar con {uri.Host}:{port}: {ex.SocketErrorCode}.",
            }, watch.Elapsed);
        }
    }
}
