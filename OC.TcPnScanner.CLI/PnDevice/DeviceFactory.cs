using OC.TcPnScanner.CLI.Packets;

namespace OC.TcPnScanner.CLI.PnDevice;

internal static class DeviceFactory
{
    internal static Device CreateFromPacket(ProfinetDcpIdentRequestPacket dcpIdentRequestPacket)
    {
        Guard.ThrowIfNull(dcpIdentRequestPacket.FakePhysicalAddress);
        return new Device(dcpIdentRequestPacket.FakePhysicalAddress, dcpIdentRequestPacket.NameOfStation);
    }
}
