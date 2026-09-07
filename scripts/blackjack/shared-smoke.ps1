<#
.SYNOPSIS
    Sits TWO players at one shared blackjack table on a running SPT server, no game client.

.DESCRIPTION
    This is how shared blackjack gets proved on a real server before anybody tries it with
    two copies of the game open. It drives two profiles through the whole thing -- open,
    list, join, bet, deal, act, leave -- and checks the things that could each be silently
    wrong:

      * THE MONEY. Each stake leaves its OWN stash and each return goes back to its OWN
        profile. Reported per player, before and after, with the engine's own per-seat
        figures beside them. A settlement that crossed the two would show here as one
        player up by the other's stake.
      * THE NAMES. Every box must carry its owner's PMC nickname. The engine's fallback for
        an unnamed occupied seat is the word "You", which is a relationship rather than a
        name -- it reaches everybody unchanged, so each player sees their friend labelled
        as themselves. That shipped in poker and somebody hit it at a live table.
      * THE SHARING. Both players' cards must be present in BOTH players' views. This is
        the opposite of poker's smoke test on purpose: hole cards are secret in hold'em and
        face up in blackjack, and a table where you cannot see the other hands is not a
        blackjack table.
      * THE ONE-TABLE RULE. While seated at a shared table, the SOLO table must refuse --
        one escrow row per session cannot serve two tables, and the failure mode is a mint.
        See docs/memory/2026-09-07-one-escrow-row-two-tables.md.

    THIS STAKES REAL CURRENCY on BOTH profiles. Use -DryRun to prove the routes are
    reachable without betting anything.

    What it does NOT cover: the websocket. This talks to the HTTP routes only, so it
    exercises the table, the boxes, the money and the views -- but nothing pushes to a
    PowerShell script. A player's view here is what they get when they ask, which is what
    the panel falls back to anyway. The socket needs two game clients and is the one part
    that cannot be proved from here.

.PARAMETER SessionId
    The first profile. Find it in the filename under SPT\user\profiles\.

.PARAMETER FriendSessionId
    The second profile. Must be a DIFFERENT profile -- the point is two people.

.EXAMPLE
    .\shared-smoke.ps1 -SessionId 66e4... -FriendSessionId 66e5... -DryRun

.EXAMPLE
    .\shared-smoke.ps1 -SessionId 66e4... -FriendSessionId 66e5... -Rounds 3
