# IMMEDIATE FIX - For "table already exists" error on Linux machine

You have 3 options to fix this:

## Option 1: Use the Fix Script (RECOMMENDED)
```bash
# On your Linux machine:
chmod +x fix-linux-migration.sh
./fix-linux-migration.sh
dotnet ef database update --project RadegastWeb.csproj
```

## Option 2: Manual Migration File Fix
Edit the file `Migrations/20251013163043_InitialCreateWithVisitorStats.cs` and replace all:
- `migrationBuilder.CreateTable(` with `migrationBuilder.Sql(@"CREATE TABLE IF NOT EXISTS`
- `migrationBuilder.CreateIndex(` with `migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS`

## Option 3: Manual Database Fix (if you can't edit code files)
```bash
# Step 1: Create a backup (IMPORTANT!)
cp ./data/radegast.db ./data/radegast_backup_manual.db

# Step 2: Connect to your database and run these SQL commands
sqlite3 ./data/radegast.db

-- In the SQLite prompt, run these commands:

-- Create the migration history table
CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
    MigrationId TEXT PRIMARY KEY, 
    ProductVersion TEXT
);

-- Clear any existing migration history
DELETE FROM __EFMigrationsHistory;

-- Tell EF that all migrations have already been applied
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES 
    ('20251013163043_InitialCreateWithVisitorStats', '8.0.0'),
    ('20251022093619_AddAvatarRelayUuid', '8.0.0'),
    ('20251023005245_FixResidentLastNames', '8.0.0'),
    ('20251028111813_AddInteractiveNoticeFields', '8.0.0');

-- Check if AvatarRelayUuid column exists
PRAGMA table_info(Accounts);

-- If you don't see AvatarRelayUuid in the output above, add it:
ALTER TABLE Accounts ADD COLUMN AvatarRelayUuid TEXT;

-- Verify your accounts are still there
SELECT COUNT(*) as AccountCount FROM Accounts;
SELECT FirstName, LastName FROM Accounts LIMIT 5;

-- Exit SQLite
.quit

# Step 3: Now run the normal EF migration
dotnet ef database update --project RadegastWeb.csproj
```

## What These Fixes Do
- **Preserve ALL existing accounts** - no data loss
- **Fix the "table already exists" error** permanently  
- **Allow migrations to run successfully** on existing databases
- **Work on fresh installs** too

The fix changes `CREATE TABLE` to `CREATE TABLE IF NOT EXISTS` so the migration works whether tables exist or not.