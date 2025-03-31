namespace PS2_Assistant.Models.Census.WebSocket
{
    public record Heartbeat(
        ServerEndpoints Online,
        ServiceTypeEnum Service,
        DateTime Timestamp,
        MessageTypeEnum Type
    );
}
