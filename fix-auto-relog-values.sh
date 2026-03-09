#!/bin/bash
# Fix auto-relog values for existing accounts
# This script updates accounts with AutoRelogMinutes = 0 to the default 30 minutes

echo "Fixing auto-relog values in database..."

DB_PATH="./data/radegast.db"

if [ ! -f "$DB_PATH" ]; then
    echo "Error: Database not found at $DB_PATH"
    exit 1
fi

# Check if sqlite3 is installed
if ! command -v sqlite3 &> /dev/null; then
    echo "Error: sqlite3 is not installed. Please install it first:"
    echo "  sudo apt-get install sqlite3"
    exit 1
fi

# Backup the database first
BACKUP_PATH="./data/radegast.db.backup-$(date +%Y%m%d-%H%M%S)"
echo "Creating backup at: $BACKUP_PATH"
cp "$DB_PATH" "$BACKUP_PATH"

# Fix the values
echo "Updating accounts with invalid AutoRelogMinutes values..."
sqlite3 "$DB_PATH" <<EOF
UPDATE Accounts 
SET AutoRelogMinutes = 30 
WHERE AutoRelogMinutes IS NULL OR AutoRelogMinutes < 1;

SELECT 'Updated ' || changes() || ' account(s)';

.mode column
.headers on
SELECT Id, FirstName, LastName, AutoRelogEnabled, AutoRelogMinutes 
FROM Accounts;
EOF

echo ""
echo "Fix completed! Backup saved at: $BACKUP_PATH"
echo "Please restart your RadeWeb application for changes to take effect."
