global using Terminal.Gui.App;
global using Terminal.Gui.Drawing;
global using Terminal.Gui.Drivers;
global using Terminal.Gui.Input;
global using Terminal.Gui.ViewBase;
global using Terminal.Gui.Views;

// Terminal.Gui.Input and Terminal.Gui.Drawing introduce CommandContext and Color
// types that collide with the existing Coda.Tui.Repl.CommandContext and
// Spectre.Console.Color used across the codebase. Pin the unqualified names to the
// pre-existing types so current code keeps resolving as before; Terminal.Gui's
// equivalents remain reachable via their fully-qualified names.
global using CommandContext = Coda.Tui.Repl.CommandContext;
global using Color = Spectre.Console.Color;
global using Padding = Spectre.Console.Padding;
