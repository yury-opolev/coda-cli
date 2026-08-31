//! Colour roles and built-in themes.
//!
//! Every colour is stored twice: a 24-bit value and a 16-colour fallback. The
//! terminal's capability is only known at draw time, so resolution is deferred
//! rather than baked into the theme.
//!
//! RGB values mirror the C# `CodaThemes.cs` warm-ember palette so the Rust
//! front-end is visually indistinguishable from the Terminal.Gui one.

use ratatui::style::{Color, Modifier, Style};

/// A semantic colour slot. Rendering code names a role, never a literal colour.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Role {
    Background,

    // Transcript
    Assistant,
    User,
    UserBackground,
    UserTime,
    PendingUser,
    Heading,
    Code,
    Tool,
    Notification,
    Question,
    Warning,
    Error,

    // Tool outcomes
    ToolSuccess,
    ToolPartialFailure,
    Permission,
    PermissionApproved,

    // Diff
    Diff,
    DiffHeader,
    DiffAdded,
    DiffRemoved,
    DiffContext,
    DiffAddedBackground,
    DiffRemovedBackground,

    // Syntax
    SyntaxKeyword,
    SyntaxType,
    SyntaxString,
    SyntaxNumber,
    SyntaxComment,

    // Links
    Link,
    LinkDeceptive,

    // Callouts
    CalloutNote,
    CalloutTip,
    CalloutImportant,
    CalloutWarning,
    CalloutCaution,

    // Context usage breakdown
    ContextSystemPrompt,
    ContextSystemTools,
    ContextMcpTools,
    ContextMessages,
    ContextAutocompactBuffer,
    ContextFreeSpace,

    // Composer
    ComposerText,
    ComposerPrompt,
    ComposerPanelBackground,
    ComposerPanelEdge,

    // Operational status
    OperationalReady,
    OperationalInitializing,
    OperationalWorking,
    OperationalThinking,
    OperationalWaiting,

    // Chrome
    SelectionText,
    SelectionBackground,
    /// Text on the focused control's band.
    FocusText,
    /// Band drawn across every row of the focused control. The primary focus
    /// signal; the accent label and gutter marker are layered beneath it so
    /// none is load-bearing alone.
    FocusBackground,
    ScrollbarTrack,
    ScrollbarThumb,
    CompletionNormal,
    CompletionSelectedText,
    CompletionSelectedBackground,
    PromptText,
    PromptAccent,
}

/// A colour with a degraded fallback for 16-colour terminals.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ThemeColor {
    pub truecolor: Color,
    pub fallback: Color,
}

impl ThemeColor {
    pub const fn new(r: u8, g: u8, b: u8, fallback: Color) -> Self {
        Self {
            truecolor: Color::Rgb(r, g, b),
            fallback,
        }
    }

    /// Picks the representation the terminal can actually display.
    pub fn resolve(self, truecolor_supported: bool) -> Color {
        if truecolor_supported {
            self.truecolor
        } else {
            self.fallback
        }
    }
}

/// How much colour the terminal can render.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ColorDepth {
    /// Named ANSI colours only.
    Ansi16,
    /// Full 24-bit colour.
    #[default]
    TrueColor,
}

impl ColorDepth {
    /// Detects depth from the environment, honouring the usual overrides.
    pub fn detect() -> Self {
        let colorterm = std::env::var("COLORTERM").unwrap_or_default();
        if colorterm.contains("truecolor") || colorterm.contains("24bit") {
            return ColorDepth::TrueColor;
        }
        // Windows Terminal and modern conhost both support 24-bit colour.
        if std::env::var("WT_SESSION").is_ok() {
            return ColorDepth::TrueColor;
        }
        if std::env::var("NO_COLOR").is_ok() {
            return ColorDepth::Ansi16;
        }
        ColorDepth::TrueColor
    }

    fn truecolor(self) -> bool {
        matches!(self, ColorDepth::TrueColor)
    }
}

/// A named palette.
#[derive(Debug, Clone)]
pub struct Theme {
    pub name: &'static str,
    depth: ColorDepth,
    colors: ThemeColors,
}

