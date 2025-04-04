﻿using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

using Coravel.Scheduling.Schedule;
using Coravel.Scheduling.Schedule.Interfaces;

using Discord;
using Discord.WebSocket;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using Serilog.Events;

using PS2_Assistant.Data;
using PS2_Assistant.Invocables;
using PS2_Assistant.Logger;
using PS2_Assistant.Models.Census.API;
using PS2_Assistant.Models.Database;

namespace PS2_Assistant.Handlers
{
    public class OutfitTagHandler
    {
        private readonly BotContext _guildDb;
        private readonly SourceLogger _logger;
        private readonly HttpClient _httpClient;
        private readonly AssistantUtils _assistantUtils;
        private readonly DiscordSocketClient _client;
        private readonly IScheduler _scheduler;
        private readonly IConfiguration _configuration;

        public static readonly string OutfitTagUpdateInvocableId = "CensusRequest";     //  Groups outfit tag updates together to prevent overlapping and thus simultaneous census requests
        private int _outfitTagUpdatesScheduledCount = 0;    //  Keep track of the amount of outfit tag update invocables scheduled

        public OutfitTagHandler(BotContext guildDb, SourceLogger logger, HttpClient httpClient, AssistantUtils assistantUtils, DiscordSocketClient client, IScheduler scheduler, IConfiguration configuration)
        {
            _guildDb = guildDb;
            _logger = logger;
            _httpClient = httpClient;
            _assistantUtils = assistantUtils;
            _client = client;
            _scheduler = scheduler;
            _configuration = configuration;
        }

        public void ScheduleOutfitTagUpdate(ulong guildId)
        {
            int updateAtMinute = Random.Shared.Next(0, 15);
            _logger.SendLog(LogEventLevel.Information, guildId, "Scheduled outfit tag update invocable for this guild at every {Minutes} minutes past every second hour", updateAtMinute);
            _scheduler.ScheduleWithParams<OutfitTagUpdateInvocable>(guildId)
                //.Cron($"*/1 * * * *");  //  (CRON doesn't seem to be working with ScheduleWithParams, please disregard) Every two hours, at a random minute (0 to 15 inclusive) after the hour
                .HourlyAt(updateAtMinute)   //  Execute every hour at updateAtMinute minutes (0-15, inclusive) after the hour
                .PreventOverlapping(OutfitTagUpdateInvocableId);

            _outfitTagUpdatesScheduledCount++;
        }

        public void ScheduleOutfitTagUpdateForAllGuilds()
        {
            foreach(SocketGuild guild in _client.Guilds)
            {
                ScheduleOutfitTagUpdate(guild.Id);
            }
        }
        public void UnscheduleOutfitTagUpdateForAllGuilds()
        {
            if (_scheduler is not Scheduler schedulerClass)
                return;

            int totalScheduleCount = _outfitTagUpdatesScheduledCount;
            int unscheduleFailedCount = 0;

            while(_outfitTagUpdatesScheduledCount > 0)
            {
                //  When unscheduling fails, we try to continue. However, most likely we keep running into the same problematic schedule over and over again.
                //  As long as the library doesn't come with a better way to do this, nothing can be done about this other than restarting the bot once errors
                //  start appearing
                if (!schedulerClass.TryUnschedule(OutfitTagUpdateInvocableId))
                    unscheduleFailedCount++;
                _outfitTagUpdatesScheduledCount--;
            }

            if (unscheduleFailedCount == 0)
                _logger.SendLog(LogEventLevel.Information, null, "Successfully unscheduled all {ScheduleCount} outfit tag update invocables", totalScheduleCount);
            else if (unscheduleFailedCount == totalScheduleCount)
                _logger.SendLog(LogEventLevel.Error, null, "Failed to unschedule any of the {ScheduleCount} outfit tag update invocables", unscheduleFailedCount);
            else
                _logger.SendLog(LogEventLevel.Error, null, "Partial failure: failed to unschedule {UnscheduleFailedCount} out of {ScheduleCount} outfit tag update invocables", unscheduleFailedCount, totalScheduleCount);
        }

