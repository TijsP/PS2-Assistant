using System.Reflection;
using Microsoft.Extensions.Configuration;

using Serilog.Events;

using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using PS2_Assistant.Attributes;
using PS2_Assistant.Logger;

namespace PS2_Assistant.Handlers
{
    public class InteractionHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly InteractionService _interactionService;
        private readonly SourceLogger _logger;
        private readonly IServiceProvider _services;
        private readonly IConfiguration _configuration;

        public InteractionHandler(DiscordSocketClient client, InteractionService interactionService, SourceLogger logger, IServiceProvider services, IConfiguration config)
        {
            _client = client;
            _interactionService = interactionService;
            _logger = logger;
            _services = services;
            _configuration = config;
        }

        public async Task InitializeAsync()
        {
            _interactionService.Log += LogHandler;
            _interactionService.SlashCommandExecuted += SlashCommandExecutedHandler;
            _interactionService.ComponentCommandExecuted += ComponentCommandExecutedHandler;
            await _interactionService.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

            _client.Ready += ClientReadyHandler;
            _client.InteractionCreated += HandleInteraction;
        }

        private async Task SlashCommandExecutedHandler(SlashCommandInfo info, IInteractionContext context, IResult result)
        {
            await CheckResultAsync(info, context, result);
        }

        private async Task ComponentCommandExecutedHandler(ComponentCommandInfo info, IInteractionContext context, IResult result)
        {
            await CheckResultAsync(info, context, result);
        }

        private async Task CheckResultAsync(ICommandInfo info, IInteractionContext context, IResult result)
        {
            if (!result.IsSuccess)
            {
                switch (result.Error)
                {
                    case InteractionCommandError.UnmetPrecondition:
                        if (context.Interaction.HasResponded)
                            await context.Interaction.FollowupAsync($"Unmet precondition for user <@{context.User.Id}>: {result.ErrorReason}", allowedMentions: AllowedMentions.None);
                        else
                            await context.Interaction.RespondAsync($"Unmet precondition for user <@{context.User.Id}>: {result.ErrorReason}", allowedMentions: AllowedMentions.None);
                        _logger.SendLog(LogEventLevel.Warning, context.Guild.Id, result.ErrorReason, caller: info.MethodName);
                        break;
                    default:
                        if (context.Interaction.HasResponded)
                            await context.Interaction.FollowupAsync($"An error occurred while executing: {result.ErrorReason}");
                        else
                            await context.Interaction.RespondAsync($"An error occurred while executing: {result.ErrorReason}");
                        _logger.SendLog(LogEventLevel.Warning, context.Guild.Id, result.ErrorReason, caller: info.MethodName);
                        break;
                }
            }
        }

        private Task LogHandler(LogMessage message)
        {
            _logger.SendLog(message);
            return Task.CompletedTask;
        }

        private async Task ClientReadyHandler()
        {
#if DEBUG
            await _interactionService.RegisterCommandsToGuildAsync(Convert.ToUInt64(_configuration.GetConnectionString("TestGuildId")), false);
            await _interactionService.AddModulesToGuildAsync(Convert.ToUInt64(_configuration.GetConnectionString("TestGuildId")), modules: _interactionService.Modules.Where(x => x.DontAutoRegister == true).ToArray());
#else
            await _interactionService.RegisterCommandsGloballyAsync(true);
            //  Allow (sub)modules marked with the BotOwnerCommand to be accessed from the test server
            await _interactionService.AddModulesToGuildAsync(Convert.ToUInt64(_configuration.GetConnectionString("TestGuildId")),
                modules: _interactionService.Modules
                .Where(x =>
                    x.Attributes.Any(attr =>
                        attr.GetType() == typeof(BotOwnerCommandAttribute)))
                .ToArray());
#endif
            _logger.SendLog(LogEventLevel.Information, null, "Bot ready");
        }

        private async Task HandleInteraction(SocketInteraction interaction)
        {
            try
            {
                // Create an execution context that matches the generic type parameter of your InteractionModuleBase<T> modules.
                var context = new SocketInteractionContext(_client, interaction);

                // Execute the incoming command.
                var result = await _interactionService.ExecuteCommandAsync(context, _services);

                if (!result.IsSuccess)
                    switch (result.Error)
                    {
                        //case InteractionCommandError.UnmetPrecondition:
                        //    // implement
                        //    break;
                        default:
                            ulong? guildId = interaction.GuildId;
                            guildId ??= 0;
                            _logger.SendLog(LogEventLevel.Error, guildId.Value, "Fatal error occured while handling an interaction of type {InteractionType}: {ErrorType} ({ErrorReason})", interaction.Type, nameof(result.Error), result.ErrorReason);
                            break;
                    }
            }
            catch
            {
                // If Slash Command execution fails it is most likely that the original interaction acknowledgement will persist. It is a good idea to delete the original
                // response, or at least let the user know that something went wrong during the command execution.
                if (interaction.Type is InteractionType.ApplicationCommand)
                    await interaction.GetOriginalResponseAsync().ContinueWith(async (msg) => await msg.Result.DeleteAsync());
            }
        }
    }
}
