# After the training campaign finishes, re-run the shipping selector.
#
# WHY THIS EXISTS: campaign_v10.py ends by copying its own best STAGE output over
# checkpoints/ppo_selfplay_v10.zip and re-exporting the .onnx. Its leaderboard only knows stage
# outputs — it does not know about the pool snapshots that screen_pool.py rescues, and the model
# currently shipping (score 0.982, a mid-stage pool snapshot) beats every stage output. So the
# campaign's own final step would SILENTLY DOWNGRADE the deliverable.
#
# This watcher waits for the campaign's "campaign done" sentinel and then re-runs ship_v10.py, which
# re-ranks EVERYTHING on disk (pool screens + stage gates + baselines) and ships the true best.
# It runs as its own process so it survives independently of the agent session.
$repo = Split-Path -Parent $PSScriptRoot
$log  = Join-Path $PSScriptRoot "logs\campaign.log"
$mine = Join-Path $PSScriptRoot "logs\watcher.log"

function Say($m) {
    $line = "[watcher] $(Get-Date -Format 'HH:mm:ss') $m"
    Write-Output $line
    Add-Content -Path $mine -Value $line
}

Say "armed; waiting for the campaign to finish (sentinel: 'campaign done' in logs\campaign.log)"
$waited = 0
while ($true) {
    if ((Test-Path $log) -and (Select-String -Path $log -Pattern "campaign done" -Quiet)) { break }
    Start-Sleep -Seconds 120
    $waited += 2
    if ($waited % 30 -eq 0) { Say "still waiting (${waited} min)" }
}

Say "campaign finished — screening the freshest pool snapshots, then re-ranking and re-shipping"
Set-Location $repo
# Screen the newest snapshots FIRST: the campaign's own gates only score stage outputs, and the best
# model so far came from a mid-stage pool snapshot that no gate ever looked at. Without this step the
# final re-ship would only see stage outputs.
$scr = & python rl\screen_pool.py --count 8 --newest --games 6 2>&1
$scr | Add-Content -Path $mine
Say "pool screen tail: " + (($scr | Select-Object -Last 12) -join " / ")

$out = & python rl\ship_v10.py --games 20 2>&1
$out | Add-Content -Path $mine
$tail = ($out | Select-Object -Last 12) -join "`n"
Say "ship done. tail:`n$tail"
