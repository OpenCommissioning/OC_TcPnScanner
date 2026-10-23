using OC.TcPnScanner.CLI.Packets;
using Microsoft.Extensions.Logging;
using SharpPcap.LibPcap;

namespace OC.TcPnScanner.CLI;

internal interface ICapture : IDisposable
{
    ICapture WithCapturingDevice(ICaptureDeviceProxy capDeviceProxy);
    ICapture WithEthernetPacketHandler(IPacketHandler packetHandler);
    ICapture WithCaptureFileWriterDevice(CaptureFileWriterDevice? libCaptureFileWriterDevice);
    ICapture WithLogger(ILogger? logger);
    void StartCapture();
    void StopCapture();
    Task StartCaptureAndWaitUntilFininshed(TimeSpan? timeout = null);
}
