# SRUI Accessibility Guidelines

SRUI's output is speech. There is no glance, no skim, no peripheral vision: every word a widget produces costs the user linear listening time, and every duplicated word is time stolen twice. These guidelines exist because the most common failures in SRUI applications are not missing information but *repeated* information, and information smuggled into the wrong semantic slot. Both come from authors reasoning about what a screen *contains* instead of what it *says*.

The document assumes familiarity with the widget surface (CLAUDE.md section 6) and the event model (docs/architecture.md).

# 1. The Transcript Test

Before shipping a screen, narrate it. Imagine — or better, record with a headless app and a test reader (section 9) — the exact utterances a user hears when they tab through every widget, arrow through every list, and activate every control. That transcript is the interface. Judge it the way you would judge prose:

- Does any word appear twice in a row for the same reason? Cut one.
- Does reaching a routine control take a paragraph? Cut the paragraph.
- Could a sentence be a slot instead? (Section 3.)

If a widget speaks the same fact the previous utterance already carried — the dialog title again, the role in the name, the state in both the name and an announcement — the screen fails the test. Most of the rules below are corollaries of this one.

# 2. One Fact, Spoken Once

Every fact should have exactly one home, and it should be spoken from that home exactly once per user action. The framework already enforces this on its side: focused-state setters speak like the equivalent user action, label changes on the focused widget speak the delta alone, list item operations own their structural announcements. Application code breaks the rule in three recurring ways.

## 2.1 Announcing on top of a widget change

Setting a property on a focused widget already speaks. Following it with `Announce` of the same fact produces double output:

```csharp
// Wrong: the Name setter on the focused button already speaks the new label.
button.Name = "[x] Fireball";
app.Announce("Fireball selected.");   // second utterance for the same toggle
```

`Announce` is for facts that no widget change carries — an operation's outcome, a count, an error. If a property change already tells the story, the announcement is noise; if you want different wording than the property change produces, use the silent setter (`SetNameSilently`, `SetItemsSilently`, `SetTextSilently`) and announce once.

## 2.2 Restating the label in the description or prompt

A `Description` that begins by paraphrasing the `Name` makes the widget introduce itself twice ("Quit button. Press again to quit."). Descriptions never restate the name, the role, or anything else the standard announcement already says (section 4).

## 2.3 Echoing input back

When the user typed the text or picked the item, they know what it was. Confirm the *operation*, not the operand: "Added." beats "Added Water the plants." when the user just pressed Enter on "Water the plants". Echo the operand only when the system transformed it (trimming, normalizing, resolving a name) or when the action happened away from the user's focus.

# 3. Name, Role, Value, State — Use the Slots

A widget's announcement is assembled from semantic slots: name, role, value (`ValueText`), state (`StateText`), position, description. Readers can order, filter, and re-verbosify slots; they cannot do anything with a fact that has been flattened into the name string.

The cardinal sin is **state as string decoration**:

```csharp
// Wrong: state baked into the name of a stateless widget.
var b = new Button(dialog, "[x] Longsword");

// Right: a stateful widget speaks its own state.
var box = new CheckBox(dialog, "Longsword", initiallyChecked);
```

The bracket version fails on every axis: the reader speaks punctuation ("left bracket, x, right bracket") or silently drops the state depending on user settings; the role is announced as "button", so nothing tells the user it toggles; toggling requires manually rewriting the name and usually a redundant announcement (section 2.1); and a future verbosity setting can never abbreviate what it cannot identify.

The same applies to values. A button named "Weapons: 6" that opens a picker has fused a value into a name; when the count changes, application code must remember to rewrite the string. A widget whose `ValueText` override *derives* the count from state is always current and speaks the value in the value slot — derived fields are functions of widget state, pulled at announcement time, never stored.

The rule generalizes: if you find yourself rewriting a name string to reflect changing state, the widget is the wrong kind or the fact is in the wrong slot.

# 4. Descriptions

`Description` is spoken after everything else, on focus. It has exactly one legitimate job: explaining an **esoterically labeled** widget — when the name is a term of art, a proper noun, or otherwise opaque, the description says what the thing actually does.

Everything else is misuse. Specifically:

- **No keys or actions.** Nonstandard keys and unexpected capabilities — a list where left and right arrows change an item's priority — belong in `KeyHelp` (section 8), which announces "with help" and reads on F1, not in a description the user hears in full on every visit. Named shortcuts belong in the shortcut mechanism itself; announcements speak the first one.
- **No expected behavior.** "Enter submits" on an entry box, "Space toggles" on a checkbox, "Enter activates" on a button — the role already promises these. Describing them tells the user nothing and costs them the listen.
- **No name or role paraphrase.** Section 2.2.
- **No essential information.** Descriptions are the first thing verbosity settings will suppress. If the widget is unusable without the fact, it belongs in the name or in a slot.

A useful check: the description should be skippable by an experienced user with zero loss. If skipping it loses a capability entirely, the capability is undiscoverable by design and needs a better home; if skipping it loses nothing because the rest of the announcement already said it, delete it.

