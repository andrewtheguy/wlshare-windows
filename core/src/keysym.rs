//! What a Windows key press is on the wire.
//!
//! RFB carries X11 keysyms, so every key the window sees has to become one. The
//! translation is here rather than in the app for the reason every other
//! decision is: it is a table, tables go wrong quietly, and a table in Rust has
//! tests.
//!
//! Two kinds of key, and the order matters. A key that *names* itself — an
//! arrow, a function key, Escape, a keypad digit, a modifier — has a keysym of
//! its own and is found by its virtual key code, whatever character Windows says
//! it types; that is what makes the keypad's `1` arrive as `KP_1` and not as a
//! plain `1`. Every other key is a character key, and its keysym is the
//! character the app already resolved — the case included, because the server
//! takes a character keysym as a character already cased and presses Shift
//! around the keycode to make it come out that way.
//!
//! Windows gives one virtual key code to keys X11 tells apart, so a code alone
//! is not always enough. Enter, Control and Alt on the right, and the
//! navigation block beside the letters, come with the *extended* bit set; the
//! same codes without it are the main Enter, the left modifiers, and the
//! keypad's second meaning with Num Lock off. Shift sets no such bit, and its
//! scan code is what tells the two apart.
//!
//! Caps Lock and Num Lock are not sent. A character keysym reaches the server
//! already cased, and a Caps Lock the desktop latched too would case it a
//! second time; and the keypad is sent as the key it was here — `KP_1` or
//! `KP_End` — so the desktop's own Num Lock has nothing left to decide.
//!
//! The character wanted is the one the key types with Control and Alt let go
//! and Shift and Caps Lock as they are: with them, Control-A is U+0001, which is
//! not the key that was pressed.

/// A virtual key code (`VK_*`) and the X11 keysym it always means, whatever it
/// types and wherever it is.
///
/// Sorted by key code so the table reads as Windows numbers the keys, not as
/// X11 orders its keysyms.
const NAMED: &[(u16, u32)] = &[
    (0x08, 0xff08), // VK_BACK -> BackSpace
    (0x09, 0xff09), // VK_TAB -> Tab
    (0x13, 0xff13), // VK_PAUSE -> Pause
    (0x1b, 0xff1b), // VK_ESCAPE -> Escape
    (0x2c, 0xff61), // VK_SNAPSHOT -> Print
    (0x5b, 0xffeb), // VK_LWIN -> Super_L
    (0x5c, 0xffec), // VK_RWIN -> Super_R
    (0x5d, 0xff67), // VK_APPS -> Menu
    (0x60, 0xffb0), // VK_NUMPAD0 -> KP_0
    (0x61, 0xffb1), // KP_1
    (0x62, 0xffb2), // KP_2
    (0x63, 0xffb3), // KP_3
    (0x64, 0xffb4), // KP_4
    (0x65, 0xffb5), // KP_5
    (0x66, 0xffb6), // KP_6
    (0x67, 0xffb7), // KP_7
    (0x68, 0xffb8), // KP_8
    (0x69, 0xffb9), // KP_9
    (0x6a, 0xffaa), // VK_MULTIPLY -> KP_Multiply
    (0x6b, 0xffab), // VK_ADD -> KP_Add
    (0x6c, 0xffac), // VK_SEPARATOR -> KP_Separator
    (0x6d, 0xffad), // VK_SUBTRACT -> KP_Subtract
    (0x6e, 0xffae), // VK_DECIMAL -> KP_Decimal
    (0x6f, 0xffaf), // VK_DIVIDE -> KP_Divide
    (0x70, 0xffbe), // VK_F1 -> F1
    (0x71, 0xffbf), // F2
    (0x72, 0xffc0), // F3
    (0x73, 0xffc1), // F4
    (0x74, 0xffc2), // F5
    (0x75, 0xffc3), // F6
    (0x76, 0xffc4), // F7
    (0x77, 0xffc5), // F8
    (0x78, 0xffc6), // F9
    (0x79, 0xffc7), // F10
    (0x7a, 0xffc8), // F11
    (0x7b, 0xffc9), // F12
    (0x7c, 0xffca), // F13
    (0x7d, 0xffcb), // F14
    (0x7e, 0xffcc), // F15
    (0x7f, 0xffcd), // F16
    (0x80, 0xffce), // F17
    (0x81, 0xffcf), // F18
    (0x82, 0xffd0), // F19
    (0x83, 0xffd1), // F20
    (0x84, 0xffd2), // F21
    (0x85, 0xffd3), // F22
    (0x86, 0xffd4), // F23
    (0x87, 0xffd5), // F24
    (0x91, 0xff14), // VK_SCROLL -> Scroll_Lock
    (0xa0, 0xffe1), // VK_LSHIFT -> Shift_L
    (0xa1, 0xffe2), // VK_RSHIFT -> Shift_R
    (0xa2, 0xffe3), // VK_LCONTROL -> Control_L
    (0xa3, 0xffe4), // VK_RCONTROL -> Control_R
    (0xa4, 0xffe9), // VK_LMENU -> Alt_L
    (0xa5, 0xffea), // VK_RMENU -> Alt_R
];

