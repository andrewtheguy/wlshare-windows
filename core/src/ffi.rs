//! The C ABI the app P/Invokes, and the only place in this crate with `unsafe`
//! in it.
//!
//! It is a thin shell over [`crate::Client`]: every function here turns C
//! arguments into Rust ones, calls one method, and turns the answer back. No
//! decision lives here that is not about pointers, because nothing here can be
//! unit-tested the way the rest of the crate is.
//!
//! Three rules hold everywhere:
//!
//! - A null `client` is a no-op, not a crash. The app's window can outlive its
//!   connection by a frame or two and there is no value in making that fatal.
//! - A pointer handed to a callback is borrowed for that call and no longer.
//!   Copy what you need out of it — upload it to a bitmap — and do not keep it.
//! - A callback runs with a lock held, so it must not call back into this API
//!   and must not wait for anything.
//!
//! There is no C header: the app's side of this is
//! `src/WlshareViewer/Interop/Native.cs`, and `tests/interop_matches.rs` reads
//! it beside this file and fails if a function, a callback or a struct field
//! differs between the two.

use std::ffi::{CStr, c_char, c_void};

use crate::{Client, Config, Surface, session::State};

/// [`State`] as the app sees it.
pub const WLSHARE_STATE_CONNECTING: i32 = 0;
pub const WLSHARE_STATE_READY: i32 = 1;
pub const WLSHARE_STATE_CLOSED: i32 = 2;

/// Where a session has got to, and what the desktop looks like.
#[repr(C)]
pub struct WlshareStatus {
    /// One of the `WLSHARE_STATE_*` values.
    pub state: i32,
    /// The framebuffer, in pixels.
    pub width: u32,
    pub height: u32,
    /// The scale the server says it draws the desktop at.
    pub scale: f64,
    /// Whether the desktop's sound is on: asked for, and the server has it.
    pub audio: bool,
}

/// The framebuffer, as it is for the length of one callback.
#[repr(C)]
pub struct WlshareFrame {
    /// `width * height * 4` bytes of `B, G, R, X`, `stride` bytes a row — a
    /// Win2D `B8G8R8A8UIntNormalized` bitmap with its alpha ignored, exactly.
    /// Null for a framebuffer with no pixels, which is a session before its
    /// ServerInit.
    pub pixels: *const u8,
    pub width: u32,
    pub height: u32,
    pub stride: u32,
    /// Which framebuffer this is. A number the window has not seen before is a
    /// framebuffer of a new size, whose bitmap has to be made again.
    pub generation: u64,
    /// Whether anything changed since the last visit. False is a frame worth
    /// drawing again but not uploading again.
    pub damaged: bool,
    /// The region that changed, when `damaged`.
    pub damage_x: u32,
    pub damage_y: u32,
    pub damage_width: u32,
    pub damage_height: u32,
}

/// The pointer's shape, as it is for the length of one callback.
#[repr(C)]
pub struct WlshareCursor {
    /// `width * height * 4` bytes of premultiplied RGBA, as `CursorImage` holds
    /// them, or null when `present` is false.
    pub rgba: *const u8,
    /// Which shape this is. Unchanged means the same shape as last time; zero
    /// is before the first shape has arrived.
    pub generation: u64,
    pub width: u16,
    pub height: u16,
    pub hotspot_x: u16,
    pub hotspot_y: u16,
    /// False is a pointer that is hidden or has not arrived yet.
    pub present: bool,
}

/// Called on the session's thread when there is something new to draw.
pub type WlshareWakeFn = extern "C" fn(ctx: *mut c_void);
pub type WlshareFrameFn = extern "C" fn(ctx: *mut c_void, frame: *const WlshareFrame);
pub type WlshareCursorFn = extern "C" fn(ctx: *mut c_void, cursor: *const WlshareCursor);
pub type WlshareClipboardFn = extern "C" fn(ctx: *mut c_void, generation: u64, text: *const u8, len: usize);

/// A context pointer the app gave us, carried to the thread that calls back
/// into it. Whether it is safe to use from there is the app's to guarantee —
/// which is the point of saying so here rather than leaving it implied.
struct Ctx(*mut c_void);
unsafe impl Send for Ctx {}
unsafe impl Sync for Ctx {}

impl Ctx {
    /// A method rather than a field read, so that a closure calling it captures
    /// the whole [`Ctx`] — and with it the `Send + Sync` that makes the closure
    /// one — instead of capturing the bare pointer inside.
    fn get(&self) -> *mut c_void {
        self.0
    }
}

