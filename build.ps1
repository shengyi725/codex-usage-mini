param([switch]$Test)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$wpf = Join-Path $framework 'WPF'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler was not found.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $wpf $_) }
$sources = @('UsageModel.cs','CodexClient.cs','Subscription.cs','Appearance.cs','History.cs','AllowanceHistory.cs','SettingsWindow.cs','HistoryWindow.cs','AllowanceView.cs','App.cs') | ForEach-Object { Join-Path $PSScriptRoot "src\$_" }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror+ ('/out:' + (Join-Path $output 'CodexUsageMini.exe')) ('/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')) ('/win32icon:' + (Join-Path $PSScriptRoot 'assets\codex-usage-mini-horizontal-bold.ico')) @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\App.config') -Destination (Join-Path $output 'CodexUsageMini.exe.config') -Force
Write-Output "Built $output\CodexUsageMini.exe"
if ($Test) {
    & $compiler /nologo /target:exe /warnaserror+ ('/out:' + (Join-Path $output 'CoreTests.exe')) @references (Join-Path $PSScriptRoot 'src\UsageModel.cs') (Join-Path $PSScriptRoot 'src\CodexClient.cs') (Join-Path $PSScriptRoot 'src\Subscription.cs') (Join-Path $PSScriptRoot 'src\Appearance.cs') (Join-Path $PSScriptRoot 'src\History.cs') (Join-Path $PSScriptRoot 'src\AllowanceHistory.cs') (Join-Path $PSScriptRoot 'tests\AllowanceTests.cs') (Join-Path $PSScriptRoot 'tests\CoreTests.cs') (Join-Path $PSScriptRoot 'tests\HistoryTests.cs') (Join-Path $PSScriptRoot 'tests\SubscriptionTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $output 'CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
