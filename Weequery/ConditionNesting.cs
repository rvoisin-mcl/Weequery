using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// How deeply conditions may nest
/// </summary>
public static class ConditionNesting
{
    /// <summary>
    /// Levels of nesting allowed. Every container is one level, so a conjunction or a negation, and a comparison
    /// is a leaf. The limit is on nesting rather than on size
    /// </summary>
    public const int MaxDepth = 16;

    /// <summary>
    /// Step into one more level of nesting, refusing to go past <see cref="MaxDepth"/>.
    /// </summary>
    /// <param name="depth">levels already entered</param>
    /// <returns>the depth to walk the next level at, one deeper</returns>
    /// <exception cref="WeequeryException">the condition nests deeper than the limit</exception>
    public static int Descend(int depth)
    {
        if (IsTooDeep(depth + 1)) { throw TooDeep(); }

        return depth + 1;
    }

    /// <summary>
    /// If a walk at this depth has already gone past the limit. 
    /// </summary>
    /// <param name="depth">levels entered to get here</param>
    /// <returns></returns>
    public static bool IsTooDeep(int depth)
    {
        return depth > MaxDepth;
    }

    /// <summary>
    /// If a condition nests deeper than the limit.
    /// <para>
    /// Answers without walking any deeper than the limit itself, so this is safe to call on a tree of any depth,
    /// including one deep enough that walking all of it would overflow the stack.
    /// </para>
    /// </summary>
    /// <param name="condition"></param>
    /// <returns></returns>
    public static bool IsTooDeep(ICondition condition)
    {
        return IsTooDeep(condition, 0);
    }

    /// <param name="condition"></param>
    /// <param name="depth">levels entered to get to this condition</param>
    private static bool IsTooDeep(ICondition condition, int depth)
    {
        if (IsTooDeep(depth)) { return true; } // yes
        if (condition is not IConditionContainer<ICondition> container) { return false; } // nothing to dig into

        return container.Conditions.Any(child => IsTooDeep(child, depth + 1)); // keep going
    }

    /// <summary>
    /// Generate a standard depth exception so every walk reports it the same way
    /// </summary>
    /// <param name="exerpt">[OPT] to indicate the clause inside the query causing the exception</param>
    /// <returns></returns>
    public static WeequeryException TooDeep(string? exerpt = null)
    {
        return new WeequeryException(WeequeryError.NestingTooDeep, $"Condition nests deeper than the limit of {MaxDepth}{((exerpt is null) ? "" : $"'{exerpt}'")}");
    }
}
