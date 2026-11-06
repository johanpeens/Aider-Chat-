# Building from Source

You need Visual Studio 2022 or later with its MSBuild. The "Visual Studio extension development" workload is only needed to debug with F5.

```powershell
.\build.ps1                      # Release build, writes artifacts\AiderChat-<version>.vsix
.\build.ps1 -Configuration Debug
```

The script finds MSBuild with `vswhere`, restores the NuGet packages, rebuilds and copies the package. A first restore downloads a few hundred MB.

**Bump `Version` in `src/AiderVS/source.extension.vsixmanifest` for every build you install.** Visual Studio refuses to install the same version twice.

## Code layout

```
src/AiderVS/
  AiderPackage.cs, AiderToolWindow.cs, Commands.cs   package, tool window, menu commands
  AiderVS.vsct, VSPackage.resx                        command table and its resource set
  source.extension.vsixmanifest                       extension identity and version
  Chat/        the chat window (XAML plus partial classes)
  Rendering/   Markdown, syntax colouring, diffs, code blocks
  Aider/       running Aider, the review sandbox, setup checks and downloads
  Skills/      SKILL.md reading and the skill adapter
  Web/         the read-only web reader and the access history
  Common/      settings, file lists, links, Visual Studio selection and editor access
```

The full notes, including the packaging pitfalls, are in [docs/DEVELOPMENT.md](https://github.com/johanpeens/Aider-Chat-/blob/main/docs/DEVELOPMENT.md).
