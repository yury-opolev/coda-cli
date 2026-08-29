//! Search the registry's tool descriptions and return matching tool schemas.
//!
//! Query forms (matching the C# `ToolSearchEngine` interface):
//! - `select:Read,Edit,Grep` — fetch those exact tools by name (case-insensitive).
//! - `+required keyword…`   — require "required" in the tool name, rank by keywords.
//! - `keyword1 keyword2`    — full keyword search against name + description + hint.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{ToolContext, ToolDescriptor, ToolOutcome, ToolResult, Tool};

pub struct ToolSearchTool;

#[async_trait]
impl Tool for ToolSearchTool {
    fn name(&self) -> &str {
        "tool_search"
    }

    fn description(&self) -> &str {
        "Fetch full schema definitions for tools so they can be called. \
         Deferred tools appear by name until fetched; this tool returns their complete \
         JSON Schema inside a <functions> block. \
         Query forms: \"select:Name1,Name2\" for exact lookup, \
         \"+required keyword\" to require a name prefix, \
         or plain keywords for full-text search."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Query to find tools. Use \"select:<name>\" for exact lookup, or keywords."
            },
            "max_results": {
              "type": "integer",
              "description": "Maximum number of results (default 5)."
            }
          },
          "required": ["query"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(
        &self,
        input: &serde_json::Value,
        ctx: &ToolContext,
        _cancel: CancellationToken,
    ) -> ToolOutcome {
        let query = match input.get("query").and_then(|v| v.as_str()) {
            Some(q) if !q.trim().is_empty() => q,
            _ => return ToolResult::error("tool_search requires a 'query'."),
        };

        let max_results = input
            .get("max_results")
            .and_then(|v| v.as_i64())
            .map(|n| n.max(1) as usize)
            .unwrap_or(5);

        let all = ctx.all_tools.as_deref().unwrap_or(&[]);
        let matches = search_tools(query, all, max_results);

        if matches.is_empty() {
            return ToolResult::ok("No matching tools found.");
        }

        let mut out = String::from("<functions>\n");
        for desc in &matches {
            let name_json =
                serde_json::to_string(&desc.name).unwrap_or_else(|_| format!("{:?}", desc.name));
            let desc_json = serde_json::to_string(&desc.description)
                .unwrap_or_else(|_| format!("{:?}", desc.description));
            out.push_str(&format!(
                "<function>{{\"description\": {desc_json}, \"name\": {name_json}, \
                 \"parameters\": {}}}</function>\n",
                desc.input_schema_json
            ));
        }
        out.push_str("</functions>");
        ToolResult::ok(out)
    }
}

