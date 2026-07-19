# Shortcut Geometry

SRUI's accessibility guidelines govern what an application *says*; this document governs what the user's hands *do*. Its premise: for a screen-reader user, the keyboard is the only spatial medium left. Sighted users get spatial grouping for free — related controls sit beside each other on screen, and proximity communicates relatedness without a word. A screen reader linearizes all of that into a transcript. But the keyboard itself is still a two-dimensional, tactile surface, and shortcut assignment is the one place an application can restore spatial semantics: actions that belong together should live under the fingers together.

This is nearly unwritten elsewhere. Accessibility literature concentrates on output — what is spoken, in what order — because that is where the catastrophic failures are, and sighted developers experience shortcuts as labels for buttons they can see anyway, so they assign them semantically. Input geometry only becomes visible to someone who lives at the keyboard for hours. No conformance checklist can fail it; users feel it in the wrists and in the pause before every action.

# 1. Motor Memory Versus Semantic Memory

A mnemonic shortcut ("S for stop") is resolved semantically: recall the word, derive the letter, find the key. A spatial shortcut is resolved by motor memory: the hand goes where it always goes, with no recall step at all. For anything pressed many times an hour, reach beats recall — this is why professional video editors settled on the J/K/L shuttle (rewind, pause, play — three adjacent keys under three adjacent fingers), chosen for people whose eyes are on the footage and therefore cannot look at the keyboard, which is functionally the screen-reader condition.

The two systems serve different tiers of frequency:

- **High-frequency actions** — transport, movement, the operation the application exists for — get spatial clusters, placed for reach.
- **The long tail** — field jumps, occasional commands — gets mnemonics, because occasional actions are recalled, not drummed.

The tiers complement rather than compete. An application whose Alt+letter jumps are mnemonic and whose playback keys form a physical row is using each system where it wins.

# 2. Clusters

A cluster is a set of related actions on physically adjacent keys, with the geometry of the keys mirroring the geometry of the meaning. The canonical shape is the transport row: in a music application, Ctrl+O / Ctrl+P / Ctrl+[ — the keys left of, at, and right of P — map to rewind, play, and fast-forward. Back is literally left of play; forward is literally right. That is the rule in miniature:

- **Adjacency encodes relatedness.** Keys that touch belong to one task.
- **Symmetric actions sit on symmetric keys.** Back/forward, previous/next, decrease/increase straddle their anchor. An anchor with an obvious mnemonic (P for play) makes the whole cluster findable from one remembered key.
- **The cluster is the unit, not the key.** Users learn the shape once and stop thinking about individual letters — which is the entire point.

Precedents beyond J/K/L: WASD, vi's HJKL, Winamp's Z–X–C–V–B transport row, NVDA's numpad review block. Every one trades letter meaning for shape, and every one outlived its competitors.

# 3. Directional Layouts

When actions are literally directional — movement on a map, navigation of a grid — the keys should form the same shape as the space. The left hand's letter block is the usual home, leaving the right hand free for the review and speech keys screen readers put on the numpad and navigation cluster.

- **Four-way movement**: the arrow keys, or WASD. Both are fine; WASD frees the arrows for other roles and adds natural neighbors — Q and E for turning in a shooter, where turn-left sits left of forward and turn-right sits right.
- **Four-way plus a center**: A/D/W/X for west, east, north, south leaves S in the middle of the cross — the "here" key, for interact or where-am-I. The geometry states the semantics: the four directions surround the position they move.
- **Hex grids**: six neighbors map onto the staggered letter rows, which are themselves offset like hex columns. A pointy-top grid (neighbors east, west, and the four diagonals) is A/D for west/east with Q/E/Z/C as the diagonals. A flat-top grid (neighbors north, south, and the four diagonals) is W/X for north/south with the same Q/E/Z/C corners. Hex movement has a reputation for being impossible without sight; laid out this way it is immediate, and it has carried players through full Civilization-scale strategy games.

The test for any directional layout: with fingers resting on it, does pressing the key on the left move left? If describing the layout requires a table, the geometry is wrong.

# 4. Modifier Semantics

Modifiers carry meaning too, and consistency there multiplies every binding. The convention for utility applications:

- **Alt goes somewhere.** Alt+letter jumps focus to a field or list and leaves the user there.
- **Control does something.** Ctrl+letter fires an action — activate a button, check a status — without moving focus.

A user who internalizes "Control acts, Alt travels" can predict half of an unfamiliar application's bindings before pressing them, and can guess safely: a mistaken Alt+key at worst moves focus, never destroys work. SRUI's shortcut actions map directly (jump is the default; `ShortcutAction.Activate` is the Control flavor; `JumpAndActivate` exists for the rare command that should also land the user at its widget). Games are exempt — there the unmodified layout *is* the interface — but the moment a game grows menus and dialogs, the utility convention should govern them.

# 5. Layout Dependence

A cluster is a set of physical positions, not a set of letters. O/P/[ is a row on QWERTY; on Dvorak those letters scatter across the board, and on AZERTY the punctuation moves. Three consequences:

- **Document the shape, not just the keys.** "The keys left and right of P" survives translation; "Ctrl+O" alone does not.
- **Offer rebinding** for any spatial set. SRUI's ShortcutField, reservation probes, and bind-dialog pattern (see the demo's bind dialog) make a rebind screen cheap, and a user on a non-QWERTY layout needs it, not merely appreciates it.
- **Mnemonics degrade more gracefully** across layouts than geometry does — one more reason the long tail should stay mnemonic.

# 6. Discoverability

Geometry leans harder on announcement than mnemonics do: "S for stop" is guessable, "[ for fast-forward" is not. The standard channels (accessibility guidelines, section 8) carry the weight — shortcuts registered with `AddShortcut` announce themselves on focus at zero authoring cost, and a directional layout too rich to announce key-by-key (a game widget, a grid) belongs in `KeyHelp`, where F1 reads the whole shape once, on demand. A cluster whose anchor is mnemonic, whose members self-announce, and whose shape is one `KeyHelp` sentence is fully discoverable despite meaning nothing alphabetically.
