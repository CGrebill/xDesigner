using System.Globalization;
using System.Xml.Linq;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Editing;

/// <summary>"Keep this size" for the preview size (§8.4).</summary>
public sealed partial class DesignerActions
{
    /// <summary>
    /// Makes <paramref name="width"/> × <paramref name="height"/> the document's own size. A Window gets <c>Width</c>/
    /// <c>Height</c> (the size it opens at) where its XAML can set them (<see cref="Frameworks.FrameworkProfile.WindowSizeInXaml"/>); anything else gets <c>d:DesignWidth</c>/<c>d:DesignHeight</c>, which only the
    /// designer uses, declaring <c>xmlns:d</c>, <c>xmlns:mc</c> and <c>mc:Ignorable="d"</c> when the file lacks them.
    /// One undo step; false when nothing changed.
    /// </summary>
    public bool SetDesignSize(double width, double height, bool isWindow)
    {
        isWindow &= _document.Profile.WindowSizeInXaml;
        var root = _document.Root;
        var w = XamlValueText.Double(Math.Round(width));
        var h = XamlValueText.Double(Math.Round(height));
        var commands = new List<IUndoableCommand>();

        if (isWindow)
        {
            Add(commands, root, "Width", w);
            Add(commands, root, "Height", h);
        }
        else
        {
            var element = root.Element;
            var d = _document.Profile.DesignNamespace;
            var designPrefix = element.PrefixOf(d);
            if (designPrefix is null)
            {
                designPrefix = "d";
                commands.Add(new SetAttributeCommand(root.Id, XNamespace.Xmlns + designPrefix, d.NamespaceName));
            }

            var mcPrefix = element.PrefixOf(XamlNamespaces.MarkupCompatibility);
            if (mcPrefix is null)
            {
                mcPrefix = "mc";
                commands.Add(new SetAttributeCommand(root.Id, XNamespace.Xmlns + mcPrefix, XamlNamespaces.MarkupCompatibility));
            }

            XNamespace mc = XamlNamespaces.MarkupCompatibility;
            var ignorable = ((string?)element.Attribute(mc + "Ignorable") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!ignorable.Contains(designPrefix))
            {
                ignorable.Add(designPrefix);
                commands.Add(new SetAttributeCommand(root.Id, mc + "Ignorable", string.Join(' ', ignorable)));
            }

            Add(commands, root, d + "DesignWidth", w);
            Add(commands, root, d + "DesignHeight", h);
        }

        if (commands.Count == 0)
        {
            return false;
        }

        _undo.Execute(new CompositeCommand(
            string.Create(CultureInfo.InvariantCulture, $"Set {(isWindow ? "window" : "design")} size to {w} × {h}"), commands));
        return true;
    }

    private static void Add(List<IUndoableCommand> commands, ElementNode node, XName attribute, string value)
    {
        if ((string?)node.Element.Attribute(attribute) != value)
        {
            commands.Add(new SetAttributeCommand(node.Id, attribute, value));
        }
    }
}
