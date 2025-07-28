using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace dsian.TcPnScanner.CLI.Aml;

/// <summary>
/// Creates a new instance of the <see cref="AmlFile"/>.
/// </summary>
/// <param name="logger">The logger interface.</param>
public class AmlFile(ILogger? logger = null)
{
    private static readonly string TempDir = $"{Path.GetTempPath()}{Assembly.GetExecutingAssembly().GetName().Name}";
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(){WriteIndented = true};
    private readonly Dictionary<string, string> _linkA = [];
    private readonly Dictionary<string, string> _linkB = [];
    private XElement? _aml;

    /// <summary>
    /// Gets the converted aml content as <see cref="XElement"/>.
    /// </summary>
    public XElement ConvertedAml { get; } = new("Profinet");

    /// <summary>
    /// Gets a dictionary of Device IDs by profinet name.
    /// </summary>
    public Dictionary<string, string> DeviceIdsByName { get; } = [];

    /// <summary>
    /// Reads a TIA aml export file and converts to a simplified <see cref="XElement"/>.
    /// </summary>
    /// <param name="amlFilePath">The aml file path.</param>
    /// <param name="gsdFolderPath">The gsd folder path.</param>
    /// <returns>The converted <see cref="XElement"/></returns>
    public void Convert(string? amlFilePath, string? gsdFolderPath = null)
    {
        _linkA.Clear();
        _linkB.Clear();
        DeviceIdsByName.Clear();
        ConvertedAml.RemoveAll();

        if (string.IsNullOrEmpty(amlFilePath)) return;
        _aml = XDocument.Load(amlFilePath).Root;

        if (_aml is null) return;

        GetLinks();

        foreach (var device in _aml.GetDevices())
        {
            if (device is null) continue;
            var name = device.GetPnDeviceNameConverted();
            if (name is null) continue;
            var convertedDevice = new XElement("Device", new XAttribute("Name", name));
            foreach (var deviceItem in device.GetDeviceItems())
            {
                GetDeviceItemInformation(deviceItem, null, convertedDevice);
            }

            ConvertedAml.Add(convertedDevice);
        }

        CreateDeviceIdsByName(ConvertedAml, gsdFolderPath);
        ConvertedAml.Save($"{TempDir}\\ConvertedAml.xml");
    }

    private void GetDeviceItemInformation(XElement deviceItem, XElement? deviceItemParent, XElement convertedDevice)
    {
        GetDeviceItemAttributes(deviceItem, deviceItemParent, convertedDevice);

        //Call recursive
        foreach (var deviceSubItem in deviceItem.GetDeviceItems())
        {
            GetDeviceItemInformation(deviceSubItem, deviceItem, convertedDevice);
        }
    }

