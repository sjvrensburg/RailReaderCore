namespace RailReader.Core;

/// <summary>
/// Static gateway for RailReader logging. Set <see cref="Logger"/> once at
/// application startup; all RailReader libraries delegate to this instance.
/// Defaults to <see cref="NullLogger.Instance"/> so libraries work without
/// initialization.
/// </summary>
public static class RailReaderLogging
{
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// The logger instance used by all RailReader libraries. Set this once at
    /// startup before using any RailReader services.
    /// </summary>
    public static ILogger Logger
    {
        get => _logger;
        set => _logger = value ?? NullLogger.Instance;
    }
}
