using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using PS2_Assistant.Handlers;

namespace PS2_Assistant.Modules.SlashCommands
{
    
    [DontAutoRegister]
    public class SlashCommandTests : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly ClientHandler _clientHandler;
        private readonly AssistantUtils _assistantUtils;

        public SlashCommandTests(ClientHandler clientHandler, AssistantUtils assistantUtils)
        {
            _clientHandler = clientHandler;
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
            await _assistantUtils.SendMessageInChannelAsync(channel, "Test send message to channel");
        }

        [EnabledInDm(false)]
        [SlashCommand("test-user-joined", "Test the UserJoinedHandler")]
        public async Task TestUserJoinedHandler(SocketGuildUser? user = null)
        {
            await RespondAsync("Triggering UserJoinedHandler...");
            user ??= (SocketGuildUser)Context.User;
            await _clientHandler.UserJoinedHandler(user);
        }


        [EnabledInDm(false)]
        [SlashCommand("test-user-left", "Test the UserLeftHandler")]
        public async Task TestUserLeftHandler(SocketGuildUser? user = null)
        {
            await RespondAsync("Triggering UserLeftHandler...");
            user ??= (SocketGuildUser)Context.User;
            await _clientHandler.UserLeftHandler(Context.Guild, user);
        }

        [EnabledInDm(false)]
        [SlashCommand("test-guild-joined", "Test the JoinedGuildHandler")]
        public async Task TestJoinedGuildHandler()
        {
            await RespondAsync("Triggering JoinedGuildHandler...");
            await _clientHandler.JoinedGuildHandler(Context.Guild);
        }

        [EnabledInDm(false)]
        [SlashCommand("test-guild-left", "Test the JoinedGuildHandler")]
        public async Task TestLeftGuildHandler()
        {
            await RespondAsync("Triggering LeftGuildHandler...");
            await _clientHandler.LeftGuildHandler(Context.Guild);
        }
    }
}
