namespace Tasktor.Contracts.Platform;

/// <summary>
/// U2 <c>BluetoothBatterySource</c> (E8, limited): a paired device's name and battery percent, or null when the device does
/// not report it. Nothing else is read from the device (D-S5).
/// </summary>
public sealed record DeviceBattery(string Name, int? Pct);
