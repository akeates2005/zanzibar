import type { Problem } from './types';

/**
 * A failed API call. Carries the HTTP status and, when the server sent RFC 9457 problem details, the pieces the UI
 * cares about: the permission a 403 names and the stable code a 422 carries.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly permission: string | undefined;
  readonly code: string | undefined;
  readonly traceId: string | undefined;

  constructor(status: number, title: string, detail: string | undefined, extras: Partial<Problem> = {}) {
    super(detail ?? title);
    this.name = 'ApiError';
    this.status = status;
    this.permission = extras.permission ?? undefined;
    this.code = extras.code ?? undefined;
    this.traceId = extras.traceId ?? undefined;
  }

  get isNotFound(): boolean {
    return this.status === 404;
  }

  get isForbidden(): boolean {
    return this.status === 403;
  }

  get isUnauthorized(): boolean {
    return this.status === 401;
  }
}

function isProblem(value: unknown): value is Problem {
  return typeof value === 'object' && value !== null && 'title' in value && 'status' in value;
}

interface FetchResult<TData> {
  data?: TData;
  error?: unknown;
  response: Response;
}

/**
 * Turns an openapi-fetch result into its data, or throws an {@link ApiError}. A network failure (the API is down, CORS)
 * becomes an ApiError with status 0 so every caller handles one kind of error.
 */
export async function unwrap<TData>(pending: Promise<FetchResult<TData>>): Promise<TData> {
  let result: FetchResult<TData>;
  try {
    result = await pending;
  } catch (cause) {
    throw new ApiError(0, 'Network error', cause instanceof Error ? cause.message : 'The API could not be reached.');
  }

  if (result.error !== undefined || !result.response.ok) {
    const problem = isProblem(result.error) ? result.error : undefined;
    throw new ApiError(
      result.response.status,
      problem?.title ?? result.response.statusText,
      problem?.detail ?? undefined,
      problem ?? {},
    );
  }
  return result.data as TData;
}

/** A short, user-facing sentence for an error. Never shows a stack trace. */
export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 0) return 'The API could not be reached. Is it running?';
    if (error.status === 403 && error.permission) return `You do not have the '${error.permission}' permission for that.`;
    if (error.status === 503) return 'The service is unavailable or still starting. Try again in a moment.';
    return error.message;
  }
  return error instanceof Error ? error.message : 'Something went wrong.';
}
