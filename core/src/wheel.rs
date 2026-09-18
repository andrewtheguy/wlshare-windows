//! Scrolling, which RFB has no word for.
//!
//! There are no scroll events in RFB: a wheel notch is a button press and
//! release on one of four buttons above the three real ones, and the server
//! turns each press into one discrete axis event. Windows measures a scroll in
//! 120ths of a notch — `WHEEL_DELTA` — so a plain mouse wheel is a notch a
//! step, but a high-resolution wheel or a precision touchpad sends a stream of
//! small deltas that only add up to one. Those have to be gathered, and the
//! leftovers kept for the next event, or a slow scroll scrolls nothing at all.

/// The four wheel "buttons" of the RFB button mask, as the server reads them.
pub const WHEEL_UP: u8 = 8;
pub const WHEEL_DOWN: u8 = 16;
pub const WHEEL_LEFT: u8 = 32;
pub const WHEEL_RIGHT: u8 = 64;

/// Windows' `WHEEL_DELTA`: how much of a scroll is one notch.
pub const WHEEL_DELTA: i32 = 120;

/// The most notches one event may turn into. A flick on a touchpad is a large
/// delta; past this the desktop is scrolling faster than anyone reads, and each
/// notch is a round trip.
const MAX_NOTCHES: usize = 16;

/// The leftovers of scrolls too small to be a notch yet.
#[derive(Debug, Default)]
pub struct Wheel {
    horizontal: i32,
    vertical: i32,
}

impl Wheel {
    /// The wheel buttons a scroll comes to, in the order they should be
    /// clicked. `delta` is the pointer's `MouseWheelDelta`: on the vertical
    /// wheel positive is a scroll up, away from the person, and on the
    /// horizontal one — `horizontal` — positive is a scroll right.
    pub fn scroll(&mut self, delta: i32, horizontal: bool) -> Vec<u8> {
        let mut notches = Vec::new();
        if horizontal {
            Self::gather(&mut self.horizontal, delta, WHEEL_RIGHT, WHEEL_LEFT, &mut notches);
        } else {
            Self::gather(&mut self.vertical, delta, WHEEL_UP, WHEEL_DOWN, &mut notches);
        }
        notches
    }

    fn gather(carried: &mut i32, delta: i32, positive: u8, negative: u8, out: &mut Vec<u8>) {
        if delta == 0 {
            return;
        }
        // A reversal throws away what was carried: half a notch one way is not
        // half a notch back the other, and keeping it swallows the first flick
        // of every change of direction.
        if carried.signum() != delta.signum() {
            *carried = 0;
        }
        *carried = carried.saturating_add(delta);
        let button = if delta > 0 { positive } else { negative };
        while carried.abs() >= WHEEL_DELTA && out.len() < MAX_NOTCHES {
            *carried -= WHEEL_DELTA * carried.signum();
            out.push(button);
        }
        // Past the cap the rest is dropped rather than saved up, or a flick
        // would keep scrolling long after the finger stopped.
        if out.len() >= MAX_NOTCHES {
            *carried = 0;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_step_of_a_mouse_wheel_is_a_notch() {
        let mut wheel = Wheel::default();
        assert_eq!(wheel.scroll(120, false), vec![WHEEL_UP]);
        assert_eq!(wheel.scroll(-120, false), vec![WHEEL_DOWN]);
        assert_eq!(wheel.scroll(360, false), vec![WHEEL_UP; 3]);
        assert_eq!(wheel.scroll(240, true), vec![WHEEL_RIGHT; 2]);
        assert_eq!(wheel.scroll(-240, true), vec![WHEEL_LEFT; 2]);
    }

    #[test]
    fn a_fine_wheel_gathers_deltas_until_they_are_a_notch() {
        let mut wheel = Wheel::default();
        let mut sent = Vec::new();
        for _ in 0..4 {
            sent.extend(wheel.scroll(30, false));
        }
        assert_eq!(sent, vec![WHEEL_UP], "four quarters is one notch, not four and not none");

        // And the leftovers carry: three more quarters are not yet the next.
        let mut sent = Vec::new();
        for _ in 0..3 {
            sent.extend(wheel.scroll(30, false));
        }
        assert_eq!(sent, Vec::<u8>::new());
        assert_eq!(wheel.scroll(30, false), vec![WHEEL_UP]);
    }

    #[test]
    fn turning_back_does_not_have_to_undo_what_was_carried() {
        let mut wheel = Wheel::default();
        assert_eq!(wheel.scroll(110, false), Vec::<u8>::new());
        // Without the reversal rule this would need 230 of the other way.
        assert_eq!(wheel.scroll(-120, false), vec![WHEEL_DOWN]);
    }

    #[test]
    fn the_two_wheels_carry_apart() {
        let mut wheel = Wheel::default();
        assert_eq!(wheel.scroll(60, false), Vec::<u8>::new());
        assert_eq!(wheel.scroll(60, true), Vec::<u8>::new());
        assert_eq!(wheel.scroll(60, false), vec![WHEEL_UP]);
        assert_eq!(wheel.scroll(60, true), vec![WHEEL_RIGHT]);
    }

    #[test]
    fn a_flick_is_capped_and_leaves_nothing_saved_up() {
        let mut wheel = Wheel::default();
        let notches = wheel.scroll(120 * 1000, false);
        assert_eq!(notches.len(), MAX_NOTCHES);
        assert!(notches.iter().all(|b| *b == WHEEL_UP));
        // The overflow is gone, not waiting for the next event.
        assert_eq!(wheel.scroll(1, false), Vec::<u8>::new());
        assert_eq!(wheel.scroll(119, false), vec![WHEEL_UP], "and a fresh start counts from zero");
    }

    #[test]
    fn a_scroll_of_nothing_is_nothing() {
        let mut wheel = Wheel::default();
        assert_eq!(wheel.scroll(0, false), Vec::<u8>::new());
        assert_eq!(wheel.scroll(0, true), Vec::<u8>::new());
    }
}
