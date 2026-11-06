# The Chat Window

From top to bottom: the toolbar and modes, **Settings**, the chat, a bar for pending changes or internet requests, **Skills**, **Context**, and the message box.

## Toolbar

| Control | What it does |
|---|---|
| **New chat** | Forgets the conversation and starts a new internet-history session. |
| **Setup** | Opens the setup wizard (see [[Getting Started]]). |
| **Follow Solution Explorer / editor** | The file you highlight, or the editor tab you open, becomes the context. |
| **Ask only (no edits)** | A pause switch. While ticked, Aider answers and never edits. It does not change the other two boxes. |
| **Apply edits automatically** | Aider edits your files directly, without the review step. |
| **Agent mode** | Aider sees a map of the whole project and can add and edit several files. Always needs Apply. |

Apply automatically and Agent mode replace each other. See [[Review and Apply]].

## Context

- **Pin** keeps a file when the selection changes. **Read-only** lets Aider look at a file without editing it.
- **Attach highlighted editor code** sends the code you have selected with your message.
- Right-click a file, a project or the editor and choose **Add to Aider chat** to pin it.
- Every file shown in the chat is a link that opens File Explorer with the file selected.

## The message box

- **+** adds context: *Add files*, *Add a folder* (as many source files as fit in the model's memory), *Add the open documents*, and *Set the project folder*.
- The **model dropdown** lists your installed models, with a link to the setup wizard.
- The **clock** opens the internet history page. The **globe** turns internet access on or off. See [[Internet Access]].
- Type `@` to pick any file in the project, or `/` or `$` to pick a skill. See [[Skills]].
- **Enter** sends, **Shift+Enter** adds a new line, **Stop** cancels.

## Replies

Replies are shown as Markdown. Code blocks are syntax-coloured to match Visual Studio's own colours, for dark and light themes, with a copy icon in the corner. Supported languages: C#, Razor, JavaScript and TypeScript, Python, SQL, shell and PowerShell, JSON, YAML, XML, HTML and XAML, and diffs.

## Settings

Saved in `%LOCALAPPDATA%\AiderVS\settings.json`.

| Setting | Meaning |
|---|---|
| Model | The Aider model name, for example `ollama_chat/qwen2.5-coder:14b`. |
| Ollama URL | Default `http://127.0.0.1:11434`. |
| Context size | Tokens Ollama keeps in memory per request, default 16384. |
| Aider path | Leave empty to use `%USERPROFILE%\.local\bin\aider.exe` or `aider` on the PATH. |
| Extra args | Passed to Aider, for example `--edit-format whole`. |
