using System.Diagnostics;
using dsian.TcPnScanner.CLI.PnDevice;
using Microsoft.Extensions.Logging;
using PacketDotNet;
using System.Text;
using SharpPcap;

namespace dsian.TcPnScanner.CLI.Packets;

internal class PacketHandler : IPacketHandler
{
    private readonly ICaptureDeviceProxy _captureDevice;
    private readonly IDeviceStore _deviceStore;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, string> _deviceIds;
    private readonly Stopwatch _stopwatch = new();

    public PacketHandler(ICaptureDeviceProxy captureDevice, IDeviceStore deviceStore, Aml.AmlFile amlFile, ILogger? logger = null)
    {
        Guard.ThrowIfNull(captureDevice);
        _captureDevice = captureDevice;
        Guard.ThrowIfNull(deviceStore);
        _deviceStore = deviceStore;
        _logger = logger;
        _logger?.BeginScope(this);
        _deviceIds = amlFile.DeviceIdsByName;
        TrackActivity();
    }

    private void TrackActivity()
    {
        Task.Run(async () =>
        {
            var captureHasStopped = false;
            _captureDevice.PcapDevice.OnCaptureStopped += PcapDeviceOnOnCaptureStopped;

            while (_stopwatch.ElapsedMilliseconds < 10000 && !captureHasStopped)
            {
                await Task.Delay(100);
            }

            if (!captureHasStopped)
            {
                _captureDevice.PcapDevice.StopCapture();
            }

            return;

            void PcapDeviceOnOnCaptureStopped(object sender, CaptureStoppedEventStatus status)
            {
                captureHasStopped = true;
            }
        });
    }

