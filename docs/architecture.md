# How the client is put together

A native Windows window onto a [wlshare](https://github.com/andrewtheguy/wlshare)
desktop. Two halves, and the line between them is a C ABI:

```
  WinUI ──▶ DesktopView ──▶ Client.cs ──▶ Native.cs ═══ wlshare_client_core.dll
                 ▲                                              │
                 │ a frame, a cursor, a state                   ▼
                 └──────────────────── core/ (Rust) ──▶ wlshare-rfb ──▶ the socket
```

`core/` is a Rust crate that owns the socket, the RFB session, the decoders and
the framebuffer they write into. `src/WlshareViewer/` is a WinUI 3 app that owns
a window, a Win2D canvas and the events Windows hands it. The app parses no
protocol and the core knows no WinUI, which is what lets the whole of the first
be unit-tested on a machine that has never seen the second.

It is `../wlshare-macos` with the AppKit half swapped for WinUI. The core is
that repo's core, less the clipboard and the sound, with the two tables that
are about the keyboard and the wheel rewritten for Windows. What is different is
how the halves meet: the Mac app links the core as a static library, and .NET
can call native code only out of a DLL it loads at run time, so here the crate
is a `cdylib` and the app ships it beside the `.exe`.

Every protocol byte comes from `wlshare-rfb` — the same crate the daemon is
built on, read from the other end. It is a cargo dependency on a released tag of
the `wlshare` repo rather than the sibling checkout, so what this repo builds is
decided by `core/Cargo.toml` and `core/Cargo.lock` and not by the state of
somebody's `../wlshare`.

## Scope

The screen, the keyboard, the pointer, and the scale the desktop is drawn at.
The client lists ZRLE, Raw, Cursor, Cursor With Alpha, DesktopSize,
ExtendedDesktopSize, Fence, ContinuousUpdates and the density extension, and
nothing else. The server never offers the clipboard, sound, camera, microphone
or output selection; a message of one that arrives anyway is framed and
dropped rather than ending the session.

## 1× and 2×

The window asks the desktop to be its own size in device pixels — the view's
size times the screen's rasterization scale — so a framebuffer pixel is a device
pixel and nothing is resampled. What it chooses is the *density* the desktop is
drawn at, through the density extension: at 1× a 1600-pixel-wide window is a
1600-point desktop, at 2× it is an 800-point desktop drawn twice as finely. That
is the whole of the toolbar's switch, and the connect form's choice of where to
start.

The density and the size go to the server one at a time. It applies both
through wlr-output-management, whose configurations carry a serial the
compositor bumps on every commit, so the second of two in flight is cancelled
and comes back as an invalid layout. The density goes first and the size waits
for the `OutputScale` that answers it (`Live::ask_for`). A switch at one window
size is a density alone.

The switch is independent of the screen's own scale setting. A window on a
150% screen at 2× is still a device pixel per framebuffer pixel; its desktop is
simply laid out for a density the screen does not quite have, which is the
choice being offered.

## Where a session begins

`ConnectView` is a form for the host, the port, the user name, the password
and the scale, and it is what the app opens on. `--server host:port` on the
command line skips it.

The host, the port, the user name and the scale are remembered in
`%LOCALAPPDATA%\wlshare\settings.json`. The password is not, anywhere — a file
is no place for one — and it is not a command-line argument either, because an
argument list is in the shell's history and every process listing.

A session ends where it began. A refused connection, a dropped one and
**Disconnect** all put the form back with the reason on it.

## Threads

`Client::connect` starts one thread with a current-thread tokio runtime on it
and returns before the socket is open. Everything after that is:

- **the session's thread**, which reads the socket, decodes into the
  framebuffer under its lock, and calls the window's wake callback;
- **the UI thread**, which draws from the framebuffer under the same lock and
  posts input events to an unbounded channel the session selects on.

Neither waits for the other for longer than a memcpy. The wake callback runs on
the session's thread and must not block, so `Client.cs` has it do one
`DispatcherQueue.TryEnqueue` — and only when one is not already waiting, so a
burst of frames is one redraw rather than a queue of them. The canvas is
invalidated and draws when XAML next lets it.

Closing the `Client` clears the callback *before* joining the thread, so nothing
can call into a window that has let go of it. A redraw already sitting on the
dispatcher queue holds its own reference to what it needs and checks a flag
that closing sets first.

## Pixels

One format, end to end. The client asks for the server's own — `XRGB8888`, the
bytes `B, G, R, X` — which is a Win2D `B8G8R8A8UIntNormalized` bitmap with its
alpha ignored, so nothing on the path from the compositor's buffer to the
screen swizzles a pixel.

The framebuffer keeps a generation and a damage rectangle. A generation the
window has not seen is a framebuffer of a new size, and its bitmap is made again
and filled whole; otherwise the damage is packed into a reused buffer and
uploaded with one `SetPixelBytes`. At rest the bitmap is drawn at its own size in
device pixels with nearest-neighbour sampling — a copy, not a filter — and only
while the desktop is catching up with a resize or a change of scale is it
fitted to the view and filtered.

## The pointer

wlshare never paints the pointer into a frame, so the one on screen is the
desktop's shape or there is none. The shape arrives as premultiplied RGBA and
becomes an HCURSOR — divided back out of its alpha, since Windows takes a
cursor's colour straight — and XAML is given it through the Windows App SDK's
`IInputCursorStaticsInterop::CreateFromHCursor`, set as the view's
`ProtectedCursor`. XAML has no public way to take a cursor made of pixels.

The shape is in framebuffer pixels, which are device pixels, and Windows draws a
cursor made from a bitmap at the bitmap's own size, so it is the size the
desktop meant at either scale.

## Keys

RFB carries X11 keysyms. A key that names itself — an arrow, a function key, a
keypad digit, a modifier — is found by its virtual key code in `keysym.rs`, with
the extended bit telling apart the keys Windows gives one code (the two Enters,
the right Control and Alt, the navigation block and the keypad's), and the scan
code telling the two Shifts apart. Every other key is sent as the character it
types with Control and Alt let go, which `ToUnicodeEx` answers without touching
the keyboard's dead-key state. Each key is let go with the keysym it went down
with, and everything held is let go when the view loses the keyboard.

Caps Lock and Num Lock are not sent: a character reaches the desktop already
cased, and a keypad key already says which of its two meanings it is.

## The C ABI

`core/src/ffi.rs` is the only place in the crate with `unsafe` in it, and it is
a shell: each function turns C arguments into Rust ones, calls one method, and
turns the answer back.

Framebuffer and cursor are read through *callbacks* rather than a lock/unlock
pair, so there is no guard to hold across the boundary and no way to forget to
release one. The callback runs with the lock held and is handed pointers that
live only for that call, which is exactly long enough to upload a bitmap. The C#
trampolines are `[UnmanagedCallersOnly]` and catch everything: an exception may
not unwind through Rust, so it is carried out and rethrown on the C# side of the
call.

There is no C header. The ABI's other half is `Interop/Native.cs`, hand-written,
and `core/tests/interop_matches.rs` reads both it and `src/ffi.rs`, parses each
in its own language — the callbacks' C# function-pointer types worked out from
the Rust `type`s that declare them — and fails if a function, a callback or a
struct field differs. A `ushort` where the Rust says `u32` loads cleanly and
corrupts memory at run time; nothing else would catch it.

## Building

`build-core.ps1` builds the DLL for `x86_64-pc-windows-msvc` and puts it in
`dist\`, which the app's project copies beside the `.exe`. There is no pinned
release zip, the pattern `../ezvpn-windows` uses for a core in another repo,
because the core is in *this* repo and has one consumer.

`ci/windows/ci.ps1` runs the jobs; from the Linux checkout
`ci/windows/remote.ps1 ci` — a wrapper over `../devtools` — ships the working
tree to the `windows-ci-build` VM and runs them there.

## Shipping

`scripts/package-windows.ps1` is the release build: the core, a self-contained
`dotnet publish` of the app, and a WiX 5 MSI of the published folder.
`.github/workflows/release.yml` runs that one script on a Windows runner and
publishes what it leaves in `dist\package\`, so the installer a release carries
is the installer anyone can build.

Two things it deliberately does not do. It does not sign anything: there is no
certificate, and the README says how to get past SmartScreen. And it does not
parse `Directory.Build.props` for the version — it reads the product version
back out of the `.exe` it just published, so the tag `v<version>` names what
actually shipped.
