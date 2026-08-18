-- Company enrichment columns (logo, social, data sources)
USE [LeadsphereDB-dev];
GO

IF COL_LENGTH('dbo.ls_companies', 'logo_url') IS NULL
    ALTER TABLE dbo.ls_companies ADD logo_url nvarchar(500) NULL;
GO

IF COL_LENGTH('dbo.ls_companies', 'linkedin_url') IS NULL
    ALTER TABLE dbo.ls_companies ADD linkedin_url nvarchar(500) NULL;
GO

IF COL_LENGTH('dbo.ls_companies', 'twitter_url') IS NULL
    ALTER TABLE dbo.ls_companies ADD twitter_url nvarchar(500) NULL;
GO

IF COL_LENGTH('dbo.ls_companies', 'facebook_url') IS NULL
    ALTER TABLE dbo.ls_companies ADD facebook_url nvarchar(500) NULL;
GO

IF COL_LENGTH('dbo.ls_companies', 'instagram_url') IS NULL
    ALTER TABLE dbo.ls_companies ADD instagram_url nvarchar(500) NULL;
GO

IF COL_LENGTH('dbo.ls_companies', 'crunchbase_url') IS NULL
    ALTER TABLE dbo.ls_companies ADD crunchbase_url nvarchar(500) NULL;
GO

PRINT 'Company enrichment columns ready.';
GO
