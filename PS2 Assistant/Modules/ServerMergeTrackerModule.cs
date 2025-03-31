using Discord;
using Discord.Interactions;

using Microsoft.Extensions.Configuration;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using PS2_Assistant.Handlers;
using PS2_Assistant.Logger;
using PS2_Assistant.Models.Census.API;
using PS2_Assistant.Models.Census.WebSocket;

namespace PS2_Assistant.Modules
{
    public class ServerMergeTrackerModule : InteractionModuleBase<SocketInteractionContext>
    {
        public enum MergingServers { ConneryAndEmerald, Miller }

        public static readonly Dictionary<ulong, string> OutfitTagCache = new();            //  Cache storing outfit Ids and outfit tags
        private static Dictionary<IUserMessage, MergingServers> trackingEmbeds = new();     //  Keep track of all embeds send by the bot (not synced with the DB, so resets upon bot restart)
        public static Dictionary<IUserMessage, MergingServers> TrackingEmbeds { get => trackingEmbeds; private set => trackingEmbeds = value; }

        private readonly ServerMergeTrackerHandler _trackerHandler;
        private readonly HttpClient _httpClient;
        private readonly SourceLogger _logger;
        private readonly IConfiguration _configuration;


        public ServerMergeTrackerModule(ServerMergeTrackerHandler trackerHandler, HttpClient httpClient, SourceLogger logger, IConfiguration configuration)
        {
            _trackerHandler = trackerHandler;
            _httpClient = httpClient;
            _logger = logger;
            _configuration = configuration;
        }

