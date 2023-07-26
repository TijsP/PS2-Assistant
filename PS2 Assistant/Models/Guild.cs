﻿using System.ComponentModel.DataAnnotations;

namespace PS2_Assistant.Models;
public class Guild
{
    [Key]
    public ulong GuildId { get; set; }

    public Channels? Channels { get; set; }
    public Roles? Roles { get; set; }
    public ICollection<User> Users { get; } = new List<User>();
    public string? OutfitTag { get; set; }
    public bool AskNicknameUponWelcome { get; set; } = false;
    public bool SendWelcomeMessage { get; set; } = false;
}

