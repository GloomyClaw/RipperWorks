using System.Xml;
using System.Xml.Linq;
using RipperWorks.Core;

namespace RipperWorks.Organizer;

public static class FomodParser
{
    public const string ModuleConfigXmlPath = "fomod/moduleconfig.xml";
    public const string InfoXmlPath = "fomod/info.xml";

    public static bool IsFomodEntry(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath)) return false;
        var norm = normalizedPath.Replace('\\', '/').TrimStart('/');
        return string.Equals(norm, ModuleConfigXmlPath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFomodMetadataEntry(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath)) return false;
        var norm = normalizedPath.Replace('\\', '/').TrimStart('/');
        return norm.StartsWith("fomod/", StringComparison.OrdinalIgnoreCase);
    }

    public static FomodDefinition Parse(string xmlContent, string? infoXmlContent = null)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            return FomodDefinition.Unsupported("FOMOD ModuleConfig.xml is empty.");

        var xmlBytes = System.Text.Encoding.UTF8.GetBytes(xmlContent);
        var infoBytes = string.IsNullOrWhiteSpace(infoXmlContent) ? null : System.Text.Encoding.UTF8.GetBytes(infoXmlContent);
        return Parse(xmlBytes, infoBytes);
    }

    public static FomodDefinition Parse(byte[] xmlBytes, byte[]? infoXmlBytes = null)
    {
        if (xmlBytes is null || xmlBytes.Length == 0)
            return FomodDefinition.Unsupported("FOMOD ModuleConfig.xml is empty.");

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var ms = new MemoryStream(xmlBytes);
            using var xmlReader = XmlReader.Create(ms, settings);
            var doc = XDocument.Load(xmlReader);
            var root = doc.Root;
            if (root is null)
                return FomodDefinition.Unsupported("FOMOD ModuleConfig.xml has no root element.");

            if (!string.Equals(root.Name.LocalName, "config", StringComparison.OrdinalIgnoreCase))
            {
                return FomodDefinition.Unsupported($"FOMOD ModuleConfig.xml root element must be 'config', but was '{root.Name.LocalName}'.");
            }

            if (doc.Descendants().Any(e =>
                    string.Equals(e.Name.LocalName, "moduleScript", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Name.LocalName, "script", StringComparison.OrdinalIgnoreCase)))
            {
                return FomodDefinition.Unsupported("FOMOD contains unsupported script execution instructions.");
            }

            if (!FomodXmlValidation.ValidateAttributes(root, [], out var rootAttrErr))
            {
                return FomodDefinition.Unsupported(rootAttrErr!);
            }

            var whitelistedRootElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "moduleName", "moduleImage", "requiredInstallFiles", "installSteps", "conditionalFileInstalls"
            };
            foreach (var child in root.Elements())
            {
                if (!FomodXmlValidation.CheckChildNamespace(child, root, out var nsErr))
                {
                    return FomodDefinition.Unsupported(nsErr!);
                }
                if (!whitelistedRootElements.Contains(child.Name.LocalName))
                {
                    return FomodDefinition.Unsupported($"FOMOD config root contains unsupported element '{child.Name.LocalName}'.");
                }
            }

            if (!ValidateSingletons(root, ["requiredInstallFiles", "installSteps", "conditionalFileInstalls"], out var rootSingErr))
            {
                return FomodDefinition.Unsupported(rootSingErr!);
            }

            var moduleName = ElementValue(root, "moduleName");
            if (string.IsNullOrWhiteSpace(moduleName) && infoXmlBytes is not null && infoXmlBytes.Length > 0)
            {
                moduleName = ParseInfoName(infoXmlBytes);
            }
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                moduleName = "FOMOD Installer";
            }

            var moduleImage = AttributeOrElementValue(root, "moduleImage", "path");

            var (requiredFiles, reqErr) = ParseFileInstalls(root.Element(Name(root, "requiredInstallFiles")));
            if (reqErr is not null)
                return FomodDefinition.Unsupported(reqErr);

            var (steps, stepError) = ParseSteps(root.Element(Name(root, "installSteps")));
            if (stepError is not null)
                return FomodDefinition.Unsupported(stepError);

            var (conditionalInstalls, condError) = ParseConditionalInstalls(root.Element(Name(root, "conditionalFileInstalls")));
            if (condError is not null)
                return FomodDefinition.Unsupported(condError);

            return new FomodDefinition(
                moduleName,
                moduleImage,
                requiredFiles,
                steps,
                conditionalInstalls);
        }
        catch (XmlException ex)
        {
            return FomodDefinition.Unsupported($"Malformed FOMOD XML: {ex.Message}");
        }
        catch (Exception ex)
        {
            return FomodDefinition.Unsupported($"FOMOD XML parse error: {ex.Message}");
        }
    }

    private static bool ValidateSingletons(XElement container, string[] singletonNames, out string? error)
    {
        error = null;
        foreach (var name in singletonNames)
        {
            var count = container.Elements().Count(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (count > 1)
            {
                error = $"FOMOD container '{container.Name.LocalName}' contains duplicate singleton element '{name}'.";
                return false;
            }
        }
        return true;
    }

    private static string? ParseInfoName(byte[] infoXmlBytes)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var ms = new MemoryStream(infoXmlBytes);
            using var reader = XmlReader.Create(ms, settings);
            var doc = XDocument.Load(reader);
            return ElementValue(doc.Root, "Name") ?? ElementValue(doc.Root, "name");
        }
        catch
        {
            return null;
        }
    }

    private static (IReadOnlyList<FomodFileInstall> Installs, string? Error) ParseFileInstalls(XElement? container)
    {
        if (container is null) return ([], null);
        if (!FomodXmlValidation.ValidateAttributes(container, [], out var cAttrErr)) return ([], cAttrErr);

        var list = new List<FomodFileInstall>();
        foreach (var elem in container.Elements())
        {
            if (!FomodXmlValidation.CheckChildNamespace(elem, container, out var nsErr))
                return ([], nsErr);

            var localName = elem.Name.LocalName;
            var isFolder = string.Equals(localName, "folder", StringComparison.OrdinalIgnoreCase);
            var isFile = string.Equals(localName, "file", StringComparison.OrdinalIgnoreCase);
            if (!isFolder && !isFile)
            {
                return ([], $"FOMOD files container contains unsupported element '{localName}'.");
            }

            if (!FomodXmlValidation.ValidateLeafElement(elem, out var leafErr))
                return ([], leafErr);

            if (!FomodXmlValidation.ValidateAttributes(elem, ["source", "destination", "priority"], out var attrErr))
                return ([], attrErr);

            var source = elem.Attribute("source")?.Value ?? string.Empty;
            var destinationAttr = elem.Attribute("destination");
            var destination = destinationAttr is not null ? destinationAttr.Value : source;
            var priorityAttr = elem.Attribute("priority");
            var priority = 0;
            if (priorityAttr is not null)
            {
                if (string.IsNullOrWhiteSpace(priorityAttr.Value) || !int.TryParse(priorityAttr.Value, out priority))
                {
                    return ([], $"FOMOD {localName} priority attribute must be a valid integer.");
                }
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                return ([], $"FOMOD {localName} element has empty source attribute.");
            }

            list.Add(new FomodFileInstall(
                source,
                destination,
                priority,
                isFolder));
        }
        return (list, null);
    }

    private static (IReadOnlyList<FomodStep> Steps, string? Error) ParseSteps(XElement? container)
    {
        if (container is null) return ([], null);
        if (!FomodXmlValidation.ValidateAttributes(container, ["order"], out var cAttrErr)) return ([], cAttrErr);

        var steps = new List<FomodStep>();
        var stepIdx = 0;
        foreach (var child in container.Elements())
        {
            if (!FomodXmlValidation.CheckChildNamespace(child, container, out var nsErr)) return ([], nsErr);
            if (!string.Equals(child.Name.LocalName, "installStep", StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"FOMOD installSteps container contains unsupported element '{child.Name.LocalName}'.");
            }
        }
        foreach (var stepElem in container.Elements())
        {
            if (!FomodXmlValidation.ValidateAttributes(stepElem, ["name"], out var stepAttrErr))
                return ([], stepAttrErr);

            var whitelistedStepElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "optionalFileGroups"
            };
            foreach (var child in stepElem.Elements())
            {
                if (!FomodXmlValidation.CheckChildNamespace(child, stepElem, out var nsErr)) return ([], nsErr);
                if (!whitelistedStepElements.Contains(child.Name.LocalName))
                {
                    return ([], $"FOMOD installStep contains unsupported element '{child.Name.LocalName}'.");
                }
            }

            if (!ValidateSingletons(stepElem, ["optionalFileGroups"], out var stepSingErr))
            {
                return ([], stepSingErr);
            }

            var stepName = stepElem.Attribute("name")?.Value ?? $"Step {stepIdx + 1}";
            var (groups, groupErr) = ParseGroups(stepElem.Element(Name(stepElem, "optionalFileGroups")), stepIdx);
            if (groupErr is not null)
                return ([], groupErr);

            steps.Add(new FomodStep(stepName, groups));
            stepIdx++;
        }
        return (steps, null);
    }

    private static (IReadOnlyList<FomodGroup> Groups, string? Error) ParseGroups(XElement? container, int stepIdx)
    {
        if (container is null) return ([], null);
        if (!FomodXmlValidation.ValidateAttributes(container, ["order"], out var cAttrErr)) return ([], cAttrErr);

        var groups = new List<FomodGroup>();
        var groupIdx = 0;
        foreach (var child in container.Elements())
        {
            if (!FomodXmlValidation.CheckChildNamespace(child, container, out var nsErr)) return ([], nsErr);
            if (!string.Equals(child.Name.LocalName, "group", StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"FOMOD optionalFileGroups container contains unsupported element '{child.Name.LocalName}'.");
            }
        }
        foreach (var groupElem in container.Elements())
        {
            if (!FomodXmlValidation.ValidateAttributes(groupElem, ["name", "type"], out var grpAttrErr))
                return ([], grpAttrErr);

            var whitelistedGroupElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "plugins"
            };
            foreach (var child in groupElem.Elements())
            {
                if (!FomodXmlValidation.CheckChildNamespace(child, groupElem, out var nsErr)) return ([], nsErr);
                if (!whitelistedGroupElements.Contains(child.Name.LocalName))
                {
                    return ([], $"FOMOD group contains unsupported element '{child.Name.LocalName}'.");
                }
            }

            if (!ValidateSingletons(groupElem, ["plugins"], out var groupSingErr))
            {
                return ([], groupSingErr);
            }

            var groupName = groupElem.Attribute("name")?.Value ?? $"Group {groupIdx + 1}";
            var typeAttr = groupElem.Attribute("type")?.Value;
            if (string.IsNullOrWhiteSpace(typeAttr))
            {
                return ([], $"FOMOD group '{groupName}' is missing mandatory 'type' attribute.");
            }
            var groupType = ParseGroupType(typeAttr);
            if (groupType is null)
                return ([], $"Unsupported FOMOD group type '{typeAttr}'.");

            var (plugins, pluginErr) = ParsePlugins(groupElem.Element(Name(groupElem, "plugins")), stepIdx, groupIdx);
            if (pluginErr is not null)
                return ([], pluginErr);

            groups.Add(new FomodGroup(groupName, groupType.Value, plugins));
            groupIdx++;
        }
        return (groups, null);
    }

    private static FomodGroupType? ParseGroupType(string typeStr) => typeStr.ToLowerInvariant() switch
    {
        "selectexactlyone" => FomodGroupType.SelectExactlyOne,
        "selectatmostone" => FomodGroupType.SelectAtMostOne,
        "selectatleastone" => FomodGroupType.SelectAtLeastOne,
        "selectall" => FomodGroupType.SelectAll,
        "selectany" => FomodGroupType.SelectAny,
        _ => null
    };

    private static (IReadOnlyList<FomodPlugin> Plugins, string? Error) ParsePlugins(XElement? container, int stepIdx, int groupIdx)
    {
        if (container is null) return ([], null);
        if (!FomodXmlValidation.ValidateAttributes(container, ["order"], out var cAttrErr)) return ([], cAttrErr);

        var plugins = new List<FomodPlugin>();
        var pluginIdx = 0;
        foreach (var child in container.Elements())
        {
            if (!FomodXmlValidation.CheckChildNamespace(child, container, out var nsErr)) return ([], nsErr);
            if (!string.Equals(child.Name.LocalName, "plugin", StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"FOMOD plugins container contains unsupported element '{child.Name.LocalName}'.");
            }
        }
        foreach (var pluginElem in container.Elements())
        {
            if (!FomodXmlValidation.ValidateAttributes(pluginElem, ["name"], out var plgAttrErr))
                return ([], plgAttrErr);

            var whitelistedPluginElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "description", "image", "typeDescriptor", "files", "conditionFlags"
            };
            foreach (var child in pluginElem.Elements())
            {
                if (!FomodXmlValidation.CheckChildNamespace(child, pluginElem, out var childNsErr)) return ([], childNsErr);
                if (!whitelistedPluginElements.Contains(child.Name.LocalName))
                {
                    return ([], $"FOMOD plugin contains unsupported element '{child.Name.LocalName}'.");
                }
            }

            if (!ValidateSingletons(pluginElem, ["typeDescriptor", "files", "conditionFlags"], out var pluginSingErr))
            {
                return ([], pluginSingErr);
            }

            var name = pluginElem.Attribute("name")?.Value ?? $"Plugin {pluginIdx + 1}";
            var description = ElementValue(pluginElem, "description") ?? string.Empty;
            var imagePath = AttributeOrElementValue(pluginElem, "image", "path");
            var pluginId = $"s{stepIdx}_g{groupIdx}_p{pluginIdx}";

            var typeDescriptorElem = pluginElem.Element(Name(pluginElem, "typeDescriptor"));
            if (typeDescriptorElem is null)
            {
                return ([], $"FOMOD plugin '{name}' is missing mandatory typeDescriptor element.");
            }

            if (!FomodXmlValidation.ValidateAttributes(typeDescriptorElem, [], out var tdAttrErr)) return ([], tdAttrErr);

            var children = typeDescriptorElem.Elements().ToList();
            if (children.Count != 1 || !string.Equals(children[0].Name.LocalName, "type", StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"FOMOD plugin '{name}' uses malformed or ambiguous typeDescriptor.");
            }
            var typeElem = children[0];
            if (!FomodXmlValidation.CheckChildNamespace(typeElem, typeDescriptorElem, out var nsErr)) return ([], nsErr);
            if (!FomodXmlValidation.ValidateLeafElement(typeElem, out var leafErr)) return ([], leafErr);
            if (!FomodXmlValidation.ValidateAttributes(typeElem, ["name"], out var typeAttrErr)) return ([], typeAttrErr);

            var nameAttr = typeElem.Attribute("name");
            if (nameAttr is null || string.IsNullOrWhiteSpace(nameAttr.Value))
            {
                return ([], $"FOMOD plugin '{name}' type element is missing mandatory 'name' attribute.");
            }

            var typeStr = nameAttr.Value;
            var parsedType = ParsePluginType(typeStr);
            if (parsedType is null)
                return ([], $"FOMOD plugin '{name}' uses unsupported plugin type '{typeStr}'.");
            var pluginType = parsedType.Value;

            var (files, fileErr) = ParseFileInstalls(pluginElem.Element(Name(pluginElem, "files")));
            if (fileErr is not null)
                return ([], fileErr);

            var (conditionFlags, flagErr) = ParseConditionFlags(pluginElem.Element(Name(pluginElem, "conditionFlags")));
            if (flagErr is not null)
                return ([], flagErr);

            plugins.Add(new FomodPlugin(
                pluginId,
                name,
                description,
                imagePath,
                pluginType,
                files,
                conditionFlags));
            pluginIdx++;
        }
        return (plugins, null);
    }

    private static FomodPluginType? ParsePluginType(string typeStr) => typeStr.ToLowerInvariant() switch
    {
        "required" => FomodPluginType.Required,
        "recommended" => FomodPluginType.Recommended,
        "notusable" => FomodPluginType.NotUsable,
        "optional" => FomodPluginType.Optional,
        _ => null
    };

    private static (IReadOnlyList<FomodConditionFlag> Flags, string? Error) ParseConditionFlags(XElement? container)
    {
        if (container is null) return ([], null);
        if (!FomodXmlValidation.ValidateAttributes(container, [], out var cAttrErr)) return ([], cAttrErr);

        var list = new List<FomodConditionFlag>();
        foreach (var child in container.Elements())
        {
            if (!FomodXmlValidation.CheckChildNamespace(child, container, out var nsErr)) return ([], nsErr);
            if (!string.Equals(child.Name.LocalName, "flag", StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"FOMOD conditionFlags container contains unsupported element '{child.Name.LocalName}'.");
            }
            if (!FomodXmlValidation.ValidateLeafElement(child, out var leafErr)) return ([], leafErr);
            if (!FomodXmlValidation.ValidateAttributes(child, ["name"], out var flagAttrErr)) return ([], flagAttrErr);

            var name = child.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                return ([], "FOMOD flag element is missing mandatory 'name' attribute.");
            }
            var val = child.Value;
            list.Add(new FomodConditionFlag(name, val));
        }
        return (list, null);
    }

    private static (IReadOnlyList<FomodFlagDependency> Deps, string? Error) ParseFlagDependencies(XElement? container)
    {
        if (container is null) return ([], null);

        if (!FomodXmlValidation.ValidateAttributes(container, ["operator"], out var opAttrErr))
            return ([], opAttrErr);

        var operatorAttr = container.Attribute("operator")?.Value;
        if (!string.IsNullOrWhiteSpace(operatorAttr) &&
            !string.Equals(operatorAttr, "And", StringComparison.OrdinalIgnoreCase))
        {
            return ([], $"FOMOD dependencies container specifies unsupported operator '{operatorAttr}'.");
        }

        var list = new List<FomodFlagDependency>();
        foreach (var child in container.Elements())
        {
            if (!FomodXmlValidation.CheckChildNamespace(child, container, out var nsErr)) return ([], nsErr);
            if (!string.Equals(child.Name.LocalName, "flagDependency", StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"FOMOD dependencies container contains unsupported child element '{child.Name.LocalName}'.");
            }
            if (!FomodXmlValidation.ValidateLeafElement(child, out var leafErr)) return ([], leafErr);
            if (!FomodXmlValidation.ValidateAttributes(child, ["flag", "value"], out var depAttrErr)) return ([], depAttrErr);

            var name = child.Attribute("flag")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                return ([], "FOMOD flagDependency element is missing mandatory 'flag' attribute.");
            }
            var valAttr = child.Attribute("value");
            if (valAttr is null)
                return ([], "FOMOD flagDependency element is missing mandatory 'value' attribute.");

            list.Add(new FomodFlagDependency(name, valAttr.Value));
        }
        if (list.Count == 0)
            return ([], "FOMOD dependencies container must not be empty.");
        return (list, null);
    }

    private static (IReadOnlyList<FomodConditionalInstall> Installs, string? Error) ParseConditionalInstalls(XElement? container)
    {
        if (container is null) return ([], null);
        if (!FomodXmlValidation.ValidateAttributes(container, [], out var cAttrErr)) return ([], cAttrErr);

        foreach (var child in container.Elements())
        {
            if (!FomodXmlValidation.CheckChildNamespace(child, container, out var nsErr)) return ([], nsErr);
            if (!string.Equals(child.Name.LocalName, "patterns", StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"FOMOD conditionalFileInstalls contains unsupported element '{child.Name.LocalName}'.");
            }
        }

        if (!ValidateSingletons(container, ["patterns"], out var condSingErr))
        {
            return ([], condSingErr);
        }

        var list = new List<FomodConditionalInstall>();
        var patterns = container.Element(Name(container, "patterns"));
        if (patterns is null) return (list, null);
        if (!FomodXmlValidation.ValidateAttributes(patterns, [], out var patAttrErr)) return ([], patAttrErr);

        foreach (var child in patterns.Elements())
        {
            if (!FomodXmlValidation.CheckChildNamespace(child, patterns, out var nsErr)) return ([], nsErr);
            if (!string.Equals(child.Name.LocalName, "pattern", StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"FOMOD patterns container contains unsupported element '{child.Name.LocalName}'.");
            }
        }

        foreach (var patternElem in patterns.Elements())
        {
            if (!FomodXmlValidation.ValidateAttributes(patternElem, [], out var pAttrErr)) return ([], pAttrErr);

            foreach (var child in patternElem.Elements())
            {
                if (!FomodXmlValidation.CheckChildNamespace(child, patternElem, out var nsErr)) return ([], nsErr);
                if (!string.Equals(child.Name.LocalName, "dependencies", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(child.Name.LocalName, "files", StringComparison.OrdinalIgnoreCase))
                {
                    return ([], $"FOMOD pattern contains unsupported element '{child.Name.LocalName}'.");
                }
            }

            if (!ValidateSingletons(patternElem, ["dependencies", "files"], out var patSingErr))
            {
                return ([], patSingErr);
            }

            var depsElem = patternElem.Element(Name(patternElem, "dependencies"));
            if (depsElem is null)
                return ([], "FOMOD pattern is missing mandatory 'dependencies' element.");

            var (deps, depErr) = ParseFlagDependencies(depsElem);
            if (depErr is not null)
                return ([], depErr);

            var (files, fileErr) = ParseFileInstalls(patternElem.Element(Name(patternElem, "files")));
            if (fileErr is not null)
                return ([], fileErr);

            if (files.Count > 0)
            {
                list.Add(new FomodConditionalInstall(deps, files));
            }
        }
        return (list, null);
    }

    private static XName Name(XElement parent, string localName) =>
        XName.Get(localName, parent.Name.NamespaceName);

    private static string? ElementValue(XElement? parent, string localName)
    {
        if (parent is null) return null;
        var elem = parent.Element(XName.Get(localName, parent.Name.NamespaceName));
        return elem?.Value.Trim();
    }

    private static string? AttributeOrElementValue(XElement? parent, string localName, string attrName)
    {
        if (parent is null) return null;
        var elem = parent.Element(XName.Get(localName, parent.Name.NamespaceName));
        if (elem is null) return null;
        return elem.Attribute(attrName)?.Value ?? elem.Value.Trim();
    }
}
