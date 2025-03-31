namespace PS2_Assistant.Models.Census.WebSocket
{
    public record ContinentLockEvent(
        string EventName,
        int Timestamp,
        int WorldId,
        int ZoneId,
        int TriggeringFaction,
        int PreviousFaction,
        int VsPopulation,
        int NcPopulation,
        int TrPopulation,
        int MetagameEventId
    );
}
