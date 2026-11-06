# Review and Apply

By default Aider **never touches your real files directly**.

## How it works

1. You send a message. The extension copies your context files (or, in Agent mode, the filtered project) into a sandbox folder.
2. Aider edits the copy.
3. The extension compares the copy with your originals and shows every change as a diff in the chat.
4. A bar above the message box says how many files changed, with **Apply changes** and **Discard**.
5. **Apply** writes the changed files into your project. **Discard** throws the sandbox away.

Apply refuses to overwrite a file that **changed after your request was sent**, and tells you which one.

## Modes

| Mode | What happens |
|---|---|
| none ticked (default) | The review flow above. |
| Apply edits automatically | Aider edits your files directly. |
| Agent mode | Aider works on a copy of the whole project (source files only), sees a map of it, adds the files it needs and edits several at once. Always needs Apply. Needs `git`. |
| Ask only | A pause switch. Aider answers and shows code, and never edits. |

**Apply edits automatically** and **Agent mode** are alternatives, so ticking one unticks the other. **Ask only** is a pause switch: it does not change them, so they are exactly as you left them when you untick it.

If Ask only is ticked and your message reads like a request to create or change files, a bar asks whether to write them this one time.

## Agent mode limits

- Only source files are copied. `bin`, `obj`, `node_modules`, packages, binaries and hidden folders are skipped, and there are size limits.
- It refuses a project folder that is a drive root, your user folder, Desktop, Documents or Downloads.
- It needs `git` on the PATH so Aider can build its map of the project.
