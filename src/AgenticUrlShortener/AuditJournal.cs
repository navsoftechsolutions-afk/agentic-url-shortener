using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticUrlShortener;

public static class Digests
{
    public static string Of(string text) => OfBytes(Encoding.UTF8.GetBytes(text));
    public static string OfBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Review(WorkflowState w) => Of(JsonSerializer.Serialize(w.Evidence
        .Where(x => x.Key is not ("approval" or "release"))
        .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { x.Key, x.Value.Revision, x.Value.ContentHash,
            Inputs = x.Value.Inputs.OrderBy(i => i.Key, StringComparer.Ordinal), Files = x.Value.Files.OrderBy(f => f.Key, StringComparer.Ordinal) })));
}

// A separately appended journal detects accidental edits, truncation and divergence from state.
// It is not a substitute for an external immutable audit service against a privileged disk attacker.
public class AuditJournal
{
    private readonly string _directory;
    public AuditJournal(EngineeringOptions options)
    {
        _directory = Path.Combine(options.DataDirectory, "audit");
        Directory.CreateDirectory(_directory);
    }
    private string PathFor(string id) => Path.Combine(_directory, id + ".jsonl");
    private static string Hash(AuditEvent e) => Digests.Of(JsonSerializer.Serialize(new
        { e.Sequence, e.AtUtc, e.Revision, e.Actor, e.Event, e.Detail, e.PreviousHash }));

    public void Append(WorkflowState w, string kind, string detail, string actor = "engine")
    {
        lock (w)
        {
            var e = new AuditEvent(w.Audit.Count + 1, DateTime.UtcNow, w.Revision, actor, kind, detail,
                w.Audit.LastOrDefault()?.Hash ?? "", "");
            e = e with { Hash = Hash(e) };
            using var stream = new FileStream(PathFor(w.Id), FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e) + "\n");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            w.Audit.Add(e);
        }
    }

    public void Verify(WorkflowState w)
    {
        var events = ReadVerified(w);
        if (events.Count != w.Audit.Count) throw new ValidationException("Audit journal/state length mismatch.");
    }

    public List<AuditEvent> ReadVerified(WorkflowState w)
    {
        if (w.SchemaVersion != 2) throw new InvalidOperationException("Legacy workflow is read-only; create a new workflow.");
        var lines = File.Exists(PathFor(w.Id)) ? File.ReadAllLines(PathFor(w.Id)) : [];
        if (lines.Length < w.Audit.Count) throw new ValidationException("Audit journal truncated.");
        List<AuditEvent> events;
        try { events = lines.Select(line => JsonSerializer.Deserialize<AuditEvent>(line) ?? throw new JsonException()).ToList(); }
        catch (JsonException) { throw new ValidationException("Audit journal is malformed."); }
        var previous = "";
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Sequence != i + 1 || e.PreviousHash != previous || e.Hash != Hash(e) ||
                (i < w.Audit.Count && lines[i] != JsonSerializer.Serialize(w.Audit[i])))
                throw new ValidationException("Audit integrity check failed.");
            previous = e.Hash;
        }
        return events;
    }
}
