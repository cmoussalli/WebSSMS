namespace WebSSMS.Models;

public class TableDefinition
{
    public string SchemaName { get; set; } = "dbo";
    public string TableName { get; set; } = string.Empty;
    public string? DatabaseName { get; set; }
    public List<ColumnDefinition> Columns { get; set; } = new();
    public List<IndexDefinition> Indexes { get; set; } = new();
    public List<ForeignKeyDefinition> ForeignKeys { get; set; } = new();
    public List<CheckConstraintDefinition> CheckConstraints { get; set; } = new();
    public string? FileGroup { get; set; } = "PRIMARY";
    public bool IsExisting { get; set; }

    public string FullName => $"[{SchemaName}].[{TableName}]";
}

public class ColumnDefinition
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = "nvarchar";
    public int? MaxLength { get; set; } = 50;
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public bool IsNullable { get; set; } = true;
    public bool IsIdentity { get; set; }
    public int IdentitySeed { get; set; } = 1;
    public int IdentityIncrement { get; set; } = 1;
    public bool IsPrimaryKey { get; set; }
    public string? DefaultValue { get; set; }
    public string? ComputedExpression { get; set; }
    public bool IsPersisted { get; set; }
    public string? Description { get; set; }
    public int OrdinalPosition { get; set; }

    public string DataTypeDisplay
    {
        get
        {
            var type = DataType.ToLower();
            if (type is "nvarchar" or "varchar" or "nchar" or "char" or "binary" or "varbinary")
                return MaxLength == -1 ? $"{type}(MAX)" : $"{type}({MaxLength})";
            if (type is "decimal" or "numeric")
                return $"{type}({Precision},{Scale})";
            return type;
        }
    }
}

public class IndexDefinition
{
    public string Name { get; set; } = string.Empty;
    public IndexType Type { get; set; } = IndexType.NonClustered;
    public bool IsUnique { get; set; }
    public List<IndexColumn> Columns { get; set; } = new();
    public List<string> IncludedColumns { get; set; } = new();
    public string? FilterExpression { get; set; }
    public bool IsExisting { get; set; }
}

public class IndexColumn
{
    public string Name { get; set; } = string.Empty;
    public SortOrder SortOrder { get; set; } = SortOrder.Ascending;
}

public enum IndexType
{
    Clustered,
    NonClustered,
    Unique,
    ColumnStore,
    Xml,
    Spatial,
    FullText
}

public enum SortOrder
{
    Ascending,
    Descending
}

public class ForeignKeyDefinition
{
    public string Name { get; set; } = string.Empty;
    public List<ForeignKeyColumn> Columns { get; set; } = new();
    public string ReferencedTable { get; set; } = string.Empty;
    public string ReferencedSchema { get; set; } = "dbo";
    public ForeignKeyAction OnDelete { get; set; } = ForeignKeyAction.NoAction;
    public ForeignKeyAction OnUpdate { get; set; } = ForeignKeyAction.NoAction;
    public bool IsExisting { get; set; }
}

public class ForeignKeyColumn
{
    public string ColumnName { get; set; } = string.Empty;
    public string ReferencedColumnName { get; set; } = string.Empty;
}

public enum ForeignKeyAction
{
    NoAction,
    Cascade,
    SetNull,
    SetDefault
}

public class CheckConstraintDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public bool IsExisting { get; set; }
}
