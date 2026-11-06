# Skills

Skills are folders with a `SKILL.md` file: the open Agent Skills format. A skill holds instructions, for example "keep folder trees flat" or "follow our Clean Architecture rules".

## They cost nothing until used

- Type `/name` at the start of a message, or `$name` anywhere, to use a skill **for that message only**.
- Tick **always** in the Skills panel to send a skill with every message.
- **Pick skills for me** (off by default) matches your request to the skill descriptions. **Ask the model to confirm** adds one short model call.

The Skills header says how many skills exist and how many are sent every time.

## The skill adapter

Aider cannot run tools or scripts, so the extension sits in the middle and adapts each skill:

1. **Finds** the skill and reads it leniently.
2. **Selects** it (you named it, it is marked always, or it matched).
3. **Adapts** it: removes the frontmatter, fills in `$ARGUMENTS` and similar placeholders, replaces `` !`command` `` with a note, attaches reference files when you name the skill, and lists what Aider cannot do.
4. **Delivers** it as one read-only file, so it never piles up in the chat history.

The chat says which skills were used and what could not be carried over. **View** in the Skills panel shows exactly what Aider receives.

## Where skills are found

- `.aidervs\skills`, `.agents\skills` and `.claude\skills` in the project and in your user folder
- `%LOCALAPPDATA%\AiderVS\skills`
- any extra folders you add in the Skills panel

A project's own skills load only after you press **Trust this project's skills**, because they can steer what Aider does.

## Limits

- Scripts and tools in a skill are listed but not run.
- Text is capped at about 4000 characters per message, so very long skills are shortened.
