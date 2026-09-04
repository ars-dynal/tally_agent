# ============================================================================
#  Where is the 39,000 revenue leg of DEPL/26-27/222?
#
#  Fetches the Day Book for the candidate days (SVCURRENTDATE - the only date
#  control that report honours), then reports, for the target voucher AND a
#  working control on the same day:
#     - top-level ledger lines and their amounts
#     - whether ACCOUNTINGALLOCATIONS.LIST exists inside the inventory entry
#     - which ledger that nested block names, and for how much
#
#  Each day is one small request (12.6 MB on the heaviest day observed).
# ============================================================================

$Target = 'DEPL/26-27/222'
$Days   = @('20260831','20260904')   # voucher date and entry date - both checked
$Dir    = 'C:\TallyRef\voucher-222'
$Fx     = 'C:\ProgramData\TallyBigQueryAgent\fixtures'

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }

$dayBook = @'
<ENVELOPE><HEADER><VERSION>1</VERSION><TALLYREQUEST>Export</TALLYREQUEST><TYPE>Data</TYPE><ID>Day Book</ID></HEADER><BODY><DESC><STATICVARIABLES><SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT><SVCURRENTCOMPANY>{{COMPANY}}</SVCURRENTCOMPANY><SVCURRENTDATE>__DAY__</SVCURRENTDATE><SVFROMDATE>__DAY__</SVFROMDATE><SVTODATE>__DAY__</SVTODATE></STATICVARIABLES></DESC></BODY></ENVELOPE>
'@

Write-Host "`nWriting probes to $Dir" -ForegroundColor Cyan
foreach ($d in $Days) {
    $name = "24-daybook-$d.xml"
    [System.IO.File]::WriteAllText((Join-Path $Dir $name),
        $dayBook.Replace('__DAY__', $d).Trim(), [System.Text.Encoding]::ASCII)
    Write-Host "  $name"
}

$cli = @(
    (Join-Path $env:ProgramFiles 'Tally BigQuery Agent\cli\TallyAgent.Cli.exe'),
    (Join-Path $env:ProgramFiles 'Tally BigQuery Agent\TallyAgent.Cli.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cli) { Write-Host "TallyAgent.Cli.exe not found." -ForegroundColor Red; return }

Write-Host "`nFetching..." -ForegroundColor Cyan
& $cli capture-xml --envelope-dir $Dir --dump | Out-Null

# ---------------------------------------------------------------------------
function Show-Voucher($v, $label) {
    Write-Host "`n--- $label" -ForegroundColor Yellow
    $num  = $v.SelectSingleNode('VOUCHERNUMBER')
    $date = $v.SelectSingleNode('DATE')
    Write-Host ("  number={0}  date={1}  VCHTYPE={2}" -f `
        $(if ($num) { $num.InnerText } else { '?' }),
        $(if ($date) { $date.InnerText } else { '?' }),
        $v.GetAttribute('VCHTYPE'))

    $led = $v.SelectNodes('.//ALLLEDGERENTRIES.LIST')
    if ($led.Count -eq 0) { $led = $v.SelectNodes('.//LEDGERENTRIES.LIST') }
    Write-Host "  top-level ledger lines: $($led.Count)"
    $sum = 0.0
    foreach ($l in $led) {
        $n = $l.SelectSingleNode('LEDGERNAME'); $a = $l.SelectSingleNode('AMOUNT')
        $amt = 0.0; if ($a) { [double]::TryParse($a.InnerText, [ref]$amt) | Out-Null }
        $sum += $amt
        Write-Host ("      {0,-45} {1,15:N2}" -f $(if ($n) { $n.InnerText } else { '?' }), $amt)
    }
    Write-Host ("  SUM of top-level ledger amounts: {0:N2}   {1}" -f $sum,
        $(if ([math]::Abs($sum) -lt 0.01) { '(balances)' } else { '<-- DOES NOT BALANCE' })) `
        -ForegroundColor $(if ([math]::Abs($sum) -lt 0.01) { 'Green' } else { 'Red' })

    $inv = $v.SelectNodes('.//ALLINVENTORYENTRIES.LIST')
    if ($inv.Count -eq 0) { $inv = $v.SelectNodes('.//INVENTORYENTRIES.LIST') }
    Write-Host "  inventory entries: $($inv.Count)"
    foreach ($i in $inv) {
        $item = $i.SelectSingleNode('STOCKITEMNAME')
        $iamt = $i.SelectSingleNode('AMOUNT')
        Write-Host ("      item {0}  amount {1}" -f `
            $(if ($item) { $item.InnerText } else { '?' }),
            $(if ($iamt) { $iamt.InnerText } else { '?' }))
        $acc = $i.SelectNodes('.//ACCOUNTINGALLOCATIONS.LIST')
        if ($acc.Count -eq 0) {
            Write-Host "        ACCOUNTINGALLOCATIONS.LIST: ABSENT" -ForegroundColor Red
        } else {
            Write-Host "        ACCOUNTINGALLOCATIONS.LIST: $($acc.Count)" -ForegroundColor Green
            foreach ($a in $acc) {
                $an = $a.SelectSingleNode('LEDGERNAME'); $aa = $a.SelectSingleNode('AMOUNT')
                Write-Host ("          {0,-43} {1,15}" -f `
                    $(if ($an) { $an.InnerText } else { '?' }),
                    $(if ($aa) { $aa.InnerText } else { '?' })) -ForegroundColor Green
            }
        }
    }
    # Anything else carrying a LEDGERNAME that the two loops above missed.
    $allLedgerNames = $v.SelectNodes('.//LEDGERNAME') | ForEach-Object { $_.InnerText } | Sort-Object -Unique
    Write-Host "  every LEDGERNAME anywhere in this voucher:"
    foreach ($n in $allLedgerNames) { Write-Host "      $n" }
}

Write-Host "`n=== analysis ===" -ForegroundColor Cyan
foreach ($d in $Days) {
    $f = Get-ChildItem (Join-Path $Fx "24-daybook-$d-*.xml") -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { Write-Host "`n$d : no fixture" -ForegroundColor Yellow; continue }

    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $false
    $doc.Load($f.FullName)
    $vouchers = $doc.SelectNodes('//VOUCHER')
    Write-Host "`n$d : $($vouchers.Count) vouchers in $($f.Name)" -ForegroundColor Cyan

    $hit = $vouchers | Where-Object {
        $n = $_.SelectSingleNode('VOUCHERNUMBER'); $n -and $n.InnerText.Trim() -eq $Target }
    if ($hit) { foreach ($h in $hit) { Show-Voucher $h "TARGET  $Target" } }
    else { Write-Host "  $Target not on this day" }

    # Control: another sales voucher on the same day that is NOT the target.
    $ctl = $vouchers | Where-Object {
        $_.GetAttribute('VCHTYPE') -match 'Sales' -and
        (($_.SelectSingleNode('VOUCHERNUMBER')) -and $_.SelectSingleNode('VOUCHERNUMBER').InnerText.Trim() -ne $Target)
    } | Select-Object -First 1
    if ($ctl) { Show-Voucher $ctl "CONTROL (working sales invoice, same day)" }
    else { Write-Host "  no control sales voucher on this day" }
}

Write-Host "`nRaw responses under $Fx" -ForegroundColor Cyan