# 5. Dialogs

A dialog's label is spoken when the dialog opens and as context on focus entry. Widgets inside it are heard *after* that label, so they must never repeat it.

```csharp
// Wrong: the user hears "Saved fighters" twice before reaching the list.
Dialog dialog = app.OpenDialog();
_ = new Label(dialog, "Saved fighters");
var list = new ListBox(dialog, "Saved fighters", items);

// Right: the dialog carries the title; the widget's name adds only what
// the title does not already say — or nothing, if there is nothing to add.
```

The general form of the rule: a container's name is inherited context for everything inside it. Name children *relative to* that context. Inside a "Select weapons for Alice" dialog, the list is not "Alice's weapon selection list"; the group named "Tasks" does not contain a "Task list" and a "New task box" if plain "To-do" and "New task" — or less — would do. Every word of a child's name should be a word the surrounding context has not already spoken.

The same reasoning covers instruction labels. A leading label reading "Space toggles each" (worse when it is not even true — buttons activate, they don't toggle) is a description-shaped fact in a label-shaped slot, spoken on every open. If the widgets are the right kind (section 7), their roles carry the interaction model and the instruction label can usually be deleted outright.

# 6. Announce Discipline

`Announce` is the escape hatch for facts with no widget: operation outcomes, background events, errors. Because it is unstructured, it should be the *last* tool considered, and its content held to the same no-duplication standard:

- Never announce what a property change on a focused widget just spoke (section 2.1).
- Never announce what focus movement is about to speak. Closing a dialog restores focus, and the restored widget announces itself; an `Announce("Returned to the menu.")` immediately followed by "Menu, list, …" says everything twice.
- State outcomes once, tersely, most-important-first: "Added. 5 fighters." The user can act on the first word; everything after it is optional listening.
- Never rely on interruption or urgency tiers to make an announcement land — structure the content so the front-loaded words suffice.

# 7. Choosing Widgets: Lists Versus Button Stacks

The choice between a list and a row of discrete widgets is the choice between two navigation costs. A `ListBox` is **one tab stop**; its items cost one arrow press each, with typeahead, and each speaks name-state-position. A stack of buttons costs **one tab stop per item**. That trade dictates the answer:

- **Many homogeneous items, especially with per-item state** → a list. A "choose your six weapons from forty" screen is a `ListBox<T>` whose items carry a selected flag exposed through their live `Text` (or a dedicated toggle-list widget); the user arrows through forty items, space toggles, and the item speaks its own state because `Text` is read live at announcement time — no refresh pass, no name rewriting.
- **Few heterogeneous commands** → buttons. "OK", "Cancel", "Reposition" are distinct actions with distinct consequences; a tab stop each is correct, and primary/cancel routing gives them Enter and Escape for free.

Forty buttons named `[x] Longsword` is the worst of both: forty tab stops, no typeahead, fake state (section 3), and a lying instruction label (section 5). This shape usually arrives by porting — a source platform where "menu of clickable text items" was the only primitive gets transliterated item-for-item into the closest clickable SRUI widget. Port the *task*, not the widget tree: ask what the user is choosing, then pick the SRUI widget whose semantics match the choice.

The same task-first reasoning covers the other classic mismatches: a two-state action is a `CheckBox`, not a button that rewrites its own name; a one-of-N choice is a `ListBox` or slider, not N buttons; bulk operations over a selection ("select all", "random 6") are fine as buttons *beside* the list, because they are genuine commands.

# 8. Keyboard Shortcuts and Discoverability

Shortcuts are discoverable through two channels:

- **The shortcut mechanism itself.** Widgets announce their first shortcut as part of the standard announcement; a shortcut registered with `AddShortcut` documents itself at zero authoring cost.
- **Key help.** `Widget.KeyHelp` is the home for nonstandard keys and actions — anything a user could not predict from the widget's name and role (a list whose left and right arrows set priority, a game widget's whole key layout). A widget with key help announces "with help", and F1 shows the text in a reviewable status dialog: read on demand, once, instead of recited on every focus visit.

Never put a key list in a `Description` — that is exactly the per-focus recitation `KeyHelp` exists to avoid — and never put a shortcut anywhere when the widget already announces it. Expected behavior (Enter on a button, typing in an edit box, arrows in a list) belongs in neither channel; the role already promises it.

# 9. Testing the Transcript

The transcript test (section 1) is automatable, and SRUI applications are expected to encode their spoken surface as tests: build the screen in a headless `SruiApp`, attach a recording `IReader`, push logical input with `HandleInput`, and assert the utterances (Srui.Net.Tests/SurfaceTests.cs holds the reference harness). Two assertions are worth writing for every screen:

- **The walk**: tab from the first widget to the last and assert the full sequence. Duplication is immediately visible as repeated substrings in adjacent utterances.
- **The action**: perform each state-changing operation and assert that it produces exactly one utterance, and that the utterance leads with the outcome.

A screen whose walk transcript reads well and whose actions speak once is, by construction, following everything above.
