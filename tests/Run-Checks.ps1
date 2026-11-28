$ErrorActionPreference = 'Stop'

$checks = @('Recording', 'WordTiming', 'SongSync', 'SceneEditor', 'LyricsStudio', 'GraphemeRendering', 'YouTubeTrack',
    'YouTubeBridge', 'VisualizerMotion', 'NeonRendering', 'FullNeon', 'GlitchRendering')
foreach ($check in $checks) {
    $project = Join-Path $PSScriptRoot "$check/$check.csproj"
    Write-Host "Running $check"
    dotnet run --project $project -c Release
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
