using System;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IAuthService
{
    bool IsAuthenticated { get; }
    Models.UserInfo? CurrentUser { get; }
    Task<bool> LoginAsync(string email, string password);
    Task LogoutAsync();
}

public class AuthService : IAuthService
{
    private Models.UserInfo? _currentUser;

    public bool IsAuthenticated => _currentUser != null;
    public Models.UserInfo? CurrentUser => _currentUser;

    public Task<bool> LoginAsync(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Task.FromResult(false);

        _currentUser = new Models.UserInfo
        {
            Name = email.Split('@')[0],
            Email = email,
            LastLogin = DateTime.Now
        };

        return Task.FromResult(true);
    }

    public Task LogoutAsync()
    {
        _currentUser = null;
        return Task.CompletedTask;
    }
}