        [EnabledInDm(false)]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("show-server-merge-tracker", "Posts an automatically updating embed showing merge progress")]
        public async Task ShowMergeTracker(
            [Summary(name: "Server", description: "Which server(s) to show merge progress for")]
            MergingServers serverOfInterest)
        {
            await DeferAsync(ephemeral: true);

            Embed trackingEmbed = await GetTrackerEmbed(serverOfInterest, _configuration.GetConnectionString("CensusAPIKey")!, _httpClient, _trackerHandler, _logger);

            IUserMessage trackingEmbedMessage = await Context.Channel.SendMessageAsync(embed: trackingEmbed);
            //  Add message to the dictionary in order to keep track of which embeds should be updated when more data is available

            //  Before adding message to dictionary, check if an embed targeting the in-game server already exists in this channel
            IUserMessage? messageToRemoveFromDict = null;
            foreach(IUserMessage message in TrackingEmbeds.Keys)
            {
                //  Don't care about embeds in unrelated channels
                if(message.Channel != Context.Channel) continue;
                else if (message.Channel == Context.Channel && TrackingEmbeds[message] == serverOfInterest)
                {
                    //  Embed targeting this in-game server already exists in this channel.
                    //  Edit old message to refer to the new embed and remove it from the dictionary
                    messageToRemoveFromDict = message;
                    await Context.Channel.ModifyMessageAsync(message.Id, x => {
                        x.Embed = null;
                        x.Content = $"Another embed was requested:\nhttps://discordapp.com/channels/{Context.Guild.Id}/{Context.Channel.Id}/{trackingEmbedMessage.Id}";
                        });
                }
            }
            if(messageToRemoveFromDict is not null)
                TrackingEmbeds.Remove(messageToRemoveFromDict);

            TrackingEmbeds.Add(trackingEmbedMessage, serverOfInterest);

            await FollowupAsync("Sending tracking embed...", ephemeral: true);
        }

        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("update-merge-tracker-embeds", "Manually trigger the process of updating server merge tracker embeds")]
        public async Task ManuallyUpdateMergeTrackingEmbeds()
        {
            await DeferAsync();
            await UpdateEmbeds(_configuration.GetConnectionString("CensusAPIKey")!, _httpClient, _trackerHandler, _logger);      //  The existance of the API key was validated on startup
            await FollowupAsync("All embeds updated");
        }

        /// <summary>
        /// Update all embeds send by the bot
        /// </summary>
        /// <param name="censusAPIKey">The private API key for accessing the Census API</param>
        /// <param name="httpClient">The <seealso cref="HttpClient"/> to use for connecting to Census</param>
        /// <param name="trackerHandler">The <seealso cref="ServerMergeTrackerHandler"/> in charge of gathering and providing alert win and base capture data</param>
        /// <param name="logger">The <seealso cref="SourceLogger"/> to use</param>
        public static async Task UpdateEmbeds(string censusAPIKey, HttpClient httpClient, ServerMergeTrackerHandler trackerHandler, SourceLogger logger)
        {
            //  Embeds only need to be generated once for all messages
            Embed millerEmbed = await GetTrackerEmbed(MergingServers.Miller, censusAPIKey, httpClient, trackerHandler, logger);
            Embed conneryAndEmeraldEmbed = await GetTrackerEmbed(MergingServers.ConneryAndEmerald, censusAPIKey, httpClient, trackerHandler, logger);

            List<IUserMessage> deletedMessages = new ();
            foreach (KeyValuePair<IUserMessage, MergingServers> pair in TrackingEmbeds)
            {
                //  Try to update the message, and mark the message as deleted if updating fails
                try
                {
                    await pair.Key.ModifyAsync(x => x.Embed = pair.Value switch { MergingServers.Miller => millerEmbed, MergingServers.ConneryAndEmerald => conneryAndEmeraldEmbed, _ => millerEmbed });
                }catch (Exception ex)
                {
                    if(await pair.Key.Channel.GetMessageAsync(pair.Key.Id) is null)
                        deletedMessages.Add(pair.Key);
                    logger.SendLog(Serilog.Events.LogEventLevel.Warning, null, "Failed to update an embed. Most likely, it was deleted", exep: ex);
                }
            }

            //  Lists can't be modified while being iterated over, so do it here instead
            foreach(IUserMessage message in deletedMessages)
                TrackingEmbeds.Remove(message);
        }

        /// <summary>
        /// Generates an embed containing alert win and base capture info by faction and, in the latter case, outfit
        /// </summary>
        /// <param name="serverOfInterest">The server for which to generate the embed</param>
        /// <param name="censusAPIKey">The private API key for accessing the Census API</param>
        /// <param name="httpClient">The <seealso cref="HttpClient"/> to use for connecting to Census</param>
        /// <param name="trackerHandler">The <seealso cref="ServerMergeTrackerHandler"/> in charge of gathering and providing alert win and base capture data</param>
        /// <param name="logger">The <seealso cref="SourceLogger"/> to use</param>
        /// <returns>The tracker embed</returns>
        private static async Task<Embed> GetTrackerEmbed(MergingServers serverOfInterest, string censusAPIKey, HttpClient httpClient, ServerMergeTrackerHandler trackerHandler, SourceLogger logger)
        {
            Dictionary<int, List<FacilityControlEvent>> baseCaptureAggregateDictionary;
            Dictionary<int, List<MetagameEvent>> alertWinAggregateDictionary;
            if (serverOfInterest == MergingServers.Miller)
            {
                //  Initialize with values from Miller
                baseCaptureAggregateDictionary = trackerHandler.FacilityCaptures[10];
                alertWinAggregateDictionary = trackerHandler.MetagameEvents[10];
            }
            else if (serverOfInterest == MergingServers.ConneryAndEmerald)
            {
                //  Initialize with values from Connery
                baseCaptureAggregateDictionary = new()
                {
                    //  Append values from Emerald. Don't use AddRange, as this modifies the original list
                    { 1, trackerHandler.FacilityCaptures[1][1].Concat(trackerHandler.FacilityCaptures[17][1]).ToList() },
                    { 2, trackerHandler.FacilityCaptures[1][2].Concat(trackerHandler.FacilityCaptures[17][2]).ToList() },
                    { 3, trackerHandler.FacilityCaptures[1][3].Concat(trackerHandler.FacilityCaptures[17][3]).ToList() }
                };
                alertWinAggregateDictionary = new()
                {
                    //  Append values from Emerald. Don't use AddRange, as this modifies the original list
                    { 1, trackerHandler.MetagameEvents[1][1].Concat(trackerHandler.MetagameEvents[17][1]).ToList() },
                    { 2, trackerHandler.MetagameEvents[1][2].Concat(trackerHandler.MetagameEvents[17][2]).ToList() },
                    { 3, trackerHandler.MetagameEvents[1][3].Concat(trackerHandler.MetagameEvents[17][3]).ToList() }
                };
            }
            else
            {
                //  Initialize with empty lists
                baseCaptureAggregateDictionary = new()
                {
                    { 1, new() },
                    { 2, new() },
                    { 3, new() }
                };
                alertWinAggregateDictionary = new()
                {
                    { 1, new() },
                    { 2, new() },
                    { 3, new() }
                };

                logger.SendLog(Serilog.Events.LogEventLevel.Error, null, "Invalid value for ServerOfInterest! Continuing with empty dictionaries");
            }

            int totalBaseCaptures = baseCaptureAggregateDictionary.Select(x => x.Value.Count).Sum();
            int totalAlerts = alertWinAggregateDictionary.Select(x => x.Value.Count).Sum();

            List<KeyValuePair<ulong, List<FacilityControlEvent>>> topVsContributors = baseCaptureAggregateDictionary[1]
                .GroupBy(x => x.OutfitId)
                .ToDictionary(pair => pair.Key, pair => pair.ToList())
                .OrderBy(y => -y.Value.Count)
                .ToList();
            topVsContributors.RemoveAll(x => x.Key == 0);
            List<KeyValuePair<ulong, List<FacilityControlEvent>>> topNcContributors = baseCaptureAggregateDictionary[2]
                .GroupBy(x => x.OutfitId)
                .ToDictionary(pair => pair.Key, pair => pair.ToList())
                .OrderBy(y => -y.Value.Count)
                .ToList();
            topNcContributors.RemoveAll(x => x.Key == 0);
            List<KeyValuePair<ulong, List<FacilityControlEvent>>> topTrContributors = baseCaptureAggregateDictionary[3]
                .GroupBy(x => x.OutfitId)
                .ToDictionary(pair => pair.Key, pair => pair.ToList())
                .OrderBy(y => -y.Value.Count)
                .ToList();
            topTrContributors.RemoveAll(x => x.Key == 0);

            (int r, int g, int b) embedColour = (247, 82, 37);
            string inTheLead = "";
            var winOrder = alertWinAggregateDictionary.OrderBy(x => -x.Value.Count).ToList();       //  Sort factions by alert wins (descending)
            if (winOrder[0].Value.Count == winOrder[1].Value.Count)
            {
                //  All factions tied
                if (winOrder[1].Value.Count == winOrder[2].Value.Count)
                    inTheLead = "All factions are currently even!";
                //  First and second place tied
                else
                    inTheLead = $"{FactionIdToShorthand(winOrder[0].Key)} and {FactionIdToShorthand(winOrder[1].Key)} are tied for first place!";
            }
            else
            {
                string serverName = "";
                //  Set embed colour and new server name
                switch (serverOfInterest)
                {
                    case MergingServers.Miller:
                        switch (winOrder[0].Key)
                        {
                            case 1:
                                embedColour = (69, 34, 178);
                                serverName = "Erebus";
                                break;
                            case 2:
                                embedColour = (0, 56, 169);
                                serverName = "Excavion";
                                break;
                            case 3:
                                embedColour = (201, 0, 0);
                                serverName = "Wainwright";
                                break;
                            default:
                                serverName = "Unknown";
                                break;
                        }
                        break;
                    case MergingServers.ConneryAndEmerald:
                        switch (winOrder[0].Key)
                        {
                            case 1:
                                embedColour = (69, 34, 178);
                                serverName = "Helios";
                                break;
                            case 2:
                                embedColour = (0, 56, 169);
                                serverName = "Osprey";
                                break;
                            case 3:
                                embedColour = (201, 0, 0);
                                serverName = "LithCorp";
                                break;
                            default:
                                serverName = "Unknown";
                                break;
                        }
                        break;
                }
                if (DateTime.UtcNow < AssistantUtils.ServerMergeEventEndTime)
                    inTheLead = $"{FactionIdToShorthand(winOrder[0].Key)} is in the lead! The server might be named {serverName}...";
                else
                    inTheLead = $"The Server Naming Event has concluded! Thanks to all those who fought for {FactionIdToShorthand(winOrder[0].Key)}, the server will be named {serverName}!";
            }

            EmbedBuilder trackingEmbed = new EmbedBuilder()
                .WithColor(embedColour.r, embedColour.g, embedColour.b)
                .WithTitle("Server Merge Tracker")
                .WithDescription($"Current progress for {serverOfInterest switch { MergingServers.ConneryAndEmerald => "Connery and Emerald", MergingServers.Miller => "Miller", _ => "unknown server" }}:\n{inTheLead}\nㅤ")
                .AddField("Total Alerts won:", $"{totalAlerts}\n\nBy faction:", inline: false)
                .AddField("VS:", alertWinAggregateDictionary[1].Count.ToString(), inline: true)
                .AddField("NC:", alertWinAggregateDictionary[2].Count.ToString(), inline: true)
                .AddField("TR:", $"{alertWinAggregateDictionary[3].Count}\nㅤ", inline: true)     //  Invisible character (U+3164) after newline
                .AddField("Base Captures", $"**VS**: {baseCaptureAggregateDictionary[1].Count}\n**NC**: {baseCaptureAggregateDictionary[2].Count}\n**TR**: {baseCaptureAggregateDictionary[3].Count}", inline: true)
                .AddField("Total:", totalBaseCaptures.ToString(), inline: true)
                .AddField("Top Outfits", "By faction:", inline: false)
                .AddField("VS", await GetTopContributorsText(topVsContributors, censusAPIKey, httpClient, logger), inline: true)
                .AddField("NC", await GetTopContributorsText(topNcContributors, censusAPIKey, httpClient, logger), inline: true)
                .AddField("TR", await GetTopContributorsText(topTrContributors, censusAPIKey, httpClient, logger), inline: true)
                .WithFooter("Base capture statistics provided purely as trivia: only alert wins count\nUpdated:")
                .WithCurrentTimestamp();

                return trackingEmbed.Build();
        }

        /// <summary>
        /// Gets the abbreviation of the outfit's name
        /// </summary>
        /// <param name="factionId">The Id of the faction for which to get the shorthand</param>
        /// <returns>"VS", "NC", or "TR"</returns>
        private static string FactionIdToShorthand(int factionId)
        {
            return factionId switch { 1 => "VS", 2 => "NC", 3 => "TR", _ => "??" };
        }

        /// <summary>
        /// Get the top three (if available, less otherwise) outfits by base captures
        /// </summary>
        /// <param name="sortedTopContributorsList">A list of outfits sorted by their number of base captures</param>
        /// <param name="censusAPIKey">The private API key for accessing the Census API</param>
        /// <param name="httpClient">The <seealso cref="HttpClient"/> to use for connecting to Census</param>
        /// <param name="logger">The <seealso cref="SourceLogger"/> to use</param>
        /// <returns>A string with the top three outfits. If there are no outfits in the list, only returns "**1.** "</returns>
        private static async Task<string> GetTopContributorsText(List<KeyValuePair<ulong, List<FacilityControlEvent>>> sortedTopContributorsList, string censusAPIKey, HttpClient httpClient, SourceLogger logger)
        {
            string contributorText = "**1.** ";
            if (sortedTopContributorsList.Count > 0)
                contributorText += $"{await GetOutfitTag(sortedTopContributorsList[0].Key, censusAPIKey, httpClient, logger)} [{sortedTopContributorsList[0].Value.Count}]\n";
            if (sortedTopContributorsList.Count > 1)
                contributorText += $"**2.** {await GetOutfitTag(sortedTopContributorsList[1].Key, censusAPIKey, httpClient, logger)} [{sortedTopContributorsList[1].Value.Count}]\n";
            if (sortedTopContributorsList.Count > 2)
                contributorText += $"**3.** {await GetOutfitTag(sortedTopContributorsList[2].Key, censusAPIKey, httpClient, logger)} [{sortedTopContributorsList[2].Value.Count}]";

            return contributorText;
        }

        /// <summary>
        /// Takes an outfit Id and returns the outfit tag. First looks at the bot's internal outfit tag cache. If the outfit tag is not in the cache, the Census API is queried and the result is stored in the cache
        /// </summary>
        /// <param name="outfitId">The Census outfit Id</param>
        /// <param name="censusAPIKey">The private API key for accessing the Census API</param>
        /// <param name="httpClient">The <seealso cref="HttpClient"/> to use for connecting to Census</param>
        /// <param name="logger">The <seealso cref="SourceLogger"/> to use</param>
        /// <returns>The outfit tag of the outfit. Format: XXXX if the tag was found, (xxx) if the outfit has a name but no tag, "No outfit" if outfit tag is 0, or "empty" if the outfit tag couldn't be found (most likely due to an error with Census)</returns>
        private static async Task<string> GetOutfitTag(ulong outfitId, string censusAPIKey, HttpClient httpClient, SourceLogger logger)
        {
            if(OutfitTagCache.TryGetValue(outfitId, out string? value))
                return value;

            if (outfitId == 0) {
                OutfitTagCache.Add(0, "No outfit");
                return OutfitTagCache[0];
            }

            JsonSerializer serializer = new() { ContractResolver = new DefaultContractResolver() { NamingStrategy = new SnakeCaseNamingStrategy() } };
            string censusQuery = $"http://census.daybreakgames.com/s:{censusAPIKey}/get/ps2:v2/{OutfitNameQuery.CollectionQuery}&outfit_id={outfitId}";
            string response = await httpClient.GetStringAsync(censusQuery);
            JObject returnedData = JObject.Parse(response);
            //  Simply use outfit tag if possible
            if (returnedData.SelectToken("outfit_member_extended_list[0].alias") is JToken aliasToken && aliasToken.ToObject<string>(serializer) is string outfitTag && !string.IsNullOrEmpty(outfitTag))
            {
                OutfitTagCache.Add(outfitId, outfitTag);
                return outfitTag;
            }
            //  Try and compose a custom outfit tag from the outfit name, if the outfit doesn't have an outfit tag
            else if (returnedData.SelectToken("outfit_member_extended_list[0].name") is JToken nameToken && nameToken.ToObject<string>(serializer) is string outfitName && !string.IsNullOrEmpty(outfitName))
            {
                //  If an outfit has a name but no tag, use the first letters of the first three words (if available)
                string makeshiftOutfitTag = "(";
                int secondWordIndex = outfitName.IndexOf(" ");

                //  Would be a shame to have the bot crash mid-event over an OutOfBounds exception, and I can't be bothered to check all the edge cases
                try
                {
                    //  If the outfit name only has one or two words, only look for one or two letters
                    if (secondWordIndex == -1)
                        return $"({outfitName[0..(outfitName.Length < 3 ? 1 : 3)].ToUpper()})";
                    int thirdWordIndex = outfitName.IndexOf(" ", secondWordIndex + 1);
                    if (thirdWordIndex == -1)
                        return $"({outfitName[0..(outfitName.Length < 3 ? 2 : 3)].ToUpper()})";

                    makeshiftOutfitTag += outfitName.First().ToString();
                    makeshiftOutfitTag += outfitName[secondWordIndex + 1];
                    makeshiftOutfitTag += outfitName[secondWordIndex + 2];
                }
                catch (Exception ex)
                {
                    logger.SendLog(Serilog.Events.LogEventLevel.Error, null, "Something went wrong when trying to compose a custom outfit tag for outfit {OutfitName} ({OutfitId})", outfitName, outfitId, exep: ex);
                    return "empty";
                }

                return $"({makeshiftOutfitTag.ToUpper()})";
            }
            //  If, somehow, the outfit has no tag and no name
            else
                return "empty";
        }
    }
}
