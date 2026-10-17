using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace D365FO.Anon;

internal static class Program
{
    private const string Help = """
        D365FO.Anon --server SERVER --database DATABASE (--dataarea ID | --all-dataareas) [options]

        Preview is the default. No database values are printed.
          --dataarea ID                Only rows in this company; skip shared tables
          --all-dataareas               All companies, including shared tables
          --apply                      Execute supported rules in one transaction
          --names PATH                 Name lists (default: names.json beside executable)
          --rules PATH                 Explicit table/column rules (default: rules.json)
          --seed-env VARIABLE          Optional stable generation seed from environment
          --user USER                  SQL login; otherwise Windows authentication
          --password-env VARIABLE      Password environment variable for SQL login
          --trust-server-certificate   Trust the SQL Server certificate
          --timeout SECONDS            Command timeout (default: 120)
          --self-test                  Run offline transformation checks
          --help                       Show this help

        Specify exactly one scope option for both preview and apply.
        Shared party/address/HR tables require --all-dataareas.

        Example: D365FO.Anon --server localhost --database AxDB --dataarea USMF
        Example: D365FO.Anon --server localhost --database AxDB --all-dataareas --apply
        """;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help"))
            {
                Console.WriteLine(Help);
                return 0;
            }
            if (args.SequenceEqual(["--self-test"]))
            {
                SelfTest.Run();
                return 0;
            }
            var options = Options.Parse(args);
            var names = Load<Names>(options.NamesPath);
            names.Validate();
            var rules = Load<List<TableRule>>(options.RulesPath);
            ValidateRules(rules);
            var generator = new Generator(names, options.Seed);
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            var engine = new Engine(connection, options.Timeout, generator, options.Scope);
            await engine.Run(rules, options.Apply);
            return 0;
        }
        catch (Exception exception)
        {
            // SQL errors can include source values; only report their numeric codes.
            Console.Error.WriteLine(exception is SqlException sql
                ? $"SQL operation failed (number {sql.Number}, state {sql.State}). Active changes were rolled back."
                : exception is ArgumentException or InvalidDataException or JsonException
                    ? exception.Message
                    : $"Operation failed ({exception.GetType().Name}). Active changes were rolled back.");
            return 1;
        }
    }

    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException($"Empty configuration: {path}");

    private static void ValidateRules(List<TableRule> rules)
    {
        if (rules.Count == 0 || rules.Select(rule => rule.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Count)
            throw new InvalidDataException("Rules must contain unique table names.");
        foreach (var rule in rules)
        {
            if (rule.Columns.Count == 0)
                throw new InvalidDataException($"No columns configured for {rule.Table}.");
            foreach (var (column, kind) in rule.Columns)
            {
                if (new[] { "RECID", "PARTITION", "DATAAREAID", "PERSON", "PARTY" }.Contains(column.ToUpperInvariant()))
                    throw new InvalidDataException($"Relationship column cannot be transformed: {rule.Table}.{column}");
                if (!Generator.Kinds.Contains(kind))
                    throw new InvalidDataException($"Unknown transformation: {kind}");
            }
        }
    }
}

