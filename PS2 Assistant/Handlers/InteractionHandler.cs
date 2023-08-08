﻿using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using System.Reflection;

using PS2_Assistant.Logger;
using Serilog.Events;

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
            await _interactionService.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

            _client.Ready += ClientReadyHandler;
            _client.InteractionCreated += HandleInteraction;
        }

        private async Task SlashCommandExecutedHandler(SlashCommandInfo info, IInteractionContext context, IResult result)
        {
            if (!result.IsSuccess)
            {
                switch (result.Error)
                {
                    case InteractionCommandError.UnmetPrecondition:
                        await context.Interaction.RespondAsync($"Unmet precondition: {result.ErrorReason}");
                        _logger.SendLog(LogEventLevel.Warning, context.Guild.Id, result.ErrorReason, caller: info.MethodName);
                        break;
                    default:
                        await context.Interaction.RespondAsync($"An error occurred while executing: {result.ErrorReason}");
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
            _logger.SendLog(LogEventLevel.Information, 0, "Bot ready");
#if DEBUG
            await _interactionService.RegisterCommandsToGuildAsync(Program.testGuildID, false);
            await _interactionService.AddModulesToGuildAsync(Program.testGuildID, modules: _interactionService.Modules.Where(x => x.DontAutoRegister == true).ToArray());
#else
            await _interactionService.RegisterCommandsGloballyAsync(true);
#endif
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