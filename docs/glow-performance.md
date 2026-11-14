# Glow rendering performance

Version 0.8.1 replaces the animated vector strips and opacity masks with a cached capsule raster. The map of pixel positions, edge coverage, and glow falloff is rebuilt when the surface's size, layer, or effective DPI/scale changes. Animation updates palette values and reuses the pixel buffer and `WriteableBitmap`; each glow surface submits one `DrawImage`. The inner and outer layers remain separate from panel text and controls.

The 30 Hz animation cadence and existing effect behavior remain: Rainbow Wave's full spectrum, Neon Flow's cyan/pink colors and white core, inner wash, outer halo, and music-driven traveling highlights. The renderer targets a two-million-pixel budget and caps raster scale at 1.5 pixels per DIP, using linear scaling for larger display scales. These limits affect the soft glow rather than the sharpness of HUD text.

## Measured comparison

These are **offscreen CPU/raster elapsed timings, not GPU-utilization measurements**. They do not establish an onscreen GPU percentage, DWM cost, game FPS, or delivery of every frame at 30 Hz.

The same harness compared the v0.8.0 renderer with the final v0.8.1 candidate on one Windows build 26300 machine, .NET 10.0.14, WPF rendering tier 2. It reused one surface, canvas, and `RenderTargetBitmap`: 1276 × 66 DIP panel, 26 DIP margin on each side, 96 DPI, combined inner/outer diagnostic layer, intensity 0.75. Each case used four warmup frames and 45 measured frames. The deterministic animation timeline advances at 30 Hz, but benchmark renders run without frame pacing. Elapsed time includes dependency-property updates, layout, bitmap clearing, and raster rendering. No app services, visible windows, or real audio are used.

| Style / input | v0.8.0 p50 / p95, ms | v0.8.1 p50 / p95, ms | UI allocation p50, bytes/frame, before → after |
| --- | ---: | ---: | ---: |
| Rainbow Wave / calm | 14.54 / 16.47 | 3.34 / 3.68 | 411,416 → 1,272 |
| Rainbow Wave / synthetic pulse | 141.95 / 148.74 | 3.47 / 3.73 | 504,408 → 1,272 |
| Neon Flow / calm | 110.24 / 113.91 | 3.37 / 3.55 | 180,944 → 1,272 |
| Neon Flow / synthetic pulse | 188.26 / 200.55 | 3.40 / 3.54 | 202,800 → 1,272 |

This is one local run per variant, with the same settings and harness. It demonstrates less work in this fixture; real hardware and onscreen composition still need separate measurement. The existing 67 semantic glow checks also pass, covering the style distinctions, white core, layer boundaries, transparency, music response, and animation seams. These checks do not assert that every old and new pixel is identical.

## Run the optional benchmark

On Windows with the .NET 10 SDK, run from the repository root:

```powershell
dotnet run --project tests/GlimHUD.GlowChecks -c Release -- --benchmark-render artifacts/glow-performance/candidate.json
```

Normal glow checks run without `--benchmark-render`. The benchmark is opt-in and does not modify the running GlimHUD instance. JSON reports include local source and assembly paths for traceability; remove those fields before sharing a report publicly.

For an older renderer, the check project accepts the MSBuild property `GlowSurfaceSource`. This example requires the `v0.8.0` tag locally and keeps the current harness, substituting only the tagged renderer with separate intermediate and output directories:

```powershell
$baselineDirectory = Join-Path (Get-Location) 'artifacts/glow-performance/baseline'
New-Item -ItemType Directory -Path $baselineDirectory -Force | Out-Null
$baselineSource = Join-Path $baselineDirectory 'ReactiveGlowSurface.cs'
git show v0.8.0:src/GlimHUD/Controls/ReactiveGlowSurface.cs |
    Set-Content -LiteralPath $baselineSource -Encoding utf8
dotnet build tests/GlimHUD.GlowChecks -c Release `
    "-p:GlowSurfaceSource=$baselineSource" `
    "-p:BaseIntermediateOutputPath=$baselineDirectory/obj/" `
    "-p:BaseOutputPath=$baselineDirectory/bin/"
dotnet "$baselineDirectory/bin/Release/net10.0-windows/GlimHUD.GlowChecks.dll" `
    --benchmark-render "$baselineDirectory/report.json"
```

A source override does not switch the rest of the app to that historical version. Repeat both variants under the same display/runtime conditions and compare matching cases; benchmark reports from different setups are not interchangeable.
