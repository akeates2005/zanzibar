import { Anchor, Badge, Button, Card, Group, List, NativeSelect, SegmentedControl, Stack, Table, Text, Title } from '@mantine/core';
import { useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';

import { addGroupMember, createGroup, deleteGroup, removeGroupMember, useCommand } from '../../api/commands';
import { useGroup, useGroups, useMe, useObject } from '../../api/queries';
import type { DirectoryUser, GroupSummary } from '../../api/types';
import { ActionButton, EmptyState, PageTitle, QueryBoundary } from '../../components/common';
import { UserPicker } from '../../components/pickers';
import { describeSubject, typeLabel } from '../../lib/format';
import { ConfirmModal, TextPromptModal } from '../resources/dialogs';

/** `/customers/:id/groups` and `/platform/groups`: create groups, manage members and nesting, see where a group is granted. */
export function GroupsPage({ platform }: { platform: boolean }) {
  return platform ? <PlatformGroups /> : <CustomerGroups />;
}

function PlatformGroups() {
  const me = useMe();
  return (
    <QueryBoundary query={me} notFoundWhat="The platform">
      {(profile) => (
        <GroupsBody customerId={null} title="Platform groups" createPermissions={profile.platformPermissions} back={null} />
      )}
    </QueryBoundary>
  );
}

function CustomerGroups() {
  const { id = '' } = useParams();
  // The permission to create a group comes from the owner: here, the customer's own permission map.
  const customer = useObject('customer', id);
  return (
    <QueryBoundary query={customer} notFoundWhat="This customer">
      {(c) => (
        <GroupsBody customerId={id} title={`Groups of ${c.name}`} createPermissions={c.permissions} back={{ to: `/resources/customer/${id}`, label: 'Back to the customer' }} />
      )}
    </QueryBoundary>
  );
}

function GroupsBody({ customerId, title, createPermissions, back }: {
  customerId: string | null;
  title: string;
  createPermissions: Readonly<Record<string, boolean>>;
  back: { to: string; label: string } | null;
}) {
  const [params, setParams] = useSearchParams();
  const selected = params.get('group');
  const groups = useGroups(customerId);
  const [creating, setCreating] = useState(false);
  const create = useCommand(createGroup, {
    success: (_, v) => `Group ${v.name} created.`,
    onSuccess: (created) => {
      setCreating(false);
      if ('id' in created) setParams({ group: created.id });
    },
  });

  return (
    <Stack>
      {back ? (
        <Anchor component={Link} to={back.to} size="sm">
          {back.label}
        </Anchor>
      ) : null}
      <PageTitle
        title={title}
        actions={<ActionButton label="New group" permission="create_group" permissions={createPermissions} onClick={() => { setCreating(true); }} />}
      />
      <Group align="flex-start" gap="lg" wrap="wrap">
        <Card withBorder padding="md" w={280} data-testid="group-list">
          <QueryBoundary query={groups} notFoundWhat="This group list">
            {(items) =>
              items.length === 0 ? (
                <EmptyState title="No groups you can see" />
              ) : (
                <List listStyleType="none" spacing={4} withPadding={false} aria-label="Groups">
                  {items.map((g) => (
                    <List.Item key={g.id}>
                      <Anchor component={Link} to={`?group=${g.id}`} fw={g.id === selected ? 700 : 400}>
                        {g.name}
                      </Anchor>
                    </List.Item>
                  ))}
                </List>
              )
            }
          </QueryBoundary>
        </Card>
        <Stack flex={1} miw={320}>
          {selected ? <GroupPanel key={selected} groupId={selected} customerId={customerId} onDeleted={() => { setParams({}); }} /> : <Text c="dimmed">Choose a group to see its members and where it is used.</Text>}
        </Stack>
      </Group>
      <TextPromptModal opened={creating} onClose={() => { setCreating(false); }} busy={create.isPending} title="New group" label="Group name" submitLabel="Create group" onSubmit={(name) => { create.mutate({ customerId, name }); }} />
    </Stack>
  );
}

function GroupPanel({ groupId, customerId, onDeleted }: { groupId: string; customerId: string | null; onDeleted: () => void }) {
  const detail = useGroup(groupId);
  return (
    <QueryBoundary query={detail} notFoundWhat="This group">
      {(group) => <GroupDetailView group={group} customerId={customerId} onDeleted={onDeleted} />}
    </QueryBoundary>
  );
}

function GroupDetailView({ group, customerId, onDeleted }: {
  group: NonNullable<ReturnType<typeof useGroup>['data']>;
  customerId: string | null;
  onDeleted: () => void;
}) {
  const navigate = useNavigate();
  const permissions = group.permissions;
  const canManage = permissions.manage === true;
  const [confirming, setConfirming] = useState(false);
  const remove = useCommand(deleteGroup, { success: 'Group deleted.', onSuccess: () => { setConfirming(false); onDeleted(); void navigate('.', { replace: true }); } });
  const removeMember = useCommand(removeGroupMember, { success: 'Member removed.' });

  return (
    <Card withBorder padding="md" data-testid="group-panel">
      <Group justify="space-between" mb="sm">
        <Group gap="xs">
          <Title order={4}>{group.group.name}</Title>
          <Badge variant="light">{group.group.ownerType === 'Platform' ? 'Platform group' : 'Customer group'}</Badge>
        </Group>
        <ActionButton color="red" variant="outline" label="Delete group" permission="manage" permissions={permissions} onClick={() => { setConfirming(true); }} />
      </Group>

      <Title order={5}>Members</Title>
      {group.members.length === 0 ? (
        <Text c="dimmed" mb="sm">No members yet.</Text>
      ) : (
        <Table verticalSpacing="xs" mb="sm" aria-label="Members">
          <Table.Tbody>
            {group.members.map((m) => (
              <Table.Tr key={`${m.subject.type}:${m.subject.id}`}>
                <Table.Td>{describeSubject(m.subject)}</Table.Td>
                <Table.Td>{typeLabel(m.subject.type)}</Table.Td>
                <Table.Td>
                  <ActionButton
                    size="compact-sm"
                    color="red"
                    variant="light"
                    label={`Remove ${m.subject.displayName}`}
                    permission="manage"
                    permissions={permissions}
                    onClick={() => { removeMember.mutate({ groupId: group.group.id, member: { type: m.subject.type === 'group' ? 'group' : 'user', id: m.subject.id } }); }}
                  />
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <AddMember groupId={group.group.id} customerId={customerId} ownerType={group.group.ownerType} canManage={canManage} />

      <Title order={5} mt="md">Where this group is used</Title>
      {group.grantedOn.length === 0 ? (
        <Text c="dimmed">It has not been granted anything, and is not nested in another group.</Text>
      ) : (
        <List aria-label="Where this group is used">
          {group.grantedOn.map((g) => (
            <List.Item key={`${g.resourceType}:${g.resourceId}:${g.relation}`}>
              {g.relation === 'member' ? 'Nested in' : `${g.relation.replaceAll('_', ' ')} on`} {typeLabel(g.resourceType).toLowerCase()} <strong>{g.resourceName}</strong>
            </List.Item>
          ))}
        </List>
      )}

      <ConfirmModal opened={confirming} onClose={() => { setConfirming(false); }} busy={remove.isPending} title="Delete group" message={`Delete ${group.group.name}? Everyone loses what this group gave them.`} confirmLabel="Delete group" onConfirm={() => { remove.mutate({ id: group.group.id }); }} />
    </Card>
  );
}

function AddMember({ groupId, customerId, ownerType, canManage }: { groupId: string; customerId: string | null; ownerType: GroupSummary['ownerType']; canManage: boolean }) {
  const [kind, setKind] = useState<'user' | 'group'>('user');
  const [user, setUser] = useState<DirectoryUser | null>(null);
  const [nested, setNested] = useState('');
  // Nesting is allowed only between groups with the same owner (a service rule), so offer only this list's groups.
  const siblings = useGroups(ownerType === 'Platform' ? null : customerId);
  const options = (siblings.data ?? []).filter((g) => g.id !== groupId);
  const chosen = nested !== '' ? nested : (options[0]?.id ?? '');
  const add = useCommand(addGroupMember, { success: 'Member added.', onSuccess: () => { setUser(null); } });
  const memberId = kind === 'user' ? user?.id : chosen;

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (memberId) add.mutate({ groupId, member: { type: kind, id: memberId } });
      }}
    >
      <Stack gap="xs" maw={420}>
        <Title order={6}>Add a member</Title>
        <SegmentedControl aria-label="Add" value={kind} onChange={(v) => { setKind(v === 'group' ? 'group' : 'user'); }} data={[{ value: 'user', label: 'A user' }, { value: 'group', label: 'A group' }]} disabled={!canManage} />
        {kind === 'user' ? (
          <UserPicker label="User" value={user} onChange={setUser} disabled={!canManage} />
        ) : (
          <NativeSelect label="Group" data={options.length === 0 ? [{ value: '', label: 'No other group' }] : options.map((g) => ({ value: g.id, label: g.name }))} value={chosen} onChange={(e) => { setNested(e.currentTarget.value); }} disabled={!canManage || options.length === 0} />
        )}
        <Group>
          <Button type="submit" disabled={!canManage || !memberId} loading={add.isPending} title={canManage ? undefined : "Requires the 'manage' permission"}>
            Add member
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
