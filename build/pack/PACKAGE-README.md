# Modulith.Backend.Templates

`dotnet new` templates for an ASP.NET Core 10 modular-monolith backend on SQL Server: module boundaries, outbox, idempotency, observability and architecture tests.

The package holds two templates: `modulith-backend` (the whole solution) and `modulith-module` (one more module for a generated project).

## Install

```bash
dotnet new install Modulith.Backend.Templates
# a prerelease, by version
dotnet new install Modulith.Backend.Templates::<version>
```

## Generate

```bash
dotnet new modulith-backend -n Acme.Hr
# inside the generated project, add a module
dotnet new modulith-module
```

Source, documentation and issues: https://github.com/jackyjiale-oss/modulith-backend
