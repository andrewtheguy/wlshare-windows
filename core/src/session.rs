//! One connection, from the TCP handshake to the socket closing.
//!
//! This is the daemon's `session.rs` read backwards. It runs on its own thread,
//! on a current-thread tokio runtime, and it owns everything about the wire; the
//! window owns nothing but pixels it reads under a lock and events it posts to a
//! channel. The two never block each other for longer than a memcpy.
//!
//! What it speaks, and why:
//!
//! - **ZRLE**, on one inflate stream for the whole connection, decoded straight
//!   into the framebuffer. Raw is listed too because the server sends it before
//!   the first `SetEncodings` and there is no arranging otherwise.
//! - **The server's own pixel format.** Asking for `XRGB8888` is asking for the
//!   framebuffer's bytes as they are, which is also what the window's bitmap
//!   reads; no pixel is swizzled anywhere between the compositor and the screen.
//! - **Cursor and Cursor With Alpha**, because captured frames have no pointer
//!   painted in them and a client that does not draw one has no pointer at all.
//! - **Fence and ContinuousUpdates**, so frames arrive as the desktop changes
//!   with one update in flight, rather than one round trip per frame.
//! - **The density extension**, which is what the window's 1×/2× is: the window
//!   says what scale the desktop should be drawn at, the server draws it at that
//!   scale, and `SetDesktopSize` asks for a framebuffer the size of the window in
//!   device pixels. The picture is then one device pixel per framebuffer pixel
//!   and never resampled, at either scale.
//!
//! The clipboard, the sound, the camera, the microphone and output selection
//! are not spoken. Their pseudo-encodings are not listed, so the server never
//! offers them.

use std::future::{Future, pending};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use anyhow::{Context as _, bail};
use tokio::io::{AsyncRead, AsyncReadExt as _, AsyncWrite, AsyncWriteExt as _};
use tokio::net::TcpStream;
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::sync::mpsc::UnboundedReceiver;
use tokio::time::Instant;
use wlshare_rfb::client::{self, RectBody, ServerMsg};
use wlshare_rfb::cursor::CursorImage;
use wlshare_rfb::density::from_fixed;
use wlshare_rfb::msg::{FENCE_REQUEST, PROTOCOL_VERSION, SECURITY_NONE};
use wlshare_rfb::pixel::PixelFormat;
use wlshare_rfb::rsa_aes::{self, ClientKey, Credentials, FrameReader, Sealer, Strength};
use wlshare_rfb::zrle::ZrleDecoder;
use wlshare_rfb::{
    ENCODING_CONTINUOUS_UPDATES, ENCODING_CURSOR, ENCODING_CURSOR_WITH_ALPHA, ENCODING_DENSITY, ENCODING_DESKTOP_SIZE,
    ENCODING_EXTENDED_DESKTOP_SIZE, ENCODING_FENCE, ENCODING_RAW, ENCODING_ZRLE,
};

use crate::framebuffer::Framebuffer;

/// Listed in the client's order of preference, and deliberately short: a
/// pseudo-encoding here is a promise to understand what it turns on, and
/// [`client::parse`] ends the connection over a rectangle nobody asked for.
const ENCODINGS: &[i32] = &[
    ENCODING_ZRLE,
    ENCODING_RAW,
    ENCODING_CURSOR_WITH_ALPHA,
    ENCODING_CURSOR,
    ENCODING_EXTENDED_DESKTOP_SIZE,
    ENCODING_DESKTOP_SIZE,
    ENCODING_FENCE,
    ENCODING_CONTINUOUS_UPDATES,
    ENCODING_DENSITY,
];

/// How long to wait for the far end to answer at all.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(20);
/// How long the handshake may take once connected, through ServerInit: a
/// server that accepts a socket and never answers would otherwise leave the
/// window connecting for good. Long enough for a PAM login that is slow to say
/// no.
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(30);
/// How long a live resize settles before the desktop is asked to follow it.
/// A drag posts a size every frame, and each one the server honoured would be a
/// compositor mode change and a full repaint.
const RESIZE_SETTLE: Duration = Duration::from_millis(120);
/// Read this much of the socket at a time.
const CHUNK: usize = 256 * 1024;

/// Where to connect and as whom.
#[derive(Debug, Clone)]
pub struct Config {
    pub host: String,
    pub port: u16,
    pub username: String,
    /// Empty asks for the `None` security type; anything else asks for RSA-AES,
    /// which is the only type this client authenticates with.
    pub password: String,
}

/// What the window asks the desktop to be: its size in device pixels, and the
/// scale the desktop is drawn at — the window's 1× or 2×.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Surface {
    pub width: u16,
    pub height: u16,
    pub scale: f64,
}

impl Surface {
    fn is_usable(&self) -> bool {
        self.width > 0 && self.height > 0 && (0.5..=8.0).contains(&self.scale)
    }
}

/// What the window asks the session to do.
#[derive(Debug, Clone)]
pub enum Command {
    /// The button mask and the position, in the window's device pixels.
    Pointer { buttons: u8, x: u16, y: u16 },
    Key { down: bool, keysym: u32 },
    Surface(Surface),
    Shutdown,
}

/// How far the session has got.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum State {
    Connecting,
    /// The handshake is done and the framebuffer is the desktop's size.
    Ready,
    /// The session is over, for the reason in [`Status::error`] if it failed.
    Closed,
}

