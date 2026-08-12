using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamVault.Models;

/// <summary>
/// One account inside the group editor. Membership is stored on the account itself
/// (<see cref="SteamAccount.GroupName"/>), so this row is a staging checkbox: the user ticks
/// freely and nothing moves until they save. That keeps a half-built group from re-routing
/// live transfers mid-edit.
/// </summary>
public partial class GroupMemberRow : ObservableObject
{
    public required SteamAccount Account { get; init; }

    [ObservableProperty] private bool _isMember;

    public string Login => Account.Login;
    public string Initial => Account.Initial;
    public string? AvatarUrl => Account.AvatarUrl;
    public bool HasAvatar => Account.HasAvatar;

    /// <summary>Group this account belongs to right now, when it is not the one being edited.</summary>
    public string? OtherGroup { get; init; }
    public bool HasOtherGroup => !string.IsNullOrWhiteSpace(OtherGroup);

    public string ValueText => Account.InventoryValue > 0 ? $"${Account.InventoryValue:0.00}" : "—";
}