/// The raw colour table, separated so themes can be defined as plain data.
#[derive(Debug, Clone, Copy)]
struct ThemeColors {
    background: ThemeColor,
    assistant: ThemeColor,
    user: ThemeColor,
    user_background: ThemeColor,
    user_time: ThemeColor,
    heading: ThemeColor,
    code: ThemeColor,
    tool: ThemeColor,
    diff: ThemeColor,
    success: ThemeColor,
    warn: ThemeColor,
    error: ThemeColor,
    dim: ThemeColor,
    accent: ThemeColor,
    diff_added_background: ThemeColor,
    diff_removed_background: ThemeColor,
    link: ThemeColor,
    link_deceptive: ThemeColor,
    context_system_prompt: ThemeColor,
    context_system_tools: ThemeColor,
    context_mcp_tools: ThemeColor,
    context_messages: ThemeColor,
    context_autocompact: ThemeColor,
    context_free_space: ThemeColor,
    callout_note: ThemeColor,
    callout_tip: ThemeColor,
    callout_important: ThemeColor,
    callout_warning: ThemeColor,
    callout_caution: ThemeColor,
    pending_user: ThemeColor,
    composer_text: ThemeColor,
    composer_prompt: ThemeColor,
    composer_panel_background: ThemeColor,
    operational_ready: ThemeColor,
    operational_initializing: ThemeColor,
    operational_working: ThemeColor,
    operational_thinking: ThemeColor,
    operational_waiting: ThemeColor,
    selection_text: ThemeColor,
    selection_background: ThemeColor,
    focus_text: ThemeColor,
    focus_background: ThemeColor,
    scrollbar_track: ThemeColor,
    scrollbar_thumb: ThemeColor,
    completion_normal: ThemeColor,
    completion_selected_text: ThemeColor,
    completion_selected_background: ThemeColor,
    prompt_text: ThemeColor,
    prompt_accent: ThemeColor,
}

/// The warm-ember palette, transcribed from the C# theme definition.
const WARM_EMBER: ThemeColors = ThemeColors {
    background: ThemeColor::new(23, 19, 16, Color::Black),
    assistant: ThemeColor::new(242, 214, 179, Color::White),
    user: ThemeColor::new(230, 168, 74, Color::LightYellow),
    user_background: ThemeColor::new(38, 30, 24, Color::Black),
    user_time: ThemeColor::new(150, 128, 104, Color::Gray),
    heading: ThemeColor::new(240, 179, 91, Color::LightYellow),
    code: ThemeColor::new(200, 184, 166, Color::Gray),
    tool: ThemeColor::new(240, 190, 84, Color::LightYellow),
    diff: ThemeColor::new(201, 138, 82, Color::Yellow),
    success: ThemeColor::new(110, 180, 85, Color::LightGreen),
    warn: ThemeColor::new(240, 199, 94, Color::Yellow),
    error: ThemeColor::new(217, 104, 93, Color::Red),
    dim: ThemeColor::new(191, 174, 156, Color::Gray),
    accent: ThemeColor::new(150, 170, 220, Color::LightBlue),
    diff_added_background: ThemeColor::new(22, 52, 22, Color::DarkGray),
    diff_removed_background: ThemeColor::new(52, 20, 20, Color::DarkGray),
    link: ThemeColor::new(110, 165, 215, Color::LightBlue),
    link_deceptive: ThemeColor::new(215, 125, 55, Color::Yellow),
    context_system_prompt: ThemeColor::new(240, 190, 84, Color::LightYellow),
    context_system_tools: ThemeColor::new(222, 146, 74, Color::Yellow),
    context_mcp_tools: ThemeColor::new(216, 122, 90, Color::LightRed),
    context_messages: ThemeColor::new(214, 96, 96, Color::Red),
    context_autocompact: ThemeColor::new(168, 154, 134, Color::Gray),
    context_free_space: ThemeColor::new(112, 102, 92, Color::DarkGray),
    callout_note: ThemeColor::new(150, 190, 230, Color::LightBlue),
    callout_tip: ThemeColor::new(120, 190, 100, Color::LightGreen),
    callout_important: ThemeColor::new(200, 140, 215, Color::LightMagenta),
    callout_warning: ThemeColor::new(235, 165, 45, Color::Yellow),
    callout_caution: ThemeColor::new(220, 90, 70, Color::LightRed),
    pending_user: ThemeColor::new(150, 128, 104, Color::Gray),
    composer_text: ThemeColor::new(242, 214, 179, Color::White),
    composer_prompt: ThemeColor::new(230, 168, 74, Color::LightYellow),
    composer_panel_background: ThemeColor::new(46, 38, 30, Color::Black),
    operational_ready: ThemeColor::new(143, 136, 128, Color::Gray),
    operational_initializing: ThemeColor::new(179, 138, 80, Color::Yellow),
    operational_working: ThemeColor::new(229, 139, 54, Color::LightYellow),
    operational_thinking: ThemeColor::new(216, 94, 94, Color::LightRed),
    operational_waiting: ThemeColor::new(143, 136, 128, Color::Gray),
    selection_text: ThemeColor::new(23, 19, 16, Color::Black),
    selection_background: ThemeColor::new(230, 168, 74, Color::LightYellow),
    // Lifted a few steps off the shell background: enough to find in
    // peripheral vision, not so much that a focused control shouts.
    focus_text: ThemeColor::new(240, 224, 208, Color::White),
    focus_background: ThemeColor::new(46, 38, 32, Color::DarkGray),
    scrollbar_track: ThemeColor::new(112, 102, 92, Color::DarkGray),
    scrollbar_thumb: ThemeColor::new(230, 168, 74, Color::LightYellow),
    completion_normal: ThemeColor::new(215, 194, 168, Color::White),
    completion_selected_text: ThemeColor::new(23, 19, 16, Color::Black),
    completion_selected_background: ThemeColor::new(230, 168, 74, Color::LightYellow),
    prompt_text: ThemeColor::new(242, 214, 179, Color::White),
    prompt_accent: ThemeColor::new(233, 130, 107, Color::LightRed),
};

