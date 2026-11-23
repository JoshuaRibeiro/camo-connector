using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CamoConnector.Bridge;

internal sealed class LocalTrackerRoutes(LocalTracker tracker, ProgressFeed feed, byte[] secret)
{
    private sealed record OverlaySelection(string? Layout);
    private readonly string overlayToken = Convert.ToHexStringLower(HMACSHA256.HashData(secret, "camo-local-overlay"u8));

    public async Task HandleAsync(HttpContext context, string host)
    {
        var request = context.Request;
        var response = context.Response;
        var origin = request.Headers.Origin.ToString();
        if (request.Host.Value != host || (origin.Length != 0 && origin != "http://" + host))
        {
            response.StatusCode = 403;
            return;
        }

        // OBS applies Browser Source Custom CSS as an inline stylesheet.
        var styles = request.Path == "/local/overlay" ? "'self' 'unsafe-inline'" : "'self'";
        var frames = request.Path == "/local/tracker" ? "'self'" : "'none'";
        var ancestors = request.Path == "/local/overlay" && request.Query.ContainsKey("preview") ? "'self'" : "'none'";
        response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; script-src 'self'; img-src 'self'; style-src {styles}; connect-src 'self'; base-uri 'none'; frame-src {frames}; frame-ancestors {ancestors}";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        var path = request.Path.Value;
        var read = HttpMethods.IsGet(request.Method);
        var write = HttpMethods.IsPost(request.Method);
        const string artworkPrefix = "/local/artwork/";
        if (read && path?.StartsWith(artworkPrefix, StringComparison.Ordinal) == true)
        {
            var filename = path[artworkPrefix.Length..];
            using var stream = Regex.IsMatch(filename, @"\A[a-z0-9-]+\.png\z")
                ? typeof(LocalTrackerRoutes).Assembly.GetManifestResourceStream(LocalTracker.ArtworkResourcePrefix + filename)
                : null;
            if (stream == null)
            {
                response.StatusCode = 404;
                return;
            }
            response.ContentType = "image/png";
            response.Headers.CacheControl = "public, max-age=3600";
            await stream.CopyToAsync(response.Body, context.RequestAborted);
            return;
        }
        var asset = path switch
        {
            "/local/tracker" => ("tracker.html", "text/html"),
            "/local/overlay" => ("overlay.html", "text/html"),
            "/local/progress.js" => ("progress.js", "text/javascript"),
            "/local/tracker.js" => ("tracker.js", "text/javascript"),
            "/local/overlay.js" => ("overlay.js", "text/javascript"),
            "/local/style.css" => ("style.css", "text/css"),
            "/local/minimal-glow-rim.svg" => ("minimal-glow-rim.svg", "image/svg+xml"),
            _ => (null, null)
        };
        if (read && asset.Item1 != null)
        {
            using var stream = typeof(LocalTrackerRoutes).Assembly.GetManifestResourceStream("CamoConnector.LocalUi." + asset.Item1)!;
            response.ContentType = asset.Item2 + "; charset=utf-8";
            await stream.CopyToAsync(response.Body, context.RequestAborted);
            return;
        }

        var expected = path == "/local/overlay-state" ? overlayToken : Convert.ToHexStringLower(secret);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(request.Headers.Authorization.ToString()),
            Encoding.ASCII.GetBytes("Bearer " + expected)))
        {
            response.StatusCode = 401;
            return;
        }

        if (read && path == "/local/overlay-state")
            await response.WriteAsJsonAsync(tracker.Overlay());
        else if (read && path == "/local/catalog")
            await response.WriteAsJsonAsync(new { catalog = tracker.GetCatalog(),
                overlayUrl = "http://" + host + "/local/overlay#" + overlayToken });
        else if (read && path == "/local/state")
            await response.WriteAsJsonAsync(tracker.TrackerView());
        else if (write && path == "/local/overlay-layout")
        {
            OverlaySelection? selection;
            try { selection = await request.ReadFromJsonAsync<OverlaySelection>(context.RequestAborted); }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            { response.StatusCode = 400; return; }
            if (!tracker.SetOverlayLayout(selection?.Layout)) { response.StatusCode = 400; return; }
            await response.WriteAsJsonAsync(tracker.TrackerView());
        }
        else if (write && path == "/local/edit")
        {
            LocalTracker.Edit? edit;
            try { edit = await request.ReadFromJsonAsync<LocalTracker.Edit>(context.RequestAborted); }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            { response.StatusCode = 400; return; }
            if (edit == null || !tracker.ApplyEdit(edit)) { response.StatusCode = 400; return; }
            if (edit.Camo == null)
            {
                var weapon = tracker.GetCatalog().Weapons.Single(item => item.Id == edit.Weapon);
                feed.ReportFocus(weapon.Name);
            }
            await response.WriteAsJsonAsync(tracker.TrackerView());
        }
        else if (write && path == "/local/import")
        {
            try
            {
                if (!tracker.ImportBackup()) { response.StatusCode = 404; return; }
            }
            catch (JsonException) { response.StatusCode = 400; return; }
            await response.WriteAsJsonAsync(tracker.TrackerView());
        }
        else response.StatusCode = 404;
    }
}