/// Search `tools` using `query` and return at most `max_results` descriptors.
pub fn search_tools(
    query: &str,
    tools: &[ToolDescriptor],
    max_results: usize,
) -> Vec<ToolDescriptor> {
    // ── select: exact name lookup ─────────────────────────────────────────────
    if let Some(names_part) = query.strip_prefix("select:") {
        let names: Vec<&str> = names_part.split(',').map(str::trim).collect();
        let mut result = Vec::new();
        for name in &names {
            if let Some(t) = tools.iter().find(|t| t.name.eq_ignore_ascii_case(name)) {
                result.push(t.clone());
            }
            if result.len() >= max_results {
                break;
            }
        }
        return result;
    }

    // ── +prefix keyword… ─────────────────────────────────────────────────────
    let (name_filter, keyword_str) = if query.starts_with('+') {
        let rest = &query[1..];
        match rest.find(char::is_whitespace) {
            Some(pos) => (&rest[..pos], rest[pos..].trim()),
            None => (rest, ""),
        }
    } else {
        ("", query)
    };

    let keywords: Vec<&str> = keyword_str.split_whitespace().collect();

    // Build (score, tool) pairs and sort by score descending.
    let mut scored: Vec<(usize, &ToolDescriptor)> = tools
        .iter()
        .filter(|t| {
            name_filter.is_empty()
                || t.name.to_lowercase().contains(&name_filter.to_lowercase())
        })
        .filter_map(|t| {
            let haystack = format!(
                "{} {} {}",
                t.name,
                t.description,
                t.search_hint.as_deref().unwrap_or("")
            )
            .to_lowercase();

            let score = keywords
                .iter()
                .filter(|&&kw| haystack.contains(&kw.to_lowercase()))
                .count();

            if score > 0 || keywords.is_empty() {
                Some((score, t))
            } else {
                None
            }
        })
        .collect();

    scored.sort_by(|a, b| b.0.cmp(&a.0));
    scored.into_iter().take(max_results).map(|(_, t)| t.clone()).collect()
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn ctx() -> ToolContext {
        ToolContext::new("/")
    }

    fn ctx_with_tools(tools: Vec<ToolDescriptor>) -> ToolContext {
        ToolContext::new("/").with_all_tools(tools)
    }

    fn make_desc(name: &str, description: &str) -> ToolDescriptor {
        ToolDescriptor {
            name: name.to_owned(),
            description: description.to_owned(),
            input_schema_json: r#"{"type":"object"}"#.to_owned(),
            is_deferred: false,
            search_hint: None,
        }
    }

    fn sample_tools() -> Vec<ToolDescriptor> {
        vec![
            make_desc("read_file", "Read a UTF-8 text file"),
            make_desc("write_file", "Create or overwrite a text file"),
            make_desc("grep", "Search file contents by regex"),
            make_desc("web_fetch", "Fetch a URL and return its content"),
        ]
    }

    // ── validation ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn missing_query_returns_error() {
        let result = ToolSearchTool
            .execute(&serde_json::json!({}), &ctx(), CancellationToken::new())
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn empty_tool_list_returns_no_matches() {
        let result = ToolSearchTool
            .execute(
                &serde_json::json!({"query": "read"}),
                &ctx(), // no all_tools
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(
            result.content.contains("No matching"),
            "{}",
            result.content
        );
    }

    // ── keyword search ────────────────────────────────────────────────────────

    #[tokio::test]
    async fn keyword_search_finds_matching_tools() {
        let ctx = ctx_with_tools(sample_tools());
        let result = ToolSearchTool
            .execute(
                &serde_json::json!({"query": "file"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("read_file"), "{}", result.content);
        assert!(result.content.contains("write_file"), "{}", result.content);
    }

    #[tokio::test]
    async fn keyword_search_respects_max_results() {
        let ctx = ctx_with_tools(sample_tools());
        let result = ToolSearchTool
            .execute(
                &serde_json::json!({"query": "file", "max_results": 1}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        // Only one <function> block should appear.
        let count = result.content.matches("<function>").count();
        assert_eq!(count, 1, "expected 1 result, got {count}: {}", result.content);
    }

    // ── select: prefix ────────────────────────────────────────────────────────

    #[tokio::test]
    async fn select_prefix_fetches_exact_tools_by_name() {
        let ctx = ctx_with_tools(sample_tools());
        let result = ToolSearchTool
            .execute(
                &serde_json::json!({"query": "select:read_file,grep"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("read_file"), "{}", result.content);
        assert!(result.content.contains("grep"), "{}", result.content);
        assert!(!result.content.contains("web_fetch"), "{}", result.content);
    }

    #[tokio::test]
    async fn select_unknown_name_returns_empty() {
        let ctx = ctx_with_tools(sample_tools());
        let result = ToolSearchTool
            .execute(
                &serde_json::json!({"query": "select:nonexistent_tool"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("No matching"), "{}", result.content);
    }

    // ── +name filter ──────────────────────────────────────────────────────────

    #[tokio::test]
    async fn plus_prefix_filters_by_name() {
        let ctx = ctx_with_tools(sample_tools());
        let result = ToolSearchTool
            .execute(
                &serde_json::json!({"query": "+web fetch"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("web_fetch"), "{}", result.content);
        assert!(!result.content.contains("read_file"), "{}", result.content);
    }

    // ── search_tools unit tests ───────────────────────────────────────────────

    #[test]
    fn search_tools_select_case_insensitive() {
        let tools = sample_tools();
        let res = search_tools("select:READ_FILE", &tools, 5);
        assert_eq!(res.len(), 1);
        assert_eq!(res[0].name, "read_file");
    }

    #[test]
    fn search_tools_keyword_returns_all_matches_sorted_by_score() {
        let tools = vec![
            make_desc("alpha", "file operations and file search"),
            make_desc("beta", "file write"),
        ];
        let res = search_tools("file search", &tools, 5);
        // alpha has 2 keyword hits; beta has 1
        assert_eq!(res[0].name, "alpha");
    }
}
