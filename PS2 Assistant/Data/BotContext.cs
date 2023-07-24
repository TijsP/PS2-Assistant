using Microsoft.EntityFrameworkCore;
using PS2_Assistant.Models;

namespace PS2_Assistant.Data;
public class BotContext : DbContext
{
    //public BotContext(DbContextOptions<BotContext> options) : base(options) { }

    public DbSet<Guild> Guilds => Set<Guild>();

    public async Task<Guild?> getGuildByGuildIdAsync(ulong guildId)
    {
        return await Guilds
            .Include(p => p.Users)
            .Include(p => p.Channels)
            .Include(p => p.Roles)
            .SingleOrDefaultAsync(p => p.GuildId == guildId);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        //base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseSqlite("Data Source=Assistant.db");
    }
}

