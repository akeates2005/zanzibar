import { Alert, Center, Loader } from '@mantine/core';
import { Navigate, Route, Routes } from 'react-router-dom';

import { NotFoundPanel } from './components/common';
import { Shell } from './components/Shell';
import { ExplorerPage } from './features/explorer/ExplorerPage';
import { GroupsPage } from './features/groups/GroupsPage';
import { ResourceBrowser } from './features/resources/ResourceBrowser';
import { ResourceDetail } from './features/resources/ObjectDetail';
import { SystemPage } from './features/system/SystemPage';

export function App() {
  return (
    <Routes>
      <Route element={<Shell />}>
        <Route index element={<Navigate to="/resources" replace />} />
        <Route path="/resources" element={<ResourceBrowser />} />
        <Route path="/resources/:type/:id" element={<ResourceDetail />} />
        <Route path="/resources/:type/:id/access" element={<ResourceDetail />} />
        <Route path="/customers/:id/groups" element={<GroupsPage platform={false} />} />
        <Route path="/platform/groups" element={<GroupsPage platform />} />
        <Route path="/explorer" element={<ExplorerPage />} />
        <Route path="/system" element={<SystemPage />} />
        <Route path="*" element={<NotFoundPanel />} />
      </Route>
    </Routes>
  );
}

/** Shown before sign-in completes, or if the token issuer cannot be reached. */
export function SignInFallback({ state }: { state: 'loading' | { error: string } }) {
  if (state === 'loading') {
    return (
      <Center h="100vh" aria-busy="true">
        <Loader />
      </Center>
    );
  }
  return (
    <Center h="100vh" p="md">
      <Alert color="red" title="Cannot sign in" role="alert" maw={520}>
        {state.error} Is the stub token issuer running?
      </Alert>
    </Center>
  );
}