#[derive(Debug, Clone)]
pub struct Status {
    pub state: State,
    pub error: Option<String>,
    /// The desktop's name, from ServerInit.
    pub name: String,
    /// The scale the server says the framebuffer is drawn at, which is what
    /// the density extension answers a `ClientDensity` with.
    pub scale: f64,
    /// How many updates have been decoded into the framebuffer. A session that
    /// is Ready with none of these is one whose desktop has not moved.
    pub frames: u64,
}

impl Default for Status {
    fn default() -> Self {
        Self { state: State::Connecting, error: None, name: String::new(), scale: 1.0, frames: 0 }
    }
}

/// Everything the window reads and the session writes. One lock each, held for
/// a decode or a copy and never across an await.
pub struct Shared {
    pub framebuffer: Mutex<Framebuffer>,
    pub status: Mutex<Status>,
    /// The pointer shape as it last changed, `None` for a pointer that is
    /// hidden or has not arrived. The generation is what tells the window it
    /// is looking at a shape it has not seen.
    pub cursor: Mutex<(u64, Option<CursorImage>)>,
    /// Called from the session's thread whenever there is something new to
    /// draw. The window uses it to post itself a redraw; it must not block.
    wake: Mutex<Option<Box<dyn Fn() + Send + Sync>>>,
}

impl Default for Shared {
    fn default() -> Self {
        Self {
            framebuffer: Mutex::new(Framebuffer::new(0, 0)),
            status: Mutex::new(Status::default()),
            cursor: Mutex::new((0, None)),
            wake: Mutex::new(None),
        }
    }
}

impl Shared {
    pub fn set_wake(&self, wake: Option<Box<dyn Fn() + Send + Sync>>) {
        *self.wake.lock().unwrap() = wake;
    }

    pub fn wake(&self) {
        // Cloned out from under the lock would need the box to be Clone; called
        // under it, a callback that re-entered would deadlock. It does not:
        // the window's is one post to its dispatcher queue.
        if let Some(wake) = self.wake.lock().unwrap().as_ref() {
            wake();
        }
    }

    fn set_state(&self, state: State) {
        self.status.lock().unwrap().state = state;
        self.wake();
    }

    /// Record why the session ended. The first reason wins: a failure usually
    /// causes a second one on the way out.
    pub fn fail(&self, error: String) {
        let mut status = self.status.lock().unwrap();
        status.state = State::Closed;
        if status.error.is_none() {
            status.error = Some(error);
        }
        drop(status);
        self.wake();
    }
}

/// The socket's read side, opened frame by frame after RSA-AES.
enum Reader {
    Plain(OwnedReadHalf),
    Sealed(Box<FrameReader<OwnedReadHalf>>),
}

impl Reader {
    async fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        match self {
            Self::Plain(r) => r.read(buf).await,
            Self::Sealed(r) => r.read(buf).await,
        }
    }
}

/// The socket's write side: whole messages, each sealed into frames after
/// RSA-AES. Generic over what it writes to so that the tests can read back what
/// the session decided to send.
struct Writer<W> {
    inner: W,
    sealer: Option<Sealer>,
}

impl<W: AsyncWrite + Unpin> Writer<W> {
    async fn send(&mut self, message: &[u8]) -> std::io::Result<()> {
        match &mut self.sealer {
            Some(sealer) => self.inner.write_all(&sealer.frame(message)).await,
            None => self.inner.write_all(message).await,
        }
    }
}

/// Run one session to its end. The error it returns is the one the window
/// shows; it is recorded in [`Shared::status`] either way.
pub async fn run(config: Config, surface: Surface, shared: Arc<Shared>, commands: UnboundedReceiver<Command>) -> anyhow::Result<()> {
    let result = connect_and_run(config, surface, &shared, commands).await;
    match &result {
        Ok(()) => shared.set_state(State::Closed),
        Err(error) => shared.fail(format!("{error:#}")),
    }
    result
}

