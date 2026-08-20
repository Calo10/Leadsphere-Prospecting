-- Stock quote columns for public companies. News is stored in metadata_json.
USE [LeadsphereDB-dev];
GO

IF COL_LENGTH('dbo.ls_companies', 'ticker') IS NULL
    ALTER TABLE dbo.ls_companies ADD ticker nvarchar(20) NULL;
GO

IF COL_LENGTH('dbo.ls_companies', 'stock_price') IS NULL
    ALTER TABLE dbo.ls_companies ADD stock_price decimal(18, 4) NULL;
GO

IF COL_LENGTH('dbo.ls_companies', 'stock_change_percent') IS NULL
    ALTER TABLE dbo.ls_companies ADD stock_change_percent decimal(9, 4) NULL;
GO

IF COL_LENGTH('dbo.ls_companies', 'stock_currency') IS NULL
    ALTER TABLE dbo.ls_companies ADD stock_currency nvarchar(10) NULL;
GO

IF COL_LENGTH('dbo.ls_companies', 'stock_as_of') IS NULL
    ALTER TABLE dbo.ls_companies ADD stock_as_of datetimeoffset(7) NULL;
GO

PRINT 'Company market data columns ready.';
GO
