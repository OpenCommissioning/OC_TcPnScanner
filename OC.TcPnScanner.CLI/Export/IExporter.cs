using OC.TcPnScanner.CLI.PnDevice;

namespace OC.TcPnScanner.CLI.Export;

internal interface IExporter
{
    MemoryStream Export(IEnumerable<Device> devices);
}
