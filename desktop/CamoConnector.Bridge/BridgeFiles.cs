using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CamoConnector.Bridge;

internal sealed class BridgeFiles(string data, ProgressFeed feed, LocalTracker tracker)
{
    private readonly string eventLog = Path.Combine(data, "sunburn-events.jsonl");
    private readonly string backup = Path.Combine(data, "mw4-camos.latest.json");
    private readonly SemaphoreSlim writer = new(1);

    public static byte[] LoadSecret(string data)
    {
        var path = Path.Combine(data, "bridge-pairing.key");
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { File.Delete(temporary); }
        }
        var secret = Convert.FromHexString(File.ReadAllText(path).Trim());
        if (secret.Length != 32) throw new InvalidDataException("Bridge pairing key is invalid.");
        return secret;
    }

    public async Task FollowAsync(CancellationToken cancellationToken)
    {
        var initial = new FileInfo(eventLog);
        var historicalEnd = initial.Exists ? initial.Length : 0;
        long offset = 0;
        var created = initial.Exists ? initial.CreationTimeUtc : DateTime.MinValue;
        var buffer = new byte[65536];
        using var line = new MemoryStream();
        var oversized = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var info = new FileInfo(eventLog);
                if (!info.Exists || info.CreationTimeUtc != created || info.Length < offset)
                {
                    created = info.Exists ? info.CreationTimeUtc : DateTime.MinValue;
                    offset = 0;
                    historicalEnd = 0;
                    line.SetLength(0);
                    oversized = false;
                }
                if (info.Exists && info.Length > offset)
                {
                    using var input = new FileStream(eventLog, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    input.Position = offset;
                    var size = input.Read(buffer);
                    offset += size;
                    for (var i = 0; i < size; i++)
                    {
                        if (buffer[i] == (byte)'\n')
                        {
                            if (!oversized)
                            {
                                var record = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                                var historical = offset - size + i < historicalEnd;
                                feed.Accept(record, historical);
                                if (!historical)
                                {
                                    try { tracker.Accept(record); }
                                    catch (Exception error) when (error is IOException or JsonException)
                                    { Console.Error.WriteLine("Local tracker: " + error.Message); }
                                }
                            }
                            line.SetLength(0);
                            oversized = false;
                        }
                        else if (!oversized)
                        {
                            if (line.Length < 1024 * 1024) line.WriteByte(buffer[i]);
                            else oversized = true;
                        }
                    }
                }
            }
            catch (IOException error) { Console.Error.WriteLine("Log read failed: " + error.Message); }
            try { await Task.Delay(100, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<JsonElement?> ReadBackupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken);
            var value = document.RootElement;
            if (!ValidBackup(value)) throw new InvalidDataException("Backup file is invalid.");
            return value.Clone();
        }
        catch (FileNotFoundException) { return null; }
    }

    public async Task SaveBackupAsync(HttpContext context)
    {
        var cancel = context.RequestAborted;
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancel); }
        catch (JsonException) { context.Response.StatusCode = 400; return; }
        using (document)
        {
            var value = document.RootElement;
            if (!ValidBackup(value)) { context.Response.StatusCode = 400; return; }
            await writer.WaitAsync(cancel);
            try
            {
                var counts = ProgressFeed.Field(value, "counts");
                var done = ProgressFeed.Field(value, "done");
                var previous = await ReadBackupAsync(cancel);
                var populated = counts.EnumerateObject().Any(item => item.Value.GetInt64() > 0) ||
                    done.EnumerateObject().Any(item => item.Value.GetBoolean());
                var regressed = previous is JsonElement old && (
                    ProgressFeed.Field(value, "updatedAt").GetDouble() < ProgressFeed.Field(old, "updatedAt").GetDouble() ||
                    ProgressFeed.Field(old, "counts").EnumerateObject().Any(item =>
                        item.Value.GetInt64() > (ProgressFeed.Field(counts, item.Name).ValueKind == JsonValueKind.Number
                            ? counts.GetProperty(item.Name).GetInt64() : 0)) ||
                    ProgressFeed.Field(old, "done").EnumerateObject().Any(item =>
                        item.Value.GetBoolean() && ProgressFeed.Field(done, item.Name).ValueKind != JsonValueKind.True));
                if (!populated || regressed)
                {
                    await context.Response.WriteAsJsonAsync(new { status = "preserved",
                        updatedAt = previous is JsonElement stored ? ProgressFeed.Field(stored, "updatedAt").GetDouble() : (double?)null }, cancel);
                    return;
                }
                if (previous is not JsonElement before || !JsonElement.DeepEquals(value, before))
                {
                    var temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllTextAsync(temporary, value.GetRawText(), cancel);
                        File.Move(temporary, backup, true);
                    }
                    finally { File.Delete(temporary); }
                }
                await context.Response.WriteAsJsonAsync(new { status = "saved" }, cancel);
            }
            finally { writer.Release(); }
        }
    }

    private static bool ValidBackup(JsonElement value)
    {
        var counts = ProgressFeed.Field(value, "counts");
        var done = ProgressFeed.Field(value, "done");
        var updatedAt = ProgressFeed.Field(value, "updatedAt");
        return ProgressFeed.Number(value, "v") == 1 && ProgressFeed.Text(value, "mode") == "mp" &&
            updatedAt.ValueKind == JsonValueKind.Number && updatedAt.TryGetDouble(out var time) && double.IsFinite(time) &&
            counts.ValueKind == JsonValueKind.Object && counts.EnumerateObject().All(item =>
                item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt64(out var number) && number is >= 0 and <= 9007199254740991) &&
            done.ValueKind == JsonValueKind.Object && done.EnumerateObject().All(item =>
                item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False);
    }
}
