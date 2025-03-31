using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using PS2_Assistant.Logger;
using PS2_Assistant.Models.Census.WebSocket;

using System.Net.WebSockets;
using System.Text;

namespace PS2_Assistant.Handlers
{
    public class ServerMergeTrackerHandler : BackgroundService
    {
        private readonly SourceLogger _logger;
        private readonly IConfiguration _configuration;
        private readonly Uri _censusSocketUri;

        private const string _socketDumpFilePath = "MergeTracker";
        private const string _socketDumpFileName = "SocketDump.json";
        private readonly byte[] _lineEnd = Encoding.ASCII.GetBytes("\n");
        private readonly byte[] _requestFacilityControlEvents = Encoding.ASCII.GetBytes($"{{\r\n\t\"service\":\"event\",\r\n\t\"action\":\"subscribe\",\r\n\t\"worlds\":[\"all\"],\r\n\t\"eventNames\":[\"FacilityControl\",\"MetagameEvent\"]\r\n}}");
        //  Alert Ids taken from https://github.com/ps2alerts/constants/blob/main/metagameEventType.ts
        private readonly int[] _alertMetagameIds =
        {
            // VS Triggered
            148,
            154,
            157,
            151,
            224,

            // NC Triggered
            149,
            155,
            158,
            152,
            222,

            // TR Triggered
            147,
            153,
            156,
            150,
            223,

            // Current Generation Unstable Meltdowns
            179,
            177,
            178,
            176,
            248,

            189,
            187,
            188,
            186,
            249,

            193,
            191,
            192,
            190,
            250,

            // High pop alerts
            211,
            212,
            213,
            214,
            226,
        };
        private readonly int[] _suddenDeathMetagameIds =
        {
            236,
            237,
            238,
            239,
            240,
            241,
            260
        };
        private int _lastWinningFactionId = 0;

        public Dictionary<int, Dictionary<int, List<FacilityControlEvent>>> FacilityCaptures { get; private set; }      //  First key indicates world, second key indicates the faction that captured the base
        public Dictionary<int, Dictionary<int, List<MetagameEvent>>> MetagameEvents { get; private set; }               //  First key indicates world, second key indicates the faction that captured the base
        private readonly Dictionary<int, int> _ignoreCapturesUntil;     //  First key indicates world, second key is the timestamp for until when captures should be ignored
        private const int _ignoreCapturesDuration = 2;                  //  For how long captures should be ignored after an alert ends

