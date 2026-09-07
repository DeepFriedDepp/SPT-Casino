<#
.SYNOPSIS
    Sits TWO players at one shared table on a running SPT server, with no game client.

.DESCRIPTION
    This is how the shared-table work gets proved on a real server before anybody tries
    it with two copies of the game open. It drives two profiles through the whole thing
    -- open, list, join, deal, act, leave -- and checks the two things that could each
    be silently wrong:

      * THE MONEY. Two buy-ins leave two stashes and two stacks come back. Reported per
        player, before and after.
      * THE PRIVACY. Each player's own view is fetched and searched for the OTHER
        player's hole cards. Finding them is a hard failure and the script says so
        loudly, because that is the defect this whole design exists to prevent and it
        is invisible from inside the game.

    THIS STAKES REAL CURRENCY on BOTH profiles. Use -DryRun to prove the routes are
    reachable without sitting anybody down.

    What it does NOT cover: the websocket. This talks to the HTTP routes only, so it
    exercises the table, the seats, the money and the per-seat views -- but nothing
    pushes to a PowerShell script. A player's view here is what they get when they ask,
    which is exactly what the panel falls back to anyway. The socket needs two game
    clients and is the one part that cannot be proved from here.

.PARAMETER SessionId
    The first profile. Find it in the filename under SPT\user\profiles\.

.PARAMETER FriendSessionId
    The second profile. Must be a DIFFERENT profile -- the point is two people.

.EXAMPLE
    .\shared-smoke.ps1 -SessionId 66e4... -FriendSessionId 66e5... -DryRun

.EXAMPLE
    .\shared-smoke.ps1 -SessionId 66e4... -FriendSessionId 66e5... -Hands 2
