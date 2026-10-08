using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.Services;

/// <summary>
/// Login con Microsoft por código de dispositivo (sin Azure propio: usa el
/// client público de Minecraft, igual que otros launchers). Flujo:
/// devicecode → el usuario confirma en el navegador → token → Xbox Live →
/// XSTS → Minecraft → perfil. Todo por HTTPS.
/// </summary>
public sealed class MsaTokens
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class MinecraftProfile
{
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty; // UUID sin guiones
}

public interface IMicrosoftAuthService
{
    /// <summary>
    /// URL de login para abrir en el navegador. El redirect validado por
    /// Microsoft para este client es oauth20_desktop.srf: tras iniciar
    /// sesión, la barra de direcciones trae el ?code= para pegar acá.
    /// </summary>
    string BuildAuthorizeUrl();
    Task<MsaTokens?> ExchangeCodeAsync(string code, CancellationToken ct);
    Task<MsaTokens?> RefreshAsync(string refreshToken, CancellationToken ct);
    Task<(string? McToken, DateTimeOffset Expires)> LoginMinecraftAsync(MsaTokens tokens, CancellationToken ct);
    Task<MinecraftProfile?> GetProfileAsync(string mcToken, CancellationToken ct);
}

public class MicrosoftAuthService : IMicrosoftAuthService
{
    // Client público de Minecraft (el mismo que usan otros launchers).
    private const string ClientId = "00000000402b5328";
    private const string Scope = "XboxLive.signin offline_access";

    // Único redirect aceptado por este client: loopback y custom URIs
    // son rechazados (invalid_request). El código llega en la URL final
    // (?code=...) y el usuario lo pega en el launcher.
    private const string DesktopRedirect = "https://login.live.com/oauth20_desktop.srf";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MineMount/1.1");
        }
        catch
        {
            // UA opcional
        }
        return client;
    }
    private readonly ILogService _logService;

    public MicrosoftAuthService(ILogService logService)
    {
        _logService = logService;
    }

    public string BuildAuthorizeUrl()
    {
        return "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize"
            + "?client_id=" + ClientId
            + "&response_type=code"
            + "&redirect_uri=" + Uri.EscapeDataString(DesktopRedirect)
            + "&scope=" + Uri.EscapeDataString(Scope)
            + "&prompt=select_account";
    }

    public async Task<MsaTokens?> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        code = (code ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code)) return null;

        // Acepta el código pelado o la URL completa (?code=...).
        var codeIndex = code.IndexOf("code=", StringComparison.OrdinalIgnoreCase);
        if (codeIndex >= 0)
        {
            code = code.Substring(codeIndex + 5);
            var amp = code.IndexOf('&');
            if (amp >= 0) code = code.Substring(0, amp);
            code = Uri.UnescapeDataString(code.Trim());
        }

        if (string.IsNullOrWhiteSpace(code)) return null;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = DesktopRedirect
        });

        using var resp = await Http.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token", form, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logService.Warning($"Canje de código falló: {ReadOAuthError(json)}");
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        return new MsaTokens
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? string.Empty,
            RefreshToken = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? string.Empty : string.Empty,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60)
        };
    }

    private static string ReadOAuthError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return string.IsNullOrWhiteSpace(desc) ? (error ?? "error desconocido") : desc;
        }
        catch
        {
            return "respuesta inválida";
        }
    }

    public async Task<MsaTokens?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["scope"] = Scope,
                ["refresh_token"] = refreshToken
            });

            using var resp = await Http.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token", form, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            return new MsaTokens
            {
                AccessToken = root.GetProperty("access_token").GetString() ?? string.Empty,
                RefreshToken = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? refreshToken : refreshToken,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60)
            };
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo renovar el token: {ex.Message}");
            return null;
        }
    }

    public async Task<(string? McToken, DateTimeOffset Expires)> LoginMinecraftAsync(MsaTokens tokens, CancellationToken ct)
    {
        var (xblToken, _) = await AuthenticateXboxLiveAsync(tokens.AccessToken, ct);
        if (xblToken == null) return (null, DateTimeOffset.MinValue);

        var (xstsToken, userHash) = await AuthorizeXstsAsync(xblToken, ct);
        if (xstsToken == null || userHash == null) return (null, DateTimeOffset.MinValue);

        var payload = JsonSerializer.Serialize(new
        {
            identityToken = $"XBL3.0 x={userHash};{xstsToken}"
        });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox", content, ct);
        if (!resp.IsSuccessStatusCode) return (null, DateTimeOffset.MinValue);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 86400;
        return (root.GetProperty("access_token").GetString(), DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60));
    }

    public async Task<MinecraftProfile?> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            return new MinecraftProfile
            {
                Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                Id = root.TryGetProperty("id", out var id) ? (id.GetString() ?? string.Empty).Replace("-", "") : string.Empty
            };
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo obtener el perfil: {ex.Message}");
            return null;
        }
    }

    private static async Task<(string? Token, string? UserHash)> AuthenticateXboxLiveAsync(string msaToken, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + msaToken
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync("https://user.auth.xboxlive.com/user/authenticate", content, ct);
        if (!resp.IsSuccessStatusCode) return (null, null);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var uhs = root.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString();
        return (root.GetProperty("Token").GetString(), uhs);
    }

    private static async Task<(string? Token, string? UserHash)> AuthorizeXstsAsync(string xblToken, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize", content, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode)
        {
            // XErr típicos: 2148916233 = sin cuenta Xbox, 2148916238 = menor con familia.
            var xerr = root.TryGetProperty("XErr", out var x) ? x.GetInt64().ToString() : "desconocido";
            throw new InvalidOperationException($"Xbox rechazó el login (XErr {xerr}). Revisá tu cuenta en xbox.com.");
        }

        var uhs = root.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString();
        return (root.GetProperty("Token").GetString(), uhs);
    }
}
