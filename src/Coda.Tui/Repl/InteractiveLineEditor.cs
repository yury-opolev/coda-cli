using System.Globalization;
using System.Text;
using Spectre.Console;

namespace Coda.Tui.Repl;

internal sealed class InteractiveLineEditor
{
    private const int ReservedSuggestionRows = 10;
    private readonly IAnsiConsole console;
    private readonly SlashCommandCompletion completion;
    private readonly StringBuilder input = new();
    private int cursorIndex;
    private int inputLeft;
    private int inputTop;
    private int reservedRows;

    public InteractiveLineEditor(IAnsiConsole console, SlashCommandRegistry commands)
    {
        this.console = console;
        this.completion = new SlashCommandCompletion(commands);
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        this.input.Clear();
        this.cursorIndex = 0;
        this.reservedRows = 0;
        this.inputLeft = Console.CursorLeft;
        this.inputTop = Console.CursorTop;
        this.completion.Update(string.Empty, 0);

        try
        {
            while (true)
            {
                var key = await ReadKeyAsync(this.console.Input, cancellationToken).ConfigureAwait(false);
                if (key is null)
                {
                    return null;
                }

                if (key.Value.Key == ConsoleKey.Enter)
                {
                    this.ClearSuggestions();
                    this.MoveCursorToInputEnd();
                    this.console.WriteLine();
                    return this.input.ToString();
                }

                if (key.Value.Key == ConsoleKey.Z &&
                    key.Value.Modifiers.HasFlag(ConsoleModifiers.Control) &&
                    this.input.Length == 0)
                {
                    this.ClearSuggestions();
                    return null;
                }

                if (this.HandleKey(key.Value))
                {
                    this.completion.Update(this.input.ToString(), this.cursorIndex);
                    this.Render();
                }
            }
        }
        finally
        {
            this.console.Cursor.Show(true);
        }
    }

    internal static async Task<ConsoleKeyInfo?> ReadKeyAsync(
        IAnsiConsoleInput input,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            return await input.ReadKeyAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private bool HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow when this.completion.IsVisible:
                this.completion.MoveSelection(-1);
                return true;

            case ConsoleKey.DownArrow when this.completion.IsVisible:
                this.completion.MoveSelection(1);
                return true;

            case ConsoleKey.Tab when this.completion.IsVisible:
                if (this.completion.Complete() is { } completed)
                {
                    var suffix = this.input.ToString(this.cursorIndex, this.input.Length - this.cursorIndex);
                    this.input.Clear();
                    this.input.Append(completed);
                    this.input.Append(suffix);
                    this.cursorIndex = completed.Length;
                }

                return true;

            case ConsoleKey.Escape when this.completion.IsVisible:
                this.completion.Dismiss();
                return true;

            case ConsoleKey.Backspace when this.cursorIndex > 0:
                var previousIndex = GetPreviousTextElementIndex(this.input.ToString(), this.cursorIndex);
                this.input.Remove(previousIndex, this.cursorIndex - previousIndex);
                this.cursorIndex = previousIndex;
                this.completion.Reactivate();
                return true;

            case ConsoleKey.Delete when this.cursorIndex < this.input.Length:
                var nextIndex = GetNextTextElementIndex(this.input.ToString(), this.cursorIndex);
                this.input.Remove(this.cursorIndex, nextIndex - this.cursorIndex);
                this.completion.Reactivate();
                return true;

            case ConsoleKey.LeftArrow when this.cursorIndex > 0:
                this.cursorIndex = GetPreviousTextElementIndex(this.input.ToString(), this.cursorIndex);
                this.completion.Reactivate();
                return true;

            case ConsoleKey.RightArrow when this.cursorIndex < this.input.Length:
                this.cursorIndex = GetNextTextElementIndex(this.input.ToString(), this.cursorIndex);
                this.completion.Reactivate();
                return true;

            case ConsoleKey.Home:
                this.cursorIndex = 0;
                this.completion.Reactivate();
                return true;

            case ConsoleKey.End:
                this.cursorIndex = this.input.Length;
                this.completion.Reactivate();
                return true;
        }

        if (!char.IsControl(key.KeyChar))
        {
            this.input.Insert(this.cursorIndex, key.KeyChar);
            this.cursorIndex++;
            this.completion.Reactivate();
            return true;
        }

