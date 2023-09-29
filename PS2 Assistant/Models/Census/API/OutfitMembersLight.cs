namespace PS2_Assistant.Models.Census.API
{
    /// <inheritdoc />
    public record OutfitMembersLight(
        ulong CharacterId,
        string AliasLower,
        PlayerDataLight? PlayerName     //  player_name can be null when the character has been deleted
        ) : ICensusObject
    {
        /// <summary>
        /// Returns the first 5000 outfit members. Append <code>&c:start=XXXX</code> to get the next 5000 outfit members
        /// </summary>
        public static string CollectionQuery => "outfit_member_extended/?c:join=character_name^on:character_id^to:character_id^inject_at:player_name&c:show=character_id,alias_lower&c:limit=5000";
    }
}
