# ============================================================================
#  Does the Day Book report honour SVFROMDATE?
#
#  PART A costs Tally NOTHING. The guard that fired prints the date range Tally
#  actually served; that line is already in the agent log.
#
#  PART B is four small probes that settle it independently. They are sized to
#  stay small EVEN IF the hypothesis is right: every TO date sits within a week
#  of a financial-year start, so the worst case is ~7 days of Day Book (~28 MB)
#  rather than the ~255 MB a request ending 3-Jun-2026 would return.
# ============================================================================

$LogDir = 'C:\ProgramData\TallyBigQueryAgent\Logs'
$Dir    = 'C:\TallyRef\voucher-scope-3'

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- PART A ---
Write-Host "`n=== A. What did the guard actually see? (no Tally request) ===" -ForegroundColor Cyan
if (-not (Test-Path $LogDir)) {
    Write-Host "  $LogDir not found - skipping." -ForegroundColor Yellow
} else {
    $hits = Select-String -Path (Join-Path $LogDir '*.log') -Pattern 'was not honoured' `
                          -ErrorAction SilentlyContinue | Select-Object -Last 5
    if (-not $hits) {
        Write-Host "  No 'was not honoured' line found. Widening to any served-range mention..." -ForegroundColor Yellow
        $hits = Select-String -Path (Join-Path $LogDir '*.log') -Pattern 'outside the requested range|out-of-window' `
                              -ErrorAction SilentlyContinue | Select-Object -Last 5
    }
    if ($hits) {
        foreach ($h in $hits) {
            Write-Host "`n  $($h.Filename):$($h.LineNumber)" -ForegroundColor DarkGray
            Write-Host "  $($h.Line.Trim())" -ForegroundColor Green
        }
        Write-Host "`n  The 'dated X..Y' pair is the answer:"
        Write-Host "    starts 2026-04-01  -> SVFROMDATE ignored, served from the FY start"
        Write-Host "    starts 2019-04-01  -> served from the BOOKS start, not the FY"
        Write-Host "    starts 2026-05-04  -> the window WAS honoured and something else is wrong"
    } else {
        Write-Host "  Nothing matched - fall through to Part B." -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------- PART B ---
if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }

$report = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>__REPORT__</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVFROMDATE>__FROM__</SVFROMDATE><SVTODATE>__TO__</SVTODATE></STATICVARIABLES></DESC></BODY></ENVELOPE>
'@

$collectionRange = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Collection</TYPE><ID>RangeProbe</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVFROMDATE>__FROM__</SVFROMDATE><SVTODATE>__TO__</SVTODATE></STATICVARIABLES><TDL><TDLMESSAGE><COLLECTION NAME="RangeProbe"><TYPE>Voucher</TYPE><FETCH>DATE</FETCH><FILTER>RangeLiteral</FILTER></COLLECTION><SYSTEM TYPE="Formulae" NAME="RangeLiteral">$Date &gt;= $$Date:"__DFROM__" AND $Date &lt;= $$Date:"__DTO__"</SYSTEM></TDLMESSAGE></TDL></DESC></BODY></ENVELOPE>
'@

function Write-Probe($Name, $Xml, $Report, $From, $To, $DFrom, $DTo) {
    $t = $Xml.Replace('__REPORT__', $Report).Replace('__FROM__', $From).Replace('__TO__', $To)
    if ($DFrom) { $t = $t.Replace('__DFROM__', $DFrom).Replace('__DTO__', $DTo) }
    [System.IO.File]::WriteAllText((Join-Path $Dir $Name), $t.Trim(), [System.Text.Encoding]::ASCII)
    Write-Host ("  {0}  {1}..{2}" -f $Name.PadRight(40), $From, $To)
}

Write-Host "`n=== B. Probes -> $Dir ===" -ForegroundColor Cyan
# 19 vs 20 is the decisive pair: same TO, different FROM.
#   identical results -> SVFROMDATE is ignored.
Write-Probe '19-daybook-from-05-to-07.xml'  $report 'Day Book' '20260405' '20260407'
Write-Probe '20-daybook-from-01-to-07.xml'  $report 'Day Book' '20260401' '20260407'
# 21: previous financial year. If the start follows SVTODATE's FY, this returns
#     2025-04-01.., not 2026-04-01.. - that distinguishes FY-start from a fixed date.
Write-Probe '21-daybook-prevfy-to-07.xml'   $report 'Day Book' '20250405' '20250407'
# 22: mechanism (b) - a literal-date RANGE filter, extending probe 15's single date.
Write-Probe '22-collection-literal-range.xml' $collectionRange $null '20260405' '20260407' '5-Apr-2026' '7-Apr-2026'

$cli = @(
    (Join-Path $env:ProgramFiles 'Tally BigQuery Agent\cli\TallyAgent.Cli.exe'),
    (Join-Path $env:ProgramFiles 'Tally BigQuery Agent\TallyAgent.Cli.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cli) {
    Write-Host "`nTallyAgent.Cli.exe not found. Run:" -ForegroundColor Red
    Write-Host "  <path>\TallyAgent.Cli.exe capture-xml --envelope-dir `"$Dir`" --dump"
    return
}

Write-Host "`nRunning 19-22. Compare the DATE range of 19 against 20.`n" -ForegroundColor Cyan
& $cli capture-xml --envelope-dir $Dir --dump

# Report the actual date extent of each saved response - the histogram gives
# counts, not dates, and dates are what this question turns on.
Write-Host "`n=== date extent of each response ===" -ForegroundColor Cyan
foreach ($n in @('19-daybook-from-05-to-07','20-daybook-from-01-to-07','21-daybook-prevfy-to-07','22-collection-literal-range')) {
    $f = Get-ChildItem "C:\ProgramData\TallyBigQueryAgent\fixtures\$n-*.xml" -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { Write-Host ("  {0}  (no fixture)" -f $n.PadRight(32)); continue }
    $txt = [System.IO.File]::ReadAllText($f.FullName)
    # [^<]* not [^<]+ : an empty <DATE/> must still be counted, not skipped.
    $vals = [regex]::Matches($txt, '<DATE[^>]*>([^<]*)</DATE>') | ForEach-Object { $_.Groups[1].Value.Trim() } | Where-Object { $_ }
    if (-not $vals) { Write-Host ("  {0}  0 dated vouchers" -f $n.PadRight(32)); continue }
    # [string[]] is REQUIRED. Passed an Object[], the multi-format overload does
    # not bind and TryParseExact returns False for everything - silently, with no
    # exception. Same shape of bug as the rest of this investigation.
    $fmts = [string[]]@('yyyyMMdd','d-MMM-yyyy','dd-MMM-yyyy','d-MMM-yy','dd-MMM-yy','yyyy-MM-dd')
    $ds = foreach ($v in $vals) {
        $d = [datetime]::MinValue
        if ([datetime]::TryParseExact($v, $fmts, [Globalization.CultureInfo]::InvariantCulture,
                                      [Globalization.DateTimeStyles]::None, [ref]$d)) { $d }
        elseif ([datetime]::TryParse($v, [ref]$d)) { $d }
    }
    if ($ds) {
        Write-Host ("  {0}  {1,6:N0} dated   {2}  ..  {3}" -f $n.PadRight(32), $ds.Count,
                    ($ds | Measure-Object -Minimum).Minimum.ToString('yyyy-MM-dd'),
                    ($ds | Measure-Object -Maximum).Maximum.ToString('yyyy-MM-dd')) -ForegroundColor Green
    } else {
        Write-Host ("  {0}  unparsed sample: {1}" -f $n.PadRight(32), ($vals | Select-Object -First 3))
    }
}
