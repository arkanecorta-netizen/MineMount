using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Models;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace MineMount.ViewModels;

public partial class SeriesViewModel : ObservableObject
{
    private readonly ISeriesService _seriesService;
    private readonly INavigationService _navigationService;
    private readonly ILogService _logService;

    [ObservableProperty]
    private ObservableCollection<SeriesInfo> _series = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public SeriesViewModel(
        ISeriesService seriesService,
        INavigationService navigationService,
        ILogService logService)
    {
        _seriesService = seriesService;
        _navigationService = navigationService;
        _logService = logService;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            IsLoading = true;
            StatusText = "Cargando series...";

            var list = await _seriesService.GetSeriesAsync();
            Series = new ObservableCollection<SeriesInfo>(list);

            StatusText = $"{list.Count} series disponibles";
        }
        catch (Exception ex)
        {
            _logService.Error("Failed to load series", ex);
            StatusText = "Error al cargar las series";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void OpenSeries(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        _navigationService.NavigateTo(NavigationPage.SeriesDetail, id);
    }
}
