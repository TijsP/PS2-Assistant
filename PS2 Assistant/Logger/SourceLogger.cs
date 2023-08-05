using Discord;
using Serilog.Context;
using Serilog.Events;
using Serilog;
using System.Runtime.CompilerServices;

namespace PS2_Assistant.Logger
{
    public class SourceLogger
    {
        //  Uses the Serilog.ILogger, NOT Microsoft.Extensions.Logging.ILogger
        private readonly ILogger _logger;

        public SourceLogger(ILogger logger)
        {
            _logger = logger;
        }

        public Task SendLogHandler(LogMessage message)
        {
            SendLog(message);
            return Task.CompletedTask;
        }

        public void SendLog(LogMessage message)
        {
            var severity = message.Severity switch
            {
                LogSeverity.Critical => LogEventLevel.Fatal,
                LogSeverity.Error => LogEventLevel.Error,
                LogSeverity.Warning => LogEventLevel.Warning,
                LogSeverity.Info => LogEventLevel.Information,
                LogSeverity.Verbose => LogEventLevel.Verbose,
                LogSeverity.Debug => LogEventLevel.Debug,
                _ => LogEventLevel.Information
            };
            using (LogContext.PushProperty("Source", message.Source))
                _logger.Write(severity, message.Exception, "{Message}", message.Message);
        }

        public void SendLog(LogEventLevel level, ulong? guildId, string template, Exception? exep = null, [CallerMemberName] string caller = "")
        {
            using (LogContext.PushProperty("Source", caller))
            using (LogContext.PushProperty("GuildId", guildId))
                _logger.Write(level, exep, template);
        }
        public void SendLog<T>(LogEventLevel level, ulong guildId, string template, T prop, Exception? exep = null, [CallerMemberName] string caller = "")
        {
            using (LogContext.PushProperty("Source", caller))
            using (LogContext.PushProperty("GuildId", guildId))
                _logger.Write(level, exep, template, prop);
        }
        public void SendLog<T0, T1>(LogEventLevel level, ulong guildId, string template, T0 prop0, T1 prop1, Exception? exep = null, [CallerMemberName] string caller = "")
        {
            using (LogContext.PushProperty("Source", caller))
            using (LogContext.PushProperty("GuildId", guildId))
                _logger.Write(level, exep, template, prop0, prop1);
        }
        public void SendLog<T0, T1, T2>(LogEventLevel level, ulong guildId, string template, T0 prop0, T1 prop1, T2 prop2, Exception? exep = null, [CallerMemberName] string caller = "")
        {
            using (LogContext.PushProperty("Source", caller))
            using (LogContext.PushProperty("GuildId", guildId))
                _logger.Write(level, exep, template, prop0, prop1, prop2);
        }
    }
}
