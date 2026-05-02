using System.CommandLine;

var fileArgument = new Argument<FileInfo>("file")
{
    Description = "Path to a .pakt file",
};

var parseCommand = new Command("parse", "Parse a .pakt file and print the event stream")
{
    fileArgument,
};

parseCommand.SetAction((ParseResult parseResult) =>
{
    var file = parseResult.GetValue(fileArgument)!;
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return 1;
    }

    Console.WriteLine($"parse: not implemented ({file.Name})");
    return 0;
});

var validateCommand = new Command("validate", "Validate a .pakt file against its type annotations")
{
    fileArgument,
};

validateCommand.SetAction((ParseResult parseResult) =>
{
    var file = parseResult.GetValue(fileArgument)!;
    if (!file.Exists)
    {
        Console.Error.WriteLine($"File not found: {file.FullName}");
        return 1;
    }

    Console.WriteLine($"validate: not implemented ({file.Name})");
    return 0;
});

var rootCommand = new RootCommand("PAKT CLI — typed data interchange format")
{
    parseCommand,
    validateCommand,
};

return rootCommand.Parse(args).Invoke();