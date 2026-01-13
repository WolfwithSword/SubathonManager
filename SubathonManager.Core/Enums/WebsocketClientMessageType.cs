using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Enums;

public enum WebsocketClientMessageType
{
    None,
    Generic, // an unconfigured websocket client
    Widget, // consumes all, but is specifically for a widget
    Overlay, // mainly for limited what to limit a refresh req to
    IntegrationSource, // send partial-integration events 
    IntegrationConsumer, // get all updates like widgets
    ValueConfig, // will consume config events but also signify sending
    Command // will send commands
}

[ExcludeFromCodeCoverage]
public static class WebsocketClientTypeHelper
{
    public static readonly WebsocketClientMessageType[] ConsumersList = new[]
    {
        WebsocketClientMessageType.Widget,
        WebsocketClientMessageType.IntegrationConsumer
    };
    
    public static readonly WebsocketClientMessageType[] ConfigConsumersList = new[]
    {
        WebsocketClientMessageType.Widget,
        WebsocketClientMessageType.IntegrationConsumer,
        WebsocketClientMessageType.ValueConfig
    };

    public static bool IsConsumer(this WebsocketClientMessageType varMessageType) =>
        ConsumersList.Contains(varMessageType);
}