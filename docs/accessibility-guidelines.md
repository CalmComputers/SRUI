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

Every fact should have exactly one home, and it should be spoken from that home exactly once per user action. The framework enforces this on its side by construction: nothing a widget's state does is spoken as it happens. At the end of each tick the framework reads the widget the user ended on, as the difference between what they last heard and what is now under the cursor — the whole widget when focus arrived, the whole item when the cursor landed on another one, otherwise only the fields that changed (docs/architecture.md, section 7.2). Program code neither checks whether a widget is focused nor orders a focus move against a state change; both are the framework's. Application code breaks the rule in three recurring ways.

## 2.1 Announcing on top of a widget change

A field that changes under the cursor is read at the tick end. Following it with `Announce` of the same fact produces double output:

```csharp
// Wrong: the checked field changed under the cursor; the tick end reads it.
box.Checked = true;
app.Announce("Fireball selected.");   // second utterance for the same toggle
```

`Announce` is for facts that no field carries — an operation's outcome, a count, an error. If a field change already tells the story, the announcement is noise; if you want different wording than the field's reading, keep the field out of that tick's reading with `Suppress` and announce once. That is the only time `Suppress` is right: when your own words replace the framework's, never to hide a change the user should hear.

## 2.2 Restating the label in the description or prompt

A `Description` that begins by paraphrasing the `Name` makes the widget introduce itself twice ("Quit button. Press again to quit."). Descriptions never restate the name, the role, or anything else the standard announcement already says (section 4).

## 2.3 Confirming what focus is about to show

An action the user initiated has a result, and the result is usually a widget: a new list item, a changed value, a closed dialog. If focus lands on that result, the landing *is* the confirmation. A user who adds a task and finds themselves on "Water the plants, 4 of 4" knows the add succeeded, knows the count, and knows what was added; an `Announce("Added.")` on top of that is a third statement of a fact the list already made. Route focus to the result and say nothing.

In SRUI terms: add the item, move the cursor to it, and focus the list; in whatever order, in the same tick. The tick end reads the landing once — the list with its new item if focus arrived, the item alone if the user was already in the list — and nothing else. An announcement attributed to a widget speaks only if that widget is where focus settled, so an add box that says "Added." and then focuses the list is heard as the list, not as both; an announcement from the app speaks regardless, so make the confirmation the box's own (`Announce` from the widget) when it should yield to a landing.

A spoken confirmation is correct in exactly two situations:

- **The action was not user-initiated.** A background sync finishing, a message arriving, a file appearing: nothing moved focus, so nothing else will speak.
- **No widget shows the outcome.** The classic case is "Copied." — the clipboard has no widget to land on. Others: the add box stays focused and empty for the next entry; the result lives on another screen; the change is to something focus cannot visit.

Failure is a third case, and it is not optional. When an add fails, focus does not land on a new item, but the user cannot hear an absence quickly, so the error is always announced (section 6).

When a confirmation is spoken, confirm the *operation*, not the operand. The user typed the text or picked the item and knows what it was: "Added." beats "Added Water the plants." Echo the operand only when the system transformed it (trimming, normalizing, resolving a name) and focus will not show the transformed form.

# 3. Name, Role, Value, State — Use the Fields

A widget's reading is assembled from typed fields: name, role, value, checked, position, the states, description, and whatever a widget declares of its own (`[Field]` properties — docs/architecture.md, section 4.2). Readers order, filter, translate, and re-verbosify fields; they cannot do anything with a fact that has been flattened into the name string.

The cardinal sin is **state as string decoration**:

```csharp
// Wrong: state baked into the name of a stateless widget.
var b = new Button(dialog, "[x] Longsword");

// Right: a stateful widget speaks its own state.
var box = new CheckBox(dialog, "Longsword", initiallyChecked);
```

The bracket version fails on every axis: the reader speaks punctuation ("left bracket, x, right bracket") or silently drops the state depending on user settings; the role is announced as "button", so nothing tells the user it toggles; toggling requires manually rewriting the name and usually a redundant announcement (section 2.1); and a future verbosity setting can never abbreviate what it cannot identify.

The same applies to values. A button named "Weapons: 6" that opens a picker has fused a value into a name; when the count changes, application code must remember to rewrite the string. A widget with a `[Field] public int Weapons => ...` derives the count from state and reads it as a field of its own — computed fields are functions of widget state, read at the tick end, never stored — and a field bound to the model (`Bind`) needs no code at all when the model moves. A fact that is not a string (a count, a flag, a number with a unit) should be a field of that type, so a reader can say it its own way.

