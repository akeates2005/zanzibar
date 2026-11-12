/** Where the API and the stub issuer live. Both are overridable at build time; the defaults match the PRD's run-time table. */
export const config = {
  apiUrl: import.meta.env.VITE_API_URL ?? 'http://localhost:5080',
  issuerUrl: import.meta.env.VITE_ISSUER_URL ?? 'http://localhost:5090',
} as const;
