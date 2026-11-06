# Choosing a Model

The setup wizard offers these coding models. Sizes are the download sizes listed on [ollama.com](https://ollama.com/library).

| Model | Download | Good for |
|---|---|---|
| `qwen2.5-coder:1.5b` | 986 MB | Very light and fast, simple answers only. Old PCs. |
| `qwen2.5-coder:3b` | 1.9 GB | Small and quick. Short edits. |
| `qwen2.5-coder:7b` | 4.7 GB | The balanced choice for most PCs. |
| `qwen2.5-coder:14b` | 9.0 GB | Noticeably better at multi-file changes. Needs a strong PC. |
| `qwen2.5-coder:32b` | 20 GB | Best quality of this family. Needs 32 GB or more of memory. |
| `qwen3-coder:30b` | 19 GB | Newest family, 256K context, only 3B active parameters. |

The wizard puts a star on the largest model that fits your PC's memory comfortably. This is a hint, not a rule.

## Tips

- Bigger models write better code but are slower and need more memory.
- If replies are slow, pick a smaller model from the dropdown beside Send, or lower the **Context size** in Settings.
- Press **Download** again on an installed model to update it. Ollama only fetches what changed.
- You can use any Ollama model. Type its name in **Settings > Model**, for example `ollama_chat/llama3.1:8b`.
