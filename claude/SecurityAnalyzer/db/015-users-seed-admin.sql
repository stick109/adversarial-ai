-- Seed the default admin/pass row.  Insert-only: if the row already
-- exists (operator may have rotated the password) we do not overwrite.
IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username = N'admin')
INSERT INTO dbo.Users (Username, PasswordHash) VALUES (
    N'admin',
    N'100000.BSm9QfyUTSAWk68i4GHfbw==.gke7xwYJIpgMuRS8uSVqeyX+Bjx12EA4KTtzdpVpW5E='
);
