import { AppShell, Anchor, Group, NativeSelect, Text, Title } from '@mantine/core';
import { NavLink, Outlet } from 'react-router-dom';

import { useMe } from '../api/queries';
import { useAuth } from '../auth/AuthContext';
import { describeError } from '../api/problem';

const navStyle = ({ isActive }: { isActive: boolean }) => ({
  fontWeight: isActive ? 700 : 400,
  textDecoration: isActive ? 'underline' : 'none',
});

/**
 * The shell: title, navigation and the user switcher. Switching the signed-in user is the main demonstration device, so
 * it is a plain select that fetches a token and replaces the session at once, clearing every cached query.
 */
export function Shell() {
  const { users, session, switchUser } = useAuth();
  const me = useMe();
  const platform = me.data?.platformPermissions;

  return (
    <AppShell header={{ height: 64 }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="lg" wrap="nowrap">
            <Title order={4} component="span">
              Zanzibar PoC
            </Title>
            <Group component="nav" aria-label="Main" gap="md" wrap="nowrap">
              <Anchor component={NavLink} to="/resources" style={navStyle}>
                Resources
              </Anchor>
              {platform?.view ? (
                <Anchor component={NavLink} to="/platform/groups" style={navStyle}>
                  Platform groups
                </Anchor>
              ) : null}
              <Anchor component={NavLink} to="/explorer" style={navStyle}>
                Explorer
              </Anchor>
              {platform?.admin ? (
                <Anchor component={NavLink} to="/system" style={navStyle}>
                  System
                </Anchor>
              ) : null}
            </Group>
          </Group>
          <Group gap="xs" wrap="nowrap">
            <NativeSelect
              aria-label="Signed in as"
              data={users.map((u) => ({ value: u.id, label: `${u.displayName} (${u.id})` }))}
              value={session.user.id}
              onChange={(event) => {
                void switchUser(event.currentTarget.value).catch((error: unknown) => {
                  // The previous session is still in place if the issuer refuses; say so rather than fail silently.
                  window.alert(describeError(error));
                });
              }}
            />
            <Text size="sm" c="dimmed" data-testid="signed-in-name">
              {session.user.displayName}
            </Text>
          </Group>
        </Group>
      </AppShell.Header>
      <AppShell.Main>
        <Outlet />
      </AppShell.Main>
    </AppShell>
  );
}
