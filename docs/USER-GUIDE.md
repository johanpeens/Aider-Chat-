# User guide

## Install

1. Close every Visual Studio window.
2. Double-click `AiderChat-<version>.vsix` and follow the installer. Updating works the same way.
3. Open Visual Studio: **View > Other Windows > Aider Chat** (or press Ctrl+Q and type "Aider").

To remove it: **Extensions > Manage Extensions > Installed**, then uninstall *Aider Chat*. Its data stays in `%LOCALAPPDATA%\AiderVS` until you delete that folder.

## First run: the setup wizard

The wizard opens by itself when something the chat needs is missing, and any time from the **Setup** button. Nothing is downloaded until you press a button.

| Step | What it does |
|---|---|
| 1. Ollama | Shows whether it is installed and running. Can start it or install it with winget. |
| 2. Aider | Installs Aider with `uv tool install` (installing `uv` first if needed), or checks for an update. |
| 3. Coding model | Pick Qwen2.5-Coder 1.5b / 3b / 7b / 14b / 32b or Qwen3-Coder 30b. A star marks the one that suits this PC's memory. Download shows a progress bar, and pressing it again on an installed model updates it. A stopped download carries on where it stopped. |
| 4. Git | Only needed for Agent mode. |

Bigger models write better code but need more memory and are slower.

## The window

From top to bottom: the toolbar and modes, **Settings**, the chat, a bar for pending changes or internet requests, **Skills**, **Context**, and the message box.

### Message box

- **Enter** sends, **Shift+Enter** adds a new line. **Stop** cancels.
- **+** adds context: *Add files*, *Add a folder* (as many source files as fit in the model's memory), *Add the open documents*, and *Set the project folder*.
- The **model dropdown** lists the installed models and links to the setup wizard.
- The **clock** opens the internet history page; the **globe** turns internet access on or off.
- Type `@` to pick any file in the project, or `/` or `$` to pick a skill.

### Context

- **Follow Solution Explorer / editor:** the file you highlight, or the editor tab you open, becomes the context.
- **Pin** keeps a file when the selection changes. **Read-only** lets Aider look at a file without editing it.
- **Attach highlighted editor code** sends the code you have selected with your message.
- Right-click a file, a project or the editor and choose **Add to Aider chat** to pin it.
- The project folder is the folder of the open `.sln`, or the opened folder in Open Folder mode. If Visual Studio reports the wrong one, use **+ > Set the project folder**.
- Every file shown in the chat is a link that opens File Explorer with it selected.

## Modes

Apply edits automatically and Agent mode are alternatives, so ticking one unticks the other. **Ask only** is different: it is a pause switch. While it is ticked Aider never edits, but it does not touch the other two, so Agent mode or Apply automatically are exactly as you left them when you untick it.

| Mode | What happens |
|---|---|
| none ticked (default) | Aider works on a copy of your context files. Its changes appear as diffs, with **Apply changes** and **Discard** in a bar above the message box. Nothing is written until you press Apply. |
| Apply edits automatically | Aider edits your files directly. |
| Agent mode | Aider works on a copy of the whole project (source files only), sees a map of it, adds the files it needs and edits several at once. Always needs Apply. Needs `git`. |
| Ask only | Aider answers and shows code, and never edits anything. Use it for questions like "show me the code". |

Apply refuses to overwrite a file that changed after the request was sent, and says so. Agent mode refuses a project folder that is a drive root, your user folder, Desktop, Documents or Downloads.

## Replies

Replies are shown as Markdown. Code blocks are syntax-coloured to match Visual Studio's own colours (control keywords, types, methods, strings, comments and so on, for dark and light themes), with a copy icon in the corner. Diffs keep the code colours and tint the added and removed lines.

## Skills

Skills are folders with a `SKILL.md`, the open Agent Skills format. They cost nothing until used:

- Type `/name` at the start of a message, or `$name` anywhere, to use a skill for that message only.
- Tick **always** in the Skills panel to send a skill with every message.
- **Pick skills for me** (off by default) matches your request to skill descriptions. **Ask the model to confirm** adds one short model call.

Aider cannot run tools or scripts, so the extension acts as an adapter: it removes the frontmatter, fills in `$ARGUMENTS` and similar placeholders, replaces `` !`command` `` with a note, attaches reference files when you name the skill, lists what Aider cannot do, and gives Aider one read-only file. The chat says which skills were used, and **View** shows exactly what Aider receives.

Skills are found in `.aidervs\skills`, `.agents\skills` and `.claude\skills` in the project and in your user folder, in `%LOCALAPPDATA%\AiderVS\skills`, and in any extra folders you add in the Skills panel. A project's own skills load only after you press **Trust this project's skills**.

## Internet access

Off by default; the **globe** turns it on. When it is on, a message with a web address, or starting with `/search`, or asking to search the web, makes Aider read from the internet, but only after you allow it:

- A bar names exactly what will be read and what leaves your PC (the address or search words, never your code). **Don't ask again** remembers a site, or web searches.
- While reading, a loader shows one line per page and the globe pulses. **Stop** cancels.
- Only plain GET requests are made. Addresses on your own computer or network are refused, also through redirects. At most 3 pages are read, and the text is shortened to a few thousand characters.
- The text reaches Aider as a read-only file marked as untrusted web content.
- **Research, then build:** one message can do both, for example *"research how Ollama sets the context length and then create a file called ollama-context.md that explains it"*. The extension searches for the research part, reads the best pages, and gives Aider the text with the instruction to carry out the rest, so the files appear as proposed changes with the usual **Apply** step. If **Ask only** is ticked and your message reads like a request to create or change files, a bar asks whether to write them this one time.
- If the model says it cannot browse, a bar offers to search the web and ask again.
- The **clock** shows the history: every page and search, newest chat first, including failures and requests you did not allow. It is kept in `%LOCALAPPDATA%\AiderVS\web-history.jsonl` and can be cleared on that page.

## Settings

Saved in `%LOCALAPPDATA%\AiderVS\settings.json`.

| Setting | Meaning |
|---|---|
| Model | The Aider model name, for example `ollama_chat/qwen2.5-coder:14b`. |
| Ollama URL | Default `http://127.0.0.1:11434`. |
| Context size | Tokens Ollama keeps in memory per request (`num_ctx`), default 16384. |
| Aider path | Leave empty to use `%USERPROFILE%\.local\bin\aider.exe` or `aider` on the PATH. |
| Extra args | Passed to Aider, for example `--edit-format whole`. |

## Troubleshooting

| Problem | What to try |
|---|---|
| The menu entry is missing after installing | Close Visual Studio, run `devenv.exe /updateconfiguration` from the Visual Studio `Common7\IDE` folder, and start it again. |
| "Ollama is not answering" | Open **Setup** and press *Start Ollama*, or start Ollama yourself. |
| Replies are slow | Pick a smaller model from the dropdown, or lower the context size. |
| The model says it cannot browse the internet | Turn on the globe and ask again, or accept the bar that offers to search the web for you. |
| Files appear in the wrong folder | Use **+ > Set the project folder**. |

`scripts\aider-qwen.bat` starts the same Aider and model from a terminal (`aider-qwen.bat check` only checks and repairs the setup).
