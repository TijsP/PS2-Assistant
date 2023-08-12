using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Templates;
using Serilog.Templates.Themes;

using PS2_Assistant.Data;
using PS2_Assistant.Handlers;
using PS2_Assistant.Logger;

namespace PS2_Assistant;

public class Program
{
    private readonly DiscordSocketClient _botclient = new( new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers });
    private readonly HttpClient _censusclient = new();
    private readonly BotContext _botDatabase = new();
    private readonly IConfiguration appSettings = new ConfigurationBuilder().AddJsonFile("appsettings.json").SetBasePath(Environment.CurrentDirectory).Build();

    public static Task Main() => new Program().MainAsync();

    public async Task MainAsync()
    {
        //  Ensure all required strings are specified
        if (appSettings.GetConnectionString("CensusAPIKey") is null ||
            appSettings.GetConnectionString("DiscordBotToken") is null ||
            appSettings.GetConnectionString("LoggerURL") is null ||
            appSettings.GetConnectionString("LoggerAPIKey") is null ||
            appSettings.GetConnectionString("TestGuildId") is null)
        {
            var exception = new KeyNotFoundException("Missing connection strings in appsettings.json");
            await Console.Out.WriteLineAsync(Newtonsoft.Json.JsonConvert.SerializeObject(exception));
            throw exception;
        }

        //  Setup the logger
        string outputTemplate = "[{@t:yyyy-MM-dd HH:mm:ss} {@l:u3}] [{Source}]{#if GuildId is not null} (Guild: {GuildId}){#end} {@m:lj}\n{@e}";
        var expression = new ExpressionTemplate(outputTemplate, theme: TemplateTheme.Literate);
        ILogger sLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(new JsonFormatter(), "Logs/log.json", rollingInterval: RollingInterval.Day)
            .WriteTo.Seq(appSettings.GetConnectionString("LoggerURL")!, apiKey: appSettings.GetConnectionString("LoggerAPIKey"))
            .WriteTo.Console(expression)
            .CreateLogger();
        SourceLogger _logger = new(sLogger);

        using (LogContext.PushProperty("Source", nameof(MainAsync)))
            sLogger.Information("Bot starting up...");

        //  Setup services collection
        IServiceProvider _services = new ServiceCollection()
            .AddSingleton(appSettings)
            .AddSingleton(_botclient)
            .AddSingleton(_logger)
            .AddSingleton(_censusclient)
            .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>(), new InteractionServiceConfig { LogLevel = LogSeverity.Verbose }))
            .AddSingleton<ClientHandler>()
            .AddSingleton<InteractionHandler>()
            .AddSingleton(_botDatabase)
            .AddSingleton<AssistantUtils>()
            .AddSingleton<CLIHandler>()
            .BuildServiceProvider();


        //  Initialize both interation and bot client handlers
        //  Logging function is added here instead of in the ClientHandler to ensure the logger is setup before any other client methods are called
        _botclient.Log += _services.GetRequiredService<SourceLogger>().SendLogHandler;

        await _services.GetRequiredService<InteractionHandler>().InitializeAsync();
        await _services.GetRequiredService<ClientHandler>().InitializeAsync();

        await _botclient.LoginAsync(TokenType.Bot, appSettings.GetConnectionString("DiscordBotToken"));
        await _botclient.StartAsync();

        //  Setup Census API HTTP client
        _censusclient.DefaultRequestHeaders.Accept.Clear();

        //  Handle command line input
        CancellationTokenSource closeProgramToken = new();
        await _services.GetRequiredService<CLIHandler>().CommandHandlerAsync(closeProgramToken);
        try
        {
            await Task.Delay(-1, closeProgramToken.Token);
        }
        catch
        {
            _logger.SendLog(LogEventLevel.Information, null, "Stopping bot...");
            await _botclient.StopAsync();
        }
    }

}