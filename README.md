# Cognecto: Migration table upload

ASP.NET Core MVC (.NET 10) module. An officer signs in, uploads the **Format: Migration table** sheet as a CSV, and every row is saved to Supabase (Postgres) with the uploader's ID.

## Layout

| Path | What |
|---|---|
| `database/schema.sql` | Tables plus 4 demo uploader IDs. Run once in Supabase. |
| `src/Cognecto.Web/wwwroot/samples/migration_sample.csv` | Demo data (18 rows, ACs 201 and 202). Also downloadable from the upload page. |
| `src/Cognecto.Web/Services/MigrationCsvParser.cs` | CSV reading and per-row validation |
| `src/Cognecto.Web/Controllers/MigrationController.cs` | Upload, list, and view uploads |
| `src/Cognecto.Web/Controllers/AccountController.cs` | Demo sign-in (pick an officer, no password) |

## Setup

1. **Create the tables.** In the Supabase dashboard, open SQL Editor, paste `database/schema.sql`, and click Run. It is safe to run again.
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

If any row is invalid, the whole file is rejected and the page lists the problems by row number, so a file is never half-saved. Blank lines are skipped. The size limit is 5 MB.

## Who uploaded what

- `migration_uploads.uploaded_by`: one row per file.
- `migration_records.uploaded_by`: also stored on every data row.

The ID comes from the signed-in user's session, not from a form field. To add real login later, replace `AccountController`. Uploads only read the `NameIdentifier` claim.

## Demo uploaders

| ID | Name | Role |
|---|---|---|
| ERO-201-001 | Ravi Kumar | Electoral Registration Officer, AC 201 |
| AERO-201-002 | Priya Sharma | Asst. Electoral Registration Officer, AC 201 |
| ERO-202-001 | Anil Verma | Electoral Registration Officer, AC 202 |
| DEO-001 | Meena Iyer | District Election Officer |
