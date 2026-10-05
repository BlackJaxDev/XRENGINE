param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [string]$OutputPath = $env:GITHUB_STEP_SUMMARY
)

$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) {
    Write-Output 'No TRX test report is available.'
    exit 0
}

$results = @(foreach ($file in $files) {
    [xml]$report = Get-Content -LiteralPath $file.FullName -Raw
    $report.TestRun.Results.UnitTestResult
})
$passed = @($results | Where-Object outcome -eq 'Passed').Count
$failed = @($results | Where-Object outcome -eq 'Failed').Count
$notExecuted = @($results | Where-Object outcome -eq 'NotExecuted')
$other = $results.Count - $passed - $failed - $notExecuted.Count
$lines = @(
    '## Test results',
    '',
    '| Reported cases | Passed | Failed | Not executed | Other outcomes |',
    '| ---: | ---: | ---: | ---: | ---: |',
    "| $($results.Count) | $passed | $failed | $($notExecuted.Count) | $other |",
    '',
    'Counts include every case in the TRX report. The console summary can omit some cases that did not run.',
    ''
)
if ($notExecuted.Count -gt 0) {
    $lines += @('### Tests that did not run', '', '| Count | Reported reason |', '| ---: | --- |')
    $reasons = $notExecuted | ForEach-Object {
        $message = [string]$_.Output.ErrorInfo.Message
        if ([string]::IsNullOrWhiteSpace($message)) { $message = [string]$_.Output.StdOut }
        if ([string]::IsNullOrWhiteSpace($message)) { $message = 'No reason was recorded.' }
        ($message -split '\r?\n')[0].Trim()
    } | Group-Object | Sort-Object Count -Descending
    foreach ($reason in $reasons) {
        $label = $reason.Name.Replace('|', '\|').Replace('<', '&lt;').Replace('>', '&gt;')
        $lines += "| $($reason.Count) | $label |"
    }
    $lines += ''
}
$summary = $lines -join "`n"
Write-Output $summary
if ($OutputPath) { Add-Content -LiteralPath $OutputPath -Value $summary -Encoding utf8 }
