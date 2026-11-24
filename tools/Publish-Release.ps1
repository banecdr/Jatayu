$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'Jatayu.csproj'
$distDirectory = Join-Path $repoRoot 'dist'
$stagingDirectory = Join-Path $distDirectory '.Jatayu-staging'
$releaseDirectory = Join-Path $distDirectory 'Jatayu'

New-Item -ItemType Directory -Force -Path $distDirectory | Out-Null
if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}

& dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true --output $stagingDirectory -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Release publish failed with exit code $LASTEXITCODE."
}

$executablePath = Join-Path $stagingDirectory 'Jatayu.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Published executable is missing: $executablePath"
}

$shaderDirectory = Join-Path $stagingDirectory 'assets\shaders'
$spirvFiles = @(Get-ChildItem -LiteralPath $shaderDirectory -Filter '*.spv' -File -Recurse)
if ($spirvFiles.Count -eq 0) {
    throw "No precompiled SPIR-V files were published under $shaderDirectory."
}

$looseShaders = @(Get-ChildItem -LiteralPath $shaderDirectory -File -Recurse | Where-Object {
    @('.vert', '.frag', '.comp', '.glsl') -contains $_.Extension.ToLowerInvariant()
})
if ($looseShaders.Count -gt 0) {
    throw "Loose GLSL source files were published: $($looseShaders.FullName -join ', ')"
}

$rootDlls = @(Get-ChildItem -LiteralPath $stagingDirectory -Filter '*.dll' -File)
if ($rootDlls.Count -gt 0) {
    throw "Unexpected DLL sidecars beside Jatayu.exe: $($rootDlls.Name -join ', ')"
}

$unexpectedRootFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -File | Where-Object {
    $_.Name -ne 'Jatayu.exe'
})
if ($unexpectedRootFiles.Count -gt 0) {
    throw "Unexpected files beside Jatayu.exe: $($unexpectedRootFiles.Name -join ', ')"
}

$worldPackageSource = Join-Path $repoRoot 'assets\generated\world.jpak'
$worldPackageOutput = Join-Path $stagingDirectory 'assets\packages\world.jpak'
if ((Test-Path -LiteralPath $worldPackageSource -PathType Leaf) -and
    -not (Test-Path -LiteralPath $worldPackageOutput -PathType Leaf)) {
    throw "The world package was not published to assets\packages\world.jpak."
}

if (Test-Path -LiteralPath $releaseDirectory) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}
Move-Item -LiteralPath $stagingDirectory -Destination $releaseDirectory

Write-Host "Release package ready: $releaseDirectory"
Write-Host "  Executable: Jatayu.exe (self-contained win-x64)"
Write-Host "  SPIR-V shaders: $($spirvFiles.Count)"
if (Test-Path -LiteralPath (Join-Path $releaseDirectory 'assets\packages\world.jpak')) {
    Write-Host "  World package: assets\packages\world.jpak"
}