    private void GetDeviceItemAttributes(XElement deviceItem, XElement? deviceItemParent, XElement convertedDevice)
    {
        var convertedDeviceItem = new XElement($"Module{deviceItem.GetPositionNumber() + 1}");
        var portElement = new XElement("Ports");

        if (convertedDevice.Element("Module1")?.Element(portElement.Name) is null)
        {
            GetNetworkInformation(deviceItem, portElement);
        }

        //Read address infos and check if this is a submodule
        var addresses = deviceItem.GetAddresses();
        var isSubModule = addresses is not null && deviceItemParent is not null;
        var hasIOs = addresses?.Aggregate(false, (hasIOs, address) =>
            hasIOs | GetAddressAttributes(address, convertedDeviceItem)) == true;

        switch (isSubModule)
        {
            case true when hasIOs: //this is a submodule with I/O
            {
                //Read failsafe info
                if (deviceItem.IsProfisafeItem())
                {
                    convertedDeviceItem.Add(new XAttribute("IsFailsafe", true));
                }

                //Because this is a submodule, add it to a module
                string moduleName;

                if (deviceItemParent.GetPositionNumber() == 0)
                {
                    convertedDeviceItem.Name = "IoModule1";
                    moduleName = $"Module{deviceItem.GetPositionNumber() + 1}";
                }
                else
                {
                    var index = deviceItemParent.GetAndIncrementAttribute("IoModuleIndex");
                    convertedDeviceItem.Name = $"IoModule{index}";
                    moduleName = $"Module{deviceItemParent.GetPositionNumber() + 1}";
                }

                //Module does not exist yet -> create new
                if (!convertedDevice.ChildExists(moduleName))
                {
                    convertedDevice.Add(new XElement(moduleName));
                }

                //Add submodule to module
                convertedDevice.Element(moduleName)?.Add(convertedDeviceItem);
                break;
            }
            case false: //This is a module -> added to the convertedDevice directly
            {
                if (convertedDevice.ChildExists(convertedDeviceItem.Name))
                {
                    foreach (var item in convertedDeviceItem.Elements())
                    {
                        convertedDevice.Element(convertedDeviceItem.Name)?.Add(item);
                    }
                }
                else
                {
                    convertedDevice.Add(convertedDeviceItem);
                }

                var typeIdentifier = deviceItem.GetAttributeValue("TypeIdentifier");
                if (typeIdentifier is not null && !convertedDevice.AttributeExists("TypeIdentifier"))
                {
                    if (typeIdentifier.Contains("OrderNumber:") || typeIdentifier.Contains("GSD:"))
                    {
                        convertedDevice.Add(new XAttribute("TypeIdentifier", typeIdentifier));
                    }
                }
                break;
            }
        }

        //Add ports to Module1, if any
        if (!portElement.HasElements) return;
        if (!convertedDevice.ChildExists("Module1"))
        {
            convertedDevice.Add(new XElement("Module1"));
        }
        convertedDevice.Element("Module1")?.Add(portElement);
    }

    private void CreateDeviceIdsByName(XElement rootElement, string? gsdFolderPath)
    {
        var deviceIds = GetDeviceIdsFromGit();
        GetDeviceIdsFromGsdFiles(deviceIds, gsdFolderPath);

        var missing = new HashSet<string>();

        foreach (var device in rootElement.Descendants("Device"))
        {
            if (device.Attribute("Name")?.Value is not {} name) continue;
            if (device.Attribute("TypeIdentifier")?.Value is not {} typeIdentifier) continue;
            typeIdentifier = typeIdentifier.Split('/')[0];

            if (!deviceIds.TryGetValue(typeIdentifier, out var deviceId))
            {
                if (missing.Add(typeIdentifier))
                {
                    logger?.LogWarning("Unknown device of type {typeIdentifier}", typeIdentifier);
                }
                continue;
            }
            DeviceIdsByName.Add(name, deviceId);
        }
        if (JsonSerializer.Serialize(DeviceIdsByName, JsonSerializerOptions) is not {} deviceIdsByName) return;
        File.WriteAllText($"{TempDir}\\DeviceIdsByName.json", deviceIdsByName);
    }

    private Dictionary<string, string> GetDeviceIdsFromGit()
    {
        const string url = "https://raw.githubusercontent.com/opencommissioning/OC_ProfinetDeviceIds/master/DeviceIds.json";
        using var client = new HttpClient();
        try
        {
            var raw = client.GetStringAsync(url).Result;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? [];
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Error getting device ids from git");
            return [];
        }
    }

