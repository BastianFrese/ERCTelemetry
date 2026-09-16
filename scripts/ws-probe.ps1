# Ad-hoc diagnostics: connects to the local overlay WebSocket and prints the first messages.
# Not part of the app or the build.
$code = @'
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class WsProbe {
    public static async Task<string> Probe() {
        var ws = new ClientWebSocket();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:8090/ws"), cts.Token);
        var sb = new StringBuilder();
        var buf = new byte[65536];
        var deadline = DateTime.UtcNow.AddSeconds(8);
        int msgs = 0;
        while (DateTime.UtcNow < deadline && msgs < 14) {
            try {
                var seg = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                var text = Encoding.UTF8.GetString(buf, 0, seg.Count);
                msgs++;
                sb.AppendLine(text.Substring(0, Math.Min(text.Length, 400)));
            } catch (OperationCanceledException) { break; }
        }
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        return sb.ToString();
    }
}
'@
Add-Type -TypeDefinition $code -Language CSharp
[WsProbe]::Probe().Result