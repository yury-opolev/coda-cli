namespace Coda.Agent.Watchers;

/// <summary>
/// The notes file template and the prompts that drive the SessionMemory fork.
/// The update instruction makes the fork return the full updated file (Coda
/// writes it) rather than calling an Edit tool.
/// </summary>
public static class SessionMemoryPrompts
{
    /// <summary>The skeleton used when no notes exist yet.</summary>
    public const string DefaultTemplate = """
        # Session Title
        _A short and distinctive 5-10 word descriptive title for the session. Super info dense, no filler_

        # Current State
        _What is actively being worked on right now? Pending tasks not yet completed. Immediate next steps._

        # Task specification
        _What did the user ask to build? Any design decisions or other explanatory context_

        # Files and Functions
        _What are the important files? In short, what do they contain and why are they relevant?_

        # Workflow
        _What bash commands are usually run and in what order? How to interpret their output if not obvious?_

        # Errors & Corrections
        _Errors encountered and how they were fixed. What did the user correct? What approaches failed and should not be tried again?_

        # Codebase and System Documentation
        _What are the important system components? How do they work/fit together?_

        # Learnings
        _What has worked well? What has not? What to avoid? Do not duplicate items from other sections_

        # Key results
        _If the user asked a specific output such as an answer to a question, a table, or other document, repeat the exact result here_

        # Worklog
        _Step by step, what was attempted, done? Very terse summary for each step_
        """;

    public const string SystemPrompt =
        "You maintain a concise, information-dense markdown notes file that captures the state of a coding session so work can resume after the context is lost. You output only the updated notes file content — nothing else.";

    /// <summary>
    /// Builds the update instruction appended after the conversation transcript.
    /// The fork must return the COMPLETE updated notes file, preserving every
    /// section header and its italic _description_ line exactly.
    /// </summary>
    public static string BuildUpdatePrompt(string currentNotes) => $"""
        IMPORTANT: This message and these instructions are NOT part of the actual user conversation. Do NOT reference note-taking or these instructions in the notes content.

        Based on the conversation above (excluding this instruction and any system prompt), update the session notes.

        Here are the current notes:
        <current_notes>
        {currentNotes}
        </current_notes>

        Rules:
        - Output the COMPLETE updated notes file and nothing else (no preamble, no code fences).
        - Keep the exact structure: never modify, delete, or add section headers (lines starting with '#') or the italic _section description_ lines immediately under them.
        - Only update the content BELOW each italic description. Leave a section unchanged if there is nothing substantial to add (no filler like "No info yet").
        - Write detailed, specific content: file paths, function names, exact commands, error messages.
        - Always keep "Current State" reflecting the most recent work — it is critical for continuity.
        """;
}
