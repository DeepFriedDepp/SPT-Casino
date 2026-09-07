using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace Casino.Shared
{
    /// <summary>
    /// The two things the shared drawing code needs from whoever is hosting it.
    ///
    /// These files used to live once per table and reach for that table's own plugin
    /// by name, which is most of why they could not simply be shared. There are only
    /// ever two such reaches: where the art is, and where to log. Both are set once at
    /// startup.
    ///
    /// Deliberately tolerant of never being set. <see cref="Textures"/> touches neither
    /// and is pure arithmetic; the card and chip faces fall back to something drawn
    /// rather than failing, and a null log is a no-op. A shared file that throws
    /// because a host forgot to introduce itself would be worse than the duplication
    /// it replaced.
    /// </summary>
    internal static class Host
    {
        internal static BaseUnityPlugin Plugin;

        internal static ManualLogSource Log;

        /// <summary>
        /// Something the server pushed down, for whichever panel cares.
        ///
        /// The third thing a host provides, and it is here for the same reason as the
        /// other two: a panel that reached for the socket by name could not be compiled
        /// into both assemblies. `PokerPanel.cs` is built into `Poker.Client`, which is
        /// only an editing surface and has no websocket reference at all, AND into
        /// `Casino.Client`, which is what ships and owns the socket. Naming
        /// `CasinoSocketClient` from the panel breaks the first build.
        ///
        /// Reflection was the other way to bridge that, and it worked -- but a seam that
        /// resolves by string survives a rename silently and stops delivering, which for
        /// a live table means everybody quietly stops seeing each other's moves. This is
        /// the same bridge with a compiler behind it.
        ///
        /// Tolerant of never being set, like the rest of this class. In the standalone
        /// plugin nothing ever calls <see cref="Push"/>, so the event simply never
        /// fires and the panel falls back to asking.
        ///
        /// **Raised on whatever thread calls <see cref="Push"/>.** The casino pumps its
        /// socket from Unity's main thread precisely so handlers can touch the UI, so
        /// anything that calls Push from elsewhere breaks every subscriber.
        /// </summary>
        internal static event Action<string> Pushed;

        /// <summary>Hands a pushed message to whoever is listening. A no-op when nobody is.</summary>
        internal static void Push(string message) => Pushed?.Invoke(message);

        /// <summary>
        /// The folder the art sits in, which is the one the plugin was loaded from.
        /// Falls back to the working directory, where it will find nothing and every
        /// caller already copes with that.
        /// </summary>
        internal static string AssetFolder =>
            Path.GetDirectoryName(Plugin?.Info?.Location ?? ".") ?? ".";

        internal static void Warn(string message) => Log?.LogWarning(message);

        internal static void Error(string message) => Log?.LogError(message);
    }
}
