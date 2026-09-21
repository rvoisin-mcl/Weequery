/**
 * Everything this library refuses. One type, as the C# side has one `WeequeryException`, so a caller can tell a
 * bad query from a bug in their own code with a single `catch`.
 */
export class WeequeryError extends Error {
  /** Index into the query text the problem starts at, where there is one to point at. */
  readonly position?: number;

  constructor(message: string, position?: number) {
    super(message);
    this.name = 'WeequeryError';
    this.position = position;

    // Extending a built-in loses the prototype chain when the output targets ES5, so `instanceof` needs help
    Object.setPrototypeOf(this, WeequeryError.prototype);
  }
}

/** Excerpt around a position, so an error can show where in the text it went wrong. */
export function excerpt(text: string, position: number, width = 40): string {
  const from = Math.max(0, position - width);
  const to = Math.min(text.length, position + width);

  return `${from > 0 ? '...' : ''}${text.slice(from, to)}${to < text.length ? '...' : ''}`;
}

/** An error that points into the query, in the shape the C# side's `QueryText.Describe` produces. */
export function describeError(text: string, message: string, position: number): WeequeryError {
  return new WeequeryError(`${message} at position ${position}: '${excerpt(text, position)}'`, position);
}
