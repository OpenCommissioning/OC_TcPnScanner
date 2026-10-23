using PacketDotNet;

namespace OC.TcPnScanner.CLI.Packets;

public interface IPacketHandler
{
    public void HandleEthernetPacket(EthernetPacket ethPacket);
}
