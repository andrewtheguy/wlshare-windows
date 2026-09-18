//! `src/WlshareViewer/Interop/Native.cs` is hand-written, and the app calls the
//! core through it rather than through anything generated from the Rust. A type
//! that differs between the two — a `ushort` where the Rust says `u32`, a field
//! in the wrong place, a callback with one parameter too few — loads cleanly and
//! corrupts memory at run time, and no amount of testing either side alone
//! would find it.
//!
//! So: read both, and compare. `Native.cs` is parsed as C# and `src/ffi.rs` as
//! Rust, by two parsers that know nothing about each other beyond the table
//! mapping one language's spelling of a type to the other's. Names are compared
//! with case and underscores taken out, which is all that separates Rust's
//! `damage_x` from C#'s `DamageX`.

use std::collections::BTreeMap;

/// How a Rust type in the FFI is spelled in C#. Anything not in here is a type
/// the ABI has not been thought about for, and the test says so rather than
/// guessing. The callbacks are not here: their spelling is worked out from the
/// Rust `type` that declares them ([`rust_callbacks`]).
const TYPES: &[(&str, &str)] = &[
    ("()", "void"),
    ("bool", "bool"),
    ("f64", "double"),
    ("i32", "int"),
    ("u8", "byte"),
    ("u16", "ushort"),
    ("u32", "uint"),
    ("u64", "ulong"),
    ("usize", "nuint"),
    // In only: the core copies a C string it is handed and keeps none.
    ("*const c_char", "string"),
    ("*mut c_char", "byte*"),
    ("*const u8", "byte*"),
    ("*mut u8", "byte*"),
    ("*mut c_void", "nint"),
    // Opaque to the app, which only ever hands it back.
    ("*const Client", "nint"),
    ("*mut Client", "nint"),
    ("*mut WlshareStatus", "WlshareStatus*"),
    ("*const WlshareFrame", "WlshareFrame*"),
    ("*const WlshareCursor", "WlshareCursor*"),
];

type Fields = Vec<(String, String)>;

#[derive(Debug, PartialEq, Eq)]
struct Function {
    returns: String,
    params: Fields,
}

fn squash(ty: &str) -> String {
    ty.chars().filter(|c| !c.is_whitespace()).collect()
}

/// A name as both languages agree on it: `damage_x` and `DamageX` are one.
fn name(name: &str) -> String {
    name.chars().filter(|c| *c != '_').flat_map(char::to_lowercase).collect()
}

/// Split on the commas that separate, not the ones inside `<...>`, `[...]` or
/// `(...)` — a C# function pointer's type list has commas of its own.
fn split_top(list: &str) -> Vec<String> {
    let mut parts = Vec::new();
    let mut depth = 0i32;
    let mut current = String::new();
    for c in list.chars() {
        match c {
            '<' | '[' | '(' => depth += 1,
            '>' | ']' | ')' => depth -= 1,
            ',' if depth == 0 => {
                parts.push(std::mem::take(&mut current));
                continue;
            }
            _ => {}
        }
        current.push(c);
    }
    parts.push(current);
    parts.into_iter().map(|part| part.trim().to_owned()).filter(|part| !part.is_empty()).collect()
}

fn without_line_comments(source: &str) -> String {
    // Comments carry commas, braces and the word `fn`; none of them is code.
    source
        .lines()
        .map(|line| match line.find("//") {
            Some(at) => &line[..at],
            None => line,
        })
        .collect::<Vec<_>>()
        .join("\n")
}

// ── The Rust half ────────────────────────────────────────────────────────────

struct Rust {
    callbacks: BTreeMap<String, String>,
}

impl Rust {
    fn cs_type(&self, rust: &str) -> String {
        let rust = rust.trim();
        // A nullable callback is the same function pointer, null or not.
        let bare = rust.strip_prefix("Option<").and_then(|inner| inner.strip_suffix('>')).unwrap_or(rust);
        if let Some(spelling) = self.callbacks.get(bare) {
            return spelling.clone();
        }
        let cs = TYPES
            .iter()
            .find(|(from, _)| *from == rust)
            .unwrap_or_else(|| panic!("the FFI uses the Rust type `{rust}`, which this test has no C# spelling for — add it to TYPES"))
            .1;
        squash(cs)
    }

