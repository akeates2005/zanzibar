import { Alert, Badge, Button, Center, Group, Loader, Stack, Text, Title, Tooltip, type ButtonProps } from '@mantine/core';
import type { UseQueryResult } from '@tanstack/react-query';
import type { ReactNode } from 'react';

import { ApiError, describeError } from '../api/problem';
import { humanise } from '../lib/format';

export function PageTitle({ title, badge, actions }: { title: string; badge?: string; actions?: ReactNode }) {
  return (
    <Group justify="space-between" align="flex-start" wrap="wrap" mb="md">
      <Group gap="sm">
        <Title order={2}>{title}</Title>
        {badge ? <Badge variant="light">{badge}</Badge> : null}
      </Group>
      {actions}
    </Group>
  );
}

export function ErrorPanel({ error, title = 'Something went wrong' }: { error: unknown; title?: string }) {
  return (
    <Alert color="red" title={title} role="alert">
      {describeError(error)}
    </Alert>
  );
}

/** Shown when an object does not exist or the user may not know that it does: the server deliberately says nothing more. */
export function NotFoundPanel({ what = 'This page' }: { what?: string }) {
  return (
    <Alert color="gray" title="Not found" data-testid="not-found">
      {what} does not exist, or you do not have access to it.
    </Alert>
  );
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <Stack align="center" gap="xs" py="xl" data-testid="empty-state">
      <Title order={4}>{title}</Title>
      {children ? (
        <Text c="dimmed" ta="center" maw={480}>
          {children}
        </Text>
      ) : null}
    </Stack>
  );
}

/** Renders a query's loading and error states, and its data only once it exists. 404 and 403 get their own wording. */
export function QueryBoundary<T>({
  query,
  children,
  notFoundWhat,
}: {
  query: UseQueryResult<T>;
  children: (data: T) => ReactNode;
  notFoundWhat?: string;
}) {
  if (query.isPending) {
    return (
      <Center py="xl" aria-busy="true">
        <Loader size="sm" />
      </Center>
    );
  }
  if (query.isError) {
    if (query.error instanceof ApiError && query.error.isNotFound) return <NotFoundPanel what={notFoundWhat} />;
    return <ErrorPanel error={query.error} />;
  }
  return <>{children(query.data)}</>;
}

interface ActionButtonProps extends Omit<ButtonProps, 'disabled' | 'children'> {
  label: string;
  /** The permission the server requires for this action. */
  permission: string;
  /** The caller's permission map from the object's response. Used to enable the control; the server never trusts it. */
  permissions: Readonly<Record<string, boolean>>;
  onClick: () => void;
  /** Another reason the action cannot run right now (for example "the subscription is cancelled"). */
  blockedReason?: string | undefined;
}

/**
 * An action the user may or may not have. A missing permission shows the control disabled with a tooltip naming the
 * permission, not hidden, so the permission model stays readable (PRD: Object detail). The wrapper span is what carries
 * the tooltip, because a disabled button receives no pointer events.
 */
export function ActionButton({ label, permission, permissions, onClick, blockedReason, ...rest }: ActionButtonProps) {
  const allowed = permissions[permission] === true;
  const reason = !allowed ? `Requires the '${humanise(permission)}' permission` : blockedReason;
  const blocked = reason !== undefined;
  return (
    <Tooltip label={reason ?? ''} disabled={!blocked} withArrow>
      <span data-testid={`action-${label.toLowerCase().replaceAll(/[^a-z0-9]+/g, '-')}`} data-requires={permission}>
        <Button {...rest} disabled={blocked} onClick={onClick}>
          {label}
        </Button>
      </span>
    </Tooltip>
  );
}

/** A tick or a cross for each permission: the "Your access" panel. */
export function PermissionList({ permissions }: { permissions: Readonly<Record<string, boolean>> }) {
  const entries = Object.entries(permissions).sort(([a], [b]) => a.localeCompare(b));
  return (
    <Stack gap={4} data-testid="your-access">
      {entries.map(([name, held]) => (
        <Group key={name} gap="xs" wrap="nowrap" data-testid={`permission-${name}`} data-held={held}>
          <Text span aria-hidden="true" c={held ? 'teal' : 'red'} fw={700} w={18}>
            {held ? '✓' : '✗'}
          </Text>
          <Text span>{humanise(name)}</Text>
          <Text span className="visually-hidden">
            {held ? 'allowed' : 'not allowed'}
          </Text>
        </Group>
      ))}
    </Stack>
  );
}
