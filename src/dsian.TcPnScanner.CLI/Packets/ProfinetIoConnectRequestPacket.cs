using Microsoft.Extensions.Logging;
using PacketDotNet;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace dsian.TcPnScanner.CLI.Packets;

internal sealed class ProfinetIoConnectRequestPacket
{
    public DceRpcRequestHeader DceRpcRequestHeader { get; set; }
    public uint ArgsMaximum { get; private set; }
    public uint ArgsLength { get; private set; }
    public uint ArrayMaximumCount { get; private set; }
    public uint ArrayOffset { get; private set; }
    public uint ArrayActualCount { get; private set; }
    public ARBlockReqHeader ARBlockReqHeader { get; private set; }
    public IOCRBlockReq IOCRBlockReqInput { get; private set; }
    public IOCRBlockReq IOCRBlockReqOutput { get; private set; }
    public List<ExpectedSubmoduleBlockReq> ExpectedSubmoduleBlockRequests { get; private set; } = new();
    public PhysicalAddress DestinationHardwareAddress { get; private set; } = PhysicalAddress.None;
    public IPAddress IPAddress { get; private set; } = IPAddress.None;
    public List<byte> FragmentedData { get; } = [];
    public bool LastFragment { get; private set; }
    private IPv4Packet? IPv4Packet { get; init; }
    private UdpPacket? UdpPacket { get; init; }

    private ProfinetIoConnectRequestPacket()
    {
    }

    public void UpdateFromFragmentedData()
    {
        if (UdpPacket is null)
        {
            return;
        }

        using MemoryStream memoryStream = new([.. FragmentedData]);
        using BinaryReader binaryReader = new(memoryStream);
        ParseArgsAndArray(binaryReader);

        while (binaryReader.BaseStream.Position < binaryReader.BaseStream.Length)
        {
            if (!BlockHeader.TryDeserializePeek(binaryReader, out var blockHeader))
            {
                throw new InvalidDataException($"Couldn't deserialize {nameof(BlockHeader)} at position {binaryReader.BaseStream.Position} from packet content:\n{UdpPacket.PrintHex()}");
            }

            switch (blockHeader.BlockType)
            {
                case BlockType.ARBLockReq:
                    if (!ARBlockReqHeader.TryDeserialize(binaryReader, out var arBlockReqHeader))
                    {
                        throw new InvalidDataException($"Couldn't deserialize {nameof(ARBlockReqHeader)} from packet content:\n{UdpPacket.PrintHex()}");
                    }
                    ARBlockReqHeader = arBlockReqHeader;
                    break;
                case BlockType.IOCRBlockReq:
                    ParseIOCRBlockReq(this, binaryReader, UdpPacket);
                    break;
                case BlockType.ExpectedSubmoduleBlockReq:
                    ExpectedSubmoduleBlockRequests = GetExpectedSubmodules(binaryReader);
                    break;
                case BlockType.AlarmCRBlockReq: // TODO: Implement (currently not needed)
                case BlockType.Unknown:
                    AdvanceBinaryReaderByBlockLength(binaryReader, blockHeader);
                    break;
            }
        }

        FragmentedData.Clear();
    }

