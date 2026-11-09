# BiliCopilot local SDK packages

Baseline: upstream `Richasy/bili-kernel` main commit
`e26f6dbd071e20d4220806fcff7bd675f3c29fc5` (`0.0.2-preview2`).
Both upstream and fork default-branch HEADs were this commit before applying
the local changes. No unrelated later upstream commits were merged.

This applies the source snapshot used to build
`0.0.2-preview2-bilicopilot.2611.2.sb2` on 2026-10-11:

- Content metadata for comments, moments and episodes, plus corresponding adapters.
- Local API constants and live discovery request handling.
- Explicit assembly version `1.0.0.0` and package-based dependencies matching sb2.

The fork package version is `0.0.3-bilicopilot.1`, defined centrally in
`src/nuget-package.props`. Only Abstractions and Services.Comment/Media/Moment
are packed by Bili.Copilot's `scripts/Build-BiliKernel.ps1`. Their dependency
on the modified Abstractions package uses this fork version. Core remains
the public `[0.0.2-preview2]` package, including its public transitive dependencies;
do not rebuild or bump Core/Grpc as part of the consumer workflow.

The original README and LICENSE are preserved. The source LICENSE is GPLv3;
the original file headers and package metadata refer to MIT. This historical
license inconsistency has not been rewritten in this source-only build change.
