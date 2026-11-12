import { Alert, Badge, Button, Card, Group, List, Stack, Table, Text, Title } from '@mantine/core';
import { useState } from 'react';

import { reconcile, resetSystem, useCommand } from '../../api/commands';
import { useMe, useSystemStatus } from '../../api/queries';
import { EmptyState, NotFoundPanel, PageTitle, QueryBoundary } from '../../components/common';
import { formatDateTime } from '../../lib/format';
import { ConfirmModal } from '../resources/dialogs';

/** Outbox state, reconciliation, and the reset that rebuilds both stores. Platform administrators only. */
export function SystemPage() {
  const me = useMe();
  return (
    <QueryBoundary query={me}>
      {(profile) => (profile.platformPermissions.admin ? <SystemBody /> : <NotFoundPanel what="The System screen" />)}
    </QueryBoundary>
  );
}

function SystemBody() {
  const status = useSystemStatus(true);
  const [resetting, setResetting] = useState(false);
  const run = useCommand(reconcile, { success: (r) => (r.inSync ? 'No drift found.' : 'Drift found.') });
  const reset = useCommand(resetSystem, { success: 'Data reset to the seed scenario.', onSuccess: () => { setResetting(false); } });

  return (
    <Stack maw={900}>
      <PageTitle title="System" />
      <QueryBoundary query={status}>
        {(s) => (
          <>
            <Card withBorder padding="md" data-testid="outbox">
              <Group gap="sm" mb="xs">
                <Title order={5}>Authorisation outbox</Title>
                <Badge color={s.ready ? 'teal' : 'orange'}>{s.ready ? 'Ready' : 'Starting'}</Badge>
              </Group>
              <Text>
                Pending: <strong data-testid="outbox-pending">{s.outbox.pending}</strong> · Failed: <strong data-testid="outbox-failed">{s.outbox.failed}</strong> · Start-up runs: {s.startupCount}
              </Text>
              {s.outbox.failures.length === 0 ? (
                <Text c="dimmed" mt="xs">No message has failed.</Text>
              ) : (
                <Table mt="sm" verticalSpacing="xs" aria-label="Failed messages">
                  <Table.Thead>
                    <Table.Tr><Table.Th>#</Table.Th><Table.Th>Type</Table.Th><Table.Th>Attempts</Table.Th><Table.Th>Last error</Table.Th></Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {s.outbox.failures.map((f) => (
                      <Table.Tr key={f.sequence}><Table.Td>{f.sequence}</Table.Td><Table.Td>{f.type}</Table.Td><Table.Td>{f.attempts}</Table.Td><Table.Td>{f.lastError}</Table.Td></Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Card>

            <Card withBorder padding="md" data-testid="reconciliation">
              <Title order={5} mb="xs">Reconciliation</Title>
              <Text c="dimmed" size="sm" mb="sm">Compares every parent and owner reference in the database with the tuples in SpiceDB. Detecting drift changes nothing; repair is a separate, explicit step.</Text>
              <Group mb="sm">
                <Button loading={run.isPending} onClick={() => { run.mutate({ repair: false }); }}>Run reconciliation</Button>
                <Button variant="default" loading={run.isPending} onClick={() => { run.mutate({ repair: true }); }}>Run and repair</Button>
              </Group>
              {s.reconciliation ? (
                <Stack gap="xs">
                  <Group gap="xs">
                    <Badge color={s.reconciliation.inSync ? 'teal' : 'red'} data-testid="reconciliation-verdict">{s.reconciliation.inSync ? 'In sync' : 'Drift'}</Badge>
                    <Text size="sm" c="dimmed">at {formatDateTime(s.reconciliation.at)}{s.reconciliation.skippedPending > 0 ? ` · ${String(s.reconciliation.skippedPending)} object(s) still in flight` : ''}</Text>
                  </Group>
                  {s.reconciliation.missing.length > 0 ? <Drift title="Missing from SpiceDB" items={s.reconciliation.missing} /> : null}
                  {s.reconciliation.unexpected.length > 0 ? <Drift title="Unexpected in SpiceDB" items={s.reconciliation.unexpected} /> : null}
                </Stack>
              ) : (
                <EmptyState title="Reconciliation has not run yet" />
              )}
            </Card>
          </>
        )}
      </QueryBoundary>

      <Alert color="red" title="Reset data" data-testid="reset">
        <Text size="sm" mb="sm">Wipes SpiceDB and the database and reseeds both from the seed scenario. Every change made since start-up is lost.</Text>
        <Button color="red" onClick={() => { setResetting(true); }}>Reset data</Button>
      </Alert>
      <ConfirmModal opened={resetting} onClose={() => { setResetting(false); }} busy={reset.isPending} title="Reset data" message="Wipe both stores and reseed? Every change since start-up is lost." confirmLabel="Reset data" onConfirm={() => { reset.mutate(undefined); }} />
    </Stack>
  );
}

function Drift({ title, items }: { title: string; items: readonly string[] }) {
  return (
    <div>
      <Text fw={600} size="sm">{title}</Text>
      <List size="sm" aria-label={title}>
        {items.map((t) => <List.Item key={t}><code>{t}</code></List.Item>)}
      </List>
    </div>
  );
}
