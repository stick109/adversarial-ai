-- Upgrade-in-place: if RedTeamRuns predates ErrorMessages, add the FK
-- column.  Paired with 011-runs-migrate-error.sql which moves the
-- inline ErrorMessage text into ErrorMessages and drops the old column.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE Name = N'ErrorMessageId'
       AND Object_ID = Object_ID(N'dbo.RedTeamRuns')
)
    ALTER TABLE dbo.RedTeamRuns ADD ErrorMessageId INT NULL REFERENCES dbo.ErrorMessages(Id);
