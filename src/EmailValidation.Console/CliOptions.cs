namespace EmailValidation.ConsoleApp;

internal enum OutputFormat { Text, Json, Csv }

internal sealed record CliOptions(
    string Command,
    IReadOnlyList<string> Values,
    OutputFormat Format,
    string? OutputPath,
    string? Column,
    bool ColumnSpecified,
    bool Verbose,
    bool Live)
{
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0) return new("help", [], OutputFormat.Text, null, null, false, false, false);
        var values = new List<string>();
        var format = OutputFormat.Text;
        string? output = null;
        string? column = null;
        var columnSpecified = false;
        var verbose = false;
        var live = false;

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--format" when index + 1 < args.Length:
                    if (!Enum.TryParse<OutputFormat>(args[++index], true, out format))
                        throw new ArgumentException("--format must be text, json, or csv.");
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--column" when index + 1 < args.Length:
                    column = args[++index];
                    columnSpecified = true;
                    break;
                case "--verbose": verbose = true; break;
                case "--live": live = true; break;
                case "--help" or "-h": return new("help", [], format, output, column, columnSpecified, verbose, live);
                default:
                    if (args[index].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown option: {args[index]}");
                    values.Add(args[index]);
                    break;
            }
        }
        return new(args[0].ToLowerInvariant(), values, format, output, column, columnSpecified, verbose, live);
    }
}
