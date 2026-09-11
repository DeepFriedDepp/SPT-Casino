using Farkle.Game;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace Farkle.Server;

/// <summary>
/// Announces the table on the server console, and only when asked.
///
/// Silent unless its verbose switch is on: `Casino.Server.Startup` prints the one line
/// the casino needs at boot, and five tables each printing a block is somebody's whole
/// console for games they have not opened.
///
/// Nothing is registered here. `Casino.Server.Startup` does not list the tables either
/// -- SPT loads every assembly in the mod folder and registers every `[Injectable]` it
/// finds, so putting `Farkle.Server.dll` beside the others is the whole of the wiring.
/// The work order that asked for a registration line in `Casino.Server/Startup.cs`
/// was describing a mechanism this loader does not have.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 1)]
public class Startup(FarkleLog log) : IOnLoad
{
    public Task OnLoad()
    {
        // FIRST, and ahead of the verbose gate on purpose: without this every one of this
        // table's item events throws inside SPT's JSON layer, before the router is
        // reached. A table whose banner is switched off still has to bind its own bodies.
        FarkleActions.Register();

        if (!log.Verbose)
        {
            return Task.CompletedTask;
        }

        log.Banner($"v{TableInfo.Version} loaded -- built for SPT {TableInfo.SptVersion}");
        log.Banner($"mod folder: {log.ModFolder}");
        log.Banner("routes: POST /farkle/ping, /tables, /open, /join, /leave, /roll, /keep, /bank, /state");
        log.Banner($"item event: {FarkleActions.Sync}, so the stash keeps up without a reload");
        log.Banner(
            $"six dice farkle {Odds.FarkleChance(6):P2} of the time, one die {Odds.FarkleChance(1):P2} -- "
            + "computed rather than measured");

        log.Notice("THIS TABLE PLAYS FOR REAL MONEY. Each player's stake leaves the stash at");
        log.Notice("sit-down and the winner is paid both. Standing up mid-match forfeits yours.");

        log.Banner("verbose logging is ON -- every request will be logged.");
        log.Banner("turn it off in farkle.config.json once things are working.");

        return Task.CompletedTask;
    }
}
