using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

using Discord;
using Discord.WebSocket;
using Newtonsoft.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PS2_Assistant.Data;
using PS2_Assistant.Models;
using Newtonsoft.Json.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

public class Program
{

    //  Before release:
    //  TODO:   Add setup option to help command (as bool, to explain admins how to set up the bot)
    //  TODO:   Remove user form User table when leaving guild
    //  TODO:   Check whether a user with a given character name already exists on the guild in question
    //  TODO:   Clean up code in HandleNicknameModal (i.e. multiple calls to playerdata...alias, etc)

    //  After release:
    //  TODO:   Periodically check and update outfit tag of members (and update in User table
    //  TODO:   Send message upon bot joining guild
    //  TODO:   Add detection for when bot gets kicked/added while it is offline (to remove the database entry for that guild)
    //  TODO:   Remove "Get started" message after user has been set up?
    //  TODO:   Add "Welcome to outfit" message in Welcome channel when user switches outfit
    //  TODO:   Annotate entire codebase
    //  TODO:   Add service to BotContext?
    //  TODO:   Convert to use Interaction Framework?
    //  FIX:    ModalSubmitted handler is blocking the gateway task (wait for PR https://github.com/discord-net/Discord.Net/pull/2722)
    //  TODO:   Implement SendChannelMessage(guildId, channelId, message, [CallerMemberName] caller)

    public static Task Main(string[] args) => new Program().MainAsync();

