using System.Collections.ObjectModel;
using XDesigner.Core.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XDesigner.App.ViewModels;

/// <summary>One row of the Document Outline (§9.6): an element, its label and its children.</summary>
public sealed partial class OutlineNodeViewModel : ObservableObject
{
    public OutlineNodeViewModel(ElementNode node, OutlineNodeViewModel? parent)
    {
        Id = node.Id;
        Parent = parent;
        Name = node.Name;
        TypeName = node.TypeName;
        Caption = CaptionOf(node);
        foreach (var child in node.Children)
        {
            Children.Add(new OutlineNodeViewModel(child, this));
        }
    }

    public int Id { get; }

    public OutlineNodeViewModel? Parent { get; }

    /// <summary><c>x:Name</c>; null for unnamed elements.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(Detail))]
    public partial string? Name { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(Detail))]
    public partial string TypeName { get; private set; }

    /// <summary>Primary label: the name, or the type in brackets.</summary>
    public string Label => Name ?? $"[{TypeName}]";

    /// <summary>The element's visible text (Header, Content or Text), so unnamed tabs, items and buttons can be told apart.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    public partial string? Caption { get; private set; }

    /// <summary>Secondary label: the type next to named elements, else the element's text (<c>[TabItem] Details</c>).</summary>
    public string Detail => Name is not null ? TypeName : Caption ?? "";

    private static string? CaptionOf(ElementNode node)
    {
        foreach (var attribute in new[] { "Header", "Content", "Text", "Title" })
        {
            if ((string?)node.Element.Attribute(attribute) is { Length: > 0 } value && !Core.Properties.XamlValueSerializer.IsMarkupExtension(value))
            {
                return Shorten(Core.Properties.XamlValueSerializer.FromAttribute(value));
            }
        }

        return Core.Properties.PropertyGridBuilder.TextContentOf(node.Element) is { } text ? Shorten(text) : null;

        static string Shorten(string s) => s.ReplaceLineEndings(" ") is var one && one.Length > 28 ? one[..27] + "…" : one;
    }

    public ObservableCollection<OutlineNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>Hidden on the surface by the eye icon (§9.6, view only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EyeGlyph), nameof(EyeOpacity), nameof(TextOpacity), nameof(EyeTip))]
    public partial bool IsHiddenInDesigner { get; set; }

    /// <summary>The root can't be hidden (nothing would be left to click).</summary>
    public bool CanHide => Parent is not null;

    public string EyeGlyph => IsHiddenInDesigner ? "" : "";

    /// <summary>The eye is faint until the row is hidden, so it doesn't crowd the outline.</summary>
    public double EyeOpacity => !CanHide ? 0 : IsHiddenInDesigner ? 1 : 0.35;

    public double TextOpacity => IsHiddenInDesigner ? 0.5 : 1;

    public string EyeTip => IsHiddenInDesigner ? "Show on the form (the file is unchanged)" : "Hide on the form while designing (the file is unchanged)";

    public IEnumerable<OutlineNodeViewModel> DescendantsAndSelf() =>
        Children.SelectMany(c => c.DescendantsAndSelf()).Prepend(this);

    /// <summary>
    /// Brings this row and its subtree in line with <paramref name="node"/> in place: existing rows are kept
    /// (with their expanded state and tree containers), new ones inserted, missing ones removed.
    /// </summary>
    public void Sync(ElementNode node)
    {
        Name = node.Name;
        TypeName = node.TypeName;
        Caption = CaptionOf(node);

        // TreeView collapses a row when its last child goes away; show it open again when children come back.
        if (Children.Count == 0 && node.Children.Count > 0)
        {
            IsExpanded = true;
        }

        var desired = node.Children;
        var desiredIds = desired.Select(d => d.Id).ToHashSet();
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(Children[i].Id))
            {
                Children.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var existing = Children.Skip(i).Select((c, offset) => (c, offset)).FirstOrDefault(x => x.c.Id == desired[i].Id);
            if (existing.c is null)
            {
                Children.Insert(i, new OutlineNodeViewModel(desired[i], this));
            }
            else
            {
                if (existing.offset > 0)
                {
                    // WinUI's TreeView ignores Move notifications, so the row is taken out and put back (same view model).
                    Children.RemoveAt(i + existing.offset);
                    Children.Insert(i, existing.c);
                }

                existing.c.Sync(desired[i]);
            }
        }
    }
}