/// A cooler, blue-leaning variant of the same role structure.
const COOL_DARK: ThemeColors = ThemeColors {
    background: ThemeColor::new(16, 18, 24, Color::Black),
    assistant: ThemeColor::new(214, 221, 235, Color::White),
    user: ThemeColor::new(112, 176, 232, Color::LightBlue),
    user_background: ThemeColor::new(24, 28, 38, Color::Black),
    user_time: ThemeColor::new(112, 122, 140, Color::Gray),
    heading: ThemeColor::new(126, 186, 240, Color::LightCyan),
    code: ThemeColor::new(178, 188, 204, Color::Gray),
    tool: ThemeColor::new(126, 186, 240, Color::LightCyan),
    diff: ThemeColor::new(126, 160, 200, Color::Cyan),
    success: ThemeColor::new(102, 187, 128, Color::LightGreen),
    warn: ThemeColor::new(224, 186, 96, Color::Yellow),
    error: ThemeColor::new(226, 106, 116, Color::Red),
    dim: ThemeColor::new(146, 156, 174, Color::Gray),
    accent: ThemeColor::new(150, 170, 220, Color::LightBlue),
    diff_added_background: ThemeColor::new(18, 46, 30, Color::DarkGray),
    diff_removed_background: ThemeColor::new(52, 22, 28, Color::DarkGray),
    link: ThemeColor::new(110, 165, 215, Color::LightBlue),
    link_deceptive: ThemeColor::new(215, 125, 55, Color::Yellow),
    context_system_prompt: ThemeColor::new(126, 186, 240, Color::LightCyan),
    context_system_tools: ThemeColor::new(112, 160, 220, Color::Cyan),
    context_mcp_tools: ThemeColor::new(150, 150, 230, Color::LightBlue),
    context_messages: ThemeColor::new(200, 120, 200, Color::LightMagenta),
    context_autocompact: ThemeColor::new(140, 150, 168, Color::Gray),
    context_free_space: ThemeColor::new(92, 100, 116, Color::DarkGray),
    callout_note: ThemeColor::new(150, 190, 230, Color::LightBlue),
    callout_tip: ThemeColor::new(120, 190, 100, Color::LightGreen),
    callout_important: ThemeColor::new(200, 140, 215, Color::LightMagenta),
    callout_warning: ThemeColor::new(228, 176, 80, Color::Yellow),
    callout_caution: ThemeColor::new(226, 106, 116, Color::LightRed),
    pending_user: ThemeColor::new(112, 122, 140, Color::Gray),
    composer_text: ThemeColor::new(214, 221, 235, Color::White),
    composer_prompt: ThemeColor::new(112, 176, 232, Color::LightBlue),
    composer_panel_background: ThemeColor::new(26, 30, 40, Color::Black),
    operational_ready: ThemeColor::new(130, 140, 158, Color::Gray),
    operational_initializing: ThemeColor::new(180, 168, 96, Color::Yellow),
    operational_working: ThemeColor::new(112, 176, 232, Color::LightBlue),
    operational_thinking: ThemeColor::new(180, 140, 230, Color::LightMagenta),
    operational_waiting: ThemeColor::new(130, 140, 158, Color::Gray),
    selection_text: ThemeColor::new(16, 18, 24, Color::Black),
    selection_background: ThemeColor::new(112, 176, 232, Color::LightBlue),
    focus_text: ThemeColor::new(226, 232, 240, Color::White),
    focus_background: ThemeColor::new(30, 38, 52, Color::DarkGray),
    scrollbar_track: ThemeColor::new(92, 100, 116, Color::DarkGray),
    scrollbar_thumb: ThemeColor::new(112, 176, 232, Color::LightBlue),
    completion_normal: ThemeColor::new(200, 210, 226, Color::White),
    completion_selected_text: ThemeColor::new(16, 18, 24, Color::Black),
    completion_selected_background: ThemeColor::new(112, 176, 232, Color::LightBlue),
    prompt_text: ThemeColor::new(214, 221, 235, Color::White),
    prompt_accent: ThemeColor::new(126, 186, 240, Color::LightCyan),
};

