namespace PS2_Assistant.Models.Census.WebSocket
{
    public enum MessageTypeEnum
    {
        Heartbeat,
        ServiceMessage,
        ServiceStateChanged,
        ConnectionStateChanged
    }
}
