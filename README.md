# RipperWorks

Windows desktop mod manager for Cyberpunk 2077.

> **Status: Nexus Mods API/OAuth review — pre-public release**

This repository contains the complete application source code corresponding to RipperWorks v1.0.1.

The application source under `src/` is unchanged from the v1.0.1 source supplied to Nexus Mods for review.

Internal development planning documents, agent instructions, test infrastructure, review tooling, and other non-product development artifacts are intentionally excluded from this public repository.

## Building

Requirements:

- Windows
- .NET SDK compatible with `global.json`

Build the application with:

```powershell
dotnet build .\RipperWorks.sln -c Release
Nexus Mods integration

RipperWorks includes Nexus Mods integration for metadata, requirements/dependencies, update checking, NXM handling, and user-initiated download workflows.

Authentication and download integration is currently undergoing Nexus Mods API/OAuth review and may change before general public distribution.

No public binary release is currently distributed through this repository.

License

No open-source license has been granted at this time.
