using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using CamoConnector.Bridge;

const int defaultPort = 47150;
var ephemeralPort = args is [_, "--ephemeral-port"];
var host = $"127.0.0.1:{defaultPort}";
var data = args.Length == 1 || ephemeralPort ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Data"));
Directory.CreateDirectory(data);
var secret = BridgeFiles.LoadSecret(data);
var feed = new ProgressFeed(Path.Combine(AppContext.BaseDirectory, "special-catalog.json"));
var tracker = new LocalTracker(data);
var localRoutes = new LocalTrackerRoutes(tracker, feed, secret);
var files = new BridgeFiles(data, feed, tracker);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = Array.Empty<string>(),
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.ConfigureKestrel(server =>
{
    server.Listen(IPAddress.Loopback, ephemeralPort ? 0 : defaultPort);
    server.Limits.MaxRequestBodySize = 1024 * 1024;
    server.AddServerHeader = false;
});
var app = builder.Build();
var pairing = new SemaphoreSlim(1);
var lastPrompt = DateTimeOffset.MinValue;
var shutdown = app.Lifetime.ApplicationStopping;
var reader = files.FollowAsync(shutdown);
var syncOwnerLock = new object();
long trackerLeaseUntil = 0;
bool? embeddedSyncOwner = null;

app.Run(async context =>
{
    var request = context.Request;
    var response = context.Response;
    var origin = request.Headers.Origin.ToString();
    response.Headers.CacheControl = "no-store";
    if (request.Path.StartsWithSegments("/local"))
    {
        await localRoutes.HandleAsync(context, host);
        return;
    }
    if (request.Host.Value != host || origin.Length > 0 && !ValidOrigin(origin))
    {
        response.StatusCode = 403;
        return;
    }
    if (origin.Length > 0)
    {
        response.Headers.AccessControlAllowOrigin = origin;
        response.Headers.Vary = "Origin";
        if (HttpMethods.IsOptions(request.Method))
        {
            response.Headers.AccessControlAllowMethods = "GET, POST";
            response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, X-MW4-Connector, X-Camo-Client";
            response.StatusCode = 204;
            return;
        }
    }

    if (request.Path == "/pair" && HttpMethods.IsPost(request.Method))
    {
        // Privileged extension requests can omit Origin. A web page cannot
        // send the custom headers without a preflight, which rejects web origins.
        var client = request.Headers["X-Camo-Client"].ToString();
        if (!ValidOrigin(client) || origin.Length > 0 && origin != client ||
            request.Headers["X-MW4-Connector"] != "pair-v1")
        {
            response.StatusCode = 403;
            return;
        }
        if (!await pairing.WaitAsync(0, context.RequestAborted))
        {
            response.StatusCode = 429;
            return;
        }
        try
        {
            if (DateTimeOffset.UtcNow - lastPrompt < TimeSpan.FromSeconds(15))
            {
                response.StatusCode = 429;
                return;
            }
            lastPrompt = DateTimeOffset.UtcNow;
            if (!await ApproveBrowserAsync(client) || context.RequestAborted.IsCancellationRequested)
            {
                response.StatusCode = 403;
                return;
            }
            await response.WriteAsJsonAsync(new { token = ClientToken(secret, client) });
        }
        finally { pairing.Release(); }
        return;
    }

    var claimed = request.Headers["X-Camo-Client"].ToString();
    var token = ValidOrigin(claimed) && (origin.Length == 0 || origin == claimed)
        ? ClientToken(secret, claimed) : null;
    var authorization = Encoding.ASCII.GetBytes(request.Headers.Authorization.ToString());
    var expected = token != null ? Encoding.ASCII.GetBytes("Bearer " + token)
        : claimed.Length == 0 && origin.Length == 0
            ? Encoding.ASCII.GetBytes("Bearer " + Convert.ToHexStringLower(secret)) : Array.Empty<byte>();
    if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(authorization, expected))
    {
        response.StatusCode = 401;
        return;
    }
    if (request.Path == "/tracker-events" && HttpMethods.IsGet(request.Method))
    {
        if (claimed.Length != 0 || origin.Length != 0)
        {
            response.StatusCode = 403;
            return;
        }
        string snapshot;
        lock (syncOwnerLock)
        {
            if (embeddedSyncOwner == false) feed.StartSyncSession();
            embeddedSyncOwner = true;
            trackerLeaseUntil = Environment.TickCount64 + 3000;
            snapshot = feed.Snapshot();
        }
        response.ContentType = "application/json";
        await response.WriteAsync(snapshot);
    }
    else if (request.Path == "/events" && HttpMethods.IsGet(request.Method))
    {
        string? snapshot = null;
        lock (syncOwnerLock)
        {
            if (trackerLeaseUntil <= Environment.TickCount64)
            {
                if (embeddedSyncOwner == true) feed.StartSyncSession();
                embeddedSyncOwner = false;
                snapshot = feed.Snapshot();
            }
        }
        if (snapshot == null)
        {
            response.StatusCode = 409;
            await response.WriteAsync("Sync is active in the desktop Tracker tab.");
            return;
        }
        response.ContentType = "application/json";
        await response.WriteAsync(snapshot);
    }
    else if (request.Path == "/focus")
    {
        if (HttpMethods.IsGet(request.Method))
        {
            await response.WriteAsJsonAsync(new { weapon = feed.CurrentFocus() });
        }
        else if (HttpMethods.IsPost(request.Method) && request.Headers["X-MW4-Connector"] == "focus-v1")
        {
            var weapon = request.Query["weapon"].ToString();
            if (!Regex.IsMatch(weapon, @"\A[a-zA-Z0-9 .-]{2,40}\z"))
            {
                response.StatusCode = 400;
                return;
            }
            feed.ReportFocus(weapon);
            response.StatusCode = 204;
        }
        else response.StatusCode = 405;
    }
    else if (request.Path == "/backup" && request.Headers["X-MW4-Connector"] == "backup-v1")
    {
        if (HttpMethods.IsGet(request.Method))
            await response.WriteAsJsonAsync(new { backup = await files.ReadBackupAsync(context.RequestAborted) });
        else if (HttpMethods.IsPost(request.Method))
            await files.SaveBackupAsync(context);
        else response.StatusCode = 405;
    }
    else response.StatusCode = 404;
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    if (ephemeralPort) host = new Uri(app.Urls.Single()).Authority;
    Console.WriteLine($"Camo bridge ready at http://{host}");
});
await app.RunAsync();
await reader;

static bool ValidOrigin(string origin) =>
    Regex.IsMatch(origin, @"\A(?:chrome-extension://[a-p]{32}|moz-extension://[0-9a-f-]{36})\z");

static string ClientToken(byte[] secret, string origin) =>
    Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes("CoD Streaming Tools browser v1:" + origin)));

static Task<bool> ApproveBrowserAsync(string origin)
{
    var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            approval.SetResult(MessageBox.Show(
                $"Allow this browser extension to access your local CoD Streaming Tools session?\n\n{origin}\n\nApprove only if you just clicked Connect in CoD Streaming Tools.",
                "CoD Streaming Tools browser connection", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes);
        }
        catch (Exception error) { approval.TrySetException(error); }
    }) { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return approval.Task;
}
