using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.UI.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.RenderHost.Metadata;

/// <summary>
/// Events an element type can connect to code-behind handlers (§9.3, §14.3): public CLR events whose delegate is
/// <c>void (sender, e)</c>. An event is routed when the type (or a base) has a static <c>{Name}Event</c> RoutedEvent.
/// </summary>
internal sealed class EventMetadataProvider
{
    private readonly ConcurrentDictionary<Type, EventInfoDto[]> _cache = new();

    public EventInfoDto[] For(Type type) => _cache.GetOrAdd(type, Describe);

    private static EventInfoDto[] Describe(Type type)
    {
        var result = new List<EventInfoDto>();
        foreach (var evt in type.GetEvents(BindingFlags.Public | BindingFlags.Instance).GroupBy(e => e.Name).Select(g => g.First()))
        {
            if (evt.EventHandlerType?.GetMethod("Invoke") is not { } invoke || invoke.ReturnType != typeof(void)
                || invoke.GetParameters() is not [var sender, var args])
            {
                continue;
            }

            var argsType = args.ParameterType;
            result.Add(new EventInfoDto(
                evt.Name,
                IsRouted(type, evt.Name),
                sender.ParameterType == typeof(object) ? "object" : CSharpTypeNames.Of(sender.ParameterType, qualify: true),
                CSharpTypeNames.Of(argsType, qualify: false),
                argsType.Namespace ?? "",
                CSharpTypeNames.BaseTypeNames(argsType).ToArray()));
        }

        return result.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }

    private static bool IsRouted(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        return typeof(RoutedEvent).IsAssignableFrom(type.GetProperty(name + "Event", flags)?.PropertyType ?? type.GetField(name + "Event", flags)?.FieldType);
    }
}