    fn params(&self, list: &str) -> Fields {
        split_top(list)
            .iter()
            .map(|param| {
                let (param_name, ty) = param.split_once(':').expect("a Rust parameter is `name: type`");
                (name(param_name), self.cs_type(ty))
            })
            .collect()
    }
}

/// The `pub type X = extern "C" fn(...);` callbacks, as the C# function
/// pointer each one is: its parameter types, then its return type.
fn rust_callbacks(source: &str) -> BTreeMap<String, String> {
    let bare = Rust { callbacks: BTreeMap::new() };
    let mut callbacks = BTreeMap::new();
    let mut rest = source;
    while let Some(at) = rest.find("pub type ") {
        rest = &rest[at + "pub type ".len()..];
        let (alias, definition) = rest.split_once('=').expect("a type alias has a definition");
        let end = definition.find(';').expect("a type alias ends");
        let definition = definition[..end].trim();
        let signature = definition.strip_prefix("extern \"C\" fn").expect("every alias in the FFI is a C callback");
        let open = signature.find('(').expect("a callback has parameters");
        let close = signature.rfind(')').expect("a callback's parameters end");
        let mut types: Vec<String> = bare.params(&signature[open + 1..close]).into_iter().map(|(_, ty)| ty).collect();
        types.push(match signature[close + 1..].trim().strip_prefix("->") {
            Some(ty) => bare.cs_type(ty),
            None => bare.cs_type("()"),
        });
        callbacks.insert(alias.trim().to_owned(), format!("delegate*unmanaged[Cdecl]<{}>", types.join(",")));
    }
    callbacks
}

fn rust_functions(rust: &Rust, source: &str) -> BTreeMap<String, Function> {
    let mut functions = BTreeMap::new();
    let mut rest = source;
    // The trailing space is what tells an exported function from a callback
    // type, which is `extern "C" fn(` with none.
    while let Some(at) = rest.find("extern \"C\" fn ") {
        rest = &rest[at + "extern \"C\" fn ".len()..];
        let open = rest.find('(').expect("a function has a parameter list");
        let function = rest[..open].trim().to_owned();
        let close = open + rest[open..].find(')').expect("a parameter list ends");
        let params = rust.params(&rest[open + 1..close]);
        rest = &rest[close + 1..];
        let body = rest.find('{').expect("a function has a body");
        let returns = match rest[..body].trim().strip_prefix("->") {
            Some(ty) => rust.cs_type(ty),
            None => rust.cs_type("()"),
        };
        functions.insert(function, Function { returns, params });
    }
    functions
}

fn rust_structs(rust: &Rust, source: &str) -> BTreeMap<String, Fields> {
    let mut structs = BTreeMap::new();
    let mut rest = source;
    while let Some(at) = rest.find("pub struct Wlshare") {
        rest = &rest[at + "pub struct ".len()..];
        let open = rest.find('{').expect("a struct has a body");
        let struct_name = rest[..open].trim().to_owned();
        let close = rest.find('}').expect("a struct body ends");
        let members = split_top(&rest[open + 1..close])
            .iter()
            .map(|member| {
                let member = member.trim().strip_prefix("pub ").expect("an FFI struct's fields are public");
                let (field, ty) = member.split_once(':').expect("a Rust field is `name: type`");
                (name(field), rust.cs_type(ty))
            })
            .collect();
        structs.insert(struct_name, members);
        rest = &rest[close + 1..];
    }
    structs
}

// ── The C# half ──────────────────────────────────────────────────────────────

/// A C# parameter — `[MarshalAs(UnmanagedType.U1)] bool down`,
/// `delegate* unmanaged[Cdecl]<nint, void> wake` — as its name and its type.
/// The marshalling attribute is taken off: it says how a type crosses, and
/// the one it says for a bool is the one byte a Rust bool is.
fn cs_param(param: &str) -> (String, String) {
    let mut param = param.trim();
    while param.starts_with('[') {
        let end = param.find(']').expect("an attribute ends");
        param = param[end + 1..].trim();
    }
    let at = param.rfind(|c: char| !(c.is_alphanumeric() || c == '_')).expect("a C# parameter is `type name`") + 1;
    (name(&param[at..]), squash(&param[..at]))
}

