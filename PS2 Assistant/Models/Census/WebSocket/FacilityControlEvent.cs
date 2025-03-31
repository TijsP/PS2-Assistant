namespace PS2_Assistant.Models.Census.WebSocket
{
    public record FacilityControlEvent(
        int DurationHeld,
        string EventName,
        int FacilityId,
        int NewFactionId,
        int OldFactionId,
        ulong OutfitId,
        int Timestamp,
        int WorldId,
        int ZoneId
    );
}
