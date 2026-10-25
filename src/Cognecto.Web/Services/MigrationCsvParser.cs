using System.Globalization;
using System.Text.RegularExpressions;
using Cognecto.Web.Models;
using CsvHelper;
using CsvHelper.Configuration;

namespace Cognecto.Web.Services;

public sealed record MigrationCsvResult(List<MigrationRecord> Records, List<string> Errors);

/// <summary>
/// Reads the migration-table CSV into records. Any invalid row fails the whole file,
/// so a bad upload never lands in the database half-saved.
/// </summary>
public partial class MigrationCsvParser
{
    public const string AcNoName = "AC No. & Name";
    public const string ExistingPartNo = "Existing Part No.";
    public const string ExistingPartName = "Existing Part Name";
    public const string ExistingSection = "Existing Section";
    public const string BreakupType = "Break-up Type";
    public const string ExistingElectors = "Existing Electors";
    public const string RationalizedPartNo = "Rationalized Part No.";
    public const string RationalizedPartName = "Rationalized Part Name";
    public const string RationalizedSection = "Rationalized Section";
    public const string RationalizedElectors = "Rationalized Electors";
    public const string Action = "Action";

    public static readonly string[] Columns =
    [
        AcNoName, ExistingPartNo, ExistingPartName, ExistingSection, BreakupType, ExistingElectors,
        RationalizedPartNo, RationalizedPartName, RationalizedSection, RationalizedElectors, Action,
    ];

    public MigrationCsvResult Parse(Stream stream)
    {
        var records = new List<MigrationRecord>();
        var errors = new List<string>();

        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null, // short rows read as blanks and are reported by validation
        });

        try
        {
            if (!csv.Read() || !csv.ReadHeader())
            {
                errors.Add("The file is empty.");
                return new(records, errors);
            }

            // Match headers loosely so "AC No & Name" or "ac_no_name" also work.
            var index = new Dictionary<string, int>();
            for (var i = 0; i < csv.HeaderRecord!.Length; i++)
                index.TryAdd(Normalize(csv.HeaderRecord[i]), i);

            var missing = Columns.Where(c => !index.ContainsKey(Normalize(c))).ToList();
            if (missing.Count > 0)
            {
                errors.Add($"Missing column(s): {string.Join(", ", missing)}. Download the sample CSV to see the expected format.");
                return new(records, errors);
            }

            while (csv.Read())
            {
                if (csv.Parser.Record!.All(string.IsNullOrWhiteSpace))
                    continue; // Excel often leaves trailing ",,,,," lines

                var row = new RowReader(csv, index);
                var record = new MigrationRecord
                {
                    RowNo = csv.Parser.Row,
                    AcNoName = row.Required(AcNoName),
                    ExistingPartNo = row.WholeNumber(ExistingPartNo),
                    ExistingPartName = row.Required(ExistingPartName),
                    ExistingSection = row.Required(ExistingSection),
                    BreakupType = row.Optional(BreakupType) is { Length: > 0 } breakup ? breakup : null,
                    ExistingElectors = row.CountOrRange(ExistingElectors),
                    RationalizedPartNo = row.Required(RationalizedPartNo),
                    RationalizedPartName = row.Required(RationalizedPartName),
                    RationalizedSection = row.Required(RationalizedSection),
                    RationalizedElectors = row.WholeNumber(RationalizedElectors),
                    Action = row.Required(Action),
                };

                if (row.Errors.Count > 0)
                    errors.Add($"Row {csv.Parser.Row}: {string.Join("; ", row.Errors)}");
                else
                    records.Add(record);
            }
        }
        catch (CsvHelperException)
        {
            errors.Add($"Row {csv.Parser.Row}: the line is not valid CSV (check for an unclosed quote).");
        }

        if (errors.Count == 0 && records.Count == 0)
            errors.Add("The file has a header row but no data rows.");

        return new(records, errors);
    }

    private static string Normalize(string header) =>
        new string(header.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // A count ("600") or, for an elector-wise break-up, a serial range ("1001-1600").
    [GeneratedRegex(@"^(\d{1,9})(?:\s*-\s*(\d{1,9}))?$")]
    private static partial Regex CountOrRangePattern();

    private sealed class RowReader(CsvReader csv, Dictionary<string, int> index)
    {
        public List<string> Errors { get; } = [];

        public string Optional(string column)
        {
            var value = csv.GetField(index[Normalize(column)])?.Trim() ?? "";
            if (!value.Contains('\0'))
                return value;
            // Postgres text cannot hold NUL; it shows up when a binary or UTF-16 file is renamed to .csv.
            Errors.Add($"{column} contains an invalid character (check the file is saved as CSV UTF-8)");
            return value.Replace("\0", "");
        }

        public string Required(string column)
        {
            var value = Optional(column);
            if (value.Length == 0)
                Errors.Add($"{column} is required");
            return value;
        }

        public int WholeNumber(string column)
        {
            var value = Required(column);
            if (value.Length == 0)
                return 0;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return number;
            Errors.Add($"{column} must be a whole number, got '{value}'");
            return 0;
        }

        public string CountOrRange(string column)
        {
            var value = Required(column);
            if (value.Length == 0)
                return value;

            var match = CountOrRangePattern().Match(value);
            if (!match.Success)
            {
                Errors.Add($"{column} must be a number like 600 or a range like 1001-1600, got '{value}'");
                return value;
            }
            if (!match.Groups[2].Success)
                return match.Groups[1].Value;

            var (from, to) = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
            if (from > to)
                Errors.Add($"{column} range '{value}' starts after it ends");
            return $"{from}-{to}";
        }
    }
}
