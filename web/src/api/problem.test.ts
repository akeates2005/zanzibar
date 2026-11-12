import { describe, expect, it } from 'vitest';

import { ApiError, describeError, unwrap } from './problem';

const response = (status: number, statusText = ''): Response => new Response(null, { status, statusText });

describe('unwrap', () => {
  it('returns the data of a successful call', async () => {
    await expect(unwrap(Promise.resolve({ data: { id: 'x' }, response: response(200) }))).resolves.toEqual({ id: 'x' });
  });

  it('returns undefined for a 204 with no body', async () => {
    await expect(unwrap(Promise.resolve({ response: response(204) }))).resolves.toBeUndefined();
  });

  it('turns problem details into an ApiError carrying the permission a 403 names', async () => {
    const problem = { title: 'Forbidden', status: 403, detail: "You lack the 'top_up' permission.", permission: 'top_up' };
    const error = await unwrap(Promise.resolve({ error: problem, response: response(403) })).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 403, permission: 'top_up', isForbidden: true });
    expect(describeError(error)).toBe("You do not have the 'top_up' permission for that.");
  });

  it('carries the rule code of a 422', async () => {
    const error = await unwrap(Promise.resolve({ error: { title: 'Request refused', status: 422, detail: 'Cycle.', code: 'group_cycle' }, response: response(422) })).catch((e: unknown) => e);
    expect(error).toMatchObject({ status: 422, code: 'group_cycle', message: 'Cycle.' });
  });

  it('marks a 404 so screens can say "not found" without guessing', async () => {
    const error = await unwrap(Promise.resolve({ error: { title: 'Not found', status: 404 }, response: response(404) })).catch((e: unknown) => e);
    expect(error).toMatchObject({ isNotFound: true });
  });

  it('still throws when an error status arrives with no problem body', async () => {
    const error = await unwrap(Promise.resolve({ response: response(500, 'Internal Server Error') })).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 500 });
  });

  it('turns a network failure into an ApiError with status 0', async () => {
    const error = await unwrap(Promise.reject(new TypeError('Failed to fetch'))).catch((e: unknown) => e);
    expect(error).toMatchObject({ status: 0 });
    expect(describeError(error)).toBe('The API could not be reached. Is it running?');
  });
});

describe('describeError', () => {
  it('words a 503 as a retryable condition', () => {
    expect(describeError(new ApiError(503, 'Unavailable', 'x'))).toMatch(/try again/i);
  });

  it('never exposes more than a message for an unknown error', () => {
    expect(describeError('boom')).toBe('Something went wrong.');
    expect(describeError(new Error('plain'))).toBe('plain');
  });
});
