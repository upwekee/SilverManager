namespace SteamVault.Models;

/// <summary>One selectable warehouse destination for a group's ComboBox.</summary>
public sealed class GroupWarehouseOption
{
    public string AccountId { get; init; } = "";
    public string Login { get; init; } = "";
    public string Label { get; init; } = "";
    public bool HasTradeUrl { get; init; }

    public override string ToString() => Label;
}