internal sealed record Options(string ConnectionString, bool Apply, string NamesPath, string RulesPath, string Seed, int Timeout, DataScope Scope)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        string[] valueOptions = ["--server", "--database", "--dataarea", "--names", "--rules", "--seed-env", "--user", "--password-env", "--timeout"];
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name is "--apply" or "--trust-server-certificate" or "--all-dataareas")
            {
                if (!flags.Add(name)) throw new ArgumentException($"Duplicate option: {name}");
            }
            else if (valueOptions.Contains(name) && index + 1 < args.Length && !args[index + 1].StartsWith("--"))
            {
                if (!values.TryAdd(name, args[++index])) throw new ArgumentException($"Duplicate option: {name}");
            }
            else throw new ArgumentException($"Unknown option or missing value: {name}");
        }
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Required option: {name}");
        string EnvironmentValue(string name) => Environment.GetEnvironmentVariable(Required(name)) is { Length: > 0 } value
            ? value : throw new ArgumentException($"Environment variable specified by {name} is empty.");
        if (values.ContainsKey("--dataarea") == flags.Contains("--all-dataareas"))
            throw new ArgumentException("Specify exactly one of --dataarea ID or --all-dataareas.");
        var dataArea = values.ContainsKey("--dataarea") ? Required("--dataarea").Trim() : null;
        if (dataArea is { Length: > 4 }) throw new ArgumentException("--dataarea must be a company ID of 1–4 characters.");
        var scope = new DataScope(dataArea);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Required("--server"), InitialCatalog = Required("--database"),
            IntegratedSecurity = !values.ContainsKey("--user"), Encrypt = true,
            TrustServerCertificate = flags.Contains("--trust-server-certificate"),
            ApplicationName = "D365FO.Anon", PersistSecurityInfo = false
        };
        if (!builder.IntegratedSecurity)
        {
            builder.UserID = Required("--user");
            builder.Password = EnvironmentValue("--password-env");
        }
        else if (values.ContainsKey("--password-env")) throw new ArgumentException("--password-env requires --user.");
        var timeout = 120;
        if (values.TryGetValue("--timeout", out var text) && (!int.TryParse(text, out timeout) || timeout < 1))
            throw new ArgumentException("--timeout must be a positive integer.");
        return new Options(builder.ConnectionString, flags.Contains("--apply"),
            values.GetValueOrDefault("--names", Path.Combine(AppContext.BaseDirectory, "names.json")),
            values.GetValueOrDefault("--rules", Path.Combine(AppContext.BaseDirectory, "rules.json")),
            values.ContainsKey("--seed-env") ? EnvironmentValue("--seed-env") : "fo-anon-v1", timeout, scope);
    }
}

internal sealed record DataScope(string? DataArea)
{
    public bool Includes(IReadOnlyDictionary<string, Column> schema) => DataArea is null || schema.ContainsKey("DATAAREAID");

    public string Predicate(IReadOnlyDictionary<string, Column> schema)
    {
        if (!Includes(schema)) throw new InvalidDataException("Shared tables cannot be processed in single-company mode.");
        return DataArea is null ? "" : " AND [DATAAREAID]=@dataArea";
    }

    public void Bind(SqlCommand command)
    {
        if (DataArea is not null) command.Parameters.Add("@dataArea", SqlDbType.NVarChar, 4).Value = DataArea;
    }
}

internal sealed record Names(string[] FirstNames, string[] LastNames, string[] Companies, string[] CompanySuffixes)
{
    public void Validate()
    {
        if (new[] { FirstNames, LastNames, Companies, CompanySuffixes }.Any(list => list is null || list.Length == 0 || list.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidDataException("All name lists must contain nonempty entries.");
    }
}

internal sealed record TableRule(string Table, Dictionary<string, string> Columns);
internal sealed record Column(string Name, string Type, int MaxLength);
internal sealed record Plan(TableRule Rule, Dictionary<string, Column> Schema, Dictionary<string, string> Columns);

internal sealed class Generator(Names names, string seed)
{
    public static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "firstName", "lastName", "personName", "partyName", "clear", "street", "address", "city",
        "contact", "email", "phone", "bankNumber", "identification", "birthDate"
    };

    private ulong Number(string key, string purpose) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(seed), Encoding.UTF8.GetBytes($"{key}|{purpose}")));

    public string First(string key) => names.FirstNames[(int)(Number(key, "first") % (ulong)names.FirstNames.Length)];
    public string Last(string key) => names.LastNames[(int)(Number(key, "last") % (ulong)names.LastNames.Length)];
    public string Person(string key) => $"{First(key)} {Last(key)}";

    public object Transform(string kind, string key, string token, object original, bool person, int? contactType)
    {
        var originalText = Convert.ToString(original, CultureInfo.InvariantCulture) ?? "";
        return kind switch
        {
            "firstName" => First(key),
            "lastName" => Last(key),
            "personName" => Person(key),
            "partyName" => person ? Person(key) : Company(key, token, originalText),
            "clear" => "",
            "street" or "address" => $"Test Street {token}",
            "city" => "Test City",
            "email" => $"contact{token}@example.invalid",
            "phone" => "000" + (Number(key, "phone") % 100000000).ToString("D8", CultureInfo.InvariantCulture),
            // FO LogisticsElectronicAddressMethodType: Phone=1, Email=2, URL=3, Fax=5.
            "contact" => contactType switch
            {
                2 => $"contact{token}@example.invalid",
                3 => $"https://example.invalid/contact/{token}",
                1 or 5 => "000" + (Number(key, "phone") % 100000000).ToString("D8", CultureInfo.InvariantCulture),
                _ => $"TEST-{token}"
            },
            "bankNumber" => $"TEST{token}",
            "identification" => $"TEST-{token}",
            "birthDate" => new DateTime(1970, 1, 1).AddDays((int)(Number(key, "birth") % 10957)),
            _ => throw new InvalidDataException($"Unknown transformation: {kind}")
        };
    }

    private string Company(string key, string token, string original)
    {
        var suffix = names.CompanySuffixes.OrderByDescending(value => value.Length)
            .FirstOrDefault(value => original.TrimEnd().EndsWith(" " + value, StringComparison.OrdinalIgnoreCase));
        return $"{names.Companies[(int)(Number(key, "company") % (ulong)names.Companies.Length)]} {token}" +
            (suffix is null ? "" : " " + suffix);
    }
}