/// A virtual key code whose keysym depends on the extended bit: the keysym
/// without it, then the keysym with it.
const SPLIT: &[(u16, u32, u32)] = &[
    (0x0c, 0xff9d, 0xff0b), // VK_CLEAR: the keypad's 5 with Num Lock off -> KP_Begin; Clear
    (0x0d, 0xff0d, 0xff8d), // VK_RETURN: Return; the keypad's -> KP_Enter
    (0x11, 0xffe3, 0xffe4), // VK_CONTROL: Control_L; Control_R
    (0x12, 0xffe9, 0xffea), // VK_MENU: Alt_L; Alt_R
    (0x21, 0xff9a, 0xff55), // VK_PRIOR: KP_Prior; Page_Up
    (0x22, 0xff9b, 0xff56), // VK_NEXT: KP_Next; Page_Down
    (0x23, 0xff9c, 0xff57), // VK_END: KP_End; End
    (0x24, 0xff95, 0xff50), // VK_HOME: KP_Home; Home
    (0x25, 0xff96, 0xff51), // VK_LEFT: KP_Left; Left
    (0x26, 0xff97, 0xff52), // VK_UP: KP_Up; Up
    (0x27, 0xff98, 0xff53), // VK_RIGHT: KP_Right; Right
    (0x28, 0xff99, 0xff54), // VK_DOWN: KP_Down; Down
    (0x2d, 0xff9e, 0xff63), // VK_INSERT: KP_Insert; Insert
    (0x2e, 0xff9f, 0xffff), // VK_DELETE: KP_Delete; Delete
];

/// `VK_SHIFT`, which is both Shifts, and the scan code of the right one.
const VK_SHIFT: u16 = 0x10;
const SCAN_RIGHT_SHIFT: u16 = 0x36;

/// The keysym a key always means, if it is one that names itself: its virtual
/// key code, its scan code, and whether Windows marked it extended.
pub fn named(vk: u16, scan: u16, extended: bool) -> Option<u32> {
    if vk == VK_SHIFT {
        return Some(if scan == SCAN_RIGHT_SHIFT { 0xffe2 } else { 0xffe1 });
    }
    if let Some((_, plain, with_extended)) = SPLIT.iter().find(|(code, _, _)| *code == vk) {
        return Some(if extended { *with_extended } else { *plain });
    }
    NAMED.iter().find(|(code, _)| *code == vk).map(|(_, keysym)| *keysym)
}

/// The keysym for a character, RFC 6143 §7.5.4's rule: Latin-1 is itself, and
/// everything else is Unicode with the high bit of the keysym space set.
/// A control character is not a key and has none — the app is meant to send
/// what the key types with Control let go, which is a printable one.
pub fn of_char(c: char) -> Option<u32> {
    match u32::from(c) {
        0x20..=0x7e | 0xa0..=0xff => Some(u32::from(c)),
        0x00..=0x1f | 0x7f..=0x9f => None,
        other => Some(0x0100_0000 + other),
    }
}

