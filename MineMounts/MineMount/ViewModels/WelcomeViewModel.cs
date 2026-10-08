using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineMount.Services;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MineMount.ViewModels;

/// <summary>
/// Primera pantalla (solo sin cuenta guardada): Microsoft por código de
/// dispositivo o modo offline con validación en vivo. Recuerda la elección.
/// </summary>
public partial class WelcomeViewModel : ObservableObject
{
    private readonly IAuthService _authService;
    private readonly IMicrosoftAuthService _msAuth;
    private readonly ILogService _logService;

    public TaskCompletionSource<GameSession?> Done { get; } = new();

    [ObservableProperty]
    private int _stage;

    [ObservableProperty]
    private string _offlineName = string.Empty;

    [ObservableProperty]
    private string _offlineNameError = string.Empty;

    [ObservableProperty]
    private string _authUrl = string.Empty;

    [ObservableProperty]
    private string _pastedCode = string.Empty;

    [ObservableProperty]
    private bool _isWaitingMicrosoft;

    [ObservableProperty]
    private string _microsoftError = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsOfflineNameValid => IAuthService.IsValidOfflineName(OfflineName);

    public WelcomeViewModel(IAuthService authService, IMicrosoftAuthService msAuth, ILogService logService)
    {
        _authService = authService;
        _msAuth = msAuth;
        _logService = logService;
    }

    partial void OnOfflineNameChanged(string value)
    {
        value = (value ?? string.Empty).Trim();
        OfflineNameError = value.Length == 0
            ? string.Empty
            : IAuthService.IsValidOfflineName(value)
                ? string.Empty
                : Loc.T("S.Welcome.NameHint");
        OnPropertyChanged(nameof(IsOfflineNameValid));
    }

    [RelayCommand]
    private async Task ContinueOfflineAsync()
    {
        if (!IsOfflineNameValid || IsBusy) return;

        IsBusy = true;
        try
        {
            var session = await _authService.LoginOfflineAsync(OfflineName.Trim());
            Done.TrySetResult(session);
        }
        catch (Exception ex)
        {
            OfflineNameError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StartMicrosoft()
    {
        if (IsBusy) return;

        MicrosoftError = string.Empty;
        PastedCode = string.Empty;
        AuthUrl = _msAuth.BuildAuthorizeUrl();
        Stage = 1;

        // Abrir el navegador automáticamente (el código queda para pegar acá)
        OpenBrowser();
    }

    [RelayCommand]
    private async Task FinishMicrosoftAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(PastedCode)) return;

        IsBusy = true;
        IsWaitingMicrosoft = true;
        MicrosoftError = string.Empty;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var tokens = await _msAuth.ExchangeCodeAsync(PastedCode.Trim(), cts.Token);
            if (tokens == null)
            {
                MicrosoftError = Loc.T("S.Welcome.MSError") + " (código inválido o vencido)";
                return;
            }

            await FinishMicrosoftLoginAsync(tokens, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelado por el usuario
        }
        catch (Exception ex)
        {
            _logService.Warning($"Login Microsoft falló: {ex.Message}");
            MicrosoftError = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsWaitingMicrosoft = false;
        }
    }

    private async Task FinishMicrosoftLoginAsync(MsaTokens tokens, CancellationToken ct)
    {
        var (mcToken, _) = await _msAuth.LoginMinecraftAsync(tokens, ct);
        if (string.IsNullOrWhiteSpace(mcToken))
        {
            MicrosoftError = Loc.T("S.Welcome.MSError") + " (Xbox/Minecraft)";
            return;
        }

        var profile = await _msAuth.GetProfileAsync(mcToken, ct);
        if (profile == null || string.IsNullOrWhiteSpace(profile.Name))
        {
            // Token válido pero sin perfil = la cuenta no tiene Minecraft Java.
            MicrosoftError = Loc.T("S.Welcome.MSError") + " (esta cuenta no tiene Minecraft Java)";
            return;
        }

        var session = await _authService.LoginMicrosoftAsync(tokens, profile);
        if (session != null) Done.TrySetResult(session);
        else MicrosoftError = Loc.T("S.Welcome.MSError");
    }

    [RelayCommand]
    private void OpenBrowser()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(AuthUrl))
                Process.Start(new ProcessStartInfo(AuthUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logService.Warning($"No se pudo abrir el navegador: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelMicrosoft()
    {
        Stage = 0;
        MicrosoftError = string.Empty;
        PastedCode = string.Empty;
        IsWaitingMicrosoft = false;
        IsBusy = false;
    }

    public void CloseWithoutLogin() => Done.TrySetResult(null);
}
