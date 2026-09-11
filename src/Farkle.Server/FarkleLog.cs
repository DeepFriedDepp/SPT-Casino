using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace Farkle.Server;

public record FarkleConfig
{
    /// <summary>
    /// Logs every request. Noisy in normal play; the point of it is the first run on a
    /// new SPT build, where the interesting failures are all in code that has never
    /// executed.
    /// </summary>
    public bool VerboseLogging { get; init; } = false;
}

/// <summary>
/// Every line this table writes goes through here, prefixed so the whole of it can be
/// picked out of a busy server console with one filter on "[Farkle]".
///
/// The same shape as the other four tables' logs, and the fifth copy of it. The
/// server-side duplication is the known, accepted kind -- see `CLAUDE.md`, "Still
/// duplicated, on the server side".
/// </summary>
[Injectable(InjectionType.Singleton)]
public class FarkleLog : IFarkleLog
{
    private const string Prefix = "[Farkle]";

    private readonly ISptLogger<FarkleLog> _logger;

    public FarkleLog(ISptLogger<FarkleLog> logger, ModHelper modHelper, FileUtil fileUtil, JsonUtil jsonUtil)
    {
        _logger = logger;

        // Named ModFolder rather than Path: a property called Path shadows
        // System.IO.Path inside the class and breaks every Path.Combine in it.
        ModFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        var configPath = System.IO.Path.Combine(ModFolder, "farkle.config.json");

        try
        {
            Config = fileUtil.FileExists(configPath)
                ? jsonUtil.Deserialize<FarkleConfig>(fileUtil.ReadFile(configPath)) ?? new FarkleConfig()
                : new FarkleConfig();
        }
        catch (Exception ex)
        {
            // A broken config must never stop the mod loading. That failure looks
            // identical to the mod being rejected by the version gate, which is the
            // one thing this logging exists to tell apart.
            Config = new FarkleConfig();
            _logger.Error($"{Prefix} farkle.config.json is unreadable, using defaults -- {ex.Message}");
        }
    }

    public string ModFolder { get; }

    public FarkleConfig Config { get; }

    public bool Verbose => Config.VerboseLogging;

    public void Success(string message) => _logger.Success($"{Prefix} {message}");

    /// <summary>Something the reader has to see, in orange.</summary>
    public void Notice(string message) =>
        _logger.LogWithColor($"{Prefix} {message}", LogTextColor.Yellow);

    /// <summary>A startup line, in the casino's banner colour, the same as the other four.</summary>
    public void Banner(string message) =>
        _logger.LogWithColor($"{Prefix} {message}", LogTextColor.Cyan);

    public void Info(string message) => _logger.Info($"{Prefix} {message}");

    void IFarkleLog.Error(string message) => Error(message);

    public void Error(string message, Exception? ex = null) =>
        _logger.Error($"{Prefix} {message}{(ex is null ? string.Empty : $" -- {ex}")}");

    /// <summary>Only when verbose logging is on.</summary>
    public void Detail(string message)
    {
        if (Verbose)
        {
            _logger.Info($"{Prefix} {message}");
        }
    }
}
