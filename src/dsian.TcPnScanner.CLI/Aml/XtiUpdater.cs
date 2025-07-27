using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace dsian.TcPnScanner.CLI.Aml;

internal partial class XtiUpdater(ILogger? logger = null)
{
    private XElement? _amlConverted;
    private List<string> _deviceNames = [];

    public void Update(MemoryStream xtiStream, XElement? amlConverted)
    {
        try
        {
            _deviceNames.Clear();
            _amlConverted = amlConverted;

            if (_amlConverted is null)
            {
                return;
            }

            xtiStream.Position = 0;
            var xti = XDocument.Load(xtiStream).Root;
            if (xti is null)
            {
                logger?.LogError("Error reading xti file");
                return;
            }

            var plcPortNr = xti.Element("Device")?.Element("Profinet")?.Attribute("PLCPortNr");
            if (plcPortNr is not null)
            {
                plcPortNr.Value = "852";
            }

            var boxes = xti.Descendants("Box").ToList();
            if (boxes.Count == 0) return;

            _deviceNames = (from box in boxes select box.Element("Name")?.Value).ToList();

            foreach (var box in boxes)
            {
                UpdateBox(box);
            }

            xtiStream.SetLength(0);
            using var streamWriter = new StreamWriter(xtiStream);
            xti.Save(streamWriter);
            xtiStream.Position = 0;
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Error reading xti file");
        }
    }

    private void UpdateBox(XElement box)
    {
        var boxName = box.Element("Name")?.Value;
        if (boxName is null) return;

        var modules = box.Element("Profinet")?.Element("API")?.Elements("Module");
        if (modules is null) return;

        var moduleIndex = 1;
        foreach (var module in modules)
        {
            UpdateModule(module, moduleIndex, boxName);
            moduleIndex++;
        }
    }

    private void UpdateModule(XElement module, int moduleIndex, string boxName)
    {
        var portModuleIndex = 1;
        var ioModuleIndex = 1;
        foreach (var subModule in module.Elements("SubModule"))
        {
           UpdateSubModule(subModule, boxName, moduleIndex, ref portModuleIndex, ref ioModuleIndex);
        }
    }

    private void UpdateSubModule(XElement subModule, string boxName, int moduleIndex, ref int portModuleIndex, ref int ioModuleIndex)
    {
        var subSlotNumber = subModule.Attribute("SubSlotNumber")?.Value;
        if (subSlotNumber is null) return;

        var typeOfSubmodule = GetTypeOfSubmodule(subSlotNumber);
        var remPeerPort = GetRemPeerPort(typeOfSubmodule, boxName, moduleIndex, portModuleIndex);

        subModule.SetAttribute("TypeOfSubModule", typeOfSubmodule);

        if (typeOfSubmodule == "2")
        {
            portModuleIndex++;
            subModule.SetAttribute("PortData", "00000000000000000000000000000000000000000000000000000000");
            if (remPeerPort is not null)
            {
                subModule.SetAttribute("RemPeerPort", remPeerPort);
            }
        }

        var inputVar = subModule
            .Elements("Vars")
            .FirstOrDefault(x => x.Attribute("VarGrpType")?.Value == "1");

        var outputVar = subModule
            .Elements("Vars")
            .FirstOrDefault(x => x.Attribute("VarGrpType")?.Value == "2");

        if (inputVar is null && outputVar is null) return;
        ioModuleIndex++;

        var matchedIoModule = GetMatchedIoModule(boxName, moduleIndex, ioModuleIndex);
        if (matchedIoModule is null) return;

        var name = subModule.Element("Name")?.Value;
        if (name is not null)
        {
            if (IsFailsafe(matchedIoModule))
            {
                name += " #failsafe";
            }
            subModule.Element("Name")!.Value = name;
        }

        if (inputVar != null) SetAddress(inputVar, matchedIoModule, true);
        if (outputVar != null) SetAddress(outputVar, matchedIoModule, false);
    }

    private static void SetAddress(XElement? var, XElement matchedSubmodule, bool isInput)
    {
        var name = var?.Element("Var")?.Element("Name");
        if (name is null) return;

        var address = GetStartAddress(matchedSubmodule, isInput ? "Output" : "Input");
        if (address == -1) return;

        name.Value = isInput ? $"Q{address}" : $"I{address}";
    }


    private static string GetTypeOfSubmodule(string subSlotNumber)
    {
        var subSlot = int.Parse(subSlotNumber);

        if (subSlot is >= 0x8000 and <= 0x8FFF)
        {
            return subSlot % 0x100 == 0 ? "1" : "2";
        }

        return "3";
    }

    private XElement? GetMatchedDevice(string boxName)
    {
        try
        {
            return _amlConverted?
                .Descendants("Device")
                .FirstOrDefault(x =>
                    string.Equals(x.Attribute("Name")?.Value, boxName, StringComparison.CurrentCultureIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private string? GetRemPeerPort(string typeOfSubmodule, string boxName, int moduleIndex, int portModuleIndex)
    {
        if (typeOfSubmodule != "2") return null;

        try
        {
            var remPeerPort = GetMatchedDevice(boxName)?
                .Element($"Module{moduleIndex}")?
                .Element("Ports")?
                .Element($"Port{portModuleIndex}")?
                .Attribute("RemPeerPort")?
                .Value;

            if (remPeerPort is null) return null;

            var remPeerDeviceName = GetPortRegex().Replace(remPeerPort, "");
            return _deviceNames.Any(x => x == remPeerDeviceName) ? remPeerPort : null;
        }
        catch
        {
            return null;
        }
    }

    private XElement? GetMatchedIoModule(string boxName, int moduleIndex, int ioModuleIndex)
    {
        try
        {
            return GetMatchedDevice(boxName)?
                .Element($"Module{moduleIndex}")?
                .Element($"IoModule{ioModuleIndex}");
        }
        catch
        {
            return null;
        }
    }

    private static int GetStartAddress(XElement matchedSubmodule, string filter)
    {
        var addresses = matchedSubmodule.Elements("Address");

        foreach (var address in addresses)
        {
            var ioType = address.Element("IoType")?.Value;
            if (ioType is null) continue;
            if (address.Element("IoType")?.Value != filter) continue;
            return int.Parse(address.Element("StartAddress")?.Value ?? "-1");
        }

        return -1;
    }

    private static bool IsFailsafe(XElement matchedSubmodule)
    {
        return bool.TryParse(matchedSubmodule.Attribute("IsFailsafe")?.Value, out var result) && result;
    }

    [GeneratedRegex(@"\.port-\d+")]
    private static partial Regex GetPortRegex();
}
