using System.Net.WebSockets;
using System.Reflection;
using System.Text;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private bool releaseNoticePending;

    private async Task ListenForReleaseNoticesAsync(CancellationToken cancel)
    {
        var address = typeof(ConnectorWindow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "CamoUpdateNotifyUrl")?.Value;
        if (updateKey == null || !Uri.TryCreate(address, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "https") return;

        endpoint = new UriBuilder(endpoint) { Scheme = "wss", Path = "/updates" }.Uri;
        var delaySeconds = 15;
        using var buffer = new MemoryStream(32);
        var bytes = new byte[32];
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                var connectedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        await socket.ConnectAsync(endpoint, timeout.Token);
                    }
                    connectedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (socket.State == WebSocketState.Open)
                    {
                        buffer.SetLength(0);
                        WebSocketReceiveResult message;
                        var valid = true;
                        do
                        {
                            message = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), cancel);
                            if (message.MessageType != WebSocketMessageType.Text || buffer.Length + message.Count > 32)
                            {
                                valid = false;
                                break;
                            }
                            buffer.Write(bytes, 0, message.Count);
                        } while (!message.EndOfMessage);
                        if (!valid) break;
                        var text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                        if (!Version.TryParse(text, out var version) || version.Build < 0 ||
                            version.Revision != -1 || text != version.ToString(3) || version <= desktopVersion)
                            continue;
                        if (checkingUpdate) releaseNoticePending = true;
                        else await CheckForUpdateAsync(manual: false);
                    }
                }
                catch (Exception error) when (error is WebSocketException or HttpRequestException or IOException ||
                    error is OperationCanceledException && !cancel.IsCancellationRequested) { }

                if (System.Diagnostics.Stopwatch.GetElapsedTime(connectedAt) >= TimeSpan.FromMinutes(1))
                    delaySeconds = 15;
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds + Random.Shared.Next(0, 6)), cancel);
                delaySeconds = Math.Min(300, delaySeconds * 2);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }
}
