# wlshare-windows — repository instructions

A native Windows client for the [`wlshare`](https://github.com/andrewtheguy/wlshare)
VNC server, built against a released `wlshare-client` — the Rust session that
repo holds for both native clients. The sibling of `../wlshare-macos`, with the
same two halves and the same session under them.

- Strict no backward-compatibility or legacy paths no matter what.
- **Windows x64 only.** The core DLL is built for `x86_64-pc-windows-msvc`
  and nothing else.
- Two halves: `core/` is the Rust crate (`wlshare-client-core`) built as the
  DLL `wlshare_client_core.dll` — `wlshare-client`'s session with what is
  Windows' on top: the key and wheel tables and the C ABI; `src/WlshareViewer/`
  is the WinUI 3 (.NET) app that puts it in a window, P/Invoking the DLL. The
  app never parses a protocol byte.
- **The session is `wlshare-client`'s, and every protocol byte `wlshare-rfb`'s**,
  both in `../wlshare`. A change to how the session behaves — the handshake,
  resizing, decoding, the clipboard, the sound — belongs in
  `../wlshare/crates/wlshare-client`, where the macOS app gets it too, and a
  wire change in `../wlshare/crates/wlshare-rfb`; neither belongs here.
- **The dependency is a pinned release tag** of `wlshare-client`, which
  re-exports `wlshare-rfb`, not the sibling checkout —
  `core/Cargo.toml` names the tag and `core/Cargo.lock` the revision. To build
  against an unreleased one, pass the `--config` patch `core/Cargo.toml` spells
  out rather than editing the dependency.
- **The ABI has two hand-written halves**, `core/src/ffi.rs` and
  `src/WlshareViewer/Interop/Native.cs`, and `core/tests/interop_matches.rs`
  fails if they differ. Change both together.
- After Rust changes run `cargo test` and `cargo clippy --all-targets -- -D warnings`
  in `core/` — the core builds and tests on Linux, which is the fast loop; add
  `--target x86_64-pc-windows-msvc` to clippy for the real target. Do not run
  `cargo fmt`. Use `anyhow` for application errors and `thiserror` for typed
  ones.
- Every script is PowerShell 7 (`pwsh`), never Windows PowerShell 5.1, except
  `scripts/make-icon.sh`, which runs where the SVG tools are.
- **To check the app, run `pwsh ci/windows/remote.ps1 ci`** from the Linux
  checkout: it ships the working tree to the `windows-ci-build` VM and runs
  `ci/windows/ci.ps1` there — the core's tests and clippy, then the release
  build and the MSI.
- The live tests against a real wlshare are `wlshare-client`'s, in
  `../wlshare/crates/wlshare-client/tests/live_session.rs` — see CLAUDE.local.md.
- **Packaging is one script.** `scripts/package-windows.ps1` builds Release and
  makes the MSI, and `.github/workflows/release.yml` runs that and nothing else
  before publishing `v<WlshareAppVersion>`. A change to how the app is packaged
  belongs in the script, never only in the workflow. Nothing is signed.
- Design and wire details live in `docs/architecture.md`, not here.
