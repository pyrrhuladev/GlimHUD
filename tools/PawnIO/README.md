# PawnIO CPU sensor dependency

GlimHUD bundles the complete, unmodified official PawnIO 2.2.0 installer. It is
used for low-level CPU temperature access by LibreHardwareMonitor. It is separate
from the signed LGPL modules embedded in LibreHardwareMonitor itself.

Run `scripts/Get-PawnIO.ps1` to obtain the pinned vendor installer before publishing.
The script checks its size, SHA256, valid Authenticode signature and pinned signer.
`-VerifyOnly -DestinationDirectory <directory>` performs local verification only.
The executable is fetched for release builds; it is not committed to this repository.

The GlimHUD installer offers CPU sensor support and requests ordinary Windows UAC
only when running the vendor installer. Portable users can install it explicitly
from System metrics in Settings. A compatible installed version is preserved.
There is no automatic reboot, unrestricted-driver installation or device ACL change.

- Official website: https://pawnio.eu/
- Exact release: https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0
- Redistribution permission: https://github.com/a1ive/nwinfo/issues/86
- Bundled notice: ../../Notices/PawnIO.Setup.txt

The setup executable is redistributed unchanged. Its proprietary signed driver is
not extracted or redistributed separately.
