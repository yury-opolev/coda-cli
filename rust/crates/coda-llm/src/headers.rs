//! Header precedence shared by provider clients.

pub(crate) fn apply(
    mut builder: reqwest::RequestBuilder,
    defaults: Vec<(String, String)>,
    extra: &[(String, String)],
    dynamic: Option<&[(String, String)]>,
) -> reqwest::RequestBuilder {
    let mut merged: Vec<(String, String)> = Vec::new();
    for (name, value) in defaults.into_iter()
        .chain(extra.iter().cloned())
        .chain(dynamic.into_iter().flatten().cloned())
    {
        if let Some(index) = merged.iter().position(|(existing, _)| existing.eq_ignore_ascii_case(&name)) {
            if name.eq_ignore_ascii_case("anthropic-beta") {
                // Auth-mode flags augment the features required by the request.
                let mut flags = Vec::new();
                for flag in merged[index].1.split(',').chain(value.split(',')).map(str::trim) {
                    if !flag.is_empty() && !flags.contains(&flag) {
                        flags.push(flag);
                    }
                }
                merged[index].1 = flags.join(",");
            } else {
                merged[index] = (name, value);
            }
        } else {
            merged.push((name, value));
        }
    }
    for (name, value) in merged {
        builder = builder.header(name, value);
    }
    builder
}
