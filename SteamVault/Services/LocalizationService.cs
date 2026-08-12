using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamVault.Services;

/// <summary>Application language state. English is the default; Russian is opt-in.</summary>
public sealed partial class LocalizationService : ObservableObject
{
    private readonly AppSettings _settings;

    /// <summary>Latest instance — models (e.g. groups) can resolve Expand/Collapse labels.</summary>
    public static LocalizationService? Current { get; private set; }

    public LocalizationService(AppSettings settings)
    {
        _settings = settings;
        Current = this;
        ApplyCulture();
    }

    public bool IsRussian => string.Equals(_settings.Language, "ru", StringComparison.OrdinalIgnoreCase);
    public string LanguageCode => IsRussian ? "ru" : "en";

    public void SetLanguage(string language)
    {
        _settings.Language = string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        _settings.HasSelectedLanguage = true;
        _settings.Save();
        ApplyCulture();
        OnPropertyChanged(nameof(IsRussian));
        OnPropertyChanged(nameof(LanguageCode));
    }

    public string T(string english, string russian) => IsRussian ? russian : english;

    private void ApplyCulture()
    {
        var culture = CultureInfo.GetCultureInfo(IsRussian ? "ru-RU" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
