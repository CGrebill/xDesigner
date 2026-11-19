using XDesigner.App.ViewModels;
using XDesigner.Core.Properties;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace XDesigner.App.Controls;

/// <summary>The Events tab (§9.3): the selected control's events and the code-behind methods they're connected to.</summary>
public sealed partial class PropertiesPane
{
    private readonly Dictionary<string, IReadOnlyList<EventInfoDto>> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _eventHeaders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedEvents = new(StringComparer.Ordinal);
    private Dictionary<string, EventRowView> _eventViews = new(StringComparer.Ordinal);
    private IReadOnlyList<EventRow> _eventRows = [];
    private List<UIElement> _eventsShown = [];
    private EventEditorContext? _eventContext;

    private EventEditorContext EventContext => _eventContext ??= new EventEditorContext
    {
        Connect = (ids, evt, name) => Document?.ConnectHandler(Live(ids), evt, name),
        Disconnect = (ids, name) => Document?.DisconnectHandler(Live(ids), name),
        CompatibleMethods = evt => Document?.CompatibleMethods(evt) ?? [],
    };

    private IReadOnlyList<int> Live(IReadOnlyList<int> ids) =>
        Document is { } doc ? ids.Where(id => doc.Xaml.FindById(id) is not null).ToList() : [];

    private void FlushEventEdits()
    {
        foreach (var view in _eventViews.Values.Where(v => v.HasPendingEdit).ToList())
        {
            view.Flush();
        }
    }

    /// <summary>Fetches event lists for types not seen yet. True when something new arrived.</summary>
    private async Task<bool> LoadEventsAsync(DocumentViewModel doc, IReadOnlyList<ElementNode> nodes)
    {
        var changed = false;
        foreach (var type in nodes.Select(n => doc.Catalog.Resolve(n)?.FullName).OfType<string>().Distinct().Where(t => !_events.ContainsKey(t)).ToList())
        {
            if (await doc.GetEventsAsync(type) is { } events)
            {
                _events[type] = events;
                changed = true;
            }
        }

        return changed;
    }

    private void RenderEvents(DocumentViewModel doc, IReadOnlyList<int> ids)
    {
        var rows = doc.Catalog.IsEmpty ? [] : EventGridBuilder.Build(doc.Xaml, doc.Catalog, ids, t => _events.GetValueOrDefault(t));
        if (rows.Count == 0)
        {
            ShowEventsMessage(doc.Catalog.IsEmpty || ids.Any(id => doc.Xaml.FindById(id) is { } n && doc.Catalog.Resolve(n) is { } t && !_events.ContainsKey(t.FullName))
                ? "Loading events…"
                : ids.Count > 1 ? "These controls have no events in common." : "This control has no events the designer can connect.");
            return;
        }

        var views = new Dictionary<string, EventRowView>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!_eventViews.TryGetValue(row.Name, out var view))
            {
                view = new EventRowView(row, EventContext);
            }

            view.Update(row, ids);
            views[row.Name] = view;
        }

        _eventViews = views;
        _eventRows = rows;
        ApplyEventFilter();
    }

    /// <summary>Same toolbar as the Properties tab: search, A–Z, only connected events, advanced.</summary>
    private void ApplyEventFilter()
    {
        if (_eventRows.Count == 0)
        {
            return;
        }

        var search = SearchBox.Text.Trim();
        var setOnly = SetOnlyToggle.IsChecked == true;
        var advanced = AdvancedToggle.IsChecked == true;
        var visible = _eventRows.Where(r =>
            (advanced || !r.IsAdvanced || r.IsSet)
            && (!setOnly || r.IsSet)
            && (search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();

        var elements = new List<UIElement>();
        if (AlphabeticalToggle.IsChecked == true)
        {
            elements.AddRange(visible.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => (UIElement)_eventViews[r.Name].View));
        }
        else
        {
            foreach (var group in visible.GroupBy(r => r.Category))
            {
                var collapsed = _collapsedEvents.Contains(group.Key) && search.Length == 0;
                elements.Add(CategoryHeader(_eventHeaders, _collapsedEvents, group.Key, collapsed, ApplyEventFilter));
                if (!collapsed)
                {
                    elements.AddRange(group.Select(r => (UIElement)_eventViews[r.Name].View));
                }
            }
        }

        EventsScroll.Visibility = Visibility.Visible;
        EventsMessage.Visibility = elements.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EventsMessage.Text = elements.Count == 0 ? "No events match." : "";
        if (!elements.SequenceEqual(_eventsShown))
        {
            EventsPanel.Children.Clear();
            foreach (var element in elements)
            {
                EventsPanel.Children.Add(element);
            }

            _eventsShown = elements;
        }
    }

    private void ShowEventsMessage(string message)
    {
        EventsPanel.Children.Clear();
        _eventsShown = [];
        _eventRows = [];
        EventsScroll.Visibility = Visibility.Collapsed;
        EventsMessage.Visibility = Visibility.Visible;
        EventsMessage.Text = message;
    }
}
