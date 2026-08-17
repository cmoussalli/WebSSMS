namespace WebSSMS.Models;

public class LoginDefinition
{
    public string Name { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string? DefaultDatabase { get; set; } = "master";
    public string? DefaultLanguage { get; set; }
    public bool IsDisabled { get; set; }
    public bool EnforcePasswordPolicy { get; set; } = true;
    public bool EnforcePasswordExpiration { get; set; }
    public DateTime? CreateDate { get; set; }
    public DateTime? ModifyDate { get; set; }
    public List<string> ServerRoles { get; set; } = new();
    public bool IsExisting { get; set; }
}

public class DatabaseUserDefinition
{
    public string Name { get; set; } = string.Empty;
    public string? LoginName { get; set; }
    public string? DefaultSchema { get; set; } = "dbo";
    public string? DatabaseName { get; set; }
    public List<string> DatabaseRoles { get; set; } = new();
    public List<PermissionEntry> Permissions { get; set; } = new();
    public DateTime? CreateDate { get; set; }
    public bool IsExisting { get; set; }
}

public class RoleDefinition
{
    public string Name { get; set; } = string.Empty;
    public bool IsServerRole { get; set; }
    public string? OwnerName { get; set; }
    public List<string> Members { get; set; } = new();
    public List<PermissionEntry> Permissions { get; set; } = new();
    public bool IsFixedRole { get; set; }
    public bool IsExisting { get; set; }
}

public class PermissionEntry
{
    public string Permission { get; set; } = string.Empty;
    public string? ObjectName { get; set; }
    public string? SchemaName { get; set; }
    public PermissionState State { get; set; } = PermissionState.Grant;
    public string? GranteeName { get; set; }
}

public enum PermissionState
{
    Grant,
    Deny,
    Revoke,
    GrantWithGrant
}
