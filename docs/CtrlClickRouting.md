# Ctrl-Click Routing

Ctrl-click routing connects VSTs, groups and endpoints without dragging every
pin. It works in the **VST / Route** canvas. Enter connects; grouping is an
explicit **Create Group** menu command.

## Basic Pattern

1. Hold `Ctrl` and click one or more source endpoint cards on the left.
2. Ctrl-click VST nodes on the canvas or VST names in the plugin browser.
3. Ctrl-click one or more destination endpoint cards on the right.
4. Release `Ctrl`.
5. Press `Enter` to apply the selection.

Selected items stay highlighted. Press `Esc` before `Enter` to clear the pending
selection without changing the graph.

The operation uses the current canvas and current callback side. Items from a
different canvas are not included.

## Processing Order

VST order is the order in which the VSTs were Ctrl-clicked:

```text
first selected VST -> second selected VST -> third selected VST
```

Example:

1. Ctrl-click `Hardware In 4` on the left.
2. Ctrl-click `Noise Reduction`.
3. Ctrl-click `EQ`.
4. Ctrl-click `Compressor`.
5. Ctrl-click `Hardware In 4` on the right.
6. Press `Enter`.

The result is a connected chain, without creating a group:

```text
Hardware In 4 -> Noise Reduction -> EQ -> Compressor -> Hardware In 4
```

The app connects as many matching pins as the selected endpoints and VSTs
support, up to the current visible/effective channel layout.

## Select VSTs From The Browser

Ctrl-clicking a VST name in the plugin browser adds it to the pending chain and
keeps it highlighted. You can continue Ctrl-clicking more plugin names. The VSTs
are loaded only when `Enter` completes the operation.

Browser selection can be mixed with existing canvas nodes. Existing nodes keep
their identity; browser choices create new nodes.

## Supported Combinations

### Source, VST Chain, Destination

```text
Ctrl-click source -> Ctrl-click VSTs -> Ctrl-click destination -> Enter
```

Connects the VSTs in selection order and attaches the chain to both endpoints.
Existing links are retained; selecting an already-connected chain does not group it.

### VSTs Only

```text
Ctrl-click VSTs -> Enter
```

Connects the selected VSTs in order without adding endpoints or a group. Use
this to prepare a chain before its source and destination are chosen.

### Sources To One VST

```text
Ctrl-click source endpoints -> Ctrl-click one VST node -> Enter
```

Connects every selected source to the VST input. Repeat later with destinations
when needed.

### One VST To Destinations

```text
Ctrl-click one VST node -> Ctrl-click destination endpoints -> Enter
```

Connects the VST output to every selected destination.

### Direct Endpoint Routing

```text
Ctrl-click source endpoints -> Ctrl-click destination endpoints -> Enter
```

Creates matching endpoint-to-endpoint cables without a VST between them. The
route must be valid on the current canvas.

### Connect An Existing Group

```text
Ctrl-click source endpoints -> Ctrl-click VST group -> Ctrl-click destinations -> Enter
```

Attaches the selected endpoints to the existing group. The group members and
internal wiring are preserved.

Multiple groups and VSTs can be selected together. They connect serially in
click order, with sources attached to the first item and destinations to the last.

## Canvas Controls And Grouping

Open **Menu > Settings > Canvas Controls**:

- **Box Selection** defaults to on. Left-drag empty canvas to select nodes,
  groups, endpoints and cables. Release to open the selection menu. Hold Ctrl
  to add to the current selection; Esc cancels the drag.
- **Automatic Grouping** defaults to off. When enabled, dropping a VST onto
  another VST or group groups it. Browser/file drops onto group cards follow
  the same setting. With it off, use the explicit grouping menu instead.
- **Automatic Routing** defaults to off. Grouping loose VSTs adds no cables
  while off. When on, loose VSTs are connected in selection order, and adding
  a loose VST to a group appends it to the chain. It does not wire anything
  merely because the setting is changed.

Ctrl-click existing VST nodes or box-select them, then choose **Create Group**.
This keeps existing internal and external cables. An existing chain keeps its
signal order; loose nodes follow Ctrl-click order, or left-to-right then
top-to-bottom order for a selection box. Nested groups are not supported.
The selection menu also offers the Automatic Routing checkbox.

Box-select cables and press Delete, or choose **Remove Cables**, to remove
only those cables. Their nodes remain. Ctrl-click can also add/remove cables
from the selection.

## Multiple Sources And Destinations

Ctrl-click can select several endpoint cards before `Enter`:

```text
sources A + B -> one VST -> destinations C + D
```

For direct endpoint routing, every compatible source/destination pair receives
matching channel cables. For VST routing, selected sources connect to the first
node and selected destinations connect from the last node.

If a route is unavailable for the current callback mode, the app skips that
route and writes the reason to the log.

## Groups And Incomplete Audio Paths

A newly created group follows the same audio rule as a normal VST path:

- an input cable claims the source path
- an incomplete path remains silent
- audio passes only after the chain reaches a valid output

An empty group does not pass audio. Add and connect a VST inside it, or use
**Auto-Wire Chain** after adding group members.

## Correcting A Selection

- Press `Esc` to cancel all pending Ctrl-click selections.
- Ctrl-clicking an already selected browser VST or endpoint moves it to the end
  of the current selection order.
- Ctrl-clicking a selected canvas VST or group deselects it. Select it again to
  append it to the order.
- Complete the current selection with `Enter` before starting an unrelated
  quick route.
- The in-app log names each selected source, VST, group, and destination, then
  reports the number of cables created.

## Related Guides

- [User Guide](UserGuide.md)
- [VFX Text Commands](../VFX_COMMANDS.md)
