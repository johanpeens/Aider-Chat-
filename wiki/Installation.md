# Installation

## Requirements

- Windows 10 or 11
- Visual Studio 2022 (17.x) or Visual Studio 2026, any edition
- About 5 GB of free disk space for the default model (more for larger ones)
- [Ollama](https://ollama.com), [Aider](https://aider.chat) and [git](https://git-scm.com). Git is only needed for Agent mode. The setup wizard can install Ollama and Aider for you.

## Install the extension

1. Download `AiderChat-<version>.vsix` from the [Releases](https://github.com/johanpeens/Aider-Chat-/releases) page.
2. **Close every Visual Studio window.** The installer only runs while Visual Studio is closed.
3. Double-click the `.vsix` and follow the installer.
4. Open Visual Studio and choose **View > Other Windows > Aider Chat**. You can also press Ctrl+Q and type "Aider".

## Update

Install the newer `.vsix` the same way. Your settings and chat history are kept.

## Uninstall

**Extensions > Manage Extensions > Installed**, then uninstall *Aider Chat*. Its data stays in `%LOCALAPPDATA%\AiderVS` until you delete that folder.

## The menu entry is missing

Close Visual Studio, run the line below, and start Visual Studio again.

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" /updateconfiguration
```

Use the folder of your own Visual Studio edition and version if it is different.
