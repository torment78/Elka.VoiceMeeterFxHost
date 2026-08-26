# Ctrl-Click Routing

Ctrl-click routing creates VST groups and endpoint cables without dragging every
pin. It works in the **VST / Route** canvas.

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

The result is a VST group with this internal order:

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

Creates a VST group, auto-wires its members in selection order, and connects the
group to both endpoints.

### VSTs Only

```text
Ctrl-click VSTs -> Enter
```

Creates an unattached VST group. Use this when the processing chain should be
prepared before its source and destination are chosen.

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

Multiple groups can be selected when the same sources or destinations should be
connected to each group.

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
- Complete the current selection with `Enter` before starting an unrelated
  quick route.
- The in-app log names each selected source, VST, group, and destination, then
  reports the number of cables created.

## Related Guides

- [User Guide](UserGuide.md)
- [VFX Text Commands](../VFX_COMMANDS.md)
