using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// How deeply conditions may nest, shared by everything that walks one.
/// <para>
/// A condition is a tree, and every walk over one is recursive: packing and unpacking it, writing it as a query,
/// building the expression. They take their input from a caller, and a caller is where the deep ones come from 
/// a tree nesting a few thousand levels overflows the stack, which cannot be caught and takes the process with
/// it. One limit, well past anything a hand written filter reaches, turns that into an ordinary
/// <see cref="WeequeryException"/>.
/// </para>
/// <para>
/// The limit is on the condition, wherever it came from, so a query string is held to it as well: the parser
/// checks the tree it built before handing it back, rather than counting the parentheses, which are not the same
/// thing. Whatever parses can therefore be packed, written and built.
/// </para>
/// </summary>
internal static class ConditionNesting
{
    /// <summary>
    /// Levels of nesting allowed. Every container is one level, so a conjunction or a negation, and a comparison
    /// is a leaf. The limit is on nesting rather than on size: a conjunction may hold as many operands as it
    /// likes, and a condition as many values.
    /// </summary>
    internal const int MaxDepth = 16;

    /// <summary>
    /// Step into one more level of nesting, refusing to go past <see cref="MaxDepth"/>.
    /// </summary>
    /// <param name="depth">levels already entered</param>
    /// <returns>the depth to walk the next level at, one deeper</returns>
    /// <exception cref="WeequeryException">the condition nests deeper than the limit</exception>
    internal static int Descend(int depth)
    {
        if (IsTooDeep(depth + 1)) { throw TooDeep(); }

        return depth + 1;
    }

    /// <summary>
    /// Whether a walk at this depth has already gone past the limit. For the walk that cannot throw:
    /// <see cref="QueryWriter.Describe"/> backs ToString, where an exception makes debugging worse, so it
    /// substitutes a placeholder instead.
    /// </summary>
    /// <param name="depth">levels entered to get here</param>
    /// <returns></returns>
    internal static bool IsTooDeep(int depth)
    {
        return depth > MaxDepth;
    }

    /// <summary>
    /// Whether a condition nests deeper than the limit.
    /// <para>
    /// Answers without walking any deeper than the limit itself, so this is safe to call on a tree of any depth,
    /// including one deep enough that walking all of it would overflow the stack.
    /// </para>
    /// </summary>
    /// <param name="condition"></param>
    /// <returns></returns>
    internal static bool IsTooDeep(ICondition condition)
    {
        return IsTooDeep(condition, 0);
    }

    /// <param name="condition"></param>
    /// <param name="depth">levels entered to get to this condition</param>
    private static bool IsTooDeep(ICondition condition, int depth)
    {
        // Past the limit, so stop here rather than measuring how much further it goes
        if (IsTooDeep(depth)) { return true; }

        // Both containers, the conjunction and the negation, hold their children the same way
        if (condition is not IConditionContainer<ICondition> container) { return false; }

        return container.Conditions.Any(child => IsTooDeep(child, depth + 1));
    }

    /// <summary>
    /// The one exception for the one limit, so every walk reports it the same way
    /// </summary>
    /// <returns></returns>
    internal static WeequeryException TooDeep()
    {
        return new WeequeryException($"Condition nests deeper than the limit of {MaxDepth}");
    }
}
