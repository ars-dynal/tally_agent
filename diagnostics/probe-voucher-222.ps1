# ============================================================================
#  Where is the 39,000 revenue leg of DEPL/26-27/222?
#
#  Fetches the Day Book for the candidate days (SVCURRENTDATE - the only date
#  control that report honours) and reports, for the target voucher AND a
#  working item-invoice control on the same day:
#     - top-level ledger lines, with a Dr = Cr balance check
#     - whether ACCOUNTINGALLOCATIONS.LIST exists inside the inventory entry
#     - which ledger that nested block names, and for how much
#     - every LEDGERNAME anywhere in the voucher
#
#  One small request per day. Read-only: no TDL is injected, so no Tally
#  restart is needed afterwards.
# ============================================================================

$Target = 'DEPL/26-27/222'
$Days   = @('20260831','20260904')   # voucher date and entry date - both checked
$Dir    = 'C:\TallyRef\voucher-222'
$Fx     = 'C:\ProgramData\TallyBigQueryAgent\fixtures'

$ErrorActionPreference = 'Stop'

try {
    if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }
    [System.IO.File]::WriteAllText((Join-Path $Dir '.w'), 'x'); Remove-Item (Join-Path $Dir '.w') -Force
} catch {
    $Dir = Join-Path $env:LOCALAPPDATA 'TallyRef\voucher-222'
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    Write-Host "C:\TallyRef not writable - using $Dir" -ForegroundColor Yellow
}

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

# ---- locate the CLI (installed layout puts it in a cli\ subfolder) ---------
$cli = @(
    (Join-Path $env:ProgramFiles 'Tally BigQuery Agent\cli\TallyAgent.Cli.exe'),
    (Join-Path $env:ProgramFiles 'Tally BigQuery Agent\TallyAgent.Cli.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Tally BigQuery Agent\cli\TallyAgent.Cli.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Tally BigQuery Agent\TallyAgent.Cli.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $cli) {
    try {   # the service knows where it lives; the CLI sits beside or below it
        $svc = Get-CimInstance Win32_Service -Filter "Name='TallyBigQueryAgent'" -ErrorAction Stop
        if ($svc -and $svc.PathName) {
            $exe  = ($svc.PathName -replace '^"([^"]+)".*$', '$1') -replace '^(\S+).*$', '$1'
            $root = Split-Path $exe -Parent
            $cli  = @((Join-Path $root 'TallyAgent.Cli.exe'),
                      (Join-Path $root 'cli\TallyAgent.Cli.exe'),
                      (Join-Path (Split-Path $root -Parent) 'cli\TallyAgent.Cli.exe')) |
                    Where-Object { Test-Path $_ } | Select-Object -First 1
        }
    } catch { }
}

if (-not $cli) {
    Write-Host "`nTallyAgent.Cli.exe not found. Probes are written; run:" -ForegroundColor Red
    Write-Host "  <path>\TallyAgent.Cli.exe capture-xml --envelope-dir `"$Dir`" --dump"
    return
}

Write-Host "`nUsing $cli" -ForegroundColor Cyan
& $cli capture-xml --envelope-dir $Dir --dump

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
    $balanced = [math]::Abs($sum) -lt 0.005
    Write-Host ("  SUM of top-level ledger amounts: {0:N2}   {1}" -f $sum,
        $(if ($balanced) { '(balances)' } else { '<-- DOES NOT BALANCE' })) `
        -ForegroundColor $(if ($balanced) { 'Green' } else { 'Red' })

    $inv = $v.SelectNodes('.//ALLINVENTORYENTRIES.LIST')
    if ($inv.Count -eq 0) { $inv = $v.SelectNodes('.//INVENTORYENTRIES.LIST') }
    Write-Host "  inventory entries: $($inv.Count)"
    $nested = 0.0
    foreach ($i in $inv) {
        $item = $i.SelectSingleNode('STOCKITEMNAME'); $iamt = $i.SelectSingleNode('AMOUNT')
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
                $av = 0.0; if ($aa) { [double]::TryParse($aa.InnerText, [ref]$av) | Out-Null }
                $nested += $av
                Write-Host ("          {0,-43} {1,15:N2}" -f `
                    $(if ($an) { $an.InnerText } else { '?' }), $av) -ForegroundColor Green
            }
        }
    }
    if ($inv.Count -gt 0) {
        Write-Host ("  sum(top) + sum(all nested) = {0:N2}   {1}" -f ($sum + $nested),
            $(if ([math]::Abs($sum + $nested) -lt 0.005) { '<-- nested allocations close it EXACTLY' }
              else { '(does not close exactly)' })) `
            -ForegroundColor $(if ([math]::Abs($sum + $nested) -lt 0.005) { 'Green' } else { 'Yellow' })
    }

    $names = $v.SelectNodes('.//LEDGERNAME') | ForEach-Object { $_.InnerText } | Sort-Object -Unique
    Write-Host "  every LEDGERNAME anywhere in this voucher:"
    foreach ($n in $names) { Write-Host "      $n" }
}

Write-Host "`n================ analysis ================" -ForegroundColor Cyan
foreach ($d in $Days) {
    $f = Get-ChildItem (Join-Path $Fx "24-daybook-$d-*.xml") -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { Write-Host "`n$d : no fixture saved" -ForegroundColor Yellow; continue }

    $doc = New-Object System.Xml.XmlDocument
    $doc.Load($f.FullName)
    $vouchers = @($doc.SelectNodes('//VOUCHER'))
    Write-Host "`n$d : $($vouchers.Count) vouchers in $($f.Name)" -ForegroundColor Cyan

    $hit = @($vouchers | Where-Object {
        $n = $_.SelectSingleNode('VOUCHERNUMBER'); $n -and $n.InnerText.Trim() -eq $Target })
    if ($hit.Count -gt 0) { foreach ($h in $hit) { Show-Voucher $h "TARGET  $Target" } }
    else { Write-Host "  $Target is not on this day" -ForegroundColor Yellow }

    # Control: a DIFFERENT sales voucher on the same day that also has stock.
    $ctl = @($vouchers | Where-Object {
        $_.GetAttribute('VCHTYPE') -match 'Sales' -and
        $_.SelectSingleNode('VOUCHERNUMBER') -and
        $_.SelectSingleNode('VOUCHERNUMBER').InnerText.Trim() -ne $Target -and
        $_.SelectNodes('.//ALLINVENTORYENTRIES.LIST').Count -gt 0 }) | Select-Object -First 1
    if (-not $ctl) {
        $ctl = @($vouchers | Where-Object {
            $_.GetAttribute('VCHTYPE') -match 'Sales' -and
            $_.SelectSingleNode('VOUCHERNUMBER') -and
            $_.SelectSingleNode('VOUCHERNUMBER').InnerText.Trim() -ne $Target }) | Select-Object -First 1
    }
    if ($ctl) { Show-Voucher $ctl "CONTROL (working sales invoice, same day)" }
    else { Write-Host "  no control sales voucher on this day" -ForegroundColor Yellow }
}

Write-Host "`nRaw responses saved under $Fx" -ForegroundColor Cyan
Write-Host "The question: does the CONTROL carry the revenue leg in BOTH places?" -ForegroundColor Cyan