async fn connect_and_run(config: Config, surface: Surface, shared: &Arc<Shared>, mut commands: UnboundedReceiver<Command>) -> anyhow::Result<()> {
    // Before the socket, not after: generating one takes real CPU time, and the
    // server's handshake is on a clock.
    let key = match config.password.is_empty() {
        true => None,
        false => Some(ClientKey::generate().context("generating this session's RSA key")?),
    };

    // Beside the commands rather than in front of them: closing a client sends
    // Shutdown and then waits for this thread, and a socket that is still
    // connecting — or a server that accepts one and never sends its version —
    // would otherwise hold the window for as long as it pleased.
    let mut surface = surface;
    let connecting = async {
        let socket = tokio::time::timeout(CONNECT_TIMEOUT, TcpStream::connect((config.host.as_str(), config.port)))
            .await
            .with_context(|| format!("connecting to {}:{} took over {CONNECT_TIMEOUT:?}", config.host, config.port))?
            .with_context(|| format!("connecting to {}:{}", config.host, config.port))?;
        socket.set_nodelay(true)?;
        let (reader, writer) = socket.into_split();
        tokio::time::timeout(HANDSHAKE_TIMEOUT, handshake(reader, writer, &config, key))
            .await
            .with_context(|| format!("{}:{} did not finish its handshake within {HANDSHAKE_TIMEOUT:?}", config.host, config.port))?
    };
    let Some((mut reader, mut writer, init)) = while_connecting(&mut commands, &mut surface, connecting).await.transpose()? else {
        return Ok(());
    };

    {
        let mut status = shared.status.lock().unwrap();
        status.name = init.name.clone();
        status.state = State::Ready;
    }
    shared.framebuffer.lock().unwrap().resize(init.width, init.height)?;
    shared.wake();

    // The server's own format, so that nothing on this path swizzles a pixel.
    writer.send(&client::set_pixel_format(&Framebuffer::FORMAT)).await?;
    writer.send(&client::set_encodings(ENCODINGS)).await?;

    let mut session = Live {
        shared: Arc::clone(shared),
        zrle: ZrleDecoder::default(),
        format: Framebuffer::FORMAT,
        surface,
        asked_scale: None,
        asked_size: None,
        awaiting_scale: false,
        held_surface: None,
        cursor_generation: 0,
    };
    session.ask_for(&mut writer, surface).await?;
    session.stream_whole_desktop(&mut writer).await?;

    let mut buf = Vec::with_capacity(CHUNK);
    let mut chunk = vec![0u8; CHUNK];
    let mut settle: Option<Instant> = None;

    loop {
        tokio::select! {
            read = reader.read(&mut chunk) => {
                let read = read.context("reading from the server")?;
                if read == 0 {
                    return Ok(());
                }
                buf.extend_from_slice(&chunk[..read]);
                let mut at = 0;
                while let Some((msg, used)) = client::parse(&buf[at..]).context("parsing what the server sent")? {
                    at += used;
                    session.handle(msg, &mut writer).await?;
                }
                buf.drain(..at);
            }
            command = commands.recv() => {
                let Some(command) = command else { return Ok(()) };
                match command {
                    Command::Shutdown => return Ok(()),
                    Command::Surface(wanted) => {
                        // Coalesced, not queued: a live resize posts one of
                        // these a frame and only the last is worth honouring.
                        // Against the surface the window last said, not the one
                        // last asked for — a window dragged out and back before
                        // the settle would otherwise leave the timer armed for a
                        // size it only passed through.
                        if wanted.is_usable() && wanted != session.surface {
                            session.surface = wanted;
                            settle = Some(Instant::now() + RESIZE_SETTLE);
                        }
                    }
                    Command::Pointer { buttons, x, y } => {
                        let (x, y) = session.to_framebuffer(x, y);
                        writer.send(&client::pointer_event(buttons, x, y)).await?;
                    }
                    Command::Key { down, keysym } => writer.send(&client::key_event(down, keysym)).await?,
                }
            }
            () = async { match settle { Some(at) => tokio::time::sleep_until(at).await, None => pending().await } } => {
                settle = None;
                let wanted = session.surface;
                session.ask_for(&mut writer, wanted).await?;
            }
        }
    }
}

/// Run the handshake while the window is still being listened to, and answer
/// `None` if the window gave up before it finished — the session is then over
/// before it began.
///
/// Input to a desktop that does not exist yet is dropped. The window's surface
/// is not: a window resized while connecting, or switched between 1× and 2×,
/// is the surface the session starts at.
async fn while_connecting<T>(commands: &mut UnboundedReceiver<Command>, surface: &mut Surface, work: impl Future<Output = T>) -> Option<T> {
    tokio::pin!(work);
    loop {
        tokio::select! {
            done = &mut work => return Some(done),
            command = commands.recv() => match command {
                None | Some(Command::Shutdown) => return None,
                Some(Command::Surface(wanted)) if wanted.is_usable() => *surface = wanted,
                Some(_) => {}
            },
        }
    }
}

/// RFB 3.8's handshake from the client's end, through the security type to
/// ServerInit.
async fn handshake(
    mut reader: OwnedReadHalf,
    mut writer: OwnedWriteHalf,
    config: &Config,
    key: Option<ClientKey>,
) -> anyhow::Result<(Reader, Writer<OwnedWriteHalf>, client::ServerInit)> {
    client::read_version(&mut reader).await.context("reading the server's version")?;
    writer.write_all(PROTOCOL_VERSION).await?;

    let offered = client::read_security_types(&mut reader).await.context("reading the security types")?;
    let (mut reader, mut writer) = match key {
        Some(key) => {
            // RSA-AES at the widest both ends have, which is how the password
            // is protected and the rest of the session encrypted.
            let chosen = [rsa_aes::SECURITY_RSA_AES_256, rsa_aes::SECURITY_RSA_AES_128]
                .into_iter()
                .find(|t| offered.contains(t))
                .with_context(|| format!("a password was given and the server offers no RSA-AES, only {offered:?}"))?;
            writer.write_all(&[chosen]).await?;
            let strength = Strength::of(chosen).expect("one of the two just chosen");
            let exchange = rsa_aes::begin(&mut reader, &mut writer, strength, key).await.context("the RSA-AES key exchange")?;
            log::info!("the server's key is {}, asking for {:?}", exchange.fingerprint(), exchange.subtype());
            let credentials = Credentials { username: config.username.clone(), password: config.password.clone() };
            let session = exchange.login(&mut writer, &credentials).await.context("sending the credentials")?;
            (Reader::Sealed(Box::new(FrameReader::new(reader, session.opener))), Writer { inner: writer, sealer: Some(session.sealer) })
        }
        None => {
            if !offered.contains(&SECURITY_NONE) {
                bail!("the server wants a password: it offers security types {offered:?} and none of them is None");
            }
            writer.write_all(&[SECURITY_NONE]).await?;
            (Reader::Plain(reader), Writer { inner: writer, sealer: None })
        }
    };

    let init = match &mut reader {
        Reader::Plain(r) => finish_handshake(r, &mut writer).await,
        Reader::Sealed(r) => finish_handshake(r, &mut writer).await,
    }?;
    Ok((reader, writer, init))
}

