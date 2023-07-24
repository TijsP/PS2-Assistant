﻿using System.ComponentModel.DataAnnotations;

public class Channels
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }
    public ulong? WelcomeChannel { get; set; }
    public ulong? LogChannel { get; set; }
}

