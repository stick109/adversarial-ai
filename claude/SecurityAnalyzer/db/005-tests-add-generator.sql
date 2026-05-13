-- Upgrade-in-place guard: add GeneratorModel to pre-existing installs.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE Name = N'GeneratorModel'
       AND Object_ID = Object_ID(N'dbo.PenetrationTests')
)
    ALTER TABLE dbo.PenetrationTests ADD GeneratorModel NVARCHAR(128) NULL;
