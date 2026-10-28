using System.Text.Json;

namespace AgenticUrlShortener;

// One owner per file per process. Multiple application instances require a transactional database.
public class JsonStore<T> where T : new()
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public JsonStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }
    public async Task<R> Update<R>(Func<T, R> action)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await LoadUnsafe();
            var result = action(data);
            var temporary = _path + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, data, Options);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, true);
            return result;
        }
        finally { _gate.Release(); }
    }
    public async Task<R> Read<R>(Func<T, R> action)
    {
        await _gate.WaitAsync();
        try { return action(await LoadUnsafe()); }
        finally { _gate.Release(); }
    }
    private async Task<T> LoadUnsafe() => File.Exists(_path)
        ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(_path), Options) ?? throw new InvalidDataException("Store is null.")
        : new T();
}
