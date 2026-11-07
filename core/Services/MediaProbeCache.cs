using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IDMLite.Core.Resolvers;

namespace IDMLite.Core.Services;

/// <summary>Shares in-flight probes and bounds UI metadata work independently of actual downloads.</summary>
public sealed class MediaProbeCache
{
    private sealed record Entry(Task<ResolvedLinkResult> Task, DateTime Expires);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly SemaphoreSlim _slots = new(3);
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(8);

    public async Task<ResolvedLinkResult> GetAsync(string url, string? cookies,
        Func<CancellationToken, Task<ResolvedLinkResult>> resolver, CancellationToken ct = default)
    {
        string key = url + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookies ?? "")));
        Entry entry;
        lock (_gate) {
            if (!_entries.TryGetValue(key, out entry!) || entry.Expires < DateTime.UtcNow) {
                if (_entries.Count >= 64) _entries.Remove(_entries.MinBy(p => p.Value.Expires).Key);
                entry = new Entry(RunAsync(url, resolver), DateTime.UtcNow.AddMinutes(3));
                _entries[key] = entry;
            }
        }
        var result = await entry.Task.WaitAsync(ct).ConfigureAwait(false);
        if (!result.IsSuccess) lock (_gate) { if (_entries.GetValueOrDefault(key) == entry) _entries.Remove(key); }
        return JsonSerializer.Deserialize<ResolvedLinkResult>(JsonSerializer.SerializeToUtf8Bytes(result))!;
    }

    private async Task<ResolvedLinkResult> RunAsync(string url, Func<CancellationToken, Task<ResolvedLinkResult>> resolver)
    {
        using var budget = new CancellationTokenSource(Budget);
        bool entered = false;
        try {
            await _slots.WaitAsync(budget.Token).ConfigureAwait(false); entered = true;
            return await resolver(budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return new() { OriginalUrl = url, DirectUrl = url, IsSuccess = false, ErrorMessage = "Video bilgisi zaman sınırını aştı. İndirme sırasında yeniden çözümlenecek." };
        } catch (Exception error) {
            return new() { OriginalUrl = url, DirectUrl = url, IsSuccess = false, ErrorMessage = error.Message };
        } finally { if (entered) _slots.Release(); }
    }
}
