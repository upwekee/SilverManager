using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SteamVault.Services;
using SteamVault.ViewModels;
using SteamVault.Views;

namespace SteamVault;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var settings = AppSettings.Load();
                var vm = new MainViewModel(settings);
                var mainWindow = new MainWindow { DataContext = vm };
                desktop.MainWindow = mainWindow;

                // ShowDialog requires an actual owner. The main window must be assigned first.
                if (!settings.HasSelectedLanguage)
                {
                    mainWindow.Opened += async (_, _) =>
                    {
                        var languageWindow = new LanguageSelectionWindow();
                        await languageWindow.ShowDialog(mainWindow);
                        settings.Language = languageWindow.SelectedLanguage ?? "en";
                        settings.HasSelectedLanguage = true;
                        settings.Save();
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                desktop.MainWindow = CreateErrorWindow(ex);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window CreateErrorWindow(Exception ex)
    {
        return new Window
        {
            Title = "SilverManager — startup error",
            Width = 720,
            Height = 420,
            Background = Brushes.Black,
            Content = new ScrollViewer
            {
                Padding = new Thickness(20),
                Content = new TextBlock
                {
                    Text = "Startup failed:\n\n" + ex,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.OrangeRed,
                    FontFamily = new FontFamily("Consolas")
                }
            }
        };
    }
}