fn cs_functions(source: &str) -> BTreeMap<String, Function> {
    let mut functions = BTreeMap::new();
    let mut rest = source;
    while let Some(at) = rest.find("EntryPoint = \"") {
        rest = &rest[at + "EntryPoint = \"".len()..];
        let end = rest.find('"').expect("an entry point is quoted");
        let entry = rest[..end].to_owned();
        let declared = rest.find("static partial ").expect("an import is a static partial method") + "static partial ".len();
        rest = &rest[declared..];
        let open = rest.find('(').expect("a method has a parameter list");
        let signature = &rest[..open];
        let returns = squash(&signature[..signature.trim_end().rfind(' ').expect("a method has a return type")]);
        let close = rest.find(");").expect("a method declaration ends");
        let params = split_top(&rest[open + 1..close]).iter().map(|param| cs_param(param)).collect();
        functions.insert(entry, Function { returns, params });
        rest = &rest[close..];
    }
    functions
}

fn cs_structs(source: &str) -> BTreeMap<String, Fields> {
    let mut structs = BTreeMap::new();
    let mut rest = source;
    while let Some(at) = rest.find(" struct Wlshare") {
        rest = &rest[at + " struct ".len()..];
        let open = rest.find('{').expect("a struct has a body");
        let struct_name = rest[..open].trim().to_owned();
        let close = rest.find('}').expect("a struct body ends");
        let members = rest[open + 1..close]
            .split(';')
            .map(str::trim)
            .filter(|member| !member.is_empty())
            .map(|member| cs_param(member.strip_prefix("public ").expect("an interop struct's fields are public")))
            .collect();
        structs.insert(struct_name, members);
        rest = &rest[close + 1..];
    }
    structs
}

// ── The comparison ───────────────────────────────────────────────────────────

#[test]
fn native_cs_declares_what_the_rust_exports() {
    let rust_source = without_line_comments(include_str!("../src/ffi.rs"));
    let rust = Rust { callbacks: rust_callbacks(&rust_source) };
    let rust_functions = rust_functions(&rust, &rust_source);
    let rust_structs = rust_structs(&rust, &rust_source);

    let cs_source = without_line_comments(include_str!("../../src/WlshareViewer/Interop/Native.cs"));
    let cs_functions = cs_functions(&cs_source);
    let cs_structs = cs_structs(&cs_source);

    assert!(rust.callbacks.len() >= 3, "the Rust parser found {} callbacks, so it is broken rather than passing", rust.callbacks.len());
    assert!(!rust_functions.is_empty(), "the Rust parser found no functions, so it is broken rather than passing");
    assert!(!rust_structs.is_empty(), "the Rust parser found no structs, so it is broken rather than passing");

    fn names<T>(map: &BTreeMap<String, T>) -> Vec<String> {
        map.keys().cloned().collect()
    }
    assert_eq!(names(&rust_functions), names(&cs_functions), "the two halves declare different functions");
    assert_eq!(names(&rust_structs), names(&cs_structs), "the two halves declare different structs");

    for (function, declared) in &rust_functions {
        assert_eq!(declared, &cs_functions[function], "{function} differs between Native.cs and the Rust");
    }
    for (struct_name, members) in &rust_structs {
        assert_eq!(members, &cs_structs[struct_name], "{struct_name} differs between Native.cs and the Rust");
    }
}

#[test]
fn a_callback_is_spelled_from_its_rust_type() {
    let rust = Rust { callbacks: rust_callbacks("pub type F = extern \"C\" fn(ctx: *mut c_void, frame: *const WlshareFrame);") };
    assert_eq!(rust.cs_type("F"), "delegate*unmanaged[Cdecl]<nint,WlshareFrame*,void>");
    assert_eq!(rust.cs_type("Option<F>"), "delegate*unmanaged[Cdecl]<nint,WlshareFrame*,void>");
}
