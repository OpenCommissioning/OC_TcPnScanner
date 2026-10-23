using System.Reflection;

namespace OC.TcPnScanner.CLI;

internal static class AssemblyHelper
{
    private static readonly string _nameField = AssemblyName.GetAssemblyName(Assembly.GetExecutingAssembly().Location).Name ?? "OC.TcPnScanner.CLI";
    internal static string Name => _nameField;

    private static readonly Version _versionField = AssemblyName.GetAssemblyName(Assembly.GetExecutingAssembly().Location).Version ?? Version.Parse("6.6.6");
    internal static Version Version => _versionField;
}
