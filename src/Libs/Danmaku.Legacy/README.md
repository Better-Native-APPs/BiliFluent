# Danmaku.Legacy (vendored)

- Origin: https://github.com/Richasy/danmaku-kernel/tree/4d0f8691f08f1e143294402477e72306a1b38b6f/src/Danmaku.Legacy
- Commit: `4d0f8691f08f1e143294402477e72306a1b38b6f` (recorded in the `Danmaku.Legacy` `0.0.1-preview6` NuGet package).
- License: MIT; the original `LICENSE.txt` and source headers are preserved.

The only source-code patch removes `GC.TryStartNoGCRegion(50_000_000, true)`
and its paired `GC.EndNoGCRegion()` cleanup from `DanmakuRender.OnCanvasDraw`.
Entering and exiting a NoGC region every frame causes repeated GC suspension
and commits large heap budgets. Rendering and exception handling are unchanged;
all other C# files are unmodified upstream copies.

The project targets the application's .NET/Windows framework and architectures.
Dependencies use `src/Directory.Packages.props`, including the unchanged NuGet
`Danmaku.Share` package. The upstream packaging import is removed because this
copy is a local, non-packable library. Analyzer, code-style, nullable, and XML
documentation enforcement are disabled only for this project.
`TreatAsLocalProperty` keeps those opt-outs effective when the release script
passes analyzer and warnings-as-errors settings as global MSBuild properties.
