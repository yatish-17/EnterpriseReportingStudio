IF OBJECT_ID(N'dbo.TenantReportFolders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantReportFolders
    (
        TenantId int NOT NULL,
        FolderName nvarchar(150) NOT NULL,
        CreatedAt datetime2(7) NOT NULL
            CONSTRAINT DF_TenantReportFolders_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_TenantReportFolders PRIMARY KEY (TenantId, FolderName)
    );
END;

IF OBJECT_ID(N'dbo.TenantReportFolderSetup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantReportFolderSetup
    (
        TenantId int NOT NULL PRIMARY KEY,
        InitializedAt datetime2(7) NOT NULL
            CONSTRAINT DF_TenantReportFolderSetup_InitializedAt DEFAULT SYSUTCDATETIME()
    );
END;
