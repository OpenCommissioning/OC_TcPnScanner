using dsian.TcPnScanner.CLI.PnDevice;
using dsian.TcPnScanner.CLI.Aml;
using Microsoft.Extensions.Logging;
using System.Text;

namespace dsian.TcPnScanner.CLI.Export;

internal class Exporter
{
    internal static async Task ExportToFile(IExporter exporter, IDeviceStore deviceStore, CliOptions options, ILogger? logger = null, AmlFile? amlFile = null)
    {
        Guard.ThrowIfNull(exporter);
        Guard.ThrowIfNull(deviceStore);

        var fi = TempDirectory.CreateFileInfo(options.ExportDirectory, deviceStore.GetProfinetDeviceName());
        using var ms = exporter.Export(deviceStore.GetDevices());
        new XtiUpdater(logger).Update(ms, amlFile?.ConvertedAml);
        await File.WriteAllBytesAsync(fi.FullName, ms.ToArray());

        logger?.LogInformation("Exported devices to {ExportDirectory}", fi.FullName);
    }

    internal static async Task ExportToCLI(IExporter exporter, IDeviceStore deviceStore, CliOptions options, ILogger? logger = null, AmlFile? amlFile = null)
    {
        Guard.ThrowIfNull(exporter);
        Guard.ThrowIfNull(deviceStore);

        using var ms = exporter.Export(deviceStore.GetDevices());
        new XtiUpdater(logger).Update(ms, amlFile?.ConvertedAml);
        using var sr = new StreamReader(ms);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(await sr.ReadToEndAsync());
    }
}
