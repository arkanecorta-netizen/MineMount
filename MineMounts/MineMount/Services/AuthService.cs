using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.Services;

public enum AccountKind
{
    None,
    Microsoft,
    Offline
}

/// <summary>
/// Sesión de juego: nombre + UUID estables. Microsoft trae token real
/// (userType msa); offline usa UUID estable derivado del nombre.
/// </summary>
public sealed class GameSession
{
    public AccountKind Kind { get; set; } = AccountKind.None;
    public string Name { get; set; } = string.Empty;
    public string Uuid { get; set; } = string.Empty; // sin guiones
    public string AccessToken { get; set; } = "0";
    public string UserType { get; set; } = "legacy";
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MaxValue;
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc.AddMinutes(-15);
}

public interface IAuthService
{
    bool IsAuthenticated { get; }
    GameSession? CurrentSession { get; }
    event EventHandler? SessionChanged;
    Task<bool> TryRestoreAsync();
    Task<GameSession> LoginOfflineAsync(string name);
    Task<GameSession?> LoginMicrosoftAsync(MsaTokens tokens, MinecraftProfile profile);
    Task RefreshIfNeededAsync();
    Task LogoutAsync();
    static bool IsValidOfflineName(string? name)
        => !string.IsNullOrWhiteSpace(name)
        && Regex.IsMatch(name.Trim(), @"^[A-Za-z0-9_]{3,16}$");

    /// <summary>UUID offline estable (mismo algoritmo que el juego en modo offline).</summary>
    static string OfflineUuidFor(string name)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // versión 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variante
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public class AuthService : IAuthService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogService _logService;

    private static string AuthDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MineMount", "auth");
    private static string OfflinePath => Path.Combine(AuthDir, "offline.json");
    private static string MsaPath => Path.Combine(AuthDir, "msa.dat");

    public bool IsAuthenticated => CurrentSession != null;
    public GameSession? CurrentSession { get; private set; }

    public event EventHandler? SessionChanged;

    public AuthService(ISettingsService settingsService, ILogService logService)
    {
        _settingsService = settingsService;
        _logService = logService;
    }

    public async Task<bool> TryRestoreAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            Directory.CreateDirectory(AuthDir);

            if (settings.AccountType == nameof(AccountKind.Microsoft) && File.Exists(MsaPath))
            {
                var session = LoadMsaSession();
                if (session != null)
                {
                    CurrentSession = session;
                    SessionChanged?.Invoke(this, EventArgs.Empty);
                    _ = RefreshIfNeededAsync();
                    return true;
                }
            }

            if (settings.AccountType == nameof(AccountKind.Offline) && File.Exists(OfflinePath))
            {
                var session = LoadOfflineSession();
                if (session != null && IAuthService.IsValidOfflineName(session.Name))
                {
                    CurrentSession = session;
                    SessionChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo restaurar la sesión: {ex.Message}");
        }

        return false;
    }

    public async Task<GameSession> LoginOfflineAsync(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (!IAuthService.IsValidOfflineName(name))
            throw new ArgumentException("Nombre inválido (3-16: letras, números y guion bajo).");

        var session = new GameSession
        {
            Kind = AccountKind.Offline,
            Name = name,
            Uuid = IAuthService.OfflineUuidFor(name),
            AccessToken = "0",
            UserType = "legacy"
        };

        Directory.CreateDirectory(AuthDir);
        await File.WriteAllTextAsync(OfflinePath, JsonSerializer.Serialize(new
        {
            name = session.Name,
            uuid = session.Uuid
        }));
        if (File.Exists(MsaPath)) File.Delete(MsaPath);

        await SaveAccountAsync(nameof(AccountKind.Offline), name, session.Uuid);

        CurrentSession = session;
        SessionChanged?.Invoke(this, EventArgs.Empty);
        _logService.Info($"Sesión offline iniciada: {name}");
        return session;
    }

