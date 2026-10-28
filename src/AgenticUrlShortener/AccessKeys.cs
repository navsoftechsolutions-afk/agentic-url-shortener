using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AgenticUrlShortener;

public class AccessKeys
{
    private readonly Dictionary<string, string> _keys;
    public AccessKeys(EngineeringOptions options, IConfiguration configuration)
    {
        var operatorKey = configuration["OPERATOR_API_KEY"];
        var reviewerKey = configuration["REVIEWER_API_KEY"];
        if (operatorKey != null || reviewerKey != null)
        {
            if (string.IsNullOrWhiteSpace(operatorKey) || string.IsNullOrWhiteSpace(reviewerKey) || operatorKey == reviewerKey)
                throw new InvalidOperationException("Configure distinct OPERATOR_API_KEY and REVIEWER_API_KEY values together.");
            _keys = new() { ["operator"] = operatorKey, ["reviewer"] = reviewerKey };
            return;
        }
        Directory.CreateDirectory(options.DataDirectory);
        var path = Path.Combine(options.DataDirectory, "access-keys.json");
        if (!File.Exists(path))
        {
            var keys = new Dictionary<string, string>
            {
                ["operator"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ["reviewer"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            };
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(path, fileOptions);
            JsonSerializer.Serialize(file, keys, new JsonSerializerOptions { WriteIndented = true });
        }
        _keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        if (_keys.Count != 2 || !_keys.ContainsKey("operator") || !_keys.ContainsKey("reviewer") ||
            _keys.Values.Any(string.IsNullOrWhiteSpace) || _keys["operator"] == _keys["reviewer"])
            throw new InvalidDataException("Invalid access-key configuration.");
    }
    public string? Authenticate(string supplied)
    {
        var suppliedHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(supplied));
        foreach (var (role, secret) in _keys)
            if (CryptographicOperations.FixedTimeEquals(suppliedHash,
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)))) return role;
        return null;
    }
}

public class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AccessKeys keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var key)) return Task.FromResult(AuthenticateResult.NoResult());
        var role = keys.Authenticate(key.ToString());
        if (role == null) return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, role), new Claim(ClaimTypes.Role, role)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