    internal static bool TryParse(Packet packet, ICaptureDeviceProxy captureDevice, [NotNullWhen(true)] out ProfinetIoConnectRequestPacket? pnIoConReqPacket, ILogger? logger = null)
    {
        pnIoConReqPacket = null;
        try
        {
            if (!packet.HasPayloadPacket || packet.PayloadPacket is null)
            {
                return false;
            }

            if (packet.PayloadPacket is not IPv4Packet ipv4Packet
                || ipv4Packet.Protocol != ProtocolType.Udp
                || !ipv4Packet.HasPayloadPacket
                || ipv4Packet.DestinationAddress.GetAddressBytes().Last() == 0xff)
            {
                return false;
            }

            if (ipv4Packet.PayloadPacket is not UdpPacket udpPacket || !udpPacket.HasPayloadData)
            {
                return false;
            }

            if (!MarshalAs.TryDeserializeStruct<DceRpcRequestHeader>(udpPacket.PayloadData, out var dceRpcRequestHeader))
            {
                logger?.LogWarning($"Ignoring packet, due to invalid {nameof(DceRpcRequestHeader)}");
                logger?.LogDebug("Couldn't deserialize {DceRpcRequestHeader} from packet content:\n{UdpPacketContent)}", nameof(DceRpcRequestHeader), udpPacket.PrintHex());
                return false;
            }

            if (dceRpcRequestHeader.Type != 0x0 || dceRpcRequestHeader.FragmentLength == 0)
            {
                logger?.LogWarning("Ignoring DCE/RPC Request Header: Packet type={DceRpcReqHeaderType} with Fragment length={DceRpcReqHeaderFragmentLength}",
                    dceRpcRequestHeader.Type, dceRpcRequestHeader.FragmentLength);
                return false;
            }

            pnIoConReqPacket = new ProfinetIoConnectRequestPacket
            {
                DestinationHardwareAddress = (packet as EthernetPacket)!.DestinationHardwareAddress,
                IPAddress = ipv4Packet.DestinationAddress,
                DceRpcRequestHeader = dceRpcRequestHeader,
                UdpPacket = udpPacket,
                IPv4Packet = ipv4Packet
            };

            if (GetBit(dceRpcRequestHeader.Flags1, 2))
            {
                return TryParseFragment(packet, captureDevice, pnIoConReqPacket, logger);
            }

            pnIoConReqPacket.FragmentedData.AddRange(udpPacket.PayloadData[Marshal.SizeOf(typeof(DceRpcRequestHeader))..]);
            pnIoConReqPacket.UpdateFromFragmentedData();

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Parsing \"PNIO-CM Connect Req\" failed");
            return false;
        }
    }

    private static bool TryParseFragment(Packet packet, ICaptureDeviceProxy captureDevice, ProfinetIoConnectRequestPacket pnIoConReqPacket, ILogger? logger = null)
    {
        try
        {
            if (pnIoConReqPacket.IPv4Packet is null)
            {
                return false;
            }

            if (pnIoConReqPacket.UdpPacket is null)
            {
                return false;
            }

            pnIoConReqPacket.FragmentedData.AddRange(pnIoConReqPacket.UdpPacket.PayloadData[Marshal.SizeOf(typeof(DceRpcRequestHeader))..]);

            if (GetBit(pnIoConReqPacket.DceRpcRequestHeader.Flags1, 1))
            {
                pnIoConReqPacket.LastFragment = true;
                return true;
            }

            var ethPacket = (EthernetPacket)packet;
            var srcAddress = ethPacket.SourceHardwareAddress;
            var dstAddress = ethPacket.DestinationHardwareAddress;
            ethPacket.SourceHardwareAddress = dstAddress;
            ethPacket.DestinationHardwareAddress = srcAddress;

            var srcIp = pnIoConReqPacket.IPv4Packet.SourceAddress;
            var dstIp = pnIoConReqPacket.IPv4Packet.DestinationAddress;
            pnIoConReqPacket.IPv4Packet.SourceAddress = dstIp;
            pnIoConReqPacket.IPv4Packet.DestinationAddress = srcIp;

            var srcPort = pnIoConReqPacket.UdpPacket.SourcePort;
            var dstPort = pnIoConReqPacket.UdpPacket.DestinationPort;
            pnIoConReqPacket.UdpPacket.SourcePort = dstPort;
            pnIoConReqPacket.UdpPacket.DestinationPort = srcPort;

            pnIoConReqPacket.DceRpcRequestHeader = pnIoConReqPacket.DceRpcRequestHeader with
            {
                Type = 0x9,
                FragmentLength = 0,
                FragmentNumber = 0,
                Flags1 = 0,
                Flags2 = 0,
                ServerBootTime = 1
            };

            var data = new List<byte>();
            data.AddRange(Serialize(pnIoConReqPacket.DceRpcRequestHeader));

            pnIoConReqPacket.UdpPacket.PayloadData = [.. data];
            pnIoConReqPacket.UdpPacket.UpdateCalculatedValues();
            pnIoConReqPacket.UdpPacket.UpdateUdpChecksum();
            pnIoConReqPacket.IPv4Packet.UpdateCalculatedValues();
            pnIoConReqPacket.IPv4Packet.UpdateIPChecksum();
            ethPacket.UpdateCalculatedValues();

            captureDevice.SendPacketHandler(ethPacket);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Parsing \"PNIO-CM Connect Req\" failed");
            return false;
        }
    }

    private static bool GetBit(byte b, int bitIndex)
    {
        if (bitIndex is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(bitIndex), "Index must be between 0 and 7");
        }

        return (b & (1 << bitIndex)) != 0;
    }

    private static byte[] Serialize<T>(T str) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var arr = new byte[size];

        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(str, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return arr;
    }

    private static void AdvanceBinaryReaderByBlockLength(BinaryReader binaryReader, BlockHeader blockHeader)
    {
        binaryReader.BaseStream.Position += blockHeader.BlockLength + 4;
    }

    private static void ParseIOCRBlockReq(ProfinetIoConnectRequestPacket pnIoConReqPacket, BinaryReader binaryReader, UdpPacket udpPacket)
    {
        if (!IOCRBlockReq.TryDeserialize(binaryReader, out var ioCRBlockReq))
        {
            throw new InvalidDataException($"Couldn't deserialize {nameof(ioCRBlockReq)} from packet content:\n{udpPacket.PrintHex()}");
        }
        if (ioCRBlockReq.IOCRType == 0x1)
        {
            pnIoConReqPacket.IOCRBlockReqInput = ioCRBlockReq;
        }
        else if (ioCRBlockReq.IOCRType == 0x2)
        {
            pnIoConReqPacket.IOCRBlockReqOutput = ioCRBlockReq;
        }
        else
        {
            throw new InvalidDataException($"Expected IOCRType of 0x1 or 0x2, but got {ioCRBlockReq.IOCRType}");
        }
    }

    private void ParseArgsAndArray(BinaryReader reader)
    {
        ArgsMaximum = reader.ReadUInt32();
        ArgsLength = reader.ReadUInt32();
        ArrayMaximumCount = reader.ReadUInt32();
        ArrayOffset = reader.ReadUInt32();
        ArrayActualCount = reader.ReadUInt32();
    }

    private List<ExpectedSubmoduleBlockReq> GetExpectedSubmodules(BinaryReader reader)
    {
        var result = new List<ExpectedSubmoduleBlockReq>();

        while (ExpectedSubmoduleBlockReq.TryDeserialize(reader, out var expSubModule) &&
            expSubModule.BlockHeader.BlockType == BlockType.ExpectedSubmoduleBlockReq)
        {
            result.Add(expSubModule);
        }

        return result;
    }
}
