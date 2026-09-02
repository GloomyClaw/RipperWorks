using System.Xml.Linq;

namespace RipperWorks.Organizer;

internal static class FomodXmlValidation
{
    public static bool CheckChildNamespace(XElement child, XElement parent, out string? error)
    {
        error = null;
        if (!string.Equals(child.Name.NamespaceName, parent.Name.NamespaceName, StringComparison.Ordinal))
        {
            error = $"FOMOD element '{child.Name.LocalName}' belongs to unsupported XML namespace '{child.Name.NamespaceName}'.";
            return false;
        }
        return true;
    }

    public static bool ValidateAttributes(XElement element, HashSet<string> allowedUnqualifiedAttributes, out string? error)
    {
        error = null;
        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;

            if (!string.IsNullOrEmpty(attr.Name.NamespaceName))
            {
                var isConfigRoot = string.Equals(element.Name.LocalName, "config", StringComparison.OrdinalIgnoreCase);
                if (isConfigRoot && string.Equals(attr.Name.NamespaceName, "http://www.w3.org/2001/XMLSchema-instance", StringComparison.Ordinal))
                {
                    continue;
                }
                error = $"FOMOD '{element.Name.LocalName}' element contains unsupported namespaced attribute '{attr.Name}'.";
                return false;
            }

            var localName = attr.Name.LocalName;
            if (!allowedUnqualifiedAttributes.Contains(localName))
            {
                error = $"FOMOD '{element.Name.LocalName}' element contains unsupported attribute '{localName}'.";
                return false;
            }

            if (string.Equals(localName, "order", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(attr.Value.Trim(), "Explicit", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"FOMOD '{element.Name.LocalName}' specifies unsupported order attribute value '{attr.Value}'.";
                    return false;
                }
            }
        }
        return true;
    }

    public static bool ValidateLeafElement(XElement element, out string? error)
    {
        error = null;
        if (element.HasElements)
        {
            error = $"FOMOD leaf element '{element.Name.LocalName}' contains unsupported child elements.";
            return false;
        }
        return true;
    }
}