async fn finish_handshake<R: AsyncRead + Unpin, W: AsyncWrite + Unpin>(reader: &mut R, writer: &mut Writer<W>) -> anyhow::Result<client::ServerInit> {
    client::read_security_result(reader).await.context("the login")?;
    writer.send(&client::client_init()).await?;
    let init = client::read_server_init(reader).await.context("reading ServerInit")?;
    if init.format.bits_per_pixel != 32 {
        bail!("the server's framebuffer is {} bits per pixel, and this client only reads 32", init.format.bits_per_pixel);
    }
    log::info!("connected to {:?}, {}x{}", init.name, init.width, init.height);
    Ok(init)
}

/// The session once it is past the handshake: what it needs to turn the
/// server's messages into pixels and the window's events into messages.
struct Live {
    shared: Arc<Shared>,
    zrle: ZrleDecoder,
    format: PixelFormat,
    /// The window's surface as it last said.
    surface: Surface,
    asked_scale: Option<f64>,
    asked_size: Option<(u16, u16)>,
    /// Set while a declared density is waiting for the `OutputScale` that
    /// answers it. Nothing else may go out until it arrives ([`Live::ask_for`]).
    awaiting_scale: bool,
    /// The window's latest surface, waiting for the density in flight to be
    /// answered ([`Live::ask_for`]).
    held_surface: Option<Surface>,
    cursor_generation: u64,
}

impl Live {
    /// Ask the desktop to be this window: drawn at the scale the window chose,
    /// and as many pixels across as the window has device pixels. Together those
    /// make the framebuffer a device-pixel-for-device-pixel match.
    ///
    /// **One at a time.** The server applies both through
    /// wlr-output-management, whose configurations carry a serial the
    /// compositor bumps on every commit, so the second of two in flight is
    /// cancelled and comes back as an invalid layout. The density goes first,
    /// and while it is in flight the window's latest surface — its size, or a
    /// density of its own — waits in `held_surface` for the `OutputScale` the
    /// extension promises for every declaration; [`Live::released_by`]
    /// recognises it, and the surface is then asked for as if just posted.
    async fn ask_for<W: AsyncWrite + Unpin>(&mut self, writer: &mut Writer<W>, surface: Surface) -> anyhow::Result<()> {
        if !surface.is_usable() {
            return Ok(());
        }
        if self.awaiting_scale {
            // Replaces whatever older surface was waiting.
            self.held_surface = Some(surface);
            return Ok(());
        }
        let size = (surface.width, surface.height);
        if self.asked_scale != Some(surface.scale) {
            writer.send(&client::client_density(surface.scale)).await?;
            self.asked_scale = Some(surface.scale);
            self.awaiting_scale = true;
            self.held_surface = Some(surface);
            return Ok(());
        }
        if self.asked_size != Some(size) {
            writer.send(&client::set_desktop_size(surface.width, surface.height)).await?;
            self.asked_size = Some(size);
        }
        Ok(())
    }

    /// Whether an `OutputScale` of this scale is the answer to the density that
    /// was declared, rather than the one every `SetEncodings` is answered with
    /// or one another client caused. Only the answer releases the size.
    fn released_by(&self, scale: f64) -> bool {
        self.asked_scale.is_some_and(|asked| (asked - scale).abs() < 0.001)
    }

    /// Turn on continuous updates over the whole framebuffer and ask for one
    /// full repaint to start from. Called again after every size change: the
    /// region enabled is the framebuffer that was, and the new one is bigger or
    /// smaller than it.
    async fn stream_whole_desktop<W: AsyncWrite + Unpin>(&mut self, writer: &mut Writer<W>) -> anyhow::Result<()> {
        let (width, height) = {
            let fb = self.shared.framebuffer.lock().unwrap();
            (fb.width(), fb.height())
        };
        writer.send(&client::enable_continuous_updates(true, 0, 0, width, height)).await?;
        writer.send(&client::framebuffer_update_request(false, 0, 0, width, height)).await?;
        Ok(())
    }

    /// The window's device pixels as framebuffer pixels. They are the same
    /// thing once the desktop has followed a resize, and briefly not while it
    /// is still catching up, which is exactly when a click must still land
    /// where it was aimed.
    fn to_framebuffer(&self, x: u16, y: u16) -> (u16, u16) {
        let fb = self.shared.framebuffer.lock().unwrap();
        let map = |value: u16, from: u16, to: u16| -> u16 {
            if from == 0 || to == 0 {
                return 0;
            }
            let scaled = u32::from(value) * u32::from(to) / u32::from(from);
            scaled.min(u32::from(to) - 1) as u16
        };
        (map(x, self.surface.width, fb.width()), map(y, self.surface.height, fb.height()))
    }