The rule generalizes: if you find yourself rewriting a name string to reflect changing state, the widget is the wrong kind or the fact is in the wrong field.

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

- Never announce what a field change under the cursor will read (section 2.1).
- Never announce what focus movement is about to read. Closing a dialog restores focus, and the restore reads on its own — the full reading by default, less under a trimmed restore verbosity; an `Announce("Returned to the menu.")` narrates a transition the user already made.
- Announce an outcome only when focus will not show it (section 2.3). If the operation ends with focus on its result, the focus reading is the announcement. Announcements always speak before the tick's state, whatever order the handler emitted them in, so the natural handler shape — do the work, announce, move focus — reads as outcome then landing.
- When an outcome is announced, state it once, tersely, most-important-first: "Added. 5 fighters." The user can act on the first word; everything after it is optional listening.
- Never rely on interruption or urgency tiers to make an announcement land — structure the content so the front-loaded words suffice.

# 7. Choosing Widgets: Lists Versus Button Stacks

The choice between a list and a row of discrete widgets is the choice between two navigation costs. A `ListBox` is **one tab stop**; its items cost one arrow press each, with typeahead, and each speaks name-state-position. A stack of buttons costs **one tab stop per item**. That trade dictates the answer:

- **Many homogeneous items, especially with per-item state** → a list. A "choose your six weapons from forty" screen is a multi-select `ListBox<T>` whose items carry their own `Checked` field; the user arrows through forty items, space toggles, and the item speaks its own state because its fields are read at the tick end — no refresh pass, no name rewriting. Items bound to the model (`BindItems`) need no list calls at all.
- **Few heterogeneous commands** → buttons. "OK", "Cancel", "Reposition" are distinct actions with distinct consequences; a tab stop each is correct, and primary/cancel routing gives them Enter and Escape for free.

Forty buttons named `[x] Longsword` is the worst of both: forty tab stops, no typeahead, fake state (section 3), and a lying instruction label (section 5). This shape usually arrives by porting — a source platform where "menu of clickable text items" was the only primitive gets transliterated item-for-item into the closest clickable SRUI widget. Port the *task*, not the widget tree: ask what the user is choosing, then pick the SRUI widget whose semantics match the choice.

The same task-first reasoning covers the other classic mismatches: a two-state action is a `CheckBox`, not a button that rewrites its own name; a one-of-N choice is a `ListBox` or slider, not N buttons; bulk operations over a selection ("select all", "random 6") are fine as buttons *beside* the list, because they are genuine commands.

# 8. Keyboard Shortcuts and Discoverability

Shortcuts are discoverable through two channels:

- **The shortcut mechanism itself.** Widgets announce their first shortcut as part of the standard announcement; a shortcut registered with `AddShortcut` documents itself at zero authoring cost.
- **Key help.** `Widget.KeyHelp` is the home for nonstandard keys and actions — anything a user could not predict from the widget's name and role (a list whose left and right arrows set priority, a game widget's whole key layout). A widget with key help announces "with help", and F1 shows the text in a reviewable status dialog: read on demand, once, instead of recited on every focus visit. When help is ubiquitous — an app whose every list carries it — the announced state itself becomes noise; `Widget.AnnounceHelp = false` drops the spoken "with help" while keeping F1 and its reservation.

Never put a key list in a `Description` — that is exactly the per-focus recitation `KeyHelp` exists to avoid — and never put a shortcut anywhere when the widget already announces it. Expected behavior (Enter on a button, typing in an edit box, arrows in a list) belongs in neither channel; the role already promises it.

These channels cover how bindings are *found*; choosing which keys to bind is its own discipline — see docs/shortcut-geometry.md.

# 9. Testing the Transcript

The transcript test (section 1) is automatable, and SRUI applications are expected to encode their spoken surface as tests: build the screen in `Srui.Testing`'s `TestApp`, push input, and `Expect` the utterances (docs/architecture.md, section 12). Two assertions are worth writing for every screen:

- **The walk**: tab from the first widget to the last and assert the full sequence. Duplication is immediately visible as repeated substrings in adjacent utterances.
- **The action**: perform each state-changing operation and assert that it produces exactly one utterance, and that the utterance leads with the outcome. When the operation ends with focus on its result, that one utterance is the focus reading of the result, and the test asserts that nothing else was spoken. The framework makes this the default: a tick reads once, and its state utterance is one composed reading, so a second utterance in a step is always an announcement the test should question.

A screen whose walk transcript reads well and whose actions speak once is, by construction, following everything above.
