import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';

import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import './index.css';

import { ApiError } from './api/problem';
import { App, SignInFallback } from './App';
import { AuthProvider } from './auth/AuthContext';

const queryClient = new QueryClient({
  queryCache: new QueryCache({
    onError: (error) => {
      // A 401 means the one-hour token has expired; the fix is a fresh token, which AuthProvider handles via a refetch.
      if (error instanceof ApiError && error.isUnauthorized) window.dispatchEvent(new Event('poc:token-expired'));
    },
  }),
  defaultOptions: { queries: { staleTime: 5_000, refetchOnWindowFocus: false, retry: (count, error) => !(error instanceof ApiError) && count < 2 } },
});

const root = document.getElementById('root');
if (!root) throw new Error('The page has no #root element.');

createRoot(root).render(
  <StrictMode>
    <MantineProvider defaultColorScheme="auto">
      <Notifications position="bottom-right" />
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <AuthProvider fallback={(state) => <SignInFallback state={state} />}>
            <App />
          </AuthProvider>
        </BrowserRouter>
      </QueryClientProvider>
    </MantineProvider>
  </StrictMode>,
);
