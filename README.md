# wlshare-windows

A native Windows client for the [`wlshare`](https://github.com/andrewtheguy/wlshare)
VNC server: a WinUI 3 window drawing a Win2D canvas, over a Rust core that
speaks the whole RFB session.

**Scope:** the screen, the keyboard, the pointer, the clipboard and the
desktop's sound. The screen arrives as wlshare's VP9 stream — the whole
desktop, 4:4:4, at the server's `vp9_quality` or, while the link is behind,
lower, down to its `vp9_quality_min`, and sharpened back to `vp9_quality` half
a second after the desktop goes quiet — or, when the connect form's **Encoding**
says so, as exact ZRLE. VP9 is asked for alone: a server without it ends the
session with an error instead of sending ZRLE, and the session's title ends in
`· VP9` when the form chose it. The desktop is asked to be
exactly the window's size in device pixels, so what is on screen is one device
pixel per desktop pixel and never resampled, and it is drawn at **1×** or
**2×** to match the panel the window is on: 2× on one of 180 pixels per inch or
more — a panel made for 2×, as a Mac's Retina ones are — and 1× below that,
whatever Windows' scale setting is. Moving the window to another screen switches
it. The clipboard is text, both ways, as UTF-8. The sound is wlshare's lossless
FLAC stream, played on the default Windows output, and only when the connect
form's **Play the desktop's sound** is ticked. The camera, the microphone and
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
- PowerShell 7. The core links `wlshare-client`, the session both native
  clients are built on, and `wlshare-rfb` under it, where every protocol byte
  comes from — pinned in `core/Cargo.toml` to a released tag of the `wlshare`
  repo and fetched by cargo, no sibling checkout needed.

## Connecting

The app opens on the saved desktops as a list, and beside it a form for the one
selected: a name, the host, the port, the user name, the password, the encoding
and the sound. **Connect** (or a double-click on the row) saves the form into
that profile and connects; with nothing in the list yet it makes the first one
of what you typed. **+** starts a new desktop and **−** deletes the selected
one. A row is its name and where it goes — there is no picture of the desktop.

The password is saved only for a profile whose **Save the password** is ticked,
and then sealed: see [Saved passwords](#saved-passwords). **Disconnect** ends
the session, and a connection that is refused or drops brings the list back
with the reason on it.

An empty password asks for the `None` security type; anything else asks for
RSA-AES, which is the only type this client authenticates with — and the one
that encrypts the session.

A destination on the command line skips the form:

```powershell
WlshareViewer.exe --server 192.168.1.10:5900 --username me --audio --encoding zrle
```

`--audio` is the form's sound checkbox; without it the session is silent.
`--encoding` is the form's encoding, `vp9` (the default) or `zrle`.

There is no password argument, deliberately: an argument list is in the
shell's history and in every process listing. A password saved in a profile for
the same host, port and user name is used, and anything else is typed into the
form — which is what a connection refused for want of one brings back.

### Saved passwords

They are kept the way Chrome and Slack keep theirs. Credential Manager holds one
generic credential, **WlshareViewer Safe Storage**: a random 256-bit key, made
when the first password is saved. Each saved password is sealed with it
(AES-GCM, bound to its profile) and kept with the profiles in
`%LOCALAPPDATA%\wlshare\profiles.json`; the password is never written anywhere
in the clear. Removing the credential in Credential Manager forgets every saved
password at once.

## Checks

```powershell
pwsh ci/windows/ci.ps1           # the core's tests and clippy, the release build and the MSI
pwsh ci/windows/ci.ps1 app       # the quicker Debug build
pwsh ci/windows/remote.ps1 ci    # all of it on the Windows CI VM, from any machine
```

`core/` builds and tests on Linux and macOS too, and that is the fast loop: the
key and wheel tables and the ABI are plain Rust, and the session under them —
the protocol, the decoders and the state machine — is `wlshare-client`'s, which
has its own tests in the `wlshare` repo.

## Releasing

Bump `WlshareAppVersion` in `Directory.Build.props`, then run the **Release the
Windows app** workflow by hand (`gh workflow run release.yml --ref main`). It
builds and packages on a runner with `scripts/package-windows.ps1`, publishes
the MSI and `SHA256SUMS` as `v<WlshareAppVersion>`, and creates that tag — a run
on any branch other than `main` is a prerelease instead. A version that already
has a tag is refused.

## Layout

- `core/` — the Rust crate: `wlshare-client`'s session with the Windows key and
  wheel tables and the C ABI in `src/ffi.rs` on top.
- `src/WlshareViewer/` — the app: the form, the window, the Win2D view, the
  input, and `Interop/Native.cs`, the ABI's other half.
- `installer/` — the WiX 5 MSI.
- `scripts/package-windows.ps1` — the release build and the MSI; the release
  workflow runs nothing else.
- `docs/architecture.md` — how the two halves fit together, and why.