/// The keysym for a key press: what the key names, or failing that what it
/// typed. `None` is a key with neither, which is not sent.
pub fn keysym(vk: u16, scan: u16, extended: bool, character: Option<char>) -> Option<u32> {
    named(vk, scan, extended).or_else(|| character.and_then(of_char))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_key_that_names_itself_beats_whatever_it_typed() {
        // The keypad's 1 types '1' and is still KP_1.
        assert_eq!(keysym(0x61, 0x4f, false, Some('1')), Some(0xffb1));
        // Return types a carriage return, which of_char refuses on its own.
        assert_eq!(keysym(0x0d, 0x1c, false, Some('\r')), Some(0xff0d));
        // Backspace types U+0008 and Escape U+001B.
        assert_eq!(keysym(0x08, 0x0e, false, Some('\u{8}')), Some(0xff08));
        assert_eq!(keysym(0x1b, 0x01, false, Some('\u{1b}')), Some(0xff1b));
        assert_eq!(keysym(0x26, 0x48, true, None), Some(0xff52));
    }

    #[test]
    fn a_character_key_carries_the_case_the_app_resolved() {
        // The same key, shifted and not: the server presses Shift to match.
        assert_eq!(keysym(0x41, 0x1e, false, Some('a')), Some(0x61));
        assert_eq!(keysym(0x41, 0x1e, false, Some('A')), Some(0x41));
        assert_eq!(keysym(0x30, 0x0b, false, Some('0')), Some(0x30));
        assert_eq!(keysym(0xbb, 0x0d, false, Some('+')), Some(0x2b));
        assert_eq!(keysym(0x20, 0x39, false, Some(' ')), Some(0x20));
    }

    #[test]
    fn the_extended_bit_tells_the_two_keys_on_one_code_apart() {
        assert_eq!(named(0x0d, 0x1c, false), Some(0xff0d), "Return");
        assert_eq!(named(0x0d, 0x1c, true), Some(0xff8d), "KP_Enter");
        assert_eq!(named(0x11, 0x1d, false), Some(0xffe3), "Control_L");
        assert_eq!(named(0x11, 0x1d, true), Some(0xffe4), "Control_R");
        assert_eq!(named(0x12, 0x38, false), Some(0xffe9), "Alt_L");
        assert_eq!(named(0x12, 0x38, true), Some(0xffea), "Alt_R");
        // The arrows beside the letters, and the keypad's with Num Lock off.
        assert_eq!(named(0x25, 0x4b, true), Some(0xff51), "Left");
        assert_eq!(named(0x25, 0x4b, false), Some(0xff96), "KP_Left");
        assert_eq!(named(0x2e, 0x53, true), Some(0xffff), "Delete");
        assert_eq!(named(0x2e, 0x53, false), Some(0xff9f), "KP_Delete");
        assert_eq!(named(0x0c, 0x4c, false), Some(0xff9d), "KP_Begin");
    }

    #[test]
    fn shift_is_told_apart_by_its_scan_code() {
        assert_eq!(named(0x10, 0x2a, false), Some(0xffe1), "Shift_L");
        assert_eq!(named(0x10, 0x36, false), Some(0xffe2), "Shift_R");
        // The left/right codes Windows gives when asked for them.
        assert_eq!(named(0xa0, 0x2a, false), Some(0xffe1));
        assert_eq!(named(0xa1, 0x36, false), Some(0xffe2));
    }

    #[test]
    fn the_locks_the_desktop_must_not_latch_are_not_sent() {
        assert_eq!(keysym(0x14, 0x3a, false, None), None, "Caps Lock");
        assert_eq!(keysym(0x90, 0x45, true, None), None, "Num Lock");
    }

    #[test]
    fn latin_1_is_itself_and_the_rest_is_unicode() {
        assert_eq!(of_char(' '), Some(0x20));
        assert_eq!(of_char('~'), Some(0x7e));
        assert_eq!(of_char('é'), Some(0xe9));
        assert_eq!(of_char('ÿ'), Some(0xff));
        // Just past Latin-1, and well past it.
        assert_eq!(of_char('Ā'), Some(0x0100_0100));
        assert_eq!(of_char('中'), Some(0x0100_4e2d));
        assert_eq!(of_char('😀'), Some(0x0101_f600));
    }

    #[test]
    fn a_control_character_is_not_a_key() {
        assert_eq!(of_char('\u{1}'), None);
        assert_eq!(of_char('\u{7f}'), None);
        assert_eq!(of_char('\u{9f}'), None);
        assert_eq!(keysym(0x41, 0x1e, false, Some('\u{1}')), None);
        assert_eq!(keysym(0xff, 0, false, None), None);
    }

    #[test]
    fn the_tables_are_one_key_code_per_row_and_no_keysym_twice() {
        let mut codes: Vec<u16> = NAMED.iter().map(|(code, _)| *code).collect();
        let ordered = codes.clone();
        codes.sort_unstable();
        codes.dedup();
        assert_eq!(codes.len(), NAMED.len(), "a key code appears twice in NAMED");
        assert_eq!(codes, ordered, "NAMED is not in key-code order");

        let mut split: Vec<u16> = SPLIT.iter().map(|(code, _, _)| *code).collect();
        let ordered = split.clone();
        split.sort_unstable();
        split.dedup();
        assert_eq!(split.len(), SPLIT.len(), "a key code appears twice in SPLIT");
        assert_eq!(split, ordered, "SPLIT is not in key-code order");
        assert!(split.iter().all(|code| !codes.contains(code) && *code != VK_SHIFT), "a key code is in both tables");

        // The left and right modifiers are in both, by design: Windows gives
        // either spelling depending on who asks.
        let modifiers = [0xffe1, 0xffe2, 0xffe3, 0xffe4, 0xffe9, 0xffea];
        let mut keysyms: Vec<u32> = NAMED.iter().map(|(_, keysym)| *keysym).collect();
        keysyms.extend(SPLIT.iter().flat_map(|(_, plain, extended)| [*plain, *extended]).filter(|k| !modifiers.contains(k)));
        keysyms.sort_unstable();
        let all = keysyms.len();
        keysyms.dedup();
        assert_eq!(keysyms.len(), all, "two keys send the same keysym");
    }

    #[test]
    fn the_function_keys_are_the_run_x11_says_they_are() {
        for n in 0..24u16 {
            assert_eq!(named(0x70 + n, 0, false), Some(0xffbe + u32::from(n)), "F{}", n + 1);
        }
    }
}
