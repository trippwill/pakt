using System.Buffers;
using System.Text;
using System.Text.Json;

using Pakt;

using Spectre.Console;

var fileArgument = new System.CommandLine.Argument<FileInfo>("file")
{
    Description = "Path to a .pakt file",
};

var formatOption = new System.CommandLine.Option<string>("--format")
{
    Description = "Output format (text or json)",
    DefaultValueFactory = _ => "text",
};

// ── Parse command ──

var parseCommand = new System.CommandLine.Command("parse", "Parse a .pakt file and print the token stream")
{
    fileArgument,
    formatOption,
};

parseCommand.SetAction(parseResult =>
{
    var file = parseResult.GetValue(fileArgument)!;
    if (!file.Exists)
    {
        AnsiConsole.MarkupLine($"[red]File not found:[/] {file.FullName}");
        return 1;
    }

    var format = parseResult.GetValue(formatOption) ?? "text";
    byte[] data = File.ReadAllBytes(file.FullName);
    var seq = new ReadOnlySequence<byte>(data);
    var reader = new PaktReader(seq, isFinalBlock: true);

    if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
    {
        using var jsonWriter = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
        jsonWriter.WriteStartArray();
        while (reader.Read())
        {
            string value = reader.ValueSequence.Length > 0
                ? Encoding.UTF8.GetString(reader.ValueSequence)
                : "";
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("type", reader.TokenType.ToString());
            jsonWriter.WriteString("value", value);
            jsonWriter.WriteNumber("offset", reader.TokenStartIndex);
            jsonWriter.WriteNumber("depth", reader.CurrentDepth);
            jsonWriter.WriteEndObject();
        }
        jsonWriter.WriteEndArray();
        jsonWriter.Flush();
        Console.WriteLine();
    }
    else
    {
        while (reader.Read())
        {
            string value = reader.ValueSequence.Length > 0
                ? Encoding.UTF8.GetString(reader.ValueSequence)
                : "";
            string depth = reader.CurrentDepth > 0 ? $"  [{reader.CurrentDepth}]" : "";
            Console.WriteLine($"{reader.TokenStartIndex,6} {reader.TokenType,-25} {value}{depth}");
        }
    }

    return 0;
});

// ── Validate command ──

var validateCommand = new System.CommandLine.Command("validate", "Validate a .pakt file against its type annotations")
{
    fileArgument,
};

validateCommand.SetAction(parseResult =>
{
    var file = parseResult.GetValue(fileArgument)!;
    if (!file.Exists)
    {
        AnsiConsole.MarkupLine($"[red]File not found:[/] {file.FullName}");
        return 1;
    }

    byte[] data = File.ReadAllBytes(file.FullName);
    int tokenCount = 0;

    try
    {
        var reader = new PaktValidatingReader(data);
        while (reader.Read())
            tokenCount++;

        AnsiConsole.MarkupLine($"[green]✓[/] {file.Name} — valid ({tokenCount} tokens)");
        return 0;
    }
    catch (PaktParseException ex)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {file.Name}");
        AnsiConsole.MarkupLine($"  [red]{PaktParseException.GetErrorIdentifier((PaktErrorCode)ex.Code)}[/] at offset {ex.Position.Offset} (line {ex.Position.Line}, col {ex.Position.Column})");
        AnsiConsole.MarkupLine($"  {EscapeMarkup(ex.Message)}");
        return 1;
    }
});

// ── Root command ──

var rootCommand = new System.CommandLine.RootCommand("PAKT CLI — typed data interchange format")
{
    parseCommand,
    validateCommand,
};

return await rootCommand.Parse(args).InvokeAsync();

static string EscapeMarkup(string s) =>
    s.Replace("[", "[[").Replace("]", "]]");