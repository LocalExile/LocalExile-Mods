using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LiveEconomy;

/// <summary>The same dll on a server, with no Gateway: <c>dotnet liveeconomy.dll --serve</c> reads GGG's digests and serves <c>/rates.json</c>, so that the players
/// using it never contact GGG. Nothing here touches the mod's API, which is not there without a Gateway. Game_RATES_PORT (default 6114), Game_RATES_FOLDER
/// (default rates-data), Game_RATES_HOURS (default 168). With <c>--once &lt;file&gt;</c> it writes the file and exits.</summary>
static class Server
{
    static async Task<int> Main(string[] args)
    {
        string folder = Environment.GetEnvironmentVariable("Game_RATES_FOLDER") ?? "rates-data";
        int port = int.Parse(Environment.GetEnvironmentVariable("Game_RATES_PORT") ?? "6114");
        int hours = int.Parse(Environment.GetEnvironmentVariable("Game_RATES_HOURS") ?? "168");
        if (args is ["--once", var output])
        {
            using var http = Digests.NewClient();
            if (await Digests.Update(http, folder, hours, CancellationToken.None) is not { } rates) return 1;
            await File.WriteAllTextAsync(output, rates);
            Console.WriteLine($"[rates] wrote {output}, {rates.Length} characters");
            return 0;
        }
        if (args is not ["--serve"])
        {
            Console.Error.WriteLine("This is a mod for LocalExile's Gateway. To serve its rates: dotnet liveeconomy.dll --serve, or --once <file>.");
            return 1;
        }
        await Serve(folder, port, hours);
        return 0;
    }

    static async Task Serve(string folder, int port, int hours)
    {
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

        byte[]? served = null;
        string tag = "";
        void Publish(string json)
        {
            served = Encoding.UTF8.GetBytes(json);
            tag = $"\"{Convert.ToHexString(SHA256.HashData(served))[..16]}\"";
        }
        string latest = Path.Combine(folder, "rates.json");
        if (File.Exists(latest)) Publish(File.ReadAllText(latest));

        var updating = Task.Run(async () =>
        {
            using var http = Digests.NewClient();
            while (!stopping.IsCancellationRequested)
            {
                var wait = TimeSpan.FromMinutes(5);
                try
                {
                    if (await Digests.Update(http, folder, hours, stopping.Token) is { } json)
                    {
                        await File.WriteAllTextAsync(latest, json, stopping.Token);
                        Publish(json);
                        Console.WriteLine($"[rates] {json.Length} characters, {Directory.GetFiles(folder, "digest-*.json").Length} digests");
                    }
                    wait = Digests.UntilNext();
                }
                catch (Exception e) when (!stopping.IsCancellationRequested && e is HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                    or KeyNotFoundException or InvalidOperationException or OperationCanceledException) // cancelled and not stopping: a timeout
                {
                    Console.WriteLine($"[rates] {e.Message}");
                }
                await Task.Delay(wait, stopping.Token);
            }
        }, stopping.Token);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();
        Console.WriteLine($"[rates] serving /rates.json on port {port}");
        stopping.Token.Register(listener.Stop);
        while (!stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
            _ = Task.Run(async () =>
            {
                using var response = context.Response;
                try
                {
                    var body = served;
                    if (context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/rates.json" || body is null) response.StatusCode = 404;
                    else if (context.Request.Headers["If-None-Match"] == tag) response.StatusCode = 304;
                    else
                    {
                        response.ContentType = "application/json";
                        response.AddHeader("ETag", tag);
                        response.ContentLength64 = body.Length;
                        await response.OutputStream.WriteAsync(body);
                    }
                }
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
            });
        }
        try { await updating; }
        catch (OperationCanceledException) { }
    }
}
