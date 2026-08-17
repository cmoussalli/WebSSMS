namespace WebSSMS.Services;

public class TemplateService
{
    private readonly List<SqlTemplate> _templates;

    public TemplateService()
    {
        _templates = InitializeTemplates();
    }

    public List<SqlTemplate> GetTemplates() => _templates;

    public List<string> GetCategories() => _templates.Select(t => t.Category).Distinct().OrderBy(c => c).ToList();

    public List<SqlTemplate> GetTemplatesByCategory(string category) =>
        _templates.Where(t => t.Category == category).OrderBy(t => t.Name).ToList();

    private List<SqlTemplate> InitializeTemplates() => new()
    {
        // Database
        new("Create Database", "Database", "CREATE DATABASE [DatabaseName]\nGO"),
        new("Drop Database", "Database", "USE [master]\nGO\nIF DB_ID(N'DatabaseName') IS NOT NULL\nBEGIN\n    ALTER DATABASE [DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE\n    DROP DATABASE [DatabaseName]\nEND\nGO"),
        new("Alter Database", "Database", "ALTER DATABASE [DatabaseName]\n    MODIFY FILE (NAME = N'logical_name', SIZE = 100MB, MAXSIZE = UNLIMITED, FILEGROWTH = 10MB)\nGO"),

        // Table
        new("Create Table", "Table", "CREATE TABLE [dbo].[TableName]\n(\n    [Id] INT IDENTITY(1,1) NOT NULL,\n    [Column1] NVARCHAR(50) NOT NULL,\n    [Column2] NVARCHAR(100) NULL,\n    [CreatedDate] DATETIME2 NOT NULL DEFAULT GETDATE(),\n    CONSTRAINT [PK_TableName] PRIMARY KEY CLUSTERED ([Id])\n)\nGO"),
        new("Alter Table - Add Column", "Table", "ALTER TABLE [dbo].[TableName]\n    ADD [NewColumn] NVARCHAR(100) NULL\nGO"),
        new("Alter Table - Drop Column", "Table", "ALTER TABLE [dbo].[TableName]\n    DROP COLUMN [ColumnName]\nGO"),
        new("Drop Table", "Table", "IF OBJECT_ID(N'[dbo].[TableName]', N'U') IS NOT NULL\n    DROP TABLE [dbo].[TableName]\nGO"),
        new("Rename Table", "Table", "EXEC sp_rename 'dbo.OldTableName', 'NewTableName'\nGO"),

        // Index
        new("Create Index", "Index", "CREATE NONCLUSTERED INDEX [IX_TableName_ColumnName]\n    ON [dbo].[TableName] ([ColumnName] ASC)\n    INCLUDE ([IncludedColumn])\nGO"),
        new("Create Unique Index", "Index", "CREATE UNIQUE NONCLUSTERED INDEX [UX_TableName_ColumnName]\n    ON [dbo].[TableName] ([ColumnName] ASC)\nGO"),
        new("Drop Index", "Index", "DROP INDEX [IX_IndexName] ON [dbo].[TableName]\nGO"),
        new("Rebuild All Indexes", "Index", "ALTER INDEX ALL ON [dbo].[TableName] REBUILD\nGO"),

        // Stored Procedure
        new("Create Procedure", "Stored Procedure", "CREATE PROCEDURE [dbo].[ProcedureName]\n    @Param1 INT,\n    @Param2 NVARCHAR(50) = NULL\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    -- Your logic here\n    SELECT @Param1 AS Result\nEND\nGO"),
        new("Alter Procedure", "Stored Procedure", "ALTER PROCEDURE [dbo].[ProcedureName]\n    @Param1 INT\nAS\nBEGIN\n    SET NOCOUNT ON;\n    -- Your logic here\nEND\nGO"),

        // View
        new("Create View", "View", "CREATE VIEW [dbo].[ViewName]\nAS\n    SELECT Column1, Column2\n    FROM [dbo].[TableName]\n    WHERE Column1 IS NOT NULL\nGO"),

        // Function
        new("Create Scalar Function", "Function", "CREATE FUNCTION [dbo].[FunctionName]\n(\n    @Param1 INT\n)\nRETURNS INT\nAS\nBEGIN\n    DECLARE @Result INT\n    SET @Result = @Param1 * 2\n    RETURN @Result\nEND\nGO"),
        new("Create Table Function", "Function", "CREATE FUNCTION [dbo].[FunctionName]\n(\n    @Param1 INT\n)\nRETURNS TABLE\nAS\nRETURN\n(\n    SELECT Column1, Column2\n    FROM [dbo].[TableName]\n    WHERE Id = @Param1\n)\nGO"),

        // Trigger
        new("Create Trigger", "Trigger", "CREATE TRIGGER [dbo].[TriggerName]\n    ON [dbo].[TableName]\n    AFTER INSERT, UPDATE\nAS\nBEGIN\n    SET NOCOUNT ON;\n    -- Your logic here\nEND\nGO"),

        // Security
        new("Create Login", "Security", "CREATE LOGIN [LoginName]\n    WITH PASSWORD = N'StrongPassword123!',\n    DEFAULT_DATABASE = [master],\n    CHECK_POLICY = ON\nGO"),
        new("Create User", "Security", "USE [DatabaseName]\nGO\nCREATE USER [UserName] FOR LOGIN [LoginName]\n    WITH DEFAULT_SCHEMA = [dbo]\nGO"),
        new("Grant Permissions", "Security", "USE [DatabaseName]\nGO\nGRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[dbo] TO [UserName]\nGO"),

        // Backup
        new("Full Backup", "Backup", "BACKUP DATABASE [DatabaseName]\n    TO DISK = N'C:\\Backups\\DatabaseName_Full.bak'\n    WITH COMPRESSION, STATS = 10, CHECKSUM\nGO"),
        new("Differential Backup", "Backup", "BACKUP DATABASE [DatabaseName]\n    TO DISK = N'C:\\Backups\\DatabaseName_Diff.bak'\n    WITH DIFFERENTIAL, COMPRESSION, STATS = 10\nGO"),
        new("Transaction Log Backup", "Backup", "BACKUP LOG [DatabaseName]\n    TO DISK = N'C:\\Backups\\DatabaseName_Log.trn'\n    WITH COMPRESSION, STATS = 10\nGO"),
        new("Restore Database", "Backup", "RESTORE DATABASE [DatabaseName]\n    FROM DISK = N'C:\\Backups\\DatabaseName_Full.bak'\n    WITH REPLACE, RECOVERY, STATS = 10\nGO"),

        // Common Queries
        new("Find Tables by Name", "Common Queries", "SELECT TABLE_SCHEMA, TABLE_NAME\nFROM INFORMATION_SCHEMA.TABLES\nWHERE TABLE_TYPE = 'BASE TABLE'\n    AND TABLE_NAME LIKE '%SearchTerm%'\nORDER BY TABLE_SCHEMA, TABLE_NAME"),
        new("Find Columns by Name", "Common Queries", "SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE\nFROM INFORMATION_SCHEMA.COLUMNS\nWHERE COLUMN_NAME LIKE '%SearchTerm%'\nORDER BY TABLE_SCHEMA, TABLE_NAME"),
        new("Table Row Counts", "Common Queries", "SELECT s.name AS SchemaName, t.name AS TableName,\n    SUM(p.rows) AS RowCount\nFROM sys.tables t\nINNER JOIN sys.schemas s ON t.schema_id = s.schema_id\nINNER JOIN sys.partitions p ON t.object_id = p.object_id\nWHERE p.index_id IN (0, 1)\nGROUP BY s.name, t.name\nORDER BY SUM(p.rows) DESC"),
        new("Database Size", "Common Queries", "SELECT\n    DB_NAME() AS DatabaseName,\n    SUM(CAST(size AS BIGINT)) * 8 / 1024 AS SizeMB,\n    SUM(CASE WHEN type = 0 THEN CAST(size AS BIGINT) ELSE 0 END) * 8 / 1024 AS DataMB,\n    SUM(CASE WHEN type = 1 THEN CAST(size AS BIGINT) ELSE 0 END) * 8 / 1024 AS LogMB\nFROM sys.database_files"),
        new("Active Connections", "Common Queries", "SELECT\n    s.session_id, s.login_name, s.host_name,\n    DB_NAME(s.database_id) AS database_name,\n    s.program_name, s.status,\n    s.cpu_time, s.memory_usage\nFROM sys.dm_exec_sessions s\nWHERE s.is_user_process = 1\nORDER BY s.cpu_time DESC"),
        new("Blocking Queries", "Common Queries", "SELECT\n    r.session_id, r.blocking_session_id,\n    r.wait_type, r.wait_time,\n    DB_NAME(r.database_id) AS database_name,\n    (SELECT text FROM sys.dm_exec_sql_text(r.sql_handle)) AS sql_text\nFROM sys.dm_exec_requests r\nWHERE r.blocking_session_id > 0"),

        // CTE & Window Functions
        new("CTE Example", "Advanced", "WITH CTE AS (\n    SELECT\n        Column1,\n        Column2,\n        ROW_NUMBER() OVER (PARTITION BY Column1 ORDER BY Column2) AS RowNum\n    FROM [dbo].[TableName]\n)\nSELECT * FROM CTE\nWHERE RowNum = 1"),
        new("MERGE Statement", "Advanced", "MERGE INTO [dbo].[TargetTable] AS target\nUSING [dbo].[SourceTable] AS source\n    ON target.Id = source.Id\nWHEN MATCHED THEN\n    UPDATE SET target.Column1 = source.Column1\nWHEN NOT MATCHED THEN\n    INSERT (Column1) VALUES (source.Column1)\nWHEN NOT MATCHED BY SOURCE THEN\n    DELETE\nOUTPUT $action, inserted.*, deleted.*;\nGO"),
        new("Pivot Table", "Advanced", "SELECT *\nFROM (\n    SELECT Category, Year, Amount\n    FROM [dbo].[SalesData]\n) AS SourceTable\nPIVOT (\n    SUM(Amount)\n    FOR Year IN ([2023], [2024], [2025])\n) AS PivotTable"),
        new("Try-Catch", "Advanced", "BEGIN TRY\n    BEGIN TRANSACTION\n\n    -- Your operations here\n\n    COMMIT TRANSACTION\nEND TRY\nBEGIN CATCH\n    IF @@TRANCOUNT > 0\n        ROLLBACK TRANSACTION\n\n    SELECT\n        ERROR_NUMBER() AS ErrorNumber,\n        ERROR_SEVERITY() AS ErrorSeverity,\n        ERROR_STATE() AS ErrorState,\n        ERROR_MESSAGE() AS ErrorMessage\nEND CATCH")
    };
}

public class SqlTemplate
{
    public string Name { get; set; }
    public string Category { get; set; }
    public string Content { get; set; }

    public SqlTemplate(string name, string category, string content)
    {
        Name = name;
        Category = category;
        Content = content;
    }
}
