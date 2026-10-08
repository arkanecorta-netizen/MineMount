using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using MineMount.Models;

namespace MineMount.Services;

public interface INewsService
{
    Task<List<ViewModels.NewsItem>> GetNewsAsync();
    event EventHandler<List<ViewModels.NewsItem>>? OnNewsUpdated;
}

// Noticias remotas (news.json) con caché local:
//   1. mostrar inmediatamente las noticias cacheadas
//   2. comprobar si existe una versión nueva
//   3. actualizar silenciosamente
//   4. refrescar la UI
// Sin internet: se usa la caché o las noticias locales por defecto.
public class NewsService : INewsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILogService _logService;
    private readonly ISettingsService _settingsService;
    private readonly string _newsPath;
    private readonly string _cachePath;

    public NewsService(ILogService logService, ISettingsService settingsService)
    {
        _logService = logService;
        _settingsService = settingsService;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount");
        Directory.CreateDirectory(dir);
        _newsPath = Path.Combine(dir, "news.json");

        var cacheDir = Path.Combine(dir, "Cache");
        Directory.CreateDirectory(cacheDir);
        _cachePath = Path.Combine(cacheDir, "news.json");
    }

    public async Task<List<ViewModels.NewsItem>> GetNewsAsync()
    {
        await EnsureLocalNewsFileAsync();

        // 1. Caché primero: HOME nunca aparece vacío por internet lento
        var cached = await LoadLocalAsync(_cachePath);
        if (cached.Count == 0)
        {
            cached = await LoadLocalAsync(_newsPath);
        }

        // 2. Actualización silenciosa en segundo plano
        _ = RefreshRemoteAsync(cached);

        return cached;
    }

    private async Task RefreshRemoteAsync(List<ViewModels.NewsItem> current)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var feedUrl = settings.NewsFeedUrl?.Trim();

            if (string.IsNullOrWhiteSpace(feedUrl))
            {
                return;
            }

            if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                _logService.Warning($"URL de noticias inválida: {feedUrl}");
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            req.Headers.UserAgent.ParseAdd("MineMount-News/1.0");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                _logService.Warning($"Noticias remotas: HTTP {(int)resp.StatusCode}");
                return;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var remote = ParseRemote(json);

            if (remote.Count == 0)
            {
                return;
            }

            // 3. Guardar caché y 4. refrescar la UI
            await File.WriteAllTextAsync(_cachePath, json);
            _logService.Info($"Noticias remotas actualizadas: {remote.Count} entradas");

            OnNewsUpdated?.Invoke(this, remote);
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudieron actualizar las noticias: {ex.Message}");
        }
    }

    // La UI se suscribe para refrescar cuando llegan noticias nuevas
    public event EventHandler<List<ViewModels.NewsItem>>? OnNewsUpdated;

    private async Task<List<ViewModels.NewsItem>> LoadLocalAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<ViewModels.NewsItem>();

            var json = await File.ReadAllTextAsync(path);
            return ParseLocal(json);
        }
        catch
        {
            return new List<ViewModels.NewsItem>();
        }
    }

    private static List<ViewModels.NewsItem> ParseLocal(string json)
    {
        var list = JsonSerializer.Deserialize<List<ViewModels.NewsItem>>(json, JsonOptions) ?? new();
        list.RemoveAll(n => string.IsNullOrWhiteSpace(n.Title));
        return list;
    }

    // Formato remoto: { "news": [ { id, title, description, date, category, image, featured } ] }
    private static List<ViewModels.NewsItem> ParseRemote(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("news", out var news)
                || news.ValueKind != JsonValueKind.Array)
            {
                return new List<ViewModels.NewsItem>();
            }

            var list = new List<ViewModels.NewsItem>();
            foreach (var item in news.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(title)) continue;

                list.Add(new ViewModels.NewsItem
                {
                    Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    Title = title,
                    Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                    Date = item.TryGetProperty("date", out var dt) && DateTime.TryParse(dt.GetString(), out var date)
                        ? date
                        : DateTime.Now,
                    Category = item.TryGetProperty("category", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                    ImageUrl = item.TryGetProperty("image", out var img) ? img.GetString() ?? string.Empty : string.Empty,
                    Featured = item.TryGetProperty("featured", out var f) && f.ValueKind == JsonValueKind.True
                });
            }

            return list;
        }
        catch
        {
            return new List<ViewModels.NewsItem>();
        }
    }

    private async Task EnsureLocalNewsFileAsync()
    {
        if (File.Exists(_newsPath)) return;

        var defaults = new List<ViewModels.NewsItem>
        {
            new()
            {
                Title = Loc.T("S.News.Nse.Title"),
                Description = Loc.T("S.News.Nse.Desc"),
                Date = DateTime.Now.AddDays(-1),
                ImageUrl = string.Empty,
                Category = "Serie"
            },
            new()
            {
                Title = Loc.T("S.News.Welcome.Title"),
                Description = Loc.T("S.News.Welcome.Desc"),
                Date = DateTime.Now.AddDays(-3),
                ImageUrl = string.Empty,
                Category = "Anuncio"
            },
            new()
            {
                Title = Loc.T("S.News.System.Title"),
                Description = Loc.T("S.News.System.Desc"),
                Date = DateTime.Now.AddDays(-5),
                ImageUrl = string.Empty,
                Category = "Actualización"
            },
            new()
            {
                Title = Loc.T("S.News.Soon.Title"),
                Description = Loc.T("S.News.Soon.Desc"),
                Date = DateTime.Now.AddDays(-7),
                ImageUrl = string.Empty,
                Category = "Comunidad"
            }
        };

        var json = JsonSerializer.Serialize(defaults, JsonOptions);
        await File.WriteAllTextAsync(_newsPath, json);
    }
}
