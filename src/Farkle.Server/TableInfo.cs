namespace Farkle.Server;

/// <summary>
/// What this table calls itself, and which version of it this is.
///
/// Not an `IModMetadata`: the casino ships as one mod folder and SPT allows exactly
/// one metadata class in it. See <see cref="Casino.Server.ModMetadata"/>.
/// </summary>
internal static class TableInfo
{
    internal const string Name = "Farkle";

    internal const string Version = "0.1.0";

    /// <summary>
    /// The SPT range the casino targets. See Casino.Server.ModMetadata. This is the
    /// sixth place that says it -- `CLAUDE.md` counted five before this table -- and
    /// moving one means moving all six.
    /// </summary>
    internal const string SptVersion = "~4.0.13";
}
