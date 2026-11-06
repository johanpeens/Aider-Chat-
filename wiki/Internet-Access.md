# Internet Access

Aider's model cannot browse by itself. The extension can read web pages and search the web **for** it. This is **off by default**, **read-only**, and **always asks first**.

## Turning it on

Click the **globe** next to Send. It lights up when on and pulses while the web is being read.

## What triggers it

A message that contains a web address, starts with `/search`, or asks to search, browse or research, for example:

- `/search how to set num_ctx in ollama`
- *"search the web for blazor virtualize examples"*
- *"read https://learn.microsoft.com/dotnet/api/system.string"*
- *"research X and then create a file called notes.md"*: the search covers X, and the rest is the job for Aider.

## Permission, loader and history

- **Permission:** a bar names exactly what will be read and what leaves your PC (the page address or the search words, never your code). **Don't ask again** remembers a site, or web searches.
- **Loader:** a progress line per page while reading. **Stop** cancels.
- **History:** the **clock** switches the chat area to a page listing every page and search, newest chat first, including failures and requests you did not allow. Click it again to go back. It is kept in `%LOCALAPPDATA%\AiderVS\web-history.jsonl` and can be cleared on that page.

## Safety

- Only plain GET requests. No cookies, no logins.
- Addresses on your own computer or network (`localhost`, `192.168.x.x`, `10.x.x.x` and so on) are refused, also through redirects.
- At most 3 pages, 1.5 MB each, shortened to a few thousand characters in total.
- The text goes to Aider as a read-only file marked as untrusted web content.

## Search

DuckDuckGo's plain results, with Wikipedia as a fallback. The top two readable results are read.

## If the model says it cannot browse

A bar offers to search the web for your request and ask again.

## Limits

Scripted pages that need a browser to draw them, and PDFs, are not read.
