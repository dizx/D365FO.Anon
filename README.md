# D365 FO database anonymizer

A .NET 10 console application for a directly accessible SQL Server database copy.
Supply the server, database, and an explicit company scope as parameters. Windows authentication is the default.

## Build and run

```powershell
dotnet build -c Release
dotnet run -c Release --no-build -- --server localhost --database AxDB --dataarea USMF
dotnet run -c Release --no-build -- --server localhost --database AxDB --dataarea USMF --apply
```

The first command-line invocation previews changes and validates generated field lengths without writing data.
It reports counts and configured columns, never original values. Preview scans all affected records and can take time.

Alternatively run the compiled executable:

```powershell
.\bin\Release\net10.0\D365FO.Anon.exe --server localhost --database AxDB --all-dataareas --apply
```

Use `--help` for all options. SQL authentication uses `--user LOGIN --password-env FO_SQL_PASSWORD`,
where `FO_SQL_PASSWORD` is an environment variable you supply locally. Credentials are not stored in configuration.
Connections use encryption. For a local SQL Server with a self-signed certificate, explicitly add `--trust-server-certificate` if needed.

## Scope and operation

Both preview and apply require **exactly one** of:

- `--dataarea USMF`: select only that company's rows in tables with `DATAAREAID`.
  The ID is validated against `dbo.DataArea.ID` before processing rows. Company comparison uses the database collation.
  Reads and updates both use a parameterized `DATAAREAID` predicate.
- `--all-dataareas`: explicitly process all companies and shared tables.

Omitting scope, combining the two options, or repeating either option is an error before connecting to SQL Server.
`--help` and `--self-test` do not require a scope.

**Shared tables are excluded in single-company mode**, with a `SKIPPED SHARED TABLE` message for each configured table.
This includes the standard DirPartyTable, DirPersonName, postal/electronic address tables, and many HR tables.
They have no `DATAAREAID`, and a party/address/person can be used by multiple legal entities.
The initial implementation does not infer ownership through customer/vendor/employment relationships.
Consequently, single-company mode does **not** anonymize that company's shared party names, addresses, or shared HR data.
Use `--all-dataareas` to include these rules. The default single-company coverage is primarily the bank-account tables.
If no configured tables support the selected scope, the command fails without updates.

Run against a restored/development database with FO services, batch jobs, and integrations stopped.
SQL updates bypass FO validations, events, caches, change tracking behavior, and derived-field maintenance normally performed by application code.
Restart the application after a successful run. Keep a recoverable source backup outside the anonymized environment.

This is an initial, configurable masking tool, not a certification that the whole database is anonymous.
Unconfigured columns and tables are untouched. Missing configured tables/columns are printed and skipped;
`--apply` commits the supported subset. A successful exit means those supported rules completed, not full coverage.

Rows are read in pages of 500 and updated individually. All updates run in **one serializable transaction**:
an error rolls back the run. This favors consistency over throughput and can require substantial transaction-log space
and hold locks for the entire run. Preview also uses a serializable transaction but issues no updates.
Do not use the initial implementation for a large database without measuring preview/runtime and log requirements.
RECID must be unique across each configured table. PARTITION is included in update predicates when present.

## Names and reruns

Edit `names.json`, or pass `--names PATH`, to choose first names, surnames, company names, and recognized legal suffixes.
Names are selected independently of the original person name. For example, the lists can generate short names such as `Mo Son`.
Companies get a synthetic name, a record-ID suffix, and their recognized original legal suffix, such as `Testfirma 123 AS`.

Person identity is based on PARTITION and party RECID. DirPersonName.PERSON uses the same identity;
all effective-dated name records receive the same synthetic name. DirPerson and DirPersonName identify person parties.
DirPartyTable.Name is formatted as `FirstName LastName`; alternative FO name-sequence conventions are not implemented.
Middle names, aliases, and configured phonetic fields are cleared. Relationship keys are preserved.

Stable HMAC-based selection avoids a mapping table containing original names. The default seed is public;
optionally use `--seed-env FO_ANON_SEED` for a locally supplied stable seed.
Keep that seed and the name lists unchanged across reruns. Names are not guaranteed to be unique.
IDs remain linkable to the source database; this is synthetic masking/pseudonymization, not irreversible anonymization.

## Default rules

`rules.json` explicitly lists the table/column transformations. Schema matching is case-insensitive under dbo.
The application checks actual SQL types and generated string lengths. Values are never silently truncated.

- DirPartyTable and DirPersonName: coordinated synthetic names.
- LogisticsPostalAddress: synthetic street/address/city; selected detailed address components cleared; country retained.
  Postal codes are cleared, not replaced with geographically validated test addresses.
