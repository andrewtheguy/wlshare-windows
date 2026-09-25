# How the client is put together

A native Windows client for [wlshare](https://github.com/andrewtheguy/wlshare)
desktops, a window to each. Two halves, and the line between them is a C ABI:

```
  WinUI ──▶ DesktopView ──▶ Client.cs ──▶ Native.cs ═══ wlshare_client_core.dll
                 ▲                                              │
                 │ a frame, a cursor, a state, a clipboard,     ▼
                 │ the sound                                    │
                 └──────────────────── core/ (Rust) ──▶ wlshare-rfb ──▶ the socket
```

`core/` is a Rust crate that owns the socket, the RFB session, the decoders and
the framebuffer they write into — through `wlshare-client`, the session both
native apps are built on, with the Windows-only parts on top. `src/WlshareViewer/` is a WinUI 3 app that owns
a window, a Win2D canvas and the events Windows hands it. The app parses no
protocol and the core knows no WinUI, which is what lets the whole of the first
be unit-tested on a machine that has never seen the second.

It is `../wlshare-macos` with the AppKit half swapped for WinUI, on the same
session. `wlshare-client`, in the `wlshare` repo, is everything but the
window: the handshake, the density and resize rules (`Live::ask_for`), the
decoders, the framebuffer, the pointer's shape, the clipboard and the sound —
the names in this document that are not in this repo are its. `core/` holds
what is Windows': the table that turns a virtual key into a keysym
(`keysym.rs`), the gathering of `WHEEL_DELTA`s into notches (`wheel.rs`), and
the C ABI (`ffi.rs`), with a `Client` that is `wlshare-client`'s plus the
wheel's leftovers. What is different from the Mac is how the halves meet: the
Mac app links its core as a static library, and .NET can call native code only
out of a DLL it loads at run time, so here the crate is a `cdylib` and the app
ships it beside the `.exe`.

Every protocol byte comes from `wlshare-rfb` — the same crate the daemon is
built on, read from the other end — through `wlshare-client`, which re-exports
it. The dependency is a released tag of the `wlshare` repo rather than the
sibling checkout, so what this repo builds is decided by `core/Cargo.toml` and
`core/Cargo.lock` and not by the state of somebody's `../wlshare`.

## Scope

The screen, the keyboard, the pointer, the scale the desktop is drawn at,
which follows the screen's, the clipboard, and the desktop's sound when it is
asked for. The client lists wlshare's VP9 encoding alone as its pixel
encoding — or, when the form says ZRLE, ZRLE and Raw — then Cursor, Cursor With
Alpha, DesktopSize, ExtendedDesktopSize, Fence, ContinuousUpdates, the density extension, Extended
Clipboard and — only when the form's sound checkbox is ticked — the audio
extension, and nothing else. The server never offers camera, microphone or
output selection; sound that arrives unasked is framed and dropped rather than
ending the session.

## 1× and 2×

The window asks the desktop to be its own size in device pixels — the view's
size times the screen's rasterization scale — so a framebuffer pixel is a device
pixel and nothing is resampled. The other half is the *density* the desktop is
drawn at, through the density extension: at 1× a 1600-pixel-wide window is a
1600-point desktop, at 2× it is an 800-point desktop drawn twice as finely.

The density follows the panel, as the Mac client's follows its display's
backing scale, and there is no switch. It is the panel's physical pixels per
inch — `GetDpiForMonitor`'s raw DPI, which Windows takes from the monitor's
EDID (`Screen.PixelsPerInch`) — and not Windows' scale setting, which is a
preference: 180 and up — a 24" 4K — is a panel made for 2×, as a Mac's
Retina ones are at 218 and up, and anything less, or a screen with no size, is 1×
(`DesktopView.DesktopScale`). A ~166 PPI laptop at 150% is 1×, and its
desktop is laid out at the panel's own pixels. The view asks again when the
window moves (`AppWindow.Changed`), which is how it hears of another screen,
and when the rasterization scale changes (`XamlRoot.Changed`).

A change of density is one `ClientDensity` that carries the size with it, which
the server applies as one output configuration; a change of size at the same
density is a `SetDesktopSize`. They go to the server one at a time. It applies
both through wlr-output-management, whose configurations carry a serial the
compositor bumps on every commit, so the second of two in flight is cancelled
and comes back as an invalid layout. Whatever the window asks for while a
density is in flight waits for the `OutputScale` that answers it
(`Live::ask_for`). A move between two panels on the same side of 180 PPI is at
most a new size at the same density, a `SetDesktopSize`; a move across it is
one `ClientDensity`.

## Where a session begins

`ConnectView` is the library — the saved desktops as a list beside a form for
the one selected, a name, the host, the port, the user name, the password, the
**Encoding** and **Play the desktop's sound** — and it is what the app opens
on. `ConnectWindow` is the one window it lives in, made once and brought
forward or put away, never made again, and it opens where it was last left.
`--server host:port` on the command line skips it, with `--audio` for the
checkbox and `--encoding zrle` for the encoding, which is VP9 without it.

Every connection made from the form is to a profile. Nothing is written by
itself: **Save** writes the form into the selected one, or makes a new one of
it when none is selected, and **Connect** does the same before connecting, so
the list is the history too. **Save** is offered only while the form differs
from what is stored, and moving the selection, closing the window or closing
the app with an unsaved edit asks whether to keep it, the way a document does —
**Don't Save** puts the form back as it is stored, so a dropped edit does not
come back with the window. A form filled from the command line but never shown
is not asked about: nobody typed it. A new desktop from **+** is only a cleared
form until it is saved. The list is in the order the rows are dragged into, a
new desktop joining at the bottom. A row is two lines of text, the name and who goes
where, and nothing is captured from the desktop to put beside it. The profiles,
which one was showing and where the library window was left are
`%LOCALAPPDATA%\wlshare\profiles.json`, written beside itself and moved into
place.

Every launch is a process of its own over that one file, so nothing is written
from what a launch read when it started. A change takes a named mutex that
every launch shares (`Local\WlshareViewer`), reads the file again, makes itself
there and writes it back — and only once it is written is it the launch's list,
so a change that fails is not kept to be written later by another. A profile
another launch added shows up in the list the next time this one saves. The
same mutex makes the key: the first launch to save a password makes it, and
one saving at the same time finds it already there.

A password is saved only for a profile whose **Save the password** is ticked,
and it is saved sealed, the way Chrome's and Slack's Safe Storage do it.
Credential Manager holds exactly one generic credential, `WlshareViewer Safe
Storage`: 32 random bytes, base64, made the first time a password is saved and
never by a read — a key made when the old one has gone would open nothing the
old one sealed. `SafeStorage` seals each password with AES-GCM under that key,
with the profile's id as the associated data so a sealed password moved onto
another profile does not open, and the result sits in the profile beside its
host. The key is read from Credential Manager at most once a launch.

A saved password is opened only to connect with, never to show: the form's
field stays empty with *saved* as its placeholder, and what is typed there
replaces it. A credential Credential Manager will not give up, or a sealed
password the key does not open, is a message on the form and not a connection
attempted without it.

The password is not a command-line argument, because an argument list is in the
shell's history and every process listing. A `--server` launch takes the
password saved in the first profile with the same host, port and user name, and
a destination with none is typed into the form, which is the only place a
password is ever typed: a refused one says so in the desktop's window, and
**Library** has the form filled with what was tried.

## A desktop to a window

A desktop opens in a window of its own and stays there: **Connect** adds a
session in front of the library, never in place of it or of anything else
open, so several desktops stand side by side, each with its own socket, its
own decoders and its own sound, and the library stays where it is behind them.
`SessionWindow` is one of them — the window, the `Client` under it, the
`DesktopView` in it, the clipboard and the sound — and `App` holds the list of
them and the one `ConnectWindow` they are started from. Nothing is shared but
the saved desktops: the Windows clipboard is offered to the one window that
has just become the one in use.

A desktop's window is all desktop: what is not the desktop floats over it. The
connection bar is a strip along the top edge, solid under the pointer and faded
so the desktop shows through when the pointer is off it — the desktop's name
and size, a pin, **Library** and **Disconnect** — as a remote desktop client
keeps one. Unpinned, it slides up out of the window a moment after the pointer
leaves it (half a second; two when it is shown on connect), and the pointer at
the top edge of the desktop brings it back — seen by the window after the
desktop view has handled the move, which the desktop still gets — so the
desktop has the whole window and the bar is there when it is looked for, as it
is in mstsc and RealVNC; it is dragged along the edge by its title, and kept as
an offset from the middle so it stays on the window at any size. A click on it
does not keep the keyboard: the pin and **Library** hand it back to the
desktop. **Library** brings the library forward without touching what is open:
it is brought forward, not connected — connecting is the library's own button.
**Disconnect** closes the desktop it is in, with the library brought forward
first when that was the last one. A refused connection and a dropped one end
the session but not the window: the desktop is replaced by the reason, the bar
stays up with **Disconnect** become **Close** and no pin, and the window stays
until it is closed. A desktop's window is only ever that desktop's and the
library only ever the library — neither turns into the other, and the library
is not brought forward for a desktop's trouble.

Closing a desktop's window ends that session and joins its thread while the
window is still there for the callbacks to have reached. Closing the last one
with the library put away rather than asked for closes the library too, and
with it the app; closing the library while a desktop is open only puts it away
again, since it is the only library there is, and with something unsaved in
the form it asks first. Each desktop window opens three quarters of its
screen, a step down and right of the desktop opened before it; the library
opens where it was last left, clamped into the work area of the screen that
place is on, and in the middle of the screen the first time or when that
screen is gone.

## Threads

`Client::connect` starts one thread with a current-thread tokio runtime on it
and returns before the socket is open. Everything after that is:

- **the session's thread**, which reads the socket, decodes into the
  framebuffer under its lock, and calls the window's wake callback;
- **the UI thread**, which draws from the framebuffer under the same lock and
  posts input events to an unbounded channel the session selects on;
- **the audio thread**, only while the sound is on, which takes the decoded
  sound under its own lock ([Sound](#sound)).

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

### VP9

wlshare's VP9 encoding is the whole framebuffer as one stream: every update is
one rectangle covering it, and each frame is coded against the frames before
it, so one decoder, made at the first frame, takes them all in order. What the
stream is — 8-bit 4:4:4 at BT.601 studio swing, a quantizer the server moves
between `vp9_quality` and `vp9_quality_min` as the link keeps up or falls
behind, keyframes only at a new size or a full repaint — is the server's and
`wlshare-rfb`'s; see wlshare's `docs/architecture.md`. The decoder writes the
same `B, G, R, X` as every other rectangle, so the path from the framebuffer to
the screen does not know which encoding filled it. libvpx comes with
`wlshare-rfb`, as a static archive its `libvpx-prebuilt-sys` dependency
downloads for `x86_64-pc-windows-msvc` and links into the DLL; nothing beside
the DLL ships for it.

It is the default, because it is what makes a desktop that moves cheap to
watch, and it is not exact: a desktop that settles is shown at the server's
quality, not pixel for pixel. **ZRLE** on the form is the exact picture. VP9 is
never a request ZRLE answers: it is listed with no pixel encoding behind it, and
a Raw or ZRLE rectangle in a VP9 session — what any server without the
encoding, a generic VNC server or a wlshare older than it, sends to a list it
does not understand — ends the session with an error that says to choose ZRLE,
rather than showing a picture that is not the one chosen. The session title's
`· VP9` therefore follows the form.

A frame is decoded with the framebuffer's lock let go, into a buffer of the
session's own, and copied in under it: a whole-desktop decode is milliseconds,
and the window must not wait on it to draw. Only the session resizes the
framebuffer, so its size cannot change between the two.

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

## The clipboard

Text, both ways, through Extended Clipboard — UTF-8, which is the only
clipboard wlshare speaks; a latin-1 cut text is dropped. Nothing is said about
either clipboard until the server's caps arrive, which it sends on every
`SetEncodings`, and every caps is answered with the client's own: text, every
action, and no unsolicited text. `wlshare-rfb` turns the wire's CRLF into LF
and back; the window turns LF into Windows' CRLF on the way onto its clipboard.

**The desktop's.** A notify is answered with a request at once rather than when
something pastes, so the text is on the Windows clipboard before anything can
paste it. It goes into `Shared` with a generation, the window is woken, and
`ClipboardSync.Take` puts a generation it has not seen on the clipboard. A
notify of nothing — the desktop's clipboard cleared, or holding no text — leaves
the Windows one alone.

**The Windows one.** The window looks when it becomes the one in use — the
session starts, or the window is activated — which is also the only moment a
paste into the desktop can next happen. If `GetClipboardSequenceNumber` has
moved, the clipboard's text goes to the core, which notifies the desktop and
sends the text only when the desktop asks. The desktop therefore never learns
anything copied while the window was not in front, and never anything the
window did not hand over. A clipboard the window hands over before the session
is ready is kept, and notified once the caps arrive.

The clipboard is read and written through Win32 rather than WinRT's
`Clipboard`, whose reads are asynchronous: the sequence number has to be read in
the same breath as the text it numbers. Neither direction sends back what the
other just did: `ClipboardSync` records the sequence number both after offering
and after writing the desktop's text, and the server keeps a clipboard a client
set out of its own notifications.

## Sound

wlshare's audio extension carries what the desktop plays as FLAC on the
connection the pixels use; the wire is `wlshare-rfb`'s `audio` module, built
with its `decode` feature, and its design is in wlshare's own
`docs/architecture.md`. It is asked for or not, per connection, by the form's
**Play the desktop's sound** — `--audio` on the command line. Unticked, the
pseudo-encoding is not listed and the server never sends a byte of it.

Ticked, the session lists `WLSF` and waits for the empty rectangle that
announces it; a server without the extension never sends one and the session
runs silent. The announcement is answered after the update it came in with a
set-format — signed 16-bit stereo at 48 kHz, the stream's format whatever the
output device runs at — and an enable, once. Each begin makes a fresh
`FlacDecoder`, each end drops it, and every frame between is decoded on the
session's thread as it arrives, into the core's `Playback` buffer. A frame that
does not decode costs its 20 ms and no more; each decodes on its own.

`AudioOutput` is the other end: a shared-mode WASAPI stream on a thread of its
own, opened as 48 kHz float stereo with `AUTOCONVERTPCM`, so the audio engine
converts it to whatever the device mixes at. The device's event wakes the
thread whenever it has room, and the thread fills exactly that much through
`wlshare_client_read_audio` — two channel buffers, interleaved into the
device's — on the device's clock. It is made only once the status says the
sound is on, so a session without it leaves the audio hardware alone. A new
default output, reported by an `IMMNotificationClient`, has the thread reopen
the stream on it; a device that fails or goes away is retried every few
seconds, or at once when the default changes. It is disposed before the
`Client` is, since the thread reads from it until it has stopped.

The WASAPI interfaces in `Interop/Wasapi.cs` are source-generated COM
(`[GeneratedComInterface]`), with every method declared in vtable order whether
it is called or not, and every interface a call hands back wrapped as a unique
instance so it can be released the moment the stream is done with it.

The two clocks — the server's capture and the device's — are not one clock,
and the network is not smooth, so `Playback` has a floor and a ceiling. It
starts playing only once 60 ms is waiting, and running dry puts it back to
waiting for that much rather than playing each frame the instant it lands; past
300 ms the oldest sound is dropped back down to 60, so a stall followed by a
burst costs a skip rather than a delay that never goes away. Sound shares the
TCP stream with the pixels, and wlshare sends it ahead of every framebuffer
update, so a large frame delays it by no more than its own transfer.

## The C ABI

`core/src/ffi.rs` is the only place in the crate with `unsafe` in it, and it is
a shell: each function turns C arguments into Rust ones, calls one method, and
turns the answer back.

Framebuffer, cursor and the desktop's clipboard are read through *callbacks*
rather than a lock/unlock pair, so there is no guard to hold across the
boundary and no way to forget to release one. The callback runs with the lock
held and is handed pointers that live only for that call, which is exactly long
enough to upload a bitmap or copy a string. The C#
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
