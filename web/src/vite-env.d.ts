/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the API. Defaults to http://localhost:5080. */
  readonly VITE_API_URL?: string;
  /** Base URL of the stub token issuer. Defaults to http://localhost:5090. */
  readonly VITE_ISSUER_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
