param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $SkipBuild) {
        dotnet run --project Tests/Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
        dotnet build AnimSync/AnimSync.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $built = 'AnimSync/bin/Release/AnimSync/AnimSync.json'
    $archive = 'AnimSync/bin/Release/AnimSync/latest.zip'
    $manifest = Get-Content -LiteralPath $built -Raw | ConvertFrom-Json
    $version = $manifest.AssemblyVersion
    $tag = "v$version"
    $repositoryUrl = $manifest.RepoUrl.TrimEnd('/')
    $download = "$repositoryUrl/releases/download/$tag/AnimationSyncerPlus.zip"
    $entry = [ordered]@{
        Author = $manifest.Author
        Name = $manifest.Name
        Description = $manifest.Description
        Punchline = $manifest.Punchline
        InternalName = $manifest.InternalName
        AssemblyVersion = $version
        RepoUrl = $repositoryUrl
        IconUrl = $manifest.IconUrl
        ApplicableVersion = $manifest.ApplicableVersion
        Tags = @($manifest.Tags)
        DalamudApiLevel = $manifest.DalamudApiLevel
        IsHide = $false
        IsTestingExclusive = $false
        LastUpdated = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        DownloadLinkInstall = $download
        DownloadLinkTesting = $download
        DownloadLinkUpdate = $download
    }
    # Explicit array wrapping works in both Windows PowerShell 5 and PowerShell 7.
    $json = "[`n" + ($entry | ConvertTo-Json -Depth 10) + "`n]`n"
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'repo.json'), $json, [Text.UTF8Encoding]::new($false))
    New-Item -ItemType Directory -Path dist -Force | Out-Null
    Copy-Item -LiteralPath $archive -Destination dist/AnimationSyncerPlus.zip
    Write-Host "Prepared $tag"
    Write-Host 'Release asset: dist/AnimationSyncerPlus.zip'
    Write-Host "Repository index: repo.json ($version)"
    Write-Host 'No upload or publishing has been performed.'
} finally {
    Pop-Location
}
