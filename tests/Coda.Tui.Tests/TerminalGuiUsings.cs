global using Terminal.Gui.App;
global using Terminal.Gui.Drawing;
global using Terminal.Gui.Drivers;
global using Terminal.Gui.Input;
global using Terminal.Gui.Testing;
global using Terminal.Gui.ViewBase;
global using Terminal.Gui.Views;

// Terminal.Gui.Input introduces a CommandContext type that collides with the
// existing Coda.Tui.Repl.CommandContext used throughout the test suite. Pin the
// unqualified name to the pre-existing type so current tests keep resolving as
// before; Terminal.Gui's CommandContext remains reachable via its full name.
global using CommandContext = Coda.Tui.Repl.CommandContext;
global using Timeout = System.Threading.Timeout;
