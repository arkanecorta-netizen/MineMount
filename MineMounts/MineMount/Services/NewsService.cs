using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface INewsService
{
    Task<List<ViewModels.NewsItem>> GetNewsAsync();
}

public class NewsService : INewsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILogService _logService;
    private readonly string _newsPath;
    private readonly string _feedUrlPath;

    public NewsService(ILogService logService)
    {
        _logService = logService;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount");
        Directory.CreateDirectory(dir);
        _newsPath = Path.Combine(dir, "news.json");
        _feedUrlPath = Path.Combine(dir, "newsfeed.url");
    }

    public async Task<List<ViewModels.NewsItem>> GetNewsAsync()
    {
        try
        {
            await EnsureLocalNewsFileAsync();

            // Soporte futuro para servidor externo: si existe newsfeed.url con una
            // URL http(s), se intenta descargar el feed remoto; si falla, se usa el JSON local.
            var remoteUrl = await ReadFeedUrlAsync();
            if (!string.IsNullOrWhiteSpace(remoteUrl) &&
                (remoteUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 remoteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var json = await http.GetStringAsync(remoteUrl);
                    var remote = JsonSerializer.Deserialize<List<ViewModels.NewsItem>>(json, JsonOptions);
                    if (remote is { Count: > 0 })
                    {
                        return remote;
                    }
                }
                catch (Exception ex)
                {
                    _logService.Warning($"No se pudo obtener el feed remoto, se usa el JSON local: {ex.Message}");
                }
            }

            var localJson = await File.ReadAllTextAsync(_newsPath);
            var news = JsonSerializer.Deserialize<List<ViewModels.NewsItem>>(localJson, JsonOptions);
            return news ?? new List<ViewModels.NewsItem>();
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load news", ex);
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
                Title = "NSE6 ya está disponible",
                Description = "Conocé las novedades de la nueva serie.",
                Date = DateTime.Now.AddDays(-1),
                ImageUrl = string.Empty,
                Category = "Serie"
            },
            new()
            {
                Title = "Bienvenido a MineMount",
                Description = "El launcher moderno para tus series. Explora, instalá y personaliza tu experiencia.",
                Date = DateTime.Now.AddDays(-3),
                ImageUrl = string.Empty,
                Category = "Anuncio"
            },
            new()
            {
                Title = "Sistema de series renovado",
                Description = "Ahora podés instalar, actualizar y reparar series completas con muchos mods y recursos.",
                Date = DateTime.Now.AddDays(-5),
                ImageUrl = string.Empty,
                Category = "Actualización"
            },
            new()
            {
                Title = "Próximamente nuevas series",
                Description = "Estamos preparando más series para que puedas disfrutar desde un solo lugar.",
                Date = DateTime.Now.AddDays(-7),
                ImageUrl = string.Empty,
                Category = "Comunidad"
            }
        };

        var json = JsonSerializer.Serialize(defaults, JsonOptions);
        await File.WriteAllTextAsync(_newsPath, json);
    }

    private async Task<string> ReadFeedUrlAsync()
    {
        try
        {
            if (File.Exists(_feedUrlPath))
            {
                return (await File.ReadAllTextAsync(_feedUrlPath)).Trim();
            }
        }
        catch
        {
            // Ignorar errores de lectura de la URL del feed
        }

        return string.Empty;
    }
}
