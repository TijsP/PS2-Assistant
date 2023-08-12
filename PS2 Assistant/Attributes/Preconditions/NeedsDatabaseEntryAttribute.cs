using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Discord;
using Discord.Interactions;

using PS2_Assistant.Data;

namespace PS2_Assistant.Attributes.Preconditions
{
    public class NeedsDatabaseEntryAttribute : PreconditionAttribute
    {
        public override async Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
        {
            if (await services.GetRequiredService<BotContext>().Guilds.AnyAsync(x => x.GuildId == context.Guild.Id))
                return PreconditionResult.FromSuccess();

            return PreconditionResult.FromError("No database entry found for this guild");
        }
    }
}
