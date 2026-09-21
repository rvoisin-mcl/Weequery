import assert from 'node:assert/strict';

import { WeequeryError } from '../src/index.js';

/**
 * Run something that must throw, and hand back what it threw.
 *
 * `assert.throws` returns nothing, so it can say that a call failed but not let you look at the message. Most of
 * what is worth checking here is the message: a refusal that does not name the spelling to use instead is not
 * doing its job.
 */
export function expectError(run: () => unknown): WeequeryError {
  try {
    run();
  } catch (caught) {
    assert.ok(caught instanceof WeequeryError, `expected a WeequeryError, got ${String(caught)}`);
    return caught;
  }

  assert.fail('expected this to throw, and it did not');
}
