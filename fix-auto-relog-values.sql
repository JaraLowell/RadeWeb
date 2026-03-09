-- Fix script for auto-relog settings
-- This updates any accounts with AutoRelogMinutes = 0 to the default 30 minutes
-- Run this if you had accounts before the auto-relog feature was added

-- Update accounts with invalid AutoRelogMinutes (0 or NULL)
UPDATE Accounts 
SET AutoRelogMinutes = 30 
WHERE AutoRelogMinutes IS NULL OR AutoRelogMinutes < 1;

-- Verify the update
SELECT Id, FirstName, LastName, AutoRelogEnabled, AutoRelogMinutes 
FROM Accounts;
