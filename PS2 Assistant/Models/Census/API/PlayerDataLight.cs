namespace PS2_Assistant.Models.Census.API
{
    /// <inheritdoc cref="ICensusObject"/>
    public record PlayerDataLight(
        ulong CharacterId,
        NameCollection Name,
        OutfitDataCollection Outfit
        ) : ICensusObject
    {
        public static string CollectionQuery => "character_name/?c:join=outfit_member_extended^on:character_id^inject_at:outfit^show:alias&c:limit=6&c:exactMatchFirst=true";
    }
}
