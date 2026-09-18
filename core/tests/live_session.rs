//! A session against a real wlshare, which no unit test can stand in for: the
//! handshake, the ZRLE stream, the cursor and the density extension all only
//! exist between two processes.
//!
//! Ignored by default and pointed at `WLSHARE_TEST_SERVER`, or `127.0.0.1:5900`,
//! as `WLSHARE_TEST_USERNAME` with `WLSHARE_TEST_PASSWORD`, or unauthenticated
//! when there is none. The core has nothing Windows in it, so these run from
//! the Linux checkout against the wlshare of the session they run in:
//!
//! ```text
//! cargo test --test live_session -- --ignored --nocapture --test-threads=1
//! ```
//!
//! They resize the desktop they talk to, so point them at one nobody is using.

use std::time::{Duration, Instant};

use wlshare_client_core::{BUTTON_LEFT, Client, Config, State, Surface, WHEEL_DELTA};

const PATIENCE: Duration = Duration::from_secs(20);

fn server() -> (String, u16) {
    let address = std::env::var("WLSHARE_TEST_SERVER").unwrap_or_else(|_| "127.0.0.1:5900".to_owned());
    let (host, port) = address.rsplit_once(':').expect("WLSHARE_TEST_SERVER is host:port");
    (host.to_owned(), port.parse().expect("a port"))
}

/// Poll until `done` or give up. Polling rather than the wake callback: what is
/// being tested is what the window would see, and the window sees this.
fn until<T>(what: &str, mut done: impl FnMut() -> Option<T>) -> T {
    let deadline = Instant::now() + PATIENCE;
    loop {
        if let Some(value) = done() {
            return value;
        }
        assert!(Instant::now() < deadline, "waited {PATIENCE:?} for {what}");
        std::thread::sleep(Duration::from_millis(20));
    }
}

fn connect(surface: Surface) -> Client {
    let _ = env_logger::builder().is_test(false).try_init();
    let (host, port) = server();
    let username = std::env::var("WLSHARE_TEST_USERNAME").unwrap_or_default();
    let password = std::env::var("WLSHARE_TEST_PASSWORD").unwrap_or_default();
    let client = Client::connect(Config { host, port, username, password }, surface);
    until("the handshake", || match client.status() {
        status if status.state == State::Ready => Some(()),
        status if status.state == State::Closed => panic!("the session ended: {:?}", status.error),
        _ => None,
    });
    client
}

#[test]
#[ignore = "needs a wlshare server; see the module comment"]
fn a_session_gets_a_desktop_and_paints_it() {
    let client = connect(Surface { width: 1024, height: 768, scale: 1.0 });
    let status = client.status();
    assert!(!status.name.is_empty(), "ServerInit names the desktop");
    println!("connected to {:?}", status.name);

    // The desktop painted at the size that was asked for. A previous test may
    // have left it another size, so this waits for both rather than looking
    // once: the resize and the repaint that follows it are two frames apart.
    let (damage, lit) = until("a painted 1024x768 desktop", || {
        client.with_frame(|fb, damage| {
            if (fb.width(), fb.height()) != (1024, 768) {
                return None;
            }
            let lit = fb.pixels().as_chunks::<4>().0.iter().filter(|p| p[..3] != [0, 0, 0]).count();
            (lit > 0).then_some((damage, lit))
        })
    });
    println!("1024x768, damage {damage:?}, {lit} pixels lit");
    let damage = damage.expect("a frame that painted damages what it painted");
    assert!(damage.width > 0 && damage.height > 0);
    assert!(client.status().frames > 0, "those pixels came from a decoded update, not from a resize");
}

/// Wait for the desktop to be this many pixels across at this scale, which is
/// what a window of that size at that choice of 1× or 2× asked for.
fn settled(client: &Client, width: u16, height: u16, scale: f64) {
    until(&format!("a {width}x{height} desktop at scale {scale}"), || {
        let status = client.status();
        let size = client.desktop_size();
        ((status.scale - scale).abs() < 0.01 && size == (width, height)).then_some(())
    });
    println!("{width}x{height} at scale {scale}");
}

#[test]
#[ignore = "needs a wlshare server; see the module comment"]
fn switching_between_1x_and_2x_redraws_the_desktop_at_that_scale() {
    // A window of 1024x768 device pixels at 2× gets a desktop of 1024x768
    // pixels drawn at scale 2 — 512x384 of desktop, twice as sharp.
    let client = connect(Surface { width: 1024, height: 768, scale: 2.0 });
    // Both, and waited for together: the size is asked for only once the scale
    // has been answered, so the two land one after the other.
    settled(&client, 1024, 768, 2.0);

    // The window's switch to 1×, at the same size: only the scale moves.
    client.surface(Surface { width: 1024, height: 768, scale: 1.0 });
    settled(&client, 1024, 768, 1.0);

    // And a resize at 1×, which is the window being dragged.
    client.surface(Surface { width: 800, height: 600, scale: 1.0 });
    settled(&client, 800, 600, 1.0);
}

#[test]
#[ignore = "needs a wlshare server; see the module comment"]
fn the_pointer_has_a_shape_and_input_is_taken() {
    let client = connect(Surface { width: 1024, height: 768, scale: 1.0 });

    // A desktop nobody has touched may have no pointer showing at all, and an
    // output with no pointer on it sends an empty cursor rectangle. Move it
    // first, and the shape follows.
    client.pointer(0, 400, 300);
    client.pointer(0, 512, 384);

    // wlshare never paints the pointer into a frame, so a client that is sent
    // no cursor has no pointer at all: this is the one that must arrive.
    let (generation, width, height, hotspot) = until("the cursor", || {
        client.with_cursor(|generation, image| {
            let image = image?;
            Some((generation, image.width(), image.height(), image.hotspot()))
        })
    });
    println!("cursor {width}x{height}, hotspot {hotspot:?}, shape {generation}");
    assert!(width > 0 && height > 0);
    assert!(hotspot.0 < width && hotspot.1 < height);

    // Nothing here asserts what the desktop did with these — that is the
    // daemon's own e2e — only that a session takes them and stays up.
    client.pointer(BUTTON_LEFT, 512, 384);
    client.pointer(0, 512, 384);
    for notch in client.wheel(-3 * WHEEL_DELTA, false) {
        client.pointer(notch, 512, 384);
        client.pointer(0, 512, 384);
    }
    client.key(true, 0xffe1); // Shift_L
    client.key(true, 0x41); // 'A', which the server cases for itself
    client.key(false, 0x41);
    client.key(false, 0xffe1);

    std::thread::sleep(Duration::from_millis(500));
    let status = client.status();
    assert_eq!(status.state, State::Ready, "the session survived the input: {:?}", status.error);
}
