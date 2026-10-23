using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace OC.TcPnScanner.CLI;

internal interface ICaptureDeviceProxy : IPcapDevice
{
    PcapDevice PcapDevice { get; init; }
    void SendPacketHandler(Packet p);

    event EventHandler<Packet>? OnPacketSend;
}
