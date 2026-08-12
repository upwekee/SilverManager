using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace SteamVault.Services;

public static class FileDialogs
{
    private static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    public static async Task<string?> OpenFileAsync(string title, params (string name, string[] exts)[] filters)
    {
        var top = GetTopLevel();
        if (top == null) return null;

        var fileTypes = filters.Select(f => new FilePickerFileType(f.name)
        {
            Patterns = f.exts.Select(e => e.StartsWith("*.") ? e : e == "*" ? "*" : $"*.{e}").ToArray()
        }).ToList();

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> OpenFolderAsync(string title)
    {
        var top = GetTopLevel();
        if (top == null) return null;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
