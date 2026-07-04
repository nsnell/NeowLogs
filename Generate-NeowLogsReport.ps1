param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$InputPath,

    [Parameter(Position = 1)]
    [string]$OutputPath = ".\neowlogs-share-report.html"
)

$ErrorActionPreference = "Stop"

function HtmlEscape($value) {
    return [System.Net.WebUtility]::HtmlEncode([string]$value)
}

function ToNumber($value) {
    if ($null -eq $value -or $value -eq "") { return 0.0 }
    $out = 0.0
    if ([double]::TryParse([string]$value, [ref]$out)) { return $out }
    return 0.0
}

function GetProp($object, [string]$name) {
    if ($null -eq $object) { return $null }
    $prop = $object.PSObject.Properties[$name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

function Meta($event, [string]$name) {
    return GetProp (GetProp $event "metadata") $name
}

function DisplayName($value) {
    $text = ([string]$value).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return "" }
    if ($text -match '^PlayerId\s+\d+$') { return "" }
    if ($text -match '^\d+$') { return "" }
    return $text
}

function IsTrue($value) {
    if ($value -is [bool]) { return $value }
    $text = ([string]$value).Trim().ToLowerInvariant()
    return $text -in @("true", "1", "yes")
}

function IsPlayerActor($event) {
    $meta = GetProp $event "metadata"
    $actorIsPlayer = GetProp $meta "actor_is_player"
    if ($null -ne $actorIsPlayer) { return IsTrue $actorIsPlayer }

    $actorSide = [string](GetProp $meta "actor_side")
    if ($actorSide -eq "Player") { return $true }
    if ($actorSide -eq "Enemy") { return $false }

    $arg1 = GetProp $meta "arg_1_members"
    $arg1IsPlayer = GetProp $arg1 "IsPlayer"
    if ($null -ne $arg1IsPlayer) { return IsTrue $arg1IsPlayer }

    $arg1Side = [string](GetProp $arg1 "Side")
    if ($arg1Side -eq "Player") { return $true }
    if ($arg1Side -eq "Enemy") { return $false }

    $type = [string](GetProp $event "event_type")
    $actor = DisplayName (Meta $event "actor_display_name")
    if ([string]::IsNullOrWhiteSpace($actor)) { $actor = DisplayName (GetProp $event "actor_name") }
    if ($type -in @("card_played", "block_gained", "healing_done", "debuff_applied", "buff_applied", "power_applied", "damage_taken")) {
        return -not [string]::IsNullOrWhiteSpace($actor)
    }

    return $false
}

function IsEnemyActor($event) {
    $meta = GetProp $event "metadata"
    $actorIsEnemy = GetProp $meta "actor_is_enemy"
    if ($null -ne $actorIsEnemy) { return IsTrue $actorIsEnemy }

    $actorSide = [string](GetProp $meta "actor_side")
    if ($actorSide -eq "Enemy") { return $true }

    $arg1 = GetProp $meta "arg_1_members"
    $arg1IsEnemy = GetProp $arg1 "IsEnemy"
    if ($null -ne $arg1IsEnemy) { return IsTrue $arg1IsEnemy }

    $arg1Side = [string](GetProp $arg1 "Side")
    return $arg1Side -eq "Enemy"
}

function NewStats {
    [ordered]@{
        Damage = 0.0
        Direct = 0.0
        DamageAssist = 0.0
        Block = 0.0
        DamageBlocked = 0.0
        PreventedDamage = 0.0
        UtilityScore = 0.0
        UtilityDamage = 0.0
        DamageTaken = 0.0
        Healing = 0.0
        Cards = 0
        Attacks = 0
        Skills = 0
        Powers = 0
        Energy = 0.0
        Utility = 0
        Statuses = @{}
        Sources = @{}
    }
}

function EnsureStats($table, [string]$key) {
    if ([string]::IsNullOrWhiteSpace($key)) { $key = "Unknown" }
    if (-not $table.Contains($key)) {
        $table[$key] = NewStats
    }
    return $table[$key]
}

function AddCounter($table, [string]$key, [double]$amount) {
    if ([string]::IsNullOrWhiteSpace($key)) { $key = "Unknown" }
    if (-not $table.Contains($key)) { $table[$key] = 0.0 }
    $table[$key] = [double]$table[$key] + $amount
}

function UtilityScore($stats) {
    return [math]::Round(([double]$stats.DamageAssist + [double]$stats.PreventedDamage), 0)
}

function DamageContribution($stats) {
    return [double]$stats.Damage + [double]$stats.DamageAssist
}

function BlockContribution($stats) {
    return [double]$stats.Block + [double]$stats.PreventedDamage
}

function EstimatePreventedDamage($event, $setup) {
    if ($null -eq $setup) { return 0.0 }
    $observed = ToNumber (Meta $event "total_damage")
    if ($observed -le 0) {
        $observed = (ToNumber (GetProp $event "amount")) + (ToNumber (Meta $event "blocked_damage"))
    }
    if ($observed -le 0) { return 0.0 }

    $status = ([string](GetProp $setup "Status")).ToLowerInvariant()
    if ($status.Contains("weak")) {
        return [math]::Max(0, ($observed / 0.75) - $observed)
    }
    if ($status.Contains("strength")) {
        return [math]::Max(0, [math]::Abs((ToNumber (GetProp $setup "Stacks"))))
    }
    return 0.0
}

function NormalizeCardType($value) {
    $text = ([string]$value).ToLowerInvariant()
    if ($text.Contains(".")) { return ($text -split "\.")[-1] }
    return $text
}

function IsIndirectDamageSource($event) {
    $text = "$([string](GetProp $event "source_type")) $([string](GetProp $event "source_name")) $([string](Meta $event "power")) $([string](Meta $event "status"))".ToLowerInvariant()
    return $text.Contains("poison") -or $text.Contains("doom")
}

function RankRows($statsByPlayer, [string]$metric) {
    $rows = foreach ($name in $statsByPlayer.Keys) {
        $s = $statsByPlayer[$name]
        if ($metric -eq "Block") {
            $primary = [double]$s.Block
            $assist = [double]$s.PreventedDamage
            $value = BlockContribution $s
        }
        else {
            $primary = [double]$s.Damage
            $assist = [double]$s.DamageAssist
            $value = DamageContribution $s
        }
        [pscustomobject]@{ Name = $name; Value = $value; Primary = $primary; Assist = $assist }
    }
    return @($rows | Sort-Object Value -Descending)
}

function Bar($row, $max) {
    $value = [double]$row.Value
    $width = if ($max -le 0) { 0 } else { [math]::Max(4, [math]::Round(($value / $max) * 100)) }
    $primaryWidth = if ($value -le 0) { 0 } else { [math]::Round(([double]$row.Primary / $value) * $width) }
    $assistWidth = [math]::Max(0, $width - $primaryWidth)
    return "<span class='bar'><span class='bar-main' style='width:$primaryWidth%'></span><span class='bar-assist' style='width:$assistWidth%'></span></span>"
}

function Board($title, $rows) {
    $rows = @($rows | Select-Object -First 5)
    $max = [double](($rows | Measure-Object Value -Maximum).Maximum)
    if ($null -eq $max) { $max = 0 }
    $body = ""
    $i = 0
    foreach ($row in $rows) {
        $i += 1
        $body += "<tr><td class='rank'>$i</td><td>$(HtmlEscape $row.Name)</td><td>$(Bar $row $max)</td><td class='num'>$([math]::Round($row.Value,0))</td></tr>"
    }
    if ($body -eq "") { $body = "<tr><td colspan='4' class='muted'>No data yet</td></tr>" }
    return "<section class='panel board'><h2>$(HtmlEscape $title)</h2><table>$body</table></section>"
}

function TopSource($sources) {
    $top = $sources.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
    if ($null -eq $top) { return "None" }
    return "$(HtmlEscape $top.Key) ($([math]::Round([double]$top.Value,0)))"
}

$inputFile = Resolve-Path -LiteralPath $InputPath
$events = @()
foreach ($line in Get-Content -LiteralPath $inputFile -Encoding UTF8) {
    if (-not [string]::IsNullOrWhiteSpace($line)) {
        $events += ($line | ConvertFrom-Json)
    }
}

$players = [ordered]@{}
$enemyDamage = @{}
$timeline = @()
$utilityStatuses = @("vulnerable", "weak", "frail", "mark", "lock_on", "strength_down", "artifact_strip", "strength")
$vulnerableByTarget = @{}
$reductionBySource = @{}
$indirectDamageByTarget = @{}

foreach ($event in $events) {
    $type = ([string](GetProp $event "event_type")).ToLowerInvariant()
    $amount = ToNumber (GetProp $event "amount")
    $base = ToNumber (GetProp $event "base_amount")
    if ($base -eq 0) { $base = $amount }
    $actor = DisplayName (Meta $event "actor_display_name")
    if ([string]::IsNullOrWhiteSpace($actor)) { $actor = DisplayName (GetProp $event "actor_name") }
    if ([string]::IsNullOrWhiteSpace($actor)) { $actor = DisplayName (GetProp $event "player") }
    if ([string]::IsNullOrWhiteSpace($actor)) { $actor = [string](GetProp $event "actor_player_id") }
    $source = [string](GetProp $event "source_name")
    $cardName = [string](Meta $event "card_name")
    if (-not [string]::IsNullOrWhiteSpace($cardName)) { $source = $cardName }
    if ([string]::IsNullOrWhiteSpace($source)) { $source = $type }

    if ($type -eq "damage_dealt" -and (IsEnemyActor $event)) {
        AddCounter $enemyDamage $actor $amount
    }

    if (-not (IsPlayerActor $event)) {
        $targetKey = [string](GetProp $event "target_id")
        if ([string]::IsNullOrWhiteSpace($targetKey)) { $targetKey = [string](GetProp $event "target_name") }
        if ($type -ne "damage_dealt" -or -not (IsIndirectDamageSource $event) -or -not $indirectDamageByTarget.ContainsKey($targetKey)) {
            continue
        }
        $actor = GetProp $indirectDamageByTarget[$targetKey] "Player"
    }

    $stats = EnsureStats $players $actor

    switch ($type) {
        "damage_dealt" {
            $bonus = [math]::Max(0, $amount - $base)
            $stats.Damage += $amount
            $stats.Direct += [math]::Max(0, $amount - $bonus)
            AddCounter $stats.Sources $source $amount
            $targetKey = [string](GetProp $event "target_id")
            if ([string]::IsNullOrWhiteSpace($targetKey)) { $targetKey = [string](GetProp $event "target_name") }
            if (-not [string]::IsNullOrWhiteSpace($targetKey) -and $vulnerableByTarget.ContainsKey($targetKey)) {
                $utilityBonus = if ($bonus -gt 0) { $bonus } else { [math]::Max(0, $amount - ($amount / 1.5)) }
                if ($utilityBonus -gt 0) {
                    $helper = EnsureStats $players $vulnerableByTarget[$targetKey]
                    $helper.DamageAssist += $utilityBonus
                    $helper.UtilityDamage += $utilityBonus
                }
            }
        }
        "block_gained" {
            $stats.Block += $amount
            AddCounter $stats.Sources $source $amount
        }
        "damage_taken" {
            $stats.DamageTaken += $amount
            $blocked = ToNumber (Meta $event "blocked_damage")
            $stats.DamageBlocked += $blocked
            if (IsTrue (Meta $event "pet_damage_absorbed")) { $stats.Block += $blocked }
            $sourceKey = [string](GetProp $event "target_id")
            if ([string]::IsNullOrWhiteSpace($sourceKey)) { $sourceKey = [string](GetProp $event "target_name") }
            $setup = if ($reductionBySource.ContainsKey($sourceKey)) { $reductionBySource[$sourceKey] } else { $null }
            $prevented = ToNumber (Meta $event "prevented_damage")
            if ($prevented -le 0) { $prevented = EstimatePreventedDamage $event $setup }
            if (-not [string]::IsNullOrWhiteSpace($sourceKey) -and $prevented -gt 0 -and $null -ne $setup) {
                $reducer = EnsureStats $players (GetProp $setup "Player")
                $reducer.PreventedDamage += $prevented
                $reducer.UtilityDamage += $prevented
            }
        }
        "hp_lost" {
            $stats.DamageTaken += $amount
        }
        "healing_done" {
            $stats.Healing += $amount
        }
        "heal" {
            $stats.Healing += $amount
        }
        "card_played" {
            $stats.Cards += 1
            $stats.Energy += ToNumber (Meta $event "energy_spent")
            $cardType = NormalizeCardType (Meta $event "card_type")
            if ($cardType -eq "attack") { $stats.Attacks += 1 }
            elseif ($cardType -eq "skill") { $stats.Skills += 1 }
            elseif ($cardType -eq "power") { $stats.Powers += 1 }
            AddCounter $stats.Sources $source 1
        }
        "debuff_applied" {
            $status = ([string](Meta $event "status")).ToLowerInvariant()
            if ([string]::IsNullOrWhiteSpace($status)) { $status = "debuff" }
            if (-not $stats.Statuses.Contains($status)) { $stats.Statuses[$status] = 0 }
            $stats.Statuses[$status] = [int]$stats.Statuses[$status] + [math]::Max(1, [int]$amount)
            if ($utilityStatuses -contains $status) { $stats.Utility += 1 }
            $targetKey = [string](GetProp $event "target_id")
            if ([string]::IsNullOrWhiteSpace($targetKey)) { $targetKey = [string](GetProp $event "target_name") }
            if ($status -eq "vulnerable" -and -not [string]::IsNullOrWhiteSpace($targetKey)) {
                $vulnerableByTarget[$targetKey] = $actor
            }
            if (($status.Contains("poison") -or $status.Contains("doom")) -and -not [string]::IsNullOrWhiteSpace($targetKey)) {
                $indirectDamageByTarget[$targetKey] = [pscustomobject]@{ Player = $actor; Status = $status; Stacks = $amount }
            }
            if (($status.Contains("weak") -or $status.Contains("strength_down") -or $status.Contains("damage_decrease") -or ($status.Contains("strength") -and $amount -lt 0)) -and -not [string]::IsNullOrWhiteSpace($targetKey)) {
                $reductionBySource[$targetKey] = [pscustomobject]@{ Player = $actor; Status = $status; Stacks = $amount }
            }
        }
        "buff_applied" {
            $stats.Utility += 1
        }
        "power_applied" {
            $stats.Utility += 1
        }
        "card_drawn" {
            $stats.Utility += 1
        }
        "energy_gained" {
            $stats.Utility += 1
        }
        default {
            if ($type -in @("potion_used", "relic_triggered", "utility")) {
                $stats.Utility += 1
            }
        }
    }

    $timeline += [pscustomobject]@{
        Time = ToNumber (GetProp $event "timestamp_ms")
        Actor = $actor
        Type = $type
        Source = $source
        Target = [string](GetProp $event "target_name")
        Amount = $amount
    }
}

$runId = if ($events.Count -gt 0) { [string](GetProp $events[0] "run_id") } else { [IO.Path]::GetFileNameWithoutExtension($inputFile) }
$generated = Get-Date -Format "yyyy-MM-dd HH:mm"
$totalDamage = ($players.Values | ForEach-Object { DamageContribution $_ } | Measure-Object -Sum).Sum
$totalBlock = ($players.Values | ForEach-Object { BlockContribution $_ } | Measure-Object -Sum).Sum
$totalUtility = ($players.Values | ForEach-Object { UtilityScore $_ } | Measure-Object -Sum).Sum
$totalTaken = ($players.Values | ForEach-Object { $_.DamageTaken } | Measure-Object -Sum).Sum
$totalHealing = ($players.Values | ForEach-Object { $_.Healing } | Measure-Object -Sum).Sum

$damageRows = RankRows $players "Damage"
$blockRows = RankRows $players "Block"

$playerRows = ""
foreach ($name in ($players.Keys | Sort-Object)) {
    $s = $players[$name]
    $playerRows += "<tr><td class='player'>$(HtmlEscape $name)</td><td class='num'>$([math]::Round((DamageContribution $s),0))</td><td class='num'>$([math]::Round($s.Damage,0))</td><td class='num'>$([math]::Round($s.DamageAssist,0))</td><td class='num'>$([math]::Round((BlockContribution $s),0))</td><td class='num'>$([math]::Round($s.Block,0))</td><td class='num'>$([math]::Round($s.PreventedDamage,0))</td><td class='num'>$([math]::Round($s.DamageTaken,0))</td><td class='num'>$($s.Cards)</td><td class='num'>$($s.Attacks)/$($s.Skills)/$($s.Powers)</td><td>$(TopSource $s.Sources)</td></tr>"
}
if ($playerRows -eq "") { $playerRows = "<tr><td colspan='11' class='muted'>No player combat events found.</td></tr>" }

$enemyRows = ""
foreach ($row in ($enemyDamage.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8)) {
    $enemyRows += "<tr><td>$(HtmlEscape $row.Key)</td><td class='num'>$([math]::Round([double]$row.Value,0))</td></tr>"
}
if ($enemyRows -eq "") { $enemyRows = "<tr><td colspan='2' class='muted'>No enemy damage captured yet.</td></tr>" }

$timelineRows = ""
foreach ($event in ($timeline | Sort-Object Time -Descending | Select-Object -First 24)) {
    $detail = $event.Source
    if (-not [string]::IsNullOrWhiteSpace($event.Target)) { $detail += " -> $($event.Target)" }
    $timelineRows += "<tr><td>$(HtmlEscape $event.Actor)</td><td>$(HtmlEscape $event.Type)</td><td>$(HtmlEscape $detail)</td><td class='num'>$([math]::Round($event.Amount,0))</td></tr>"
}

$css = @"
:root { color-scheme: dark; --bg:#0e1117; --panel:#171c25; --panel2:#202736; --line:#30384a; --text:#eef3fb; --muted:#9ba7ba; --gold:#f4c86a; --red:#ee746f; --blue:#7db2ff; --green:#69d79a; --violet:#c7a1ff; }
* { box-sizing: border-box; }
body { margin:0; background: radial-gradient(circle at top left, #20283a 0, #0e1117 34rem); color:var(--text); font:14px/1.45 "Segoe UI", system-ui, sans-serif; }
main { width:min(1280px, calc(100vw - 40px)); margin:0 auto; padding:28px 0 40px; }
.hero { display:flex; justify-content:space-between; gap:20px; align-items:flex-end; margin-bottom:18px; }
h1 { font-size:42px; margin:0 0 6px; letter-spacing:0; }
h2 { margin:0 0 12px; font-size:16px; }
.muted { color:var(--muted); }
.pill { border:1px solid var(--line); background:#10141d; color:var(--muted); padding:6px 10px; border-radius:999px; }
.grid { display:grid; gap:14px; }
.cards { grid-template-columns: repeat(5, minmax(0, 1fr)); margin-bottom:14px; }
.boards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.two { grid-template-columns: 1.2fr .8fr; margin-top:14px; }
.panel, .metric { background:rgba(23,28,37,.94); border:1px solid var(--line); border-radius:8px; padding:14px; box-shadow:0 18px 45px rgba(0,0,0,.22); }
.metric h3 { margin:0 0 8px; color:var(--muted); font-size:11px; text-transform:uppercase; }
.metric strong { display:block; font-size:32px; line-height:1; }
table { width:100%; border-collapse:collapse; }
td, th { padding:8px 7px; border-bottom:1px solid var(--line); text-align:left; white-space:nowrap; }
tr:last-child td { border-bottom:0; }
th { color:var(--muted); font-size:11px; text-transform:uppercase; }
.num, .rank { text-align:right; font-variant-numeric:tabular-nums; }
.rank { color:var(--gold); width:28px; }
.player { font-weight:700; }
.bar { display:flex; height:7px; width:80px; background:#0b0f16; border-radius:99px; overflow:hidden; }
.bar span { display:block; height:100%; }
.bar-main { background:linear-gradient(90deg, var(--blue), var(--green)); }
.bar-assist { background:var(--gold); }
.board td:nth-child(2) { max-width:105px; overflow:hidden; text-overflow:ellipsis; }
.note { margin-top:10px; color:var(--muted); font-size:12px; }
@media (max-width: 1050px) { .cards, .boards { grid-template-columns: repeat(2, minmax(0, 1fr)); } .two { grid-template-columns:1fr; } .hero { display:block; } }
"@

$html = @"
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>NeowLogs $runId</title>
  <style>$css</style>
</head>
<body>
  <main>
    <section class="hero">
      <div>
        <h1>NeowLogs Run Report</h1>
        <div class="muted">Run $runId Â· generated $generated Â· $($events.Count) events parsed</div>
      </div>
      <div class="pill">Screenshot-ready local report</div>
    </section>

    <section class="grid cards">
      <div class="metric"><h3>Damage</h3><strong>$([math]::Round($totalDamage,0))</strong></div>
      <div class="metric"><h3>Block</h3><strong>$([math]::Round($totalBlock,0))</strong></div>
      <div class="metric"><h3>Utility</h3><strong>$([math]::Round($totalUtility,0))</strong></div>
      <div class="metric"><h3>Damage Taken</h3><strong>$([math]::Round($totalTaken,0))</strong></div>
      <div class="metric"><h3>Healing</h3><strong>$([math]::Round($totalHealing,0))</strong></div>
    </section>

    <section class="grid boards">
      $(Board "Damage" $damageRows)
      $(Board "Block" $blockRows)
    </section>

    <section class="panel" style="margin-top:14px">
      <h2>Player Breakdown</h2>
      <table>
        <thead><tr><th>Player</th><th class="num">Damage Total</th><th class="num">Raw Dmg</th><th class="num">Dmg Assist</th><th class="num">Block Total</th><th class="num">Block</th><th class="num">Prevented</th><th class="num">Taken</th><th class="num">Cards</th><th class="num">Atk/Skill/Power</th><th>Top Source</th></tr></thead>
        <tbody>$playerRows</tbody>
      </table>
      <div class="note">Damage Total = raw damage + damage utility. Block Total = direct block + prevented damage from utility like Weak or Strength reduction.</div>
    </section>

    <section class="grid two">
      <section class="panel">
        <h2>Enemy Damage Captured</h2>
        <table><thead><tr><th>Creature</th><th class="num">Damage</th></tr></thead><tbody>$enemyRows</tbody></table>
      </section>
      <section class="panel">
        <h2>Recent Player Events</h2>
        <table><thead><tr><th>Player</th><th>Type</th><th>Detail</th><th class="num">Amount</th></tr></thead><tbody>$timelineRows</tbody></table>
      </section>
    </section>
  </main>
</body>
</html>
"@

$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$outDir = [IO.Path]::GetDirectoryName($out)
if (-not [string]::IsNullOrWhiteSpace($outDir)) {
    [IO.Directory]::CreateDirectory($outDir) | Out-Null
}
[IO.File]::WriteAllText($out, $html, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $out from $($events.Count) events."

