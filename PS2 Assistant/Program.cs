using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

using Discord;
using Discord.WebSocket;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Serilog;
using Serilog.Formatting.Json;
using Serilog.Events;
using Serilog.Context;
using Serilog.Templates;
using Serilog.Templates.Themes;

using PS2_Assistant.Data;
using PS2_Assistant.Models;

using JsonSerializer = System.Text.Json.JsonSerializer;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "Program will not be used as a library")]
public class Program
{

    public static Task Main() => new Program().MainAsync();

    private bool stopBot = false;
    private bool clientIsReady = false;
    private readonly DiscordSocketClient _botclient = new( new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers });
    private readonly HttpClient _censusclient = new();
    private readonly rResponse? apiResponse;
    private readonly JsonSerializerOptions defaultCensusJsonDeserializeOptions = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IConfiguration appSettings = new ConfigurationBuilder().AddJsonFile("appsettings.json").SetBasePath(Environment.CurrentDirectory).Build();
    private readonly BotContext _botDatabase = new();

    public async Task MainAsync()
    {
        if (appSettings.GetConnectionString("CensusAPIKey") is null ||
            appSettings.GetConnectionString("DiscordBotToken") is null ||
            appSettings.GetConnectionString("LoggerURL") is null ||
            appSettings.GetConnectionString("LoggerAPIKey") is null)
        {
            var exception = new KeyNotFoundException("Missing connection strings in appsettings.json");
            await Console.Out.WriteLineAsync(JsonConvert.SerializeObject(exception));
            throw exception;
        }

        //  Setup the logger
        string outputTemplate = "[{@t:HH:mm:ss} {@l:u3}] [{Source}]{#if GuildId is not null} (Guild: {GuildId}){#end} {@m:lj}\n{@e}";
        var expression = new ExpressionTemplate(outputTemplate, theme: TemplateTheme.Literate);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(new JsonFormatter(), "Logs/log.json")
            .WriteTo.Seq(appSettings.GetConnectionString("LoggerURL")!, apiKey: appSettings.GetConnectionString("LoggerAPIKey"))
            .WriteTo.Console(expression)
            .CreateLogger();

        using (LogContext.PushProperty("Source", nameof(MainAsync)))
            Log.Information("Bot starting up...");

        //  Setup bot client
        _botclient.Log += BotLogHandler;
        _botclient.Ready += Client_Ready;
        _botclient.SlashCommandExecuted += SlashCommandHandler;
        _botclient.AutocompleteExecuted += AutocompleteExecutedHandler;
        _botclient.SelectMenuExecuted += MenuHandler;
        _botclient.UserJoined += UserJoined;
        _botclient.UserLeft += UserLeftHandler;
        _botclient.ButtonExecuted += ButtonExecutedHandler;
        _botclient.ModalSubmitted += ModalSubmittedHandler;
        _botclient.JoinedGuild += JoinedGuildHandler;
        _botclient.LeftGuild += LeftGuildHandler;

        await _botclient.LoginAsync(TokenType.Bot, appSettings.GetConnectionString("DiscordBotToken"));
        await _botclient.StartAsync();

        //  Setup database

        //  Setup Census API HTTP client
        _censusclient.DefaultRequestHeaders.Accept.Clear();

        //  Wait for client to be ready
        while (!clientIsReady) ;

        using(LogContext.PushProperty("Source", nameof(MainAsync)))
            Log.Information("Bot is running");

        //  Check whether the bot was added to/removed from any guilds while offline
        List<ulong> subscribedGuilds =  _botclient.Guilds.ToList().Select(x => x.Id).ToList();
        List<ulong> addedGuilds = subscribedGuilds.Except(_botDatabase.Guilds.Select(x => x.GuildId).ToList()).ToList();
        List<ulong> removedGuilds = _botDatabase.Guilds.Select(x => x.GuildId).ToList().Except(subscribedGuilds).ToList();

        if (addedGuilds.Count != 0)
        {
            foreach (ulong guildId in addedGuilds)
            {
                SendLog(LogEventLevel.Information, guildId, "Bot was added to guild while offline");
                await AddGuild(guildId);
            }
        }
        if (removedGuilds.Count != 0)
        {
            foreach (ulong guildId in removedGuilds)
            {
                SendLog(LogEventLevel.Information, guildId, "Bot was removed from guild while offline");
                await RemoveGuild(guildId);
            }
        }

        //  Handle command line input
        while (!stopBot)
        {
            if (Console.ReadLine() is string fullCommand)
            {
                if (fullCommand.StartsWith("help"))
                    await Console.Out.WriteLineAsync( "\nList of commands:\n" +
                                                        "help:      displays a list of commands\n" +
                                                        "stop:      stops the program\n" +
                                                        "info:      returns information about the bot status\n" +
                                                        "db-info:   returns information about the database (use \"db-info help\" for more information)");
                else if (fullCommand.StartsWith("stop"))
                stopBot = true;
                else if (fullCommand.StartsWith("info"))
                    await Console.Out.WriteLineAsync(await CLIInfo());
                else if (fullCommand.StartsWith("db-info"))
                {
                    bool list = false;
                    ulong? id = null;
                    bool guildNotFound = false;
                    fullCommand = fullCommand.Trim("db-info ".ToCharArray());

                    try
                    {
                        if (fullCommand.StartsWith("list"))
                            list = true;
                        else if (fullCommand.StartsWith("help"))
                        {
                            await Console.Out.WriteLineAsync( "\nUsage: db-info [list] [help] [guildId]\n" +
                                                                "   list:       include \"list\" to get a list of all guilds registered in the database\n" +
                                                                "   help:       include \"help\" to display the help page of this command\n" +
                                                                "   guildId:    specify a guild ID to get all data in the database related to that guild\n" +
                                                                "   none:       returns information about the database itself");
                            continue;
                        }
                        else if (!fullCommand.IsNullOrEmpty())
                        {
                            id = ulong.Parse(fullCommand);
                            if (!_botDatabase.Guilds.Any(x => x.GuildId == id))
                                guildNotFound = true;
                        }
                    }
                    catch
                    {
                        guildNotFound = true;
                    }

                    if (guildNotFound)
                    {
                        await Console.Out.WriteLineAsync($"No guild found in database with ID {fullCommand}");
                        continue;
                    }

                    await Console.Out.WriteLineAsync(await CLIDatabaseInfo(list, id));
                }
                else
                    await Console.Out.WriteLineAsync($"command not recognized: {fullCommand}. Use \"help\" for a list of commands");
        }
        }
        await _botclient.StopAsync();
        Log.CloseAndFlush();
    }

    readonly List<ApplicationCommandProperties> guildApplicationCommandProperties = new();
    readonly List<ApplicationCommandProperties> globalApplicationCommandProperties = new();
    public async Task Client_Ready()
    {
        SocketGuild guild = _botclient.GetGuild(testGuildID);

        var guildCommandPing = new SlashCommandBuilder();
        guildCommandPing.WithName("ping");
        guildCommandPing.WithDescription("Play ping-pong!");

        var guildCommandFirstWithRank = new SlashCommandBuilder()
            .WithName("first-with-rank")
            .WithDescription("The member who has had the given rank the longest")
            .AddOption("ordinal",
                ApplicationCommandOptionType.Integer,
                "The ordinal representation of the requested rank",
                isRequired: true,
                minValue: 1,
                maxValue: 8);
        guildApplicationCommandProperties.Add(guildCommandFirstWithRank.Build());

        var guildCommandFindPromotableCharWithRank = new SlashCommandBuilder()
            .WithName("find-promotable-member-at-rank")
            .WithDescription("Returns all members that have been at a given rank for a certain period of time")
            .AddOption(
                "outfit",
                ApplicationCommandOptionType.String,
                "The outfit of interest",
                isRequired: true,
                minLength: 4,
                maxLength: 4
                )
            .AddOption(
                "min-activity",
                ApplicationCommandOptionType.Integer,
                "The required period of activity to be eligible for promotion, in months",
                isRequired: true,
                minValue: 1,
                maxValue: 12
            )
            .AddOption(
                "max-inactivity",
                ApplicationCommandOptionType.Integer,
                "The maximum duration of inactivity to still be eligible for promotion, in months",
                isRequired: false
                );
        guildApplicationCommandProperties.Add(guildCommandFindPromotableCharWithRank.Build());

        var commandHelp = new SlashCommandBuilder()
            .WithName("help")
            .WithDescription("Shows a list of commands and their parameters")
            .AddOption(new SlashCommandOptionBuilder()
                        .WithName("page")
                        .WithDescription("Which page to display")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("number", ApplicationCommandOptionType.Integer, "The page number", minValue: 1, isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                        .WithName("setup")
                        .WithDescription("Details how to setup the bot on this server")
                        .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                        .WithName("command")
                        .WithDescription("Get help for a specific command")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("name", ApplicationCommandOptionType.String, "The name of the command", isAutocomplete: true, isRequired: true));
        globalApplicationCommandProperties.Add(commandHelp.Build());

        var commandSendNicknamePoll = new SlashCommandBuilder()
            .WithName("send-nickname-poll")
            .WithDescription("Manually sends a poll that asks users for their in-game character name")
            .WithDMPermission(false)
            .AddOption("channel", ApplicationCommandOptionType.Channel, "The channel in which the poll will be sent");
        globalApplicationCommandProperties.Add(commandSendNicknamePoll .Build());

        var commandSendWelcomeMessage = new SlashCommandBuilder()
            .WithName("send-welcome-message")
            .WithDescription("Send a welcome message when a new user joins the server")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("welcome", ApplicationCommandOptionType.Boolean, "Whether a welcome should be sent or not", isRequired: true);
        globalApplicationCommandProperties.Add(commandSendWelcomeMessage.Build());

        var commandIncludeNicknamePollInWelcomeMessage = new SlashCommandBuilder()
            .WithName("include-nickname-poll")
            .WithDescription("Includes a nickname poll in the welcome message when a new user joins")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("include", ApplicationCommandOptionType.Boolean, "Whether to include the poll or not", isRequired: true);
        globalApplicationCommandProperties.Add(commandIncludeNicknamePollInWelcomeMessage.Build());

        var commandSetLogChannel = new SlashCommandBuilder()
            .WithName("set-log-channel")
            .WithDescription("Sets the channel where this bot's log messages will be sent")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("log-channel", ApplicationCommandOptionType.Channel, "Sets the log channel", isRequired: true);
        globalApplicationCommandProperties.Add(commandSetLogChannel.Build());

        var commandSetWelcomeChannel = new SlashCommandBuilder()
            .WithName("set-welcome-channel")
            .WithDescription("Sets the channel where new users will be greeted by the bot")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("welcome-channel", ApplicationCommandOptionType.Channel, "Sets the welcome channel", isRequired: true);
        globalApplicationCommandProperties.Add(commandSetWelcomeChannel.Build());

        var commandSetMemberRole = new SlashCommandBuilder()
            .WithName("set-member-role")
            .WithDescription("Sets the role users will get if their character is in the outfit represented by this server")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("member-role", ApplicationCommandOptionType.Role, "Sets the member role", isRequired: true);
        globalApplicationCommandProperties.Add(commandSetMemberRole.Build());

        var commandSetNonMemberRole = new SlashCommandBuilder()
            .WithName("set-non-member-role")
            .WithDescription("Sets the role users will get if their character isn't in the outfit represented by this server")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("non-member-role", ApplicationCommandOptionType.Role, "Sets the non-member role", isRequired: true);
        globalApplicationCommandProperties.Add(commandSetNonMemberRole.Build());

        var commandSetMainOutfit = new SlashCommandBuilder()
            .WithName("set-main-outfit")
            .WithDescription("Sets the the main outfit represented by this server")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("outfit-tag", ApplicationCommandOptionType.String, "The tag of the outfit", isRequired: true, minLength: 1, maxLength: 4);
        globalApplicationCommandProperties.Add(commandSetMainOutfit.Build());

        //  Test commands

        var commandTest = new SlashCommandBuilder()
            .WithName("test-command")
            .WithDescription("just a test")
            .AddOption("required-parameter", ApplicationCommandOptionType.Boolean, "test required parameters", isRequired: true)
            .AddOption("optional-parameter", ApplicationCommandOptionType.Boolean, "test optional parameters", isRequired: false)
            .AddOption("unspecified-parameter", ApplicationCommandOptionType.Boolean, "test unspecified parameters");
        guildApplicationCommandProperties.Add(commandTest.Build());

        var guildCommandTestUserJoined = new SlashCommandBuilder()
            .WithName("test-user-join")
            .WithDescription("Test the UserJoined function")
            .WithDMPermission(false);
        guildApplicationCommandProperties.Add(guildCommandTestUserJoined.Build());

        var guildCommandTestJoinedGuild = new SlashCommandBuilder()
            .WithName("test-guild-joined")
            .WithDescription("Test the JoinedGuildHandler function")
            .WithDMPermission(false);
        guildApplicationCommandProperties.Add(guildCommandTestJoinedGuild.Build());

        var guildCommandTestLeftGuild = new SlashCommandBuilder()
            .WithName("test-guild-left")
            .WithDescription("Test the LeftGuildHandler function")
            .WithDMPermission(false);
        guildApplicationCommandProperties.Add(guildCommandTestLeftGuild.Build());

        guildApplicationCommandProperties.AddRange(globalApplicationCommandProperties);

        try
        {
            await guild.BulkOverwriteApplicationCommandAsync(guildApplicationCommandProperties.ToArray());
            await _botclient.BulkOverwriteGlobalApplicationCommandsAsync(globalApplicationCommandProperties.ToArray());
        }
        catch (Exception ex)
        {
            var json = JsonConvert.SerializeObject(ex);
            Console.WriteLine(json);
        }

        clientIsReady = true;
    }

    public async Task UserJoined(SocketGuildUser user)
    {
        await SendLogChannelMessageAsync(user.Guild.Id, $"User {user.Mention} joined the server");

        if (await _botDatabase.GetGuildByGuildIdAsync(user.Guild.Id) is Guild guild && guild.Channels?.WelcomeChannel is ulong welcomeChannelId)
        {
            if (HasPermissionsToWriteChannel((SocketGuildChannel)_botclient.GetChannel(welcomeChannelId)))
            {
                if(guild.SendWelcomeMessage is true)
                await ((SocketTextChannel)_botclient.GetChannel(welcomeChannelId)).SendMessageAsync($"Welcome, {user.Mention}!");
                if(guild.AskNicknameUponWelcome is true)
                await SendNicknamePoll((SocketTextChannel)_botclient.GetChannel(welcomeChannelId));
            }
            }
        else
        {
            await SendLogChannelMessageAsync(user.Guild.Id, "Can't send welcome message: no welcome channel set!");
            SendLog(LogEventLevel.Warning, user.Guild.Id, "No welcome channel set");
        }

        SendLog(LogEventLevel.Information, user.Guild.Id, "User {UserId} joined the guild", user.Id);
    }
    public async Task UserLeftHandler(SocketGuild guild, SocketUser user)
    {
        if(await _botDatabase.GetGuildByGuildIdAsync(guild.Id) is Guild originalGuild && originalGuild.Users.Where(x => x.SocketUserId == user.Id).FirstOrDefault(defaultValue: null) is User savedUser)
        {
            originalGuild.Users.Remove(savedUser);
            await _botDatabase.SaveChangesAsync();
        }

        SendLog(LogEventLevel.Information, guild.Id, "User {UserId} left the guild", user.Id);
    }
    public async Task JoinedGuildHandler(SocketGuild guild)
    {
        await AddGuild(guild.Id);

        SendLog(LogEventLevel.Information, guild.Id, "Bot was added to the guild");
    }
    private async Task AddGuild(ulong guildId)
    {
        if (_botDatabase.Guilds.Find(guildId) is null)
        {
            await _botDatabase.Guilds.AddAsync(new Guild { GuildId = guildId, Channels = new Channels(), Roles = new Roles() });
            await _botDatabase.SaveChangesAsync();
        }
    }

    public async Task LeftGuildHandler(SocketGuild guild)
    {
        await RemoveGuild(guild.Id);

        SendLog(LogEventLevel.Information, guild.Id, "Bot left the guild");
    }
    private async Task RemoveGuild(ulong guildId)
        {
        if (_botDatabase.Guilds.Find(guildId) is Guild guildToLeave)
        {
                _botDatabase.Guilds.Remove(guildToLeave);
            await _botDatabase.SaveChangesAsync();
        }
    }

    private async Task ButtonExecutedHandler(SocketMessageComponent component)
    {
        switch (component.Data.CustomId)
        {
            case "start-nickname-process":
                SendLog(LogEventLevel.Debug, component.GuildId!.Value, "User {UserId} started the nickname process", component.User.Id);
                var modal = new ModalBuilder()
                    .WithTitle("Planetside username")
                    .WithCustomId("nickname-modal")
                    .AddTextInput("Please enter your planetside username:", "ingame-nickname", TextInputStyle.Short, "name", 2, 32, true);

                await component.RespondWithModalAsync(modal.Build());
                break;
        }
    }

    private async Task ModalSubmittedHandler(SocketModal result)
    {
        switch (result.Data.CustomId)
        {
            case "nickname-modal":
                await HandleNicknameModal(result);
                break;
        }
    }

    private async Task MenuHandler(SocketMessageComponent arg)
    {
        switch (arg.Data.CustomId)
        {
            case "rank-selector":
                await arg.UpdateAsync(x =>
                {
                    x.Content = "Searching...";
                    x.Components = new ComponentBuilder().Build();
                });
                break;
        }
    }

    private async Task SlashCommandHandler(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "ping":
                await Console.Out.WriteLineAsync("The bot was invited to ping-pong!");
                await command.RespondAsync("pong");
                break;
            case "first-with-rank":
                await HandleFirstWithRankCommand(command);
                break;
            case "find-promotable-member-at-rank":
                await command.RespondAsync("Command not implemented");
                //await HandleFindPromotableMemberAtRankCommand(command);
                break;
            case "help":
                await HandleHelp(command);
                break;
            case "send-nickname-poll":
                await HandleSendNicknamePoll(command);
                break;
            case "send-welcome-message":
                await HandleSendWelcomeMessage(command);
                break;
            case "include-nickname-poll":
                await HandleIncludeNicknamePoll(command);
                break;
            case "set-log-channel":
                await HandleSetLogChannel(command);
                break;
            case "set-welcome-channel":
                await HandleSetWelcomeChannel(command);
                break;
            case "set-member-role":
                await HandleSetMemberRole(command);
                break;
            case "set-non-member-role":
                await HandleSetNonMemberRole(command);
                break;
            case "set-main-outfit":
                await HandleSetMainOutfit(command);
                break;

            //  Test commands
            case "test-user-join":
                SocketGuildUser user = (SocketGuildUser)command.User;
                await command.DeferAsync();
                await UserJoined(user);
                await command.FollowupAsync("Executed.", ephemeral: true);
                break;
            case "test-guild-joined":
                await command.DeferAsync();
                if (command.GuildId is not null)
                    await JoinedGuildHandler(_botclient.GetGuild((ulong)command.GuildId));
                await command.FollowupAsync("Executed.", ephemeral: true);
                break;
            case "test-guild-left":
                await command.DeferAsync();
                if (command.GuildId is not null)
                    await LeftGuildHandler(_botclient.GetGuild((ulong)command.GuildId));
                await command.FollowupAsync("Executed.", ephemeral: true);
                break;
        }
    }

    private async Task AutocompleteExecutedHandler(SocketAutocompleteInteraction interaction)
    {
        switch (interaction.Data.CommandName)
        {
            case "help":
                if(interaction.Data.Options.First().Name == "command")
                    if (interaction.Data.Current.Name == "name")
                    await HandleHelpCommandOptionAutocomplete(interaction);
                break;
        }
    }

    private async Task HandleHelpCommandOptionAutocomplete(SocketAutocompleteInteraction interaction)
    {
        List<ApplicationCommandProperties> propertiesList = CommandsAvailableToUser(interaction);
        List<AutocompleteResult> results = new();

        foreach (ApplicationCommandProperties properties in propertiesList)
        {
            string name = properties.Name.IsSpecified ? (string)properties.Name : "no name found for command";
            if (interaction.Data.Current.Value is string value && name.Contains(value))
                results.Add(new AutocompleteResult { Name = name, Value = name });
        }

        await interaction.RespondAsync(results.Take(25), options: null);
    }

    private async Task HandleNicknameModal(SocketModal socketModal)
    {
        await socketModal.RespondAsync("Validating character name...");

        string nickname = socketModal.Data.Components.First().Value;

        //  A nickname modal can only be sent in a guild, so GuildId will not be null.
        var findGuild = _botDatabase.GetGuildByGuildIdAsync((ulong)socketModal.GuildId!);
        var jsonTask = _censusclient.GetStringAsync($"http://census.daybreakgames.com/s:{appSettings.GetConnectionString("CensusAPIKey")}/get/ps2:v2/character_name/?name.first_lower=*{nickname.ToLower()}&c:join=outfit_member_extended^on:character_id^inject_at:outfit^show:alias&c:limit=6&c:exactMatchFirst=true");

        //  Validate given nickname
        nickname = nickname.Trim();
        if (Regex.IsMatch(nickname, @"[\s]"))
        {
            SendLog(LogEventLevel.Information, socketModal.GuildId.Value, "User {UserId} submitted an invalid username: {nickname}", socketModal.User.Id, nickname);
            await socketModal.ModifyOriginalResponseAsync(x => x.Content = $"Invalid nickname submitted: {nickname}. Whitespace are not allowed. Please try again.");
            return;
        }
        SendLog(LogEventLevel.Information, socketModal.GuildId.Value, "User {UserId} submitted nickname: {nickname}", socketModal.User.Id, nickname);

        //  Request players with this name from Census, including a few other, similar names
        string outfitDataJson = await jsonTask;
        if (JsonSerializer.Deserialize<rReturnedPlayerDataLight>(outfitDataJson, defaultCensusJsonDeserializeOptions) is rReturnedPlayerDataLight playerData && playerData.returned.HasValue)
        {
            //  If 0 is returned, no similar names were found. If more than 1 are returned and the first result is incorrect, no exact match was found
            if (playerData?.returned == 0 || playerData?.returned > 1 && playerData.character_name_list[0].name.first_lower != nickname.ToLower())
            {
                SendLog(LogEventLevel.Information, socketModal.GuildId.Value, "Unable to find a match for name {nickname} in the Census database. Dumping returned JSON string as a debug log message.", nickname);
                SendLog(LogEventLevel.Debug, socketModal.GuildId.Value, "Unable to find match for {nickname} using Census API. Returned JSON:\n{json}", nickname, outfitDataJson);
                await socketModal.ModifyOriginalResponseAsync(x => x.Content = $"No exact match found for {nickname}. Please try again.");
                return;
            }

            //  Check whether a database entry exists for this guild, before trying to access it
            Guild? guild = await findGuild;
            if (guild is null)
            {
                SendLog(LogEventLevel.Error, socketModal.GuildId.Value, "No guild data found in database for this guild");
                await socketModal.ModifyOriginalResponseAsync(x => x.Content = "Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
                return;
            }

            //  Set the Discord nickname of the user, including outfit tag and either the member or non-member role, as defined by the guild admin
            string? alias = playerData?.character_name_list[0].outfit.alias;
            if (socketModal.User is IGuildUser guildUser)
            {

                //  Check whether a user with nickname already exists on the server, to prevent impersonation
                if (guild.Users.Where(x => x.CharacterName == nickname && x.SocketUserId != socketModal.User.Id).FirstOrDefault(defaultValue: null) is User impersonatedUser)
                {
                    SendLog(LogEventLevel.Warning, socketModal.GuildId.Value, "Possible impersonation: user {UserId} tried to set nickname to {nickname}, but that character already exists in this guild!", socketModal.User.Id, nickname);
                    await socketModal.FollowupAsync($"User {socketModal.User.Mention} tried to set his nickname to {nickname}, but user <@{impersonatedUser.SocketUserId}> already exists on the server! Incident reported...");
                    await SendLogChannelMessageAsync((ulong)socketModal.GuildId, $"User {socketModal.User.Mention} tried to set nickname to \"{nickname}\", but that user already exists on this server (<@{impersonatedUser.SocketUserId}>)");
                    return;
                }

                try
                {
                    //  Assign Discord nickname and member/non-member role
                    await guildUser.ModifyAsync(x => x.Nickname = $"[{alias}] {nickname}");
                    if (guild.OutfitTag is not null && alias?.ToLower() == guild.OutfitTag.ToLower() && guild.Roles?.MemberRole is ulong memberRoleId)
                    {
                        await guildUser.AddRoleAsync(memberRoleId);
                        SendLog(LogEventLevel.Information, socketModal.GuildId.Value, "Added role {MemberRoleId} to user {UserId}", memberRoleId, socketModal.User.Id);
                    }
                    else if (guild.OutfitTag is not null && alias?.ToLower() != guild.OutfitTag.ToLower() && guild.Roles?.NonMemberRole is ulong nonMemberRoleId)
                    {
                        await guildUser.AddRoleAsync(nonMemberRoleId);
                        SendLog(LogEventLevel.Information, socketModal.GuildId.Value, "Added role {NonMemberId} to user {UserId}", nonMemberRoleId, socketModal.User.Id);
                    }

                    await socketModal.ModifyOriginalResponseAsync(x => { x.Content = $"Nickname set to {guildUser.Mention}"; x.AllowedMentions = AllowedMentions.None; });
                    await socketModal.FollowupAsync($"We've now set your Discord nickname to your in-game name, to avoid potential confusion during tense moments.\nWith that you're all set, thanks for joining and have fun!", ephemeral: true);
                }
                catch (Exception ex)
                {
                    SendLog(LogEventLevel.Warning, socketModal.GuildId.Value, "Unable to assign nickname to user {UserId}. Encountered exception:", socketModal.User.Id, exep: ex);
                    await socketModal.ModifyOriginalResponseAsync(x => x.Content = $"Something went wrong when trying to set your nickname to \"[{alias}] {nickname}\"...\nPlease contact an admin to have them set the nickname!");
                }

                    //  Check whether user already exists in the database for this guild
                    if (guild.Users.Where(x => x.SocketUserId == socketModal.User.Id).FirstOrDefault(defaultValue: null) is User user)
                    {
                        user.CharacterName = nickname;
                        user.CurrentOutfit = alias;
                    }
                    else
                        guild.Users.Add(new User { CharacterName = nickname, CurrentOutfit = alias, SocketUserId = socketModal.User.Id });
                    await _botDatabase.SaveChangesAsync();
                }
            else
            {
                SendLog(LogEventLevel.Warning, socketModal.GuildId.Value, "Could not convert user {UserId} from SocketUser to IGuildUser", socketModal.User.Id);
                await socketModal.ModifyOriginalResponseAsync(x => x.Content = $"Something went wrong when trying to set your nickname to \"[{alias}] {nickname}\"...\nPlease contact an admin to have them set the nickname!");
            }

        }
        else
        {
            SendLog(LogEventLevel.Warning, socketModal.GuildId.Value, "Census failed to return a list of names. Dumping returned JSON as a debug log message");
            SendLog(LogEventLevel.Debug, socketModal.GuildId.Value, "Returned Census JSON: {json}", outfitDataJson);
            await socketModal.ModifyOriginalResponseAsync(x => x.Content = $"Whoops, something went wrong... Unable to find that user due to an error in the Census database.");
        }
    }
    private async Task HandleFirstWithRankCommand(SocketSlashCommand command)
    {
        SendLog(LogEventLevel.Information, command.GuildId!.Value, "User {UserId} executed first-with-rank. The member holding rank {RankOrdinal} the longest was requested", command.User.Id, command.Data.Options.First().Value);
        await command.DeferAsync();

        rPlayerData? longestMember = null;
        int requestedRank = (int)(Int64)command.Data.Options.First().Value;

        if (apiResponse != null)
        {
            ulong memberSince = ulong.MaxValue;
            foreach (rPlayerData player in apiResponse.outfit_member_extended_list)
            {
                if (player.member_rank_ordinal == requestedRank && player.member_since < memberSince)
                {
                    longestMember = player;
                    memberSince = player.member_since == null ? memberSince : (ulong)player.member_since;
                }
            }

            if (longestMember == null)
            {
                await command.FollowupAsync($"No member found at rank {requestedRank}");
                return;
            }
        }

        await command.FollowupAsync($"Member {longestMember?.character_id_join_character_name?.name?.first} has been at rank {requestedRank} since {longestMember?.member_since_date}");
    }

    //  NEEDS REWRITE
    private async Task HandleFindPromotableMemberAtRankCommand(SocketSlashCommand command)
    {
        await BotLogHandler(new LogMessage(LogSeverity.Info, nameof(HandleFindPromotableMemberAtRankCommand), $"find-promotable-member-at-rank called by user {command.User.Id} with params: {command.Data.Options.ToArray()[0].Value}{(command.Data.Options.Count > 2 ? ", " + command.Data.Options.ToArray()[2].Value : "")}"));
        await command.DeferAsync();

        var outfitDataJson = await _censusclient.GetStringAsync($"http://census.daybreakgames.com/s:{appSettings.GetConnectionString("CensusAPIKey")}/get/ps2:v2/outfit/?c:show=outfit_id,alias_lower,alias,member_count&alias_lower=txlc&c:join=outfit_rank^inject_at:ranks^show:ordinal%27name^list:1^terms:ordinal");
        await BotLogHandler(new LogMessage(LogSeverity.Info, nameof(HandleFindPromotableMemberAtRankCommand), $"Census returned: {outfitDataJson}"));
        if (JsonSerializer.Deserialize<rReturnedOutfitData>(outfitDataJson, defaultCensusJsonDeserializeOptions) is rReturnedOutfitData returnedData)
        {
            rOutfitData outfitData = returnedData.outfit_list[0];
            if (returnedData.returned == 0 || outfitData.ranks is null)
            {
                await BotLogHandler(new LogMessage(LogSeverity.Warning, nameof(HandleFindPromotableMemberAtRankCommand), $"No data found by Census for outfit with tag \"{command.Data.Options.First().Value}\""));
                await command.FollowupAsync($"No outfit data found for tag \"{command.Data.Options.First().Value}\". Are you sure the outfit tag is correct?");
                return;
            }
            List<rOutfitData.rOutfitRanks> ranks = outfitData.ranks.ToList();
            ranks.Sort((x, y) => y.ordinal.CompareTo(x.ordinal));
            await Console.Out.WriteLineAsync(string.Join(", ", ranks));

            var menuBuilder = new SelectMenuBuilder()
                .WithPlaceholder("rank")
                .WithMinValues(1)
                .WithMaxValues(1)
                .WithCustomId("rank-selector");

            foreach (var rank in ranks)
            {
                menuBuilder.AddOption(rank.name, rank.ordinal.ToString());
                await Console.Out.WriteLineAsync($"Added {rank.name} as an option");
            }

            var builder = new ComponentBuilder()
                .WithSelectMenu(menuBuilder);

            await command.FollowupAsync("Please select the rank of interest:", components: builder.Build());
        }
    }

    private async Task HandleHelp(SocketSlashCommand command)
    {
        bool noOptionsSpecified = command.Data.Options.Count == 0;
        int commandsPerPage = 4;
        int startingPage = 0;
        //  x.DefaultMemberPermissions are AND'ed together. When the result of an AND between x.Permissions and p is more than one, we know the user has at least one of the permission required for the command
        List<ApplicationCommandProperties> availableCommands = CommandsAvailableToUser(command);
        int totalPages = (int)Math.Ceiling((double)availableCommands.Count / commandsPerPage);

        string subcommand = command.Data.Options.First().Name;
        switch (subcommand)
        {
            case "command":
                {
                    string requestedCommand = (string)command.Data.Options.First().Options.First().Value;
                    if (requestedCommand.StartsWith("/"))
                        requestedCommand = requestedCommand.TrimStart('/');

                    if ((SlashCommandProperties)availableCommands.Where(x => x.Name.Value.ToLower() == requestedCommand.ToLower()).FirstOrDefault(defaultValue: null)! is SlashCommandProperties slashCommand)
                    {
                        var embed = CommandHelpEmbed(slashCommand);
                        await command.RespondAsync(embed: embed.Build());
                    }
                    else if ((SlashCommandProperties)globalApplicationCommandProperties.Where(x => x.Name.Value.ToLower() == requestedCommand.ToLower()).FirstOrDefault(defaultValue: null)! is not null)
                    {
                        await command.RespondAsync($"You don't have the right permissions to execute command `/{requestedCommand}`");
                    }
                    else
                    {
                        await command.RespondAsync($"Command `/{requestedCommand}` doesn't exist");
                    }
                }
                break;

            case "setup":
                {
                    var embeds = new List<Embed>(){
                        new EmbedBuilder()
                        .WithTitle("Configure Channels")
                        .WithDescription("First of all, make sure all channels are configured properly. Use `/set-welcome-channel` to set a welcome channel, and use `/set-log-channel` to set a log channel. " +
                                            "The welcome channel will be used to send messages whenever a new user joins (such as a welcome message, or a nickname poll), while the log channel will be used to send " +
                                            "messages that are of interest to the admins. Because of this, it's recommended to set the welcome channel to a public channel, and the log channel to a private (admin only) " +
                                            "channel.\n" +
                                            "Also, please make sure the bot has the right permissions to post in these channels - namely the \"View Channel\" and \"Send Messages\" permissions")
                        .WithColor(247, 82, 37)
                        .Build(),
                    new EmbedBuilder()
                        .WithTitle("Set Main Outfit")
                        .WithDescription("Next, please inform the bot of the main outfit represented by this server by using `/set-main-outfit`. If left unset, all users that join will be given the non-member role " +
                                            "(see the next step). The provided tag will be checked against the Planetside API, to ensure the outfit actually exists.")
                        .WithColor(247, 82, 37)
                        .Build(),
                    new EmbedBuilder()
                        .WithTitle("Nickname Poll")
                        .WithDescription("Now we'll set up the behaviour of the nickname poll. This poll can ask the user for their in-game character name and, if the character exists, will set their Discord nickname to " +
                                            "their outfit tag + their character name (for example, \"[OUTF] xXCharacterNameXx\"). Using `/include-nickname-poll`, you can choose whether this poll should be sent whenever a new " +
                                            "user joins, while `/send-nickname-poll` will send a nickname poll to whichever channel the bot has access to (the rules channel, for instance).")
                        .WithColor(247, 82, 37)
                        .Build(),
                    new EmbedBuilder()
                        .WithTitle("Configure Roles")
                        .WithDescription("Now we're ready to configure the roles that will be handed out by the bot. These can be set by `/set-member-role` and `/set-non-member-role`. The member role will be given whenever " +
                                            "the in-game character of the user is a member of the outfit specified by `/set-main-outfit`, while the non-member role will be given in any other case.\n" +
                                            "Please keep in mind that this bot has no way of actually verifying whether the user actually owns the character provided: for this reason, it's recommended not to hand out any " +
                                            "roles that have meaningfull permissions associated with them.\n" +
                                            "Also, make sure the role of the bot outranks any of the roles set by `/set-member-role` and `/set-non-member-role`. The bot will not be able to hand out these roles otherwise.")
                        .WithColor(247, 82, 37)
                        .Build(),
                    new EmbedBuilder()
                        .WithTitle("Optionally")
                        .WithDescription("Finally, you can choose whether the bot should send a general welcome message whenever a user joins. This can be done using `/send-welcome-message`. Welcome messages will be sent to " +
                                            "the channel specified with `/set-welcome-channel`.")
                        .WithColor(247, 82, 37)
                        .Build()};
                    await command.RespondAsync(embeds: embeds.ToArray());
                }
                break;

            case "page":
                goto default;

            default:
        {
            Int64 requestedPage = 1;
            if (!noOptionsSpecified)
                        requestedPage = (Int64)command.Data.Options.First().Options.First().Value;

            if (requestedPage > totalPages)
                requestedPage = totalPages;

            startingPage = (int)requestedPage - 1;

        List<Embed> embeds = new();

            for (int i = startingPage * commandsPerPage; i < availableCommands.Count; i++)
        {
            SlashCommandProperties slashCommand = (SlashCommandProperties)availableCommands[i];
                var embed = CommandHelpEmbed(slashCommand);

                if (i == (startingPage + 1) * commandsPerPage - 1 || i == availableCommands.Count - 1)
                {
                    embed.WithFooter($"page {startingPage + 1}/{totalPages}");
                    embeds.Add(embed.Build());
                    break;
                }
                embeds.Add(embed.Build());
            }
            await command.RespondAsync("Available commands:", embeds: embeds.ToArray());
            }
                break;
        }
    }
    private static EmbedBuilder CommandHelpEmbed(SlashCommandProperties slashCommand)
    {
            string description = (string)slashCommand.Description;
            var embed = new EmbedBuilder()
                .WithTitle("/" + slashCommand.Name)
                .WithColor(247, 82, 37);

            if (slashCommand.Options.IsSpecified)
            {
                List<ApplicationCommandOptionProperties> options = (List<ApplicationCommandOptionProperties>)slashCommand.Options;
                if (options.Count > 0)
                {
                    description += "\n\nOptions:\n";
                    foreach (var option in options)
                    {
                        //  Assume optional if IsRequired is null
                        bool required = false;
                        if (option.IsRequired is not null)
                            required = (bool)option.IsRequired;
                        description += $"`{option.Name}`: {option.Description} {(required ? "(required)" : "(optional)")}\n";
                    }
                }
            }

            embed.Description = description;
        return embed;
        }

    private async Task HandleSendNicknamePoll(SocketSlashCommand command)
    {
        await command.DeferAsync();

        bool respondEphemerally = true;
        SocketTextChannel targetChannel;
        if (!command.Data.Options.IsNullOrEmpty() && command.Data.Options.First().Value is SocketTextChannel channel)
        {
            targetChannel = channel;
            respondEphemerally = channel == command.Channel;
        }
        else
            targetChannel = (SocketTextChannel)command.Channel;

        await command.FollowupAsync($"Attempting to send poll to <#{targetChannel.Id}>", ephemeral: respondEphemerally);
        await SendNicknamePoll(targetChannel, command);
    }

    private async Task HandleSendWelcomeMessage(SocketSlashCommand command)
    {
        if(command.Data.Options.First().Value is bool sendWelcomeMessage && _botDatabase.Guilds.Find(command.GuildId) is Guild guild)
        {
            guild.SendWelcomeMessage = sendWelcomeMessage;
            await _botDatabase.SaveChangesAsync();
            SendLog(LogEventLevel.Information, command.GuildId!.Value, "Welcome messages will {Confirmation} be sent", sendWelcomeMessage ? "now" : "not");
            await command.RespondAsync($"Welcome messages will {(sendWelcomeMessage ? "now" : "not")} be sent");
        }
    }

    private async Task HandleIncludeNicknamePoll(SocketSlashCommand command)
    {
        await command.DeferAsync();
        if (_botDatabase.Guilds.Find(command.GuildId) is Guild guild && command.Data.Options.First().Value is bool include)
        {
            guild.AskNicknameUponWelcome = include;
            await command.FollowupAsync($"Welcome messages will {(include ? "" : "not")} include a nickname poll");
            _botDatabase.SaveChanges();
        }
        else
        {
            SendLog(LogEventLevel.Error, command.GuildId!.Value, "No database entry found for this guild");
            await command.FollowupAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
        }

    }

    private async Task HandleSetLogChannel(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if (command.GuildId is null || (await _botDatabase.GetGuildByGuildIdAsync((ulong)command.GuildId))?.Channels is not Channels channels)
        {
            SendLog(LogEventLevel.Error, command.GuildId!.Value, "No channels found in database for this guild");
            await command.RespondAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        //  Specifying a channel is required for this command, and thus channel cannot be null
        SocketGuildChannel channel = (SocketGuildChannel)command.Data.Options.First().Value;
        channels.LogChannel = channel.Id;
        _botDatabase.SaveChanges();

        SendLog(LogEventLevel.Information, command.GuildId.Value, "Log channel set to {LogChannelId}", channel.Id);
        await command.FollowupAsync($"Log channel set to <#{channel.Id}>");

        //  Notifies user if bot can't write to channel
        HasPermissionsToWriteChannel(channel);
        }
    private async Task HandleSetWelcomeChannel(SocketSlashCommand command)
    {
        if(command.GuildId is null || (await _botDatabase.GetGuildByGuildIdAsync((ulong)command.GuildId))?.Channels is not Channels channels)
        {
            SendLog(LogEventLevel.Error, command.GuildId!.Value, "No channels found in database for this guild");
            await command.RespondAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        //  Specifying a channel is required for this command, and thus channel cannot be null
        SocketGuildChannel channel = (SocketGuildChannel)command.Data.Options.First().Value;
        channels.WelcomeChannel = channel.Id;
        _botDatabase.SaveChanges();

        SendLog(LogEventLevel.Information, command.GuildId.Value, "Welcome channel set to {WelcomeChannelId}", channel.Id);
        await command.RespondAsync($"Welcome channel set to <#{channel.Id}>");

        //  Notifies user if bot can't write to channel
        HasPermissionsToWriteChannel(channel, command);
        }

    private async Task HandleSetMemberRole(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if ((await _botDatabase.GetGuildByGuildIdAsync((ulong)command.GuildId!))?.Roles is not Roles roles)
        {
            SendLog(LogEventLevel.Error, command.GuildId.Value, "No roles found in database for this guild");
            await command.FollowupAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        SocketRole role = (SocketRole)command.Data.Options.First().Value;
        roles.MemberRole = role.Id;
        _botDatabase.SaveChanges();

        SendLog(LogEventLevel.Information, command.GuildId.Value, "Member role set to {MemberRoleId}", role.Id);
        await command.FollowupAsync($"Member role set to {role?.Mention}");

        SocketRole? botRole = _botclient.GetGuild((ulong)command.GuildId!).GetUser(_botclient.CurrentUser.Id).Roles.FirstOrDefault(x => x.IsManaged);
        if (role?.Position > botRole?.Position)
        {
            SendLog(LogEventLevel.Warning, command.GuildId.Value, "Bot doesn't have the right permissions to give role {MemberRoleId} to users", role.Id);
            await command.FollowupAsync($"The bot won't be able to give role {role.Mention} to users, because it outranks the bot's role. Please go to `Server Settings -> Roles` and make sure that the {botRole.Mention} role is higher on the list than the {role.Mention} role.", allowedMentions: AllowedMentions.None);
    }
    }
    private async Task HandleSetNonMemberRole(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if ((await _botDatabase.GetGuildByGuildIdAsync((ulong)command.GuildId!))?.Roles is not Roles guildParameters)
        {
            SendLog(LogEventLevel.Error, command.GuildId.Value, "No roles found in database for this guild");
            await command.FollowupAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        SocketRole role = (SocketRole)command.Data.Options.First().Value;
        guildParameters.NonMemberRole = role.Id;
        _botDatabase.SaveChanges();

        SendLog(LogEventLevel.Information, command.GuildId.Value, "Non-member role set to {NonMemberRoleId}", role.Id);
        await command.FollowupAsync($"Non-member role set to {role?.Mention}");

        SocketRole? botRole = _botclient.GetGuild((ulong)command.GuildId!).GetUser(_botclient.CurrentUser.Id).Roles.FirstOrDefault(x => x.IsManaged);
        if (role?.Position > botRole?.Position)
        {
            SendLog(LogEventLevel.Warning, command.GuildId.Value, "Bot doesn't have the right permissions to give role {NonMemberRoleId} to users", role.Id);
            await command.FollowupAsync($"The bot won't be able to give role {role.Mention} to users, because it outranks the bot's role. Please go to `Server Settings -> Roles` and make sure that the {botRole.Mention} role is higher on the list than the {role.Mention} role.", allowedMentions: AllowedMentions.None);
        }
    }
    private async Task HandleSetMainOutfit(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if (_botDatabase.Guilds.Find(command.GuildId) is not Guild guild)
        {
            SendLog(LogEventLevel.Error, command.GuildId!.Value, "No guild data found in database for this guild");
            await command.FollowupAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        string tag = (string)command.Data.Options.First().Value;

        var outfitCountJson = await _censusclient.GetStringAsync($"http://census.daybreakgames.com/s:{appSettings.GetConnectionString("CensusAPIKey")}/count/ps2/outfit/?alias_lower={tag.ToLower()}");
        int? count = JObject.Parse(outfitCountJson)["count"]?.ToObject<int>();      //  Only the number of results is returned by the query. If the result is 1 it is assumed the given outfit exists, though it might be different from what the user requested
        if(count == 0)
        {
            SendLog(LogEventLevel.Information, command.GuildId!.Value, "No outfit found with tag {OutfitTag}", tag);
            await command.FollowupAsync($"No outfit found with tag {tag}!");
            return;
        }
        else if (!count.HasValue || count != 1)
        {
            SendLog(LogEventLevel.Warning, command.GuildId!.Value, "Something went wrong requesting outfit tag {OutfitTag} from Census. Dumping JSON as a debug log message", tag);
            SendLog(LogEventLevel.Debug, command.GuildId.Value, "Census returned after requesting outfit tag {OutfitTag}:\n{json}", tag, outfitCountJson);
            await command.FollowupAsync($"Something went wrong while validating outfit tag {tag}...");
            return;
        }

        await command.FollowupAsync($"Main outfit set to {command.Data.Options.First().Value}");
        SendLog(LogEventLevel.Information, command.GuildId!.Value, "Main outfit set to {OutfitTag}", tag);

        guild.OutfitTag = tag;
        _botDatabase.SaveChanges();
    }

    private bool HasOutfitTagConfigured(ulong? guildId)
    {
        if (guildId is null || _botDatabase.Guilds.Find(guildId)?.OutfitTag is null)
            return false;
        return true;
    }

    //  Consider using SendMessageIfPermittedAsync for performance reasons
    private bool HasPermissionsToWriteChannel(SocketGuildChannel channel, SocketCommandBase? command = null, [CallerMemberName] string caller = "")
    {
        SocketGuildUser botUser = _botclient.GetGuild(channel.Guild.Id).GetUser(_botclient.CurrentUser.Id);
        SocketRole everyoneRole = channel.Guild.EveryoneRole;
        List<SocketRole> botRoles = botUser.Roles.ToList();
        List<Overwrite> overwrites = channel.PermissionOverwrites.ToList();
        OverwritePermissions everyoneOverwritePermissions = channel.GetPermissionOverwrite(everyoneRole)!.Value;    //  Every channel has permission overwrites for the everyone role, so it can't be null
        bool hasViewChannel = false;
        bool hasSendMessages = false;
        
        //  Check View Channel permissions
        if (everyoneOverwritePermissions.ViewChannel == PermValue.Allow || (everyoneOverwritePermissions.ViewChannel == PermValue.Inherit && everyoneRole.Permissions.ViewChannel == true))
            hasViewChannel = true;
        else if (overwrites.Any(x => (botRoles.Any(y => x.TargetId == y.Id) || x.TargetId == botUser.Id) && x.Permissions.ViewChannel == PermValue.Allow))
            hasViewChannel = true;
        //  The View Channel permission can't be inherited: if a channel is set to private, the bot has to have a role that has the permission allowed for that channel

        //  Check Send Messages permissions
        //  If any (non-@everyone) roles are allowed, it doesn't matter if there are any that are denied (allow overwrites deny)
        if (overwrites.Any(x => (botRoles.Any(y => x.TargetId == y.Id) || x.TargetId == botUser.Id) && x.TargetId != everyoneRole.Id && x.Permissions.SendMessages == PermValue.Allow))
            hasSendMessages = true;
        //  If there are no roles which have specifically been denied, we can check for either @everyone permissions or inherited permissions
        else if (!overwrites.Any(x => (botRoles.Any(y => x.TargetId == y.Id) || x.TargetId == botUser.Id) && x.Permissions.SendMessages == PermValue.Deny))
            //  @everyone permissions can't overwrite a deny
            if (everyoneOverwritePermissions.SendMessages == PermValue.Allow || (everyoneOverwritePermissions.SendMessages == PermValue.Inherit && everyoneRole.Permissions.SendMessages == true))
                hasSendMessages = true;
            else if (overwrites.Any(x => x.Permissions.SendMessages == PermValue.Inherit &&
                        (botUser.Id == x.TargetId ?     //  Check if overwrite is for a user
                            botUser.GuildPermissions.Has(GuildPermission.ViewChannel) :
                            _botclient.GetGuild(channel.Guild.Id).Roles.Where(y => y.Id == x.TargetId).First().Permissions.SendMessages == true)))
                hasSendMessages = true;

        if (hasViewChannel && hasSendMessages)
            return true;
        else
        {
            SendLog(LogEventLevel.Warning, channel.Guild.Id, "Bot doesn't have the right permissions to post in channel {ChannelId}", channel.Id);

            if (command is not null)
            {
                string warningMessage = $"Warning: the bot doesn't have the right permissions to post in <#{channel.Id}>. Missing permissions: {(hasViewChannel ? "" : "\"View Channel\"")}{(hasViewChannel && hasSendMessages ? ", " : "")}{(hasSendMessages ? "" : "\"Send Messages\"")}";
                if (command.HasResponded)
                    command.FollowupAsync(warningMessage);
                else
                    command.RespondAsync(warningMessage);
            }
            //  Required to prevent a recursive loop, since SendLogChannelMessageAsync calls HasPermissionsToWriteChannel
            else if (caller != nameof(SendLogChannelMessageAsync))
                Task.Run(async () => await SendLogChannelMessageAsync(channel.Guild.Id, "Couldn't set welcome message: missing permissions!"));

        return false;
    }
    }
        
    private async Task SendMessageIfPermittedAsync(SocketTextChannel channel, string message, SocketSlashCommand? respondTo = null, MessageComponent? messageComponent = null, [CallerMemberName] string caller = "")
    {
        try
            {
            await channel.SendMessageAsync(message, components: messageComponent);
            }
        catch(Exception e)
            {
            //  If the bot has the permissions to post to a channel yet can't, something went seriously wrong
            //  Specifying caller of HasPermissionsToWriteChannel manually is required to prevent a recursive loop from forming
            if (HasPermissionsToWriteChannel(channel, respondTo, caller: caller == nameof(SendLogChannelMessageAsync) ? nameof(SendLogChannelMessageAsync) : nameof(SendMessageIfPermittedAsync)))
                SendLog(LogEventLevel.Error, channel.Guild.Id, "Fatal error occurred sending message in channel {ChannelId}", channel.Id, exep: e);
            }
    }

    private async Task SendNicknamePoll(SocketTextChannel channel, SocketSlashCommand? command = null)
    {
        var confirmationButton = new ComponentBuilder()
                .WithButton("Get Started", "start-nickname-process");

        //await channel.SendMessageAsync($"To get started, press this button so we can set you up properly:", components: confirmationButton.Build());
        await SendMessageIfPermittedAsync(channel, "To get started, press this button so we can set you up properly:", command, confirmationButton.Build());
    }

    List<ApplicationCommandProperties> CommandsAvailableToUser(SocketInteraction interaction)
    {
        List<ApplicationCommandProperties> commands = interaction.GuildId == testGuildID ? guildApplicationCommandProperties : globalApplicationCommandProperties;
        if(interaction.GuildId is null)
            return new List<ApplicationCommandProperties>();
        return commands.Where(x => {
            if (x.DefaultMemberPermissions.IsSpecified)
                return _botclient.GetGuild((ulong)interaction.GuildId!).GetUser(interaction.User.Id).GuildPermissions.Has(x.DefaultMemberPermissions.Value);
            else
                return true;
        }).ToList();
    }

    private async Task SendLogChannelMessageAsync(ulong guildId, string message, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if ((await _botDatabase.GetGuildByGuildIdAsync(guildId))?.Channels?.LogChannel is ulong logChannelId && _botclient.GetGuild(guildId).GetChannel(logChannelId) is SocketTextChannel logChannel)
            await SendMessageIfPermittedAsync(logChannel, message);
        else
            SendLog(LogEventLevel.Warning, guildId, "Failed to send log message. Has the log channel been set up properly?");
    }

    private async Task BotLogHandler(LogMessage message)
    {
        var severity = message.Severity switch
        {
            LogSeverity.Critical => LogEventLevel.Fatal,
            LogSeverity.Error => LogEventLevel.Error,
            LogSeverity.Warning => LogEventLevel.Warning,
            LogSeverity.Info => LogEventLevel.Information,
            LogSeverity.Verbose => LogEventLevel.Verbose,
            LogSeverity.Debug => LogEventLevel.Debug,
            _ => LogEventLevel.Information
        };
        using(LogContext.PushProperty("Source", message.Source))
            Log.Write(severity, message.Exception, "{Message}", message.Message);
        await Task.CompletedTask;
    }

    public static void SendLog(LogEventLevel level, ulong guildId, string template, Exception? exep = null, [CallerMemberName] string caller = "")
    {
        using (LogContext.PushProperty("Source", caller))
        using (LogContext.PushProperty("GuildId", guildId))
            Log.Write(level, exep, template);
    }
    public static void SendLog<T>(LogEventLevel level, ulong guildId, string template, T prop, Exception? exep = null, [CallerMemberName] string caller = "")
    {
        using (LogContext.PushProperty("Source", caller))
        using (LogContext.PushProperty("GuildId", guildId))
            Log.Write(level, exep, template, prop);
    }
    public static void SendLog<T0, T1>(LogEventLevel level, ulong guildId, string template, T0 prop0, T1 prop1, Exception? exep = null, [CallerMemberName] string caller = "")
    {
        using (LogContext.PushProperty("Source", caller))
        using (LogContext.PushProperty("GuildId", guildId))
            Log.Write(level, exep, template, prop0, prop1);
    }
    public static void SendLog<T0, T1, T2>(LogEventLevel level, ulong guildId, string template, T0 prop0, T1 prop1, T2 prop2, Exception? exep = null, [CallerMemberName] string caller = "")
    {
        using(LogContext.PushProperty("Source", caller))
        using(LogContext.PushProperty("GuildId", guildId))
            Log.Write(level, exep, template, prop0, prop1, prop2);
    }

    private async Task<string> CLIInfo()
    {
        List<SocketGuild> guilds = _botclient.Guilds.ToList();
        int accumulativeUserCount = 0;
        foreach (SocketGuild guild in guilds)
        {
            accumulativeUserCount += guild.MemberCount;
        }

        string returnString =
             "\nPS2 Assistant bot info:\n" +
            $"| Connected guilds:       {_botclient.Guilds.Count}\n" +
            $"| Recommended shards:     {await _botclient.GetRecommendedShardCountAsync()}\n" +
            $"| Accumulative users:     {accumulativeUserCount}\n" +
            $"| Bot running for:        {DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()}";
        return returnString;
    }

    private async Task<string> CLIDatabaseInfo(bool list = false, ulong? guildId = null)
    {
        string guildIdLabel = "Guild ID",
            guildNameLabel = "Guild name",
            guildMembersLabel = "Guild Members",
            tagLabel = "Outfit Tag",
            sendWelcomeLabel = "Send welcome message",
            sendNicknameLabel = "Send nickname poll",
            welcomeChannelLabel = "Welcome channel ID",
            logChannelLabel = "Log channel ID",
            memberRoleLabel = "Member role ID",
            nonMemberRoleLabel = "Non-member role ID",
            userLabel = "User ID",
            userOutfitLabel = "Current outfit",
            characterLabel = "Character name";

        string returnString = "\nPS2 Assistant database info:\n";

        if (list)
        {
            //  Get the longest ID, to ensure all entries share the same column width
            int longestGuildIdLength = 0;
            foreach(Guild guild in _botDatabase.Guilds)
                longestGuildIdLength = guild.GuildId.ToString().Length > longestGuildIdLength ? guild.GuildId.ToString().Length : longestGuildIdLength;

            //  Header
            returnString += $"| {CLIColumn(guildIdLabel, longestGuildIdLength)} {CLIColumn(guildMembersLabel, guildMembersLabel.Length)} {CLIColumn(guildNameLabel, guildNameLabel.Length)}";
            returnString = returnString.Remove(returnString.Length - 2) + "\n";     //  Get rid of the last " |" for a cleaner look
            
            //  Body
            foreach (Guild guild in _botDatabase.Guilds)
                returnString += $"| {CLIColumn(guild.GuildId.ToString(), longestGuildIdLength)} {CLIColumn((_botclient.GetGuild(guild.GuildId).MemberCount - 1).ToString(), guildMembersLabel.Length)} {_botclient.GetGuild(guild.GuildId).Name}\n";
        }
        else if (guildId is not null && await _botDatabase.GetGuildByGuildIdAsync((ulong)guildId) is Guild guild)
        {
            //  Guild, channels and roles table headers and bodies
            returnString +=
                $"| {CLIColumn(guildIdLabel, guildId.ToString()!.Length)} {CLIColumn(tagLabel, tagLabel.Length)} {CLIColumn(sendWelcomeLabel, sendWelcomeLabel.Length)} {CLIColumn(sendNicknameLabel, sendNicknameLabel.Length)}\n" +
                $"| {guildId} | {CLIColumn(guild.OutfitTag, tagLabel.Length)} {CLIColumn(guild.SendWelcomeMessage.ToString(), sendWelcomeLabel.Length)} {CLIColumn(guild.AskNicknameUponWelcome.ToString(), sendNicknameLabel.Length)}\n" +
                 "\n" +
                $"| {CLIColumn(welcomeChannelLabel, guild.Channels?.WelcomeChannel.ToString()?.Length)} {CLIColumn(logChannelLabel, guild.Channels?.LogChannel.ToString()?.Length)}\n" +
                $"| {CLIColumn(guild.Channels?.WelcomeChannel.ToString(), guild.Channels?.WelcomeChannel.ToString()?.Length)} {CLIColumn(guild.Channels?.LogChannel.ToString(), guild.Channels?.LogChannel.ToString()?.Length)}\n" +
                 "\n" +
                $"| {CLIColumn(memberRoleLabel, guild.Roles?.MemberRole.ToString()?.Length)} {CLIColumn(nonMemberRoleLabel, guild.Roles?.NonMemberRole.ToString()?.Length)}\n" +
                $"| {CLIColumn(guild.Roles?.MemberRole.ToString(), guild.Roles?.MemberRole.ToString()?.Length)} {CLIColumn(guild.Roles?.NonMemberRole.ToString(), guild.Roles?.NonMemberRole.ToString()?.Length)}\n" +
                 "\n";

            //  Get the longest ID, to ensure all entries share the same column width
            int longestUserIdLength = 0;
            foreach (User user in guild.Users)
                longestUserIdLength = user.SocketUserId.ToString().Length > longestUserIdLength ? user.SocketUserId.ToString().Length : longestUserIdLength;

            //  Users header
            returnString += $"| {CLIColumn(userLabel, longestUserIdLength)} {CLIColumn(userOutfitLabel, userOutfitLabel.Length)} {CLIColumn(characterLabel, 32)}\n";      //  Planetside doesn't allow character names with a length of more than 32 characters
            
            //  Users body
            foreach (User user in guild.Users)
            {
                returnString += $"| {CLIColumn(user.SocketUserId.ToString(), longestUserIdLength)} {CLIColumn(user.CurrentOutfit, userOutfitLabel.Length)} {CLIColumn(user.CharacterName, 32)}";
            }
        }
        else
        {
            string dbPath = Path.GetFullPath(_botDatabase.dbLocation);
            returnString +=
                $"| Database file location: {dbPath}\n" +
                $"| Database storage size:  {decimal.Round(new FileInfo(dbPath).Length / (decimal)1024, 2):0.00} KiB\n" +
                $"| Guilds in database:     {await _botDatabase.Guilds.CountAsync()}\n" +
                $"| Registered users:       {await _botDatabase.Guilds.SelectMany(x => x.Users).CountAsync()}";
        }

        return returnString;
    }

    private string CLIColumn(string? content, int? width)
    {
        width ??= 0;
        if (!content.IsNullOrEmpty() && content!.Length > width)
                width = content.Length;
        return $"{content}{new string(' ', content.IsNullOrEmpty() ? width.Value : width.Value - content!.Length)} |";
    }
}

#pragma warning disable IDE1006 // Naming Styles
record rPromotableMemberParams(
    string outfit,
    int minActivity,
    int? maxInactivity
    );

record rResponse(
    rPlayerData[] outfit_member_extended_list,
    int returned
    );

record rReturnedOutfitData(
    rOutfitData[] outfit_list,
    int returned
    );

record rReturnedPlayerDataLight(
    rPlayerDataLight[] character_name_list,
    int? returned
    );

record rOutfitData(
    ulong? outfit_id,
    string? alias,
    string? alias_lower,
    int? member_count,
    rOutfitData.rOutfitRanks[]? ranks
    )
{
    public record rOutfitRanks(
        int ordinal,
        string name
        );
}

record rPlayerData(
    ulong? character_id,
    ulong? member_since,
    string? member_since_date,
    string? member_rank,
    int? member_rank_ordinal,
    string? alias,
    rCharacterName? character_id_join_character_name
    );

record rCharacterName(
        ulong? character_id,
        rName? name
        );

record rName(
    string? first,
    string? first_lower
    );

record rPlayerDataLight(
    ulong character_id,
    rName name,
    rOutfitData outfit
    );
#pragma warning restore IDE1006 // Naming Styles