        return false;
    }

    private void Render()
    {
        this.console.Cursor.Show(false);
        this.ClampAnchorToTerminal();
        this.ReserveSuggestionRows();

        var width = this.GetInputWidth();
        var (visible, cursorColumn) = BuildVisibleInput(this.input.ToString(), this.cursorIndex, width);

        this.console.Cursor.SetPosition(this.inputLeft, this.inputTop);
        this.console.Write(PadToCellWidth(visible, width));

        var availableRows = Math.Max(0, this.console.Profile.Out.Height - this.inputTop - 1);
        var rowsToRender = Math.Min(this.reservedRows, availableRows);
        for (var index = 0; index < rowsToRender; index++)
        {
            this.console.Cursor.SetPosition(0, this.inputTop + index + 1);
            var lineWidth = Math.Max(1, this.console.Profile.Out.Width - 1);
            var line = this.BuildSuggestionLine(index, lineWidth);
            this.console.Write(PadToCellWidth(line, lineWidth));
        }

        this.console.Cursor.SetPosition(Math.Min(this.inputLeft + cursorColumn, this.console.Profile.Out.Width - 1), this.inputTop);
        this.console.Cursor.Show(true);
    }

    private void ReserveSuggestionRows()
    {
        if (!this.completion.IsVisible || this.reservedRows > 0)
        {
            return;
        }

        var terminalHeight = Math.Max(1, this.console.Profile.Out.Height);
        var rows = Math.Min(ReservedSuggestionRows, Math.Max(0, terminalHeight - 1));
        if (rows == 0)
        {
            return;
        }

        var previousTop = Console.CursorTop;
        this.console.Profile.Out.Writer.Write(new string('\n', rows));
        this.console.Profile.Out.Writer.Flush();
        var currentTop = Console.CursorTop;
        var scrollCount = Math.Max(0, previousTop + rows - currentTop);
        this.inputTop = Math.Max(0, this.inputTop - scrollCount);
        this.reservedRows = rows;
        this.ClampAnchorToTerminal();
    }

    private string BuildSuggestionLine(int index, int width)
    {
        if (!this.completion.IsVisible || index >= this.completion.Suggestions.Count)
        {
            return string.Empty;
        }

        var command = this.completion.Suggestions[index];
        var marker = index == this.completion.SelectedIndex ? "› " : "  ";
        var line = $"{marker}{("/" + command.Name),-18} {command.Summary}";
        return TruncateToCellWidth(line, width);
    }

    private void MoveCursorToInputEnd()
    {
        this.ClampAnchorToTerminal();
        var (_, endColumn) = BuildVisibleInput(this.input.ToString(), this.input.Length, this.GetInputWidth());
        this.console.Cursor.SetPosition(Math.Min(this.inputLeft + endColumn, this.console.Profile.Out.Width - 1), this.inputTop);
    }

    private void ClearSuggestions()
    {
        this.ClampAnchorToTerminal();
        var rowsToClear = Math.Min(this.reservedRows, Math.Max(0, this.console.Profile.Out.Height - this.inputTop - 1));
        for (var index = 0; index < rowsToClear; index++)
        {
            this.console.Cursor.SetPosition(0, this.inputTop + index + 1);
            this.console.Write(new string(' ', Math.Max(1, this.console.Profile.Out.Width - 1)));
        }
    }

    private void ClampAnchorToTerminal()
    {
        var width = Math.Max(1, this.console.Profile.Out.Width);
        var height = Math.Max(1, this.console.Profile.Out.Height);
        this.inputLeft = Math.Clamp(this.inputLeft, 0, width - 1);
        this.inputTop = Math.Clamp(this.inputTop, 0, height - 1);
    }

    private int GetInputWidth() =>
        Math.Max(1, this.console.Profile.Out.Width - this.inputLeft - 1);

    private static (string Text, int CursorColumn) BuildVisibleInput(string input, int cursorIndex, int width)
    {
        var elements = GetTextElements(input);
        var cursorElementIndex = elements.FindIndex(element => element.Index >= cursorIndex);
        if (cursorElementIndex < 0)
        {
            cursorElementIndex = elements.Count;
        }

        var start = cursorElementIndex;
        var cursorWidth = 0;
        while (start > 0)
        {
            var elementWidth = elements[start - 1].Text.GetCellWidth();
            if (cursorWidth + elementWidth > width)
            {
                break;
            }

            cursorWidth += elementWidth;
            start--;
        }

        var builder = new StringBuilder();
        var usedWidth = 0;
        for (var index = start; index < elements.Count; index++)
        {
            var elementWidth = elements[index].Text.GetCellWidth();
            if (usedWidth + elementWidth > width)
            {
                break;
            }

            builder.Append(elements[index].Text);
            usedWidth += elementWidth;
        }

        return (builder.ToString(), cursorWidth);
    }

    private static string TruncateToCellWidth(string value, int width)
    {
        if (value.GetCellWidth() <= width)
        {
            return value;
        }

        if (width <= 1)
        {
            return GetTextElements(value)[0].Text;
        }

        var builder = new StringBuilder();
        var usedWidth = 0;
        foreach (var element in GetTextElements(value))
        {
            var elementWidth = element.Text.GetCellWidth();
            if (usedWidth + elementWidth + 1 > width)
            {
                break;
            }

            builder.Append(element.Text);
            usedWidth += elementWidth;
        }

        return builder.Append('…').ToString();
    }

    private static string PadToCellWidth(string value, int width) =>
        value + new string(' ', Math.Max(0, width - value.GetCellWidth()));

    private static int GetPreviousTextElementIndex(string value, int index) =>
        GetTextElements(value).Last(element => element.Index < index).Index;

    private static int GetNextTextElementIndex(string value, int index)
    {
        var next = GetTextElements(value).FirstOrDefault(element => element.Index > index);
        return next.Text is null ? value.Length : next.Index;
    }

    private static List<(int Index, string Text)> GetTextElements(string value)
    {
        var elements = new List<(int Index, string Text)>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            elements.Add((enumerator.ElementIndex, enumerator.GetTextElement()));
        }

        return elements;
    }
}
