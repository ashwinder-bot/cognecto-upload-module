using System.Net;

namespace Cognecto.Web.Tests;

[Collection(AppCollection.Name)]
public class MigrationUploadTests(AppFixture app)
{
    private const string Header =
        "AC No. & Name,Existing Part No.,Existing Part Name,Existing Section,Break-up Type,Existing Electors," +
        "Rationalized Part No.,Rationalized Part Name,Rationalized Section,Rationalized Electors,Action";

    private const string ValidRow = "201-abc,1,abc,1-abc,,600,1,abc,1-abc,600,No action";

    private static readonly string SampleCsv =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "migration_sample.csv"));

    // Each test uploads under its own file name so tests sharing the database never see each other's rows.
    private static string UniqueFileName() => $"migration-{Guid.NewGuid():N}.csv";

    private sealed record SavedUpload(long Id, string UploadedBy, string DistrictId, int RowCount);

    private sealed record SavedRecord(string UploadedBy, string DistrictId, int RowNo, string AcNoName, string ExistingElectors, string Action);

    private Task<List<SavedUpload>> UploadsNamed(string fileName) =>
        app.QueryAsync(
            "select id, uploaded_by, district_id, row_count from migration_uploads where file_name = @f",
            r => new SavedUpload(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3)),
            ("f", fileName));

    private Task<List<SavedRecord>> RecordsOf(long uploadId) =>
        app.QueryAsync(
            "select uploaded_by, district_id, row_no, ac_no_name, existing_electors, action from migration_records where upload_id = @id order by row_no",
            r => new SavedRecord(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5)),
            ("id", uploadId));

    [Fact]
    public async Task Upload_saves_every_row_with_the_uploaders_id_and_district()
    {
        var client = await app.SignedInAs("ERO-201-001");
        var fileName = UniqueFileName();

        var response = await AppFixture.Upload(client, fileName, SampleCsv);

        var upload = Assert.Single(await UploadsNamed(fileName));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Migration/Details/{upload.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(new SavedUpload(upload.Id, "ERO-201-001", "DST-01", 18), upload);

        var records = await RecordsOf(upload.Id);
        Assert.Equal(18, records.Count);
        Assert.All(records, r => Assert.Equal(("ERO-201-001", "DST-01"), (r.UploadedBy, r.DistrictId)));
        Assert.Equal(Enumerable.Range(2, 18), records.Select(r => r.RowNo));
        Assert.Equal("1001-1600", records[2].ExistingElectors);
        Assert.Equal("Creation of New PS, New Section, Elector Movement request", records[2].Action);
    }

    [Theory]
    [InlineData("AERO-201-002", "DST-01")]
    [InlineData("ERO-202-001", "DST-02")]
    [InlineData("DEO-001", "DST-01")]
    public async Task Each_uploader_is_stamped_with_their_own_district(string userId, string districtId)
    {
        var client = await app.SignedInAs(userId);
        var fileName = UniqueFileName();

        await AppFixture.Upload(client, fileName, $"{Header}\n{ValidRow}\n");

        var upload = Assert.Single(await UploadsNamed(fileName));
        Assert.Equal((userId, districtId), (upload.UploadedBy, upload.DistrictId));
        var record = Assert.Single(await RecordsOf(upload.Id));
        Assert.Equal((userId, districtId), (record.UploadedBy, record.DistrictId));
    }

    [Fact]
    public async Task Records_can_be_filtered_by_district()
    {
        var district1 = await app.SignedInAs("ERO-201-001");
        var district2 = await app.SignedInAs("ERO-202-001");
        var (file1, file2) = (UniqueFileName(), UniqueFileName());

        await AppFixture.Upload(district1, file1, SampleCsv);
        await AppFixture.Upload(district2, file2, $"{Header}\n{ValidRow}\n{ValidRow}\n");

        // The kind of query the main authority will run.
        var byDistrict = await app.QueryAsync(
            """
            select r.district_id, count(*) from migration_records r
            join migration_uploads u on u.id = r.upload_id
            where u.file_name in (@f1, @f2) and r.district_id = any(@districts)
            group by r.district_id order by r.district_id
            """,
            r => (r.GetString(0), r.GetInt64(1)),
            ("f1", file1), ("f2", file2), ("districts", new[] { "DST-02" }));

        Assert.Equal([("DST-02", 2L)], byDistrict);
    }

    [Fact]
    public async Task District_cannot_be_chosen_through_the_form()
    {
        var client = await app.SignedInAs("ERO-201-001");
        var fileName = UniqueFileName();

        await AppFixture.Upload(client, fileName, $"{Header}\n{ValidRow}\n", new Dictionary<string, string>
        {
            ["DistrictId"] = "DST-99",
            ["district_id"] = "DST-99",
            ["UploadedBy"] = "ERO-202-001",
        });

        var upload = Assert.Single(await UploadsNamed(fileName));
        Assert.Equal(("ERO-201-001", "DST-01"), (upload.UploadedBy, upload.DistrictId));
    }

    [Fact]
    public async Task District_change_applies_to_the_next_upload_without_signing_in_again()
    {
        await app.ExecuteAsync("""
            insert into app_users (user_id, full_name, designation, district_id)
            values ('TEST-MOVER', 'Test Mover', 'Test', 'DST-01')
            """);
        var client = await app.SignedInAs("TEST-MOVER");
        await app.ExecuteAsync("update app_users set district_id = 'DST-03' where user_id = 'TEST-MOVER'");
        var fileName = UniqueFileName();

        await AppFixture.Upload(client, fileName, $"{Header}\n{ValidRow}\n");

        Assert.Equal("DST-03", Assert.Single(await UploadsNamed(fileName)).DistrictId);
    }

    [Fact]
    public async Task Earlier_uploads_keep_their_district_when_the_uploader_moves()
    {
        await app.ExecuteAsync("""
            insert into app_users (user_id, full_name, designation, district_id)
            values ('TEST-HISTORY', 'Test History', 'Test', 'DST-01')
            """);
        var client = await app.SignedInAs("TEST-HISTORY");
        var fileName = UniqueFileName();
        await AppFixture.Upload(client, fileName, $"{Header}\n{ValidRow}\n");

        await app.ExecuteAsync("update app_users set district_id = 'DST-04' where user_id = 'TEST-HISTORY'");

        var upload = Assert.Single(await UploadsNamed(fileName));
        Assert.Equal("DST-01", upload.DistrictId);
        Assert.Equal("DST-01", Assert.Single(await RecordsOf(upload.Id)).DistrictId);
    }

    [Fact]
    public async Task One_bad_row_rejects_the_whole_file_and_lists_the_problem()
    {
        var client = await app.SignedInAs("ERO-201-001");
        var fileName = UniqueFileName();

        var response = await AppFixture.Upload(client, fileName, $"{Header}\n{ValidRow}\n201-abc,two,abc,1-abc,,600,1,abc,1-abc,600,No action\n");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("The file was not saved.", html);
        Assert.Contains("Row 3: Existing Part No. must be a whole number, got &#x27;two&#x27;", html);
        Assert.Empty(await UploadsNamed(fileName));
    }

    [Theory]
    [InlineData("migration.txt", "x", "Only .csv files are accepted.")]
    [InlineData("migration.csv.exe", "x", "Only .csv files are accepted.")]
    [InlineData("migration.csv", "", "Choose a CSV file to upload.")]
    [InlineData("migration.csv", "not,a,migration,table\n1,2,3,4\n", "Missing column(s): AC No. &amp; Name")]
    public async Task Invalid_files_are_rejected_with_a_message(string fileName, string content, string message)
    {
        var client = await app.SignedInAs("ERO-201-001");
        var before = await CountUploads();

        var response = await AppFixture.Upload(client, fileName, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await CountUploads());
    }

    [Fact]
    public async Task Uppercase_csv_extension_is_accepted()
    {
        var client = await app.SignedInAs("ERO-201-001");
        var fileName = UniqueFileName().Replace(".csv", ".CSV");

        var response = await AppFixture.Upload(client, fileName, $"{Header}\n{ValidRow}\n");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Single(await UploadsNamed(fileName));
    }

    [Fact]
    public async Task Files_over_20_mb_are_rejected()
    {
        var client = await app.SignedInAs("ERO-201-001");
        var fileName = UniqueFileName();
        var csv = Header + "\n" + string.Concat(Enumerable.Repeat(ValidRow + "\n", 20 * 1024 * 1024 / ValidRow.Length + 1));

        var response = await AppFixture.Upload(client, fileName, csv);

        Assert.Contains("The file is larger than 20 MB.", await response.Content.ReadAsStringAsync());
        Assert.Empty(await UploadsNamed(fileName));
    }

    [Fact]
    public async Task Twenty_thousand_rows_with_hindi_names_are_saved()
    {
        var client = await app.SignedInAs("ERO-201-001");
        var fileName = UniqueFileName();
        const string row = "245-लखनऊ पूर्व,1,प्राथमिक विद्यालय रामपुर कक्ष संख्या 1,1-रामपुर खुर्द मोहल्ला पश्चिम,Elector,1-600,1N1,प्राथमिक विद्यालय रामपुर कक्ष संख्या 1,1-रामपुर खुर्द मोहल्ला पश्चिम,600,\"Creation of New PS, New Section, Elector Movement request\"";
        var csv = Header + "\n" + string.Concat(Enumerable.Repeat(row + "\n", 20_000));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(csv) > 9 * 1024 * 1024);

        var response = await AppFixture.Upload(client, fileName, csv);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var upload = Assert.Single(await UploadsNamed(fileName));
        Assert.Equal(20_000, upload.RowCount);
        var names = await app.QueryAsync(
            "select distinct ac_no_name, existing_part_name from migration_records where upload_id = @id",
            r => (r.GetString(0), r.GetString(1)), ("id", upload.Id));
        Assert.Equal([("245-लखनऊ पूर्व", "प्राथमिक विद्यालय रामपुर कक्ष संख्या 1")], names);
    }

    [Fact]
    public async Task Large_valid_file_is_saved_in_full()
    {
        var client = await app.SignedInAs("ERO-202-001");
        var fileName = UniqueFileName();
        var csv = Header + "\n" + string.Concat(Enumerable.Repeat(ValidRow + "\n", 20_000));

        var response = await AppFixture.Upload(client, fileName, csv);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var upload = Assert.Single(await UploadsNamed(fileName));
        Assert.Equal(20_000, upload.RowCount);
        var counts = await app.QueryAsync(
            "select count(*), count(*) filter (where district_id = 'DST-02') from migration_records where upload_id = @id",
            r => (r.GetInt64(0), r.GetInt64(1)), ("id", upload.Id));
        Assert.Equal((20_000L, 20_000L), Assert.Single(counts));
    }

    [Fact]
    public async Task Signed_out_users_are_sent_to_sign_in_and_nothing_is_saved()
    {
        var client = app.AnonymousClient();
        var before = await CountUploads();

        var page = await client.GetAsync("/Migration");
        var form = new MultipartFormDataContent { { new StringContent($"{Header}\n{ValidRow}\n"), "file", "x.csv" } };
        var upload = await client.PostAsync("/Migration/Upload", form);

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("http://localhost/Account/Login", page.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
        Assert.Equal(before, await CountUploads());
    }

    [Fact]
    public async Task Upload_without_antiforgery_token_is_refused()
    {
        var client = await app.SignedInAs("ERO-201-001");
        var before = await CountUploads();

        var form = new MultipartFormDataContent { { new StringContent($"{Header}\n{ValidRow}\n"), "file", "x.csv" } };
        var response = await client.PostAsync("/Migration/Upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CountUploads());
    }

    [Fact]
    public async Task Sign_in_rejects_unknown_users()
    {
        var client = app.AnonymousClient();

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["userId"] = "NOBODY",
            ["__RequestVerificationToken"] = await AppFixture.AntiforgeryToken(client, "/Account/Login"),
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Unknown user.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upload_pages_only_show_the_officers_own_district()
    {
        var district1 = await app.SignedInAs("ERO-201-001");
        var district2 = await app.SignedInAs("ERO-202-001");
        var (file1, file2) = (UniqueFileName(), UniqueFileName());
        await AppFixture.Upload(district1, file1, $"{Header}\n{ValidRow}\n");
        await AppFixture.Upload(district2, file2, $"{Header}\n{ValidRow}\n");
        var id1 = Assert.Single(await UploadsNamed(file1)).Id;
        var id2 = Assert.Single(await UploadsNamed(file2)).Id;

        var index = await district1.GetStringAsync("/Migration");
        Assert.Contains(file1, index);
        Assert.DoesNotContain(file2, index);
        Assert.Contains("district <code>DST-01</code>", index);

        var own = await district1.GetAsync($"/Migration/Details/{id1}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Contains("<code>DST-01</code>", await own.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await district1.GetAsync($"/Migration/Details/{id2}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await district2.GetAsync($"/Migration/Details/{id2}")).StatusCode);
    }

    private async Task<long> CountUploads() =>
        Assert.Single(await app.QueryAsync("select count(*) from migration_uploads", r => r.GetInt64(0)));
}
