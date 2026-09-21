using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Weequery.Bindings;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery;

// Turning what was declared and what was asked for into one IQueryable. Private throughout, and shared by
// Validate and the Build family, which is why it lives with neither of them.
public partial class Inquiry<T> where T : class
{

    /// <summary>
    /// If a key maps to a bound property or collection
    /// </summary>
    private bool IsBound(string field)
    {
        var key = BindingLookup.SplitIndex(field).Key;

        return Bindings.ContainsKey(key) || Collections.ContainsKey(key);
    }

    /// <summary>
    /// If a field survives, noting it as dropped where it does not. For the two halves that filter a flat
    /// list rather than rewriting a tree, see <see cref="DroppedFields"/>.
    /// </summary>
    /// <param name="field">the key as the query named it</param>
    /// <param name="from">which part of the query is asking</param>
    private bool Keep(string field, BindingUse from)
    {
        if (IsBound(field)) { return true; }

        Drop(field, from);

        return false;
    }

    /// <summary>
    /// Refuse a condition using an operator that has been excluded, see
    /// <see cref="InquirySettings.Operators"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Check here because this is where <see cref="Build"/> and <see cref="Validate()"/> meet
    /// </para>
    /// <para>
    /// Every operator counts, including the conjunctions and the ones inside a quantifier, see
    /// <see cref="ConditionFunctions.OperatorsUsed"/>. The first one refused is the one reported, as everywhere
    /// else, see <see cref="ValidationResult"/>.
    /// </para>
    /// <para>
    /// The default set allows everything
    /// </para>
    /// </remarks>
    /// <param name="condition"></param>
    /// <exception cref="WeequeryException">it uses an operator the set does not allow</exception>
    private void RefuseUnsupportedOperators(ICondition condition)
    {
        if (Settings.Operators.IsEverything) { return; }

        if (ConditionFunctions.FirstUnsupported(condition, Settings.Operators) is not { } unsupported) { return; }

        throw new WeequeryException(
            WeequeryError.NotTranslatable,
            string.IsNullOrEmpty(unsupported.Field)
                ? $"{unsupported.Operator} is not supported by this data source"
                : $"'{unsupported.Field}' is tested with {unsupported.Operator}, which this data source does not support");
    }

    /// <summary>
    /// The predicate for one condition, bounded where it is this process that will run it.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Operator.IsMatch"/> cares, and only because the overload carrying a timeout is not one a
    /// provider translates, see <see cref="RegexTimeout"/>. A query over an in-memory sequence is evaluated here,
    /// so the bound applies; one over a provider is the database's to run under its own limits.
    /// </remarks>
    /// <param name="condition"></param>
    /// <returns></returns>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private Expression<Func<T, bool>> Predicate(ICondition condition)
    {
        RefuseUnsupportedOperators(condition);

        var predicate = ExpressionBuilder.BuildExpression(Bindings, condition, Collections);

        // LINQ to Objects, which is what AsQueryable over a list gives. Anything else is a provider that will be
        // handed the expression rather than running it here.
        return (Query.Provider is EnumerableQuery) ? StringComparisonRules.Apply(RegexTimeout.Apply(predicate), Settings) : predicate;
    }

    /// <summary>
    /// The wrapped IQueryable with every condition applied, and nothing else.
    /// </summary>
    /// <remarks>
    /// The rows the caller's filter matched, before any ordering is imposed, or any window applied. This is
    /// what <see cref="PagedQuery{T}.Total"/> is a count of.
    /// </remarks>
    /// <returns></returns>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private IQueryable<T> Filtered()
    {
        var condition = Combined();

        return (condition is null) ? Query : Query.Where(Predicate(condition));
    }

    /// <summary>
    /// Every applied condition as one, pruned where the caller asked for that.
    /// </summary>
    /// <remarks>
    /// Can be null for a query that never had applied coniditons, one if the the conditions were completely pruned
    /// away, see <see cref="InquirySettings.IgnoreUnboundFields"/>. Either represents an unfiltered query.
    /// </remarks>
    /// <returns>null where there is nothing left to filter by</returns>
    private ICondition? Combined()
    {
        ICondition? combined = Conditions.Count switch
        {
            0 => null,
            1 => Conditions.First(),
            _ => new ConjunctionCondition(Operator.And, Conditions), // When more than one root condition was applied, they are ANDed
        };

        if ((combined is null) || (!Settings.IgnoreUnboundFields)) { return combined; }

        return ConditionPruner.Prune(combined, Bindings, Collections, field => Drop(field, BindingUse.Test));
    }