unsafe fn text(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::new();
    }
    unsafe { CStr::from_ptr(ptr) }.to_string_lossy().into_owned()
}

/// Copy a string out, NUL-terminated and truncated to fit, and return how long
/// it is. A caller that got back more than `cap - 1` can ask again with room.
unsafe fn copy_out(from: &str, out: *mut c_char, cap: usize) -> usize {
    if !out.is_null() && cap > 0 {
        let room = cap - 1;
        let mut end = from.len().min(room);
        while end > 0 && !from.is_char_boundary(end) {
            end -= 1;
        }
        unsafe {
            std::ptr::copy_nonoverlapping(from.as_ptr(), out.cast::<u8>(), end);
            *out.add(end) = 0;
        }
    }
    from.len()
}

/// Start a session. Never null: a connection that fails does so in the status.
///
/// # Safety
/// The three strings are NUL-terminated UTF-8, or null for empty. An empty
/// password asks for the `None` security type and any other asks for RSA-AES.
/// `audio` asks for the desktop's sound.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_connect(
    host: *const c_char,
    port: u16,
    username: *const c_char,
    password: *const c_char,
    audio: bool,
    surface_width: u16,
    surface_height: u16,
    scale: f64,
) -> *mut Client {
    let config = unsafe { Config { host: text(host), port, username: text(username), password: text(password), audio } };
    let surface = Surface { width: surface_width, height: surface_height, scale };
    Box::into_raw(Box::new(Client::connect(config, surface)))
}

/// End the session and wait for its thread. The frame callback is cleared
/// first, so nothing calls back into the app after this returns.
///
/// # Safety
/// `client` came from [`wlshare_client_connect`] and is not used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_close(client: *mut Client) {
    if !client.is_null() {
        drop(unsafe { Box::from_raw(client) });
    }
}

/// # Safety
/// `client` is live or null, and `output` points at a [`WlshareStatus`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_status(client: *const Client, output: *mut WlshareStatus) {
    let (Some(client), false) = (unsafe { client.as_ref() }, output.is_null()) else { return };
    let status = client.status();
    let (width, height) = client.desktop_size();
    unsafe {
        *output = WlshareStatus {
            state: match status.state {
                State::Connecting => WLSHARE_STATE_CONNECTING,
                State::Ready => WLSHARE_STATE_READY,
                State::Closed => WLSHARE_STATE_CLOSED,
            },
            width: u32::from(width),
            height: u32::from(height),
            scale: status.scale,
            audio: status.audio,
        };
    }
}

/// Why the session ended, into `output`. Returns its length, which is 0 for a
/// session that has not failed.
///
/// # Safety
/// `output` has room for `cap` bytes, or `cap` is 0.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_error(client: *const Client, output: *mut c_char, cap: usize) -> usize {
    let Some(client) = (unsafe { client.as_ref() }) else { return 0 };
    unsafe { copy_out(client.status().error.as_deref().unwrap_or_default(), output, cap) }
}

/// The desktop's name, into `output`. Returns its length.
///
/// # Safety
/// `output` has room for `cap` bytes, or `cap` is 0.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_name(client: *const Client, output: *mut c_char, cap: usize) -> usize {
    let Some(client) = (unsafe { client.as_ref() }) else { return 0 };
    unsafe { copy_out(&client.status().name, output, cap) }
}

/// Call `wake` from the session's thread whenever there is something new. A
/// null `wake` takes the callback off.
///
/// # Safety
/// `ctx` must be usable from another thread for as long as the client lives or
/// until the callback is taken off, and `wake` must not block or call back into
/// this API.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_on_frame(client: *const Client, wake: Option<WlshareWakeFn>, ctx: *mut c_void) {
    let Some(client) = (unsafe { client.as_ref() }) else { return };
    match wake {
        Some(wake) => {
            let ctx = Ctx(ctx);
            client.on_frame(Some(Box::new(move || wake(ctx.get()))));
        }
        None => client.on_frame(None),
    }
}

/// Show `visit` the framebuffer and take its damage. The framebuffer's lock is
/// held for the call.
///
/// # Safety
/// `visit` must not block or call back into this API, and must not keep the
/// pointers it is given.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_with_frame(client: *const Client, visit: WlshareFrameFn, ctx: *mut c_void) {
    let Some(client) = (unsafe { client.as_ref() }) else { return };
    client.with_frame(|fb, damage| {
        let frame = WlshareFrame {
            pixels: if fb.pixels().is_empty() { std::ptr::null() } else { fb.pixels().as_ptr() },
            width: u32::from(fb.width()),
            height: u32::from(fb.height()),
            stride: fb.stride() as u32,
            generation: fb.generation(),
            damaged: damage.is_some(),
            damage_x: damage.map_or(0, |d| d.x),
            damage_y: damage.map_or(0, |d| d.y),
            damage_width: damage.map_or(0, |d| d.width),
            damage_height: damage.map_or(0, |d| d.height),
        };
        visit(ctx, &raw const frame);
    });
}

