namespace Casino.Server;

/// <summary>
/// The line the casino prints when the server starts, a letter at a time.
///
/// ## Why this does not go through the logger
///
/// `ISptLogger.LogWithColor` takes one colour for a whole line, which is as far as it
/// goes -- and embedding markup in the message does not work either, because
/// `ConsoleLogHandler.GetColorizedText` runs `Markup.Escape` over it first and the
/// tags would print as text. A letter at a time means writing to the console directly.
///
/// ## Why it does not go through Spectre either, any more
///
/// It used to, and on SPT 4.1.x that was free: the server ships Spectre.Console and a
/// mod could just use it. **SPT 4.0.13 ships no Spectre assembly at all** -- there is
/// not one anywhere under the install. Adding a `PackageReference` fixes only the
/// compile: `Casino.Server.csproj` sets `CopyLocalLockFileAssemblies=false`, and every
/// pack script copies an explicit allowlist of `*.Server.dll` / `*.Game.dll` / `pdb` /
/// `config.json`, so the DLL would be neither copied next to the mod nor packaged into
/// the zip. `Startup.OnLoad` calls straight into here, so the first thing a 4.0.13
/// server would have done on boot is throw `FileNotFoundException` -- and a mod that
/// cannot load is a much worse trade than a banner that cannot do 256 colours.
///
/// So the cycle is <see cref="ConsoleColor"/> now. Eight of the sixteen the console
/// has always had, no dependency, and it renders in a plain `conhost` window that has
/// never heard of a VT escape sequence. Writing raw ANSI was the other candidate and
/// is worse in exactly the place it matters: where virtual-terminal processing is off,
/// escape codes print as literal garbage, which is a louder failure than the flat text
/// this degrades to.
///
/// ## What that costs, and why it is affordable
///
/// This line does not reach `spt*.log`. That would matter -- the version is the first
/// thing worth knowing when somebody reports a problem -- except SPT already writes it
/// there itself:
///
///     Mod: SPT Casino version: 1.1.0 (GUID: com.mybutthasarash.sptcasino | ...) loaded
///
/// So the file keeps the fact and the console gets the flourish. Debug level was the
/// other candidate for keeping a plain copy in the file, and it is not one: the log
/// holds Information, Warning and Critical and no Debug lines at all, so a line logged
/// there would appear nowhere.
/// </summary>
public static class Banner
{
    /// <summary>
    /// The rainbow, as close to the old Spectre colour names as sixteen colours reach:
    /// red, orange1, yellow, green, aqua, dodgerblue1, purple, magenta1.
    /// </summary>
    private static readonly ConsoleColor[] Cycle =
    [
        ConsoleColor.Red,
        ConsoleColor.DarkYellow,
        ConsoleColor.Yellow,
        ConsoleColor.Green,
        ConsoleColor.Cyan,
        ConsoleColor.Blue,
        ConsoleColor.DarkMagenta,
        ConsoleColor.Magenta,
    ];

    /// <summary>
    /// Writes one line, cycling a colour per visible character.
    ///
    /// Spaces are passed through uncoloured: colouring them shifts every letter after
    /// them along the cycle for no visible gain, and it makes the rainbow drift out of
    /// step between one line and the next.
    /// </summary>
    public static void Rainbow(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        ConsoleColor original;

        try
        {
            original = Console.ForegroundColor;
        }
        catch
        {
            // No console to colour -- redirected, or none attached at all.
            Console.WriteLine(text);
            return;
        }

        var step = 0;

        try
        {
            foreach (var character in text)
            {
                if (char.IsWhiteSpace(character))
                {
                    Console.Write(character);
                    continue;
                }

                Console.ForegroundColor = Cycle[step++ % Cycle.Length];
                Console.Write(character);
            }

            Console.WriteLine();
        }
        catch
        {
            // Half a line may already be out; finish it plainly rather than leave it
            // hanging. A console that will not take colour is not a reason to fail a
            // mod load.
            Console.WriteLine();
            Console.WriteLine(text);
        }
        finally
        {
            try
            {
                Console.ForegroundColor = original;
            }
            catch
            {
                // Nothing sensible left to do, and the mod is loaded either way.
            }
        }
    }
}
