using Discord;
using Discord.Interactions;

namespace PS2_Assistant.Modules.SlashCommands
{
    
    [DontAutoRegister]
    public class SlashCommandTests : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly AssistantUtils _assistantUtils;

        public SlashCommandTests(AssistantUtils assistantUtils)
        {
            _assistantUtils = assistantUtils;
        }

        [DefaultMemberPermissions(Discord.GuildPermission.Administrator)]
        [SlashCommand("ping", "Testing version of the /ping command, only available in this guild")]
        public async Task TestPing() =>
            await RespondAsync("Pong");

        [SlashCommand("send-logchannel-message", "Send a channel to this guilds log channel")]
        public async Task TestSendLogMessage()
        {
            await RespondAsync("Sending log message");
            await _assistantUtils.SendLogChannelMessageAsync(Context.Guild.Id, "Log channel test");
        }

        [SlashCommand("send-message-to-channel", "Sends a message to a specified channel")]
        public async Task TestSendMessageToChannel(ITextChannel channel)
        {
            await RespondAsync("Sending message to channel");
            await _assistantUtils.SendMessageInChannel(channel, "Test send message to channel");
        }
    }
}
