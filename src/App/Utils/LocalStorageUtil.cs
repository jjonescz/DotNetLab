using Blazored.LocalStorage;

namespace DotNetLab;

internal static class LocalStorageUtil
{
    extension(ILocalStorageService localStorage)
    {
        public async Task<T?> TryLoadOptionAsync<T>(string name, T? defaultValue = default)
        {
            return await localStorage.ContainKeyAsync(name)
                ? await localStorage.GetItemAsync<T>(name)
                : defaultValue;
        }
    }
}
