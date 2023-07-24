using System.ComponentModel.DataAnnotations;

public class Roles
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }
    public ulong? MemberRole { get; set; }
    public ulong? NonMemberRole { get; set; }
}

