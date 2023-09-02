using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

using Discord;
using Discord.Interactions;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using Serilog.Events;

using PS2_Assistant.Data;
using PS2_Assistant.Logger;
using PS2_Assistant.Models.Census.API;
using PS2_Assistant.Models.Database;
using Microsoft.IdentityModel.Tokens;

namespace PS2_Assistant.Handlers
{
    public class NicknameHandler
    {
        private readonly BotContext _guildDb;
        private readonly SourceLogger _logger;
        private readonly AssistantUtils _assistantUtils;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        public NicknameHandler(BotContext guildDb, SourceLogger logger, AssistantUtils assistantUtils, HttpClient httpClient, IConfiguration configuration)
        {
            _guildDb = guildDb;
            _logger = logger;
            _assistantUtils = assistantUtils;
            _httpClient = httpClient;
            _configuration = configuration;
        }

        public async Task VerifyNicknameAsync(SocketInteractionContext context, string nickname, IGuildUser targetUser)
        {
            var findGuild = _guildDb.GetGuildByGuildIdAsync(context.Guild.Id);

            //  Validate given nickname
            nickname = nickname.Trim();
            if (Regex.IsMatch(nickname, @"[\s]"))
            {
                _logger.SendLog(LogEventLevel.Information, context.Guild.Id, "User {UserId} submitted an invalid username: {nickname}", targetUser.Id, nickname);
                await context.Interaction.ModifyOriginalResponseAsync(x => x.Content = $"Invalid nickname submitted: {nickname}. Whitespace are not allowed. Please try again.");
                return;
            }else if (nickname.IsNullOrEmpty())
            {
                _logger.SendLog(LogEventLevel.Information, context.Guild.Id, "User {UserId} submitted an empty username", targetUser.Id, nickname);
                await context.Interaction.ModifyOriginalResponseAsync(x => x.Content = $"Invalid nickname submission: no empty nickname allowed.");
                return;
            }
            _logger.SendLog(LogEventLevel.Information, context.Guild.Id, "User {UserId} submitted nickname: {nickname}", targetUser.Id, nickname);

            //  Request players with this name from Census, including a few other, similar names
            string outfitDataJson = await _httpClient.GetStringAsync($"http://census.daybreakgames.com/s:{_configuration.GetConnectionString("CensusAPIKey")}/get/ps2:v2/{PlayerDataLight.CollectionQuery}&name.first_lower=*{nickname.ToLower()}");

            JsonSerializer serializer = new()
            {
                ContractResolver = new DefaultContractResolver() { NamingStrategy = new SnakeCaseNamingStrategy() }
            };
            var returnedData = JsonConvert.DeserializeObject<CensusObjectWrapper>(outfitDataJson);
            if (returnedData?.Data?["character_name_list"].ToObject<List<PlayerDataLight>>(serializer) is List<PlayerDataLight> playerData && returnedData.Returned.HasValue)
            {
                //  If 0 is returned, no similar names were found. If more than 1 are returned and the first result is incorrect, no exact match was found
                if (returnedData.Returned == 0 || returnedData.Returned > 1 && playerData[0].Name.FirstLower != nickname.ToLower())
                {
                    _logger.SendLog(LogEventLevel.Information, context.Guild.Id, "Unable to find a match for name {nickname} in the Census database. Dumping returned JSON string as a debug log message.", nickname);
                    _logger.SendLog(LogEventLevel.Debug, context.Guild.Id, "Unable to find match for {nickname} using Census API. Returned JSON:\n{json}", nickname, outfitDataJson);
                    await context.Interaction.ModifyOriginalResponseAsync(x => x.Content = $"No exact match found for {nickname}. Please try again.");
                    return;
                }

                //  Check whether a database entry exists for this guild, before trying to access it
                Guild? guild = await findGuild;
                if (guild is null)
                {
                    _logger.SendLog(LogEventLevel.Error, context.Guild.Id, "No guild data found in database for this guild");
                    await context.Interaction.ModifyOriginalResponseAsync(x => x.Content = "Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
                    return;
                }

                //  Set the Discord nickname of the user, including outfit tag and either the member or non-member role, as defined by the guild admin
                string? alias = playerData[0].Outfit.Alias;
                //  Check whether a user with nickname already exists on the server, to prevent impersonation
                if (guild.Users.Where(x => x.CharacterName == nickname && x.SocketUserId != targetUser.Id).FirstOrDefault(defaultValue: null) is User impersonatedUser)
                {
                    bool followupEphemerally = (await context.Interaction.GetOriginalResponseAsync()).Flags?.HasFlag(MessageFlags.Ephemeral) ?? false;
                    _logger.SendLog(LogEventLevel.Warning, context.Guild.Id, "Possible impersonation: user {UserId} tried to set nickname to {nickname}, but that character already exists in this guild!", targetUser.Id, nickname);
                    await context.Interaction.FollowupAsync($"User {targetUser.Mention} tried to set his nickname to {nickname}, but user <@{impersonatedUser.SocketUserId}> already exists on the server! Incident reported...", ephemeral: followupEphemerally);
                    await _assistantUtils.SendLogChannelMessageAsync(context.Guild.Id, $"User {targetUser.Mention} tried to set nickname to \"{nickname}\", but that user already exists on this server (<@{impersonatedUser.SocketUserId}>)");
                    return;
                }

                try
                {
                    //  Assign Discord nickname and member/non-member role
                    await targetUser.ModifyAsync(x => x.Nickname = $"[{alias}] {nickname}");
                    if (guild.OutfitTag is not null && alias?.ToLower() == guild.OutfitTag.ToLower() && guild.Roles?.MemberRole is ulong memberRoleId)
                    {
                        if (guild.Roles.NonMemberRole is ulong nonMemberRole && targetUser.RoleIds.Contains(nonMemberRole))
                            await targetUser.RemoveRoleAsync(nonMemberRole);

                        await targetUser.AddRoleAsync(memberRoleId);
                        _logger.SendLog(LogEventLevel.Information, context.Guild.Id, "Added role {MemberRoleId} to user {UserId}", memberRoleId, targetUser.Id);
                    }
                    else if (guild.OutfitTag is not null && alias?.ToLower() != guild.OutfitTag.ToLower() && guild.Roles?.NonMemberRole is ulong nonMemberRoleId)
                    {
                        if (guild.Roles.MemberRole is ulong memberRole && targetUser.RoleIds.Contains(memberRole))
                            await targetUser.RemoveRoleAsync(memberRole);

                        await targetUser.AddRoleAsync(nonMemberRoleId);
                        _logger.SendLog(LogEventLevel.Information, context.Guild.Id, "Added role {NonMemberId} to user {UserId}", nonMemberRoleId, targetUser.Id);
                    }

                    await context.Interaction.ModifyOriginalResponseAsync(x => { x.Content = $"Nickname set to {targetUser.Mention}"; x.AllowedMentions = AllowedMentions.None; });
                    if(context.User.Id == targetUser.Id)
                        await context.Interaction.FollowupAsync($"We've now set your Discord nickname to your in-game name, to avoid potential confusion during tense moments.\nWith that you're all set, thanks for joining and have fun!", ephemeral: true);
                }
                catch (Exception ex)
                {
                    _logger.SendLog(LogEventLevel.Warning, context.Guild.Id, "Unable to assign nickname to user {UserId}. Encountered exception:", targetUser.Id, exep: ex);
                    await context.Interaction.ModifyOriginalResponseAsync(x => x.Content = $"Something went wrong when trying to set <@{targetUser.Id}>'s nickname to \"[{alias}] {nickname}\"...\nPlease contact an admin to have them set the nickname!");
                }

                //  Check whether user already exists in the database for this guild
                if (guild.Users.Where(x => x.SocketUserId == targetUser.Id).FirstOrDefault(defaultValue: null) is User user)
                {
                    user.CharacterName = nickname;
                    user.CurrentOutfit = alias;
                }
                else
                    guild.Users.Add(new User { CharacterName = nickname, CurrentOutfit = alias, SocketUserId = targetUser.Id });
                await _guildDb.SaveChangesAsync();
            }
            else
            {
                _logger.SendLog(LogEventLevel.Warning, context.Guild.Id, "Census failed to return a list of names. Dumping returned JSON as a debug log message");
                _logger.SendLog(LogEventLevel.Debug, context.Guild.Id, "Returned Census JSON: {json}", outfitDataJson);
                await context.Interaction.ModifyOriginalResponseAsync(x => x.Content = $"Whoops, something went wrong... Unable to find that user due to an error in the Census database.");
            }
        }
    }
}
