using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace Casino.Server;

/// <summary>
/// The casino's one line at startup.
///
/// It used to be three, one per table, because it used to be three mods. It is one
/// mod, it installs into one folder and it registers under one GUID, so it says so
/// once. Three blocks introducing three tables was a mod author's view of the thing
/// printed at somebody who has not opened it yet.
///
/// The tables still have their own `Startup`, and each is silent unless its verbose
/// switch is on -- see any of them. Turn one on and its block comes back, underneath
/// this.
///
/// Ordered ahead of them on purpose: `PostSptModLoader` against their
/// `PostSptModLoader + 1`, so the headline is above the detail rather than buried in
/// the middle of it. (It was `PostLoad` and `PostLoad + 1` on 4.1.x. That constant does
/// not exist in 4.0.13, whose `OnLoadOrder` is renamed and renumbered throughout -- of
/// the eight names the two versions share, only `Watermark` still means the same
/// number. Only the relative order matters here, and it is preserved.)
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostSptModLoader)]
public class Startup : IOnLoad
{
    public Task OnLoad()
    {
        var metadata = new ModMetadata();

        Banner.Rainbow($"[Casino] v{metadata.Version}");

        return Task.CompletedTask;
    }
}
