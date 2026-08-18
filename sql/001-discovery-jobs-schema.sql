-- LeadSphere discovery jobs schema extension
-- Run against LeadsphereDB-dev after 002-mvp-schema.sql

USE [LeadsphereDB-dev];
GO

IF OBJECT_ID(N'dbo.ls_discovery_jobs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ls_discovery_jobs (
        id                      uniqueidentifier   NOT NULL CONSTRAINT DF_ls_discovery_jobs_id DEFAULT NEWSEQUENTIALID(),
        org_id                  uniqueidentifier   NOT NULL,
        search_id               uniqueidentifier   NOT NULL,
        status                  nvarchar(20)       NOT NULL CONSTRAINT DF_ls_discovery_jobs_status DEFAULT N'pending',
        error_message           nvarchar(max)      NULL,
        companies_found_count   int                NOT NULL CONSTRAINT DF_ls_discovery_jobs_companies DEFAULT 0,
        contacts_found_count    int                NOT NULL CONSTRAINT DF_ls_discovery_jobs_contacts DEFAULT 0,
        started_at              datetimeoffset(7)  NULL,
        completed_at            datetimeoffset(7)  NULL,
        created_at              datetimeoffset(7)  NOT NULL CONSTRAINT DF_ls_discovery_jobs_created_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
        updated_at              datetimeoffset(7)  NOT NULL CONSTRAINT DF_ls_discovery_jobs_updated_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
        CONSTRAINT PK_ls_discovery_jobs PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_ls_discovery_jobs_search FOREIGN KEY (search_id) REFERENCES dbo.ls_searches (id),
        CONSTRAINT CK_ls_discovery_jobs_status CHECK (status IN (N'pending', N'running', N'completed', N'failed', N'cancelled'))
    );

    CREATE NONCLUSTERED INDEX IX_ls_discovery_jobs_search ON dbo.ls_discovery_jobs (search_id, created_at DESC);
    CREATE NONCLUSTERED INDEX IX_ls_discovery_jobs_org_status ON dbo.ls_discovery_jobs (org_id, status);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_ls_companies_org_domain' AND object_id = OBJECT_ID(N'dbo.ls_companies'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UQ_ls_companies_org_domain
        ON dbo.ls_companies (org_id, domain)
        WHERE domain IS NOT NULL;
END
GO

PRINT 'LeadSphere discovery jobs schema ready.';
GO
