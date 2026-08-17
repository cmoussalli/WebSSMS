using Microsoft.Data.SqlClient;
using WebSSMS.Models;
using System.Text;

namespace WebSSMS.Services;

public class SecurityService
{
    private readonly ConnectionManager _connectionManager;

    public SecurityService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    public async Task<List<LoginDefinition>> GetLoginsAsync()
    {
        var logins = new List<LoginDefinition>();
        if (Connection == null) return logins;

        var sql = @"
            SELECT sp.name, sp.default_database_name, sp.default_language_name,
                   sp.is_disabled, sp.create_date, sp.modify_date,
                   LOGINPROPERTY(sp.name, 'IsExpired') as IsExpired,
                   LOGINPROPERTY(sp.name, 'IsLocked') as IsLocked
            FROM sys.server_principals sp
            WHERE sp.type IN ('S', 'U', 'G')
            ORDER BY sp.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var login = new LoginDefinition
            {
                Name = reader.GetString(0),
                DefaultDatabase = reader.IsDBNull(1) ? null : reader.GetString(1),
                DefaultLanguage = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsDisabled = reader.GetBoolean(3),
                CreateDate = reader.GetDateTime(4),
                ModifyDate = reader.GetDateTime(5),
                IsExisting = true
            };

            logins.Add(login);
        }

        // Get server roles for each login
        foreach (var login in logins)
        {
            var roleSql = @"
                SELECT r.name
                FROM sys.server_role_members m
                INNER JOIN sys.server_principals r ON m.role_principal_id = r.principal_id
                INNER JOIN sys.server_principals l ON m.member_principal_id = l.principal_id
                WHERE l.name = @loginName";

            using var roleCmd = Connection.CreateCommand();
            roleCmd.CommandText = roleSql;
            roleCmd.Parameters.AddWithValue("@loginName", login.Name);

            using var roleReader = await roleCmd.ExecuteReaderAsync();
            while (await roleReader.ReadAsync())
            {
                login.ServerRoles.Add(roleReader.GetString(0));
            }
        }

        return logins;
    }

    public async Task<List<DatabaseUserDefinition>> GetDatabaseUsersAsync(string database)
    {
        var users = new List<DatabaseUserDefinition>();
        if (Connection == null) return users;

        var sql = $@"
            USE [{database}];
            SELECT dp.name, sp.name as login_name, dp.default_schema_name, dp.create_date
            FROM sys.database_principals dp
            LEFT JOIN sys.server_principals sp ON dp.sid = sp.sid
            WHERE dp.type IN ('S', 'U', 'G') AND dp.name NOT IN ('guest', 'INFORMATION_SCHEMA', 'sys')
            ORDER BY dp.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(new DatabaseUserDefinition
            {
                Name = reader.GetString(0),
                LoginName = reader.IsDBNull(1) ? null : reader.GetString(1),
                DefaultSchema = reader.IsDBNull(2) ? null : reader.GetString(2),
                DatabaseName = database,
                CreateDate = reader.GetDateTime(3),
                IsExisting = true
            });
        }

        return users;
    }

    public async Task<List<RoleDefinition>> GetServerRolesAsync()
    {
        var roles = new List<RoleDefinition>();
        if (Connection == null) return roles;

        var sql = @"
            SELECT sp.name, sp.is_fixed_role, SUSER_SNAME(sp.owning_principal_id) as owner
            FROM sys.server_principals sp
            WHERE sp.type = 'R'
            ORDER BY sp.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            roles.Add(new RoleDefinition
            {
                Name = reader.GetString(0),
                IsServerRole = true,
                IsFixedRole = reader.GetBoolean(1),
                OwnerName = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsExisting = true
            });
        }

        return roles;
    }

    public async Task<List<RoleDefinition>> GetDatabaseRolesAsync(string database)
    {
        var roles = new List<RoleDefinition>();
        if (Connection == null) return roles;

        var sql = $@"
            USE [{database}];
            SELECT dp.name, dp.is_fixed_role, USER_NAME(dp.owning_principal_id) as owner
            FROM sys.database_principals dp
            WHERE dp.type = 'R'
            ORDER BY dp.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            roles.Add(new RoleDefinition
            {
                Name = reader.GetString(0),
                IsServerRole = false,
                IsFixedRole = reader.GetBoolean(1),
                OwnerName = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsExisting = true
            });
        }

        return roles;
    }

    public async Task<(bool Success, string? Error)> CreateLoginAsync(LoginDefinition login)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE LOGIN [{login.Name}] WITH PASSWORD = N'{login.Password}'");
            if (login.DefaultDatabase != null) sb.AppendLine($", DEFAULT_DATABASE = [{login.DefaultDatabase}]");
            if (!login.EnforcePasswordPolicy) sb.AppendLine(", CHECK_POLICY = OFF");

            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync();

            // Add server roles
            foreach (var role in login.ServerRoles)
            {
                using var roleCmd = Connection.CreateCommand();
                roleCmd.CommandText = $"ALTER SERVER ROLE [{role}] ADD MEMBER [{login.Name}]";
                await roleCmd.ExecuteNonQueryAsync();
            }

            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> CreateDatabaseUserAsync(DatabaseUserDefinition user)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            var sb = new StringBuilder();
            sb.Append($"USE [{user.DatabaseName}]; CREATE USER [{user.Name}]");
            if (user.LoginName != null) sb.Append($" FOR LOGIN [{user.LoginName}]");
            if (user.DefaultSchema != null) sb.Append($" WITH DEFAULT_SCHEMA = [{user.DefaultSchema}]");

            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync();

            // Add database roles
            foreach (var role in user.DatabaseRoles)
            {
                using var roleCmd = Connection.CreateCommand();
                roleCmd.CommandText = $"USE [{user.DatabaseName}]; ALTER ROLE [{role}] ADD MEMBER [{user.Name}]";
                await roleCmd.ExecuteNonQueryAsync();
            }

            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<PermissionEntry>> GetObjectPermissionsAsync(string database)
    {
        var permissions = new List<PermissionEntry>();
        if (Connection == null) return permissions;

        var sql = $@"
            USE [{database}];
            SELECT dp.state_desc, dp.permission_name, dp.class_desc,
                   OBJECT_NAME(dp.major_id) as object_name,
                   SCHEMA_NAME(o.schema_id) as schema_name,
                   USER_NAME(dp.grantee_principal_id) as grantee
            FROM sys.database_permissions dp
            LEFT JOIN sys.objects o ON dp.major_id = o.object_id
            WHERE dp.class <= 1
            ORDER BY grantee, dp.permission_name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            permissions.Add(new PermissionEntry
            {
                State = reader.GetString(0) switch
                {
                    "GRANT" => PermissionState.Grant,
                    "DENY" => PermissionState.Deny,
                    "GRANT_WITH_GRANT_OPTION" => PermissionState.GrantWithGrant,
                    _ => PermissionState.Revoke
                },
                Permission = reader.GetString(1),
                ObjectName = reader.IsDBNull(3) ? null : reader.GetString(3),
                SchemaName = reader.IsDBNull(4) ? null : reader.GetString(4),
                GranteeName = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return permissions;
    }
}