    private bool stopBot = false;
    private bool clientIsReady = false;
    private DiscordSocketClient _botclient = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers });
    private HttpClient _censusclient = new();
    private ulong _guildID = 325905696652787713;
    private rResponse? apiResponse;
    private JsonSerializerOptions defaultCensusJsonDeserializeOptions = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IConfiguration appSettings = new ConfigurationBuilder().AddJsonFile("appsettings.json").SetBasePath(Environment.CurrentDirectory).Build();
    private BotContext _botDatabase = new BotContext();

    public async Task MainAsync()
    {
        //  Setup bot client
        _botclient.Log += Log;
        _botclient.Ready += Client_Ready;
        _botclient.SlashCommandExecuted += SlashCommandHandler;
        _botclient.AutocompleteExecuted += AutocompleteExecutedHandler;
        //_botclient.SelectMenuExecuted += MenuHandler;
        _botclient.UserJoined += UserJoined;
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

        //  Handle command line input
        while (!stopBot)
        {
            if (Console.ReadLine() == "stop")
                stopBot = true;
            else
                await Console.Out.WriteLineAsync("command not recognized");
        }
        await _botclient.StopAsync();
    }

    List<ApplicationCommandProperties> globalApplicationCommandProperties = new();
    public async Task Client_Ready()
    {
        SocketGuild guild = _botclient.GetGuild(_guildID);

        List<ApplicationCommandProperties> guildApplicationCommandProperties = new();

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
            .AddOption("page", ApplicationCommandOptionType.Integer, "The page number to display", minValue: 1)
            .AddOption("setup", ApplicationCommandOptionType.Boolean, "Explain how to set up the bot on a server")
            .AddOption("command", ApplicationCommandOptionType.String, "Get help for a specific command", isAutocomplete: true);
        guildApplicationCommandProperties.Add(commandHelp.Build());
        globalApplicationCommandProperties.Add(commandHelp.Build());

        var commandSendNicknamePoll = new SlashCommandBuilder()
            .WithName("send-nickname-poll")
            .WithDescription("Manually sends a poll that asks users for their in-game character name")
            .WithDMPermission(false)
            .AddOption("channel", ApplicationCommandOptionType.Channel, "The channel in which the poll will be sent");
        guildApplicationCommandProperties.Add(commandSendNicknamePoll.Build());
        globalApplicationCommandProperties.Add(commandSendNicknamePoll .Build());

        var commandIncludeNicknamePollInWelcomeMessage = new SlashCommandBuilder()
            .WithName("include-nickname-poll")
            .WithDescription("Includes a nickname poll in the welcome message when a new user joins")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("include", ApplicationCommandOptionType.Boolean, "Whether to include the poll or not", isRequired: true);
        guildApplicationCommandProperties.Add(commandIncludeNicknamePollInWelcomeMessage.Build());
        globalApplicationCommandProperties.Add(commandIncludeNicknamePollInWelcomeMessage.Build());

        var commandSetLogChannel = new SlashCommandBuilder()
            .WithName("set-log-channel")
            .WithDescription("Sets the channel where this bot's log messages will be sent")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("log-channel", ApplicationCommandOptionType.Channel, "Sets the log channel", isRequired: true);
        guildApplicationCommandProperties.Add(commandSetLogChannel.Build());
        globalApplicationCommandProperties.Add(commandSetLogChannel.Build());

        var commandSetWelcomeChannel = new SlashCommandBuilder()
            .WithName("set-welcome-channel")
            .WithDescription("Sets the channel where new users will be greeted by the bot")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("welcome-channel", ApplicationCommandOptionType.Channel, "Sets the welcome channel", isRequired: true);
        guildApplicationCommandProperties.Add(commandSetWelcomeChannel.Build());
        globalApplicationCommandProperties.Add(commandSetWelcomeChannel.Build());

        var commandSetMemberRole = new SlashCommandBuilder()
            .WithName("set-member-role")
            .WithDescription("Sets the role users will get if their character is in the outfit represented by this server")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("member-role", ApplicationCommandOptionType.Role, "Sets the member role", isRequired: true);
        guildApplicationCommandProperties.Add(commandSetMemberRole.Build());
        globalApplicationCommandProperties.Add(commandSetMemberRole.Build());

        var commandSetNonMemberRole = new SlashCommandBuilder()
            .WithName("set-non-member-role")
            .WithDescription("Sets the role users will get if their character isn't in the outfit represented by this server")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("non-member-role", ApplicationCommandOptionType.Role, "Sets the non-member role", isRequired: true);
        guildApplicationCommandProperties.Add(commandSetNonMemberRole.Build());
        globalApplicationCommandProperties.Add(commandSetNonMemberRole.Build());

        var commandSetMainOutfit = new SlashCommandBuilder()
            .WithName("set-main-outfit")
            .WithDescription("Sets the the main outfit represented by this server")
            .WithDMPermission(false)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption("outfit-tag", ApplicationCommandOptionType.String, "The tag of the outfit", isRequired: true, minLength: 1, maxLength: 4);
        guildApplicationCommandProperties.Add(commandSetMainOutfit.Build());
        globalApplicationCommandProperties.Add(commandSetMainOutfit.Build());

        var commandTest = new SlashCommandBuilder()
            .WithName("test-command")
            .WithDescription("just a test")
            .AddOption("required-parameter", ApplicationCommandOptionType.Boolean, "test required parameters", isRequired: true)
            .AddOption("optional-parameter", ApplicationCommandOptionType.Boolean, "test optional parameters", isRequired: false)
            .AddOption("unspecified-parameter", ApplicationCommandOptionType.Boolean, "test unspecified parameters");
        globalApplicationCommandProperties.Add(commandTest.Build());

        //  Test commands

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

        if ((await _botDatabase.getGuildByGuildIdAsync(user.Guild.Id))?.Channels?.WelcomeChannel is ulong welcomeChannelId)
        {
            var confirmationButton = new ComponentBuilder()
                .WithButton("Get Started", "start-nickname-process");
            if (hasPermissionsToWriteChannel((SocketGuildChannel)_botclient.GetChannel(welcomeChannelId)))
            {
                await ((SocketTextChannel)_botclient.GetChannel(welcomeChannelId)).SendMessageAsync($"Welcome, {user.Mention}!");
                if(_botDatabase.Guilds.Find(user.Guild.Id)?.askNicknameUponWelcome is true)
                await SendNicknamePoll((SocketTextChannel)_botclient.GetChannel(welcomeChannelId));
            }
            else
            {
                await Log(new LogMessage(LogSeverity.Warning, nameof(UserJoined), $"Missing permissions to send messages in welcome channel {welcomeChannelId} in guild {user.Guild.Id}"));
                await SendLogChannelMessageAsync(user.Guild.Id, "Couldn't set welcome message: missing permissions!");
            }

        }
        else
        {
            await SendLogChannelMessageAsync(user.Guild.Id, "Can't send welcome message: no welcome channel set!");
            await Log(new LogMessage(LogSeverity.Warning, nameof(UserJoined), $"No welcome channel set for guild {user.Guild.Id}"));
        }

        await Log(new LogMessage(LogSeverity.Info, nameof(UserJoined), $"User {user.Id} joined guild {user.Guild.Id}"));
    }

    public async Task JoinedGuildHandler(SocketGuild guild)
    {
        await Task.Run(() => { if (_botDatabase.Guilds.Find(guild.Id) is null) _botDatabase.Guilds.Add(new Guild { GuildId = guild.Id, Channels = new Channels(), Roles = new Roles() }); });
        _botDatabase.SaveChanges();

        await Log(new LogMessage(LogSeverity.Info, nameof(JoinedGuildHandler), $"Joined new guild: {guild.Id}"));
    }

    public async Task LeftGuildHandler(SocketGuild guild)
    {
        await Task.Run(() =>
        {
            Guild? guildToLeave = _botDatabase.Guilds.Find(guild.Id);
            if (guildToLeave != null)
                _botDatabase.Guilds.Remove(guildToLeave);
        });
        _botDatabase.SaveChanges();

        await Log(new LogMessage(LogSeverity.Info, nameof(LeftGuildHandler), $"Left guild: {guild.Id}"));
    }

    private async Task ButtonExecutedHandler(SocketMessageComponent component)
    {
        switch (component.Data.CustomId)
        {
            case "start-nickname-process":
                await Log(new LogMessage(LogSeverity.Debug, "start-nickname-process", $"User {component.User.Id} started the nickname process"));
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
                await HandleFindPromotableMemberAtRankCommand(command);
                break;
            case "help":
                await HandleHelp(command);
                break;
            case "send-nickname-poll":
                await HandleSendNicknamePoll(command);
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
                await command.RespondAsync("Executed.", ephemeral: true);
                break;
            case "test-guild-left":
                await command.DeferAsync();
                if (command.GuildId is not null)
                    await LeftGuildHandler(_botclient.GetGuild((ulong)command.GuildId));
                await command.RespondAsync("Executed.", ephemeral: true);
                break;
        }
    }

    private async Task AutocompleteExecutedHandler(SocketAutocompleteInteraction interaction)
    {
        switch (interaction.Data.CommandName)
        {
            case "help":
                if(interaction.Data.Current.Name == "command")
                {
                    await HandleHelpCommandOptionAutocomplete(interaction);
                }
                break;
        }
    }

    private async Task HandleHelpCommandOptionAutocomplete(SocketAutocompleteInteraction interaction)
    {
        List<ApplicationCommandProperties> propertiesList = availableGuildCommands(interaction);
        List<AutocompleteResult> results = new List<AutocompleteResult>();

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
        await socketModal.DeferAsync();

        string nickname = socketModal.Data.Components.First().Value;

        //  A nickname modal can ordinarily only be sent when a user joins a guild, so GuildId will not be null.
        var findGuild = _botDatabase.getGuildByGuildIdAsync((ulong)socketModal.GuildId!);
        var jsonTask = _censusclient.GetStringAsync($"http://census.daybreakgames.com/s:{appSettings.GetConnectionString("CensusAPIKey")}/get/ps2:v2/character_name/?name.first_lower=*{nickname.ToLower()}&c:join=outfit_member_extended^on:character_id^inject_at:outfit^show:alias&c:limit=6&c:exactMatchFirst=true");

        //  Validate given nickname
        nickname.Trim();
        if (Regex.IsMatch(nickname, @"[\s]"))
        {
            await Log(new LogMessage(LogSeverity.Info, nameof(HandleNicknameModal), $"User {socketModal.User.Id} submitted an invalid username: {nickname}"));
            await socketModal.FollowupAsync($"Invalid nickname submitted: {nickname}. Whitespace are not allowed. Please try again.", ephemeral: true);
            return;
        }
        await Log(new LogMessage(LogSeverity.Info, nameof(HandleNicknameModal), $"User {socketModal.User.Id} submitted nickname: {nickname}"));
        await socketModal.FollowupAsync("Validating character name...");

        //  Request players with this name from Census, including a few other, similar names
        string outfitDataJson = await jsonTask;
        if (JsonSerializer.Deserialize<rReturnedPlayerDataLight>(outfitDataJson, defaultCensusJsonDeserializeOptions) is rReturnedPlayerDataLight playerData && playerData.returned.HasValue)
        {
            //  If 0 is returned, no similar names were found. If more than 1 are returned and the first result is incorrect, no exact match was found
            if (playerData?.returned == 0 || playerData?.returned > 1 && playerData.character_name_list[0].name.first_lower != nickname.ToLower())
            {
                await Log(new LogMessage(LogSeverity.Info, nameof(HandleNicknameModal), $"Unable to find a match for name \"{nickname}\" in the Census database. Dumping returned JSON string as a debug log message."));
                await Log(new LogMessage(LogSeverity.Debug, nameof(HandleNicknameModal), outfitDataJson));
                await socketModal.FollowupAsync($"No exact match found for {nickname}. Please try again.", ephemeral: true);
                return;
            }

            //  Check whether a database entry exists for this guild, before trying to access it
            Guild? guild = await findGuild;
            if (guild is null)
            {
                await Log(new LogMessage(LogSeverity.Warning, nameof(HandleNicknameModal), $"No {nameof(Guild)} data found for guild id {socketModal.GuildId}"));
                await socketModal.RespondAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
                return;
            }

            //  Set the Discord nickname of the user, including outfit tag and either the member or non-member role, as defined by the guild admin
            if (socketModal.User is IGuildUser guildUser)
            {
                try
                {
                    await guildUser.ModifyAsync(x => x.Nickname = $"[{playerData?.character_name_list[0].outfit?.alias}] {nickname}");
                    if (guild.OutfitTag is not null && playerData?.character_name_list[0].outfit.alias?.ToLower() == guild.OutfitTag.ToLower() && guild.Roles?.MemberRole is ulong memberRoleId)
                    {
                        await guildUser.AddRoleAsync(memberRoleId);
                        await Log(new LogMessage(LogSeverity.Info, nameof(HandleNicknameModal), $"Added role {memberRoleId} to user {socketModal.User.Id} in guild {socketModal.GuildId}"));
                    }
                    else if (guild.OutfitTag is not null && playerData?.character_name_list[0].outfit.alias?.ToLower() != guild.OutfitTag.ToLower() && guild.Roles?.NonMemberRole is ulong nonMemberRoleId)
                    {
                        await guildUser.AddRoleAsync(nonMemberRoleId);
                        await Log(new LogMessage(LogSeverity.Info, nameof(HandleNicknameModal), $"Added role {nonMemberRoleId} to user {socketModal.User.Id} in guild {socketModal.GuildId}"));
                    }

                    guild.Users.Add(new User { CharacterName = nickname, CurrentOutfit = playerData?.character_name_list[0].outfit.alias, SocketUserId = socketModal.User.Id });
                    await _botDatabase.SaveChangesAsync();

                    await socketModal.FollowupAsync($"We've now set you Discord nickname to your in-game name, to avoid potential confusion during tense moments.\nWith that you're all set, thanks for joining and have fun!", ephemeral: true);
                    return;
                }
                catch (Exception ex)
                {
                    await Log(new LogMessage(LogSeverity.Warning, nameof(HandleNicknameModal), $"Unable to assign a nickname to guild user {socketModal.User.Id}. Encountered exception: \"{ex.Message}\""));
                    await socketModal.FollowupAsync($"Something went wrong when trying to set your nickname to \"[{playerData?.character_name_list[0].outfit?.alias}] {nickname}\"...\nPlease contact an admin to have them set the nickname!", ephemeral: false);
                }
            }
            else
            {
                await Log(new LogMessage(LogSeverity.Warning, nameof(HandleNicknameModal), $"Could not convert user {socketModal.User.Id} in guild {socketModal.GuildId} from SocketUser to IGuildUser."));
                await socketModal.FollowupAsync($"Something went wrong when trying to set your nickname to \"[{playerData?.character_name_list[0].outfit?.alias}] {nickname}\"...\nPlease contact an admin to have them set the nickname!", ephemeral: false);
            }

        }
        else
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleNicknameModal), "Census failed to return a list of names. Dumping returned JSON string as a debug log message."));
            await Log(new LogMessage(LogSeverity.Debug, nameof(HandleNicknameModal), outfitDataJson));
            await socketModal.FollowupAsync($"Whoops, something went wrong... Unable to find that user due to an error in the Census database.");
        }
    }
    private async Task HandleFirstWithRankCommand(SocketSlashCommand command)
    {
        //TODO: Logging user ID potential GDPR violation?
        await Log(new LogMessage(LogSeverity.Info, nameof(HandleFirstWithRankCommand), $"User {command.User.Id} executed first-with-rank. The member holding rank {command.Data.Options.First().Value} the longest was requested."));
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
    private async Task HandleFindPromotableMemberAtRankCommand(SocketSlashCommand command)
    {
        await Log(new LogMessage(LogSeverity.Info, nameof(HandleFindPromotableMemberAtRankCommand), $"find-promotable-member-at-rank called by user {command.User.Id} with params: {command.Data.Options.ToArray()[0].Value}{(command.Data.Options.Count > 2 ? ", " + command.Data.Options.ToArray()[2].Value : "")}"));
        await command.DeferAsync();

        var outfitDataJson = await _censusclient.GetStringAsync($"http://census.daybreakgames.com/s:{appSettings.GetConnectionString("CensusAPIKey")}/get/ps2:v2/outfit/?c:show=outfit_id,alias_lower,alias,member_count&alias_lower=txlc&c:join=outfit_rank^inject_at:ranks^show:ordinal%27name^list:1^terms:ordinal");
        await Log(new LogMessage(LogSeverity.Info, nameof(HandleFindPromotableMemberAtRankCommand), $"Census returned: {outfitDataJson}"));
        if (JsonSerializer.Deserialize<rReturnedOutfitData>(outfitDataJson, defaultCensusJsonDeserializeOptions) is rReturnedOutfitData returnedData)
        {
            rOutfitData outfitData = returnedData.outfit_list[0];
            if (returnedData.returned == 0 || outfitData.ranks is null)
            {
                await Log(new LogMessage(LogSeverity.Warning, nameof(HandleFindPromotableMemberAtRankCommand), $"No data found by Census for outfit with tag \"{command.Data.Options.First().Value}\""));
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
        bool noOptionsSpecified = command.Data.Options.Count == 0 ? true : false;
        int commandsPerPage = 4;
        int startingPage = 0;
        //  x.DefaultMemberPermissions are AND'ed together. When the result of an AND between x.Permissions and p is more than one, we know the user has at least one of the permission required for the command
        List<ApplicationCommandProperties> availableCommands = availableGuildCommands(command);
        int totalPages = (int)Math.Ceiling((double)availableCommands.Count / commandsPerPage);

        //  Only one entry for each option can exist
        if ((Int64?)command.Data.Options.Where(x => x.Name == "page").FirstOrDefault(defaultValue: null)?.Value is Int64 || noOptionsSpecified)
        {
            Int64 requestedPage = 1;
            if (!noOptionsSpecified)
                requestedPage = (Int64)command.Data.Options.Where(x => x.Name == "page").FirstOrDefault(defaultValue: null)?.Value!;

            if (requestedPage > totalPages)
                requestedPage = totalPages;

            startingPage = (int)requestedPage - 1;

        List<Embed> embeds = new List<Embed>();

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

        } else if (command.Data.Options.Where(x => x.Name == "command").FirstOrDefault(defaultValue: null)?.Value is string requestedCommand)
        {
            if (requestedCommand.StartsWith("/"))
                requestedCommand = requestedCommand.TrimStart('/');

            if((SlashCommandProperties)availableCommands.Where(x => x.Name.Value.ToLower() == requestedCommand.ToLower()).FirstOrDefault(defaultValue: null)! is SlashCommandProperties slashCommand)
            {
                var embed = CommandHelpEmbed(slashCommand);
                await command.RespondAsync(embed: embed.Build());
            }else if((SlashCommandProperties)globalApplicationCommandProperties.Where(x => x.Name.Value.ToLower() == requestedCommand.ToLower()).FirstOrDefault(defaultValue: null)! is SlashCommandProperties)
            {
                await command.RespondAsync($"You don't have the right permissions to execute command `/{requestedCommand}`");
            }
            else
            {
                await command.RespondAsync($"Command `/{requestedCommand}` doesn't exist");
            }
        }
    }
    private EmbedBuilder CommandHelpEmbed(SlashCommandProperties slashCommand)
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
        bool respondEphemerally = true;
        SocketTextChannel targetChannel;
        if (!command.Data.Options.IsNullOrEmpty() && command.Data.Options.First().Value is SocketTextChannel channel)
        {
            targetChannel = channel;
            respondEphemerally = channel == command.Channel ? true : false;
        }
        else
            targetChannel = (SocketTextChannel)command.Channel;

        await SendNicknamePoll(targetChannel);
        await command.RespondAsync($"Poll sent to <#{targetChannel.Id}>", ephemeral: respondEphemerally);
    }

    private async Task HandleIncludeNicknamePoll(SocketSlashCommand command)
    {
        await command.DeferAsync();
        if (_botDatabase.Guilds.Find(command.GuildId) is Guild guild && command.Data.Options.First().Value is bool include)
        {
            guild.askNicknameUponWelcome = include;
            await command.FollowupAsync($"Welcome messages will {(include ? "" : "not")} include a nickname poll");
            _botDatabase.SaveChanges();
        }
        else
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleIncludeNicknamePoll), $"No database entry found for guild {command.GuildId}"));
            await command.FollowupAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
        }

    }

    private async Task HandleSetLogChannel(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if (command.GuildId is null || (await _botDatabase.getGuildByGuildIdAsync((ulong)command.GuildId))?.Channels is not Channels channels)
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleSetLogChannel), $"No {nameof(Channels)} found for guild id {command.GuildId}"));
            await command.RespondAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        //  Specifying a channel is required for this command, and thus channel cannot be null
        SocketGuildChannel channel = (SocketGuildChannel)command.Data.Options.First().Value;
        channels.LogChannel = channel.Id;
        _botDatabase.SaveChanges();

        await Log(new LogMessage(LogSeverity.Info, nameof(HandleSetLogChannel), $"Log channel set to {channel.Id} for guild {command.GuildId}"));
        await command.FollowupAsync($"Log channel set to <#{channel.Id}>");
        if(!hasPermissionsToWriteChannel(channel))
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleSetLogChannel), $"Bot doesn't have the right permissions to post in channel {channel.Id} in guild {command.GuildId}"));
            await command.FollowupAsync($"Warning: the bot doesn't have the right permissions to post in <#{channel.Id}>. Please add the \"View Channel\" permission to the {_botclient.GetGuild((ulong)command.GuildId).GetUser(_botclient.CurrentUser.Id).Roles.FirstOrDefault(x => x.IsManaged)?.Mention} role in channel <#{channel.Id}>");
        }
    }
    private async Task HandleSetWelcomeChannel(SocketSlashCommand command)
    {
        if(command.GuildId is null || (await _botDatabase.getGuildByGuildIdAsync((ulong)command.GuildId))?.Channels is not Channels channels)
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleSetWelcomeChannel), $"No {nameof(Channels)} found for guild id {command.GuildId}"));
            await command.RespondAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        //  Specifying a channel is required for this command, and thus channel cannot be null
        SocketGuildChannel channel = (SocketGuildChannel)command.Data.Options.First().Value;
        channels.WelcomeChannel = channel.Id;
        _botDatabase.SaveChanges();

        await Log(new LogMessage(LogSeverity.Info, nameof(HandleSetWelcomeChannel), $"Welcome channel set to {channel.Id} for guild {command.GuildId}"));
        await command.RespondAsync($"Welcome channel set to <#{channel.Id}>");
        if (!hasPermissionsToWriteChannel(channel))
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleSetWelcomeChannel), $"Bot doesn't have the right permissions to post in channel {channel.Id} in guild {command.GuildId}"));
            await command.FollowupAsync($"Warning: the bot doesn't have the right permissions to post in <#{channel.Id}>. Please add the \"View Channel\" permission to the {_botclient.GetGuild((ulong)command.GuildId).GetUser(_botclient.CurrentUser.Id).Roles.FirstOrDefault(x => x.IsManaged)?.Mention} role in channel <#{channel.Id}>");
        }
    }

    private async Task HandleSetMemberRole(SocketSlashCommand command)
    {
        if (_botDatabase.Guilds.Find(command.GuildId)?.Roles is not Roles roles)
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleSetMemberRole), $"No {nameof(Channels)} found for guild id {command.GuildId}"));
            await command.RespondAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        SocketRole? role = command.Data.Options.First().Value as SocketRole;
        roles.MemberRole = role?.Id;
        _botDatabase.SaveChanges();

        await Log(new LogMessage(LogSeverity.Info, nameof(HandleSetMemberRole), $"Member role set to {role?.Id} for guild {command.GuildId}"));
        await command.RespondAsync($"Member role set to {role?.Mention}");
    }
    private async Task HandleSetNonMemberRole(SocketSlashCommand command)
    {
        if (_botDatabase.Guilds.Find(command.GuildId)?.Roles is not Roles guildParameters)
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleSetNonMemberRole), $"No {nameof(Channels)} found for guild id {command.GuildId}"));
            await command.RespondAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        SocketRole? role = command.Data.Options.First().Value as SocketRole;
        guildParameters.NonMemberRole = role?.Id;
        _botDatabase.SaveChanges();

        await Log(new LogMessage(LogSeverity.Info, nameof(HandleSetNonMemberRole), $"Non-member role set to {role?.Id} for guild {command.GuildId}"));
        await command.RespondAsync($"Non-member role set to {role?.Mention}");
    }
    private async Task HandleSetMainOutfit(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if (_botDatabase.Guilds.Find(command.GuildId) is not Guild guild)
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleSetMainOutfit), $"No {nameof(Channels)} found for guild id {command.GuildId}"));
            await command.FollowupAsync("Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
            return;
        }

        string tag = (string)command.Data.Options.First().Value;

        var outfitCountJson = await _censusclient.GetStringAsync($"http://census.daybreakgames.com/s:{appSettings.GetConnectionString("CensusAPIKey")}/count/ps2/outfit/?alias_lower={tag.ToLower()}");
        int? count = JObject.Parse(outfitCountJson)["count"]?.ToObject<int>();      //  Only the number of results is returned by the query. If the result is 1 it is assumed the given outfit exists, though it might be different from what the user requested
        if(count == 0)
        {
            await Log(new LogMessage(LogSeverity.Info, nameof(HandleSetMainOutfit), $"No outfit found with tag {tag}"));
            await command.FollowupAsync($"No outfit found with tag {tag}!");
            return;
        }
        else if (!count.HasValue || count != 1)
        {
            await Log(new LogMessage(LogSeverity.Warning, nameof(HandleSetMainOutfit), $"Something went wrong requesting outfit tag {tag} of Census. Dumping JSON as a debug log message."));
            await Log(new LogMessage(LogSeverity.Debug, nameof(HandleSetMainOutfit), $"{outfitCountJson}"));
            await command.FollowupAsync($"Something went wrong while validating outfit tag {tag}...");
            return;
        }

        await command.FollowupAsync($"Main outfit set to {command.Data.Options.First().Value}");
        await Log(new LogMessage(LogSeverity.Info, nameof(HandleSetMainOutfit), $"Main outfit of guild {command.GuildId} set to {command.Data.Options.First().Value}"));

        guild.OutfitTag = tag;
        _botDatabase.SaveChanges();
    }

    private bool hasOutfitTagConfigured(ulong? guildId)
    {
        if (guildId is null || _botDatabase.Guilds.Find(guildId)?.OutfitTag is null)
            return false;
        return true;
    }

    private bool hasPermissionsToWriteChannel(SocketGuildChannel channel)
    {
        SocketRole role = _botclient.GetGuild(channel.Guild.Id).GetUser(_botclient.CurrentUser.Id).Roles.FirstOrDefault(x => x.IsManaged)!;     //  Bots always have a managed role of their own
        SocketRole everyoneRole = channel.Guild.EveryoneRole;
        if (channel.GetPermissionOverwrite(role) is OverwritePermissions rolePerms && rolePerms.ViewChannel == PermValue.Allow && (rolePerms.SendMessages == PermValue.Allow || rolePerms.SendMessages == PermValue.Inherit && channel.Guild.GetRole(role.Id).Permissions.SendMessages is true))
            return true;
        else if (channel.GetPermissionOverwrite(_botclient.CurrentUser) is OverwritePermissions botPerms && botPerms.ViewChannel == PermValue.Allow && (botPerms.SendMessages == PermValue.Allow || botPerms.SendMessages == PermValue.Inherit && channel.Guild.GetUser(_botclient.CurrentUser.Id).GuildPermissions.SendMessages is true))
            return true;
        else if (channel.GetPermissionOverwrite(everyoneRole) is OverwritePermissions everyonePerms && (everyonePerms.ViewChannel == PermValue.Allow || everyonePerms.ViewChannel == PermValue.Inherit && everyoneRole.Permissions.ViewChannel is true) && (everyonePerms.SendMessages == PermValue.Allow || everyonePerms.SendMessages == PermValue.Inherit && channel.Guild.GetRole(everyoneRole.Id).Permissions.SendMessages is true))
            return true;
        return false;
    }
        
    private async Task SendNicknamePoll(SocketTextChannel channel)
    {
        var confirmationButton = new ComponentBuilder()
                .WithButton("Get Started", "start-nickname-process");
            if (hasPermissionsToWriteChannel(channel))
            {
                await channel.SendMessageAsync($"To get started, press this button so we can set you up properly:", components: confirmationButton.Build());
            }
            else
            {
                await Log(new LogMessage(LogSeverity.Warning, nameof(SendNicknamePoll), $"Missing permissions to send nickname poll in channel {channel.Id} in guild {channel.Guild.Id}"));
                await SendLogChannelMessageAsync(channel.Guild.Id, "Couldn't send nickname poll: missing permissions!");
            }
    }

    List<ApplicationCommandProperties> availableGuildCommands(SocketInteraction interaction)
    {
        if(interaction.GuildId is null)
            return new List<ApplicationCommandProperties>();
        return globalApplicationCommandProperties.Where(x => { if (x.DefaultMemberPermissions.IsSpecified) return _botclient.GetGuild((ulong)interaction.GuildId!).GetUser(interaction.User.Id).GuildPermissions.Has(x.DefaultMemberPermissions.Value); else return true; }).ToList();
    }

    private async Task SendLogChannelMessageAsync(ulong guildId, string message, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if ((await _botDatabase.getGuildByGuildIdAsync(guildId))?.Channels?.LogChannel is ulong logChannelId && _botclient.GetGuild(guildId).GetChannel(logChannelId) is SocketTextChannel logChannel && hasPermissionsToWriteChannel(logChannel))
            await logChannel.SendMessageAsync(message);
        else
            await Log(new LogMessage(LogSeverity.Warning, caller, $"Failed to send log message to in guild {guildId}. Has the LogChannel been set up properly?"));
    }

    private Task Log(LogMessage message)
    {
        Console.WriteLine(message.ToString());
        return Task.CompletedTask;
    }
}

record rPromotableMemberParams(
    string outfit,
    int minActivity,
    int? maxInactivity
    );

public record rResponse
{
    public rPlayerData[] outfit_member_extended_list { get; set; }
    public int returned { get; set; }
}

public record rReturnedOutfitData
{
    public rOutfitData[] outfit_list { get; set; }
    public int returned { get; set; }
}

public record rReturnedPlayerDataLight(
    rPlayerDataLight[] character_name_list,
    int? returned
    );

public record rOutfitData(
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

public record rPlayerData(
    ulong? character_id,
    ulong? member_since,
    string? member_since_date,
    string? member_rank,
    int? member_rank_ordinal,
    string? alias,
    rCharacterName? character_id_join_character_name
    );

public record rCharacterName(
        ulong? character_id,
        rName? name
        );

public record rName(
    string? first,
    string? first_lower
    );

public record rPlayerDataLight(
    ulong character_id,
    rName name,
    rOutfitData outfit
    );