- LogisticsElectronicAddress: type-dependent synthetic locators, with email at `example.invalid`.
  Standard method-type enum values Phone=1, Email=2, URL=3, Fax=5 are assumed; other types receive a test marker.
- LogisticsLocation: descriptions cleared.
- CustBankAccount, VendBankAccount, BankAccountTable: obvious test account numbers and selected other bank fields cleared.
  Bank IDs and customer/vendor account relationship keys remain unchanged. Values deliberately do not support payment testing.
- HcmPersonIdentificationNumber: test identifiers; no country-specific check-digit generation.
- HcmPersonPrivateDetails: synthetic birth dates from 1970–1999 and birthplaces cleared where these columns exist.
  Empty/sentinel dates remain empty. Age bands and dependent HR dates are not preserved.

Null/blank values are retained except required generated names. Most fields need ordinary character SQL types;
birthDate supports date/datetime/datetime2. Numeric, binary, and encrypted fields need additional implementation.
Configured identifiers are SQL-quoted; values and cursors are SQL parameters.

## Further tables and coverage to review

These are review candidates, **not implemented rules**. Table/field availability varies with FO version,
localization, and extensions. Inspect the actual schema and relationships before adding rules.

| Area | Candidate tables / storage | Review |
|---|---|---|
| Customer/vendor details | CustTable, VendTable | Tax IDs, organization numbers, copied contact information, search names, payment references, custom fields |
| Contacts | ContactPerson | Copied names, titles, identifying text |
| Party and organization details | DirPerson, DirOrganization, DirPartyTable | Remaining person attributes and organization identifiers; leave subtype/relationship keys intact |
| Addresses | LogisticsPostalAddress and extensions | Coordinates, building/unit fields, historical addresses, country-specific fields not covered by defaults |
| Worker details | HcmWorker and HcmPerson* tables | Personnel numbers, emergency contacts, private information; coordinated relationship-aware rules needed |
| Employment and compensation | HcmEmployment, HcmWorkerFixedComp, HcmWorkerVariableComp and related tables | Identifying employment data and compensation amounts; coherent date/amount rules needed |
| Payroll | Installed payroll/localization/ISV tables | Payslips, tax IDs, benefits, deductions, bank information; inventory the installed schema |
| Sales | SalesTable, SalesLine, CustInvoiceJour, CustInvoiceTrans | Copied names, delivery/invoice addresses, customer references and free text |
| Purchasing | PurchTable, PurchLine, VendInvoiceJour, VendInvoiceTrans | Copied vendor/contact information, references and free text |
| Payments and journals | LedgerJournalTrans, CustTrans, VendTrans and payment-specific tables | Text, payment references and bank details; retain posting relationships |
| Users | UserInfo, SysUserInfo and identity-related tables | Names, email addresses, identity bindings; changes may break sign-in |
| Notes and attachments | DocuRef, DocuValue and external attachment storage | Notes, file names, file content; SQL field updates alone cannot sanitize external files |
| Logs and history | SysDatabaseLog, workflow history, batch/integration logs | Original values, messages, names and payloads |
| Staging/custom data | Data entity staging tables, integration queues and extension tables | Duplicated payloads and customer-specific sensitive fields |

The tool lists HCM/payroll/compensation-named tables without active rules. That name-based inventory is not exhaustive.
Copied records that reference LogisticsPostalAddress may reuse an already-masked record; literal copies need separate rules.
Search indexes, exports, reports, data lakes, database backups and external documents require separate handling.

For simple additional text fields, extend `rules.json` using these transformations:
`clear`, `email`, `phone`, `identification`, `bankNumber`, `street`, `address`, `city`,
`firstName`, `lastName`, `personName`, `partyName`, `contact`, `birthDate`.
Except for DirPersonName, generation uses the row's RECID. Do not apply name generation to copied names
that must match a different party: that requires an explicit relationship-aware implementation.
Relationship columns RECID, PARTITION, DATAAREAID, PERSON and PARTY cannot be configured as targets;
other business keys are your responsibility when extending rules.

## Verification

```powershell
dotnet run -- --self-test
```

Offline checks cover stable identities, person name consistency, company suffixes, stable birth dates,
email domain selection, address/street generation, and CLI defaults/validation.
Scope checks cover missing/conflicting/duplicate/empty scope options, single-company and all-company execution,
shared-table exclusion, and parameterized company predicates and bindings.
Build and offline tests do not validate an FO database. Perform a preview and validate a disposable restored copy
before using the output for testing. No SQL integration tests are included yet.
