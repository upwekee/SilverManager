using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SteamVault.Models;
using SteamVault.ViewModels;
using System.ComponentModel;

namespace SteamVault.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged -= OnVmChanged;
                vm.PropertyChanged += OnVmChanged;
                UpdateRail(vm.ShellPage);
            }
        };
        Opened += (_, _) =>
        {
            Opacity = 1;
            if (DataContext is MainViewModel vm)
                UpdateRail(vm.ShellPage);
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShellPage) or null)
            UpdateRail(Vm?.ShellPage ?? 0);
    }

    private void UpdateRail(int page)
    {
        var rails = new (Button? btn, int page)[]
        {
            (RailHome, 0),
            (RailInv, 1),
            (RailTransfer, 2),
            (RailConf, 10),
            (RailIn, 11),
            (RailReview, 12),
            (RailAudit, 15),
            (RailStats, 16),
            (RailProxy, 17),
            (RailGroups, 18),
            (RailAccounts, 19),
            (RailHwid, 21),
            (RailAutoFarm, 22),
            // Settings is bottom-pinned; page 20 has no rail highlight above.
        };

        foreach (var (btn, p) in rails)
            ApplyRail(btn, p == page);
    }

    /// <summary>Theme owns the colours — the view only flips the class.</summary>
    private static void ApplyRail(Button? btn, bool on)
    {
        if (btn == null) return;
        btn.Classes.Set("active", on);
        btn.Focusable = false;
    }

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: InventoryItem item })
            Vm?.ToggleItemCommand.Execute(item);
    }

    private void OnAccountPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not SteamAccount acc) return;

        var props = e.GetCurrentPoint(border).Properties;
        // Right-click opens ContextMenu — never treat as select/toggle.
        if (props.IsRightButtonPressed || props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
            return;
        if (!props.IsLeftButtonPressed && props.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        if (e.ClickCount >= 2)
        {
            Vm?.OpenAccountPanelCommand.Execute(acc);
            e.Handled = true;
            return;
        }

        Vm?.ToggleAccountCommand.Execute(acc);
    }

    /// <summary>
    /// Context menus live in a popup visual tree — Command bindings to Window often break.
    /// Resolve the row account from PlacementTarget and invoke the VM directly.
    /// </summary>
    private static SteamAccount? AccountFromMenu(object? sender)
    {
        if (sender is not MenuItem item) return null;
        if (item.DataContext is SteamAccount direct) return direct;

        // Avalonia: walk logical parents until ContextMenu
        StyledElement? cur = item;
        while (cur != null)
        {
            if (cur is ContextMenu { PlacementTarget: Control { DataContext: SteamAccount a } })
                return a;
            cur = cur.Parent;
        }
        return null;
    }

    private void OnCtxSettings(object? sender, RoutedEventArgs e)
    {
        var acc = AccountFromMenu(sender);
        if (acc != null) Vm?.OpenAccountPanelCommand.Execute(acc);
    }

    private void OnCtxWarehouse(object? sender, RoutedEventArgs e)
    {
        var acc = AccountFromMenu(sender);
        if (acc != null) Vm?.MarkWarehouseCommand.Execute(acc);
    }

    private void OnCtxCopy2Fa(object? sender, RoutedEventArgs e)
    {
        var acc = AccountFromMenu(sender);
        if (acc != null) Vm?.CopyGuardCodeCommand.Execute(acc);
    }

    private void OnCtxDelete(object? sender, RoutedEventArgs e)
    {
        var acc = AccountFromMenu(sender);
        if (acc != null) Vm?.DeleteAccountContextCommand.Execute(acc);
    }

    private void OnOverlayDismissImport(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender)
            Vm?.CloseImportCommand.Execute(null);
    }
}