internal sealed class Engine(SqlConnection connection, int timeout, Generator generator, DataScope scope)
{
    private const int PageSize = 500;
    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
    private SqlCommand Command(string sql, SqlTransaction? transaction = null) => new(sql, connection, transaction) { CommandTimeout = timeout };

    public async Task Run(List<TableRule> rules, bool apply)
    {
        var schema = await ReadSchema();
        Console.WriteLine(scope.DataArea is null
            ? "SCOPE: all data areas, including shared tables."
            : $"SCOPE: data area {scope.DataArea}; shared tables are excluded.");
        var plans = new List<Plan>();
        foreach (var rule in rules)
        {
            if (!schema.TryGetValue(rule.Table, out var columns))
            {
                Console.WriteLine($"MISSING TABLE: dbo.{rule.Table}");
                continue;
            }
            if (!scope.Includes(columns))
            {
                Console.WriteLine($"SKIPPED SHARED TABLE: dbo.{rule.Table} has no DATAAREAID; requires --all-dataareas.");
                continue;
            }
            if (!columns.ContainsKey("RECID")) throw new InvalidDataException($"No RECID on {rule.Table}.");
            var supported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, kind) in rule.Columns)
            {
                if (!columns.TryGetValue(name, out var column)) Console.WriteLine($"MISSING COLUMN: {rule.Table}.{name}");
                else
                {
                    var valid = kind == "birthDate" ? column.Type is "date" or "datetime" or "datetime2"
                        : column.Type is "nvarchar" or "varchar" or "nchar" or "char";
                    if (!valid) throw new InvalidDataException($"Unsupported SQL type for {rule.Table}.{name}: {column.Type}");
                    supported.Add(name, kind);
                }
            }
            if (supported.Count > 0) plans.Add(new Plan(rule, columns, supported));
        }
        var partyPlan = plans.Find(plan => plan.Rule.Table.Equals("DIRPARTYTABLE", StringComparison.OrdinalIgnoreCase));
        var personPlan = plans.Find(plan => plan.Rule.Table.Equals("DIRPERSONNAME", StringComparison.OrdinalIgnoreCase));
        foreach (var plan in plans)
            if (plan.Columns.Values.Contains("contact") && !plan.Schema.ContainsKey("TYPE"))
                throw new InvalidDataException($"Contact rules require a TYPE column on {plan.Rule.Table}.");
        if (partyPlan is not null || personPlan is not null)
        {
            if (partyPlan is null || personPlan is null || !personPlan.Schema.ContainsKey("PERSON") ||
                !partyPlan.Columns.TryGetValue("NAME", out var partyKind) || partyKind != "partyName" ||
                !personPlan.Columns.TryGetValue("FIRSTNAME", out var firstKind) || firstKind != "firstName" ||
                !personPlan.Columns.TryGetValue("LASTNAME", out var lastKind) || lastKind != "lastName")
                throw new InvalidDataException("Party anonymization requires paired DirPartyTable.Name and DirPersonName.Person/FirstName/LastName rules.");
        }
        foreach (var table in schema.Keys.Where(name => name.StartsWith("HCM", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("PAYROLL", StringComparison.OrdinalIgnoreCase) || name.Contains("COMPENSATION", StringComparison.OrdinalIgnoreCase)))
            if (!plans.Any(plan => plan.Rule.Table.Equals(table, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"REVIEW REQUIRED (no rule): dbo.{table}");

        if (plans.Count == 0) throw new InvalidDataException("No supported anonymization rules found.");
        Console.WriteLine(apply ? "APPLY: all supported changes use one transaction; keep FO and integrations stopped."
            : "PREVIEW: validating generated values; no updates.");
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        if (scope.DataArea is not null)
        {
            if (!schema.TryGetValue("DATAAREA", out var dataAreaColumns) || !dataAreaColumns.ContainsKey("ID"))
                throw new InvalidDataException("Cannot validate company: dbo.DataArea.ID is missing.");
            using var company = Command("SELECT COUNT_BIG(*) FROM [dbo].[DATAAREA] WHERE [ID]=@dataArea", transaction);
            scope.Bind(company);
            if (Convert.ToInt64(await company.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 0)
                throw new InvalidDataException($"Data area '{scope.DataArea}' does not exist. Nothing was updated.");
        }
        var people = new HashSet<string>(StringComparer.Ordinal);
        if (personPlan is not null)
        {
            var partition = personPlan.Schema.ContainsKey("PARTITION") ? "[PARTITION]" : "CAST(0 AS bigint)";
            using var command = Command($"SELECT DISTINCT {partition}, [PERSON] FROM [dbo].[DIRPERSONNAME] WHERE 1=1{scope.Predicate(personPlan.Schema)}", transaction);
            scope.Bind(command);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) people.Add($"{reader.GetValue(0)}:{reader.GetValue(1)}");
        }
        if (partyPlan is not null && schema.TryGetValue("DIRPERSON", out var personColumns) &&
            personColumns.ContainsKey("RECID") && scope.Includes(personColumns))
        {
            var partition = personColumns.ContainsKey("PARTITION") ? "[PARTITION]" : "CAST(0 AS bigint)";
            using var command = Command($"SELECT {partition}, [RECID] FROM [dbo].[DIRPERSON] WHERE 1=1{scope.Predicate(personColumns)}", transaction);
            scope.Bind(command);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) people.Add($"{reader.GetValue(0)}:{reader.GetValue(1)}");
        }
        foreach (var plan in plans) await Process(plan, people, transaction, apply);
        if (apply) await transaction.CommitAsync();
        else await transaction.RollbackAsync();
        Console.WriteLine(apply ? "Supported rules committed." : "Preview complete. Use --apply to execute.");
        Console.WriteLine("Coverage is limited to configured columns. Missing rules and copied document/free-text data require review.");
    }

    private async Task Process(Plan plan, HashSet<string> people, SqlTransaction transaction, bool apply)
    {
        long lastRecId = long.MinValue;
        long examined = 0, changed = 0;
        var isPerson = plan.Rule.Table.Equals("DIRPERSONNAME", StringComparison.OrdinalIgnoreCase);
        var selected = plan.Columns.Keys.Concat(new[] { "RECID", "PARTITION", "PERSON", "TYPE" }.Where(plan.Schema.ContainsKey))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var scopePredicate = scope.Predicate(plan.Schema);
        while (true)
        {
            var rows = new List<Dictionary<string, object>>();
            using (var command = Command($"SELECT TOP ({PageSize}) {string.Join(",", selected.Select(Quote))} FROM [dbo].{Quote(plan.Rule.Table)} WHERE [RECID] > @last{scopePredicate} ORDER BY [RECID]", transaction))
            {
                command.Parameters.Add("@last", SqlDbType.BigInt).Value = lastRecId;
                scope.Bind(command);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (var index = 0; index < selected.Length; index++) row.Add(selected[index], reader.GetValue(index));
                    rows.Add(row);
                }
            }
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var recId = Convert.ToInt64(row["RECID"], CultureInfo.InvariantCulture);
                if (recId <= lastRecId) throw new InvalidDataException($"RECID must be unique across {plan.Rule.Table}.");
                lastRecId = recId;
                examined++;
                var partition = row.GetValueOrDefault("PARTITION", 0L);
                var identity = isPerson ? row["PERSON"] : row["RECID"];
                var key = $"{partition}:{identity}";
                var token = Convert.ToString(identity, CultureInfo.InvariantCulture)!;
                int? contactType = row.TryGetValue("TYPE", out var type) && type != DBNull.Value ? Convert.ToInt32(type, CultureInfo.InvariantCulture) : null;
                var changes = new List<(string Name, object Value)>();
                foreach (var (columnName, kind) in plan.Columns)
                {
                    var original = row[columnName];
                    var requiredName = kind is "firstName" or "lastName" or "personName" or "partyName";
                    if (!requiredName && (original == DBNull.Value || original is string text && string.IsNullOrWhiteSpace(text))) continue;
                    if (kind == "birthDate" && original is DateTime date && date.Year <= 1900) continue;
                    var value = generator.Transform(kind, key, token, original, people.Contains(key), contactType);
                    var column = plan.Schema[columnName];
                    if (value is string replacement && column.MaxLength >= 0)
                    {
                        var bytes = column.Type is "nvarchar" or "nchar" ? replacement.Length * 2 : Encoding.UTF8.GetByteCount(replacement);
                        if (bytes > column.MaxLength)
                            throw new InvalidDataException($"Generated value exceeds {plan.Rule.Table}.{columnName} capacity. Adjust configuration; nothing was committed.");
                    }
                    if (!Equals(original, value)) changes.Add((columnName, value));
                }
                if (changes.Count == 0) continue;
                changed++;
                if (!apply) continue;
                var wherePartition = plan.Schema.ContainsKey("PARTITION") ? " AND [PARTITION]=@partition" : "";
                using var update = Command($"UPDATE [dbo].{Quote(plan.Rule.Table)} SET {string.Join(",", changes.Select((change, index) => $"{Quote(change.Name)}=@v{index}"))} WHERE [RECID]=@recId{wherePartition}{scopePredicate}", transaction);
                update.Parameters.Add("@recId", SqlDbType.BigInt).Value = recId;
                scope.Bind(update);
                if (wherePartition.Length > 0) update.Parameters.Add("@partition", SqlDbType.BigInt).Value = partition;
                for (var index = 0; index < changes.Count; index++) update.Parameters.AddWithValue($"@v{index}", changes[index].Value);
                if (await update.ExecuteNonQueryAsync() != 1) throw new InvalidDataException($"Unexpected update count in {plan.Rule.Table}.");
            }
        }
        Console.WriteLine($"{plan.Rule.Table}: examined {examined}, {(apply ? "updated" : "would update")} {changed}; columns: {string.Join(", ", plan.Columns.Keys)}");
    }

    private async Task<Dictionary<string, Dictionary<string, Column>>> ReadSchema()
    {
        var schema = new Dictionary<string, Dictionary<string, Column>>(StringComparer.OrdinalIgnoreCase);
        using var command = Command("SELECT t.name,c.name,TYPE_NAME(c.system_type_id),c.max_length FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id WHERE s.name=N'dbo' AND c.is_computed=0");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            if (!schema.TryGetValue(table, out var columns)) schema.Add(table, columns = new(StringComparer.OrdinalIgnoreCase));
            columns.Add(reader.GetString(1), new Column(reader.GetString(1), reader.GetString(2), reader.GetInt16(3)));
        }
        return schema;
    }
}

