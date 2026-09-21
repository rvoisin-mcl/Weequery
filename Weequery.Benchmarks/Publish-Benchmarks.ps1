<#
.SYNOPSIS
    Assembles BENCHMARKS.md from the results of the last benchmark run.

.DESCRIPTION
    BenchmarkDotNet writes one GitHub flavoured markdown report per benchmark class, each repeating the same
    machine header above its table. This takes the header once, the tables in the order the template asks for
    them, and puts them into the prose in BENCHMARKS.template.md.

    Run the benchmarks first:

        dotnet run -c Release --project Weequery.Benchmarks

    then this. Two steps rather than one on purpose: a run takes about fifteen minutes and should be repeatable
    without also rewriting a checked in file, and the file should be rewritable from a run you already trust.

.PARAMETER Results
    Where BenchmarkDotNet left its reports. Defaults to the artifacts folder at the repository root, which is
    where it writes when the run is started from there.

.PARAMETER Template
    The prose, with a {{ClassName}} placeholder wherever a table belongs and {{Environment}} for the machine.

.PARAMETER Output
    Where to write the assembled file.

.EXAMPLE
    .\Weequery.Benchmarks\Publish-Benchmarks.ps1
#>
[CmdletBinding()]
param(
    [string] $Results = (Join-Path $PSScriptRoot '..\BenchmarkDotNet.Artifacts\results'),
    [string] $Template = (Join-Path $PSScriptRoot 'BENCHMARKS.template.md'),
    [string] $Output = (Join-Path $PSScriptRoot '..\BENCHMARKS.md')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Results)) {
    throw "No results in '$Results'. Run the benchmarks first: dotnet run -c Release --project Weequery.Benchmarks"
}

if (-not (Test-Path $Template)) { throw "No template at '$Template'" }

# BenchmarkDotNet escapes the quotes it puts around a name containing spaces, which every descriptive name has.
# They are apostrophes, and reading the raw markdown should not require decoding them.
function Expand-Entities {
    param([string] $Text)

    return $Text.Replace('&#39;', "'").Replace('&quot;', '"').Replace('&amp;', '&')
}

# Everything between the first pair of fences: the CPU, the OS, the runtime and the job. The part that makes an
# absolute figure mean something, and the reason none of these tables travels without it.
function Get-Environment {
    param([string] $Path)

    $lines = Get-Content -LiteralPath $Path -Encoding UTF8
    $fences = @($lines | Select-String -SimpleMatch '```' | Select-Object -ExpandProperty LineNumber)

    if ($fences.Count -lt 2) { throw "No environment block in '$Path'" }

    $block = $lines[$fences[0]..($fences[1] - 2)]

    return (($block -join "`r`n").Trim())
}

# The table, and nothing else in the file. Taking the rows by their leading pipe rather than by position, so a
# report that grew a section above or below the table still yields the right thing.
function Get-Table {
    param([string] $Path)

    $rows = @(Get-Content -LiteralPath $Path -Encoding UTF8 | Where-Object { $_.TrimStart().StartsWith('|') })

    if ($rows.Count -lt 3) { throw "No table in '$Path'" }

    return (Expand-Entities ($rows -join "`r`n"))
}

$reports = @{}
foreach ($report in Get-ChildItem -LiteralPath $Results -Filter '*-report-github.md') {
    # Weequery.Benchmarks.ParsingBenchmarks-report-github.md -> ParsingBenchmarks
    $class = $report.Name -replace '-report-github\.md$', '' -replace '^.*\.', ''
    $reports[$class] = $report.FullName
}

if ($reports.Count -eq 0) { throw "No reports in '$Results'" }

$assembled = (Get-Content -LiteralPath $Template -Raw -Encoding UTF8)

# The date, and the machine -- taken from whichever report came first, since every run writes
# the same header into all of them
$assembled = $assembled.Replace('{{Generated}}', (Get-Date -Format 'yyyy-MM-dd'))
$assembled = $assembled.Replace('{{Environment}}', (Get-Environment ($reports.Values | Select-Object -First 1)))

foreach ($class in $reports.Keys) {
    $placeholder = '{{' + $class + '}}'

    if (-not $assembled.Contains($placeholder)) {
        Write-Warning "$class was measured but the template has no $placeholder, so its table is not published"
        continue
    }

    $assembled = $assembled.Replace($placeholder, (Get-Table $reports[$class]))
}

# A placeholder left standing means a class the template expects was not in this run, which would publish the
# word {{Something}} as though it were a result. Refuse rather than write it.
$missing = @([regex]::Matches($assembled, '\{\{[A-Za-z]+\}\}') | Select-Object -ExpandProperty Value -Unique)
if ($missing.Count -gt 0) {
    throw "No results for $($missing -join ', '). Run the whole set, not a filtered one, before publishing."
}

# BenchmarkDotNet writes its timings in microseconds, with the Greek small letter mu, and nothing else in its
# output leaves ASCII. Text in this repository stays inside ASCII unless the character is the thing being
# described, and a unit is not, so it is folded on the way through. "us" is what the rest of the prose says.
$assembled = $assembled.Replace([char]0x03BC, 'u').Replace([char]0x00B5, 'u')

# Anything else that is not ASCII arrived from a report rather than from the template, which means the tool
# started emitting something new. Name it rather than publish it, the same way a left standing placeholder is
# refused above: a character nobody chose is not a thing to find out about from a rendered page.
$exotic = @([regex]::Matches($assembled, '[^\x00-\x7F]') | Select-Object -ExpandProperty Value -Unique)
if ($exotic.Count -gt 0) {
    $named = ($exotic | ForEach-Object { 'U+{0:X4}' -f [int][char]$_ }) -join ', '

    throw "The reports contain characters outside ASCII that this script does not fold: $named. Add them above, or decide they belong."
}

# Not Set-Content: Windows PowerShell writes a byte order mark with -Encoding utf8, and no other markdown in
# this repository carries one. WriteAllText with the encoding told not to emit one is the way to say so.
$path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
[System.IO.File]::WriteAllText($path, $assembled, (New-Object System.Text.UTF8Encoding $false))

Write-Host "Wrote $path from $($reports.Count) report(s)"
