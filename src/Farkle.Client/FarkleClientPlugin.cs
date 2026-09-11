using BepInEx;
using BepInEx.Logging;

namespace Farkle.Client
{
    /// <summary>
    /// What the panel reaches for by name: somewhere to log, and a MonoBehaviour to
    /// start coroutines on.
    ///
    /// This file exists so `Farkle.Client` builds on its own, as an editing surface.
    /// It is NOT compiled into `Casino.Client` -- that project has its own stand-in for
    /// this class in `Shims.cs`, set from `CasinoPlugin.Awake`, and lists only the
    /// panel and the API in its `<Compile Include>` lines. Two definitions would be a
    /// duplicate type.
    ///
    /// Unlike the three older tables' plugin classes this was never a real
    /// `BaseUnityPlugin`: Farkle arrived after the casino had one door, so there was
    /// never a Farkle tab, a Farkle escape patch or a Farkle GUID to retire. Nothing
    /// here is shippable and `scripts/casino/pack.ps1` never looks at this project.
    /// </summary>
    internal static class FarkleClientPlugin
    {
        internal static BaseUnityPlugin Instance;

        internal static ManualLogSource Log;
    }
}
