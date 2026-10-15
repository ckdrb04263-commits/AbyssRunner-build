$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$p = Join-Path $root 'work\src\AbyssRunner\UI\MainForm.cs'
$s = Get-Content $p -Raw

# Remove reward/loot dashboard fields.
$s = [regex]::Replace($s, '(?s)\s*private readonly ListBox _lootRecent = new\(\).*?private readonly Label _lootStatus = .*?;\s*', "`r`n")

# Restore a compact recent activity card instead of loot UI.
$recordsPattern = '(?s)        var records = Card\(\);\s*records\.Padding = new Padding\(14, 10, 14, 10\);.*?records\.Controls\.Add\(lootGrid\);'
$recordsReplacement = @'
        var records = Card();
        records.Padding = new Padding(18, 10, 18, 10);
        var recTitle = MakeLabel("☰  최근 기록", 9, FontStyle.Bold, Color.FromArgb(58, 68, 74));
        recTitle.Dock = DockStyle.Top;
        recTitle.Height = 27;
        records.Controls.Add(recTitle);
        _recent.Dock = DockStyle.Fill;
        _recent.BringToFront();
        records.Controls.Add(_recent);
'@
$updated = [regex]::Replace($s, $recordsPattern, $recordsReplacement, 1)
if ($updated -eq $s) { throw 'Loot UI records block patch point not found' }
$s = $updated

# Remove loot event wiring and per-run loot UI reset.
$s = [regex]::Replace($s, '\s*_engine\.LootUpdated \+= snapshot => Ui\(\(\) => UpdateLoot\(snapshot\)\);\s*', "`r`n")
$s = [regex]::Replace($s, '(?s)\s*_lootRecent\.Items\.Clear\(\);\s*_lootTotals\.Items\.Clear\(\);\s*_lootStatus\.Text = "보상 화면이 나오면 자동으로 OCR 기록합니다\." ;?\s*_lootStatus\.ForeColor = Color\.FromArgb\(133, 144, 151\);\s*', "`r`n")

# In case spacing differs, remove reset lines individually.
$s = [regex]::Replace($s, '^\s*_lootRecent\.Items\.Clear\(\);\s*$', '', [System.Text.RegularExpressions.RegexOptions]::Multiline)
$s = [regex]::Replace($s, '^\s*_lootTotals\.Items\.Clear\(\);\s*$', '', [System.Text.RegularExpressions.RegexOptions]::Multiline)
$s = [regex]::Replace($s, '^\s*_lootStatus\.Text = .*?;\s*$', '', [System.Text.RegularExpressions.RegexOptions]::Multiline)
$s = [regex]::Replace($s, '^\s*_lootStatus\.ForeColor = .*?;\s*$', '', [System.Text.RegularExpressions.RegexOptions]::Multiline)

# Remove UpdateLoot method.
$methodPattern = '(?s)\s*private void UpdateLoot\(LootSnapshot snapshot\)\s*\{.*?\n    \}\s*\n\s*    private void UpdateModeSummary\(\)'
$methodReplacement = @'

    private void UpdateModeSummary()
'@
$updated = [regex]::Replace($s, $methodPattern, $methodReplacement, 1)
if ($updated -eq $s) { throw 'UpdateLoot method patch point not found' }
$s = $updated

# Shrink the window/card row back now that the loot dashboard is gone.
$s = $s.Replace('Height = 790;', 'Height = 720;')
$s = $s.Replace('MinimumSize = new Size(980, 720);', 'MinimumSize = new Size(980, 660);')
$s = $s.Replace('content.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));', 'content.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));')

if ($s.Contains('_lootRecent') -or $s.Contains('_lootTotals') -or $s.Contains('_lootStatus') -or $s.Contains('LootUpdated') -or $s.Contains('UpdateLoot(')) {
    throw 'Loot UI references remain after removal'
}

Set-Content $p $s -Encoding utf8
Write-Host 'Loot/item tracking UI removed.'
