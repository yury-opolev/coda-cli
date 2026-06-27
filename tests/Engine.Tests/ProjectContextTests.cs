using Coda.Agent;

namespace Engine.Tests;

public sealed class ProjectContextTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_ctx_").FullName;

    private string NewDir(params string[] parts)
    {
        var p = Path.Combine([this.root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void Returns_null_when_no_claude_md_anywhere()
    {
        var work = this.NewDir("proj");
        var userClaude = this.NewDir("user", ".claude"); // empty
        Assert.Null(ProjectContext.Load(work, userClaude));
    }

    [Fact]
    public void Loads_cwd_claude_md()
    {
        var work = this.NewDir("proj");
        File.WriteAllText(Path.Combine(work, "CLAUDE.md"), "Use tabs not spaces.");
        var ctx = ProjectContext.Load(work, this.NewDir("emptyuser"));
        Assert.NotNull(ctx);
        Assert.Contains("Use tabs not spaces.", ctx);
    }

    [Fact]
    public void Includes_ancestor_then_cwd_in_priority_order()
    {
        var parent = this.NewDir("repo");
        var work = Path.Combine(parent, "sub");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(parent, "CLAUDE.md"), "PARENT-RULE");
        File.WriteAllText(Path.Combine(work, "CLAUDE.md"), "CHILD-RULE");
        var ctx = ProjectContext.Load(work, this.NewDir("emptyuser2"))!;
        // Both present; the cwd (higher priority) appears after the ancestor.
        Assert.Contains("PARENT-RULE", ctx);
        Assert.Contains("CHILD-RULE", ctx);
        Assert.True(ctx.IndexOf("PARENT-RULE", StringComparison.Ordinal) < ctx.IndexOf("CHILD-RULE", StringComparison.Ordinal));
    }

    [Fact]
    public void Loads_user_claude_md_with_lowest_priority()
    {
        var work = this.NewDir("proj2");
        File.WriteAllText(Path.Combine(work, "CLAUDE.md"), "PROJECT");
        var userClaude = this.NewDir("home", ".claude");
        File.WriteAllText(Path.Combine(userClaude, "CLAUDE.md"), "USER-GLOBAL");
        var ctx = ProjectContext.Load(work, userClaude)!;
        Assert.True(ctx.IndexOf("USER-GLOBAL", StringComparison.Ordinal) < ctx.IndexOf("PROJECT", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolves_at_path_import_one_level()
    {
        var work = this.NewDir("proj3");
        File.WriteAllText(Path.Combine(work, "shared.md"), "IMPORTED-CONTENT");
        File.WriteAllText(Path.Combine(work, "CLAUDE.md"), "before\n@shared.md\nafter");
        var ctx = ProjectContext.Load(work, this.NewDir("emptyuser3"))!;
        Assert.Contains("IMPORTED-CONTENT", ctx);
        Assert.Contains("before", ctx);
        Assert.Contains("after", ctx);
    }

    [Fact]
    public void Caps_total_size()
    {
        var work = this.NewDir("proj4");
        File.WriteAllText(Path.Combine(work, "CLAUDE.md"), new string('x', 100_000));
        var ctx = ProjectContext.Load(work, this.NewDir("emptyuser4"))!;
        Assert.True(ctx.Length <= 33_000); // 32KB cap + small notice
    }

    [Fact]
    public void Does_not_resolve_at_import_outside_the_file_directory()
    {
        var outside = this.NewDir("outside");
        File.WriteAllText(Path.Combine(outside, "secret.md"), "SECRET-SHOULD-NOT-APPEAR");
        var work = this.NewDir("proj5");
        File.WriteAllText(Path.Combine(work, "CLAUDE.md"), "before\n@../outside/secret.md\nafter");
        var ctx = ProjectContext.Load(work, this.NewDir("emptyuser5"))!;
        Assert.DoesNotContain("SECRET-SHOULD-NOT-APPEAR", ctx);
        Assert.Contains("before", ctx);
        Assert.Contains("after", ctx);
    }

    [Fact]
    public void Priority_is_user_then_ancestor_then_cwd()
    {
        var parent = this.NewDir("repoX");
        var work = Path.Combine(parent, "sub");
        Directory.CreateDirectory(work);
        var userClaude = this.NewDir("homeX", ".claude");
        File.WriteAllText(Path.Combine(userClaude, "CLAUDE.md"), "USER");
        File.WriteAllText(Path.Combine(parent, "CLAUDE.md"), "ANCESTOR");
        File.WriteAllText(Path.Combine(work, "CLAUDE.md"), "CWD");
        var ctx = ProjectContext.Load(work, userClaude)!;
        var u = ctx.IndexOf("USER", StringComparison.Ordinal);
        var a = ctx.IndexOf("ANCESTOR", StringComparison.Ordinal);
        var c = ctx.IndexOf("CWD", StringComparison.Ordinal);
        Assert.True(u < a && a < c);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { }
    }
}