    async fn handle<W: AsyncWrite + Unpin>(&mut self, msg: ServerMsg, writer: &mut Writer<W>) -> anyhow::Result<()> {
        match msg {
            ServerMsg::Update(rects) => {
                log::debug!("an update of {} rectangles", rects.len());
                let mut resized = false;
                let mut drew = false;
                for rect in rects {
                    match self.apply(rect)? {
                        Applied::Nothing => {}
                        Applied::Drew => drew = true,
                        Applied::Resized => resized = true,
                    }
                }
                if resized {
                    self.stream_whole_desktop(writer).await?;
                }
                if drew || resized {
                    self.shared.status.lock().unwrap().frames += 1;
                    self.shared.wake();
                }
            }
            // Echoed as the same fence without the request bit, which is what
            // lets the server send the next update.
            ServerMsg::Fence { flags, payload } => {
                if flags & FENCE_REQUEST != 0 {
                    writer.send(&client::fence(flags & !FENCE_REQUEST, &payload)).await?;
                }
            }
            ServerMsg::OutputScale { width, height, fixed } => {
                let scale = from_fixed(fixed);
                log::debug!("the desktop is {width}x{height} at scale {scale}");
                self.shared.status.lock().unwrap().scale = scale;
                self.shared.wake();
                if self.awaiting_scale && self.released_by(scale) {
                    self.awaiting_scale = false;
                    if let Some(held) = self.held_surface.take() {
                        self.ask_for(writer, held).await?;
                    }
                }
            }
            // No clipboard is listed, so neither kind is answered; a server
            // that sends one anyway has it framed and dropped here.
            ServerMsg::CutText(_) | ServerMsg::ExtendedCutText(_) => log::debug!("a clipboard message nobody asked for; ignored"),
            // The server sends this once to say it understands them, and again
            // if they are ever turned off. Either way there is nothing to do.
            ServerMsg::EndOfContinuousUpdates => log::debug!("the server acknowledged continuous updates"),
            // The audio extension is not listed, so these never come; framed,
            // they are dropped rather than the session.
            ServerMsg::AudioBegin | ServerMsg::AudioEnd | ServerMsg::AudioFrame(_) => log::debug!("sound nobody asked for; ignored"),
        }
        Ok(())
    }

    fn apply(&mut self, rect: client::Rect) -> anyhow::Result<Applied> {
        let client::Rect { x, y, width, height, body } = rect;
        log::trace!("a {} rectangle of {width}x{height} at {x},{y}", match &body {
            RectBody::Raw(_) => "Raw",
            RectBody::Zrle(_) => "ZRLE",
            RectBody::DesktopSize => "DesktopSize",
            RectBody::ExtendedDesktopSize { .. } => "ExtendedDesktopSize",
            RectBody::Cursor { .. } => "Cursor",
            RectBody::AlphaCursor(_) => "AlphaCursor",
            RectBody::Audio => "Audio",
        });
        match body {
            RectBody::Raw(pixels) => {
                let mut fb = self.shared.framebuffer.lock().unwrap();
                if !fb.put_raw(x, y, width, height, &pixels) {
                    bail!("a Raw rectangle of {width}x{height} at {x},{y} is not inside a {}x{} framebuffer", fb.width(), fb.height());
                }
                Ok(Applied::Drew)
            }
            RectBody::Zrle(payload) => {
                let mut fb = self.shared.framebuffer.lock().unwrap();
                let (fb_width, fb_height) = (fb.width(), fb.height());
                let Some((out, stride)) = fb.rect_mut(x, y, width, height) else {
                    bail!("a ZRLE rectangle of {width}x{height} at {x},{y} is not inside a {fb_width}x{fb_height} framebuffer");
                };
                self.zrle
                    .decode_rect(&payload, usize::from(width), usize::from(height), &self.format, out, stride)
                    .with_context(|| format!("decoding a {width}x{height} ZRLE rectangle at {x},{y}"))?;
                fb.damage_rect(x, y, width, height);
                Ok(Applied::Drew)
            }
            RectBody::DesktopSize => {
                self.resize(width, height)?;
                Ok(Applied::Resized)
            }
            // The rectangle's y is the status: anything but zero answers a
            // request of this client's and changes nothing.
            RectBody::ExtendedDesktopSize { .. } if y != 0 => {
                log::info!("the server refused a {width}x{height} desktop with status {y}");
                Ok(Applied::Nothing)
            }
            RectBody::ExtendedDesktopSize { .. } => {
                self.resize(width, height)?;
                Ok(Applied::Resized)
            }
            RectBody::Cursor { pixels, mask } => {
                let expected = (usize::from(width) * usize::from(height) * 4, usize::from(width).div_ceil(8) * usize::from(height));
                if (pixels.len(), mask.len()) != expected {
                    bail!("a {width}x{height} cursor of {} pixel bytes and {} mask bytes", pixels.len(), mask.len());
                }
                self.set_cursor(CursorImage::from_masked_rect(&self.format, width, height, (x, y), &pixels, &mask));
                Ok(Applied::Nothing)
            }
            RectBody::AlphaCursor(rgba) => {
                if rgba.len() != usize::from(width) * usize::from(height) * 4 {
                    bail!("a {width}x{height} cursor with alpha of {} bytes", rgba.len());
                }
                self.set_cursor(CursorImage::from_alpha_rect(width, height, (x, y), rgba));
                Ok(Applied::Nothing)
            }
            // The audio extension's announcement, which only comes to a client
            // that listed it; this one did not.
            RectBody::Audio => Ok(Applied::Nothing),
        }
    }

