# Weequery.EntityFrameworkCore

Checks a [Weequery](https://github.com/rvoisin-mcl/Weequery) binding list against an EF Core model **before any
query is built**, so a path nothing maps is a startup failure rather than a translation error on the first
request that happens to name it.

```csharp
// In startup, once, where a failure is yours rather than a caller's
var problems = context.ValidateBindings<Minion>(MinionBindings);

if (!problems.IsValid) { throw new InvalidOperationException(problems.ToString()); }
```

```
'Widget' (Gizmo) is not mapped: Minion has no mapped property or navigation called 'Gizmo'
```

## Why it exists

Weequery has **no opinion about what a database holds**. It has no dependencies and never will, so a binding to
`Lair.Capasity` is perfectly well-formed as far as the library is concerned: the key is legal, the path parses,
and `Validate()` will tell you the request fits the bindings. What nothing in Weequery can tell you is that no
column answers to it.

That failure then waits for the first caller who names that key, and arrives as an EF translation error in a
request handler rather than as a mistake in your binding list.

| | Answers | When |
|---|---|---|
| `Inquiry.Validate()` | does this **request** fit the bindings | per request |
| `context.ValidateBindings<T>(...)` | do the **bindings** fit the model | at startup, once |
| `inquiry.Build().ToQueryString()` | will **this query** translate | per query |

## What it checks

Each `BindingRequest.PropertyPath` is walked segment by segment against the model:

- a **mapped column** ends a path, and cannot continue one
- a **reference navigation** continues it, and is legal on its own (only the null tests will work on it)
- a **collection navigation** cannot be reached through, and says to use a quantifier instead
- anything else is reported, naming **the type that was being looked in**, so a misspelling three levels down
  tells you which level

One problem per binding rather than all of them, because a path stops meaning anything after the first segment
that does not resolve, there is nothing left to look in. Every *binding* is checked though, so one call lists
all the bad ones rather than the first.

If `T` itself is not in the model, that is one problem rather than one per binding.

## What it does not check

**Mapped is not translatable, and this only answers the first.** Whether a query translates depends on the
operator and the provider as much as on the property, `IsMatch` against SQL Server is the standing example,
since SQL Server has no regular expressions at all. A binding that passes here can still meet a provider that
will not take what is asked of it.

Where it is the **operator** that the backend does not have rather than the property, that half is answerable
without a model at all: name them on `InquirySettings.Operators` and a condition using one is refused by
`Validate()` and by `Build()`, the same way an unbound field is. See
[the core README](../README.md#telling-it-what-your-backend-cannot-do). This package answers the other half,
whether the property a binding names is mapped at all.

The complete check for one query is to build it and call `ToQueryString()`, which compiles it through the
provider **without opening a connection either**, and throws where it cannot:

```csharp
try { inquiry.Build().ToQueryString(); }
catch (InvalidOperationException) { /* the provider refused it */ }
```

That is per query. This is per binding. They answer different halves, and you probably want both.

## No connection is opened

The model is built from your configuration rather than read from the server, so this costs whatever building
the model costs and nothing after. It belongs beside the check that proves your connection string works, not on
the request path.

## Versions

Each target framework takes the EF Core that belongs to it, **8.0.11** for `net8.0` and **10.0.0** for
`net10.0`, rather than one floor for both. That is not tidiness. EF Core's metadata interfaces are not binary
compatible across majors: `IReadOnlyNavigationBase.IsCollection` compiled against 8 throws
`MissingMethodException` in a process that has loaded 10, which is exactly what this package's tests caught
before it shipped.

8.0.11 rather than 8.0.0, which the `net8.0` target could otherwise use, because 8.0.0 through 8.0.4 pull
`Microsoft.Extensions.Caching.Memory` 8.0.0 and that carries a high-severity advisory.

The reference is to `Microsoft.EntityFrameworkCore`, the abstraction, rather than to any provider: this reads a
model and never touches a database, so which one is underneath is none of its business.

## Licence

MIT, same as Weequery.
