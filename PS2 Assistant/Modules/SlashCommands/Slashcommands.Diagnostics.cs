using Discord;
using Discord.Interactions;

using PS2_Assistant.Logger;

namespace PS2_Assistant.Modules.SlashCommands
{
    public partial class Slashcommands
    {
        [DontAutoRegister]
        [EnabledInDm(false)]
        [Group("diagnostics", "returns various types of diagnostic data")]
        public class Diagnostics : InteractionModuleBase<SocketInteractionContext>
        {
            private readonly SourceLogger _logger;

            public Diagnostics(SourceLogger logger)
            {
                _logger = logger;
            }

            [SlashCommand("send-log-file", "Returns a log file")]
            public async Task SendLogFile(
                [Summary(description: "The date at which the log file was created")]
                DateTime logDate,
                [Summary(description: "When specified, returns all log files created between log-date and last-log-date")]
                DateTime? lastLogDate = null)
            {
                //  Ensure logDate is the earliest of the two
                if (lastLogDate is not null && logDate.Date > lastLogDate?.Date)
                    (logDate, lastLogDate) = (lastLogDate.Value, logDate);

                await RespondAsync($"Retrieving log file(s) for {logDate:yyyy-MM-dd}{(lastLogDate is null ? "" : " - " + lastLogDate?.ToString("yyyy-MM-dd"))}...");
                _logger.SendLog(Serilog.Events.LogEventLevel.Information, Context.Guild.Id, "Log files requested by user {UserId} for date range {FirstDate} - {SecondDate}", Context.User.Id, logDate.ToString("yyyy-MM-dd"), lastLogDate?.ToString("yyyy-MM-dd"));

                //  Find all logs in the log file directory
                List<FileAttachment> requestedLogFiles = new();
                FileInfo[] logFiles = new DirectoryInfo($"{AssistantUtils.logFilePath}").GetFiles();
                foreach ( FileInfo file in logFiles )
                {
                    DateTime creationDate = file.CreationTime.Date;

                    //  Only add the log file if it was created at the specified date or withing the specified range
                    if (creationDate != logDate.Date)
                    {
                        if (lastLogDate is not null)
                        {
                            if (!(creationDate >= logDate.Date && creationDate <= lastLogDate?.Date))
                                continue;
                        }
                        else
                            continue;
                    }

                    //  Only try to send the file if it doen't exceed the file limit for the server
                    if ((ulong)file.Length > Context.Guild.MaxUploadLimit)
                        await FollowupAsync($"Can't send file {file.Name} in this server! The file size is {file.Length}, while the upload limit for this server is {Context.Guild.MaxUploadLimit} bytes");
                    else
                    {
                        _logger.SendLog(Serilog.Events.LogEventLevel.Debug, Context.Guild.Id, "Sending log file {LogFileName}", file.Name);

                        try
                        {
                            FileStream fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            Stream streamReader = new StreamReader(fileStream, encoding: System.Text.Encoding.Default).BaseStream;
                            requestedLogFiles.Add(new(streamReader, file.Name));
                        }
                        catch (Exception ex)
                        {
                            await FollowupAsync($"Failed with the following execption: `{ex.Message}`\nWith stacktrace: ```{ex.StackTrace}```");
                        }
                    }
                }

                //  Send all log files that were found for the specified date
                if (requestedLogFiles.Count > 0)
                    await FollowupWithFilesAsync(requestedLogFiles);
                else
                    await FollowupAsync($"No log files found for that {(lastLogDate is null ? "date" : "range of dates")}");
            }
        }
    }
}
