$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'Jatayu.csproj'
$distDirectory = Join-Path $repoRoot 'dist'
$stagingDirectory = Join-Path $distDirectory '.Jatayu-linux-x64-staging'
$releaseDirectory = Join-Path $distDirectory 'Jatayu-linux-x64'

New-Item -ItemType Directory -Force -Path $distDirectory | Out-Null
if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}

& dotnet publish $projectPath --configuration Release --runtime linux-x64 --self-contained true --output $stagingDirectory -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Linux release publish failed with exit code $LASTEXITCODE."
}

$executablePath = Join-Path $stagingDirectory 'Jatayu'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Published Linux executable is missing: $executablePath"
}

$stream = [System.IO.File]::OpenRead($executablePath)
try {
    $header = New-Object byte[] 20
    if ($stream.Read($header, 0, $header.Length) -ne $header.Length -or
        $header[0] -ne 0x7f -or $header[1] -ne 0x45 -or $header[2] -ne 0x4c -or $header[3] -ne 0x46 -or
        $header[4] -ne 2 -or $header[5] -ne 1 -or $header[18] -ne 0x3e -or $header[19] -ne 0) {
        throw "Published executable is not a Linux x64 ELF binary: $executablePath"
    }
}
finally {
    $stream.Dispose()
}

$shaderDirectory = Join-Path $stagingDirectory 'assets/shaders'
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
    throw "Unexpected DLL sidecars beside Jatayu: $($rootDlls.Name -join ', ')"
}

$unexpectedRootFiles = @(Get-ChildItem -LiteralPath $stagingDirectory -File | Where-Object {
    $_.Name -ne 'Jatayu'
})
if ($unexpectedRootFiles.Count -gt 0) {
    throw "Unexpected files beside Jatayu: $($unexpectedRootFiles.Name -join ', ')"
}

$worldPackageSource = Join-Path $repoRoot 'assets/generated/world.jpak'
$worldPackageOutput = Join-Path $stagingDirectory 'assets/packages/world.jpak'
if ((Test-Path -LiteralPath $worldPackageSource -PathType Leaf) -and
    -not (Test-Path -LiteralPath $worldPackageOutput -PathType Leaf)) {
    throw "The world package was not published to assets/packages/world.jpak."
}

if ($env:OS -ne 'Windows_NT' -and (Get-Command chmod -ErrorAction SilentlyContinue)) {
    & chmod +x $executablePath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to mark the Linux executable as executable."
    }
}

if (Test-Path -LiteralPath $releaseDirectory) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}
Move-Item -LiteralPath $stagingDirectory -Destination $releaseDirectory

Write-Host "Linux release package ready: $releaseDirectory"
Write-Host "  Executable: Jatayu (self-contained linux-x64)"
Write-Host "  SPIR-V shaders: $($spirvFiles.Count)"
if (Test-Path -LiteralPath (Join-Path $releaseDirectory 'assets/packages/world.jpak')) {
    Write-Host "  World package: assets/packages/world.jpak"
}
