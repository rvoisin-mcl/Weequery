# Weequery.EntityFrameworkCore

Checks a [Weequery](https://github.com/rvoisin-mcl/Weequery) binding list against an EF Core model **before any
query is built**. A property path that nothing maps becomes a startup failure rather than a translation error on
the first request that names it.

## Installation

```bash
dotnet add package Weequery.EntityFrameworkCore
```

## Example

```csharp
// At startup, once, where a failure is the developer's rather than a caller's
var problems = context.ValidateBindings<Minion>(MinionBindings);

if (!problems.IsValid) { throw new InvalidOperationException(problems.ToString()); }
```

```
'Widget' (Gizmo) is not mapped: Minion has no mapped property or navigation called 'Gizmo'
```

## Purpose

Weequery has no knowledge of what the database holds and no dependencies. A binding to `Lair.Capasity` is
well-formed as far as Weequery is concerned: the key is legal, the path parses, and `Validate()` reports that the
request fits the bindings. Weequery cannot detect that no column has that name.

Without this check the failure is deferred to the first caller who names that key, and arrives as an EF
translation error in a request handler, where it looks like a bug rather than a typo in the binding list.

| Check | Answers | When |
|---|---|---|
| `Inquiry.Validate()` | does this **request** fit the bindings | per request |
| `context.ValidateBindings<T>(...)` | do the **bindings** fit the model | at startup, once |
| `inquiry.Build().ToQueryString()` | will **this query** translate | per query |

## What it checks

Each `BindingRequest.PropertyPath` is walked segment by segment against the model:

- A **mapped column** ends a path and cannot continue one.
- A **reference navigation** continues a path, and is legal on its own (only the null tests work on it).
- A **collection navigation** cannot be reached through; the problem says to use a quantifier instead.
- Anything else is reported, naming **the type being searched**, so a misspelling three levels down identifies the
  level at which it failed.

Rules:

- At most one problem is reported per binding, because a path means nothing after the first segment that fails to
  resolve.
- Every binding is checked, so one call returns all the bad bindings.
- If `T` itself is not in the model, that is reported as one problem rather than one per binding.

## What it does not check

**Mapped is not translatable, and this package checks only the first.** Whether a query translates depends on the
operator and the provider as much as on the property. The standard example is `IsMatch` against SQL Server, which
has no regular expressions. A binding that passes here can still meet a provider that cannot translate what is
asked of it.

Where the backend lacks an **operator** rather than a property, no model is needed: list the operator on
`InquirySettings.Operators`, and a condition using it is refused by `Validate()` and by `Build()`, as an unbound
field is. See [the core README](../README.md#backend-operator-support). This package covers the other
half: whether the property a binding names is mapped at all.

The complete check for one query is to build it and call `ToQueryString()`, which compiles it through the provider
**without opening a connection**, and throws where it cannot:

```csharp
try { inquiry.Build().ToQueryString(); }
catch (InvalidOperationException) { /* the provider refused it */ }
```

`ToQueryString()` is per query; `ValidateBindings` is per binding. They answer different halves of the same
question, and most applications want both.

## No connection is opened

The model is built from the application's own configuration rather than read from the server, so the check costs
whatever building the model costs and nothing more. Run it at startup beside the check that the connection string
works, not on the request path.

## Versions

Each target framework takes its own EF Core version rather than one floor for both: **10.0.15** for `net10.0` and
**11.0.0** for `net11.0`.

The reference is to `Microsoft.EntityFrameworkCore`, the abstraction, not to any provider. The package reads a model
and never touches a database, so the provider underneath is irrelevant.

## Licence

MIT, as Weequery.
