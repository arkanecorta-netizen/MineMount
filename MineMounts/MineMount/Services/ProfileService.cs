using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MineMount.Services;

public interface IProfileService
{
    Task<List<Models.UserProfile>> GetProfilesAsync();
    Task CreateProfileAsync(Models.UserProfile profile);
    Task DeleteProfileAsync(Models.UserProfile profile);
    Task UpdateProfileAsync(Models.UserProfile profile);
}

public class ProfileService : IProfileService
{
    private readonly List<Models.UserProfile> _profiles = new();

    public Task<List<Models.UserProfile>> GetProfilesAsync()
    {
        return Task.FromResult(_profiles);
    }

    public Task CreateProfileAsync(Models.UserProfile profile)
    {
        _profiles.Add(profile);
        return Task.CompletedTask;
    }

    public Task DeleteProfileAsync(Models.UserProfile profile)
    {
        _profiles.Remove(profile);
        return Task.CompletedTask;
    }

    public Task UpdateProfileAsync(Models.UserProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            _profiles[index] = profile;
        }
        return Task.CompletedTask;
    }
}