        public ServerMergeTrackerHandler(SourceLogger logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _censusSocketUri = new($"wss://push.planetside2.com/streaming?environment=ps2&service-id=s:{_configuration.GetConnectionString("CensusAPIKey")}");

            FacilityCaptures = new()
            {
                //  All servers instantiated with lists for all three factions
                {  1, new() { { 1, new() }, { 2, new() }, { 3, new() } } },     //  Connery
                { 10, new() { { 1, new() }, { 2, new() }, { 3, new() } } },     //  Miller
                { 17, new() { { 1, new() }, { 2, new() }, { 3, new() } } },     //  Emerald
                { 19, new() { { 1, new() }, { 2, new() }, { 3, new() } } },     //  Jaeger      Added for simplicity
                { 40, new() { { 1, new() }, { 2, new() }, { 3, new() } } }      //  Soltech     Added for simplicity
            };
            MetagameEvents = new()
            {
                //  All servers instantiated with lists for all three factions
                {  1, new() { { 1, new() }, { 2, new() }, { 3, new() } } },     //  Connery
                { 10, new() { 
                    { 1, new() {
                        new(148, 138, 46f, 15f, 36f, 25f, 1743234700, 4, 10, "MetagameEvent") } },
                    { 2, new() {
                    } },
                    { 3, new() {
                        new(239, 138, 1055f, 1699f, 1823f, 0f, 1743285305, 0, 10, "MetagameEvent"),
                        new(155, 138, 38f, 42f, 17f, 25f, 1743290413, 0, 10, "MetagameEvent"),
                        new(156, 138, 35f, 41, 20f, 25f, 1743304239, 0, 10, "MetagameEvent"),
                        new(156, 138, 35f, 41, 20f, 25f, 1743304239, 0, 10, "MetagameEvent"),
                        new(150, 138, 23f, 45f, 30f, 25f, 1743325600, 0, 10, "MetagameEvent")
                    } }
                } },     //  Miller. Some events were missed due to an error with the census websocket, so these are added back in code using data from PS2Alerts.com
                { 17, new() { { 1, new() }, { 2, new() {
                    new(148, 138, 10f, 90f, 10f, 25, 1743234700, 0, 10, "MetagameEvent"),
                    new(148, 138, 10f, 90f, 10f, 25, 1743234700, 0, 10, "MetagameEvent"),
                    new(148, 138, 10f, 90f, 10f, 25, 1743234700, 0, 10, "MetagameEvent"),
                    new(148, 138, 10f, 90f, 10f, 25, 1743234700, 0, 10, "MetagameEvent"),
                    new(148, 138, 10f, 90f, 10f, 25, 1743234700, 0, 10, "MetagameEvent"),
                    new(148, 138, 10f, 90f, 10f, 25, 1743234700, 0, 10, "MetagameEvent"),
                    new(148, 138, 10f, 90f, 10f, 25, 1743234700, 0, 10, "MetagameEvent"),
                    new(148, 138, 10f, 90f, 10f, 25, 1743234700, 0, 10, "MetagameEvent"),
                    new(148, 138, 10f, 90f, 10f, 25, 1743234700, 0, 10, "MetagameEvent")
                } }, { 3, new() } } },     //  Emerald. Like Miller, some events on Connery&Emerald were missed.
                { 19, new() { { 1, new() }, { 2, new() }, { 3, new() } } },     //  Jaeger      Added for simplicity
                { 40, new() { { 1, new() }, { 2, new() }, { 3, new() } } }      //  Soltech     Added for simplicity
            };

            _ignoreCapturesUntil = new()
            {
                {  1, 0 },
                { 10, 0 },
                { 17, 0 },
                { 19, 0 },
                { 40, 0 }
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //  Statistics to be recorded during the event:
            //      - total number of bases captured
            //      - number of bases capture by each faction
            //      - number of bases capture by each outfit

            //  Statistics to display during event:
            //      - total number of bases captured
            //      - top contributing outfits per faction, including the number of bases they captured
            //      - relatice comparison of number of bases captured by factions (using a simple bar graph)
            //      - server name currently in the lead, including margin (difference between number of bases captured by that faction vs. the next closest)

            //  Look for pre-existing event file, and load all events if it exists (in case of reboot during the event)
            Directory.CreateDirectory(_socketDumpFilePath);
            FileStream fileStream;
            if (File.Exists($"{_socketDumpFilePath}/{_socketDumpFileName}"))
            {
                fileStream = new(GetRelativeSocketDumpPath(), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                StreamReader streamReader = new (fileStream, Encoding.UTF8, true, 4096);
                LoadFile(streamReader, stoppingToken);
            }
            else
            {
                fileStream = new(GetRelativeSocketDumpPath(), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                await fileStream.WriteAsync(Encoding.ASCII.GetBytes("\n"), default);
            }

            //  Only collect new data while the event is running
            if (DateTime.UtcNow < AssistantUtils.ServerMergeEventEndTime) {
                //  Setup websocket
                int reconnectAttemptCounter = 0;        //  TODO:   Reset when a succesful connection was made
                using ClientWebSocket censusSocket = new();
                await censusSocket.ConnectAsync(_censusSocketUri, stoppingToken);
                await censusSocket.SendAsync(_requestFacilityControlEvents, WebSocketMessageType.Text, true, default);

                while (!stoppingToken.IsCancellationRequested && DateTime.UtcNow < AssistantUtils.ServerMergeEventEndTime)
                {
                    //  Receive events & save all incoming events to file
                    byte[] buffer = new byte[1024];
                    string receivedMessage = "";

                    try
                    {
                        WebSocketReceiveResult receiveResult = await censusSocket.ReceiveAsync(buffer, stoppingToken);
                        receivedMessage = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);

                        await fileStream.WriteAsync(buffer.AsMemory(0, receiveResult.Count), default);
                        await fileStream.WriteAsync(_lineEnd, default);
                        await fileStream.FlushAsync(default);      //  Maybe less efficient then doing it at the end of the method, but there have been issues with impartial writes
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.SendLog(Serilog.Events.LogEventLevel.Warning, null, "An error occured while receiving data from the Census websocket", exep: ex);
                        if (reconnectAttemptCounter == 13)
                        {
                            await Console.Out.WriteLineAsync("Failed to reconnect, aborting...");
                            break;
                        }
                        await Console.Out.WriteLineAsync($"Trying to reconnect in {5 * (reconnectAttemptCounter + 1)} seconds (assuming disconnected), attempt {reconnectAttemptCounter + 1}");
                        if (censusSocket.State != WebSocketState.Open)
                        {
                            reconnectAttemptCounter++;
                            await Task.Delay(5000 * reconnectAttemptCounter, stoppingToken);

                            await censusSocket.ConnectAsync(_censusSocketUri, default);
                            await censusSocket.SendAsync(_requestFacilityControlEvents, WebSocketMessageType.Text, true, default);
                        }
                    }

                    ParseText(receivedMessage);

                    //  Sort event types:
                    //  If heartbeat, ignore/record for statistics
                    //  If unknown, ignore (throw log error + maybe a notification on admin Discord server?)
                    //  If facility capture, add to statistics
                }

                //  Close websocket
                if (censusSocket.State == WebSocketState.Open)
                    await censusSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Cancellation requested", default);
            }

            //  Close and dispose of the FileStream
            await fileStream.FlushAsync(CancellationToken.None);
            fileStream.Dispose();
        }

        /// <summary>
        /// Takes a JSON object as a string, parses it and adds the value to the relevant collection, if applicable
        /// </summary>
        /// <param name="text">The JSON object to parse</param>
        private void ParseText(string text)
        {
            JsonSerializer serializer = new() {
                ContractResolver = new DefaultContractResolver() {
                    NamingStrategy = new SnakeCaseNamingStrategy()
                }
            };

            JObject? serviceMessage = JsonConvert.DeserializeObject<JObject>(text);
            if (serviceMessage?.SelectToken("type")?.ToObject<string>() is not string messageType)
                return;

            switch (messageType)
            {
                case "serviceMessage":
                    //  Record facility statistics
                    string? payloadEventName = serviceMessage?.SelectToken("payload.event_name")?.ToObject<string>();
                    if (payloadEventName == "FacilityControl")
                    {
                        if (serviceMessage?.SelectToken("payload")?.ToObject<FacilityControlEvent>(serializer) is not FacilityControlEvent facilityControlEvent)
                            break;

                        //Validate event first
                        if (facilityControlEvent.OldFactionId == 0 ||
                            facilityControlEvent.NewFactionId == 0 ||
                            facilityControlEvent.DurationHeld == 0 ||
                            facilityControlEvent.DurationHeld == facilityControlEvent.Timestamp)
                        {
                            //  When one bogus event is fired, others are more likely to follow
                            _ignoreCapturesUntil[facilityControlEvent.WorldId] = facilityControlEvent.Timestamp + _ignoreCapturesDuration;
                            break;
                        }
                        //  A flurry of FacilityCapturedEvents is fired off after an alert ends (as at that point, a new continent opens up). This protects against some of the false positives that are harder to detect
                        else if (facilityControlEvent.Timestamp < _ignoreCapturesUntil[facilityControlEvent.WorldId])
                            break;
                        //  Constitutes a succesful defence?
                        else if (facilityControlEvent.NewFactionId == facilityControlEvent.OldFactionId)
                            break;
                        //  Timestamps being midnight Friday 28 until midnight Sunday 30, in PT
                        else if (facilityControlEvent.Timestamp < 1743145200 || facilityControlEvent.Timestamp > 1743400800)
                            break;

                        //  If valid, add event to the dictionary
                        FacilityCaptures[facilityControlEvent.WorldId][facilityControlEvent.NewFactionId].Add(facilityControlEvent);
                    }
                    else if (payloadEventName == "MetagameEvent")
                    {
                        if (serviceMessage?.SelectToken("payload")?.ToObject<MetagameEvent>(serializer) is not MetagameEvent metagameEvent)
                            break;

                        //  Validate event first
                        bool isValidAlertMetagameEvent = false;
                        bool isValidSuddenDeathMetagameEvent = false;
                        foreach (int validAlertMetagameId in _alertMetagameIds)
                            if (metagameEvent.MetagameEventId == validAlertMetagameId)
                                isValidAlertMetagameEvent = true;
                        foreach (int validSuddenDeathMetagameId in _suddenDeathMetagameIds)
                            if (metagameEvent.MetagameEventId == validSuddenDeathMetagameId)
                                isValidSuddenDeathMetagameEvent = true;

                        if (!isValidAlertMetagameEvent && !isValidSuddenDeathMetagameEvent)
                            break;
                        else if (metagameEvent.Timestamp < 1743145200 || metagameEvent.Timestamp > 1743400800)  //  Timestamps being midnight Friday 28 until midnight Sunday 30, in PT
                            break;

                        //  If a Sudden Death alert was started, we know the previous alert "win" wasn't actually a win, and that it should be removed from the cache
                        if (isValidSuddenDeathMetagameEvent && metagameEvent.MetagameEventState == 135)     //  135 is the MetagameEventState for a starting (Sudden Death) event
                            //  Remove last alert win entry from MetagameEvents
                            MetagameEvents[metagameEvent.WorldId][_lastWinningFactionId].RemoveAt(MetagameEvents[metagameEvent.WorldId][_lastWinningFactionId].Count - 1);
                        //  If the event is Sudden Death and it ended, we can treat it like a normal event win so we don't need to cover that separately

                        //  Don't continue if the metagame event hasn't finished yet
                        if (metagameEvent.MetagameEventState != 138)    //  138 is the MetagameEventState for a finished event
                            break;

                        int winningFactionId;
                        if (metagameEvent.FactionVs > metagameEvent.FactionNc)  // Checking if num1 is greater than num2
                        {
                            if (metagameEvent.FactionVs > metagameEvent.FactionTr)  // Checking if num1 is greater than num3
                                winningFactionId = 1;
                            else
                                winningFactionId = 3;
                        }
                        else if (metagameEvent.FactionNc > metagameEvent.FactionTr)  // Checking if num2 is greater than num3
                            winningFactionId = 2;
                        else
                            winningFactionId = 3;

                        MetagameEvents[metagameEvent.WorldId][winningFactionId].Add(metagameEvent);
                        _lastWinningFactionId = winningFactionId;
                        _ignoreCapturesUntil[metagameEvent.WorldId] = metagameEvent.Timestamp + _ignoreCapturesDuration;
                    }

                    break;
                case "heartbeat":
                    //  Record heartbeat statistics
                    break;
                //  Includes serviceStateChanged and connectionStateChanged, for now
                default:
                    //  Send log warning with object text for inspection
                    break;
            }
        }

        /// <summary>
        /// Parses the entire Census Websocket file dump and uses it to initialize the relevant collections
        /// </summary>
        /// <param name="streamReader">The <seealso cref="StreamReader"/> to use for reading file dump</param>
        /// <param name="cancellationToken">The <seealso cref="CancellationToken"/> indicating whether a cancellation was requested</param>
        private void LoadFile(StreamReader streamReader, CancellationToken cancellationToken)
        {
            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = streamReader.ReadLine()) != null)
            {
                ParseText(line);
            }
        }

        /// <summary>
        /// Get the relative file path to the Census Websocket file dump, including file name
        /// </summary>
        /// <returns></returns>
        public static string GetRelativeSocketDumpPath()
        {
            return $"{_socketDumpFilePath}/{_socketDumpFileName}";
        }
    }
}