    private void GetDeviceIdsFromGsdFiles(Dictionary<string, string> deviceIds, string? gsdFolderPath)
    {
        if (string.IsNullOrEmpty(gsdFolderPath)) return;
        var files = Directory.GetFiles(gsdFolderPath, "*.xml", SearchOption.AllDirectories);
        var additional = new Dictionary<string, string>();

        foreach (var file in files)
        {
            try
            {
                const string ns = "{http://www.profibus.com/GSDML/2003/11/DeviceProfile}";
                var doc = XDocument.Load(file);
                if (doc.Descendants($"{ns}DeviceIdentity").FirstOrDefault() is not {} identity) continue;
                var vendor = identity.Attribute("VendorID");
                var device = identity.Attribute("DeviceID");
                if (vendor is null || device is null) continue;
                var id = vendor.Value + device.Value.Replace("0x", "");
                var key = $"GSD:{Path.GetFileName(file).ToUpper()}";

                if (deviceIds.TryAdd(key, id))
                {
                    additional.Add(key, id);
                }

                foreach (var deviceAccessPointItem in doc.Descendants($"{ns}DeviceAccessPointItem"))
                {
                    if (deviceAccessPointItem.Element($"{ns}ModuleInfo") is not {} moduleInfo)
                    {
                        continue;
                    }

                    if (moduleInfo.Element($"{ns}VendorName")?.Attribute("Value")?.Value
                            .Contains("siemens", StringComparison.CurrentCultureIgnoreCase) != true)
                    {
                        continue;
                    }

                    if (moduleInfo.Element($"{ns}OrderNumber")?.Attribute("Value")?.Value is not {} orderNumber)
                    {
                        continue;
                    }

                    if (deviceIds.TryAdd($"OrderNumber:{orderNumber}", id))
                    {
                        additional.Add($"OrderNumber:{orderNumber}", id);
                    }
                }
            }
            catch (Exception e)
            {
                logger?.LogError(e, "Error parsing GSDML {file}", file);
            }
        }

        if (additional.Count == 0) return;
        if (JsonSerializer.Serialize(additional, JsonSerializerOptions) is not {} deviceIdsJson) return;
        File.WriteAllText($"{TempDir}\\DeviceIds-added.json", deviceIdsJson);
    }

    private static bool GetAddressAttributes(XElement address, XElement convertedDeviceItem)
    {
        var element = new XElement("Address");
        var ioType = address.GetAttributeValue("IoType");
        var hasIOs = false;
        if (ioType is "Input" or "Output")
        {
            element.Add(new XElement("IoType", ioType));
            element.Add(new XElement("StartAddress", address.GetAttributeValue("StartAddress")));
            element.Add(new XElement("Length", address.GetAttributeValue("Length")));
            hasIOs = true;
        }

        convertedDeviceItem.Add(element);
        return hasIOs;
    }

    private void GetNetworkInformation(XElement deviceItem, XElement portElement)
    {
        foreach (var port in deviceItem.GetCommunicationPorts())
        {
            var id = port?.GetId();
            if (id is null) continue;

            var linkedPort = GetLinkedPort(id);
            if (linkedPort is null) continue;

            var linkedDeviceName = linkedPort.GetPnDeviceNameBackwardsRecursive();
            if (linkedDeviceName is null) continue;

            var connectedPortNr = ".port-" + linkedPort.GetPositionNumber().ToString("000");

            var elem = new XElement("Port" + port.GetPositionNumber());
            elem.Add(new XAttribute("RemPeerPort",linkedDeviceName + connectedPortNr));
            portElement.Add(elem);
        }
    }

    private XElement? GetLinkedPort(string id)
    {
        if (_aml is null) return null;

        if (!_linkA.TryGetValue(id, out var linkedId))
        {
            if (!_linkB.TryGetValue(id, out linkedId))
            {
                return null;
            }
        }

        return _aml
            .Descendants("InternalElement")
            .FirstOrDefault(x => x.Attribute("ID")?.Value == linkedId);
    }

    private void GetLinks()
    {
        if (_aml is null) return;

        var portLinks = _aml
            .Descendants("InternalLink")
            .Where(x => x.GetName()?.Contains("Link To Port") == true);

        foreach (var portLink in portLinks)
        {
            var linkA = portLink.Attribute("RefPartnerSideA")?.Value;
            var linkB = portLink.Attribute("RefPartnerSideB")?.Value;
            if (linkA is null || linkB is null) continue;
            _linkA.Add(linkA.Split(':')[0], linkB.Split(':')[0]);
            _linkB.Add(linkB.Split(':')[0], linkA.Split(':')[0]);
        }
    }
}
