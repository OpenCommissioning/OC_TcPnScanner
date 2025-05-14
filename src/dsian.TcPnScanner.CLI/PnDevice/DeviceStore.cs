using dsian.TcPnScanner.CLI.Packets;
using PacketDotNet;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;

namespace dsian.TcPnScanner.CLI.PnDevice;

internal class DeviceStore : IDeviceStore
{
    private readonly Dictionary<PhysicalAddress, Device> _devices = [];

    public int Count => _devices.Count;

    public bool TryAddDevice(Device device)
    {
        var foundDevice = _devices.FirstOrDefault(x => x.Value.NameOfStation == device.NameOfStation).Value;
        return foundDevice is null && _devices.TryAdd(device.PhysicalAddress, device);
    }

    public bool TryUpdateIpAddress(ProfinetDcpSetIPRequestPacket dcpSetIpReqPacket)
    {
        if (dcpSetIpReqPacket.SourcePacket is not EthernetPacket ethernetPacket)
        {
            return false;
        }

        if (!_devices.TryGetValue(ethernetPacket.DestinationHardwareAddress, out var device))
        {
            return false;
        }

        device.IpAddress = dcpSetIpReqPacket.IpAddress;
        return true;
    }

    public bool TryFindDevice(Func<Device, bool> predicate, [MaybeNullWhen(false)] out Device device)
    {
        device = _devices.Values.FirstOrDefault(predicate);
        return device is not null;
    }

    public bool UpdatePnIoConnectRequestPacket(ProfinetIoConnectRequestPacket profinetIoConnectRequestPacket)
    {
        var devicePhysicalAddress = _devices.Where(x => x.Value.IpAddress.Equals(profinetIoConnectRequestPacket.IPAddress))
                                    .Select(x => x.Key).FirstOrDefault();

        if (devicePhysicalAddress is null)
        {
            return false;
        }

        var device = _devices[devicePhysicalAddress];

        if (device.PnIoConnectRequestPacket is not null)
        {
            device.PnIoConnectRequestPacket.FragmentedData.AddRange(profinetIoConnectRequestPacket.FragmentedData);
            if (profinetIoConnectRequestPacket.LastFragment)
            {
                device.PnIoConnectRequestPacket.UpdateFromFragmentedData();
            }
            return true;
        }

        device.PnIoConnectRequestPacket = profinetIoConnectRequestPacket;
        return true;
    }

    public IEnumerable<Device> GetDevices()
    {
        return _devices.Values;
    }

    public string GetProfinetDeviceName()
    {
        const string defaultName = "dsian.TcPnScanner (Profinet Device)";
        if (_devices.Count == 0)
        {
            return defaultName;
        }

        var pnIoConReqPacket = _devices.First().Value.PnIoConnectRequestPacket;
        return pnIoConReqPacket is null ? defaultName : $"{pnIoConReqPacket.ARBlockReqHeader.CMInitiatorStationName} (Profinet Device)";
    }
}
