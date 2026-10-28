# NVDA Integration for SRUI

Notes on how NVDA works internally and the decisions taken so far for a long-term idea: an NVDA add-on that improves SRUI apps under NVDA. The add-on does not exist yet. NVDA's source is cloned at C:\nvda (the checkout is not part of this repository; it sits beside it on each machine that needs it). Line references are against the 2027.1 development branch as of 4 September 2026 and will drift.

## 1. How SRUI reaches NVDA today

Prism's NVDA backend speaks over NVDA's controller RPC endpoint (repos/srui/native/prism/idl/nvdaController.idl). The endpoint offers testIfRunning, speakText, cancelSpeech, brailleMessage, getProcessId, isSpeaking, and speakSsml with a mark-reached callback. NVDA serves these in source/NVDAHelper/__init__.py. The channel is one-directional: the app calls NVDA. NVDA has no way to ask an SRUI app anything and no way to hand it a keypress, which is the gap the add-on fills.

speakSsml marks are not a substitute for an add-on. They depend on the synthesizer reporting index positions, and synthesizers get this wrong: some lack the granularity, some misreport whether they have finished at all. SSML also does not give fine enough control over speech for what we want.

brailleMessage (NVDA side: nvdaController_brailleMessage, then braille.handler.message in source/braille/brailleHandler.py) shows a transient message, not main-buffer content. It is dropped when the user has live-region reporting off or braille messages disabled, it times out after the configured message timeout unless messages are set to show indefinitely, and it is dismissed by a routing key or by the next text written after a keypress. Anything that treats braille as a primary surface has to account for this (section 6).

## 2. NVDA never holds a whole document

NVDA's text model is TextInfo (source/textInfos/__init__.py): a range over a text object, moved and expanded by unit (character, word, line, sentence, paragraph, story). Every reading command works by moving a range one chunk at a time and fetching the text of that range alone. Nothing in the core requires the full text.

Whether the full text gets fetched depends on the TextInfo implementation behind the object:

* OffsetsTextInfo (source/textInfos/offsets.py) is the offset-based base class. Its default line and text-range lookups call _getStoryText, which is the whole document. A subclass that overrides _getTextRange, _getLineOffsets, _getWordOffsets, _getCaretOffset, _getSelectionOffsets and _getStoryLength never triggers the whole-document path. Scintilla (source/NVDAObjects/window/scintilla.py) does exactly this over window messages and cross-process memory, and Notepad++ users edit large files through it.
* Plain Win32 edit controls fetch the whole text with WM_GETTEXT. Rich edit uses EM_GETTEXTRANGE. The SDL window is neither, so neither path applies to us.
* UIA text (source/NVDAObjects/UIA/__init__.py) is range-based too, for the record; we do not use UIA (section 3.1).

Conclusion: a rope holding gigabytes is compatible with NVDA as long as the object NVDA sees answers range queries and never has to hand over the story. An add-on of our own satisfies that by construction, and in the relay shape (section 3.3) NVDA never sees the text at all.

## 3. How objects get into NVDA's tree

NVDA's "accessibility tree" is not a data structure NVDA owns. It is a set of NVDAObject instances created lazily, on demand, from platform APIs (MSAA, IAccessible2, UIA, plain HWNDs). Nothing is inserted into it; an object exists when NVDA asks the platform about a focus event or a navigation step and wraps the answer. An application influences what NVDA wraps either by implementing a platform accessibility API, or by shipping NVDA-side code that knows the application's own protocol.

### 3.1 Platform accessibility APIs are excluded

We do not build on UI Automation. We have measured it: a UIA provider adds 50 ms or more of latency on top of whatever the app spends, and that figure has not moved in years. It is unreliable in practice, and NVDA does not consume it the way a provider author expects, because NVDA has to reinterpret UIA traffic to cope with the many applications that misuse the protocol. The same reasoning applies to MSAA and IAccessible2 for our purposes: they route our semantics through a general-purpose translation layer whose output we do not control. SRUI's speech is already ours; a platform provider would put a slower, less predictable renderer between the user and it.

### 3.2 The add-on wraps the window itself

An add-on ships a globalPlugin (an appModule is chosen by executable name, which does not scale across SRUI apps). It implements chooseNVDAObjectOverlayClasses (source/NVDAObjects/__init__.py, lines 117 to 131) to attach its own NVDAObject subclass to the SDL window, keyed on window class "SDL_app" and the window property described in section 4.2. NVDA's focus tracking is platform-driven: the SDL window produces one focus event when activated and nothing for SRUI's internal focus moves, so anything NVDA is to know about widgets and text has to come from the app over our own channel. The overlay class swallows the platform focus event, so NVDA does not announce the window title, and NVDA's sleep mode (section 5) stays off so that the add-on's own scripts run. This is what NVDA's own Scintilla support is: NVDA-side knowledge of an app's protocol, no platform accessibility involved.

### 3.3 Two shapes the add-on can take

The channel (section 4) carries notifications from the app and requests from the add-on. What travels over it decides how much NVDA does:

* **Relay.** NVDA forwards gestures it alone can see (NVDA-modifier combos, numpad review keys, braille display keys such as panning) to the app as commands, and the app answers with pre-formatted speech and braille, or speaks through Prism as it already does. NVDA renders nothing; SRUI's SpeechRenderer stays the single authority on wording. The add-on is a keyboard and display bridge with a gesture map users can rebind in NVDA's input gestures dialog.
* **Full objects.** The add-on builds NVDAObject instances from WidgetInfo payloads, synthesizes NVDA focus on them, and supplies a TextInfo whose range queries go to the app. NVDA's own review cursor, object navigation, say-all and braille then work on our content with NVDA's settings. The add-on must then suppress NVDA's focus speech (a no-op focus report on the virtual objects) or SRUI must mute its speech reader while an add-on is connected, or both readers speak.

