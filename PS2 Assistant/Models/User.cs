using System.ComponentModel.DataAnnotations;

public class User
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }
    public ulong SocketUserId { get; set; }
    public string? CurrentOutfit { get; set; }
    public string? CharacterName { get; set; }
}