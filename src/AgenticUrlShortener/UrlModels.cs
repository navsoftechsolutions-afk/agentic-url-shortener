namespace AgenticUrlShortener;

public record CreateUrlRequest(string Url, int? ExpiresInDays = null);
public class ShortLink
{
    public string Code { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public bool Disabled { get; set; }
    public int Clicks { get; set; }
}
public record ClickEvent(string Code, DateTime AtUtc);
public class UrlData
{
    public List<ShortLink> Links { get; set; } = [];
    public List<ClickEvent> Clicks { get; set; } = [];
}
