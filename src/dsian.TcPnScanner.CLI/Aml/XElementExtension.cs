using System.Text;
using System.Xml.Linq;
namespace dsian.TcPnScanner.CLI.Aml;

public static class XElementExtension
{
    public static bool ChildExists(this XElement element, XName childName)
    {
        return element.Element(childName) is not null;
    }

    public static bool AttributeExists(this XElement element, XName attributeName)
    {
        return element.Attribute(attributeName) is not null;
    }

    public static string? GetPnDeviceNameConverted(this XElement element)
    {
        return element
            .Descendants("Attribute")
            .Where(x => x.Attribute("Name")?.Value == "ProfinetDeviceName")
            .Select(x => x.Element("Value")?.Value)
            .FirstOrDefault() ??
               element
            .Descendants("Attribute")
            .Where(x => x.Attribute("Name")?.Value == "DeviceItemType")
            .Where(x => x.Element("Value")?.Value == "HeadModule")
            .Select(x => x.Parent?.Attribute("Name")?.Value)
            .FirstOrDefault()
            .ConvertToPnString();
    }

    public static string? GetPnDeviceNameBackwardsRecursive(this XElement? element)
    {
        while (true)
        {
            if (element is null) return null;
            if (element.IsDevice()) return element.GetPnDeviceNameConverted();
            element = element.Parent;
        }
    }

    private static string? ConvertToPnString(this string? value)
    {
        if (value is null) return null;
        var loweredValue = value.ToLower();

        var charMappings = new Dictionary<char, string>
        {
            { '.', ".xd" },
            { '=', "xv" },
            { '+', "xn" },
            { '_', "xb" },
            { ' ', "xa" },
            { 'x', "xx" }
        };

        var stringBuilder = new StringBuilder();

        foreach (var c in loweredValue)
        {
            if (charMappings.TryGetValue(c, out var mapping))
            {
                stringBuilder.Append(mapping);
            }
            else
            {
                stringBuilder.Append(c);
            }
        }

        var convertedName = stringBuilder.ToString();

        if (convertedName != loweredValue)
        {
            convertedName += $"{Crc16Arc.Calculate(convertedName):x4}";
        }

        return convertedName;
    }

    public static IEnumerable<XElement?> GetDevices(this XElement element)
    {
        return element
            .Descendants("SupportedRoleClass")
            .Where(x => x.Attribute("RefRoleClassPath")?.Value == "AutomationProjectConfigurationRoleClassLib/Device")
            .Select(x => x.Parent);
    }

    private static bool IsDevice(this XElement? element)
    {
        return element?
            .Element("SupportedRoleClass")?
            .Attribute("RefRoleClassPath")?.Value == "AutomationProjectConfigurationRoleClassLib/Device";
    }

    public static IEnumerable<XElement> GetDeviceItems(this XElement element)
    {
        return element
            .Elements("InternalElement")
            .Where(x => x.Element("SupportedRoleClass")?.Attribute("RefRoleClassPath")?.Value == "AutomationProjectConfigurationRoleClassLib/DeviceItem");
    }

    public static bool IsProfisafeItem(this XElement element)
    {
        return element
            .Elements("SupportedRoleClass")
            .FirstOrDefault(x => x.Attribute("RefRoleClassPath")?.Value == "AutomationProjectConfigurationProfiSafeRoleClassLib/DeviceItemProfiSafe") is not null;
    }

    public static IEnumerable<XElement?> GetCommunicationPorts(this XElement element)
    {
        return element
            .Descendants("SupportedRoleClass")
            .Where(x => x.Attribute("RefRoleClassPath")?.Value == "AutomationProjectConfigurationRoleClassLib/CommunicationPort")
            .Select(x => x.Parent);
    }

    public static string? GetName(this XElement element)
    {
        return element.Attribute("Name")?.Value;
    }

    public static string? GetId(this XElement element)
    {
        return element.Attribute("ID")?.Value;
    }

    public static int GetPositionNumber(this XElement? element)
    {
        var number = element?.GetAttributeValue("PositionNumber");
        if (number is null) return -1;

        if (int.TryParse(number, out var result))
        {
            return result;
        }

        return -1;
    }

    public static IEnumerable<XElement>? GetAddresses(this XElement element)
    {
        return element
            .Elements("Attribute")
            .FirstOrDefault(x => x.Attribute("Name")?.Value == "Address")?
            .Elements("Attribute");
    }

    public static string? GetAttributeValue(this XElement element, string attributeName)
    {
        return element
            .Elements("Attribute")
            .FirstOrDefault(x => x.Attribute("Name")?.Value == attributeName)?
            .Element("Value")?.Value;
    }

    public static void SetAttribute(this XElement element, string attributeName, string attributeValue)
    {
        var attribute = element.Attribute(attributeName);
        if (attribute is null)
        {
            element.Add(new XAttribute(attributeName, attributeValue));
            return;
        }
        attribute.Value = attributeValue;
    }

    public static int GetAndIncrementAttribute(this XElement? element, string attributeName)
    {
        if (element is null) return 1;
        var attribute = element.Attribute(attributeName);
        if (attribute is null)
        {
            element.Add(new XAttribute(attributeName, 2));
            return 1;
        }

        var value = int.TryParse(attribute.Value, out var val) ? val : 1;
        attribute.Value = (value + 1).ToString();
        return value;
    }
}
