import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, type RenderResult } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router-dom';

export function newQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } } });
}

/** Renders inside the providers every screen expects. `env="test"` turns off Mantine's transitions and portals. */
export function renderWithProviders(ui: ReactElement, options: { client?: QueryClient; route?: string } = {}): RenderResult & { client: QueryClient } {
  const client = options.client ?? newQueryClient();
  const result = render(
    <MantineProvider env="test">
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[options.route ?? '/']}>{ui}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return { ...result, client };
}
