# RipperWorks

Windows desktop mod manager for Cyberpunk 2077.

> **Status: Nexus Mods API/OAuth review — pre-public release**

This repository contains the complete application source code for RipperWorks v1.0.3.

The application source under `src/` corresponds to RipperWorks v1.0.3. Internal development planning documents, agent instructions, test infrastructure, review tooling, and other non-product development artifacts are intentionally excluded from this public repository.

## Building

Requirements:

- Windows
- .NET SDK compatible with `global.json`

Build the application with:

```powershell
dotnet build .\RipperWorks.sln -c Release
```

## Nexus Mods integration

RipperWorks includes Nexus Mods integration for metadata, requirements/dependencies, update checking, NXM handling, and download workflows.

Support for user-supplied Nexus Personal API Keys has been removed from v1.0.3 as requested by Nexus Mods during API review.

Authenticated Nexus functionality that requires the future OAuth integration is temporarily unavailable pending progression of the Nexus Mods OAuth registration process.

Nexus functionality that does not depend on the removed Personal API Key path remains available where supported.

No public binary release is currently distributed through this repository.

## License

No open-source license has been granted at this time.