    fn resize(&mut self, width: u16, height: u16) -> anyhow::Result<()> {
        log::info!("the desktop is now {width}x{height}");
        self.shared.framebuffer.lock().unwrap().resize(width, height)
    }

    fn set_cursor(&mut self, image: Option<CursorImage>) {
        self.cursor_generation += 1;
        *self.shared.cursor.lock().unwrap() = (self.cursor_generation, image);
        self.shared.wake();
    }
}

/// What a rectangle did, which decides whether the window is woken and whether
/// continuous updates have to be turned on over a framebuffer of a new size.
enum Applied {
    Nothing,
    Drew,
    Resized,
}

#[cfg(test)]
mod tests {
    use wlshare_rfb::density::to_fixed;
    use wlshare_rfb::msg::{ClientMsg, Screen};

    use super::*;

    /// What the session wrote, read back with the server's own parser rather
    /// than by comparing bytes to bytes.
    fn sent(writer: &mut Writer<Vec<u8>>) -> Vec<ClientMsg> {
        let bytes = std::mem::take(&mut writer.inner);
        let mut messages = Vec::new();
        let mut at = 0;
        while at < bytes.len() {
            let (message, used) = wlshare_rfb::msg::parse(&bytes[at..]).expect("a message").expect("a whole message");
            messages.push(message);
            at += used;
        }
        messages
    }

    fn writer() -> Writer<Vec<u8>> {
        Writer { inner: Vec::new(), sealer: None }
    }

    fn live() -> Live {
        Live {
            shared: Arc::new(Shared::default()),
            zrle: ZrleDecoder::default(),
            format: Framebuffer::FORMAT,
            surface: Surface { width: 800, height: 600, scale: 2.0 },
            asked_scale: None,
            asked_size: None,
            awaiting_scale: false,
            held_surface: None,
            cursor_generation: 0,
        }
    }

    /// The bug this guards against is not hypothetical: sending both at once
    /// has the compositor cancel the second configuration, and the desktop
    /// answers a perfectly good size with *invalid layout*.
    #[tokio::test]
    async fn a_size_waits_for_the_density_it_goes_with_to_be_answered() {
        let mut live = live();
        let mut writer = writer();
        let doubled = Surface { width: 1600, height: 1200, scale: 2.0 };

        live.ask_for(&mut writer, doubled).await.unwrap();
        assert_eq!(sent(&mut writer), vec![ClientMsg::ClientDensity { fixed: to_fixed(2.0) }], "the density goes out alone");

        // The OutputScale that answers every SetEncodings, carrying the scale
        // the desktop is at now. Not the answer, so the size stays held.
        live.handle(ServerMsg::OutputScale { width: 1024, height: 768, fixed: to_fixed(1.0) }, &mut writer).await.unwrap();
        assert_eq!(sent(&mut writer), vec![]);

        live.handle(ServerMsg::OutputScale { width: 1024, height: 768, fixed: to_fixed(2.0) }, &mut writer).await.unwrap();
        assert_eq!(
            sent(&mut writer),
            vec![ClientMsg::SetDesktopSize { width: 1600, height: 1200, screens: vec![Screen::whole(1600, 1200)] }],
            "the answer releases the size"
        );

        // The same surface again is a window redrawing, not a window changing.
        live.ask_for(&mut writer, doubled).await.unwrap();
        assert_eq!(sent(&mut writer), vec![]);

        // A size change at the same density needs no configuration ahead of it.
        live.ask_for(&mut writer, Surface { width: 800, height: 600, scale: 2.0 }).await.unwrap();
        assert_eq!(
            sent(&mut writer),
            vec![ClientMsg::SetDesktopSize { width: 800, height: 600, screens: vec![Screen::whole(800, 600)] }]
        );
    }

    /// The window's 1×/2× switch is a density alone: the window has not
    /// changed size, so nothing but the density goes out, and nothing is held.
    #[tokio::test]
    async fn switching_between_1x_and_2x_at_one_size_sends_only_the_density() {
        let mut live = live();
        let mut writer = writer();
        live.ask_for(&mut writer, Surface { width: 1600, height: 1000, scale: 1.0 }).await.unwrap();
        live.handle(ServerMsg::OutputScale { width: 1600, height: 1000, fixed: to_fixed(1.0) }, &mut writer).await.unwrap();
        assert_eq!(
            sent(&mut writer),
            vec![
                ClientMsg::ClientDensity { fixed: to_fixed(1.0) },
                ClientMsg::SetDesktopSize { width: 1600, height: 1000, screens: vec![Screen::whole(1600, 1000)] },
            ]
        );

        live.ask_for(&mut writer, Surface { width: 1600, height: 1000, scale: 2.0 }).await.unwrap();
        live.handle(ServerMsg::OutputScale { width: 1600, height: 1000, fixed: to_fixed(2.0) }, &mut writer).await.unwrap();
        assert_eq!(sent(&mut writer), vec![ClientMsg::ClientDensity { fixed: to_fixed(2.0) }], "the size is already the window's");

        live.ask_for(&mut writer, Surface { width: 1600, height: 1000, scale: 1.0 }).await.unwrap();
        assert_eq!(sent(&mut writer), vec![ClientMsg::ClientDensity { fixed: to_fixed(1.0) }], "and back");
    }