We lean to the relay. The two are not exclusive; a relay can grow a TextInfo later if an NVDA feature turns out to need one.

## 4. The channel

### 4.1 A local socket, the app as server

Each SRUI app listens on a loopback TCP socket. The app is the server so that several consumers can attach at once, each requesting what it needs: the NVDA add-on, JAWS scripts later, other add-ons. Sockets are the one transport that survives a future Orca backend, which Prism already carries. The listener binds 127.0.0.1 only; any local process can connect, which is no worse than what screen readers already scrape, but nothing off the machine may.

### 4.2 Discovery and identification are one problem

The app publishes its port with SetProp on the SDL window handle (SDL3 exposes the HWND through its window properties API; SRUI's User32 interop file is where SetProp belongs). The add-on's overlay hook checks the window class is "SDL_app", then reads the property with GetProp through ctypes, which works across processes in microseconds and returns zero for every other SDL program. The hello message on connect carries a protocol version.

### 4.3 Framing

JSON-RPC 2.0 over newline-delimited JSON. On the C# side the library is StreamJsonRpc (Microsoft; the plumbing under Visual Studio's language servers): typed request and notification interfaces over any stream, one JsonRpc instance per connected client, ids, errors and cancellation handled. On the NVDA side, Python 3.13's json and socket modules cover a client in about a hundred lines; add-ons vendor pure Python freely.

The wire needs stable widget ids. SRUI deliberately has no handles, so the server keeps a weak table from widget object to integer id. WidgetInfo snapshots are already the shape of a focus notification.

### 4.4 Threading

NVDA calls into add-on code synchronously on its main thread under a watchdog, so requests from the add-on block with a short timeout (on the order of one to two hundred milliseconds): a stuck app degrades to a missing answer, never a frozen NVDA. Notifications arrive on a reader thread in the add-on and are dispatched to NVDA's main thread through its queue.

On the app side the one-thread rule holds. The listener thread touches no widget: it pushes an SDL user event to wake the loop, the app thread answers at drain time, and the listener writes the reply. The rope appears persistent (immutable leaf and branch nodes, path-copying edits, a single root field), so text-range reads could later be served off-thread from a root snapshot; caret, selection and line geometry are app-thread state regardless, so everything is answered on the app thread first and the snapshot read is kept as a latency lever. Before relying on it, confirm that rebalancing never mutates existing nodes.

## 5. Silencing NVDA's automatic announcements

Every appModule has a sleepMode flag (source/appModuleHandler.py, line 494). When it is true:

* executeEvent (source/eventHandler.py, lines 333 to 358) still tracks focus so NVDA knows where it is, but runs no event handlers. Focus, foreground, name change, value change, typed character and caret events all go silent.
* executeGesture (source/inputCore.py, lines 566 to 569) refuses every NVDA script except those marked allowInSleepMode, before the point where a gesture would cancel speech. NVDA therefore does not interrupt the app's controller speech on keypress, and passes keys through to the app.
* The controller endpoint keeps working. Only the stricter SLEEP_FULL value, which NVDA reserves for the secure desktop, refuses controller calls.

Users can toggle it themselves with NVDA+shift+s, and NVDA describes the flag as being for self-voicing applications. Setting it is the zero-effort form of silence, but it also disables the add-on's own scripts unless each is marked allowInSleepMode. The add-on therefore leaves sleep mode off and swallows events in its overlay class instead (section 3.2), which gives the same silence with the scripts intact.

## 6. Braille

Braille output is a mixed payload: text sent as text, and pictorial or width-dependent marker symbols sent directly as dot patterns. It goes out through Prism's braille function, as SRUI already does, so the add-on does not carry braille content.

Translation stays in NVDA. We considered translating in the app and concluded the safe bet is a hop through the add-on to NVDA's running liblouis (source/louisHelper.py), with the user's own table, so the app learns how many cells a line occupies. That is what lets the app decide what to do on a panning key and use the display fully. NVDA's own wrapping (source/braille/buffers.py, _set_windowEndPos) pads by placing a space at the start of the following line rather than the end of the current one, so the app pre-fits each line to the display width and NVDA's wrapping never runs on our output. Panning keys reach NVDA alone and are forwarded as commands (section 3.3).

The transient nature of brailleMessage (section 1) is not a constraint, because the add-on can bypass it. NVDA's braille buffer is a list of Region objects (source/braille/regions/base.py): the buffer calls each region's update and expects rawText or the cells themselves in brailleCells, plus cursor and selection positions. A region subclass that sets brailleCells directly is displayed as-is, without translation, and stays until replaced. The handler's _doNewObject (source/braille/brailleHandler.py) swaps the main buffer's regions for the ones it is given, which is how NVDA itself presents a newly focused object and how an add-on presents ours; it is a private method by naming, so NVDA upgrades can move it, and the add-on's braille code should keep this in one place. Whether a line goes out through brailleMessage or through a region is therefore a per-message choice: status text can stay a message, and the editor line can be a region that holds.

Braille input (Perkins-style entry) is out of scope. SRUI is designed around QWERTY keyboards at a fundamental level, and we are not taking on another input method. A Mantis Q40 is available for testing.

## 7. Order of work

1. Global plugin, GetProp identification, the swallowed window focus event, and a hello over the socket. This alone is the silence feature with a connection test built in.
2. Gesture forwarding and the notification stream: NVDA-only keys and display keys reach the app, and the app's speech and braille replies flow back.
3. The braille translation hop and pre-fitted lines with panning.

## 8. Open questions

* Which NVDA gestures the relay forwards by default, and how the app's command vocabulary is versioned alongside the protocol.
* Whether any NVDA feature will justify growing the relay into the full-objects shape (section 3.3).
