# Gutter Script Generator MCP

Write the narration for a [Gutter](https://github.com/blackberryggames/Gutter) project with the AI app you already use (Claude Desktop, Claude Code, or any MCP client), **without an API key**. The app looks at your panels and writes one line per panel straight into Gutter.

## Easiest setup

In Gutter: **Write script** (with "Claude app via MCP" as the provider) or **Settings → AI apps → Set up Claude app…**. It installs this MCP and connects it to Claude Desktop in a couple of clicks.

## Manual setup

1. Download [`releases/v1.1.0/ScriptGeneratorMcp.exe`](releases/v1.1.0/ScriptGeneratorMcp.exe) (Windows x64, nothing else to install).
2. Add it to your AI app:

**Claude Desktop**: Settings → Developer → Edit config, then add:

```json
{
  "mcpServers": {
    "gutter-script-generator": { "command": "C:\\path\\to\\ScriptGeneratorMcp.exe", "args": [] }
  }
}
```

Restart Claude Desktop.

**Claude Code**:

```
claude mcp add gutter-script-generator -- "C:\path\to\ScriptGeneratorMcp.exe"
```

**Any MCP client over HTTP**: run `ScriptGeneratorMcp.exe --http` (default port 7331) and connect to `http://127.0.0.1:7331/mcp`, e.g.
`claude mcp add --transport http gutter-script-generator http://127.0.0.1:7331/mcp`.

## Use it

1. Open Gutter and open a project in the editor (Settings → AI apps must be on; it is by default).
2. In Claude, say: *"Use the Gutter tools to write the narration for my open project."*
   Or pick the **write_script** prompt from the app's prompt menu.

Lines appear in Gutter as they're written. Ctrl+Z in Gutter undoes each batch.

## Tools

| Tool | What it does |
|---|---|
| `gutter_status` | Which project is open, panel count, how many have lines, first empty panel |
| `get_script_guide` | The narration rules, tone and the project's Story info (characters, world, notes) |
| `get_panels` | Panels as images (up to 6 at a time) with any existing lines |
| `write_lines` | Writes `[{ panel, line }]` into Gutter |
| `get_lines` | Every panel's current line |

## How it connects

```
AI app  ──MCP (stdio or HTTP)──▶  ScriptGeneratorMcp.exe  ──HTTP──▶  Gutter (127.0.0.1:7330)
```

Everything stays on your PC. Gutter's bridge only accepts connections from this machine and refuses browser pages. Point the MCP at a different Gutter port with the `GUTTER_BRIDGE_URL` environment variable.

## Build

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

Requires the .NET 10 SDK. No NuGet packages.

## Check and fix panels (v1.1)

The MCP can also clean up the panels before the script is written. Say: *"Check my Gutter panels and fix the bad cuts"*, or pick the **review_panels** prompt.

| Tool | What it does |
|---|---|
| `check_panels` | Lists panels that look like bad cuts (empty strips, lone speech bubbles, several panels stuck together, thin strips, big empty margins) with a suggested fix |
| `remove_panels` | Removes panels (empty strips, credits, content warnings) |
| `merge_panels` | Stacks neighbouring panels into one (a bubble with its picture, a character cut across two cuts) |
| `split_panel` | Cuts a panel at given heights, or automatically at the gaps |
| `crop_panel` | Crops to a box (left, top, right, bottom as 0-1) |
| `trim_panel` | Crops away empty space around the drawing |
| `restore_panel` | Puts a panel's original image back |
| `add_panel` | Inserts a new image (file path, link or base64) |

Every edit shows up in Gutter right away and can be undone there with Ctrl+Z. Needs Gutter 1.09 or newer.

