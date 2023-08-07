using Discord.Interactions;

namespace PS2_Assistant.Modules.SlashCommands
{
    
    [DontAutoRegister]
    public class SlashCommandTests : InteractionModuleBase<SocketInteractionContext>
    {
        [DefaultMemberPermissions(Discord.GuildPermission.Administrator)]
        [SlashCommand("ping", "Testing version of the /ping command, only available in this guild")]
        public async Task TestPing() =>
            await RespondAsync("Pong");
    }
}
