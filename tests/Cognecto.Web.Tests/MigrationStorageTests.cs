using System.Net;
using Cognecto.Web.Models;
using Cognecto.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cognecto.Web.Tests;

[Collection(AppCollection.Name)]
public class MigrationStorageTests(AppFixture app)
{
    private const string Header =
        "AC No. & Name,Existing Part No.,Existing Part Name,Existing Section,Break-up Type,Existing Electors," +
        "Rationalized Part No.,Rationalized Part Name,Rationalized Section,Rationalized Electors,Action";

    private const string ValidRow = "201-abc,1,abc,1-abc,,600,1,abc,1-abc,600,No action";

    private static string Rows(int count) => Header + "\n" + string.Concat(Enumerable.Repeat(ValidRow + "\n", count));

    private async Task<long> UploadId(string fileName) =>
        Assert.Single(await app.QueryAsync("select id from migration_uploads where file_name = @f", r => r.GetInt64(0), ("f", fileName)));

    // A district of its own per test, so "latest upload per district" is not disturbed by other tests.
    private async Task<string> NewOfficer(string districtId)
    {
        var userId = $"TEST-{Guid.NewGuid():N}"[..20];
        await app.ExecuteAsync(
            "insert into app_users (user_id, full_name, designation, district_id) values (@u, 'Test Officer', 'ERO', @d)",
            ("u", userId), ("d", districtId));
        return userId;
    }

