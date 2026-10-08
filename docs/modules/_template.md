# {Module} module

<!-- Copy this file to docs/modules/{module}.md (kebab-case module name, the module's route segment: LeaveManagement → leave-management.md) when you add a module. Keep every heading, in this order; write "None." under a section that does not apply. -->

## Purpose and boundaries

<!-- What the module owns, what it deliberately does not do, and which other modules it talks to (only through *.Contracts). -->

## Endpoints

<!-- Every endpoint, written as METHOD + full route (e.g. GET /api/v1/{module}/{resources}/{id:guid}), with its success status and error codes. -->

| Method | Route | Purpose | Success | Errors |
|---|---|---|---|---|
| | | | | |

## Domain model

<!-- Aggregates, their invariants and life cycle. Draw state machines as a Mermaid stateDiagram-v2. -->

## Error codes

<!-- Every error code the module returns ({module}.snake_case), its HTTP status, its params and its English message. Messages live in Resources/{Module}ErrorMessages.resx with .ms.resx and .zh-Hans.resx (ADR 0009). -->

| Code | HTTP | `params` | Message (en) |
|---|---|---|---|
| | | | |

## Events

<!-- Domain events (internal, dispatched through the module outbox) and integration events (published in *.Contracts), with their handlers. -->

| Event | Kind | Raised when | Handlers |
|---|---|---|---|
| | | | |

## Configuration

<!-- Configuration sections and keys the module reads, with defaults. -->

| Key | Default | Purpose |
|---|---|---|
| | | |

## Data

<!-- The schema, its tables and indexes, and the migrations in order. -->

| Table | Purpose | Indexes |
|---|---|---|
| | | |

## Background processing

<!-- Hosted services, outbox handlers and scheduled jobs the module runs. -->

## Observability

<!-- Log messages, ActivitySource and Meter names, and metrics the module emits. -->

## Testing

<!-- Where the module's unit and integration tests live and what they cover. -->

## Changelog

<!-- Link to the changelog entries for this module's commit scope. -->
