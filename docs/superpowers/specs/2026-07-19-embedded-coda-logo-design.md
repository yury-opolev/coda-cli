# Embedded Coda Logo Design

## Goal

Replace the generated Figlet startup wordmark with the six-line Unicode logo supplied in `C:\Users\yurio\Desktop\coda-logo.txt`.

## Design

- Embed the six non-empty logo rows in `Branding.BannerLines`.
- Preserve the supplied leading alignment and box-drawing characters.
- Do not read the Desktop file at runtime; packaged installations must be self-contained.
- Replace `Banner.WordmarkInto`'s `FigletText` with literal styled text rendered in the existing accent color.
- Keep the welcome panel, version, provider/model, working directory, and command hints unchanged.
- The source file's initial blank line is not part of the embedded logo.

The embedded rows use the refreshed file contents:

```text
 ┌───┐      ┌┐
 │┬─┐│┌──┐┌─┘│┌──┐
 ││ └┘│┬┐││┬┐││┬┐│
 ││ ┌┐││││││││││││
 │└─┴││└┴││└┴││└┴└┐
 └───┘└──┘└──┘└───┘
```

## Testing

- Add a banner test that asserts the supplied first and last rows appear in rendered output.
- Keep existing branding, provider/model, sign-in, version, and working-directory tests green.
- Build and run the focused TUI tests before release.

## Release

- Bump Coda from 0.1.71 to 0.1.72.
- Merge through a pull request into `origin/main`.
- Build/package the merged revision and update the locally installed global `coda` tool.