/// Say the whole framebuffer must be uploaded again.
///
/// # Safety
/// `client` is live or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_damage_all(client: *const Client) {
    if let Some(client) = unsafe { client.as_ref() } {
        client.damage_all();
    }
}

/// Show `visit` the pointer's shape. The cursor's lock is held for the call.
///
/// # Safety
/// As [`wlshare_client_with_frame`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_with_cursor(client: *const Client, visit: WlshareCursorFn, ctx: *mut c_void) {
    let Some(client) = (unsafe { client.as_ref() }) else { return };
    client.with_cursor(|generation, image| {
        let cursor = match image {
            Some(image) => WlshareCursor {
                rgba: image.rgba().as_ptr(),
                generation,
                width: image.width(),
                height: image.height(),
                hotspot_x: image.hotspot().0,
                hotspot_y: image.hotspot().1,
                present: true,
            },
            None => WlshareCursor {
                rgba: std::ptr::null(),
                generation,
                width: 0,
                height: 0,
                hotspot_x: 0,
                hotspot_y: 0,
                present: false,
            },
        };
        visit(ctx, &raw const cursor);
    });
}

/// The pointer: the RFB button mask, and a position in the window's device
/// pixels.
///
/// # Safety
/// `client` is live or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_pointer(client: *const Client, buttons: u8, x: u16, y: u16) {
    if let Some(client) = unsafe { client.as_ref() } {
        client.pointer(buttons, x, y);
    }
}

/// # Safety
/// `client` is live or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_key(client: *const Client, down: bool, keysym: u32) {
    if let Some(client) = unsafe { client.as_ref() } {
        client.key(down, keysym);
    }
}

/// The window: its size in device pixels, and the scale — 1× or 2× — the
/// desktop is to be drawn at. The desktop is asked to match once the change
/// settles.
///
/// # Safety
/// `client` is live or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_surface(client: *const Client, width: u16, height: u16, scale: f64) {
    if let Some(client) = unsafe { client.as_ref() } {
        client.surface(Surface { width, height, scale });
    }
}

/// The Windows clipboard, `len` bytes of UTF-8, for the desktop. Bytes that are
/// not UTF-8 are replaced rather than refused.
///
/// # Safety
/// `client` is live or null, and `text` points at `len` bytes or is null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_set_clipboard(client: *const Client, text: *const u8, len: usize) {
    let Some(client) = (unsafe { client.as_ref() }) else { return };
    let bytes = if text.is_null() || len == 0 { &[][..] } else { unsafe { std::slice::from_raw_parts(text, len) } };
    client.clipboard(String::from_utf8_lossy(bytes).into_owned());
}

/// Show `visit` the desktop's clipboard: which arrival it is, and `len` bytes
/// of UTF-8 — null and 0 before the desktop has provided any. The clipboard's
/// lock is held for the call.
///
/// # Safety
/// As [`wlshare_client_with_frame`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_with_clipboard(client: *const Client, visit: WlshareClipboardFn, ctx: *mut c_void) {
    let Some(client) = (unsafe { client.as_ref() }) else { return };
    client.with_clipboard(|generation, text| match text {
        Some(text) => visit(ctx, generation, text.as_ptr(), text.len()),
        None => visit(ctx, generation, std::ptr::null(), 0),
    });
}

/// The next `frames` of the desktop's sound, as 48 kHz stereo into `left` and
/// `right` — silence where there is none yet, or for a null client. For the
/// audio device's render thread: the lock it takes is held for the copy.
///
/// # Safety
/// `client` is live or null, and `left` and `right` each have room for
/// `frames` floats. The two must not overlap; buffers that do are left as
/// they are, since two mutable slices over the same floats cannot be made.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_read_audio(client: *const Client, left: *mut f32, right: *mut f32, frames: usize) {
    if left.is_null() || right.is_null() || frames == 0 {
        return;
    }
    let Some(bytes) = frames.checked_mul(size_of::<f32>()) else { return };
    let (l, r) = (left as usize, right as usize);
    if l < r.saturating_add(bytes) && r < l.saturating_add(bytes) {
        return;
    }
    let (left, right) = unsafe { (std::slice::from_raw_parts_mut(left, frames), std::slice::from_raw_parts_mut(right, frames)) };
    match unsafe { client.as_ref() } {
        Some(client) => client.read_audio(left, right),
        None => {
            left.fill(0.0);
            right.fill(0.0);
        }
    }
}