    [Fact]
    public async Task Every_column_is_stored_exactly()
    {
        var client = await app.SignedInAs("ERO-202-001");
        var fileName = $"columns-{Guid.NewGuid():N}.csv";

        await AppFixture.Upload(client, fileName, Header + "\n" +
            "202-def,2,stu,1-stu,Elector,1001-1450,2N1,stu,1-vwx,450,\"Creation of New PS, Elector Movement request\"\n" +
            "202-def,3,vwx,1-vwx,,900,3,vwx,1-vwx,900,No action\n");

        var rows = await app.QueryAsync(
            """
            select upload_id, uploaded_by, district_id, row_no, ac_no_name, existing_part_no, existing_part_name, existing_section,
                   breakup_type, existing_electors, rationalized_part_no, rationalized_part_name, rationalized_section,
                   rationalized_electors, action
            from migration_records where upload_id = @id order by row_no
            """,
            r => Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i)).ToArray(),
            ("id", await UploadId(fileName)));

        var id = await UploadId(fileName);
        Assert.Equal(
            [id, "ERO-202-001", "DST-02", 2, "202-def", 2, "stu", "1-stu", "Elector", "1001-1450", "2N1", "stu", "1-vwx", 450, "Creation of New PS, Elector Movement request"],
            rows[0]);
        Assert.Equal(
            [id, "ERO-202-001", "DST-02", 3, "202-def", 3, "vwx", "1-vwx", null, "900", "3", "vwx", "1-vwx", 900, "No action"],
            rows[1]);
    }

    [Fact]
    public async Task Seventy_five_districts_uploading_at_once_are_all_saved_correctly()
    {
        var officers = await Task.WhenAll(Enumerable.Range(1, 75).Select(i => NewOfficer($"CONC-{i:00}")));
        var clients = await Task.WhenAll(officers.Select(app.SignedInAs));
        var runId = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(clients.Select((client, i) =>
            AppFixture.Upload(client, $"conc-{runId}-{i}.csv", Rows(2_000))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        var saved = await app.QueryAsync(
            """
            select u.uploaded_by, u.district_id, u.row_count,
                   count(r.id), count(r.id) filter (where r.district_id = u.district_id and r.uploaded_by = u.uploaded_by)
            from migration_uploads u join migration_records r on r.upload_id = u.id
            where u.file_name like @pattern
            group by u.id
            """,
            r => (User: r.GetString(0), District: r.GetString(1), Declared: r.GetInt32(2), Stored: r.GetInt64(3), Matching: r.GetInt64(4)),
            ("pattern", $"conc-{runId}-%"));

        Assert.Equal(75, saved.Count);
        Assert.Equal(75, saved.Select(s => s.District).Distinct().Count());
        Assert.All(saved, s => Assert.Equal((2_000, 2_000L, 2_000L), (s.Declared, s.Stored, s.Matching)));
        Assert.Equal(officers.Order(), saved.Select(s => s.User).Order());
    }

    [Fact]
    public async Task Latest_views_return_each_districts_most_recent_upload_only()
    {
        var districtA = $"LATEST-A-{Guid.NewGuid():N}"[..20];
        var districtB = $"LATEST-B-{Guid.NewGuid():N}"[..20];
        var officerA = await app.SignedInAs(await NewOfficer(districtA));
        var officerB = await app.SignedInAs(await NewOfficer(districtB));

        await AppFixture.Upload(officerA, "a-first.csv", Rows(3));
        await AppFixture.Upload(officerB, "b-only.csv", Rows(2));
        await AppFixture.Upload(officerA, "a-second.csv", Rows(5));

        var latest = await app.QueryAsync(
            "select district_id, file_name from migration_latest_uploads where district_id in (@a, @b) order by district_id",
            r => (r.GetString(0), r.GetString(1)), ("a", districtA), ("b", districtB));
        Assert.Equal([(districtA, "a-second.csv"), (districtB, "b-only.csv")], latest);

        var latestRows = await app.QueryAsync(
            "select district_id, count(*) from migration_records_latest where district_id in (@a, @b) group by district_id order by district_id",
            r => (r.GetString(0), r.GetInt64(1)), ("a", districtA), ("b", districtB));
        Assert.Equal([(districtA, 5L), (districtB, 2L)], latestRows);

        // Nothing is thrown away: the first upload is still in the base tables.
        var all = await app.QueryAsync(
            "select count(*) from migration_records where district_id = @a", r => r.GetInt64(0), ("a", districtA));
        Assert.Equal(8L, Assert.Single(all));
    }

    [Fact]
    public async Task A_failed_save_leaves_nothing_behind()
    {
        var fileName = $"rollback-{Guid.NewGuid():N}.csv";
        var uploader = new AppUser { UserId = "ERO-201-001", DistrictId = "DST-01" };
        var good = new MigrationRecord { RowNo = 2, AcNoName = "201-abc", ExistingPartName = "a", ExistingSection = "s", ExistingElectors = "1", RationalizedPartNo = "1", RationalizedPartName = "a", RationalizedSection = "s", Action = "No action" };
        var bad = new MigrationRecord { RowNo = 3, AcNoName = "201-abc", ExistingPartName = "a\0b", ExistingSection = "s", ExistingElectors = "1", RationalizedPartNo = "1", RationalizedPartName = "a", RationalizedSection = "s", Action = "No action" };

        using (var scope = app.Services.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<MigrationUploadWriter>();
            await Assert.ThrowsAnyAsync<Exception>(() => writer.SaveAsync(uploader, fileName, [good, bad], CancellationToken.None));
        }

        Assert.Empty(await app.QueryAsync("select id from migration_uploads where file_name = @f", r => r.GetInt64(0), ("f", fileName)));
    }

    [Fact]
    public async Task Nul_characters_are_reported_instead_of_crashing_the_save()
    {
        var client = await app.SignedInAs("ERO-201-001");
        var fileName = $"nul-{Guid.NewGuid():N}.csv";

        var response = await AppFixture.Upload(client, fileName, Header + "\n201-abc,1,a\0b,1-abc,,600,1,abc,1-abc,600,No action\n");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Row 2: Existing Part Name contains an invalid character", await response.Content.ReadAsStringAsync());
        Assert.Empty(await app.QueryAsync("select id from migration_uploads where file_name = @f", r => r.GetInt64(0), ("f", fileName)));
    }

    [Fact]
    public async Task Missing_upload_shows_a_not_found_page()
    {
        var client = await app.SignedInAs("ERO-201-001");

        var response = await client.GetAsync("/Migration/Details/999999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("This page or upload does not exist, or it belongs to another district.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upload_page_states_the_size_limit_and_guards_against_double_submit()
    {
        var html = await (await app.SignedInAs("ERO-201-001")).GetStringAsync("/Migration");

        Assert.Contains("Up to 20 MB.", html);
        Assert.Contains("button.disabled = true", html);
    }
}

public class UploadGateTests
{
    [Fact]
    public async Task Never_lets_more_than_the_limit_run_at_once()
    {
        var gate = new UploadGate(3);
        var (running, peak) = (0, 0);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using (await gate.EnterAsync(CancellationToken.None))
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref running);
            }
        }));

        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task Waiting_request_can_be_cancelled_and_slot_is_released_once()
    {
        var gate = new UploadGate(1);
        var held = await gate.EnterAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(cancel.Token));

        held.Dispose();
        held.Dispose(); // double dispose must not free a second slot
        using var next = await gate.EnterAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(timeout.Token));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
