# 0001. Modular monolith with two projects per module

- Status: Accepted
- Date: 2026-10-07
- Deciders: Lee Jia Le

## Context
The template must give teams the module boundaries of a service-oriented design without the operational cost of many deployables. The blueprint proposed a modular monolith with Clean Architecture inside each module, using five projects per module (Domain, Application, Infrastructure, Endpoints, Contracts), about 35 projects in total. Putting each layer in its own project forces every type to be `public` so the layers can see each other. That weakens the boundary that matters most in a modular monolith: the one between modules. Many small projects also slow builds and add csproj files to keep in sync (blueprint review, D1).

## Options considered
1. Five projects per module (one per layer plus Contracts) — layering enforced by the compiler; but every type must be `public`, the module boundary is no longer protected by `internal`, ~35 projects, slower builds.
2. Two projects per module: `TemplateName.Modules.{Module}` with Domain, Application, Infrastructure and Endpoints as folders, plus `TemplateName.Modules.{Module}.Contracts` — types are `internal` by default so only the entry point and the Contracts are visible; fewer projects, faster builds; layering inside a module relies on architecture tests instead of the compiler.
3. One project per module with no separate Contracts — simplest; but other modules would have to reference the whole module, so nothing stops them using its internals.

## Decision
We chose option 2. The module boundary is the one worth protecting with the compiler: everything in `TemplateName.Modules.{Module}` is `internal` except the `{Module}Module` entry point (and EF migrations), and other modules may reference only `TemplateName.Modules.{Module}.Contracts`. Layering inside a module (Domain, Application, Infrastructure, Endpoints) is enforced by architecture tests on namespaces.

## Consequences
- Positive: a strong module boundary through `internal`; about a third of the projects; faster builds and fewer csproj files.
- Negative / trade-offs accepted: layer violations inside a module are caught by architecture tests at test time, not by the compiler at build time.
- Follow-up actions: add architecture tests for module layering and `internal` visibility; document the layout in `docs/architecture/overview.md`.
