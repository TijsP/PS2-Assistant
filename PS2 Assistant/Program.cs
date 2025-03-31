using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Coravel;

using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using Serilog;
using Serilog.Context;
using Serilog.Formatting.Json;
using Serilog.Templates;
using Serilog.Templates.Themes;

using PS2_Assistant.Data;
using PS2_Assistant.Handlers;
using PS2_Assistant.Invocables;
using PS2_Assistant.Logger;

namespace PS2_Assistant;

public class Program
{
    private readonly DiscordSocketClient _botclient = new( new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers });
    private readonly HttpClient _censusclient = new();
    private readonly BotContext _botDatabase = new();

    public static Task Main() => new Program().MainAsync();

    public async Task MainAsync()
    {
        //  Start building the generic host
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddJsonFile("appsettings.json").SetBasePath(Environment.CurrentDirectory).Build();

        //  Ensure all required strings are specified
        if (builder.Configuration.GetConnectionString("CensusAPIKey") is null ||
            builder.Configuration.GetConnectionString("DiscordBotToken") is null ||
            builder.Configuration.GetConnectionString("LoggerURL") is null ||
            builder.Configuration.GetConnectionString("LoggerAPIKey") is null ||
            builder.Configuration.GetConnectionString("TestGuildId") is null)
        {
            throw new KeyNotFoundException("Missing connection strings in appsettings.json");
        }

        //  Setup the logger
        string outputTemplate = "[{@t:yyyy-MM-dd HH:mm:ss} {@l:u3}] [{Source}]{#if GuildId is not null} (Guild: {GuildId}){#end} {@m:lj}\n{@e}";
        var expression = new ExpressionTemplate(outputTemplate, theme: TemplateTheme.Literate);
        Serilog.ILogger sLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(new JsonFormatter(), $"{AssistantUtils.logFilePath}/log.json", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 90)
            .WriteTo.Seq(builder.Configuration.GetConnectionString("LoggerURL")!, apiKey: builder.Configuration.GetConnectionString("LoggerAPIKey"))
            .WriteTo.Console(expression)
            .CreateLogger();
        SourceLogger _logger = new(sLogger);

        using (LogContext.PushProperty("Source", nameof(MainAsync)))
            sLogger.Information("Bot starting up...");

        //  Setup services collection
        builder.Services
            .AddSingleton(_botclient)
            .AddSingleton(_botDatabase)
            .AddSingleton(_censusclient)
            .AddSingleton(_logger)
            .AddSingleton<ClientHandler>()
            .AddSingleton<InteractionHandler>()
            .AddSingleton<NicknameHandler>()
            .AddSingleton<OutfitTagHandler>()
            .AddSingleton<AssistantUtils>()
            .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>(), new InteractionServiceConfig { LogLevel = LogSeverity.Verbose }))
            .AddSingleton<ServerMergeTrackerHandler>()
            .AddHostedService<CLIHandler>()
            .AddHostedService(x => x.GetRequiredService<ServerMergeTrackerHandler>())
            .AddScheduler()
            .BuildServiceProvider();

        //  Build the generic host
        IHost host = builder.Build();

        //  Schedule jobs to run at selected intervals
        host.Services.UseScheduler(scheduler =>
        {
            if (DateTime.UtcNow < AssistantUtils.ServerMergeEventEndTime.AddMinutes(10))
            {
                scheduler.ScheduleWithParams<ServerMergeEmbedUpdateInvocable>(
                    //  Dependency injection doesn't seem to work here, so pass them manually instead
                    host.Services.GetRequiredService<HttpClient>(),
                    host.Services.GetRequiredService<ServerMergeTrackerHandler>(),
                    host.Services.GetRequiredService<SourceLogger>(),
                    host.Services.GetRequiredService<IConfiguration>())
                    .EveryMinute()
                        .When(() => new Task<bool>(() => DateTime.UtcNow < AssistantUtils.ServerMergeEventEndTime.AddMinutes(10)));
            }
        });

        //  Initialize both interation and bot client handlers
        //  Logging function is added here instead of in the ClientHandler to ensure the logger is setup before any other client methods are called
        _botclient.Log += host.Services.GetRequiredService<SourceLogger>().SendLogHandler;

        await host.Services.GetRequiredService<InteractionHandler>().InitializeAsync();
        await host.Services.GetRequiredService<ClientHandler>().InitializeAsync();

        await _botclient.LoginAsync(TokenType.Bot, builder.Configuration.GetConnectionString("DiscordBotToken"));
        await _botclient.StartAsync();

        //  Setup Census API HTTP client
        _censusclient.DefaultRequestHeaders.Accept.Clear();

        //  Run the generic host
        await host.RunAsync();
    }

}