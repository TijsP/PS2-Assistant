namespace PS2_Assistant.Models.Census.API
{
    public record OutfitNameQuery(
        ulong OutfitId,
        string Name,
        string Alias) : ICensusObject
    {
        public static string CollectionQuery => "outfit_member_extended/?c:show=outfit_id,name,alias";
    }
}
