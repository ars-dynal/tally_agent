# ============================================================================
#  Tally voucher-scope probes  —  paste this whole block into PowerShell
#
#  Answers one question: what does THIS Tally actually scope vouchers by?
#  All five probes fetch DATE only, or cover a single day/month, so together
#  they cost Tally far less than ONE of the 85 windows that stalled it.
# ============================================================================

# ---- DATES.  Change these if 1-Sep-2026 has no vouchers. -------------------
$OneDayFrom = '20260901'   # probes 10, 11, 12 — a single day with vouchers
$OneDayTo   = '20260901'
$MonthFrom  = '20260801'   # probe 13 — one month
$MonthTo    = '20260831'
$FullFrom   = '20190401'   # probe 14 — the whole period, DATE only
$FullTo     = '20270331'

$Dir = 'C:\TallyRef\voucher-scope'
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'

# Fall back to a writable folder rather than failing the paste outright.
try {
    if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }
    [System.IO.File]::WriteAllText((Join-Path $Dir '.writetest'), 'x')
    Remove-Item (Join-Path $Dir '.writetest') -Force
} catch {
    $Dir = Join-Path $env:LOCALAPPDATA 'TallyRef\voucher-scope'
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    Write-Host "C:\TallyRef was not writable - using $Dir instead." -ForegroundColor Yellow
}

# Single-quoted here-strings: $Date and $$SysName must stay literal.
# __FROM__ / __TO__ are substituted below; {{COMPANY}} is substituted by the
# CLI from config.json.

$collectionNoFilter = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>ScopeProbe</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVFROMDATE>__FROM__</SVFROMDATE><SVTODATE>__TO__</SVTODATE></STATICVARIABLES><TDL><TDLMESSAGE><COLLECTION NAME="ScopeProbe"><TYPE>Voucher</TYPE><FETCH>DATE</FETCH></COLLECTION></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>
'@

$collectionDateFilter = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>ScopeProbe</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVFROMDATE>__FROM__</SVFROMDATE><SVTODATE>__TO__</SVTODATE></STATICVARIABLES><TDL><TDLMESSAGE><COLLECTION NAME="ScopeProbe"><TYPE>Voucher</TYPE><FETCH>DATE</FETCH><FILTER>ScopeProbeDateFilter</FILTER></COLLECTION><SYSTEM TYPE="Formulae" NAME="ScopeProbeDateFilter">$Date &gt;= ##SVFromDate AND $Date &lt;= ##SVToDate</SYSTEM></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>
'@

$dayBookReport = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export Data</TALLYREQUEST></HEADER><BODY><EXPORTDATA><REQUESTDESC><REPORTNAME>Day Book</REPORTNAME><STATICVARIABLES><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVFROMDATE>__FROM__</SVFROMDATE><SVTODATE>__TO__</SVTODATE><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT></STATICVARIABLES></REQUESTDESC></EXPORTDATA></BODY></ENVELOPE>
'@

function Write-Probe([string]$Name, [string]$Xml, [string]$From, [string]$To) {
    $text = $Xml.Replace('__FROM__', $From).Replace('__TO__', $To).Trim()
    [System.IO.File]::WriteAllText((Join-Path $Dir $Name), $text, [System.Text.Encoding]::ASCII)
    Write-Host ("  {0}   {1}..{2}" -f $Name.PadRight(38), $From, $To)
}

Write-Host "`nWriting probes to $Dir" -ForegroundColor Cyan
Write-Probe '10-collection-oneday-nofilter.xml'   $collectionNoFilter   $OneDayFrom $OneDayTo
Write-Probe '11-collection-oneday-datefilter.xml' $collectionDateFilter $OneDayFrom $OneDayTo
Write-Probe '12-daybook-report-oneday.xml'        $dayBookReport        $OneDayFrom $OneDayTo
Write-Probe '13-daybook-report-onemonth.xml'      $dayBookReport        $MonthFrom  $MonthTo
Write-Probe '14-collection-fullrange-dateonly.xml' $collectionNoFilter  $FullFrom   $FullTo

# ---- locate the CLI the installer put down --------------------------------
# Ordered cheapest-first. The registered service's own path is the precise
# answer; a recursive sweep of Program Files is NOT used - it takes minutes.
$candidates = @(
    (Join-Path $env:ProgramFiles 'Tally BigQuery Agent\TallyAgent.Cli.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Tally BigQuery Agent\TallyAgent.Cli.exe'),
    (Join-Path $env:ProgramData 'TallyBigQueryAgent\TallyAgent.Cli.exe')
)
$cli = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $cli) {
    # Ask Windows where the service lives; the CLI sits beside it.
    try {
        $svc = Get-CimInstance Win32_Service -Filter "Name='TallyBigQueryAgent'" -ErrorAction Stop
        if ($svc -and $svc.PathName) {
            $exe = ($svc.PathName -replace '^"([^"]+)".*$', '$1') -replace '^(\S+).*$', '$1'
            $guess = Join-Path (Split-Path $exe -Parent) 'TallyAgent.Cli.exe'
            if (Test-Path $guess) { $cli = $guess }
        }
    } catch { }
}

if (-not $cli) {
    Write-Host "`nThe probe files are written, but TallyAgent.Cli.exe was not found." -ForegroundColor Red
    Write-Host "Find it and run:" -ForegroundColor Red
    Write-Host "  <path>\TallyAgent.Cli.exe capture-xml --envelope-dir `"$Dir`" --dump"
    return
}

Write-Host "`nUsing $cli" -ForegroundColor Cyan
Write-Host "Reading the <VOUCHER> count in each element histogram is the whole answer.`n"

& $cli capture-xml --envelope-dir $Dir --dump

Write-Host "`nRaw responses saved under $env:ProgramData\TallyBigQueryAgent\fixtures" -ForegroundColor Cyan
