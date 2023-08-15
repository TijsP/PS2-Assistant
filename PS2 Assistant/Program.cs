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
    private readonly IConfiguration _appSettings = new ConfigurationBuilder().AddJsonFile("appsettings.json").SetBasePath(Environment.CurrentDirectory).Build();

    public static Task Main() => new Program().MainAsync();

    public async Task MainAsync()
    {
        //  Ensure all required strings are specified
        if (_appSettings.GetConnectionString("CensusAPIKey") is null ||
            _appSettings.GetConnectionString("DiscordBotToken") is null ||
            _appSettings.GetConnectionString("LoggerURL") is null ||
            _appSettings.GetConnectionString("LoggerAPIKey") is null ||
            _appSettings.GetConnectionString("TestGuildId") is null)
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
            .WriteTo.Seq(_appSettings.GetConnectionString("LoggerURL")!, apiKey: _appSettings.GetConnectionString("LoggerAPIKey"))
            .WriteTo.Console(expression)
            .CreateLogger();
        SourceLogger _logger = new(sLogger);

        using (LogContext.PushProperty("Source", nameof(MainAsync)))
            sLogger.Information("Bot starting up...");

        //  Setup services collection
        IServiceProvider _services = new ServiceCollection()
            .AddSingleton(_appSettings)
            .AddSingleton(_botclient)
            .AddSingleton(_botDatabase)
            .AddSingleton(_censusclient)
            .AddSingleton(_logger)
            .AddSingleton<ClientHandler>()
            .AddSingleton<CLIHandler>()
            .AddSingleton<InteractionHandler>()
            .AddSingleton<NicknameHandler>()
            .AddSingleton<AssistantUtils>()
            .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>(), new InteractionServiceConfig { LogLevel = LogSeverity.Verbose }))
            .BuildServiceProvider();


        //  Initialize both interation and bot client handlers
        //  Logging function is added here instead of in the ClientHandler to ensure the logger is setup before any other client methods are called
        _botclient.Log += _services.GetRequiredService<SourceLogger>().SendLogHandler;

        await _services.GetRequiredService<InteractionHandler>().InitializeAsync();
        await _services.GetRequiredService<ClientHandler>().InitializeAsync();

        await _botclient.LoginAsync(TokenType.Bot, _appSettings.GetConnectionString("DiscordBotToken"));
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