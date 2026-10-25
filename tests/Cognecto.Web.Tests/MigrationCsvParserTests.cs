using System.Text;
using Cognecto.Web.Services;

namespace Cognecto.Web.Tests;

public class MigrationCsvParserTests
{
    private const string Header =
        "AC No. & Name,Existing Part No.,Existing Part Name,Existing Section,Break-up Type,Existing Electors," +
        "Rationalized Part No.,Rationalized Part Name,Rationalized Section,Rationalized Electors,Action";

    private const string ValidRow = "201-abc,1,abc,1-abc,,600,1,abc,1-abc,600,No action";

    private static readonly MigrationCsvParser Parser = new();

    private static MigrationCsvResult Parse(string csv, Encoding? encoding = null) =>
        Parser.Parse(new MemoryStream((encoding ?? new UTF8Encoding(false)).GetBytes(csv)));

    private static MigrationCsvResult ParseLines(params string[] lines) => Parse(string.Join("\n", lines) + "\n");

    [Fact]
    public void Sample_file_parses_every_row()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "migration_sample.csv"));

        var result = Parser.Parse(stream);

        Assert.Empty(result.Errors);
        Assert.Equal(18, result.Records.Count);
        Assert.Equal(2, result.Records.Select(r => r.AcNoName).Distinct().Count());
    }

    [Fact]
    public void Maps_every_column_onto_the_record()
    {
        var result = ParseLines(Header,
            "201-abc,2,efg,1-efg,Elector,1001-1600,2N1,efg,1-hij,600,\"Creation of New PS, New Section, Elector Movement request\"");

        Assert.Empty(result.Errors);
        var r = Assert.Single(result.Records);
        Assert.Equal(2, r.RowNo);
        Assert.Equal("201-abc", r.AcNoName);
        Assert.Equal(2, r.ExistingPartNo);
        Assert.Equal("efg", r.ExistingPartName);
        Assert.Equal("1-efg", r.ExistingSection);
        Assert.Equal("Elector", r.BreakupType);
        Assert.Equal("1001-1600", r.ExistingElectors);
        Assert.Equal("2N1", r.RationalizedPartNo);
        Assert.Equal("efg", r.RationalizedPartName);
        Assert.Equal("1-hij", r.RationalizedSection);
        Assert.Equal(600, r.RationalizedElectors);
        Assert.Equal("Creation of New PS, New Section, Elector Movement request", r.Action);
    }

    [Fact]
    public void Leaves_uploader_and_district_for_the_controller_to_fill()
    {
        var r = Assert.Single(ParseLines(Header, ValidRow).Records);

        Assert.Equal("", r.UploadedBy);
        Assert.Equal("", r.DistrictId);
    }

    [Fact]
    public void Blank_breakup_type_is_stored_as_null()
    {
        Assert.Null(Assert.Single(ParseLines(Header, ValidRow).Records).BreakupType);
    }

    [Fact]
    public void Trims_whitespace_around_values()
    {
        var r = Assert.Single(ParseLines(Header, " 201-abc , 1 ,abc,1-abc, ,600,1,abc,1-abc, 600 , No action ").Records);

        Assert.Equal("201-abc", r.AcNoName);
        Assert.Equal(1, r.ExistingPartNo);
        Assert.Null(r.BreakupType);
        Assert.Equal(600, r.RationalizedElectors);
        Assert.Equal("No action", r.Action);
    }

    [Theory]
    [InlineData("ac_no_name,existing_part_no,existing_part_name,existing_section,breakup_type,existing_electors,rationalized_part_no,rationalized_part_name,rationalized_section,rationalized_electors,action")]
    [InlineData("AC NO & NAME,Existing Part No,Existing Part Name,Existing Section,Breakup Type,Existing Electors,Rationalized Part No,Rationalized Part Name,Rationalized Section,Rationalized Electors,ACTION")]
    public void Matches_headers_ignoring_case_and_punctuation(string header)
    {
        var result = ParseLines(header, ValidRow);

        Assert.Empty(result.Errors);
        Assert.Single(result.Records);
    }

    [Fact]
    public void Reads_columns_by_name_not_position()
    {
        var result = ParseLines(
            "Action,Rationalized Electors,Rationalized Section,Rationalized Part Name,Rationalized Part No.,Existing Electors," +
            "Break-up Type,Existing Section,Existing Part Name,Existing Part No.,AC No. & Name,Remarks",
            "No action,600,1-abc,abc,1,600,,1-abc,abc,1,201-abc,ignored extra column");

        Assert.Empty(result.Errors);
        var r = Assert.Single(result.Records);
        Assert.Equal("201-abc", r.AcNoName);
        Assert.Equal("No action", r.Action);
        Assert.Equal(600, r.RationalizedElectors);
    }

    [Fact]
    public void Reads_utf8_with_byte_order_mark_as_saved_by_excel()
    {
        var result = Parse(Header + "\r\n" + ValidRow + "\r\n", new UTF8Encoding(true));

        Assert.Empty(result.Errors);
        Assert.Single(result.Records);
    }

    [Fact]
    public void Keeps_non_ascii_text()
    {
        var r = Assert.Single(ParseLines(Header, "201-अबक,1,रामपुर,1-रामपुर,,600,1,रामपुर,1-रामपुर,600,No action").Records);

        Assert.Equal("201-अबक", r.AcNoName);
        Assert.Equal("रामपुर", r.ExistingPartName);
    }

    [Fact]
    public void Skips_blank_and_comma_only_lines()
    {
        var result = ParseLines(Header, ValidRow, "", ",,,,,,,,,,", ValidRow, ",,,,,,,,,,");

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public void Empty_file_is_rejected()
    {
        Assert.Equal(["The file is empty."], Parse("").Errors);
    }

    [Fact]
    public void Header_without_rows_is_rejected()
    {
        Assert.Equal(["The file has a header row but no data rows."], ParseLines(Header, ",,,,,,,,,,").Errors);
    }

    [Fact]
    public void Missing_columns_are_named()
    {
        var result = ParseLines(Header.Replace(",Break-up Type", "").Replace(",Action", ""), "x");

        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Missing column(s): Break-up Type, Action.", error);
        Assert.Empty(result.Records);
    }

    [Fact]
    public void Required_fields_are_reported_by_row_and_column()
    {
        var result = ParseLines(Header, ",1,,1-abc,,600,1,abc,1-abc,600,");

        var error = Assert.Single(result.Errors);
        Assert.Equal("Row 2: AC No. & Name is required; Existing Part Name is required; Action is required", error);
        Assert.Empty(result.Records);
    }

    [Fact]
    public void Short_row_reports_its_missing_fields()
    {
        var error = Assert.Single(ParseLines(Header, "201-abc,1,abc").Errors);

        Assert.StartsWith("Row 2: Existing Section is required", error);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1,000")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public void Part_no_and_electors_must_be_whole_numbers(string value)
    {
        var result = ParseLines(Header, $"201-abc,\"{value}\",abc,1-abc,,600,1,abc,1-abc,\"{value}\",No action");

        var error = Assert.Single(result.Errors);
        Assert.Contains($"Existing Part No. must be a whole number, got '{value}'", error);
        Assert.Contains($"Rationalized Electors must be a whole number, got '{value}'", error);
    }

    [Theory]
    [InlineData("600", "600")]
    [InlineData("0600", "0600")]
    [InlineData("1001-1600", "1001-1600")]
    [InlineData("1001 - 1600", "1001-1600")]
    [InlineData("0001-0100", "1-100")]
    [InlineData("5-5", "5-5")]
    public void Existing_electors_accepts_a_count_or_a_range(string value, string stored)
    {
        var r = Assert.Single(ParseLines(Header, $"201-abc,1,abc,1-abc,Elector,{value},1,abc,1-abc,600,No action").Records);

        Assert.Equal(stored, r.ExistingElectors);
    }

    [Theory]
    [InlineData("1600-1001", "starts after it ends")]
    [InlineData("1-", "must be a number like 600 or a range like 1001-1600")]
    [InlineData("1-2-3", "must be a number like 600 or a range like 1001-1600")]
    [InlineData("six hundred", "must be a number like 600 or a range like 1001-1600")]
    [InlineData("-600", "must be a number like 600 or a range like 1001-1600")]
    public void Existing_electors_rejects_bad_values(string value, string message)
    {
        var error = Assert.Single(ParseLines(Header, $"201-abc,1,abc,1-abc,,{value},1,abc,1-abc,600,No action").Errors);

        Assert.Contains(message, error);
    }

    [Fact]
    public void Reports_every_bad_row_and_keeps_the_good_ones_out_of_errors()
    {
        var result = ParseLines(Header, ValidRow, "201-abc,x,abc,1-abc,,600,1,abc,1-abc,600,No action", ValidRow, "201-abc,1,abc,1-abc,,600,1,abc,1-abc,y,No action");

        Assert.Equal(2, result.Errors.Count);
        Assert.StartsWith("Row 3:", result.Errors[0]);
        Assert.StartsWith("Row 5:", result.Errors[1]);
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public void Row_numbers_count_skipped_blank_lines()
    {
        var error = Assert.Single(ParseLines(Header, ValidRow, ",,,,,,,,,,", "201-abc,x,abc,1-abc,,600,1,abc,1-abc,600,No action").Errors);

        Assert.StartsWith("Row 4:", error);
    }

    [Fact]
    public void Unclosed_quote_is_reported_instead_of_throwing()
    {
        var result = ParseLines(Header, ValidRow, "201-abc,1,abc,1-abc,,600,1,abc,1-abc,600,\"No action");

        Assert.Contains(result.Errors, e => e.Contains("not valid CSV"));
    }

    [Fact]
    public void Nul_character_is_reported()
    {
        var error = Assert.Single(ParseLines(Header, "201-abc,1,a\0b,1-abc,,600,1,abc,1-abc,600,No action").Errors);

        Assert.Equal("Row 2: Existing Part Name contains an invalid character (check the file is saved as CSV UTF-8)", error);
    }

    [Fact]
    public void Stray_quote_inside_a_field_is_reported_instead_of_throwing()
    {
        var result = ParseLines(Header, "201-abc,1,ab\"c,1-abc,,600,1,abc,1-abc,600,No action");

        Assert.Contains(result.Errors, e => e.Contains("not valid CSV"));
    }
}
