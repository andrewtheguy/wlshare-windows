# wlshare-windows

A native Windows client for the [`wlshare`](https://github.com/andrewtheguy/wlshare)
VNC server: a WinUI 3 window drawing a Win2D canvas, over a Rust core that
speaks the whole RFB session.

**Scope:** the screen, the keyboard and the pointer. The desktop is asked to be
exactly the window's size in device pixels, so what is on screen is one device
pixel per desktop pixel and never resampled, and it is drawn at **1×** or
**2×** to match the panel the window is on: 2× on one of 192 pixels per inch or
more — a panel made for 2×, as a Mac's Retina ones are — and 1× below that,
whatever Windows' scale setting is. Moving the window to another screen switches
it. The clipboard, the sound, the camera, the microphone and
picking an output are wlshare extensions this client does not speak yet.

Windows 10 1809 or later, x64.

## Install

Each release carries `WlshareViewer-windows-x64.msi` and a `SHA256SUMS` beside
it. The installer puts the app in `Program Files\wlshare` and a **wlshare**
entry in the Start menu.

The MSI is **unsigned**: there is no certificate behind it. SmartScreen stops a
downloaded unsigned installer with *Windows protected your PC*; **More info ▸
Run anyway** gets past it. Checking the hash first is how to know it is the one
the release built:

```powershell
(Get-FileHash .\WlshareViewer-windows-x64.msi -Algorithm SHA256).Hash
```

Building it yourself avoids the warning — a locally built installer is not
marked as downloaded:

```powershell
pwsh scripts/package-windows.ps1   # dist\package\WlshareViewer-windows-x64.msi
```

## What you need to build

- Rust with the MSVC toolchain (`x86_64-pc-windows-msvc`), which needs the
  Visual Studio Build Tools' C++ workload.
- The .NET 10 SDK. The Windows App SDK, Win2D and WiX all arrive as NuGet
  packages; no Visual Studio workload beyond the C++ one is needed.
- PowerShell 7. The core links `wlshare-rfb`, where every protocol byte comes
  from, pinned in `core/Cargo.toml` to a released tag of the `wlshare` repo and
  fetched by cargo — no sibling checkout needed.

## Connecting

The app opens on a form for the host, the port, the user name and the
password, and connects when you fill it in. It comes back filled with the last
destination; the password is never remembered. **Disconnect**
ends the session, and a connection that is refused or drops brings the form
back with the reason on it.

An empty password asks for the `None` security type; anything else asks for
RSA-AES, which is the only type this client authenticates with — and the one
that encrypts the session.

A destination on the command line skips the form:

```powershell
WlshareViewer.exe --server 192.168.1.10:5900 --username me
```

There is no password argument, deliberately: an argument list is in the
shell's history and in every process listing. A desktop that wants one refuses
and brings the form back to type it into.

## Checks

```powershell
pwsh ci/windows/ci.ps1           # the core's tests and clippy, the release build and the MSI
pwsh ci/windows/ci.ps1 app       # the quicker Debug build
pwsh ci/windows/remote.ps1 ci    # all of it on the Windows CI VM, from any machine
```

`core/` builds and tests on Linux and macOS too, and that is the fast loop: the
protocol, the decoders and the session state machine have nothing Windows in
them.

## Releasing

Bump `WlshareAppVersion` in `Directory.Build.props`, then run the **Release the
Windows app** workflow by hand (`gh workflow run release.yml --ref main`). It
builds and packages on a runner with `scripts/package-windows.ps1`, publishes
the MSI and `SHA256SUMS` as `v<WlshareAppVersion>`, and creates that tag — a run
on any branch other than `main` is a prerelease instead. A version that already
has a tag is refused.

## Layout

- `core/` — the Rust crate: session, framebuffer, keysym and wheel tables, and
  the C ABI in `src/ffi.rs`.
- `src/WlshareViewer/` — the app: the form, the window, the Win2D view, the
  input, and `Interop/Native.cs`, the ABI's other half.
- `installer/` — the WiX 5 MSI.
- `scripts/package-windows.ps1` — the release build and the MSI; the release
  workflow runs nothing else.
- `docs/architecture.md` — how the two halves fit together, and why.
