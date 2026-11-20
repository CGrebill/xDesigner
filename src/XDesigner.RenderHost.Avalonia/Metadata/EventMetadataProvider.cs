using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Interactivity;
using XDesigner.Protocol.Metadata;

namespace XDesigner.RenderHost.Metadata;

/// <summary>
/// Events a control type can connect to code-behind handlers (§9.3, §14.3): public CLR events whose delegate is
/// <c>void (sender, e)</c>. Signatures come from the delegate's <c>Invoke</c> method, written as C# types.
/// </summary>
internal sealed class EventMetadataProvider
{
    private readonly ConcurrentDictionary<Type, EventInfoDto[]> _cache = new();

    public EventInfoDto[] For(Type type) => _cache.GetOrAdd(type, Describe);

    private static EventInfoDto[] Describe(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle); // routed events register statically
        }

        var routed = new HashSet<string>(StringComparer.Ordinal);
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var e in RoutedEventRegistry.Instance.GetRegistered(t))
            {
                routed.Add(e.Name);
            }
        }

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
                routed.Contains(evt.Name),
                sender.ParameterType == typeof(object) ? "object?" : CSharpName(sender.ParameterType, qualify: true),
                CSharpName(argsType, qualify: false),
                argsType.Namespace ?? "",
                CSharpTypeNames.BaseTypeNames(argsType).ToArray()));
        }

        return result.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc cref="CSharpTypeNames.Of"/>
    internal static string CSharpName(Type type, bool qualify) => CSharpTypeNames.Of(type, qualify);
}