    /// The same one at a time, from the other side: a window that is resized
    /// while a density is in flight must not slip a size past it, and what goes
    /// out when the answer comes is the size the window is now.
    #[tokio::test]
    async fn a_resize_that_catches_up_with_a_density_waits_for_it_as_well() {
        let mut live = live();
        let mut writer = writer();

        live.ask_for(&mut writer, Surface { width: 1600, height: 1200, scale: 2.0 }).await.unwrap();
        assert_eq!(sent(&mut writer), vec![ClientMsg::ClientDensity { fixed: to_fixed(2.0) }]);

        live.ask_for(&mut writer, Surface { width: 1400, height: 1000, scale: 2.0 }).await.unwrap();
        assert_eq!(sent(&mut writer), vec![], "a size at the density still in flight waits with it");

        live.handle(ServerMsg::OutputScale { width: 1024, height: 768, fixed: to_fixed(2.0) }, &mut writer).await.unwrap();
        assert_eq!(
            sent(&mut writer),
            vec![ClientMsg::SetDesktopSize { width: 1400, height: 1000, screens: vec![Screen::whole(1400, 1000)] }],
            "the size that goes out is the window's latest, not the one it held first"
        );

        // The density is answered, so the next size needs nothing ahead of it.
        live.ask_for(&mut writer, Surface { width: 1200, height: 900, scale: 2.0 }).await.unwrap();
        assert_eq!(
            sent(&mut writer),
            vec![ClientMsg::SetDesktopSize { width: 1200, height: 900, screens: vec![Screen::whole(1200, 900)] }]
        );
    }

    /// A window that changes scale again before the first density is answered
    /// must not have a second density slip past it either: the latest waits,
    /// and goes out once the first is answered.
    #[tokio::test]
    async fn a_density_that_catches_up_with_a_density_waits_for_it() {
        let mut live = live();
        let mut writer = writer();

        live.ask_for(&mut writer, Surface { width: 1600, height: 1200, scale: 2.0 }).await.unwrap();
        assert_eq!(sent(&mut writer), vec![ClientMsg::ClientDensity { fixed: to_fixed(2.0) }]);

        live.ask_for(&mut writer, Surface { width: 800, height: 600, scale: 1.0 }).await.unwrap();
        assert_eq!(sent(&mut writer), vec![], "one density in flight at a time");

        live.handle(ServerMsg::OutputScale { width: 1024, height: 768, fixed: to_fixed(2.0) }, &mut writer).await.unwrap();
        assert_eq!(sent(&mut writer), vec![ClientMsg::ClientDensity { fixed: to_fixed(1.0) }], "the latest density follows the answer");

        live.handle(ServerMsg::OutputScale { width: 1024, height: 768, fixed: to_fixed(1.0) }, &mut writer).await.unwrap();
        assert_eq!(
            sent(&mut writer),
            vec![ClientMsg::SetDesktopSize { width: 800, height: 600, screens: vec![Screen::whole(800, 600)] }],
            "and the size goes with it"
        );
    }

    #[tokio::test]
    async fn a_fence_is_echoed_without_the_bit_that_asked_for_it() {
        let mut live = live();
        let mut writer = writer();
        live.handle(ServerMsg::Fence { flags: FENCE_REQUEST | 4, payload: vec![1, 2, 3] }, &mut writer).await.unwrap();
        assert_eq!(sent(&mut writer), vec![ClientMsg::Fence { flags: 4, payload: vec![1, 2, 3] }]);

        // One that asks for nothing is not echoed, or the two ends would echo
        // each other for as long as the session lasted.
        live.handle(ServerMsg::Fence { flags: 4, payload: vec![1] }, &mut writer).await.unwrap();
        assert_eq!(sent(&mut writer), vec![]);
    }

    #[tokio::test]
    async fn a_resize_turns_continuous_updates_on_over_the_framebuffer_that_is_now() {
        let mut live = live();
        let mut writer = writer();
        live.shared.framebuffer.lock().unwrap().resize(1024, 768).unwrap();

        let rect = client::Rect { x: 0, y: 0, width: 800, height: 600, body: RectBody::DesktopSize };
        live.handle(ServerMsg::Update(vec![rect]), &mut writer).await.unwrap();
        assert_eq!(
            sent(&mut writer),
            vec![
                ClientMsg::EnableContinuousUpdates { enable: true, x: 0, y: 0, width: 800, height: 600 },
                ClientMsg::FramebufferUpdateRequest { incremental: false, x: 0, y: 0, width: 800, height: 600 },
            ],
            "the region enabled was the old framebuffer's, and the new one is not that size"
        );
    }

    #[test]
    fn only_what_this_client_speaks_is_listed() {
        assert!(!ENCODINGS.contains(&wlshare_rfb::clipboard::ENCODING), "no clipboard");
        assert!(!ENCODINGS.contains(&wlshare_rfb::ENCODING_AUDIO), "no sound");
        assert!(ENCODINGS.contains(&ENCODING_DENSITY), "the density is what 1× and 2× are");
    }

