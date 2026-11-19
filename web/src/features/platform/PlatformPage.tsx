import { Anchor, List, Paper, Stack, Tabs, Title } from '@mantine/core';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { useState } from 'react';

import { createCustomer, useCommand } from '../../api/commands';
import { useChildren, useMe } from '../../api/queries';
import { ActionButton, EmptyState, NotFoundPanel, PageTitle, PermissionList, QueryBoundary } from '../../components/common';
import { AccessTab } from '../access/AccessTab';
import { GroupsTab } from '../groups/GroupsTab';
import { TextPromptModal } from '../resources/dialogs';

/** `platform:main`: its customers, and the grants that give people platform-wide roles. */
export function PlatformPage() {
  const me = useMe();
  const navigate = useNavigate();
  const location = useLocation();
  const [creating, setCreating] = useState(false);
  const create = useCommand(createCustomer, { success: 'Customer created.', onSuccess: () => { setCreating(false); } });
  const customers = useChildren('platform', 'main');

  return (
    <QueryBoundary query={me}>
      {(profile) => {
        const permissions = profile.platformPermissions;
        if (!permissions.view) return <NotFoundPanel what="The platform" />;
        const current = location.pathname.endsWith('/access') && permissions.manage_access === true
          ? 'access'
          : location.pathname.endsWith('/groups') ? 'groups' : 'details';
        return (
          <Stack>
            <PageTitle
              title="Platform"
              badge="Platform"
              actions={<ActionButton label="New customer" permission="create_customer" permissions={permissions} onClick={() => { setCreating(true); }} />}
            />
            <Tabs value={current} onChange={(v) => { void navigate(v === 'access' || v === 'groups' ? `/resources/platform/main/${v}` : '/resources/platform/main'); }} keepMounted={false}>
              <Tabs.List>
                <Tabs.Tab value="details">Details</Tabs.Tab>
                {permissions.manage_access ? <Tabs.Tab value="access">Access</Tabs.Tab> : null}
                <Tabs.Tab value="groups">Groups</Tabs.Tab>
              </Tabs.List>
              <Tabs.Panel value="details" pt="md">
                <Stack>
                  <Paper withBorder p="md">
                    <Title order={5} mb="xs">Customers</Title>
                    <QueryBoundary query={customers}>
                      {(items) => items.length === 0 ? <EmptyState title="No customers yet" /> : (
                        <List listStyleType="none" spacing={2} withPadding={false} aria-label="Customers">
                          {items.map((c) => <List.Item key={c.id}><Anchor component={Link} to={`/resources/customer/${c.id}`}>{c.name}</Anchor></List.Item>)}
                        </List>
                      )}
                    </QueryBoundary>
                  </Paper>
                  <Paper withBorder p="md">
                    <Title order={5} mb="xs">Your access</Title>
                    <PermissionList permissions={permissions} />
                  </Paper>
                </Stack>
              </Tabs.Panel>
              {permissions.manage_access ? (
                <Tabs.Panel value="access" pt="md">
                  <AccessTab type="platform" id="main" permissions={permissions} ancestors={[]} />
                </Tabs.Panel>
              ) : null}
              <Tabs.Panel value="groups" pt="md">
                <GroupsTab customerId={null} createPermissions={permissions} />
              </Tabs.Panel>
            </Tabs>
            <TextPromptModal opened={creating} onClose={() => { setCreating(false); }} busy={create.isPending} title="New customer" label="Customer name" submitLabel="Create customer" onSubmit={(name) => { create.mutate({ name }); }} />
          </Stack>
        );
      }}
    </QueryBoundary>
  );
}
