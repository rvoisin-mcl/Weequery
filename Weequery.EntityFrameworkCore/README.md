# Weequery.EntityFrameworkCore

*[taps the glass of a very large tank]*

You have written a binding list. You are proud of it. Somewhere in it is a property that does not exist, and
neither of us knows which one yet.

This package checks a [Weequery](https://github.com/rvoisin-mcl/Weequery) binding list against an EF Core model
**before any query is built**, so a path nothing maps is a startup failure, in front of you, rather than a
translation error on the first request that happens to name it, in front of a customer.

```csharp
// In startup, once, where a failure is yours rather than a caller's
var problems = context.ValidateBindings<Minion>(MinionBindings);

if (!problems.IsValid) { throw new InvalidOperationException(problems.ToString()); }
```

```
'Widget' (Gizmo) is not mapped: Minion has no mapped property or navigation called 'Gizmo'
```

There. Now you know which one.

## Why it exists

I have **no opinion whatsoever about what your database holds**. I have no dependencies and I never will, so a
binding to `Lair.Capasity` is perfectly well-formed as far as I am concerned: the key is legal, the path parses,
and `Validate()` will cheerfully tell you the request fits the bindings. What I cannot possibly tell you, from
where I am standing, is that no column has ever answered to that name.

So the failure waits. It waits for the first caller who names that key, and then arrives as an EF translation
error in a request handler, at the worst possible moment, looking for all the world like a bug rather than a
typo you made months ago while thinking about something else.

| | Answers | When |
|---|---|---|
| `Inquiry.Validate()` | does this **request** fit the bindings | per request |
| `context.ValidateBindings<T>(...)` | do the **bindings** fit the model | at startup, once |
| `inquiry.Build().ToQueryString()` | will **this query** translate | per query |

Three questions. Three answers. Do not confuse them.

## What it checks

I walk each `BindingRequest.PropertyPath` segment by segment against the model, the way one walks a perimeter
fence:

- a **mapped column** ends a path, and cannot continue one
- a **reference navigation** continues it, and is legal on its own (only the null tests will work on it)
- a **collection navigation** cannot be reached through, and says to use a quantifier instead
- anything else is reported, naming **the type that was being looked in**, so a misspelling three levels down
  tells you which level it went wrong at rather than leaving you to guess

One problem per binding rather than all of them, because a path stops meaning anything after the first segment
that fails to resolve. There is nothing left to look in. Every *binding* is checked though, so one call hands
you all the bad ones rather than making you fix them one at a time like some sort of penance.

If `T` itself is not in the model, that is one problem rather than one per binding. I am not going to insult you
by listing forty failures that all say the same thing.

## What it does not check

**Mapped is not translatable, and this answers only the first.** Whether a query translates depends on the
operator and the provider quite as much as on the property. `IsMatch` against SQL Server is the standing
example, SQL Server having no regular expressions at all, which I take personally. A binding that sails through
here can still meet a provider that will not take what is asked of it.

Where it is the **operator** the backend lacks rather than the property, that half is answerable without a model
at all: name them on `InquirySettings.Operators` and a condition using one is refused by `Validate()` and by
`Build()`, exactly the way an unbound field is. See
[the core README](../README.md#telling-it-what-your-backend-cannot-do). This package answers the other half,
whether the property a binding names is mapped at all.

The complete check for one query is to build it and call `ToQueryString()`, which compiles it through the
provider **without opening a connection either**, and throws where it cannot:

```csharp
try { inquiry.Build().ToQueryString(); }
catch (InvalidOperationException) { /* the provider refused it */ }
```

That one is per query. This one is per binding. They answer different halves of the same question and you almost
certainly want both. Take both.

## No connection is opened

None. The model is built from your own configuration rather than read from the server, so this costs whatever
building the model costs and precisely nothing after it. Put it beside the check that proves your connection
string works. Do not put it on the request path. I will know.

## Versions

Each target framework takes the EF Core that belongs to it, **8.0.11** for `net8.0` and **10.0.0** for
`net10.0`, rather than one floor for both. That is not tidiness. That is scar tissue.


8.0.11 rather than 8.0.0, which the `net8.0` target could otherwise have used, because 8.0.0 through 8.0.4 drag
in `Microsoft.Extensions.Caching.Memory` 8.0.0, and that carries a high-severity advisory. I am in this business
myself. I know an unpatched denial of service when I see one, and I decline to ship yours.

The reference is to `Microsoft.EntityFrameworkCore`, the abstraction, rather than to any provider. This reads a
model and never touches a database, so which one is underneath is frankly none of its business.

## Licence

MIT, same as the rest of the operation.
