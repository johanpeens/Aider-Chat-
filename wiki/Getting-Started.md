# Getting Started

## First run: the setup wizard

The wizard opens by itself when something is missing, and any time from the **Setup** button. **Nothing is downloaded until you press a button.**

| Step | What it does |
|---|---|
| 1. Ollama | Shows whether it is installed and running. Can start it, or install it with winget. |
| 2. Aider | Installs Aider with `uv tool install` (installing `uv` first if needed), or checks for an update. |
| 3. Coding model | Pick a model. A star marks the one that suits your PC's memory. Download shows a progress bar. A stopped download carries on where it stopped. |
| 4. Git | Only needed for Agent mode. |

See [[Choosing a Model]] for which one to pick.

## Your first change

1. Open a project or folder in Visual Studio and open the **Aider Chat** window.
2. Click a file in Solution Explorer. It appears under **Context**.
3. Type what you want, for example *"add a null check to the constructor"*, and press **Enter**.
4. Read the diff in the reply. Added lines are green, removed lines red, and the code keeps its colours.
5. Press **Apply changes** in the bar above the message box, or **Discard**.

Nothing is written to your files until step 5.

## Good to know

- **Enter** sends. **Shift+Enter** adds a new line. **Stop** cancels.
- The project folder is the folder of the open `.sln`, or the folder you opened. If it is wrong, use **+ > Set the project folder**.
- Each message starts a fresh Aider process. Chat history is kept per project.