internal static class SelfTest
{
    public static void Run()
    {
        var generator = new Generator(new Names(["Mo", "Jonas"], ["Son", "Berg"], ["Testfirma"], ["AS", "ASA"]), "test");
        var key = "1:123";
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidDataException($"Self-test failed: {description}");
            checks++;
        }
        object Transform(string kind, object original, bool person = false, int? type = null) => generator.Transform(kind, key, "123", original, person, type);
        Check(Equals(Transform("partyName", "Original", true), generator.Person(key)), "party/person consistency");
        Check(generator.Person(key) == $"{Transform("firstName", "Morten")} {Transform("lastName", "Erikson")}", "name components");
        var company = Transform("partyName", "Original AS");
        Check(company.ToString()!.EndsWith(" AS"), "company suffix");
        Check(Equals(company, Transform("partyName", company)), "company rerun stability");
        Check(Equals(Transform("birthDate", new DateTime(1980, 1, 1)), Transform("birthDate", new DateTime(2000, 1, 1))), "stable date replacement");
        Check(Transform("contact", "real@email.com", type: 2).ToString()!.EndsWith("@example.invalid"), "non-deliverable email");
        Check(Equals(Transform("address", "original"), Transform("street", "original")), "formatted address consistency");
        void Reject(string[] extra, string expectedMessage)
        {
            try
            {
                Options.Parse(["--server", "server", "--database", "db", .. extra]);
                throw new InvalidDataException("Invalid options accepted.");
            }
            catch (ArgumentException exception) { Check(exception.Message.Contains(expectedMessage), expectedMessage); }
        }
        Reject(["--all-dataareas", "--aply"], "Unknown option");
        Reject([], "exactly one");
        Reject(["--apply"], "exactly one");
        Reject(["--dataarea", "USMF", "--all-dataareas"], "exactly one");
        Reject(["--dataarea", ""], "Required option");
        Reject(["--dataarea", "   "], "Required option");
        Reject(["--dataarea"], "missing value");
        Reject(["--dataarea", "USMFF"], "1–4 characters");
        Reject(["--all-dataareas", "--all-dataareas"], "Duplicate option");
        Reject(["--dataarea", "USMF", "--dataarea", "DEMF"], "Duplicate option");
        var options = Options.Parse(["--server", "server", "--database", "db", "--dataarea", " USMF "]);
        Check(!options.Apply && new SqlConnectionStringBuilder(options.ConnectionString).IntegratedSecurity, "preview and Windows-auth defaults");
        Check(options.Scope.DataArea == "USMF", "company scope is retained and trimmed");
        var all = Options.Parse(["--server", "server", "--database", "db", "--all-dataareas", "--apply"]);
        Check(all.Apply && all.Scope.DataArea is null, "explicit all-company apply");
        var scopedApply = Options.Parse(["--server", "server", "--database", "db", "--dataarea", "DEMF", "--apply"]);
        Check(scopedApply.Apply && scopedApply.Scope.DataArea == "DEMF", "explicit single-company apply");
        var shared = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
        var companySchema = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase) { ["dataareaid"] = new("DATAAREAID", "nvarchar", 8) };
        Check(!options.Scope.Includes(shared) && options.Scope.Includes(companySchema), "single-company shared table exclusion");
        Check(all.Scope.Includes(shared) && all.Scope.Includes(companySchema), "all-company shared table inclusion");
        Check(options.Scope.Predicate(companySchema) == " AND [DATAAREAID]=@dataArea", "parameterized company predicate");
        Check(all.Scope.Predicate(shared) == "" && all.Scope.Predicate(companySchema) == "", "all-company predicates");
        try
        {
            options.Scope.Predicate(shared);
            throw new InvalidOperationException("Shared-table filtering was allowed.");
        }
        catch (InvalidDataException) { checks++; }
        using var scopedCommand = new SqlCommand();
        options.Scope.Bind(scopedCommand);
        Check(scopedCommand.Parameters.Count == 1 && Equals(scopedCommand.Parameters["@dataArea"].Value, "USMF") &&
              scopedCommand.Parameters["@dataArea"].SqlDbType == SqlDbType.NVarChar, "bound company parameter");
        using var allCommand = new SqlCommand();
        all.Scope.Bind(allCommand);
        Check(allCommand.Parameters.Count == 0, "no company parameter for all-dataareas");
        Console.WriteLine($"All {checks} offline checks passed.");
    }
}