    public async Task<GameSession?> LoginMicrosoftAsync(MsaTokens tokens, MinecraftProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Id))
            return null;

        var session = new GameSession
        {
            Kind = AccountKind.Microsoft,
            Name = profile.Name,
            Uuid = profile.Id.Replace("-", ""),
            AccessToken = string.Empty, // el token MC se pide al jugar
            UserType = "msa",
            ExpiresAtUtc = DateTimeOffset.UtcNow
        };

        Directory.CreateDirectory(AuthDir);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            refresh = tokens.RefreshToken,
            name = session.Name,
            uuid = session.Uuid
        });
        await File.WriteAllBytesAsync(MsaPath, Dpapi.Protect(payload));
        if (File.Exists(OfflinePath)) File.Delete(OfflinePath);

        await SaveAccountAsync(nameof(AccountKind.Microsoft), session.Name, session.Uuid);

        CurrentSession = session;
        SessionChanged?.Invoke(this, EventArgs.Empty);
        _logService.Info($"Sesión Microsoft iniciada: {profile.Name}");
        return session;
    }

    /// <summary>
    /// Renueva el token en segundo plano si vence en menos de 15 minutos.
    /// Devuelve el access token de Minecraft vigente (o vacío si no se pudo).
    /// </summary>
    public async Task RefreshIfNeededAsync()
    {
        var session = CurrentSession;
        if (session == null || session.Kind != AccountKind.Microsoft) return;
        if (!session.IsExpired && !string.IsNullOrWhiteSpace(session.AccessToken)
            && session.AccessToken != "0") return;

        try
        {
            if (!File.Exists(MsaPath)) return;
            var raw = Dpapi.Unprotect(await File.ReadAllBytesAsync(MsaPath));
            using var doc = JsonDocument.Parse(raw);
            var refresh = doc.RootElement.TryGetProperty("refresh", out var r) ? r.GetString() : null;
            if (string.IsNullOrWhiteSpace(refresh)) return;

            var ms = new MicrosoftAuthService(_logService);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var tokens = await ms.RefreshAsync(refresh, cts.Token);
            if (tokens == null) return;

            var (mcToken, _) = await ms.LoginMinecraftAsync(tokens, cts.Token);
            if (string.IsNullOrWhiteSpace(mcToken)) return;

            session.AccessToken = mcToken;
            session.ExpiresAtUtc = tokens.ExpiresAtUtc;

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                refresh = tokens.RefreshToken,
                name = session.Name,
                uuid = session.Uuid
            });
            await File.WriteAllBytesAsync(MsaPath, Dpapi.Protect(payload));
            _logService.Info("Token de Microsoft renovado en segundo plano");
        }
        catch (Exception ex)
        {
            _logService.Warning($"Renovación de token falló: {ex.Message}");
        }
    }

    public async Task LogoutAsync()
    {
        try
        {
            if (File.Exists(OfflinePath)) File.Delete(OfflinePath);
            if (File.Exists(MsaPath)) File.Delete(MsaPath);
            await SaveAccountAsync(nameof(AccountKind.None), "Jugador", string.Empty);
        }
        catch (Exception ex)
        {
            _logService.Warning($"Logout incompleto: {ex.Message}");
        }

        CurrentSession = null;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveAccountAsync(string kind, string name, string uuid)
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.AccountType = kind;
        settings.UserName = name;
        settings.OfflineUuid = uuid;
        await _settingsService.SaveSettingsAsync(settings);
    }

    private GameSession? LoadOfflineSession()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(OfflinePath));
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var uuid = doc.RootElement.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";
            if (!IAuthService.IsValidOfflineName(name)) return null;
            return new GameSession
            {
                Kind = AccountKind.Offline,
                Name = name,
                Uuid = string.IsNullOrWhiteSpace(uuid) ? IAuthService.OfflineUuidFor(name) : uuid,
                AccessToken = "0",
                UserType = "legacy"
            };
        }
        catch
        {
            return null;
        }
    }

    private GameSession? LoadMsaSession()
    {
        try
        {
            var raw = Dpapi.Unprotect(File.ReadAllBytes(MsaPath));
            using var doc = JsonDocument.Parse(raw);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var uuid = doc.RootElement.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name)) return null;
            return new GameSession
            {
                Kind = AccountKind.Microsoft,
                Name = name,
                Uuid = uuid,
                AccessToken = "0",
                UserType = "msa",
                ExpiresAtUtc = DateTimeOffset.UtcNow // fuerza renovación al restaurar
            };
        }
        catch
        {
            return null;
        }
    }
}
