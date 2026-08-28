$ErrorActionPreference = 'Stop'
$env:WORKPARCEL_RUN_REAL_DESK_TEST = '1'
dotnet build (Join-Path $PSScriptRoot 'DeskMemoryDisposableWindow\DeskMemoryDisposableWindow.csproj')
dotnet test (Join-Path $PSScriptRoot '..\tests\WorkParcel.Tests\WorkParcel.Tests.csproj') --no-restore --filter 'FullyQualifiedName~RealDeskMemoryManualTests'
