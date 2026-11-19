using System.Globalization;
using XDesigner.Core.Metadata;
using XDesigner.Core.Xaml;

namespace XDesigner.Core.Editing;

/// <summary>
/// Design-only view state that changes what the surface shows without changing the file (§9.4): which tab of each
/// TabControl is open. Selecting a tab (its header in the outline or on the surface) or anything inside one opens it,
/// so controls can be dropped into any tab; the choice is remembered per TabControl.
/// </summary>
public sealed class DesignPreview
{
    private readonly Dictionary<int, int> _shownTab = []; // TabControl id → TabItem id
    private readonly HashSet<int> _hidden = [];

    /// <summary>Controls hidden on the surface only (the outline's eye icon, §9.6); the file keeps them as they are.</summary>
    public IReadOnlySet<int> Hidden => _hidden;

    public bool IsHidden(int id) => _hidden.Contains(id);

    /// <summary>Hides or shows a control on the surface. True when that changed something.</summary>
    public bool SetHidden(int id, bool hidden) => hidden ? _hidden.Add(id) : _hidden.Remove(id);

    /// <summary>Remembers the tabs the selection is in. True when that changes what should be shown.</summary>
    public bool Follow(XamlDocument document, ControlCatalog catalog, IEnumerable<int> selection)
    {
        var changed = false;
        foreach (var id in selection)
        {
            for (var node = document.FindById(id); node?.Parent is { } parent; node = parent)
            {
                if (IsTabItem(node, catalog) && IsTabControl(parent, catalog) && _shownTab.GetValueOrDefault(parent.Id) != node.Id)
                {
                    _shownTab[parent.Id] = node.Id;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Render-copy overrides: <c>SelectedIndex</c> on each TabControl whose remembered tab still exists; a TabControl with
    /// tabs, nothing remembered and no SelectedIndex of its own shows its first tab (at design time nothing would be open).
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> Overrides(XamlDocument document, ControlCatalog catalog)
    {
        var result = new Dictionary<int, Dictionary<string, string>>();
        void Set(int id, string attribute, string value)
        {
            if (!result.TryGetValue(id, out var attributes))
            {
                result[id] = attributes = [];
            }

            attributes[attribute] = value;
        }

        foreach (var control in document.AllNodes.Where(n => IsTabControl(n, catalog) && (!_shownTab.TryGetValue(n.Id, out var tab) || document.FindById(tab) is null)))
        {
            if (control.Element.Attribute("SelectedIndex") is null && ItemsEditing.Items(control.Element).Count > 0)
            {
                Set(control.Id, "SelectedIndex", "0");
            }
        }

        foreach (var (controlId, tabId) in _shownTab)
        {
            if (document.FindById(controlId) is { } control && document.FindById(tabId) is { } tab)
            {
                var index = ItemsEditing.Items(control.Element).IndexOf(tab.Element);
                if (index >= 0)
                {
                    Set(controlId, "SelectedIndex", index.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        // The root can't be hidden: there would be nothing left to click.
        foreach (var id in _hidden.Where(id => document.FindById(id) is { Parent: not null }))
        {
            Set(id, "IsVisible", "False");
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value);
    }

    private static bool IsTabItem(ElementNode node, ControlCatalog catalog) =>
        catalog.Resolve(node)?.DerivesFrom("Avalonia.Controls.TabItem") ?? node.TypeName == "TabItem";

    private static bool IsTabControl(ElementNode node, ControlCatalog catalog) =>
        catalog.Resolve(node)?.DerivesFrom("Avalonia.Controls.TabControl") ?? node.TypeName == "TabControl";
}