#>
param(
    [Parameter(Mandatory = $true)][string]$SessionId,
    [Parameter(Mandatory = $true)][string]$FriendSessionId,
    [string]$Server = "https://127.0.0.1:6969",
    [int]$Seats = 4,
    [int]$BuyIn = 1000000,
    [int]$BigBlind = 20000,
    [int]$Hands = 2,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

if ($SessionId -eq $FriendSessionId) {
    Write-Host "Both ids are the same profile. The point of this script is two people." -ForegroundColor Red
    exit 1
}

# Self-signed certificate on loopback. See scripts/poker/smoke.ps1 for why this reads
# as "the underlying connection was closed" rather than anything about certificates.
if ($Server.StartsWith("https:")) {
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
}

# Opting out of both halves of SPT's zlib framing. Without these a plain JSON body dies
# inside Inflater complaining about an unsupported compression method.
$headers = @{
    "Content-Type"       = "application/json"
    "requestcompressed"  = "0"
    "responsecompressed" = "0"
}

$serverUri = [Uri]$Server

function New-Player {
    param([string]$Id, [string]$Label)

    # The session travels as a PHPSESSID cookie and cannot go through -Headers:
    # "Cookie" is restricted and PowerShell drops it SILENTLY, so the request arrives
    # with no session at all and the server answers "session id provided was empty".
    $ws = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $ws.Cookies.Add((New-Object System.Net.Cookie("PHPSESSID", $Id, "/", $serverUri.Host)))

    return [pscustomobject]@{ Id = $Id; Label = $Label; Session = $ws }
}

function Invoke-Poker {
    param($Player, [string]$Route, [hashtable]$Body = @{})

    $json = $Body | ConvertTo-Json -Compress

    try {
        return Invoke-RestMethod -Uri "$Server$Route" -Method Post -Headers $headers `
            -Body $json -WebSession $Player.Session
    }
    catch {
        Write-Host "  $($Player.Label): $Route failed -- $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  A 404 means the mod did not load, or these routes are not in the build" -ForegroundColor DarkGray
        Write-Host "  that is installed. Check the server console for a [Casino] banner." -ForegroundColor DarkGray
        exit 1
    }
}

function Show-Money {
    param($Player, [string]$When)

    $ping = Invoke-Poker $Player "/poker/ping"
    $roubles = $ping.balances.Roubles
    Write-Host ("  {0,-8} {1,-8} {2,14:N0} roubles" -f $Player.Label, $When, $roubles)
    return [int64]$roubles
}

$alice = New-Player $SessionId "ALICE"
$bob = New-Player $FriendSessionId "BOB"

Write-Host "Shared poker smoke test -> $Server" -ForegroundColor Cyan
Write-Host ""

# ------------------------------------------------------------------- reachability

foreach ($player in @($alice, $bob)) {
    $ping = Invoke-Poker $player "/poker/ping"

    if (-not $ping.hasProfile) {
        Write-Host "  $($player.Label): no profile for that session id." -ForegroundColor Red
        Write-Host "  A blank id in the server log means the cookie did not resolve." -ForegroundColor Yellow
        exit 1
    }

    Write-Host "  $($player.Label) profile found, mod $($ping.modVersion)" -ForegroundColor Green
}

$tables = Invoke-Poker $alice "/poker/shared/tables"
Write-Host "  shared routes reachable -- $($tables.tables.Count) table(s) open" -ForegroundColor Green

if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run. Nothing was staked." -ForegroundColor Yellow
    exit 0
}

# ------------------------------------------------------------------------- money

Write-Host ""
Write-Host "Before:" -ForegroundColor Cyan
$aliceBefore = Show-Money $alice "before"
$bobBefore = Show-Money $bob "before"

# -------------------------------------------------------------------- sit down

Write-Host ""
Write-Host "Sitting down..." -ForegroundColor Cyan

$opened = Invoke-Poker $alice "/poker/shared/open" @{
    Seats = $Seats; BuyIn = $BuyIn; BigBlind = $BigBlind; Wallet = "Roubles"
}

if (-not $opened.ok) {
    Write-Host "  ALICE could not open a table: $($opened.error)" -ForegroundColor Red
    exit 1
}

$listed = Invoke-Poker $bob "/poker/shared/tables"
$table = $listed.tables | Select-Object -First 1

if (-not $table) {
    Write-Host "  BOB cannot see the table ALICE just opened. That is the bug." -ForegroundColor Red
    exit 1
}

Write-Host "  table $($table.id): $($table.people) of $($table.seats) seats taken" -ForegroundColor Green

$joined = Invoke-Poker $bob "/poker/shared/join" @{ Table = $table.id }

if (-not $joined.ok) {
    Write-Host "  BOB could not join: $($joined.error)" -ForegroundColor Red
    Invoke-Poker $alice "/poker/shared/leave" | Out-Null
    exit 1
}

Write-Host "  BOB joined. Two people at one table." -ForegroundColor Green

# ---------------------------------------------------------------------- the hands

$leak = $false

for ($hand = 1; $hand -le $Hands; $hand++) {
    Write-Host ""
    Write-Host "Hand $hand" -ForegroundColor Cyan

    $dealt = Invoke-Poker $alice "/poker/shared/deal"

    if (-not $dealt.ok) {
        Write-Host "  deal refused: $($dealt.error)" -ForegroundColor Yellow
        break
    }

    # THE PRIVACY CHECK. Each player's own view, searched for the other's hole cards.
    # Compared as raw JSON rather than by walking the object, because what matters is
    # whether the codes are ON THE WIRE at all -- a field the client is told to ignore
    # is still a field the client received.
    $aliceView = (Invoke-Poker $alice "/poker/shared/state") | ConvertTo-Json -Depth 12 -Compress
    $bobView = (Invoke-Poker $bob "/poker/shared/state") | ConvertTo-Json -Depth 12 -Compress

    $aliceCards = ([regex]::Matches($aliceView, '"([2-9TJQKA][CDHS])"') | ForEach-Object { $_.Groups[1].Value })
    $bobCards = ([regex]::Matches($bobView, '"([2-9TJQKA][CDHS])"') | ForEach-Object { $_.Groups[1].Value })

    $shared = $aliceCards | Where-Object { $bobCards -contains $_ }

    # The board is legitimately in both views. Only a card in both BEFORE any community
    # card is turned would be a hole-card leak, so this checks on the deal, when the
    # board is empty.
    $board = $dealt.table.community

    if (($board -eq $null -or $board.Count -eq 0) -and $shared.Count -gt 0) {
        Write-Host "  *** PRIVACY FAILURE: $($shared -join ', ') appear in BOTH views ***" -ForegroundColor Red
        $leak = $true
    }
    else {
        Write-Host "  views are separate -- no shared card codes before the flop" -ForegroundColor Green
    }

    # Play it out. Check or call whoever is asked, up to a sane bound.
    for ($step = 0; $step -lt 40; $step++) {
        $aState = Invoke-Poker $alice "/poker/shared/state"
        $bState = Invoke-Poker $bob "/poker/shared/state"

        $turn = $null
        if ($aState.table.awaitingPlayer) { $turn = $alice }
        elseif ($bState.table.awaitingPlayer) { $turn = $bob }

        if (-not $turn) { break }

        $view = if ($turn -eq $alice) { $aState } else { $bState }
        $moves = $view.table.options.moves

        $move = if ($moves -contains "Check") { "Check" } elseif ($moves -contains "Call") { "Call" } else { "Fold" }

        $acted = Invoke-Poker $turn "/poker/shared/act" @{ Move = $move; To = 0 }

        if (-not $acted.ok) {
            Write-Host "  $($turn.Label) $move refused: $($acted.error)" -ForegroundColor Yellow
            break
        }
    }

    Write-Host "  hand played out" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------- stand up

Write-Host ""
Write-Host "Standing up..." -ForegroundColor Cyan

$bobLeft = Invoke-Poker $bob "/poker/shared/leave"
Write-Host "  BOB:   $($bobLeft.note)"

$aliceLeft = Invoke-Poker $alice "/poker/shared/leave"
Write-Host "  ALICE: $($aliceLeft.note)"

Write-Host ""
Write-Host "After:" -ForegroundColor Cyan
$aliceAfter = Show-Money $alice "after"
$bobAfter = Show-Money $bob "after"

# ------------------------------------------------------------------------ verdict

Write-Host ""
$aliceNet = $aliceAfter - $aliceBefore
$bobNet = $bobAfter - $bobBefore
$together = $aliceNet + $bobNet

Write-Host ("  ALICE net {0,14:N0}" -f $aliceNet)
Write-Host ("  BOB   net {0,14:N0}" -f $bobNet)
Write-Host ("  together  {0,14:N0}" -f $together)
Write-Host ""

# The two of them together can still be down -- the bots at the table are real
# opponents and take real pots. What must NOT happen is currency appearing from
# nowhere, so a large positive total is the number worth staring at.
if ($together -gt 0) {
    Write-Host "  The two of them are UP overall. That can happen honestly (they won off" -ForegroundColor Yellow
    Write-Host "  the bots) but it is also what minting money looks like. Worth a second run." -ForegroundColor Yellow
}

if ($leak) {
    Write-Host "  PRIVACY FAILED -- hole cards were visible across seats. Do not ship this." -ForegroundColor Red
    exit 1
}

Write-Host "  Two people sat at one table, played, and were paid out separately." -ForegroundColor Green
Write-Host "  The websocket was NOT exercised -- that needs two game clients." -ForegroundColor DarkGray
