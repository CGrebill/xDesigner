using MessagePack;

namespace XDesigner.Protocol.Metadata;

/// <summary>
/// An event a control raises that a XAML attribute can connect to a code-behind method (§9.3, §10): the
/// control's public CLR events with an <c>(sender, e)</c> handler shape, which covers Avalonia's routed events
/// (they all have CLR wrappers).
/// </summary>
/// <param name="Name">Event name, which is also the XAML attribute (<c>Click</c>).</param>
/// <param name="IsRouted">A routed event (bubbles up the tree) as opposed to a plain CLR event.</param>
/// <param name="SenderType">C# type of the first handler parameter (<c>object?</c> for EventHandler-style delegates).</param>
/// <param name="ArgsType">C# type of the second parameter, without namespace (<c>RoutedEventArgs</c>, <c>TextChangedEventArgs</c>).</param>
/// <param name="ArgsNamespace">Namespace of <paramref name="ArgsType"/> (for the <c>using</c> directive).</param>
/// <param name="ArgsBaseTypes">Simple names of the args type's base classes (<c>RoutedEventArgs</c>, <c>EventArgs</c>): a method taking any of these can handle the event.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record EventInfoDto(
    string Name,
    bool IsRouted,
    string SenderType,
    string ArgsType,
    string ArgsNamespace,
    string[] ArgsBaseTypes);
