# AI Assistant

CalcpadCE includes an optional **AI assistant** powered by the [Anthropic Claude API](https://www.anthropic.com/).
It helps you draft worksheets, explain formulas, convert notes into Calcpad, and — in *agent mode* —
read, evaluate and edit the current worksheet on your behalf.

The assistant is **opt-in**: nothing is sent anywhere until you enter an API key and press **Send**.

## Opening the assistant

Open it from the menu bar: **AI Assistant**. A dockable chat panel appears next to the editor.

## Setting your API key

The assistant talks to Claude using *your own* Anthropic API key.

1. Create a key at [console.anthropic.com](https://console.anthropic.com/) → **API Keys**.
2. In the assistant panel, paste it into the **API key** box.
3. Choose a model (default **Claude Opus**; faster **Sonnet** / **Haiku** are also offered).

Your key is stored **encrypted on your machine** using the Windows Data Protection API (DPAPI,
current-user scope) in `%AppData%\CalcpadCE\anthropic.key`. It never leaves your computer except as the
`Authorization` header of requests you make to Anthropic. Delete that file (or clear the box) to remove it.

## Privacy — what gets sent

- Your **chat messages** are sent to Anthropic to generate replies.
- Your **worksheet is *not* sent** unless you explicitly opt in with the **Send worksheet as context**
  checkbox (or use *agent mode*, below, where the model can request it through a tool).
- Standard API [usage policies](https://www.anthropic.com/legal/aup) apply. Don't paste secrets you
  wouldn't share with a third-party service.

## Markdown-first nomenclature

The assistant is tuned for CalcpadCE's **markdown-first** style (`#md full`):

- Prose is written directly (no quotes); headings use `#` … `######`.
- Math is delimited: inline `$ … $`, block `$$ … $$`.
- Lists, tables, blockquotes and fenced code work across multiple lines.

So you can ask, for example, *"Write a simply-supported beam worksheet with span 6 m and load 10 kN/m"*
and paste the reply straight in — or let the assistant insert it for you.

See [quick-reference](quick-reference.md) for the full directive list and `#md full` details.

## Inserting replies & snippets

- **Insert** puts the assistant's code block into the worksheet at the caret (highlighted and
  recalculated automatically).
- The editor also offers **inline snippet completion**: start typing a key such as `beam`, `table`,
  `list`, `if`, `title`… and pick the green *snippet* entry to drop in a ready-made markdown-first
  fragment. The same catalogue is available from the **Snippets** menu and is offered to the assistant.
- **Convert to Markdown** (menu) rewrites a classic worksheet (`'text'`, `"title"`, bare math) into
  `#md full` form. It is undoable (Ctrl+Z).

## Agent mode (tools)

In *agent mode* the assistant can use tools to work on your worksheet directly, running a short loop
until it has an answer:

| Tool | What it does |
| --- | --- |
| `get_worksheet` | Reads the full current worksheet source. |
| `insert_calcpad` | Inserts Calcpad/markdown text at the caret. |
| `evaluate_calcpad` | Evaluates a snippet and returns the computed result, so the model can check a formula before inserting it. |

Every tool runs **locally in CalcpadCE** — the model only sees what the tools return. You stay in control:
edits are ordinary editor changes you can undo.

## Notes

- The assistant requires an internet connection and a valid API key; API usage is billed to your
  Anthropic account.
- Replies are streamed token-by-token.
- The assistant is a drafting aid: **always review generated engineering calculations** before relying
  on them.
