using Microsoft.EntityFrameworkCore;
using PS2_Assistant.Models;

namespace PS2_Assistant.Data;
public class BotContext : DbContext
{
    public readonly string dbLocation = "Assistant.db";
    public DbSet<Guild> Guilds => Set<Guild>();

    public async Task<Guild?> GetGuildByGuildIdAsync(ulong guildId)
    {
        return await Guilds
            .Include(p => p.Users)
            .Include(p => p.Channels)
            .Include(p => p.Roles)
            .SingleOrDefaultAsync(p => p.GuildId == guildId);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite("Data Source=" + dbLocation);
    }
}