    #[tokio::test]
    async fn clipboard_and_sound_a_server_sends_anyway_are_dropped_and_not_the_session() {
        let mut live = live();
        let mut writer = writer();
        let latin1 = [3, 0, 0, 0, 0, 0, 0, 2, 0xE9, b'a'];
        let (message, _) = client::parse(&latin1).unwrap().unwrap();
        live.handle(message, &mut writer).await.unwrap();
        live.handle(ServerMsg::AudioBegin, &mut writer).await.unwrap();
        live.handle(ServerMsg::AudioFrame(vec![0xFF, 0xF8, 0, 0]), &mut writer).await.unwrap();
        live.handle(ServerMsg::AudioEnd, &mut writer).await.unwrap();
        let announcement = client::Rect { x: 0, y: 0, width: 0, height: 0, body: RectBody::Audio };
        live.handle(ServerMsg::Update(vec![announcement]), &mut writer).await.unwrap();
        assert_eq!(sent(&mut writer), vec![], "none of it is answered");
    }

    #[test]
    fn a_pointer_lands_where_it_was_aimed_whatever_the_desktop_is_doing() {
        let live = live();
        // Matched, which is the steady state: the position is itself.
        live.shared.framebuffer.lock().unwrap().resize(800, 600).unwrap();
        assert_eq!(live.to_framebuffer(0, 0), (0, 0));
        assert_eq!(live.to_framebuffer(400, 300), (400, 300));
        assert_eq!(live.to_framebuffer(799, 599), (799, 599));

        // Mid-resize, the desktop still half the window: the fractions hold and
        // nothing lands outside the framebuffer.
        live.shared.framebuffer.lock().unwrap().resize(400, 300).unwrap();
        assert_eq!(live.to_framebuffer(400, 300), (200, 150));
        assert_eq!(live.to_framebuffer(799, 599), (399, 299));
        assert_eq!(live.to_framebuffer(65535, 65535), (399, 299));

        // No desktop at all, which is every event before ServerInit.
        live.shared.framebuffer.lock().unwrap().resize(0, 0).unwrap();
        assert_eq!(live.to_framebuffer(10, 10), (0, 0));
    }

    #[test]
    fn a_rectangle_outside_the_framebuffer_ends_the_session_rather_than_the_process() {
        let mut live = live();
        live.shared.framebuffer.lock().unwrap().resize(64, 64).unwrap();
        let rect = client::Rect { x: 60, y: 0, width: 8, height: 8, body: RectBody::Raw(vec![0; 8 * 8 * 4]) };
        assert!(live.apply(rect).is_err());

        let rect = client::Rect { x: 0, y: 0, width: 8, height: 8, body: RectBody::Zrle(vec![0; 4]) };
        assert!(live.apply(rect).is_err(), "a ZRLE payload that is not one is an error, not a panic");
    }

    #[test]
    fn a_cursor_whose_body_is_the_wrong_size_is_refused_before_it_can_panic() {
        // CursorImage asserts on a mismatch, so the check has to be here.
        let mut live = live();
        let rect = client::Rect { x: 0, y: 0, width: 4, height: 4, body: RectBody::AlphaCursor(vec![0; 15]) };
        assert!(live.apply(rect).is_err());
        let rect = client::Rect { x: 0, y: 0, width: 4, height: 4, body: RectBody::Cursor { pixels: vec![0; 64], mask: vec![0; 3] } };
        assert!(live.apply(rect).is_err());
    }

    #[test]
    fn an_empty_cursor_rectangle_is_a_pointer_that_is_not_there() {
        let mut live = live();
        let rect = client::Rect { x: 0, y: 0, width: 0, height: 0, body: RectBody::AlphaCursor(Vec::new()) };
        assert!(matches!(live.apply(rect), Ok(Applied::Nothing)));
        let (generation, image) = &*live.shared.cursor.lock().unwrap();
        assert_eq!(*generation, 1);
        assert!(image.is_none());
    }

    #[test]
    fn a_refused_resize_leaves_the_framebuffer_alone() {
        let mut live = live();
        live.shared.framebuffer.lock().unwrap().resize(64, 64).unwrap();
        // Status 1 in the rectangle's y: the request failed.
        let rect = client::Rect { x: 1, y: 1, width: 800, height: 600, body: RectBody::ExtendedDesktopSize { screens: Vec::new() } };
        assert!(matches!(live.apply(rect), Ok(Applied::Nothing)));
        assert_eq!(live.shared.framebuffer.lock().unwrap().width(), 64);

        let rect = client::Rect { x: 1, y: 0, width: 800, height: 600, body: RectBody::ExtendedDesktopSize { screens: Vec::new() } };
        assert!(matches!(live.apply(rect), Ok(Applied::Resized)));
        assert_eq!(live.shared.framebuffer.lock().unwrap().width(), 800);
    }

    #[test]
    fn a_surface_the_window_is_still_being_born_with_is_not_asked_for() {
        assert!(!Surface { width: 0, height: 600, scale: 2.0 }.is_usable());
        assert!(!Surface { width: 800, height: 0, scale: 2.0 }.is_usable());
        assert!(!Surface { width: 800, height: 600, scale: 0.0 }.is_usable());
        // The density extension's own bounds.
        assert!(!Surface { width: 800, height: 600, scale: 9.0 }.is_usable());
        assert!(Surface { width: 800, height: 600, scale: 1.0 }.is_usable());
        assert!(Surface { width: 800, height: 600, scale: 2.0 }.is_usable());
    }
}
