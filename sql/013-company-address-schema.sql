-- Street-level company address (separate from high-level location)
IF COL_LENGTH('dbo.ls_companies', 'address') IS NULL
    ALTER TABLE dbo.ls_companies ADD address nvarchar(500) NULL;
GO