/// The wheel notches a scroll comes to, into `output` as button-mask bits, and how
/// many there were. `delta` is the pointer's `MouseWheelDelta`, and
/// `horizontal` says which wheel it came from. A scroll too small for a notch
/// is kept for the next one.
///
/// # Safety
/// `output` has room for `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn wlshare_client_wheel(client: *const Client, delta: i32, horizontal: bool, output: *mut u8, cap: usize) -> usize {
    let Some(client) = (unsafe { client.as_ref() }) else { return 0 };
    let notches = client.wheel(delta, horizontal);
    let room = notches.len().min(cap);
    if !output.is_null() && room > 0 {
        unsafe { std::ptr::copy_nonoverlapping(notches.as_ptr(), output, room) };
    }
    room
}

/// The X11 keysym for a key: its virtual key code, its scan code, whether it
/// is extended, and the character it types with Control and Alt let go, as a
/// Unicode scalar — or 0 for a key that types nothing. 0 back is a key not to
/// send.
#[unsafe(no_mangle)]
pub extern "C" fn wlshare_keysym(vk: u16, scan: u16, extended: bool, character: u32) -> u32 {
    crate::keysym::keysym(vk, scan, extended, char::from_u32(character).filter(|c| *c != '\0')).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_key_is_its_keysym_and_a_key_with_none_is_zero() {
        assert_eq!(wlshare_keysym(0x41, 0x1e, false, u32::from('A')), 0x41);
        assert_eq!(wlshare_keysym(0x0d, 0x1c, true, u32::from('\r')), 0xff8d);
        assert_eq!(wlshare_keysym(0x14, 0x3a, false, 0), 0, "Caps Lock");
        // Not a scalar at all: a lone surrogate the app should never send.
        assert_eq!(wlshare_keysym(0x41, 0x1e, false, 0xd800), 0);
    }

    #[test]
    fn a_string_that_does_not_fit_is_cut_on_a_character_and_says_how_long_it_was() {
        let mut out = [0x55 as c_char; 5];
        let len = unsafe { copy_out("ab画面", out.as_mut_ptr(), out.len()) };
        assert_eq!(len, "ab画面".len());
        // Four bytes of room: "ab" and not half of 画.
        assert_eq!(&out[..3], &[b'a' as c_char, b'b' as c_char, 0]);
    }

    /// Separate buffers get the silence a null client has; one buffer passed
    /// as both, or two that share floats, is left as it was rather than made
    /// into two mutable slices over the same memory.
    #[test]
    fn overlapping_audio_buffers_are_left_untouched() {
        let (mut left, mut right) = ([9.0f32; 4], [9.0f32; 4]);
        unsafe { wlshare_client_read_audio(std::ptr::null(), left.as_mut_ptr(), right.as_mut_ptr(), 4) };
        assert_eq!((left, right), ([0.0; 4], [0.0; 4]));

        let mut both = [9.0f32; 6];
        let p = both.as_mut_ptr();
        unsafe { wlshare_client_read_audio(std::ptr::null(), p, p, 4) };
        unsafe { wlshare_client_read_audio(std::ptr::null(), p, p.add(2), 4) };
        unsafe { wlshare_client_read_audio(std::ptr::null(), p.add(2), p, 4) };
        assert_eq!(both, [9.0; 6]);

        // Adjacent is not overlapping.
        unsafe { wlshare_client_read_audio(std::ptr::null(), p, p.add(3), 3) };
        assert_eq!(both, [0.0; 6]);
    }

    #[test]
    fn a_null_client_is_a_no_op() {
        let mut notches = [0u8; 4];
        unsafe {
            wlshare_client_pointer(std::ptr::null(), 1, 2, 3);
            wlshare_client_key(std::ptr::null(), true, 0x61);
            wlshare_client_surface(std::ptr::null(), 800, 600, 2.0);
            wlshare_client_set_clipboard(std::ptr::null(), c"画面".as_ptr().cast(), "画面".len());
            assert_eq!(wlshare_client_wheel(std::ptr::null(), 120, false, notches.as_mut_ptr(), notches.len()), 0);
            assert_eq!(wlshare_client_error(std::ptr::null(), std::ptr::null_mut(), 0), 0);
            wlshare_client_close(std::ptr::null_mut());
        }
    }
}
