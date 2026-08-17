namespace WebSSMS.Models;

public enum DatabaseObjectType
{
    Server,
    DatabasesFolder,
    Database,
    TablesFolder,
    Table,
    ViewsFolder,
    View,
    ProceduresFolder,
    Procedure,
    FunctionsFolder,
    ScalarFunction,
    TableFunction,
    TriggersFolder,
    Trigger,
    TypesFolder,
    UserDefinedType,
    SynonymsFolder,
    Synonym,
    ColumnsFolder,
    Column,
    KeysFolder,
    PrimaryKey,
    ForeignKey,
    IndexesFolder,
    Index,
    ConstraintsFolder,
    CheckConstraint,
    UniqueConstraint,
    DefaultConstraint,
    SecurityFolder,
    LoginsFolder,
    Login,
    UsersFolder,
    User,
    RolesFolder,
    Role,
    SchemasFolder,
    Schema,
    ServerObjectsFolder,
    LinkedServersFolder,
    LinkedServer,
    AgentJobsFolder,
    AgentJob
}

public class DatabaseObjectNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public DatabaseObjectType ObjectType { get; set; }
    public string? SchemaName { get; set; }
    public string? DatabaseName { get; set; }
    public string? ParentName { get; set; }
    public string? DataType { get; set; }
    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public bool IsExpanded { get; set; }
    public bool IsLoading { get; set; }
    public bool ChildrenLoaded { get; set; }
    public List<DatabaseObjectNode> Children { get; set; } = new();

    public string Icon => ObjectType switch
    {
        DatabaseObjectType.Server => "server",
        DatabaseObjectType.Database or DatabaseObjectType.DatabasesFolder => "database",
        DatabaseObjectType.Table or DatabaseObjectType.TablesFolder => "table",
        DatabaseObjectType.View or DatabaseObjectType.ViewsFolder => "view",
        DatabaseObjectType.Procedure or DatabaseObjectType.ProceduresFolder => "procedure",
        DatabaseObjectType.ScalarFunction or DatabaseObjectType.TableFunction or DatabaseObjectType.FunctionsFolder => "function",
        DatabaseObjectType.Trigger or DatabaseObjectType.TriggersFolder => "trigger",
        DatabaseObjectType.Column or DatabaseObjectType.ColumnsFolder => "column",
        DatabaseObjectType.PrimaryKey or DatabaseObjectType.ForeignKey or DatabaseObjectType.KeysFolder => "key",
        DatabaseObjectType.Index or DatabaseObjectType.IndexesFolder => "index",
        DatabaseObjectType.CheckConstraint or DatabaseObjectType.UniqueConstraint or DatabaseObjectType.DefaultConstraint or DatabaseObjectType.ConstraintsFolder => "constraint",
        DatabaseObjectType.Login or DatabaseObjectType.User or DatabaseObjectType.LoginsFolder or DatabaseObjectType.UsersFolder => "user",
        DatabaseObjectType.Role or DatabaseObjectType.RolesFolder => "role",
        DatabaseObjectType.Schema or DatabaseObjectType.SchemasFolder => "schema",
        DatabaseObjectType.SecurityFolder => "security",
        DatabaseObjectType.AgentJob or DatabaseObjectType.AgentJobsFolder => "job",
        _ => "folder"
    };

    public string DisplayName
    {
        get
        {
            if (ObjectType == DatabaseObjectType.Column && DataType != null)
                return $"{Name} ({DataType}{(IsNullable ? ", null" : ", not null")})";
            if (SchemaName != null && ObjectType is DatabaseObjectType.Table or DatabaseObjectType.View
                or DatabaseObjectType.Procedure or DatabaseObjectType.ScalarFunction
                or DatabaseObjectType.TableFunction or DatabaseObjectType.Synonym)
                return $"{SchemaName}.{Name}";
            return Name;
        }
    }
}
