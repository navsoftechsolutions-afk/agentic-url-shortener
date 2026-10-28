using System.Security.Cryptography;

namespace AgenticUrlShortener;

public class UrlService(JsonStore<UrlData> store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public static bool IsSafeUrl(string? url)
    {
        if (url is null || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == "http" || uri.Scheme == "https") && !string.IsNullOrWhiteSpace(uri.Host) && uri.UserInfo.Length == 0;
    }
    public async Task<ShortLink> Create(CreateUrlRequest req)
    {
        if (!IsSafeUrl(req.Url)) throw new ArgumentException("Only absolute HTTP(S) URLs without credentials, up to 2048 characters, are allowed.");
        if (req.ExpiresInDays is < 1 or > 365) throw new ArgumentException("Expiration must be 1 to 365 days.");
        return await store.Update(data =>
        {
            string code;
            do { code = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant(); }
            while (data.Links.Any(x => x.Code == code));
            var now = _clock.GetUtcNow().UtcDateTime;
            var link = new ShortLink { Code = code, Url = req.Url, CreatedUtc = now,
                ExpiresUtc = req.ExpiresInDays is int days ? now.AddDays(days) : null };
            data.Links.Add(link);
            return link;
        });
    }
    public Task<ShortLink?> Resolve(string code) => store.Update(data =>
    {
        var link = data.Links.FirstOrDefault(x => x.Code == code);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (link == null || link.Disabled || link.ExpiresUtc <= now) return null;
        link.Clicks++;
        data.Clicks.Add(new ClickEvent(code, now));
        return link;
    });
    public Task<object?> Analytics(string code) => store.Read<object?>(data =>
    {
        var link = data.Links.FirstOrDefault(x => x.Code == code);
        return link == null ? null : new { link.Code, link.Url, link.Clicks, link.CreatedUtc, link.ExpiresUtc, link.Disabled,
            DailyClicks = data.Clicks.Where(c => c.Code == code).GroupBy(c => c.AtUtc.Date)
                .Select(g => new { Date = g.Key, Count = g.Count() }).ToList() };
    });
    public Task<bool> Disable(string code) => store.Update(data =>
    {
        var link = data.Links.FirstOrDefault(x => x.Code == code);
        if (link == null) return false;
        link.Disabled = true;
        return true;
    });
}
