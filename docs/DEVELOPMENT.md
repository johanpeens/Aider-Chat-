# Development notes

## Build

You need Visual Studio 2022 or later with its MSBuild (the "Visual Studio extension development" workload is only needed to debug with F5).

```powershell
.\build.ps1                 # Release build, writes artifacts\AiderChat-<version>.vsix
.\build.ps1 -Configuration Debug
```

The script finds MSBuild with `vswhere`, restores the NuGet packages, rebuilds, and copies the package. Packages come from NuGet (Microsoft.VisualStudio.SDK and Microsoft.VSSDK.BuildTools 17.x); a first restore downloads a few hundred MB.

**Bump `Version` in `src/AiderVS/source.extension.vsixmanifest` for every build you install.** Visual Studio refuses to install the same version twice.

## Code layout

```
src/AiderVS/
  AiderPackage.cs, AiderToolWindow.cs, Commands.cs   package, tool window, menu commands
  AiderVS.vsct, VSPackage.resx                        command table and its resource set (must be named VSPackage)
  source.extension.vsixmanifest                       extension identity and version
  Chat/        the chat window (XAML plus partial classes: add menu, setup wizard, skills panel, web access)
  Rendering/   Markdown, syntax colouring, diffs, code blocks with copy icon
  Aider/       running Aider, the review sandbox (PendingChange), setup checks and downloads
  Skills/      SKILL.md reading and the skill adapter
  Web/         read-only web reader and the access history
  Common/      settings and models, file lists, links, Visual Studio selection and editor access
```

All code is in the `AiderVS` namespace; folders only group files.

## How the main pieces work

**One Aider process per message.** `AiderRunner` starts `aider --message-file ...` with the context files, a per-project chat history file, and `--yes-always --no-pretty`. Skill and web text arrive as extra `--read` files, so they never pile up in the chat history.

**Review sandbox.** Unless "Apply edits automatically" is on, `PendingChange` copies the context files (or, in Agent mode, the filtered project plus a git repository so Aider can build its map) into a sandbox. Aider edits the copy. Afterwards the copy is compared with the originals using file hashes, diffs are built, and **Apply** copies the changed files back, refusing any file that changed in the meantime.

**Skill adapter** (`Skills/SkillAdapter.cs`). Discover, select, adapt, deliver: see the class comment. Selection is by explicit name, "always", or (opt-in) word matching plus a short confirmation call to the local model.

**Web reader** (`Web/WebReader.cs`). Detect a request, ask permission in the UI, fetch with plain GET (no cookies, redirects followed by hand and re-checked, private addresses refused), convert HTML to text, hand it to Aider as untrusted reference. Every request is recorded by `WebLog`.

**Rendering.** `CodeText` is an attached property that fills a panel from Markdown text: `MarkdownRenderer` for prose, `SyntaxHighlighter` for code (a dependency-free tokenizer following Visual Studio's colour classification), `DiffRenderer` for diffs.

## Visual Studio extension gotchas

- The `.pkgdef` needs `UseCodebase` (set in the csproj), or Visual Studio cannot find the assembly.
- The compiled menus are merged into a resource set that must be named `VSPackage` (`VSPackage.resx` with `MergeWithCTO`).
- The SDK-style project imports `Sdk.props`/`Sdk.targets` explicitly and imports the VSSDK targets after them, so the output folder is known when the packaging targets read it.
- In Open Folder mode `DTE.Solution.FullName` is the folder itself, not a file.

## Testing

There is no automated test project yet. Logic that does not need Visual Studio (the Markdown and diff renderers, the highlighter, the skill adapter, the web reader, the sandbox) can be exercised from PowerShell by loading `AiderVS.dll`; creating `AiderVS.ChatControl` on an STA thread and calling `Measure`/`Arrange` checks that the XAML loads. Newtonsoft.Json must be loaded from the NuGet cache in that case, because Visual Studio normally provides it.

## Not done yet

- A licence file. Choose one before publishing.
- An automated test project.
- Support for scripted (JavaScript-rendered) pages and PDFs in the web reader.