#>
param(
    [Parameter(Mandatory = $true)][string]$SessionId,
    [Parameter(Mandatory = $true)][string]$FriendSessionId,
    [string]$Server = "https://127.0.0.1:6969",
    [int]$Seats = 4,
    [int]$Wager = 10000,
    [int]$Rounds = 2,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

if ($SessionId -eq $FriendSessionId) {
    Write-Host "Both ids are the same profile. The point of this script is two people." -ForegroundColor Red
    exit 1
}

# Self-signed certificate on loopback. See scripts/blackjack/smoke.ps1 for why this reads
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
$failures = New-Object System.Collections.Generic.List[string]

function New-Player {
    param([string]$Id, [string]$Label)

    # The session travels as a PHPSESSID cookie and cannot go through -Headers: "Cookie"
    # is restricted and PowerShell drops it SILENTLY, so the request arrives with no
    # session at all and the server answers "session id provided was empty".
    $ws = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $ws.Cookies.Add((New-Object System.Net.Cookie("PHPSESSID", $Id, "/", $serverUri.Host)))

    return [pscustomobject]@{ Id = $Id; Label = $Label; Session = $ws }
}

function Invoke-Blackjack {
    param($Player, [string]$Route, [hashtable]$Body = @{})

    # Compress:$false would nest the hashtable's own ToString. Depth 5 covers the deepest
    # body here, which is two keys.
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

    $ping = Invoke-Blackjack $Player "/blackjack/ping"
    $roubles = $ping.balances.Roubles
    Write-Host ("  {0,-8} {1,-8} {2,14:N0} roubles" -f $Player.Label, $When, $roubles)
    return [int64]$roubles
}

function Fail {
    param([string]$Message)

    Write-Host "  $Message" -ForegroundColor Red
    $failures.Add($Message)
}

function Get-Cards {
    param($Seat)

    # Flattened across hands, because a split makes two and both are that box's cards.
    $cards = @()
    foreach ($hand in @($Seat.hands)) { $cards += @($hand.cards) }
    return $cards
}

$alice = New-Player $SessionId "ALICE"
$bob = New-Player $FriendSessionId "BOB"

Write-Host "Shared blackjack smoke test -> $Server" -ForegroundColor Cyan
Write-Host ""

# ------------------------------------------------------------------- reachability

foreach ($player in @($alice, $bob)) {
    $ping = Invoke-Blackjack $player "/blackjack/ping"

    if (-not $ping.hasProfile) {
        Write-Host "  $($player.Label): no profile for that session id." -ForegroundColor Red
        Write-Host "  A blank id in the server log means the cookie did not resolve." -ForegroundColor Yellow
        exit 1
    }

    Write-Host "  $($player.Label) profile found, mod $($ping.modVersion)" -ForegroundColor Green
}

$tables = Invoke-Blackjack $alice "/blackjack/shared/tables"
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

# ---------------------------------------------------------------------- sit down

Write-Host ""
Write-Host "Sitting down..." -ForegroundColor Cyan

$opened = Invoke-Blackjack $alice "/blackjack/shared/open" @{ Seats = $Seats; Wallet = "Roubles" }

if (-not $opened.ok) {
    Write-Host "  ALICE could not open a table: $($opened.error)" -ForegroundColor Red
    exit 1
}

# Nothing should have been taken. A blackjack box costs nothing until it bets, which is
# the whole difference from poker's buy-in -- and a debit here would mean the shared table
# is charging a seat.
$afterOpen = Invoke-Blackjack $alice "/blackjack/ping"
if ([int64]$afterOpen.balances.Roubles -ne $aliceBefore) {
    Fail "Sitting down cost ALICE money. A blackjack box is free until it bets."
}

$listed = Invoke-Blackjack $bob "/blackjack/shared/tables"
$table = $listed.tables | Select-Object -First 1

if (-not $table) {
    Write-Host "  BOB cannot see the table ALICE just opened. That is the bug." -ForegroundColor Red
    Invoke-Blackjack $alice "/blackjack/shared/leave" | Out-Null
    exit 1
}

Write-Host "  table $($table.id): $($table.people) of $($table.seats) boxes, host '$($table.hostName)'" -ForegroundColor Green

$joined = Invoke-Blackjack $bob "/blackjack/shared/join" @{ TableId = $table.id }

if (-not $joined.ok) {
    Write-Host "  BOB could not sit down: $($joined.error)" -ForegroundColor Red
    Invoke-Blackjack $alice "/blackjack/shared/leave" | Out-Null
    exit 1
}

Write-Host "  BOB sat down in box $($joined.yourSeat). Two people at one table." -ForegroundColor Green

# ------------------------------------------------------------------------- names

Write-Host ""
Write-Host "Names:" -ForegroundColor Cyan

$view = (Invoke-Blackjack $alice "/blackjack/shared/state").sharedTable

foreach ($seat in $view.seats) {
    if (-not $seat.isOccupied) { continue }

    Write-Host "  box $($seat.index): '$($seat.name)'"

    if ($seat.name -eq "You") {
        Fail "Box $($seat.index) is called 'You'. That is a relationship, not a name -- it reaches everybody unchanged and each player sees their friend labelled as themselves."
    }

    if ($seat.name -match '^(Box|Seat) \d+$') {
        Fail "Box $($seat.index) fell back to '$($seat.name)'. The PMC nickname did not resolve."
    }
}

# Both players must see the SAME names. A name that is right in one view and wrong in the
# other is the shape the poker defect actually took.
$bobView = (Invoke-Blackjack $bob "/blackjack/shared/state").sharedTable

foreach ($seat in $view.seats) {
    $theirs = $bobView.seats | Where-Object { $_.index -eq $seat.index }

    if ($theirs.name -ne $seat.name) {
        Fail "Box $($seat.index) is '$($seat.name)' to ALICE and '$($theirs.name)' to BOB."
    }
}

# ----------------------------------------------------------------- the solo table

Write-Host ""
Write-Host "The one-table rule:" -ForegroundColor Cyan

# One escrow row per session cannot serve two tables, and the failure mode is not a
# miscount -- opening the solo panel while seated at a shared table used to refund the
# shared stake as an orphan, then pay it again on standing up.
$solo = Invoke-Blackjack $alice "/blackjack/state"

if ($solo.ok) {
    Fail "The SOLO table answered while ALICE is at a shared one. That is the path that mints money -- see docs/memory/2026-09-07-one-escrow-row-two-tables.md."
}
else {
    Write-Host "  solo table refuses while seated: '$($solo.error)'" -ForegroundColor Green
}

$soloBalance = [int64](Invoke-Blackjack $alice "/blackjack/ping").balances.Roubles
if ($soloBalance -ne $aliceBefore) {
    Fail "Asking the solo table moved ALICE's money by $($soloBalance - $aliceBefore). Nothing should have moved."
}

# --------------------------------------------------------------------- the rounds

$aliceStaked = [int64]0
$aliceReturned = [int64]0
$bobStaked = [int64]0
$bobReturned = [int64]0

for ($round = 1; $round -le $Rounds; $round++) {
    Write-Host ""
    Write-Host "Round $round of ${Rounds}:" -ForegroundColor Cyan

    foreach ($player in @($alice, $bob)) {
        $bet = Invoke-Blackjack $player "/blackjack/shared/bet" @{ Wager = $Wager; Wallet = "Roubles" }

        if (-not $bet.ok) {
            Fail "$($player.Label) could not bet: $($bet.error)"
            break
        }
    }

    # Anybody seated may deal. BOB does it deliberately -- a table that only its host
    # could start stops dead the moment that person walks away.
    $dealt = Invoke-Blackjack $bob "/blackjack/shared/deal"

    if (-not $dealt.ok) {
        Fail "Could not deal: $($dealt.error)"
        break
    }

    $t = $dealt.sharedTable
    Write-Host "  dealt. dealer shows $($t.dealer.value), phase $($t.phase)"

    # --- the sharing. Both boxes' cards must be in BOTH views.
    $aliceSees = (Invoke-Blackjack $alice "/blackjack/shared/state").sharedTable
    $bobSees = (Invoke-Blackjack $bob "/blackjack/shared/state").sharedTable

    foreach ($seat in $aliceSees.seats | Where-Object { $_.isInRound }) {
        $mine = Get-Cards $seat
        $theirs = Get-Cards ($bobSees.seats | Where-Object { $_.index -eq $seat.index })

        if (@($mine).Count -eq 0) {
            Fail "Box $($seat.index) was dealt in but shows no cards."
        }

        foreach ($card in $mine) {
            if ($theirs -notcontains $card) {
                Fail "Box $($seat.index)'s $card is in ALICE's view but not BOB's. Blackjack cards are face up -- both must see every hand."
            }
        }
    }

    # The dealer's hole card is the one thing hidden, and from everybody equally.
    if ($t.phase -eq "PlayerTurn" -and @($aliceSees.dealer.cards).Count -ne 1) {
        Fail "The dealer is showing $(@($aliceSees.dealer.cards).Count) cards mid-round. The hole card should be concealed."
    }

    # --- play it out. Everybody stands, which is legal from any total and settles the
    # round without this script having to know what was dealt.
    $guard = 0

    while ($true) {
        $state = (Invoke-Blackjack $alice "/blackjack/shared/state").sharedTable

        if ($null -eq $state.activeSeat) { break }

        if (++$guard -gt 20) {
            Fail "The round never settled -- the turn is stuck on box $($state.activeSeat)."
            break
        }

        # Whose turn is it? The box index maps to a player through the seat each was told
        # when they sat down, which is why YourSeat is sent rather than inferred.
        $who = if ($state.activeSeat -eq $joined.yourSeat) { $bob } else { $alice }

        $acted = Invoke-Blackjack $who "/blackjack/shared/action" @{ Action = "Stand" }

        if (-not $acted.ok) {
            Fail "$($who.Label) could not stand on box $($state.activeSeat): $($acted.error)"
            break
        }
    }

    $final = (Invoke-Blackjack $alice "/blackjack/shared/state").sharedTable

    foreach ($seat in $final.seats | Where-Object { $_.isInRound }) {
        $label = if ($seat.index -eq $joined.yourSeat) { "BOB  " } else { "ALICE" }
        $net = [int64]$seat.totalReturned - [int64]$seat.totalWagered

        Write-Host ("  box {0} {1} staked {2,10:N0} back {3,10:N0} net {4,10:N0}" -f `
                $seat.index, $label, $seat.totalWagered, $seat.totalReturned, $net)

        if ($seat.index -eq $joined.yourSeat) {
            $bobStaked += [int64]$seat.totalWagered
            $bobReturned += [int64]$seat.totalReturned
        }
        else {
            $aliceStaked += [int64]$seat.totalWagered
            $aliceReturned += [int64]$seat.totalReturned
        }
    }
}

# --------------------------------------------------------------------- stand up

Write-Host ""
Write-Host "Standing up..." -ForegroundColor Cyan

foreach ($player in @($bob, $alice)) {
    $left = Invoke-Blackjack $player "/blackjack/shared/leave"

    if (-not $left.ok) {
        Fail "$($player.Label) could not stand up: $($left.error)"
    }
}

# The last person out must be free to sit somewhere else. The claim on a player is
# released by walking the table's occupants, so the one who closes the table is the one
# whose claim can be left behind -- and the symptom is a refusal on some later request,
# a long way from the cause.
$again = Invoke-Blackjack $alice "/blackjack/shared/open" @{ Seats = $Seats; Wallet = "Roubles" }

if (-not $again.ok) {
    Fail "ALICE cannot open a table after closing one: '$($again.error)'. Her claim outlived the table."
}
else {
    Write-Host "  the last player out can sit down again" -ForegroundColor Green
    Invoke-Blackjack $alice "/blackjack/shared/leave" | Out-Null
}

# And the solo table is hers again. A guard that latched would lock a player out of the
# game they have always played.
$soloBack = Invoke-Blackjack $alice "/blackjack/state"

if (-not $soloBack.ok) {
    Fail "ALICE cannot use the solo table after standing up: '$($soloBack.error)'."
}
else {
    Write-Host "  the solo table is hers again" -ForegroundColor Green
}

# ----------------------------------------------------------------------- the money

Write-Host ""
Write-Host "After:" -ForegroundColor Cyan
$aliceAfter = Show-Money $alice "after"
$bobAfter = Show-Money $bob "after"

Write-Host ""
Write-Host "The books:" -ForegroundColor Cyan

foreach ($row in @(
        @{ Label = "ALICE"; Before = $aliceBefore; After = $aliceAfter; Staked = $aliceStaked; Returned = $aliceReturned },
        @{ Label = "BOB  "; Before = $bobBefore; After = $bobAfter; Staked = $bobStaked; Returned = $bobReturned }
    )) {

    $moved = [int64]$row.After - [int64]$row.Before
    $owed = [int64]$row.Returned - [int64]$row.Staked

    Write-Host ("  {0} stash moved {1,12:N0}   engine says {2,12:N0}" -f $row.Label, $moved, $owed)

    # THE assertion. What the stash did must equal what the engine says that seat is owed
    # -- per player, never summed. A settlement that paid one player out of the other's
    # stake balances across the table and would pass any total.
    if ($moved -ne $owed) {
        Fail "$($row.Label): the stash moved $moved but the engine says $owed. Those must match."
    }
}

Write-Host ""

if ($failures.Count -eq 0) {
    Write-Host "Two people played blackjack at one table, and the books balance per player." -ForegroundColor Green
    exit 0
}

Write-Host "$($failures.Count) problem(s):" -ForegroundColor Red
foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
exit 1
