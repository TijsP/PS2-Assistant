namespace PS2_Assistant.Models.Census.API
{
    public record NameCollection(
    string? First,
    string? FirstLower
        );

    public record OutfitDataCollection(
        ulong? Outfit_id,
        string? Alias,
        string? AliasLower,
        int? MemberCount,
        OutfitRanksCollection[]? Ranks
        );

    public record OutfitRanksCollection(
        int Ordinal,
        string Name
        );
}