    /// <summary>
    /// The query with every sort applied, in the order given.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    /// <remarks>
    /// A sort that cannot be honoured because there is nothing to order by, a constant, or a type with no
    /// comparison of its own, is dropped rather than refused and recorded in <see cref="DroppedFields"/>.
    /// A sort on a field bound without <see cref="BindingUse.Sort"/> is a refusal and throws.
    /// </remarks>
    /// <exception cref="WeequeryException">
    /// a sort requests an unbound field, or one not bound for sorting
    /// </exception>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    private IQueryable<T> Sorted(IQueryable<T> query)
    {
        // If the sort uses an unbound field and dropping is configured, do so, see InquirySettings.IgnoreUnboundFields
        var sorts = Settings.IgnoreUnboundFields ? Sorts.Where(sort => Keep(sort.Field, BindingUse.Sort)) : Sorts;

        // If the query has already been ordered, we must use ThenBy instead of OrderBy
        bool alreadySorted = false;
        foreach (var sort in sorts)
        {
            var binding = BindingLookup.Resolve(Bindings, sort.Field);

            // If ordering was requested on a constant field, just ignore
            if (binding.IsConstant)
            {
                Drop(sort.Field, BindingUse.Sort, "it is a constant value, and would not affect ordering");

                continue;
            }

            // If the field has been bound, but not for ordering
            if (!binding.Allows(BindingUse.Sort))
            {
                throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Cannot sort on '{sort.Field}': it is bound for {binding.Use}");
            }

            // If the binding doesn not represent an orderable type, just ignore
            if (!binding.IsOrderable)
            {
                Drop(sort.Field, BindingUse.Sort, $"{binding.PropertyType.Name} has no ordering");

                continue;
            }

            // Sort on the accessor type, not the unwrapped one, otherwise a Nullable<> property cannot
            // satisfy the Func<T, TKey> the sort methods want. Nullable<> keys sort fine, nulls first.
            Type keyType = binding.PropertyType;
            Expression key = binding.Accessor;

            if (binding.RequiresLinkCheck)
            {
                // The path steps through something that may not be there, so a null guard is required. A row with a missing link is a null, so a
                // value typed key must be treated as nullable to hold one. Those rows sort first, as nulls do.
                keyType = ((keyType.IsValueType) && (!binding.PropertyIsWrappedByNullable)) ? typeof(Nullable<>).MakeGenericType(keyType) : keyType;

                Expression found = (keyType == binding.PropertyType) ? binding.Accessor : Expression.Convert(binding.Accessor, keyType);

                key = Expression.Condition(binding.LinkNotNullCheck, found, Expression.Constant(null, keyType));
            }

            var clause = SortMethods.For(sort.Direction, alreadySorted, typeof(T), keyType);

            // turn the binding accessor into something usable for the call
            var selector = Expression.Lambda(clause.SelectorType, key, SharedBindingParameter);

            // Add the call to the query's own expression and let the provider make a query of it
            query = query.Provider.CreateQuery<T>(Expression.Call(null, clause.Method, query.Expression, Expression.Quote(selector)));

            alreadySorted = true;
        }

        return query;
    }

    /// <summary>
    /// How many rows a page can hold, which is the size the caller named or, where it named none,
    /// <see cref="InquirySettings.DefaultPageSize"/>.
    /// </summary>
    /// <returns>zero where nothing named a size and no default was set, which is the query that has no window</returns>
    private int EffectivePageSize()
    {
        return (PageSize > 0) ? PageSize : (Settings.DefaultPageSize ?? 0);
    }

    /// <summary>
    /// The query narrowed to the requested page, or unchanged if no paging was requested
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the resolved size and the page combine past <see cref="int.MaxValue"/> rows to skip.
    /// </exception>
    private IQueryable<T> Windowed(IQueryable<T> query)
    {
        int pageSize = EffectivePageSize();
        if (pageSize <= 0) { return query; }

        int page = (Page > 0) ? Page : 0;

        long skip = (long)pageSize * page;
        if (skip > int.MaxValue)
        {
            throw new WeequeryException(WeequeryError.ArgumentInvalid, $"page size {pageSize} * page {page} exceeds {int.MaxValue}");
        }

        return query.Skip((int)skip).Take(pageSize);
    }

}
