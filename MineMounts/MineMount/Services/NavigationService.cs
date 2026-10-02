using System;

namespace MineMount.Services;

public class NavigationEventArgs : EventArgs
{
    public ViewModels.NavigationPage Page { get; }
    public object? Parameter { get; }

    public NavigationEventArgs(ViewModels.NavigationPage page, object? parameter)
    {
        Page = page;
        Parameter = parameter;
    }
}

public interface INavigationService
{
    event EventHandler<NavigationEventArgs>? Navigated;
    void NavigateTo(ViewModels.NavigationPage page, object? parameter = null);
    ViewModels.NavigationPage CurrentPage { get; }
    object? CurrentParameter { get; }
}

public class NavigationService : INavigationService
{
    private ViewModels.NavigationPage _currentPage = ViewModels.NavigationPage.Home;
    private object? _currentParameter;

    public event EventHandler<NavigationEventArgs>? Navigated;

    public ViewModels.NavigationPage CurrentPage => _currentPage;

    public object? CurrentParameter => _currentParameter;

    public void NavigateTo(ViewModels.NavigationPage page, object? parameter = null)
    {
        if (_currentPage == page && Equals(_currentParameter, parameter)) return;
        _currentPage = page;
        _currentParameter = parameter;
        Navigated?.Invoke(this, new NavigationEventArgs(page, parameter));
    }
}
