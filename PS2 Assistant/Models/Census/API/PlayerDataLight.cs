namespace PS2_Assistant.Models.Census.API
{
    /// <inheritdoc cref="ICensusObject"/>
    public record PlayerDataLight(
        ulong CharacterId,
        NameCollection Name,
        OutfitDataCollection? Outfit
        ) : ICensusObject
    {
        /// <summary>
        /// Returns 1 by default. Append <code>&c:limit=X</code> to get more results. Append <code>&name.first_lower=*</code> for a specific character
        /// </summary>
        public static string CollectionQuery => "character_name/?c:join=outfit_member_extended^on:character_id^inject_at:outfit^show:alias'alias_lower&c:exactMatchFirst=true";
    }
}
