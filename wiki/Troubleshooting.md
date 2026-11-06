# Troubleshooting

| Problem | What to try |
|---|---|
| The menu entry is missing after installing | See [[Installation]]: close Visual Studio, run `devenv.exe /updateconfiguration`, start it again. |
| The installer says it is already installed | The new package has the same version number. Uninstall the old one first, or install a newer release. |
| "Ollama is not answering" | Open **Setup** and press *Start Ollama*, or start Ollama yourself. |
| Replies are slow | Pick a smaller model from the dropdown beside Send, or lower the context size. See [[Choosing a Model]]. |
| Aider is not found | Open **Setup** and press *Install Aider*, or set the path in **Settings > Aider path**. |
| The model says it cannot browse the internet | Turn on the globe and ask again, or accept the bar that offers to search for you. See [[Internet Access]]. |
| Nothing is written to my files | Is **Ask only** ticked? It pauses all editing. Otherwise press **Apply changes** in the bar above the message box. |
| Files appear in the wrong folder | Use **+ > Set the project folder**. |
| Agent mode says the project is too broad | Open the project's own folder in Visual Studio, not a drive, your user folder, Desktop, Documents or Downloads. |
| Agent mode cannot map the project | Install [git](https://git-scm.com) and make sure it is on the PATH. |

## Where things are stored

`%LOCALAPPDATA%\AiderVS` holds the settings, the per-project chat history, the internet history and your own skills. It is safe to delete: the extension recreates what it needs.

`scripts\aider-qwen.bat` in the repository starts the same Aider and model from a terminal (`aider-qwen.bat check` only checks and repairs the setup).
