using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Casino.Server;

/// <summary>
/// The one piece of metadata for the whole casino, and the reason all four server
/// assemblies can live in a single folder.
///
/// ## Why there is exactly one
///
/// SPT loads a mod folder by taking **every** .dll in it -- `ModLoader.LoadMod` calls
/// `DirectoryInfo.GetFiles()`, filters on the extension and loads each one into a
/// single `SptMod.Assemblies` -- and `RegisterSptServicesAsync` then walks that whole
/// list, so every `[Injectable]` in every assembly is registered. Four assemblies from
/// one folder is not a trick; it is what the loader already does.
///
/// What it will not tolerate is two of these. `ModLoader.LoadModMetadata` runs
/// `SingleOrDefault` over the types implementing `IModMetadata` and throws
/// "Duplicate mod metadata found for mod at path" the moment it sees a second. That is
/// the whole constraint: **one folder, one metadata, as many assemblies as you like.**
///
/// So Blackjack, Poker and Roulette no longer carry one each. They keep their own
/// version numbers, in `TableInfo`, because those describe the table rather than the
/// download.
/// </summary>
public record ModMetadata : AbstractModMetadata
{
    /// <summary>
    /// The same GUID the client plugin declares through <c>[BepInPlugin]</c>. Both
    /// halves now agree, which they did not while the server was three mods.
    /// </summary>
    public override string ModGuid { get; init; } = "com.mybutthasarash.sptcasino";

    public override string Name { get; init; } = "SPT Casino";

    public override string Author { get; init; } = "JoelHauser";

    public override List<string>? Contributors { get; init; }

    public override SemanticVersioning.Version Version { get; init; } = new("1.1.0");

    /// <summary>
    /// Targets SPT 4.0.13. "~4.0.13" is >=4.0.13 &lt;4.1.0.
    ///
    /// **A hard gate, and a loud one.** The note that used to live here said a mod
    /// outside the range "loads nothing and logs nothing", so silence at startup meant
    /// this line. That was wrong twice over, and it was checked against 4.0.13's own
    /// `ModValidator`:
    ///
    /// - The gate that fires first is not this one. `ValidateCoreAssemblyReference`
    ///   runs at `ModValidator.cs:24`, well before the semver check at line 136, and it
    ///   compares the `SPTarkov.Server.Core` version a mod was *compiled* against with
    ///   the running server. Build against 4.1.2 and drop it on a 4.0.13 server and it
    ///   dies there -- "requires a minimum SPT version of `4.1.2`, but you are running
    ///   `4.0.13`" -- having never reached this property.
    /// - Neither failure is quiet, and neither is contained. The throw is unhandled and
    ///   it takes **every** server mod down with it, not just this one.
    ///
    /// So silence at startup is not this line. A wall of red is.
    /// </summary>
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.13");

    public override List<string>? Incompatibilities { get; init; }

    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }

    public override string? Url { get; init; } = "https://github.com/JoelHauser/SPT-Casino";

    public override string License { get; init; } = "MIT";

    public override bool? IsBundleMod { get; init; } = false;

}
