using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamVault.Models;

/// <summary>
/// Per-account hardware profile (inspired by luminary-cloud/steam-account-manager).
/// Educational: used for profile isolation when launching Steam client.
/// </summary>
public partial class HwidProfile : ObservableObject
{
    [ObservableProperty] private string _machineGuid = "";
    [ObservableProperty] private string _macAddress = "";
    [ObservableProperty] private string _diskSerial = "";
    [ObservableProperty] private string _pcName = "";
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private string _gpuVendorId = "";
    [ObservableProperty] private string _gpuDeviceId = "";
    [ObservableProperty] private int _gpuVramMb;
    [ObservableProperty] private string _boardModel = "";
    [ObservableProperty] private string _boardManufacturer = "";
    [ObservableProperty] private int _ramMb;
    [ObservableProperty] private int _monitorWidth;
    [ObservableProperty] private int _monitorHeight;
    [ObservableProperty] private int _monitorRefresh;
    [ObservableProperty] private int _storageSsds;
    [ObservableProperty] private string _storageSsdSize = "";
    [ObservableProperty] private int _storageHdds;
    [ObservableProperty] private string _storageHddSize = "";
    [ObservableProperty] private string _soundCard = "";
    [ObservableProperty] private string _displayModel = "";
    [ObservableProperty] private bool _enabled = true;

    // Component mask (which fields to apply when spoofing)
    [ObservableProperty] private bool _spoofMachineGuid = true;
    [ObservableProperty] private bool _spoofMac = true;
    [ObservableProperty] private bool _spoofDisk = true;
    [ObservableProperty] private bool _spoofPcName = true;
    [ObservableProperty] private bool _spoofGpu = true;
    [ObservableProperty] private bool _spoofBoard = true;
    [ObservableProperty] private bool _spoofRam = true;
    [ObservableProperty] private bool _spoofMonitor = true;
    [ObservableProperty] private bool _spoofStorage = true;
    [ObservableProperty] private bool _spoofSound = true;
}

public sealed class HwidCompareRow
{
    public string Component { get; init; } = "";
    public string Real { get; init; } = "";
    public string Spoofed { get; init; } = "";
}
