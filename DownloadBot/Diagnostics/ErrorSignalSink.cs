using Serilog.Core;
using Serilog.Events;

namespace DownloadBot.Diagnostics;

// Wired into Serilog's LoggerConfiguration alongside the Console/File sinks — doesn't write anything
// itself, just watches for Error-level (or worse) events and raises ErrorSignal for
// ErrorLogUploadService to react to.
public sealed class ErrorSignalSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level >= LogEventLevel.Error)
            ErrorSignal.Raise();
    }
}
