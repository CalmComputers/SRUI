// SRUI demo, object-oriented edition: the same stack as SruiDemo (SDL
// window in, Prism speech out) organized as classes. Where the gallery
// wires behavior onto built-ins with lambdas, this app demonstrates the
// subclassing path: built-ins extended by overriding their On* methods
// and ReservesKey (TaskListBox, HistoryEditBox, ConfirmButton),
// state-bearing list items that compose their own spoken lines
// (TaskItem), silent state mutation paired with owned announcements
// (SetTextSilently in the entry box), composite panels as Group
// subclasses, and an application shell class instead of a top-level
// script.
//
// The app is a small to-do list with two views, Tasks and Summary. On
// the task list: Space toggles done, Delete removes, Shift+Up/Down
// reorders, Left/Right sets priority; arrows, Home/End, and type-ahead
// are stock. In the entry box: Enter adds the task, Up/Down recall
// earlier entries. Alt+V jumps to the views, Ctrl+N to the entry box;
// Escape or Ctrl+Q quits — twice, Quit being a ConfirmButton.

using Srui;
using SruiTasks;

using var app = new SruiApp("SRUI Tasks");
Console.WriteLine($"speech backend: {app.Voice?.BackendName}");

_ = new TaskApp(app);
app.Run();
