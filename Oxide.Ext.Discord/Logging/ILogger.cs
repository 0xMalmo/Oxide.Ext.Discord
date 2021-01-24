using System;

namespace Oxide.Ext.Discord.Logging
{
    public interface ILogger
    {
        void Verbose(string message);
        void Debug(string message);
        void Info(string message);
        void Warning(string message);
        void Error(string message);
        void Exception(string message, Exception ex);
        void UpdateLogLevel(LogLevel level);
    }
}