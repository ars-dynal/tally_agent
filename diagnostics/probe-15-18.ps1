# ============================================================================
#  Round 2 — what is 4,355, and are we asking wrongly?
#
#  PART A costs Tally NOTHING: it reads the probe-10 response already saved on
#  disk and buckets its 4,354 DATE values by financial year. That alone says
#  whether 4,355 is one financial year or eight.
#
#  PART B writes four new probes into a SEPARATE folder (so probe 14 stays
#  held back) and runs them.
# ============================================================================

$OneDay     = '20260901'    # the day Day Book shows 3 vouchers for
$FixtureDir = 'C:\ProgramData\TallyBigQueryAgent\fixtures'
$Dir        = 'C:\TallyRef\voucher-scope-2'

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- PART A ---
Write-Host "`n=== A. What did probe 10 actually return? (no Tally request) ===" -ForegroundColor Cyan

$fx = Get-ChildItem (Join-Path $FixtureDir '10-collection-oneday-nofilter-*.xml') -ErrorAction SilentlyContinue |
      Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $fx) {
    Write-Host "  probe-10 fixture not found under $FixtureDir - skipping." -ForegroundColor Yellow
} else {
    Write-Host "  $($fx.Name)  ($('{0:N0}' -f $fx.Length) bytes)"
    $text = [System.IO.File]::ReadAllText($fx.FullName)
    $raw  = [regex]::Matches($text, '<DATE>([^<]+)</DATE>') | ForEach-Object { $_.Groups[1].Value.Trim() }
    Write-Host "  <DATE> elements: $($raw.Count)"

    $fmts = @('yyyyMMdd','d-MMM-yyyy','dd-MMM-yyyy','d-MMM-yy','dd-MMM-yy','yyyy-MM-dd')
    $dates = foreach ($r in $raw) {
        $d = [datetime]::MinValue
        if ([datetime]::TryParseExact($r, $fmts, [Globalization.CultureInfo]::InvariantCulture,
                                      [Globalization.DateTimeStyles]::None, [ref]$d)) { $d }
        elseif ([datetime]::TryParse($r, [ref]$d)) { $d }
    }
    if (-not $dates -or $dates.Count -eq 0) {
        Write-Host "  Could not parse any dates. First few raw values: $($raw | Select-Object -First 5)" -ForegroundColor Yellow
    } else {
        $min = ($dates | Measure-Object -Minimum).Minimum
        $max = ($dates | Measure-Object -Maximum).Maximum
        Write-Host "  parsed          : $($dates.Count)"
        Write-Host "  DATE RANGE      : $($min.ToString('yyyy-MM-dd'))  ..  $($max.ToString('yyyy-MM-dd'))" -ForegroundColor Green
        Write-Host "`n  by financial year (Apr-Mar):"
        $dates | Group-Object { $y = if ($_.Month -ge 4) { $_.Year } else { $_.Year - 1 }
                                '{0}-{1:d2}' -f $y, (($y + 1) % 100) } |
                 Sort-Object Name |
                 ForEach-Object { Write-Host ("    {0}   {1,7:N0}" -f $_.Name, $_.Count) }
        Write-Host "`n  on ${OneDay}: $(($dates | Where-Object { $_.ToString('yyyyMMdd') -eq $OneDay }).Count) voucher(s)"
        Write-Host "`n  ONE financial year  -> the collection is FY-scoped, not period-scoped."
        Write-Host "  EIGHT years         -> it really does serve the whole active period."
    }
}

# ---------------------------------------------------------------- PART B ---
if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }

# Single-quoted here-strings: $Date, $$Date and $$SysName must stay literal.
$filterLiteral = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>ScopeProbe</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVFROMDATE>__DAY__</SVFROMDATE><SVTODATE>__DAY__</SVTODATE></STATICVARIABLES><TDL><TDLMESSAGE><COLLECTION NAME="ScopeProbe"><TYPE>Voucher</TYPE><FETCH>DATE</FETCH><FILTER>ScopeLiteral</FILTER></COLLECTION><SYSTEM TYPE="Formulae" NAME="ScopeLiteral">$Date = $$Date:"1-Sep-2026"</SYSTEM></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>
'@

$filtersPlural = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>ScopeProbe</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVFROMDATE>__DAY__</SVFROMDATE><SVTODATE>__DAY__</SVTODATE></STATICVARIABLES><TDL><TDLMESSAGE><COLLECTION NAME="ScopeProbe"><TYPE>Voucher</TYPE><FETCH>DATE</FETCH><FILTERS>ScopeLiteral</FILTERS></COLLECTION><SYSTEM TYPE="Formulae" NAME="ScopeLiteral">$Date = $$Date:"1-Sep-2026"</SYSTEM></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>
'@

$dayBookAsData = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>Day Book</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVFROMDATE>__DAY__</SVFROMDATE><SVTODATE>__DAY__</SVTODATE></STATICVARIABLES></DESC></BODY></ENVELOPE>
'@

$trialBalanceAsData = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>Trial Balance</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVFROMDATE>20260401</SVFROMDATE><SVTODATE>__DAY__</SVTODATE></STATICVARIABLES></DESC></BODY></ENVELOPE>
'@

function Write-Probe([string]$Name, [string]$Xml) {
    $text = $Xml.Replace('__DAY__', $OneDay).Trim()
    [System.IO.File]::WriteAllText((Join-Path $Dir $Name), $text, [System.Text.Encoding]::ASCII)
    Write-Host "  $Name"
}

Write-Host "`n=== B. New probes -> $Dir ===" -ForegroundColor Cyan
Write-Probe '15-collection-filter-literal.xml'   $filterLiteral
Write-Probe '16-daybook-export-type-data.xml'    $dayBookAsData
Write-Probe '17-collection-filters-plural.xml'   $filtersPlural
Write-Probe '18-trialbalance-export-type-data.xml' $trialBalanceAsData

$candidates = @(
    (Join-Path $env:ProgramFiles 'Tally BigQuery Agent\cli\TallyAgent.Cli.exe'),
    (Join-Path $env:ProgramFiles 'Tally BigQuery Agent\TallyAgent.Cli.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Tally BigQuery Agent\cli\TallyAgent.Cli.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Tally BigQuery Agent\TallyAgent.Cli.exe')
)
$cli = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $cli) {
    Write-Host "`nTallyAgent.Cli.exe not found. Run:" -ForegroundColor Red
    Write-Host "  <path>\TallyAgent.Cli.exe capture-xml --envelope-dir `"$Dir`" --dump"
    return
}

Write-Host "`nRunning 15-18. Read the <VOUCHER> count; 1-Sep-2026 has 3.`n" -ForegroundColor Cyan
& $cli capture-xml --envelope-dir $Dir --dump
