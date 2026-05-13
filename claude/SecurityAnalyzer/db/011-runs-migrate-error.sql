IF EXISTS (
    SELECT 1 FROM sys.columns
     WHERE Name = N'ErrorMessage'
       AND Object_ID = Object_ID(N'dbo.RedTeamRuns')
)
BEGIN
    -- The body has to go through sp_executesql because SQL Server's
    -- parser compiles the IF body even when the guard is false, and
    -- once the column has been dropped on a previous apply the bare
    -- reference to ErrorMessage fails parse with "Invalid column name".
    DECLARE @migrate NVARCHAR(MAX) = N'
        DECLARE @runId INT, @msg NVARCHAR(MAX), @errId INT;
        DECLARE migrate_err CURSOR LOCAL FAST_FORWARD FOR
            SELECT Id, ErrorMessage
              FROM dbo.RedTeamRuns
             WHERE ErrorMessage IS NOT NULL
               AND ErrorMessageId IS NULL;
        OPEN migrate_err;
        FETCH NEXT FROM migrate_err INTO @runId, @msg;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            INSERT INTO dbo.ErrorMessages (Message) VALUES (@msg);
            SET @errId = CAST(SCOPE_IDENTITY() AS INT);
            UPDATE dbo.RedTeamRuns SET ErrorMessageId = @errId WHERE Id = @runId;
            FETCH NEXT FROM migrate_err INTO @runId, @msg;
        END
        CLOSE migrate_err;
        DEALLOCATE migrate_err;

        ALTER TABLE dbo.RedTeamRuns DROP COLUMN ErrorMessage;';
    EXEC sp_executesql @migrate;
END
