using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Models;
using MineMount.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace MineMount.ViewModels;

public partial class SeriesViewModel : ObservableObject
{
    private readonly ISeriesService _seriesService;
    private readonly ILogService _logService;

    private string? _lastSelectedId;

    [ObservableProperty]
    private ObservableCollection<SeriesInfo> _series = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeries))]
    private SeriesInfo? _selectedSeries;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasSeries => Series.Count > 0;

    public SeriesDetailViewModel Detail { get; }

    public SeriesViewModel(
        ISeriesService seriesService,
        SeriesDetailViewModel detailViewModel,
        ILogService logService)
    {
        _seriesService = seriesService;
        _logService = logService;
        Detail = detailViewModel;
        Detail.SeriesChanged += OnDetailSeriesChanged;
        _ = RefreshAsync();
    }

    partial void OnSelectedSeriesChanged(SeriesInfo? value)
    {
        if (value != null)
        {
            _lastSelectedId = value.Id;
            _ = Detail.LoadAsync(value.Id);
        }
    }

    private void OnDetailSeriesChanged()
    {
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
            OnPropertyChanged(nameof(HasSeries));

            StatusText = list.Count > 0
                ? $"{list.Count} series disponibles"
                : "No se encontraron series";

            var toSelect = list.FirstOrDefault(s => string.Equals(s.Id, _lastSelectedId, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault();

            if (toSelect != null)
            {
                if (SelectedSeries != null && string.Equals(SelectedSeries.Id, toSelect.Id, StringComparison.OrdinalIgnoreCase))
                {
                    await Detail.LoadAsync(toSelect.Id);
                }
                else
                {
                    SelectedSeries = toSelect;
                }
            }
            else
            {
                SelectedSeries = null;
                await Detail.LoadAsync(null);
            }
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
    private void SelectSeries(SeriesInfo? series)
    {
        if (series != null) SelectedSeries = series;
    }

    // Botones contextuales de las tarjetas (grilla): operan sobre la serie tocada.
    [RelayCommand]
    private async Task PlaySeriesAsync(SeriesInfo? series)
    {
        if (series == null) return;
        SelectedSeries = series;
        await Detail.LoadAsync(series.Id);
        if (Detail.PlayCommand.CanExecute(null)) Detail.PlayCommand.Execute(null);
    }

    [RelayCommand]
    private async Task InstallSeriesAsync(SeriesInfo? series)
    {
        if (series == null) return;
        SelectedSeries = series;
        await Detail.LoadAsync(series.Id);
        if (Detail.InstallCommand.CanExecute(null)) Detail.InstallCommand.Execute(null);
    }

    [RelayCommand]
    private async Task UpdateSeriesAsync(SeriesInfo? series)
    {
        if (series == null) return;
        SelectedSeries = series;
        await Detail.LoadAsync(series.Id);
        if (Detail.UpdateCommand.CanExecute(null)) Detail.UpdateCommand.Execute(null);
    }

    [RelayCommand]
    private async Task RepairSeriesAsync(SeriesInfo? series)
    {
        if (series == null) return;
        SelectedSeries = series;
        await Detail.LoadAsync(series.Id);
        if (Detail.RepairCommand.CanExecute(null)) Detail.RepairCommand.Execute(null);
    }
}
