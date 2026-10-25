# Cognecto: Migration table upload

ASP.NET Core MVC (.NET 10) module. An officer signs in, uploads the **Format: Migration table** sheet as a CSV, and every row is saved to Supabase (Postgres) with the uploader's ID and district ID, so the main authority can later filter the data by district.

## Layout

| Path | What |
|---|---|
| `database/schema.sql` | Tables plus 4 demo uploader IDs. Run once in Supabase. |
| `src/Cognecto.Web/wwwroot/samples/migration_sample.csv` | Demo data (18 rows, ACs 201 and 202). Also downloadable from the upload page. |
| `src/Cognecto.Web/Services/MigrationCsvParser.cs` | CSV reading and per-row validation |
| `src/Cognecto.Web/Services/MigrationUploadWriter.cs` | Saves an upload and its rows in one transaction using Postgres `COPY` |
| `src/Cognecto.Web/Services/UploadOptions.cs` | Upload size limit and how many uploads are processed at once |
| `src/Cognecto.Web/Controllers/MigrationController.cs` | Upload, list, and view uploads |
| `src/Cognecto.Web/Controllers/AccountController.cs` | Demo sign-in (pick an officer, no password) |
| `tests/Cognecto.Web.Tests` | Parser unit tests and end-to-end upload tests against a real Postgres |

## Setup

1. **Create the tables.** In the Supabase dashboard, open SQL Editor, paste `database/schema.sql`, and click Run. It is safe to run again.
   On a database created before `district_id` existed, it adds the column and fills it in for past uploads from the uploader's district.
2. **Get the connection string.** Click **Connect** at the top of the dashboard, choose **Session pooler**, and pick type **.NET**. Use the session pooler: the direct connection is IPv6-only.
3. **Store it as a user secret** so the password stays out of `appsettings.json`:
   ```bash
   cd src/Cognecto.Web
   dotnet user-secrets set "ConnectionStrings:Supabase" "Host=aws-0-<region>.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.<project-ref>;Password=<db-password>;SSL Mode=Require"
   ```
   On a server, set the environment variable `ConnectionStrings__Supabase` instead.
4. **Run the app:**
   ```bash
   dotnet run --project src/Cognecto.Web
   ```
   Then open http://localhost:5129, sign in as one of the demo officers, and upload `migration_sample.csv`.

## CSV format

The file needs one header row with these 11 columns. Matching ignores case and punctuation, so `AC No & Name` or `ac_no_name` also work.

| Column | Rule |
|---|---|
| AC No. & Name | required, e.g. `201-abc` |
| Existing Part No. | whole number |
| Existing Part Name | required |
| Existing Section | required |
| Break-up Type | optional, e.g. `Elector` |
| Existing Electors | a count (`600`) or a serial range (`1001-1600`) |
| Rationalized Part No. | required, e.g. `2` or `2N1` |
| Rationalized Part Name | required |
| Rationalized Section | required |
| Rationalized Electors | whole number |
| Action | required, e.g. `No action` or `Creation of New PS, Elector Movement request` |

If any row is invalid, the whole file is rejected and the page lists the problems by row number, so a file is never half-saved. Blank lines are skipped.
The size limit is 20 MB. 20,000 rows with Hindi names is about 9.5 MB, because each Devanagari letter takes 3 bytes in UTF-8.

## Who uploaded what, and from which district

- `migration_uploads.uploaded_by` and `migration_uploads.district_id`: one row per file.
- `migration_records.uploaded_by` and `migration_records.district_id`: also stored on every data row, so the authority can filter rows directly, for example `select * from migration_records where district_id = 'DST-02'`.

The user ID comes from the signed-in session, and the district is looked up from `app_users.district_id` at upload time, never from a form field.
The district is copied onto the rows when they are saved, so if an officer later moves to another district, their earlier uploads stay filed under the old one.
Officers only see uploads from their own district in this module; filtering across districts is left to the main authority.

## Re-uploads and the latest data

Every upload is kept; nothing is overwritten or deleted.
Two views give the authority the current data, which is each district's most recent upload:

- `migration_latest_uploads`: one row per district, its newest upload.
- `migration_records_latest`: the rows of those uploads.

```sql
-- Current data for two districts
select * from migration_records_latest where district_id in ('DST-01', 'DST-02');

-- Rows that need action, per district, from the current data
select district_id, count(*) from migration_records_latest where action <> 'No action' group by district_id;

-- Full history of one district
select * from migration_uploads where district_id = 'DST-02' order by id desc;
```

"Latest" means the whole most recent file, so each district should upload its complete migration table every time, not one AC per file.
The views use `security_invoker`, which needs PostgreSQL 15 or newer.

To add real login later, replace `AccountController`. Uploads only read the `NameIdentifier` claim and look the user up in `app_users`, so every real user needs a row there with their `district_id`.

## Capacity

Settings live in the `Upload` section of `appsettings.json` and can be overridden with environment variables such as `Upload__MaxConcurrentUploads`.

| Setting | Default | Meaning |
|---|---|---|
| `MaxFileMegabytes` | 20 | Largest accepted CSV |
| `MaxConcurrentUploads` | 8 | Uploads parsed and saved at the same time; the rest wait their turn |

Measured on a 12-core laptop with a local Postgres, using a Release build:

| Load | Time | Result | App memory |
|---|---|---|---|
| 1 upload of 20,000 rows | about 1.7 s | saved | about 220 MB |
| 75 districts at once, 20,000 rows each (4 MB files) | 38 s for all | all 75 saved, 1.5 million rows, every row with the right district | 605 MB peak |
| 75 districts at once, 20,000 Hindi rows each (9.4 MB files) | 32 s for all | all 75 saved | 693 MB peak |

Rows are written with Postgres `COPY` inside the same transaction as the upload, so a failure part-way leaves nothing behind.
Raise `MaxConcurrentUploads` on a larger server for shorter waits during a burst; each upload being processed needs roughly 50 to 60 MB.
If the app runs behind a reverse proxy, allow request bodies of at least 20 MB and a response timeout of at least 60 seconds, since an upload may wait for a free slot during a burst.

## Integrating into the main system

1. **Database.** Run `database/schema.sql` (PostgreSQL 15 or newer). It creates the tables, indexes and views, and is safe to run again.
2. **Connection string.** Set `ConnectionStrings__Supabase` (the name is historical; any PostgreSQL connection string works).
3. **Users and districts.** Every officer who can upload needs a row in `app_users` with their `district_id`, using the same IDs as the main system. Remove the four demo officers at the end of `schema.sql`.
4. **Sign-in.** Replace `AccountController` with the main system's login. The only requirement is that the signed-in user has a `ClaimTypes.NameIdentifier` claim equal to their `app_users.user_id`.
5. **Authority filtering.** Query `migration_records_latest` for current data, or `migration_records` for all history, filtered by `district_id`.

## Tests

```bash
dotnet test
```

Docker must be running: the end-to-end tests start a throwaway Postgres container, create it from `database/schema.sql`, then sign in and upload through the real app.

## Demo uploaders

| ID | Name | Role | District |
|---|---|---|---|
| ERO-201-001 | Ravi Kumar | Electoral Registration Officer, AC 201 | DST-01 |
| AERO-201-002 | Priya Sharma | Asst. Electoral Registration Officer, AC 201 | DST-01 |
| ERO-202-001 | Anil Verma | Electoral Registration Officer, AC 202 | DST-02 |
| DEO-001 | Meena Iyer | District Election Officer | DST-01 |
