import {
  ActionIcon,
  AppShell,
  Avatar,
  Burger,
  Group,
  NativeSelect,
  NavLink as MantineNavLink,
  Stack,
  Text,
  ThemeIcon,
  Title,
  Tooltip,
  useComputedColorScheme,
  useMantineColorScheme,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { IconHierarchy2, IconMoon, IconSearch, IconServerCog, IconShieldLock, IconSun, type Icon } from '@tabler/icons-react';
import { Link, Outlet, useLocation } from 'react-router-dom';

import { useMe } from '../api/queries';
import { useAuth } from '../auth/AuthContext';
import { describeError } from '../api/problem';

interface NavItem {
  to: string;
  label: string;
  icon: Icon;
  show: boolean;
}

const initials = (name: string): string =>
  name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? '')
    .join('');

function ColorSchemeToggle() {
  const { setColorScheme } = useMantineColorScheme();
  const scheme = useComputedColorScheme('light', { getInitialValueInEffect: true });
  const next = scheme === 'dark' ? 'light' : 'dark';
  return (
    <Tooltip label={`Switch to ${next} mode`}>
      <ActionIcon variant="default" size="lg" radius="md" aria-label={`Switch to ${next} mode`} onClick={() => { setColorScheme(next); }}>
        {scheme === 'dark' ? <IconSun size={18} stroke={1.75} /> : <IconMoon size={18} stroke={1.75} />}
      </ActionIcon>
    </Tooltip>
  );
}

/**
 * The shell: brand, navigation and the user switcher. Switching the signed-in user is the main demonstration device, so
 * it is a plain select that fetches a token and replaces the session at once, clearing every cached query.
 */
export function Shell() {
  const { users, session, switchUser } = useAuth();
  const me = useMe();
  const platform = me.data?.platformPermissions;
  const { pathname } = useLocation();
  const [opened, { toggle, close }] = useDisclosure();

  const items: NavItem[] = [
    { to: '/resources', label: 'Resources', icon: IconHierarchy2, show: true },
    { to: '/explorer', label: 'Explorer', icon: IconSearch, show: true },
    { to: '/system', label: 'System', icon: IconServerCog, show: platform?.admin === true },
  ].filter((item) => item.show);
  // Platform groups is the platform's Groups tab, reached through Resources, so it has no item here.
  const isActive = (to: string): boolean => pathname === to || pathname.startsWith(`${to}/`);

  return (
    <AppShell header={{ height: 60 }} navbar={{ width: 248, breakpoint: 'sm', collapsed: { mobile: !opened } }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" aria-label="Toggle navigation" />
            <ThemeIcon size={32} radius="md" variant="gradient" gradient={{ from: 'brand.5', to: 'violet.6', deg: 135 }} aria-hidden>
              <IconShieldLock size={19} stroke={1.75} />
            </ThemeIcon>
            <Title order={4} component="span">
              Zanzibar PoC
            </Title>
          </Group>
          <ColorSchemeToggle />
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="sm">
        <AppShell.Section grow component="nav" aria-label="Main">
          <Stack gap={4}>
            {items.map(({ to, label, icon: Glyph }) => (
                <MantineNavLink
                  key={to}
                  component={Link}
                  to={to}
                  label={label}
                  active={isActive(to)}
                  aria-current={isActive(to) ? 'page' : undefined}
                  leftSection={<Glyph size={18} stroke={1.75} />}
                  variant="light"
                  onClick={close}
                  style={{ borderRadius: 'var(--mantine-radius-md)' }}
                  fw={500}
                />
              ))}
          </Stack>
        </AppShell.Section>

        <AppShell.Section pt="sm" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
          <Group gap="sm" wrap="nowrap" mb="xs">
            <Avatar color="brand" radius="xl" variant="light" aria-hidden>
              {initials(session.user.displayName)}
            </Avatar>
            <div style={{ minWidth: 0 }}>
              <Text size="sm" fw={600} truncate data-testid="signed-in-name">
                {session.user.displayName}
              </Text>
              <Text size="xs" c="dimmed" truncate>
                {session.user.id}
              </Text>
            </div>
          </Group>
          <NativeSelect
            aria-label="Signed in as"
            size="xs"
            data={users.map((u) => ({ value: u.id, label: `${u.displayName} (${u.id})` }))}
            value={session.user.id}
            onChange={(event) => {
              void switchUser(event.currentTarget.value).catch((error: unknown) => {
                // The previous session is still in place if the issuer refuses; say so rather than fail silently.
                window.alert(describeError(error));
              });
            }}
          />
        </AppShell.Section>
      </AppShell.Navbar>

      <AppShell.Main bg="var(--app-canvas)">
        <div style={{ maxWidth: 1200, margin: '0 auto' }}>
          <Outlet />
        </div>
      </AppShell.Main>
    </AppShell>
  );
}
