import { Alert, Badge, Button, Card, Group, NativeSelect, Stack, Text, Title } from '@mantine/core';
import { useState } from 'react';

import { explainAccess, useCommand } from '../../api/commands';
import { useMe, useTree } from '../../api/queries';
import { ApiError, describeError } from '../../api/problem';
import { isObjectType, type DirectoryUser, type ExplainResponse, type ExplanationStep } from '../../api/types';
import { PageTitle } from '../../components/common';
import { UserPicker } from '../../components/pickers';
import { humanise, typeLabel } from '../../lib/format';
import { useObject } from '../../api/queries';

function Step({ step }: { step: ExplanationStep }) {
  const subject = step.subjectType ? `${step.subjectType}:${step.subjectId ?? ''}${step.subjectRelation ? `#${step.subjectRelation}` : ''}` : '';
  return (
    <li style={{ listStyle: 'none', marginLeft: step.depth * 16 }}>
      <Group gap="xs" wrap="nowrap">
        <Text span aria-hidden="true" c={step.allowed ? 'teal' : 'red'} fw={700}>
          {step.allowed ? '✓' : '✗'}
        </Text>
        <Text span size="sm">
          {step.resourceType}:{step.resourceId}
          {step.isRelation ? ' relation ' : ' permission '}
          <strong>{step.permission}</strong>
          {subject ? ` for ${subject}` : ''}
        </Text>
        <Text span className="visually-hidden">
          {step.allowed ? 'holds' : 'does not hold'}
        </Text>
      </Group>
    </li>
  );
}

/** Pick a user, an object and a permission, and see whether it is allowed and why. Needs manage_access on the object. */
export function ExplorerPage() {
  const tree = useTree();
  const me = useMe();
  const [user, setUser] = useState<DirectoryUser | null>(null);
  const [target, setTarget] = useState('');
  const [permission, setPermission] = useState('');
  const [result, setResult] = useState<ExplainResponse | null>(null);

  const objects = [
    ...(me.data?.platformPermissions.view ? [{ value: 'platform:main', label: 'Platform' }] : []),
    ...(tree.data ?? []).map((n) => ({ value: `${n.type}:${n.id}`, label: `${typeLabel(n.type)}: ${n.name}` })),
  ];
  const [type = '', id = ''] = target.split(':');
  // The object's own permission map names the permissions that exist for its type, so the page never hard-codes the schema.
  const objectQuery = useObject(isObjectType(type) ? type : 'customer', id);
  const permissionNames =
    type === 'platform' ? Object.keys(me.data?.platformPermissions ?? {}) : isObjectType(type) ? Object.keys(objectQuery.data?.permissions ?? {}) : [];
  const chosenPermission = permissionNames.includes(permission) ? permission : (permissionNames[0] ?? '');

  const explain = useCommand(explainAccess, {
    success: 'Checked.',
    onSuccess: (response) => { setResult(response); },
  });
  // Explaining changes nothing, so a failure is shown in place rather than as a toast.
  const error = explain.error;

  return (
    <Stack maw={760}>
      <PageTitle title="Permission explorer" />
      <Text c="dimmed">Choose a user, an object and a permission to see whether it is allowed and why. You need to manage access on the object.</Text>
      <Card withBorder padding="md">
        <Stack gap="sm">
          <UserPicker label="User" value={user} onChange={(u) => { setUser(u); setResult(null); }} />
          <NativeSelect
            label="Object"
            data={[{ value: '', label: 'Choose an object' }, ...objects]}
            value={target}
            onChange={(e) => { setTarget(e.currentTarget.value); setResult(null); }}
          />
          <NativeSelect
            label="Permission"
            data={permissionNames.length === 0 ? [{ value: '', label: 'Choose an object first' }] : permissionNames.map((p) => ({ value: p, label: humanise(p) }))}
            value={chosenPermission}
            onChange={(e) => { setPermission(e.currentTarget.value); setResult(null); }}
            disabled={permissionNames.length === 0}
          />
          <Group>
            <Button
              disabled={!user || !target || !chosenPermission}
              loading={explain.isPending}
              onClick={() => { if (user) explain.mutate({ userId: user.id, permission: chosenPermission, resourceType: type, resourceId: id }); }}
            >
              Check
            </Button>
          </Group>
        </Stack>
      </Card>

      {error instanceof ApiError && !result ? (
        <Alert color="orange" title={error.isForbidden ? 'You cannot manage access here' : 'Could not check'} role="alert">
          {describeError(error)}
        </Alert>
      ) : null}

      {result ? (
        <Card withBorder padding="md" data-testid="explanation">
          <Group gap="xs" mb="sm">
            <Title order={4}>Result</Title>
            <Badge color={result.allowed ? 'teal' : 'red'} size="lg" data-testid="verdict">
              {result.allowed ? 'Allowed' : 'Denied'}
            </Badge>
          </Group>
          {result.trace.length > 0 ? (
            <ul aria-label="Why" style={{ margin: 0, padding: 0 }}>
              {result.trace.map((step, index) => (
                <Step key={`${String(index)}:${step.resourceType}:${step.resourceId}:${step.permission}`} step={step} />
              ))}
            </ul>
          ) : (
            <Text c="dimmed">The engine returned no trace for this check.</Text>
          )}
        </Card>
      ) : null}
    </Stack>
  );
}
