namespace DownloadBot.Diagnostics;

// A tiny static bridge between Serilog's own pipeline (configured in Program.cs before the DI
// container exists) and ErrorLogUploadService (a normal hosted service created afterward) — an
// Error-level (or worse) log event anywhere in the app raises this, with no direct dependency
// either way.
public static class ErrorSignal
{
    public static event Action? ErrorLogged;

    public static void Raise() => ErrorLogged?.Invoke();
}
