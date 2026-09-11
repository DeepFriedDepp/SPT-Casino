using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.UI;
using SPT.Reflection.Utils;

namespace Casino.Shared
{
    /// <summary>
    /// Tells the game to pick up the money the server has just moved.
    ///
    /// Ported from Blackjack, where it was written after the money path was already
    /// working and the symptom was still there. The table talks to the server over its
    /// own routes, and currency moved that way lands in the profile but leaves the
    /// running game none the wiser: the stash on screen still shows roubles that have
    /// already gone.
    ///
    /// **That is not only a display fault.** The client goes on believing in stacks the
    /// server has deleted, so the next time the player drags one in their stash the
    /// game sends an operation naming an item that is no longer there, and the server
    /// answers
    ///
    ///     Unable to merge stacks as destination item: ... cannot be found
    ///
    /// SPT holds the profile changes it has made for a session until the client's next
    /// item event, and hands them back on that reply. So the fix is not to re-send the
    /// money -- it has already moved, correctly -- but to give the client a reason to
    /// ask. This sends an item event that does nothing at all, purely so the reply
    /// carries the changes the game then applies to its own inventory.
    ///
    /// Deliberately not a rewrite of how the buy-in is paid. The money path works and
    /// is covered by tests on both transports; what was missing was the client being
    /// told.
    /// </summary>
    internal static class ProfileSync
    {
        /// <summary>
        /// The event body. A public field named exactly as the server reads it: SPT
        /// matches item-event actions case-sensitively, and this is the shape EFT's own
        /// operations take, so the game's serialiser writes it unchanged.
        ///
        /// The action name is the one thing that genuinely differed between the three
        /// copies of this file, so it is the one thing passed in. Each must stay in
        /// step with its own server's `...Actions.Sync`.
        /// </summary>
        private sealed class SyncOperation
        {
            public SyncOperation(string action) => Action = action;

            public string Action;

            public override string ToString() => Action;
        }

        /// <summary>
        /// Asks the game to collect whatever the server has been holding for it.
        ///
        /// Safe to call when nothing has changed -- an empty set of changes applies as
        /// nothing -- so callers do not have to work out whether money moved.
        /// </summary>
        internal static void Request(string action)
        {
            try
            {
                // `ItemUiContext` is built by the inventory screens, not by the menu: walk
                // from the task bar straight into the casino without opening a stash first
                // and there is no instance. This used to give up here and say nothing --
                // deliberately, to avoid a log line every frame.
                //
                // **The cost of that silence was the whole bug it was hiding.** The money
                // had already moved on the server, but nothing ever asked the client to
                // collect the changes, so the stash on screen never budged and the table
                // looked like it was playing for nothing. No warning said so.
                //
                // The application always has a session while a profile is loaded, and it
                // is the same object the inventory screens hand back.
                var session = OrMainApp(ItemUiContext.Instance?.ClientSession);

                if (session == null)
                {
                    WarnOnce(
                        "[Casino] there is no session to sync against, so the stash on screen "
                        + "will read stale until the game reloads. The money itself has moved.");

                    return;
                }

                session.SendOperationRightNow(new SyncOperation(action), new Callback(OnSynced));
            }
            catch (Exception error)
            {
                // Never let this take the table down with it. The money has already
                // moved; the worst case without it is a stash that reads stale until
                // the game reloads, which is exactly where this started.
                Host.Error($"[Casino] could not ask the game to resync: {error}");
            }
        }

        /// <summary>
        /// The session the inventory screens had, or the running application's if they
        /// were never opened.
        ///
        /// ## Why this is generic, and why it never names the session type
        ///
        /// Backported from upstream, where the equivalent is declared as returning
        /// `IClientSession`. **That type does not exist on SPT 4.0.13's EFT**, so the
        /// upstream file does not compile here at all -- which is the ordinary shape of
        /// every backport in this repo and not a fault in theirs.
        ///
        /// Taking the current session as an argument means the compiler infers what it is
        /// from the property it came from, so the type is never written down. That is
        /// worth more than dodging one version difference: upstream's own notes record
        /// `GetClientBackEndSession` returning a class the obfuscator had renamed to a
        /// private-use glyph, which put a typeref in their assembly that only resolved
        /// against the exact `Assembly-CSharp.dll` it was built on --
        ///
        ///     TypeLoadException: Could not resolve type with token 01000068 from typeref
        ///
        /// -- and they reached for reflection to keep the name out. Inferring it keeps out
        /// the name they still had to write at the boundary as well.
        ///
        /// `TarkovApplication` is a real name and survives obfuscation; only the method's
        /// return type had to be described rather than named. `GetMethod` walks base
        /// types, which is where the method is actually declared.
        /// </summary>
        private static T OrMainApp<T>(T current)
            where T : class
        {
            if (current != null)
            {
                return current;
            }

            try
            {
                var app = ClientAppUtils.GetMainApp();

                // Unity's ==, not a raw null check: a torn-down application is not null
                // to one. See CLAUDE.md on Comfort's Singleton.
                if (app == null)
                {
                    return null;
                }

                return typeof(TarkovApplication)
                    .GetMethod("GetClientBackEndSession", BindingFlags.Public | BindingFlags.Instance)
                    ?.Invoke(app, null) as T;
            }
            catch (Exception error)
            {
                // A missing method on a game build we were not compiled against is a
                // stale stash, not a crash. The money has already moved.
                Host.Error($"[Casino] could not reach the application's session: {error}");
                return null;
            }
        }

        private static bool _warned;

        /// <summary>
        /// Says it the first time and then stops. Once per session is a bug report; once
        /// per spin is a reason to stop reading the log.
        /// </summary>
        private static void WarnOnce(string message)
        {
            if (_warned)
            {
                return;
            }

            _warned = true;
            Host.Warn(message);
        }

        private static void OnSynced(IResult result)
        {
            if (result != null && result.Failed)
            {
                Host.Warn(
                    $"[Casino] the game refused the resync: {result.Error}. "
                    + "The stash may read stale until it reloads.");
            }
        }
    }
}
