using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Serilog.Events;

using Discord;
using Discord.Interactions;

using PS2_Assistant.Attributes.Preconditions;
using PS2_Assistant.Data;
using PS2_Assistant.Logger;
using PS2_Assistant.Models.Census.API;
using PS2_Assistant.Models.Database;

namespace PS2_Assistant.Modules
{
    public class ModalModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly SourceLogger _logger;
        private readonly BotContext _guildDb;
        private readonly HttpClient _httpClient;
        private readonly AssistantUtils _assistantUtils;
        private readonly IConfiguration _configuration;

        public ModalModule(SourceLogger logger, BotContext guildDb, HttpClient httpClient, AssistantUtils assistantUtils, IConfiguration configuration)
        {
            _logger = logger;
            _guildDb = guildDb;
            _httpClient = httpClient;
            _assistantUtils = assistantUtils;
            _configuration = configuration;
        }

        public class NicknameModal : IModal
        {
            public string Title => "Planetside username";

            [InputLabel("Please enter your Planetside username:")]
            [ModalTextInput("nickname", TextInputStyle.Short, "name", 2, 32)]
            public string Nickname { get; set; } = "";
        }

        [NeedsDatabaseEntry]
        [ModalInteraction("nickname-modal")]
        public async Task NicknameModalInteraction(NicknameModal modal)
        {
            await RespondAsync("Validating character name...");
            
            string nickname = modal.Nickname;

            var findGuild = _guildDb.GetGuildByGuildIdAsync(Context.Guild.Id);

            //  Validate given nickname
            nickname = nickname.Trim();
            if (Regex.IsMatch(nickname, @"[\s]"))
            {
                _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "User {UserId} submitted an invalid username: {nickname}", Context.User.Id, nickname);
                await ModifyOriginalResponseAsync(x => x.Content = $"Invalid nickname submitted: {nickname}. Whitespace are not allowed. Please try again.");
                return;
            }
            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "User {UserId} submitted nickname: {nickname}", Context.User.Id, nickname);

            //  Request players with this name from Census, including a few other, similar names
            string outfitDataJson = await _httpClient.GetStringAsync($"http://census.daybreakgames.com/s:{_configuration.GetConnectionString("CensusAPIKey")}/get/ps2:v2/{PlayerDataLight.CollectionQuery}&name.first_lower=*{nickname.ToLower()}");
            
            var returnedData = JsonConvert.DeserializeObject<CensusObjectWrapper>(outfitDataJson);
            if (returnedData?.Data?["character_name_list"].ToObject<List<PlayerDataLight>>() is List<PlayerDataLight> playerData && returnedData.Returned.HasValue)
            {
                //  If 0 is returned, no similar names were found. If more than 1 are returned and the first result is incorrect, no exact match was found
                if (returnedData.Returned == 0 || returnedData.Returned > 1 && playerData[0].Name.FirstLower != nickname.ToLower())
                {
                    _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "Unable to find a match for name {nickname} in the Census database. Dumping returned JSON string as a debug log message.", nickname);
                    _logger.SendLog(LogEventLevel.Debug, Context.Guild.Id, "Unable to find match for {nickname} using Census API. Returned JSON:\n{json}", nickname, outfitDataJson);
                    await ModifyOriginalResponseAsync(x => x.Content = $"No exact match found for {nickname}. Please try again.");
                    return;
                }

                //  Check whether a database entry exists for this guild, before trying to access it
                Guild? guild = await findGuild;
                if (guild is null)
                {
                    _logger.SendLog(LogEventLevel.Error, Context.Guild.Id, "No guild data found in database for this guild");
                    await ModifyOriginalResponseAsync(x => x.Content = "Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
                    return;
                }

                //  Set the Discord nickname of the user, including outfit tag and either the member or non-member role, as defined by the guild admin
                string? alias = playerData[0].Outfit.Alias;
                if (Context.User is IGuildUser guildUser)
                {

                    //  Check whether a user with nickname already exists on the server, to prevent impersonation
                    if (guild.Users.Where(x => x.CharacterName == nickname && x.SocketUserId != Context.User.Id).FirstOrDefault(defaultValue: null) is User impersonatedUser)
                    {
                        _logger.SendLog(LogEventLevel.Warning, Context.Guild.Id, "Possible impersonation: user {UserId} tried to set nickname to {nickname}, but that character already exists in this guild!", Context.User.Id, nickname);
                        await FollowupAsync($"User {Context.User.Mention} tried to set his nickname to {nickname}, but user <@{impersonatedUser.SocketUserId}> already exists on the server! Incident reported...");
                        await _assistantUtils.SendLogChannelMessageAsync(Context.Guild.Id, $"User {Context.User.Mention} tried to set nickname to \"{nickname}\", but that user already exists on this server (<@{impersonatedUser.SocketUserId}>)");
                        return;
                    }

                    try
                    {
                        //  Assign Discord nickname and member/non-member role
                        await guildUser.ModifyAsync(x => x.Nickname = $"[{alias}] {nickname}");
                        if (guild.OutfitTag is not null && alias?.ToLower() == guild.OutfitTag.ToLower() && guild.Roles?.MemberRole is ulong memberRoleId)
                        {
                            await guildUser.AddRoleAsync(memberRoleId);
                            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "Added role {MemberRoleId} to user {UserId}", memberRoleId, Context.User.Id);
                        }
                        else if (guild.OutfitTag is not null && alias?.ToLower() != guild.OutfitTag.ToLower() && guild.Roles?.NonMemberRole is ulong nonMemberRoleId)
                        {
                            await guildUser.AddRoleAsync(nonMemberRoleId);
                            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "Added role {NonMemberId} to user {UserId}", nonMemberRoleId, Context.User.Id);
                        }

                        await ModifyOriginalResponseAsync(x => { x.Content = $"Nickname set to {guildUser.Mention}"; x.AllowedMentions = AllowedMentions.None; });
                        await FollowupAsync($"We've now set your Discord nickname to your in-game name, to avoid potential confusion during tense moments.\nWith that you're all set, thanks for joining and have fun!", ephemeral: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.SendLog(LogEventLevel.Warning, Context.Guild.Id, "Unable to assign nickname to user {UserId}. Encountered exception:", Context.User.Id, exep: ex);
                        await ModifyOriginalResponseAsync(x => x.Content = $"Something went wrong when trying to set your nickname to \"[{alias}] {nickname}\"...\nPlease contact an admin to have them set the nickname!");
                    }

                    //  Check whether user already exists in the database for this guild
                    if (guild.Users.Where(x => x.SocketUserId == Context.User.Id).FirstOrDefault(defaultValue: null) is User user)
                    {
                        user.CharacterName = nickname;
                        user.CurrentOutfit = alias;
                    }
                    else
                        guild.Users.Add(new User { CharacterName = nickname, CurrentOutfit = alias, SocketUserId = Context.User.Id });
                    await _guildDb.SaveChangesAsync();
                }
                else
                {
                    _logger.SendLog(LogEventLevel.Warning, Context.Guild.Id, "Could not convert user {UserId} from SocketUser to IGuildUser", Context.User.Id);
                    await ModifyOriginalResponseAsync(x => x.Content = $"Something went wrong when trying to set your nickname to \"[{alias}] {nickname}\"...\nPlease contact an admin to have them set the nickname!");
                }

            }
            else
            {
                _logger.SendLog(LogEventLevel.Warning, Context.Guild.Id, "Census failed to return a list of names. Dumping returned JSON as a debug log message");
                _logger.SendLog(LogEventLevel.Debug, Context.Guild.Id, "Returned Census JSON: {json}", outfitDataJson);
                await ModifyOriginalResponseAsync(x => x.Content = $"Whoops, something went wrong... Unable to find that user due to an error in the Census database.");
            }
        }
    }
}
