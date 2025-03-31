namespace PS2_Assistant.Models.Census.WebSocket
{
    public record MetagameEvent(
        int MetagameEventId,
        int MetagameEventState,
        float FactionVs,
        float FactionNc,
        float FactionTr,
        float ExperienceBonus,
        int Timestamp,
        int ZoneId,
        int WorldId,
        string EventName);
}
