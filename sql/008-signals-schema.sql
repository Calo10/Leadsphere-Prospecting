-- Company signal monitoring (canonical copy lives in leadsphere-api/sql/010-signals-schema.sql).
USE [LeadsphereDB-dev];
GO

IF OBJECT_ID(N'dbo.ls_signals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ls_signals (
        id                    uniqueidentifier NOT NULL CONSTRAINT DF_ls_signals_id DEFAULT NEWSEQUENTIALID() PRIMARY KEY,
        org_id                uniqueidentifier NOT NULL,
        company_id            uniqueidentifier NOT NULL,
        created_by_user_id    uniqueidentifier NULL,
        status                nvarchar(20) NOT NULL CONSTRAINT DF_ls_signals_status DEFAULT N'active',
        duration_type         nvarchar(20) NOT NULL,
        start_date            datetimeoffset(7) NOT NULL,
        end_date              datetimeoffset(7) NOT NULL,
        last_evaluation_date  datetimeoffset(7) NULL,
        created_at            datetimeoffset(7) NOT NULL CONSTRAINT DF_ls_signals_created_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
        updated_at            datetimeoffset(7) NOT NULL CONSTRAINT DF_ls_signals_updated_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
    );
    CREATE INDEX IX_ls_signals_org_id ON dbo.ls_signals (org_id, created_at DESC);
    CREATE INDEX IX_ls_signals_org_company ON dbo.ls_signals (org_id, company_id, status);
    CREATE INDEX IX_ls_signals_eval ON dbo.ls_signals (status, last_evaluation_date, end_date);
END;
GO

IF OBJECT_ID(N'dbo.ls_signal_snapshots', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ls_signal_snapshots (
        id              uniqueidentifier NOT NULL CONSTRAINT DF_ls_signal_snapshots_id DEFAULT NEWSEQUENTIALID() PRIMARY KEY,
        signal_id       uniqueidentifier NOT NULL,
        snapshot_date   datetimeoffset(7) NOT NULL,
        company_name    nvarchar(300) NULL,
        employee_count  int NULL,
        contact_count   int NULL,
        news_count      int NULL,
        industry        nvarchar(200) NULL,
        description     nvarchar(max) NULL,
        website         nvarchar(500) NULL,
        location        nvarchar(300) NULL,
        raw_json        nvarchar(max) NULL,
        created_at      datetimeoffset(7) NOT NULL CONSTRAINT DF_ls_signal_snapshots_created_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
        CONSTRAINT FK_ls_signal_snapshots_signal FOREIGN KEY (signal_id) REFERENCES dbo.ls_signals (id) ON DELETE CASCADE
    );
    CREATE INDEX IX_ls_signal_snapshots_signal ON dbo.ls_signal_snapshots (signal_id, snapshot_date DESC);
END;
GO

IF OBJECT_ID(N'dbo.ls_signal_events', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ls_signal_events (
        id              uniqueidentifier NOT NULL CONSTRAINT DF_ls_signal_events_id DEFAULT NEWSEQUENTIALID() PRIMARY KEY,
        signal_id       uniqueidentifier NOT NULL,
        snapshot_id     uniqueidentifier NULL,
        event_type      nvarchar(80) NOT NULL,
        severity        nvarchar(20) NOT NULL CONSTRAINT DF_ls_signal_events_severity DEFAULT N'info',
        title           nvarchar(300) NOT NULL,
        description     nvarchar(2000) NULL,
        previous_value  nvarchar(2000) NULL,
        new_value       nvarchar(2000) NULL,
        event_date      datetimeoffset(7) NOT NULL,
        created_at      datetimeoffset(7) NOT NULL CONSTRAINT DF_ls_signal_events_created_at DEFAULT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
        CONSTRAINT FK_ls_signal_events_signal FOREIGN KEY (signal_id) REFERENCES dbo.ls_signals (id) ON DELETE CASCADE
    );
    CREATE INDEX IX_ls_signal_events_signal ON dbo.ls_signal_events (signal_id, event_date DESC);
END;
GO

PRINT 'Signals monitoring tables ready.';
GO
