# Third-party components

GlimHUD's original code and project assets are licensed under the [MIT license](LICENSE). The following dependencies retain their own licenses. Their upstream source, package metadata, and accompanying license files remain authoritative.

| Component | Version | License | Source |
| --- | --- | --- | --- |
| LibreHardwareMonitorLib | 0.9.6 | Mozilla Public License 2.0 (MPL-2.0) | <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor> |
| NAudio.Wasapi / NAudio.Core | 2.2.1 | MIT | <https://github.com/naudio/NAudio> |
| BlackSharp.Core | 1.0.7 | Mozilla Public License 2.0 (MPL-2.0) | <https://github.com/Blacktempel/BlackSharp> |
| DiskInfoToolkit | 1.1.2 | Mozilla Public License 2.0 (MPL-2.0) | <https://github.com/Blacktempel/DiskInfoToolkit> |
| RAMSPDToolkit-NDD | 1.4.2 | Mozilla Public License 2.0 (MPL-2.0) | <https://github.com/Blacktempel/RAMSPDToolkit> |
| HidSharp | 2.6.4 | Apache License 2.0 | <https://www.nuget.org/packages/HidSharp/2.6.4/License> |
| System.Management | 10.0.2 | MIT | <https://github.com/dotnet/dotnet> |
| System.IO.Ports and its native runtime support packages | 10.0.3 | MIT | <https://github.com/dotnet/dotnet> |
| System.IO.FileSystem.AccessControl | 5.0.0 | MIT | <https://github.com/dotnet/runtime> |
| PresentMon console application | 2.6.0, when installed | MIT; see accompanying upstream license | <https://github.com/GameTechDev/PresentMon> |
| .NET / WPF | 10.x | MIT and third-party notices supplied by the .NET distribution | <https://github.com/dotnet/wpf> |

LibreHardwareMonitor is used without modifying its source. LibreHardwareMonitor, BlackSharp.Core, DiskInfoToolkit, and RAMSPDToolkit source is available at the upstream repositories above. Their MPL-2.0 terms apply to those components independently of GlimHUD's MIT license. The upstream LibreHardwareMonitor and NAudio license texts are included in `Notices`.

The PresentMon download script obtains the official upstream console binary and its license. Its local license is stored alongside it in `tools/PresentMon/LICENSE.txt` and copied to published builds when present. A build without that binary cannot collect FPS through PresentMon.

The GlimHUD Signal vector geometry and generated application icons are project assets. SVG sources and an icon generation script are included.
