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

            //  Check whether a database entry exists for this guild and parse the data returned by Census
            Guild? guild = await findGuild;
            List<PlayerDataLight>? playerData = await GetPlayerDataLightAsync(nickname, context.Guild.Id, _httpClient, _logger, _configuration);

            if (guild is null)
            {
                _logger.SendLog(LogEventLevel.Error, context.Guild.Id, "No guild data found in database for this guild");
                await context.Interaction.ModifyOriginalResponseAsync(x => x.Content = "Something went horribly wrong... No data found for this server. Please contact the developer of the bot.");
                return;
            }
            if (playerData is null)
            {
                await context.Interaction.ModifyOriginalResponseAsync(x => x.Content = $"No exact match found for {nickname}. Please try again.");
                return;
            }

            //  Set the Discord nickname of the user, including outfit tag and either the member or non-member role, as defined by the guild admin
            string outfitAlias = playerData[0].Outfit?.Alias ?? "";
            //  Check whether a user with nickname already exists on the server, to prevent impersonation
            if (guild.Users.Where(x => x.CharacterName == nickname && x.SocketUserId != targetUser.Id).FirstOrDefault(defaultValue: null) is User impersonatedUser)
            {
                _logger.SendLog(LogEventLevel.Warning, context.Guild.Id, "Possible impersonation: user {UserId} tried to set nickname to {nickname}, but that character already exists in this guild!", targetUser.Id, nickname);
                await context.Interaction.FollowupAsync($"User {targetUser.Mention} tried to set his nickname to {nickname}, but user <@{impersonatedUser.SocketUserId}> already exists on the server! Incident reported...");
                await _assistantUtils.SendLogChannelMessageAsync(context.Guild.Id, $"User {targetUser.Mention} tried to set nickname to \"{nickname}\", but that user already exists on this server (<@{impersonatedUser.SocketUserId}>)");
                return;
            }
          
            //  Assign nickname and notify user
            try
            {
                await AssignNicknameAsync(targetUser, outfitAlias, nickname, guild, _logger);

                await context.Interaction.ModifyOriginalResponseAsync(x => { x.Content = $"Nickname set to {targetUser.Mention}"; x.AllowedMentions = AllowedMentions.None; });
                if (context.User.Id == targetUser.Id)
                    await context.Interaction.FollowupAsync($"We've now set your Discord nickname to your in-game name, to avoid potential confusion during tense moments.\nWith that you're all set, thanks for joining and have fun!", ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.SendLog(LogEventLevel.Warning, context.Guild.Id, "Unable to assign nickname to user {UserId}. Encountered exception:", targetUser.Id, exep: ex);
                await context.Interaction.ModifyOriginalResponseAsync(x => x.Content = $"Something went wrong when trying to set <@{targetUser.Id}>'s nickname to \"[{outfitAlias}] {nickname}\"...\nPlease contact an admin to have them set the nickname!");
            }

            //  Check whether user already exists in the database for this guild
            if (guild.Users.Where(x => x.SocketUserId == targetUser.Id).FirstOrDefault(defaultValue: null) is User user)
            {
                user.CharacterName = nickname;
                user.CurrentOutfit = outfitAlias;
            }
            else
                guild.Users.Add(new User { CharacterName = nickname, CurrentOutfit = outfitAlias, SocketUserId = targetUser.Id });
            await _guildDb.SaveChangesAsync();
        }

        /// <summary>
        /// Assigns a nickname to a guild user, including an outfit tag
        /// </summary>
        /// <param name="user">The user who's nickname will be assigned</param>
        /// <param name="outfitTag">The outfit tag to add to the nickname</param>
        /// <param name="characterName">The name of the character to which the nickname will be set</param>
        /// <param name="guild">The database entry for the guild of which <paramref name="user"/> is part of</param>
        /// <param name="logger">The logger to which to send log messages</param>
        /// <returns></returns>
        public static async Task AssignNicknameAsync(IGuildUser user, string outfitTag, string characterName, Guild guild, SourceLogger logger)
        {
            //  Assign Discord nickname and member/non-member role
            await user.ModifyAsync(x => x.Nickname = $"[{outfitTag}] {characterName}");
            if (!guild.OutfitTag.IsNullOrEmpty())
            {
                //  guild.OutfitTag can't be null here
                if (outfitTag.ToLower() == guild.OutfitTag!.ToLower() && guild.Roles?.MemberRole is ulong memberRoleId)
                {
                    if (guild.Roles.NonMemberRole is ulong nonMemberRole && user.RoleIds.Contains(nonMemberRole))
                        await user.RemoveRoleAsync(nonMemberRole);

                    await user.AddRoleAsync(memberRoleId);
                    logger.SendLog(LogEventLevel.Information, guild.GuildId, "Added role {MemberRoleId} to user {UserId}", memberRoleId, user.Id);
                }
                else if (outfitTag.ToLower() != guild.OutfitTag!.ToLower() && guild.Roles?.NonMemberRole is ulong nonMemberRoleId)
                {
                    if (guild.Roles.MemberRole is ulong memberRole && user.RoleIds.Contains(memberRole))
                        await user.RemoveRoleAsync(memberRole);

                    await user.AddRoleAsync(nonMemberRoleId);
                    logger.SendLog(LogEventLevel.Information, guild.GuildId, "Added role {NonMemberId} to user {UserId}", nonMemberRoleId, user.Id);
                }
            }
        }

        /// <summary>
        /// Returns a list of characters by Census, where the first element contains the character specified by <paramref name="requestedNickname"/>
        /// </summary>
        /// <param name="requestedNickname">The character to search for</param>
        /// <param name="guildId">The Id of the Discord guild this method is run for</param>
        /// <param name="censusClient">The client connected to Census</param>
        /// <param name="logger">The logger to which to send log messages</param>
        /// <param name="configuration">The configuration holding the Census API key</param>
        /// <param name="requestedResults">The maximum number of results to be returned (minimum of 1)</param>
        /// <returns>A list of characters with similar names to the one requested by <paramref name="requestedNickname"/>, or null if no exact match was found</returns>
        public static async Task<List<PlayerDataLight>?> GetPlayerDataLightAsync(string requestedNickname, ulong guildId, HttpClient censusClient, SourceLogger logger, IConfiguration configuration, int requestedResults = 6)
        {
            //  Can't query for less than 1 characters
            if (requestedResults < 1) requestedResults = 1;

            //  Request players with this name from Census, including a few other, similar names
            string outfitDataJson = await censusClient.GetStringAsync($"http://census.daybreakgames.com/s:{configuration.GetConnectionString("CensusAPIKey")}/get/ps2:v2/{PlayerDataLight.CollectionQuery}&name.first_lower=*{requestedNickname.ToLower()}&c:limit={requestedResults}");

            JsonSerializer serializer = new()
            {
                ContractResolver = new DefaultContractResolver() { NamingStrategy = new SnakeCaseNamingStrategy() }
            };
            var returnedData = JsonConvert.DeserializeObject<CensusObjectWrapper>(outfitDataJson);

            //  Check whether Census returned valid data
            if (returnedData?.Data?["character_name_list"].ToObject<List<PlayerDataLight>>(serializer) is not List<PlayerDataLight> playerData || !returnedData.Returned.HasValue)
            {
                logger.SendLog(LogEventLevel.Warning, guildId, "Census failed to return a list of names. Dumping returned JSON as a debug log message");
                logger.SendLog(LogEventLevel.Debug, guildId, "Returned Census JSON: {json}", outfitDataJson);
                return null;
            }

            //  If 0 is returned, no similar names were found. If more than 1 are returned and the first result is incorrect, no exact match was found
            if (returnedData.Returned == 0 || playerData[0].Name.FirstLower != requestedNickname.ToLower())
            {
                logger.SendLog(LogEventLevel.Information, guildId, "Unable to find a match for name {nickname} in the Census database. Dumping returned JSON string as a debug log message.", requestedNickname);
                logger.SendLog(LogEventLevel.Debug, guildId, "Unable to find match for {nickname} using Census API. Returned JSON:\n{json}", requestedNickname, outfitDataJson);
                return null;
            }

            return playerData;
        }
    }
}