    public void HandleEthernetPacket(EthernetPacket ethPacket)
    {
        try
        {
            if (ProfinetDcpIdentRequestPacket.TryParse(ethPacket, out var pnIdentPacket, _logger))
            {
                if (_deviceStore.TryAddDevice(DeviceFactory.CreateFromPacket(pnIdentPacket)))
                {
                    SendProfinetDcpIdentResponsePacket(pnIdentPacket);
                    _stopwatch.Restart();
                }
            }
            else if (ProfinetDcpSetIPRequestPacket.TryParse(ethPacket, out var pnSetIpPacket))
            {
                _deviceStore.TryUpdateIpAddress(pnSetIpPacket);
                SendProfinetDcpSetIpResponsePacket(pnSetIpPacket);
                _stopwatch.Restart();
            }
            else if (ethPacket.Type == EthernetType.Arp)
            {
                SendArpResponsePacket(ethPacket);
                _stopwatch.Restart();
            }
            else if (ProfinetIoConnectRequestPacket.TryParse(ethPacket, out var pnIoConReqPacket, _logger))
            {
                UpdateDevicePnIoConReqPacket(pnIoConReqPacket);
                _stopwatch.Restart();
            }
            else
            {
                _logger?.LogWarning("Ignoring packet: {EthernetPacket}", ethPacket);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error occured on packet {EthernetPacket}", ethPacket);
        }
    }

    private void UpdateDevicePnIoConReqPacket(ProfinetIoConnectRequestPacket pnIoConReqPacket)
    {
        if (!_deviceStore.UpdatePnIoConnectRequestPacket(pnIoConReqPacket))
        {
            _logger?.LogWarning("Couldn't find matching device for {PnIoConReqPacket}", pnIoConReqPacket);
        }
    }

    private void SendProfinetDcpIdentResponsePacket(ProfinetDcpIdentRequestPacket pnPacket)
    {

        var ethPacket = (EthernetPacket)pnPacket.SourcePacket!;
        var responsePacket = new EthernetPacket(pnPacket.FakePhysicalAddress, ethPacket.SourceHardwareAddress, EthernetType.Profinet);
        var data = new List<byte>();

        data.AddRange(BitConverter.GetBytes(ProfinetDcpIdentRequestPacket.FRAME_ID_RES).Reverse());
        data.Add((byte)PnDcpServiceId.Identify);
        data.Add((byte)PnDcpServiceType.ResponseSuccess);
        data.AddRange(BitConverter.GetBytes(pnPacket.Xid));
        data.Add(0x0);
        data.Add(0x0);

        var blockDevName = BlockDeviceNameOfStation(pnPacket);
        var blockIpIp = BlockIpIp();
        var devId = VendorAndDeviceId(pnPacket);
        var blocksLength = (ushort) (blockDevName.Length + blockIpIp.Length + devId.Length);

        data.AddRange(BitConverter.GetBytes(blocksLength).Reverse());
        data.AddRange(blockDevName);
        data.AddRange(devId);
        data.AddRange(blockIpIp);

        responsePacket.PayloadData = data.ToArray();
        _captureDevice.SendPacketHandler(responsePacket);
    }

    private byte[] VendorAndDeviceId(ProfinetDcpIdentRequestPacket pnPacket)
    {
        if (!_deviceIds.TryGetValue(pnPacket.NameOfStation, out var id)) return [];

        try
        {
            var value = Convert.ToUInt32(id.Replace("0x", ""), 16);
            var vendorId = (ushort)(value >> 16);
            var deviceId = (ushort)value;

            var data = new List<byte>
            {
                0x02, // Option: DeviceProperties
                0x03, // Suboption: Device ID
                0x00, // DcpBlockLength
                0x06,
                0x00, // Reserved
                0x00
            };

            data.AddRange(BitConverter.GetBytes(vendorId).Reverse());
            data.AddRange(BitConverter.GetBytes(deviceId).Reverse());
            return [.. data];
        }
        catch
        {
            return [];
        }
    }

    private static byte[] BlockDeviceNameOfStation(ProfinetDcpIdentRequestPacket pnPacket)
    {
        var data = new List<byte>
        {
            0x2, // Option: DeviceProperties
            0x2 // Suboption: Name of Station
        };
        var lenName = pnPacket.NameOfStation.Length;
        data.AddRange(BitConverter.GetBytes((ushort)(lenName + 2)).Reverse()); // DcpBlockLength
        data.AddRange(BitConverter.GetBytes((ushort)0x0)); //reserved
        data.AddRange(Encoding.UTF8.GetBytes(pnPacket.NameOfStation));

        if (lenName % 2 != 0)
        {
            data.Add(0x0); //Padding byte
        }

        return [.. data];
    }

    private static byte[] BlockIpIp()
    {
        var data = new List<byte>
        {
            0x1, // Option: IP
            0x2 // Suboption: IP Parameter
        };
        data.AddRange(BitConverter.GetBytes((ushort)14).Reverse()); // DcpBlockLength
        data.AddRange(BitConverter.GetBytes((ushort)0x0000).Reverse()); // IP not set
        data.AddRange(BitConverter.GetBytes((uint)0x00000000).Reverse()); // IP Address
        data.AddRange(BitConverter.GetBytes((uint)0x00000000).Reverse()); // Subnet Mask
        data.AddRange(BitConverter.GetBytes((uint)0x00000000).Reverse()); // Gateway
        return [.. data];
    }

    private void SendProfinetDcpSetIpResponsePacket(ProfinetDcpSetIPRequestPacket pnPacket)
    {
        var ethPacket = (EthernetPacket)pnPacket.SourcePacket!;
        var responsePacket = new EthernetPacket(ethPacket.DestinationHardwareAddress, ethPacket.SourceHardwareAddress, EthernetType.Profinet);
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(ProfinetDcpSetIPRequestPacket.FRAME_ID_RES).Reverse());
        data.Add((byte)PnDcpServiceId.Set);
        data.Add((byte)PnDcpServiceType.ResponseSuccess);
        data.AddRange(BitConverter.GetBytes(pnPacket.Xid));
        data.Add(0x0);
        data.Add(0x0);

        data.Add(0x0);
        data.Add(0x8);  // DcpDataLength

        data.Add(0x5);  // Option: Control
        data.Add(0x4);  // Suboption: Response
        data.Add(0x0);
        data.Add(0x3);  // DcpBlockLength
        data.Add(0x1);  // Option: IP
        data.Add(0x2);  // Suboption: IP Parameter
        data.Add(0x0);  // BlockError

        data.Add(0x0);  // Padding 1 Byte

        responsePacket.PayloadData = [.. data];
        _captureDevice.SendPacketHandler(responsePacket);
    }

    private void SendArpResponsePacket(EthernetPacket ethPacket)
    {
        var arpPacket = (ArpPacket)ethPacket.PayloadPacket;

        if (!_deviceStore.TryFindDevice(x => x.IpAddress.Equals(arpPacket.TargetProtocolAddress), out var foundDevice))
        {
            return;
        }

        var responsePacket = new EthernetPacket(foundDevice.PhysicalAddress, ethPacket.SourceHardwareAddress, EthernetType.Arp);

        var data = new List<byte>();

        data.AddRange(BitConverter.GetBytes((ushort)1).Reverse());  // HW Type: Ethernet
        data.AddRange(BitConverter.GetBytes((ushort)0x800).Reverse());  // Prtocol Type: IPv4
        data.Add(0x6);  // HW Size: 6
        data.Add(0x4);  // Protocol Size: 4
        data.AddRange(BitConverter.GetBytes((ushort)2).Reverse());  // Opcode: reply
        data.AddRange(foundDevice.PhysicalAddress.GetAddressBytes());
        data.AddRange(arpPacket.TargetProtocolAddress.GetAddressBytes());
        data.AddRange(arpPacket.SenderHardwareAddress.GetAddressBytes());
        data.AddRange(arpPacket.SenderProtocolAddress.GetAddressBytes());

        responsePacket.PayloadData = [.. data];
        _captureDevice.SendPacketHandler(responsePacket);
    }
}