        public async Task UpdateOutfitTagsAsync(ulong guildId)
        {
            _logger.SendLog(LogEventLevel.Information, guildId, "Updating outfit tags");
            if (await _guildDb.GetGuildByGuildIdAsync(guildId) is not Guild guild)
            {
                _logger.SendLog(LogEventLevel.Error, guildId, "Couldn't update outfit tags: no registered users found in database");
                return;
            }
            Dictionary<string, List<User>> registeredOutfits = RegisteredUsersByOutfit(guild);

            List<string> outfitsToQuery = new();
            List<string> individualCharactersToQuery = new();
            foreach (string outfitTag in registeredOutfits.Keys)
            {
                if (outfitTag == "" || registeredOutfits[outfitTag].Count < 3)
                    individualCharactersToQuery.AddRange(registeredOutfits[outfitTag].Where(x => !x.CharacterName.IsNullOrEmpty()).Select(x => x.CharacterName!));
                else
                    outfitsToQuery.Add(outfitTag);
            }
            _logger.SendLog(LogEventLevel.Verbose, guildId, "Outfits found:\n{OutfitTags}", string.Join('\n', outfitsToQuery));
            _logger.SendLog(LogEventLevel.Verbose, guildId, "Individual characters found:\n{CharacterNames}", string.Join('\n', individualCharactersToQuery));

            //  Query Census for the members in each outfits
            _logger.SendLog(LogEventLevel.Debug, guildId, "Querying {OutfitCount} outfits", outfitsToQuery.Count);
            foreach (string outfitTag in outfitsToQuery) {

                List<User> charactersToCheck = registeredOutfits[outfitTag];
                int resultsInLastQuery = 0;
                int totalResultsCount = 0;
                do {
                    //  Query Census
                    string censusQuery = $"http://census.daybreakgames.com/s:{_configuration.GetConnectionString("CensusAPIKey")}/get/ps2:v2/{OutfitMembersLight.CollectionQuery}&alias_lower=*{outfitTag.ToLower()}&c:start={totalResultsCount}";
                    string outfitDataJson = await _httpClient.GetStringAsync(censusQuery);

                    //  Validate returned data
                    JsonSerializer serializer = new() { ContractResolver = new DefaultContractResolver() { NamingStrategy = new SnakeCaseNamingStrategy() } };
                    var returnedData = JsonConvert.DeserializeObject<CensusObjectWrapper>(outfitDataJson);
                    if (returnedData?.Data?["outfit_member_extended_list"].ToObject<List<OutfitMembersLight>>(serializer) is not List<OutfitMembersLight> playerData || !returnedData.Returned.HasValue)
                    {
                        _logger.SendLog(LogEventLevel.Warning, guildId, "Census failed to return a full list of outfit members for outfit {OutfitTag} using query {CensusQuery}. Dumping returned JSON as a debug log message", outfitTag, censusQuery);
                        _logger.SendLog(LogEventLevel.Debug, guildId, "Returned Census JSON: {json}", outfitDataJson);
                        break;  //  Break out of do-while, continue with next outfit
                    }
                    resultsInLastQuery = returnedData.Returned.Value;
                    totalResultsCount += resultsInLastQuery;
                    _logger.SendLog(LogEventLevel.Debug, guildId, "Number of results returned for outfit {OutfitTag}: {ResultCount}", outfitTag, totalResultsCount);

                    //  Remove all characters that are still in their original outfit as returned by Census
                    charactersToCheck.RemoveAll(c => playerData.Where(y => y.PlayerName?.Name.FirstLower is not null).Select(y => y.PlayerName!.Name.FirstLower?.ToLower()).Contains(c.CharacterName!.ToLower()));

                    //  If there's still characters left to check and Census returned 5000 characters, query the next 5000 characters (5000 being the maximum number of results returned by outfit_member_extended)
                } while (charactersToCheck.Count != 0 && resultsInLastQuery >= 5000);

                //  All remaining characters are not in their associated outfit anymore, and need to be checked individually
                _logger.SendLog(LogEventLevel.Debug, guildId, "Number of characters that have not been found in Census for outfit {OutfitTag}: {UnaccountedCharacterCount}", outfitTag, charactersToCheck.Count);
                individualCharactersToQuery.AddRange(charactersToCheck.Where(x => !x.CharacterName.IsNullOrEmpty()).Select(x => x.CharacterName!));
            }

            //  Query individual characters
            _logger.SendLog(LogEventLevel.Debug, guildId, "Querying {IndividualUserCount} individual users", individualCharactersToQuery.Count);
            foreach (string characterName in individualCharactersToQuery)
            {
                //  GetPlayerDataLight returns the requested character as the first element, or null if no exact match was found
                if((await NicknameHandler.GetPlayerDataLightAsync(characterName, guildId, _httpClient, _logger, _configuration, 1))?[0] is not PlayerDataLight playerData)
                    continue;

                User targetUser = guild.Users.First(x => x.CharacterName?.ToLower() == characterName.ToLower());
                string? newOutfitLower = playerData.Outfit?.AliasLower;

                //  Only change the nickname if the user actually switched outfits
                if (targetUser.CurrentOutfit?.ToString().ToLower() != newOutfitLower)
                {
                    _logger.SendLog(LogEventLevel.Debug, guildId, "Character {CharacterName} moved from {OldOutfitTag} to {NewOutfitTag}", playerData.Name.First, targetUser.CurrentOutfit?.ToString(), playerData.Outfit?.Alias?.ToString());

                    //  Only attempt to send a message if the guild represents a specific outfit
                    if (!guild.OutfitTag.IsNullOrEmpty())
                    {
                        //  If character joined guild outfit, send "welcome to outfit" message (in welcome channel)
                        if (newOutfitLower == guild.OutfitTag!.ToLower() && guild.Channels.WelcomeChannel is not null)
                        {
                            await _assistantUtils.SendLogChannelMessageAsync(guild.GuildId, $"User <@{targetUser.SocketUserId}> has joined the outfit");
                            
                            //  TODO:   Only send "welcome to outfit" message to user if this is wanted by the admins
                            //if (await _client.GetChannelAsync(guild.Channels.WelcomeChannel.Value) is ITextChannel welcomeChannel)
                            //    await _assistantUtils.SendMessageInChannelAsync(welcomeChannel, $"Welcome, <@{targetUser.SocketUserId}>, to {guild.OutfitTag}!");
                        }
                        //  If character left guild outfit, send "character left outfit" message to admins (in log channel)
                        else if (targetUser.CurrentOutfit?.ToLower() == guild.OutfitTag!.ToLower() && newOutfitLower != guild.OutfitTag!.ToLower())
                            await _assistantUtils.SendLogChannelMessageAsync(guildId, $"User <@{targetUser.SocketUserId}> has left the outfit");

                        //  If character joined unrelated outfit, don't send a message
                    }

                    //  Set guild nickname and save new outfit tag to database
                    targetUser.CurrentOutfit = playerData.Outfit?.Alias;
                    if ((await _client.GetGuild(guildId).GetUsersAsync().FlattenAsync()).First(x => x.Id == targetUser.SocketUserId) is not IGuildUser targetGuildUser)
                        continue;

                    await NicknameHandler.AssignNicknameAndRoleAsync(targetGuildUser, playerData.Outfit?.Alias ?? "", characterName, guild, _logger);
                    await _guildDb.SaveChangesAsync();
                }
                else
                    _logger.SendLog(LogEventLevel.Verbose, guildId, "Character {CharacterName} remains in {OutfitTag}", playerData.Name.First, playerData.Outfit?.Alias ?? "(no outfit)");
            }

            _logger.SendLog(LogEventLevel.Information, guildId, "Finished updating outfit tags");
        }

        /// <summary>
        /// Sorts all registered users in <paramref name="guild"/> by outfit
        /// </summary>
        /// <param name="guild">The database entry of the guild for which to generate the Dictionary</param>
        /// <returns>A <see cref="Dictionary{TKey, TValue}"/> containing a list of registered users grouped by outfit, with the outfit tag as the key ("" if unaffiliated)</returns>
        private Dictionary<string, List<User>> RegisteredUsersByOutfit(Guild guild)
        {
            //  Each key represents an outfit tag ("" for no outfit), corresponding to a list of users with that tag
            Dictionary<string, List<User>> registeredOutfits = guild.Users.GroupBy(x => x.CurrentOutfit ?? "").ToDictionary(x => x.Key, y => y.ToList());
            _logger.SendLog(LogEventLevel.Verbose, guild.GuildId, "Registered players grouped by outfit:\n{GroupedUsers}", JsonConvert.SerializeObject(registeredOutfits, Formatting.Indented));

            return registeredOutfits;
        }
    }
}
