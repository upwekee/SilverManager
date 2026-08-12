using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SteamVault.Views;

public partial class LanguageSelectionWindow : Window
{
    public string? SelectedLanguage { get; private set; }

    public LanguageSelectionWindow()
    {
        InitializeComponent();
    }

    private void ChooseEnglish(object? sender, RoutedEventArgs e)
    {
        SelectedLanguage = "en";
        Close();
    }

    private void ChooseRussian(object? sender, RoutedEventArgs e)
    {
        SelectedLanguage = "ru";
        Close();
    }
}
