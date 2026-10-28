using AgenticUrlShortener;
using Xunit;

namespace AgenticUrlShortener.Tests;

public class UrlTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("not-a-url", false)]
    [InlineData("https://user:pass@example.com", false)]
    [InlineData(null, false)]
    public void UrlValidation(string? url, bool expected) => Assert.Equal(expected, UrlService.IsSafeUrl(url));

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(366)]
    public async Task ExpirationRangeIsEnforced(int days)
    {
        using var f = new EngineFixture();
        var service = new UrlService(new JsonStore<UrlData>(Path.Combine(f.DirectoryPath, "urls.json")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create(new("https://example.com", days)));
    }

    [Fact]
    public async Task ExpirationBoundaryAndPersistenceAreReal()
    {
        using var f = new EngineFixture();
        var path = Path.Combine(f.DirectoryPath, "urls.json");
        var clock = new Clock();
        var service = new UrlService(new JsonStore<UrlData>(path), clock);
        var link = await service.Create(new("https://example.com", 1));
        Assert.NotNull(await service.Resolve(link.Code));
        var reloaded = new UrlService(new JsonStore<UrlData>(path), clock);
        clock.Now = clock.Now.AddDays(1);
        Assert.Null(await reloaded.Resolve(link.Code));
        Assert.Equal(1, await new JsonStore<UrlData>(path).Read(d => d.Links.Single().Clicks));
    }

    [Fact]
    public async Task ConcurrentCreatesAndClicksDoNotLoseData()
    {
        using var f = new EngineFixture();
        var store = new JsonStore<UrlData>(Path.Combine(f.DirectoryPath, "urls.json"));
        var service = new UrlService(store);
        var links = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => service.Create(new("https://example.com"))));
        Assert.Equal(32, links.Select(l => l.Code).Distinct().Count());
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => service.Resolve(links[0].Code)));
        Assert.Equal(50, await store.Read(d => d.Links.Single(l => l.Code == links[0].Code).Clicks));
        Assert.Equal(50, await store.Read(d => d.Clicks.Count));
        Assert.True(await service.Disable(links[0].Code));
        Assert.Null(await service.Resolve(links[0].Code));
    }
    private class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