impl Default for Theme {
    fn default() -> Self {
        Self::warm_ember()
    }
}

impl Theme {
    pub fn warm_ember() -> Self {
        Self {
            name: "warm-ember",
            depth: ColorDepth::detect(),
            colors: WARM_EMBER,
        }
    }

    pub fn cool_dark() -> Self {
        Self {
            name: "cool-dark",
            depth: ColorDepth::detect(),
            colors: COOL_DARK,
        }
    }

    /// Looks a theme up by name, returning `None` for unknown names so callers
    /// can report the mistake rather than silently using a different palette.
    pub fn by_name(name: &str) -> Option<Self> {
        match name.trim().to_ascii_lowercase().as_str() {
            "warm-ember" | "default" => Some(Self::warm_ember()),
            "cool-dark" => Some(Self::cool_dark()),
            _ => None,
        }
    }

    pub fn names() -> &'static [&'static str] {
        &["warm-ember", "cool-dark"]
    }

    pub fn with_depth(mut self, depth: ColorDepth) -> Self {
        self.depth = depth;
        self
    }

    pub fn depth(&self) -> ColorDepth {
        self.depth
    }

    /// The raw colour pair for a role, before capability resolution.
    pub fn color(&self, role: Role) -> ThemeColor {
        let c = &self.colors;
        match role {
            Role::Background => c.background,
            Role::Assistant => c.assistant,
            Role::User => c.user,
            Role::UserBackground => c.user_background,
            Role::UserTime => c.user_time,
            Role::PendingUser => c.pending_user,
            Role::Heading => c.heading,
            Role::Code => c.code,
            Role::Tool => c.tool,
            Role::Notification => c.dim,
            Role::Question => c.warn,
            Role::Warning => c.warn,
            Role::Error => c.error,
            Role::ToolSuccess => c.success,
            Role::ToolPartialFailure => c.warn,
            Role::Permission => c.error,
            Role::PermissionApproved => c.warn,
            Role::Diff => c.diff,
            Role::DiffHeader => c.tool,
            Role::DiffAdded => c.success,
            Role::DiffRemoved => c.error,
            Role::DiffContext => c.dim,
            Role::DiffAddedBackground => c.diff_added_background,
            Role::DiffRemovedBackground => c.diff_removed_background,
            Role::SyntaxKeyword => c.accent,
            Role::SyntaxType => c.warn,
            Role::SyntaxString => c.success,
            Role::SyntaxNumber => c.error,
            Role::SyntaxComment => c.dim,
            Role::Link => c.link,
            Role::LinkDeceptive => c.link_deceptive,
            Role::CalloutNote => c.callout_note,
            Role::CalloutTip => c.callout_tip,
            Role::CalloutImportant => c.callout_important,
            Role::CalloutWarning => c.callout_warning,
            Role::CalloutCaution => c.callout_caution,
            Role::ContextSystemPrompt => c.context_system_prompt,
            Role::ContextSystemTools => c.context_system_tools,
            Role::ContextMcpTools => c.context_mcp_tools,
            Role::ContextMessages => c.context_messages,
            Role::ContextAutocompactBuffer => c.context_autocompact,
            Role::ContextFreeSpace => c.context_free_space,
            Role::ComposerText => c.composer_text,
            Role::ComposerPrompt => c.composer_prompt,
            Role::ComposerPanelBackground => c.composer_panel_background,
            Role::ComposerPanelEdge => c.composer_panel_background,
            Role::OperationalReady => c.operational_ready,
            Role::OperationalInitializing => c.operational_initializing,
            Role::OperationalWorking => c.operational_working,
            Role::OperationalThinking => c.operational_thinking,
            Role::OperationalWaiting => c.operational_waiting,
            Role::SelectionText => c.selection_text,
            Role::SelectionBackground => c.selection_background,
            Role::FocusText => c.focus_text,
            Role::FocusBackground => c.focus_background,
            Role::ScrollbarTrack => c.scrollbar_track,
            Role::ScrollbarThumb => c.scrollbar_thumb,
            Role::CompletionNormal => c.completion_normal,
            Role::CompletionSelectedText => c.completion_selected_text,
            Role::CompletionSelectedBackground => c.completion_selected_background,
            Role::PromptText => c.prompt_text,
            Role::PromptAccent => c.prompt_accent,
        }
    }

    /// The foreground colour for a role, resolved for this terminal.
    pub fn fg(&self, role: Role) -> Color {
        self.color(role).resolve(self.depth.truecolor())
    }

    /// A foreground-only style for a role.
    pub fn style(&self, role: Role) -> Style {
        Style::default().fg(self.fg(role))
    }

    /// A style with both foreground and background set.
    pub fn style_on(&self, role: Role, background: Role) -> Style {
        Style::default().fg(self.fg(role)).bg(self.fg(background))
    }

    /// The base style for the transcript surface.
    pub fn surface(&self) -> Style {
        Style::default()
            .fg(self.fg(Role::Assistant))
            .bg(self.fg(Role::Background))
    }

    /// The style used to paint a selected region.
    pub fn selection(&self) -> Style {
        self.style_on(Role::SelectionText, Role::SelectionBackground)
            .add_modifier(Modifier::empty())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn warm_ember_matches_the_reference_palette() {
        let theme = Theme::warm_ember().with_depth(ColorDepth::TrueColor);
        assert_eq!(theme.fg(Role::Assistant), Color::Rgb(242, 214, 179));
        assert_eq!(theme.fg(Role::User), Color::Rgb(230, 168, 74));
        assert_eq!(theme.fg(Role::Background), Color::Rgb(23, 19, 16));
        assert_eq!(theme.fg(Role::DiffAdded), Color::Rgb(110, 180, 85));
        assert_eq!(theme.fg(Role::DiffRemoved), Color::Rgb(217, 104, 93));
        assert_eq!(theme.fg(Role::SyntaxKeyword), Color::Rgb(150, 170, 220));
    }

    #[test]
    fn degrades_to_named_colors_on_a_16_color_terminal() {
        let theme = Theme::warm_ember().with_depth(ColorDepth::Ansi16);
        assert_eq!(theme.fg(Role::Assistant), Color::White);
        assert_eq!(theme.fg(Role::DiffAdded), Color::LightGreen);
        assert_eq!(theme.fg(Role::SyntaxKeyword), Color::LightBlue);
    }

    #[test]
    fn shares_palette_slots_between_related_roles() {
        // Syntax strings and diff additions both draw from the success colour.
        let theme = Theme::warm_ember();
        assert_eq!(theme.fg(Role::SyntaxString), theme.fg(Role::DiffAdded));
        assert_eq!(theme.fg(Role::SyntaxNumber), theme.fg(Role::Error));
        assert_eq!(theme.fg(Role::SyntaxComment), theme.fg(Role::Notification));
    }

    #[test]
    fn every_theme_defines_a_distinct_focus_band() {
        for theme in [Theme::warm_ember(), Theme::cool_dark()] {
            assert_ne!(
                theme.fg(Role::FocusBackground),
                theme.fg(Role::Background),
                "{}: the focus band must differ from the shell background, \
                 or a focused control is invisible",
                theme.name
            );
            assert_ne!(
                theme.fg(Role::FocusBackground),
                theme.fg(Role::SelectionBackground),
                "{}: focus and selection must be distinguishable at the same \
                 time, so a list shows both which control is focused and which \
                 row is chosen",
                theme.name
            );
        }
    }

    #[test]
    fn resolves_every_role_without_panicking() {
        let roles = [
            Role::Background,
            Role::Assistant,
            Role::User,
            Role::UserBackground,
            Role::UserTime,
            Role::PendingUser,
            Role::Heading,
            Role::Code,
            Role::Tool,
            Role::Notification,
            Role::Question,
            Role::Warning,
            Role::Error,
            Role::ToolSuccess,
            Role::ToolPartialFailure,
            Role::Permission,
            Role::PermissionApproved,
            Role::Diff,
            Role::DiffHeader,
            Role::DiffAdded,
            Role::DiffRemoved,
            Role::DiffContext,
            Role::DiffAddedBackground,
            Role::DiffRemovedBackground,
            Role::SyntaxKeyword,
            Role::SyntaxType,
            Role::SyntaxString,
            Role::SyntaxNumber,
            Role::SyntaxComment,
            Role::Link,
            Role::LinkDeceptive,
            Role::CalloutNote,
            Role::CalloutTip,
            Role::CalloutImportant,
            Role::CalloutWarning,
            Role::CalloutCaution,
            Role::ContextSystemPrompt,
            Role::ContextSystemTools,
            Role::ContextMcpTools,
            Role::ContextMessages,
            Role::ContextAutocompactBuffer,
            Role::ContextFreeSpace,
            Role::ComposerText,
            Role::ComposerPrompt,
            Role::ComposerPanelBackground,
            Role::ComposerPanelEdge,
            Role::OperationalReady,
            Role::OperationalInitializing,
            Role::OperationalWorking,
            Role::OperationalThinking,
            Role::OperationalWaiting,
            Role::SelectionText,
            Role::SelectionBackground,
            Role::FocusText,
            Role::FocusBackground,
            Role::ScrollbarTrack,
            Role::ScrollbarThumb,
            Role::CompletionNormal,
            Role::CompletionSelectedText,
            Role::CompletionSelectedBackground,
            Role::PromptText,
            Role::PromptAccent,
        ];

        for theme in [Theme::warm_ember(), Theme::cool_dark()] {
            for role in roles {
                // Both depths must produce a concrete colour for every role.
                let _ = theme.clone().with_depth(ColorDepth::TrueColor).fg(role);
                let _ = theme.clone().with_depth(ColorDepth::Ansi16).fg(role);
            }
        }
    }

    #[test]
    fn looks_themes_up_by_name_case_insensitively() {
        assert_eq!(Theme::by_name("Warm-Ember").unwrap().name, "warm-ember");
        assert_eq!(Theme::by_name("  cool-dark ").unwrap().name, "cool-dark");
    }

    #[test]
    fn default_is_an_alias_for_warm_ember() {
        assert_eq!(Theme::by_name("default").unwrap().name, "warm-ember");
        assert_eq!(Theme::default().name, "warm-ember");
    }

    #[test]
    fn rejects_an_unknown_theme_name() {
        assert!(Theme::by_name("solarized").is_none());
    }

    #[test]
    fn every_advertised_name_resolves() {
        for name in Theme::names() {
            assert!(Theme::by_name(name).is_some(), "{name} did not resolve");
        }
    }

    #[test]
    fn surface_style_sets_both_foreground_and_background() {
        let theme = Theme::warm_ember().with_depth(ColorDepth::TrueColor);
        let style = theme.surface();
        assert_eq!(style.fg, Some(Color::Rgb(242, 214, 179)));
        assert_eq!(style.bg, Some(Color::Rgb(23, 19, 16)));
    }
}
