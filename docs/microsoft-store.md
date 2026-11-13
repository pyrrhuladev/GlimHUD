# Microsoft Store packaging

GlimHUD has a reproducible unsigned MSIX packaging path. It is a full-trust WPF desktop application running as the interactive user at **medium integrity**. Packaging does not grant administrator rights. The default package identity is for development only, and **this is not yet a certified Store release**.

## Build an unsigned package

Requirements: Windows, .NET SDK 10, a complete self-contained `win-x64` publish, and MakeAppx from the Windows SDK. `scripts/Build-MSIX.ps1` searches the installed SDK, PATH, and local NuGet/tool caches. It never installs anything, changes permissions, or creates/imports a certificate.

If the SDK is not installed, extract the official NuGet package `Microsoft.Windows.SDK.BuildTools` version **10.0.26100.1742** under `.tools/windows-sdk-buildtools/10.0.26100.1742`. The pinned package is available from:

<https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/10.0.26100.1742/microsoft.windows.sdk.buildtools.10.0.26100.1742.nupkg>

Its MakeAppx executable is `bin/10.0.26100.0/x64/makeappx.exe`. Keep its adjacent DLLs and schema files. You may also pass `-MakeAppxPath` explicitly. Check the downloaded SDK tool's Microsoft Authenticode signature before using it.

```powershell
./scripts/Publish.ps1 -SelfContained -OutputDirectory "$PWD/artifacts/GlimHUD-store-input"
./scripts/Build-MSIX.ps1 -InputDirectory "$PWD/artifacts/GlimHUD-store-input"
```

The output is `artifacts/msix/GlimHUD-<version>-x64-unsigned.msix`, a SHA-256 file, retained staging, and an unpacked verification directory. Existing packages are not overwritten. The version is derived from `GlimHUD.csproj` and expanded to four components with a zero Store revision. The script runs MakeAppx with semantic validation enabled, unpacks the result, and checks the desktop application entry, media capability namespace, expected assets, runtime files, notices, and driver-file absence. It does not use `/nv`.

To repeat the structural audit:

```powershell
./scripts/Test-StorePackage.ps1 -PackagePath "$PWD/artifacts/msix/GlimHUD-0.3.0.0-x64-unsigned.msix"
```

These checks are deliberately narrower than installation, Windows App Certification Kit (WACK), and Store certification. Even a successful MakeAppx result does not prove that a package installs or that its runtime behavior meets Store policy.

## Associate the real Store identity

Create/verify the developer account and reserve GlimHUD in Partner Center. Copy the exact **Package/Identity/Name**, **Package/Identity/Publisher**, and **Package/Properties/PublisherDisplayName** values from the product identity page. The defaults `GlimHUD.Development` and `CN=GlimHUDDevelopment` are valid development values and cannot represent a reserved Store product.

```powershell
./scripts/Build-MSIX.ps1 `
  -InputDirectory "$PWD/artifacts/GlimHUD-store-input" `
  -OutputDirectory "$PWD/artifacts/store-submission" `
  -IdentityName '<Package/Identity/Name from Partner Center>' `
  -Publisher '<Package/Identity/Publisher from Partner Center>' `
  -PublisherDisplayName '<PublisherDisplayName from Partner Center>'
```

Replace all angle-bracket values before running. The Store signs an accepted MSIX; a purchased code-signing certificate is not required for this Store route. This script produces an unsigned `.msix`, not a Visual Studio `.msixupload` archive. Follow the package format requirements shown for the actual product submission. An unsigned package is not a normal double-click installer. No development certificate has been created or trusted for you. Use a separately authorized, properly signed test deployment or Store flight to test the installed identity.

## Manifest and permissions

`packaging/AppxManifest.xml` targets Windows Desktop 10.0.19041.0 or later, x64. It declares `uap10:RuntimeBehavior="packagedClassicApp"` and `uap10:TrustLevel="mediumIL"`.

| Declaration | Purpose | Review note |
| --- | --- | --- |
| `rescap:Capability Name="runFullTrust"` | Run the WPF desktop application and its bundled desktop helper | A restricted capability; explain the desktop overlay and native APIs in certification notes. It does not elevate the process. |
| `uap7:Capability Name="globalMediaControl"` | Read and control system media sessions through Windows.Media.Control | This is in the `uap/windows10/7` namespace, **not** the restricted-capabilities namespace. |

No `allowElevation`, driver installer, service, startup task, network capability, or game injection is added by this package. The three PNG assets are rendered from the accepted Signal icon geometry by `packaging/Generate-Assets.ps1`.

## Unresolved runtime and certification blockers

The package currently contains the same telemetry implementation as the portable release; it is **not a driver-free Store profile**.

1. **LibreHardwareMonitor:** the application calls `Computer.Open()` with CPU/GPU monitoring. That library can contain compressed Ring0/low-level driver resources and may attempt driver access even when no `.sys` file is visible in the publish directory. A filename audit is insufficient. MSIX does not install Windows drivers, and Store policy places additional restrictions on dependencies on non-Microsoft drivers/services. Do not claim this build is Store-compatible without inspecting the exact 0.9.6 library behavior and resolving its initialization path. A separate explicitly defined Store sensor implementation/profile may be required. Disabling all hardware sensors would remove GPU and temperature information and must be treated as a product change, not silently hidden by packaging.
2. **PresentMon:** the bundled executable captures ETW events. Standard-user access depends on the environment and capture permissions. `runFullTrust` does not solve access-denied errors. The package must not request administrator access or alter group membership silently. Validate actual game FPS under the installed package as a normal user and decide which supported behavior can be advertised. The app must show unavailable measurements honestly.
3. **Packaged behavior:** test settings persistence, tray/exit, language switching, system media artwork and controls (including Yandex Music), volume control, overlay interaction, and child-process startup from the protected install location. Test with standard user permissions, supported display scaling, borderless/windowed games, and several GPUs. Exclusive fullscreen/anti-cheat compatibility must not be promised without tests.
4. **Submission inputs:** real Partner Center identity, account verification, support/privacy URLs, supported markets/age rating, actual screenshots, and certification notes remain owner-provided/reviewed inputs. Draft English/Russian descriptions are in `packaging/store-listing/`; they are not published listings.

The source application manifest requests `asInvoker`. This is necessary but does not eliminate low-level dependencies inside third-party libraries. A successful package structure audit must not be presented as a passed driver audit or Store approval.

## Release gate

Before submission: resolve the driver path; establish an honest standard-user FPS behavior; rebuild with the real Store identity; run WACK; install and smoke-test the signed packaged app on a clean standard-user machine; supply real screenshots, privacy/support URLs and capability explanations; and submit through Partner Center for certification. No Store submission is performed by these scripts.

## Microsoft references

- [Generate desktop MSIX components](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion): desktop entry, identity, full trust, visual assets.
- [uap7:Capability schema](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap7-capability): `globalMediaControl` and namespace.
- [App capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations): permission behavior.
- [MakeAppx and its limited validation](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool).
- [Prepare a desktop app for MSIX](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-prepare): standard-user and driver limitations.
- [Microsoft Store policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies): review current driver/service and restricted-capability rules before submission.
- [Code-signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options): Store signing versus direct distribution.
